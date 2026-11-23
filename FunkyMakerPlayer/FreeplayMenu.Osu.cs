using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FunkyMakerPlayer.Osu;
using FunkyMakerPlayer.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FunkyMakerPlayer;

/// <summary>Freeplay tabs. Official is only used by the online song select (server charts).</summary>
public enum FreeplayTab { Fnf, Osu, Official }

/// <summary>
/// Fork: the OSU!MANIA tab of Freeplay. Its rows are osu! beatmap sets (mods/osu, see
/// <see cref="OsuLibrary"/>); each row's path is the chosen 4K difficulty's .osu file, so stars,
/// preview, attempts and replays work per difficulty exactly as for FNF levels. Left / Right (or the
/// arrows in the details) change the difficulty; the choice is remembered per set.
/// </summary>
public partial class FreeplayMenu
{
    private FreeplayTab _tab = FreeplayTab.Fnf;
    private readonly Dictionary<string, OsuSet> _osuSetOf = new(StringComparer.OrdinalIgnoreCase);
    private Rectangle _diffPrevBounds, _diffNextBounds;
    private bool _diffPrevHover, _diffNextHover;
    // Header tabs (FNF | OSU!MANIA; online: the tabs the room allows) and the hovered one
    private readonly List<(FreeplayTab Tab, Rectangle Bounds)> _tabBounds = new();
    private FreeplayTab? _tabHover;
    private readonly Dictionary<FreeplayTab, string> _tabSelection = new();

    public FreeplayTab Tab => _tab;
    private bool OsuTab => _tab == FreeplayTab.Osu;

    private static string SetKey(OsuSet set) => Path.GetFileName(set.Folder).ToLowerInvariant();

    /// <summary>The osu! set a listed .osu path belongs to, or null.</summary>
    private OsuSet? OsuSetOf(string? path)
        => path != null && _osuSetOf.TryGetValue(path, out var s) ? s : null;

    private void ScanOsuSets()
    {
        _osuSetOf.Clear();
        var data = FreeplayData.Instance;
        foreach (var set in OsuLibrary.ScanSets().OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            var diff = set.Difficulties[0];
            if (data.OsuDifficulty.TryGetValue(SetKey(set), out var chosen))
                diff = set.Difficulties.FirstOrDefault(d => Path.GetFileName(d.Path).Equals(chosen, StringComparison.OrdinalIgnoreCase)) ?? diff;
            _allPaths.Add(diff.Path);
            _allNames.Add(set.Name);
            _osuSetOf[diff.Path] = set;
            try
            {
                var created = Directory.GetCreationTimeUtc(set.Folder);
                var written = Directory.GetLastWriteTimeUtc(set.Folder);
                _addedUtc[diff.Path] = created > written ? created : written;
            }
            catch { }
        }
        Console.WriteLine($"[FreeplayMenu] osu! sets: {_allPaths.Count} (folder {OsuLibrary.RootFolder})");
    }

    /// <summary>A listed path of the same osu! set as <paramref name="osuPath"/> (another difficulty), or null.</summary>
    private string? SameOsuSet(string? osuPath)
    {
        if (!OsuLibrary.IsOsuChart(osuPath)) return null;
        string? folder = Path.GetDirectoryName(osuPath!);
        return _allPaths.FirstOrDefault(p => string.Equals(Path.GetDirectoryName(p), folder, StringComparison.OrdinalIgnoreCase));
    }

    private void SwitchTab(FreeplayTab tab)
    {
        if (tab == _tab) return;
        // Each tab remembers its selection while the game runs.
        if (SelectedPath is { } current) _tabSelection[_tab] = current;
        _tab = tab;
        if (_online == null)
        {
            FreeplayData.Instance.Tab = tab == FreeplayTab.Osu ? "osu" : "fnf";
            FreeplayData.Instance.Save();
        }
        StopSearchTyping();
        _options.Close();
        _selected = 0;
        Refresh();
        if (_tabSelection.TryGetValue(tab, out var remembered))
        {
            int idx = _paths.IndexOf(remembered);
            if (idx < 0 && SameOsuSet(remembered) is { } sameSet) idx = _paths.IndexOf(sameSet);
            if (idx >= 0) { _selected = idx; _selVisual = idx; }
        }
        _manualScroll = false;
        _panelAge = 0f;
        UI.UiSfx.Play(UI.UiSound.Move);
    }

    /// <summary>Selects the next / previous 4K difficulty of the selected osu! set.</summary>
    private void ChangeDifficulty(int dir)
    {
        if (SelectedPath is not { } path || OsuSetOf(path) is not { } set || set.Difficulties.Count < 2)
        {
            if (OsuTab) UI.UiSfx.Play(UI.UiSound.SliderEnd);
            return;
        }
        int i = set.Difficulties.ToList().FindIndex(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));
        int next = Math.Clamp(i + dir, 0, set.Difficulties.Count - 1);
        if (next == i) { UI.UiSfx.Play(UI.UiSound.SliderEnd); return; }
        string newPath = set.Difficulties[next].Path;

        int all = _allPaths.IndexOf(path);
        if (all >= 0) _allPaths[all] = newPath;
        _paths[_selected] = newPath;
        _osuSetOf.Remove(path);
        _osuSetOf[newPath] = set;
        if (_addedUtc.Remove(path, out var t)) _addedUtc[newPath] = t;
        FreeplayData.Instance.OsuDifficulty[SetKey(set)] = Path.GetFileName(newPath);
        FreeplayData.Instance.Save();
        QueueStarRatings();
        UI.UiSfx.Play(dir > 0 ? UI.UiSound.SliderUp : UI.UiSound.SliderDown);
    }

    /// <summary>A beatmap set was imported (drag-and-drop): show it in the OSU!MANIA tab.</summary>
    public void OnOsuSetImported(string folder, string message)
    {
        _search = "";
        _favoritesOnly = false;
        StopSearchTyping();
        _tab = FreeplayTab.Osu;
        FreeplayData.Instance.Tab = "osu";
        Refresh();
        int idx = _paths.FindIndex(p => string.Equals(Path.GetDirectoryName(p), folder, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) { _selected = idx; _manualScroll = false; }
        _panelOpen = true;
        _inPanel = true;
        _menuSel = 0;
        ShowToast(message);
        ResetInputCooldown();
    }

    /// <summary>"[Hard]  2/4" style label of the selected difficulty.</summary>
    private string DifficultyLabel(string path, out int index, out int count)
    {
        index = 0; count = 0;
        if (OsuSetOf(path) is not { } set) return "";
        count = set.Difficulties.Count;
        index = set.Difficulties.ToList().FindIndex(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? set.Difficulties[index].Name : "";
    }

    /// <summary>The header tabs in order: FNF | OSU!MANIA, or online the ones the room allows.</summary>
    private IReadOnlyList<FreeplayTab> HeaderTabs =>
        _online?.Tabs ?? (IReadOnlyList<FreeplayTab>)new[] { FreeplayTab.Fnf, FreeplayTab.Osu };

    /// <summary>Header tabs (Q / E or click).</summary>
    private void DrawTabs(SpriteBatch sb, int x, int y, int h, int maxW, float s)
    {
        _tabBounds.Clear();
        var tabs = HeaderTabs;
        if (tabs.Count == 0) return;
        string longest = tabs.Select(TabLabel).OrderByDescending(l => l.Length).First();
        int gap = UiScale.Px(4, s);
        int font = FitFontSize(longest, UiScale.FontPx(15, s), 9, (maxW - gap * (tabs.Count - 1)) / (float)tabs.Count - UiScale.Px(16, s));
        int tx = x;
        foreach (var tab in tabs)
        {
            string label = TabLabel(tab);
            var r = new Rectangle(tx, y, (int)MeasureText(label, font).X + UiScale.Px(tabs.Count > 2 ? 16 : 24, s), h);
            _tabBounds.Add((tab, r));
            tx = r.Right + gap;
            bool on = _tab == tab, hover = _tabHover == tab;
            Color accent = TabAccent(tab);
            DrawRect(sb, r.X, r.Y, r.Width, r.Height, on ? Color.Lerp(new Color(30, 22, 42), accent, 0.25f) : hover ? new Color(44, 36, 60) : new Color(30, 22, 42));
            DrawRect(sb, r.X, r.Bottom - 2, r.Width, 2, accent * (on ? 1f : 0.3f));
            var sz = MeasureText(label, font);
            DrawText(sb, label, new Vector2(r.X + (r.Width - sz.X) / 2f, r.Y + (r.Height - sz.Y) / 2f), on ? accent : Color.White * (hover ? 0.85f : 0.5f), font);
        }
    }

    /// <summary>Difficulty selector line in the details panel (osu! songs).</summary>
    private void DrawDifficultySelector(SpriteBatch sb, string path, int x, int y, int maxW, float s, float alpha)
    {
        _diffPrevBounds = _diffNextBounds = Rectangle.Empty;
        string name = DifficultyLabel(path, out int index, out int count);
        if (count == 0) return;
        int h = UiScale.Px(26, s, 22);
        int font = UiScale.FontPx(15, s);
        int bw = h;
        _diffPrevBounds = new Rectangle(x, y, bw, h);
        string text = $"{name}   {index + 1}/{count}";
        text = TruncateText(Safe(text), font, maxW - bw * 2 - UiScale.Px(24, s));
        var tsz = MeasureText(text, font);
        int tw = (int)tsz.X + UiScale.Px(20, s);
        var mid = new Rectangle(_diffPrevBounds.Right + UiScale.Px(4, s), y, tw, h);
        _diffNextBounds = new Rectangle(mid.Right + UiScale.Px(4, s), y, bw, h);
        Color acc = new(255, 102, 170);
        bool canPrev = index > 0, canNext = index < count - 1;
        DrawRect(sb, _diffPrevBounds.X, y, bw, h, (canPrev && _diffPrevHover ? acc * 0.5f : new Color(40, 34, 58)) * alpha);
        DrawRect(sb, _diffNextBounds.X, y, bw, h, (canNext && _diffNextHover ? acc * 0.5f : new Color(40, 34, 58)) * alpha);
        DrawRect(sb, mid.X, y, mid.Width, h, acc * (0.18f * alpha));
        DrawRect(sb, mid.X, mid.Bottom - 2, mid.Width, 2, acc * alpha);
        var lsz = MeasureText("<", font);
        DrawText(sb, "<", new Vector2(_diffPrevBounds.X + (bw - lsz.X) / 2f, y + (h - lsz.Y) / 2f), Color.White * ((canPrev ? 1f : 0.3f) * alpha), font);
        DrawText(sb, ">", new Vector2(_diffNextBounds.X + (bw - lsz.X) / 2f, y + (h - lsz.Y) / 2f), Color.White * ((canNext ? 1f : 0.3f) * alpha), font);
        DrawText(sb, text, new Vector2(mid.X + UiScale.Px(10, s), y + (h - tsz.Y) / 2f), Color.White * alpha, font);
    }
}
