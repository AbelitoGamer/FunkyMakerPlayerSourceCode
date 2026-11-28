using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using FunkyMakerPlayer.Osu;
using FunkyMakerPlayer.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FunkyMakerPlayer;

/// <summary>A song the online song select can pick (one row; osu!: one difficulty of a set).</summary>
public sealed class OnlineSong
{
    /// <summary>
    /// The row's path: the level file when it's on this device (your level, a cached official level,
    /// your osu! difficulty), else <see cref="FreeplayMenu.OnlineKey"/> of the hash.
    /// </summary>
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>The level hash the room knows it by.</summary>
    public string Hash { get; init; } = "";
    public bool Official { get; init; }
    public string OfficialId { get; init; } = "";
    /// <summary>Stars the room was told (used until this device has analysed the chart).</summary>
    public float Stars { get; init; }
    /// <summary>Bytes to receive when you lack it (osu!: the whole set; SizeNoVideo without its video, 0 = no video).</summary>
    public long Size { get; init; }
    public long SizeNoVideo { get; init; }
    /// <summary>The level is on this device (Key is a real file).</summary>
    public bool OnDevice { get; init; }
    /// <summary>It's your own level file (can be deleted / shown in its folder).</summary>
    public bool Yours { get; init; }
    /// <summary>Players in the room who have it (you first), as colour + name.</summary>
    public IReadOnlyList<(Color Color, string Name)> Holders { get; init; } = Array.Empty<(Color, string)>();
    /// <summary>osu!: the set (groups difficulties into one row), its name and this difficulty's name.</summary>
    public string SetId { get; init; } = "";
    public string SetName { get; init; } = "";
    public string Difficulty { get; init; } = "";
    /// <summary>
    /// List section (FNF / osu! tabs): "SHARED LEVELS", "YOUR LEVELS", "&lt;PLAYER&gt;'S LEVELS"; empty =
    /// no sections. Rows are grouped by <see cref="GroupOrder"/>, then by the chosen sort.
    /// </summary>
    public string Group { get; init; } = "";
    public int GroupOrder { get; init; }
    /// <summary>Song length in seconds the room knows (0 = unknown).</summary>
    public float Duration { get; init; }
    /// <summary>
    /// Why it can't be picked (shown greyed out; picking it explains why), empty = pickable:
    /// too big for the server, or refused for this turn.
    /// </summary>
    public string DisabledTitle { get; init; } = "";
    public string DisabledReason { get; init; } = "";
    /// <summary>Short tag on the row ("TOO BIG", "REFUSED").</summary>
    public string DisabledTag { get; init; } = "";
    public bool Disabled => DisabledReason.Length > 0;
}

/// <summary>
/// What the online song select shows and does (fork). <see cref="Multiplayer.MultiplayerMenu"/>
/// implements it from the room: official levels, the room's FNF levels and osu! songs, and who has
/// each; picking sends the choice to the server.
/// </summary>
public interface IOnlineSongSource
{
    /// <summary>Header lines in the details side ("PICK A SONG", "Your turn in …").</summary>
    string Title { get; }
    string Subtitle { get; }
    /// <summary>Changes whenever the lists (or the room's song types) change: the menu then rebuilds them.</summary>
    int Version { get; }
    /// <summary>A pick is being sent (input waits).</summary>
    bool Busy { get; }
    /// <summary>Tabs the room's song types allow (in order).</summary>
    IReadOnlyList<FreeplayTab> Tabs { get; }
    IEnumerable<OnlineSong> Songs(FreeplayTab tab);
    /// <summary>Shown when a tab has no songs.</summary>
    (string Main, string Sub) EmptyText(FreeplayTab tab);
    /// <summary>Room facts for the info card (mode, song types…).</summary>
    string RoomInfo { get; }
    /// <param name="seconds">Song length as this device measured it (0 = unknown); long songs go to a lobby vote.</param>
    void Pick(OnlineSong song, float stars, float seconds);
    void Back();
    void Import();
    void Refresh();
}

/// <summary>
/// Fork: Freeplay as the online song select. The same browser (tabs, search, sort, favourites,
/// random, curved list, details with banner / density / stats, audio preview, "..." menu) shows the
/// songs a room can pick from an <see cref="IOnlineSongSource"/>: an OFFICIAL tab (server charts),
/// FNF (every player's levels) and OSU!MANIA (every player's osu! songs, one row per set with its
/// difficulties). Each row shows who in the room has it; ENTER / click picks the song for the round.
/// Freeplay-only parts (modifiers, attempts, song rate, chart editor, new chart, main menu) are off.
/// Songs that aren't on this device yet use <see cref="OnlineKey"/> as their path, so everything
/// that reads level files simply finds nothing for them.
/// </summary>
public partial class FreeplayMenu
{
    private readonly IOnlineSongSource? _online;
    private readonly Dictionary<string, OnlineSong> _onlineSongs = new(StringComparer.OrdinalIgnoreCase);
    private int _onlineVersion = int.MinValue;

    /// <summary>True for the online song select instance.</summary>
    public bool IsOnline => _online != null;

    /// <summary>A song preview is playing (the online menu music steps aside meanwhile).</summary>
    public bool PreviewAudible => _preview != null && PreviewPath != null;

    /// <summary>Path standing for a level that isn't on this device (nothing exists there).</summary>
    public static string OnlineKey(string hash) => Path.Combine(Path.GetTempPath(), "fmp-online", hash);

    private OnlineSong? OnlineSongOf(string? path) => path != null && _onlineSongs.TryGetValue(path, out var s) ? s : null;

    /// <summary>A real level file is behind this row (not an online-only song).</summary>
    private bool OnDevice(string? path) => path != null && (OnlineSongOf(path)?.OnDevice ?? true);

    /// <summary>Opens the online song select fresh (called when the screen is entered).</summary>
    public void OpenOnline()
    {
        if (_online == null) return;
        _panelOpen = true;
        _inPanel = true;
        _panelX = 0f;
        _panelAge = 0f;
        _onlineVersion = int.MinValue; // rebuild now
        EnsureOnlineTab();
        Refresh();
        _manualScroll = false;
        ResetInputCooldown();
    }

    /// <summary>Keeps the tab one the room allows.</summary>
    private void EnsureOnlineTab()
    {
        if (_online == null) return;
        var tabs = _online.Tabs;
        if (tabs.Count > 0 && !tabs.Contains(_tab)) _tab = tabs[0];
    }

    /// <summary>Per frame: rebuilds the list when the room's lists changed.</summary>
    private void UpdateOnlineSource()
    {
        if (_online == null) return;
        _panelOpen = true;
        _inPanel = true;
        int v = _online.Version;
        if (v == _onlineVersion) return;
        var before = _tab;
        EnsureOnlineTab();
        if (before != _tab) _selected = 0;
        Refresh();
    }

    /// <summary>Fills the list from the source (instead of scanning folders).</summary>
    private void ScanOnline()
    {
        _onlineVersion = _online!.Version;
        _onlineSongs.Clear();
        _osuSetOf.Clear();
        var songs = _online.Songs(_tab).ToList();
        foreach (var s in songs) _onlineSongs[s.Key] = s;

        if (_tab != FreeplayTab.Osu)
        {
            foreach (var s in songs.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                _allPaths.Add(s.Key);
                _allNames.Add(s.Name);
            }
            return;
        }

        // osu!: one row per set; its difficulties are every one somebody in the room has.
        var data = FreeplayData.Instance;
        foreach (var group in songs.GroupBy(s => s.SetId).OrderBy(g => g.First().SetName, StringComparer.OrdinalIgnoreCase))
        {
            var local = group.Where(s => s.OnDevice).Select(s => OsuLibrary.SetOf(s.Key)).FirstOrDefault(x => x != null);
            var diffs = group
                .Select(s =>
                {
                    var ld = local?.Difficulties.FirstOrDefault(d => string.Equals(d.Path, s.Key, StringComparison.OrdinalIgnoreCase));
                    return new OsuDifficulty(s.Key, s.Difficulty.Length > 0 ? s.Difficulty : s.Name, ld?.OverallDifficulty ?? 0f, ld?.Creator ?? "");
                })
                .OrderBy(d => _onlineSongs[d.Path].Stars).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            string setName = group.First().SetName.Length > 0 ? group.First().SetName : group.First().Name;
            var set = new OsuSet(
                local?.Folder ?? Path.Combine(Path.GetTempPath(), "fmp-online", "set-" + SafeKey(group.Key)),
                setName, "", local?.Creator ?? "", local?.BackgroundFile, diffs,
                local?.HasSkin ?? false, local?.HasVideo ?? false, local?.HasStoryboard ?? false, local?.VideoFiles);

            var chosen = diffs[0];
            if (data.OsuDifficulty.TryGetValue(SetKey(set), out var remembered))
                chosen = diffs.FirstOrDefault(d => Path.GetFileName(d.Path).Equals(remembered, StringComparison.OrdinalIgnoreCase)) ?? chosen;
            _allPaths.Add(chosen.Path);
            _allNames.Add(setName);
            _osuSetOf[chosen.Path] = set;
        }
    }

    private static string SafeKey(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    /// <summary>Stars shown for a row: this device's rating (1.0x, player chart) online, else the room's.</summary>
    private double? StarsOf(string path)
    {
        if (_online == null) return GetStars(path);
        var si = GetInfo(path)?.Sides[0];
        if (si != null) return si.Stars;
        var song = OnlineSongOf(path);
        return song != null && song.Stars > 0.05f ? song.Stars : null;
    }

    /// <summary>Side whose chart the details show: the Freeplay choice, or the player chart online.</summary>
    private int ViewSide(LevelInfo? info) => _online != null ? 0 : SideFor(info);

    /// <summary>ENTER / click on a row online: send the pick.</summary>
    private void PickOnline(int index)
    {
        if (_online == null || index < 0 || index >= _paths.Count || _online.Busy) return;
        _selected = index;
        var song = OnlineSongOf(_paths[index]);
        if (song == null) return;
        _options.Close();
        StopSearchTyping();
        UI.UiSfx.Play(song.Disabled ? UI.UiSound.Back : UI.UiSound.StartSong);
        float seconds = GetInfo(_paths[index])?.LengthSec ?? 0f;
        _online.Pick(song, (float)(StarsOf(_paths[index]) ?? song.Stars), seconds > 0f ? seconds : song.Duration);
    }

    /// <summary>Q / E online: the previous / next tab the room allows.</summary>
    private void CycleOnlineTab(int dir)
    {
        if (_online == null) return;
        var tabs = _online.Tabs;
        if (tabs.Count < 2) return;
        int i = Math.Max(0, tabs.ToList().IndexOf(_tab));
        int next = Math.Clamp(i + dir, 0, tabs.Count - 1);
        if (next != i) SwitchTab(tabs[next]);
    }

    private static string TabLabel(FreeplayTab tab) => tab switch
    {
        FreeplayTab.Official => "OFFICIAL",
        FreeplayTab.Osu => "OSU!MANIA",
        _ => "FNF",
    };

    private static Color TabAccent(FreeplayTab tab) => tab switch
    {
        FreeplayTab.Official => new Color(0, 200, 255),
        FreeplayTab.Osu => new Color(255, 102, 170),
        _ => new Color(255, 215, 0),
    };

    // ── Sections of the list (online FNF / osu! tabs) ───────────────────────

    /// <summary>Per visible row: its section label ("" = none), and how many headers sit above it (inclusive).</summary>
    private readonly List<string> _rowGroups = new();
    private readonly List<int> _headersThrough = new();

    /// <summary>
    /// Puts the rows in their sections, keeping the chosen sort inside each one. Online: shared / yours /
    /// downloaded / each player's. Offline: your levels, then the ones downloaded in online rooms.
    /// </summary>
    private IEnumerable<int> GroupOnline(IEnumerable<int> ordered)
    {
        if (_online == null) return ordered.OrderBy(i => OfflineGroup(_allPaths[i]).Order); // stable
        if (_tab == FreeplayTab.Official) return ordered;
        return ordered.OrderBy(i => OnlineSongOf(_allPaths[i])?.GroupOrder ?? int.MaxValue); // stable
    }

    /// <summary>
    /// Offline section of a level: downloaded in an online room (FNF levels under levels/Received/, osu!
    /// sets listed by <see cref="Osu.OsuLibrary.MarkReceived"/>) or yours.
    /// </summary>
    private (string Label, int Order) OfflineGroup(string path)
    {
        bool osu = Osu.OsuLibrary.IsOsuChart(path);
        bool received;
        try
        {
            received = osu
                ? Osu.OsuLibrary.IsReceived(System.IO.Path.GetDirectoryName(path) ?? "")
                : System.IO.Path.GetRelativePath(_levelsFolder, path).Replace('\\', '/')
                    .StartsWith("Received/", StringComparison.OrdinalIgnoreCase);
        }
        catch { received = false; }
        string what = osu ? "SONGS" : "LEVELS";
        return received ? ($"DOWNLOADED {what}", 1) : ($"YOUR {what}", 0);
    }

    /// <summary>After the visible list is rebuilt: where the section headers go.</summary>
    private void RebuildGroupHeaders()
    {
        _rowGroups.Clear();
        _headersThrough.Clear();
        bool online = _online != null;
        bool grouped = !online || _tab != FreeplayTab.Official;
        int headers = 0;
        string prev = "\0";
        foreach (var p in _paths)
        {
            string g = !grouped ? "" : online ? OnlineSongOf(p)?.Group ?? "" : OfflineGroup(p).Label;
            if (g.Length > 0 && g != prev) headers++;
            prev = g;
            _rowGroups.Add(g);
            _headersThrough.Add(headers);
        }
    }

    private int HeadersThrough(int i) =>
        _headersThrough.Count == 0 || i < 0 ? 0 : _headersThrough[Math.Min(i, _headersThrough.Count - 1)];

    /// <summary>Row <paramref name="i"/> opens a section (its header is drawn above it).</summary>
    private bool StartsGroup(int i) => i >= 0 && i < _rowGroups.Count && _rowGroups[i].Length > 0 && (i == 0 || _rowGroups[i] != _rowGroups[i - 1]);

    private static float GroupHeaderH(int itemH) => itemH * 0.62f;

    /// <summary>Top of row <paramref name="i"/> inside the list (rows plus the section headers above it).</summary>
    private float RowTop(int i, int itemH) => i * itemH + HeadersThrough(i) * GroupHeaderH(itemH);

    /// <summary>Height of the whole list (rows + section headers).</summary>
    private float ListContentHeight(int itemH) => _paths.Count * itemH + HeadersThrough(_paths.Count - 1) * GroupHeaderH(itemH);

    /// <summary>A section header above row <paramref name="i"/> (label, count, line).</summary>
    private void DrawGroupHeader(SpriteBatch sb, int i, int x, int w, float rowTop, int itemH, float s, float alpha)
    {
        string label = Safe(_rowGroups[i]);
        int count = 1;
        while (i + count < _rowGroups.Count && _rowGroups[i + count] == _rowGroups[i]) count++;
        float h = GroupHeaderH(itemH);
        int f = UiScale.FontPx(14, s);
        var sz = MeasureText(label, f);
        float y = rowTop - h + (h - sz.Y) / 2f + UiScale.Px(2, s);
        Color acc = label.StartsWith("SHARED", StringComparison.Ordinal) ? new Color(46, 224, 150)
            : label.StartsWith("YOUR", StringComparison.Ordinal) ? new Color(255, 215, 0)
            : label.StartsWith("DOWNLOADED", StringComparison.Ordinal) ? new Color(190, 140, 255)
            : label.StartsWith("TOO BIG", StringComparison.Ordinal) ? new Color(255, 90, 90)
            : new Color(0, 200, 255);
        DrawRect(sb, x, (int)(y + sz.Y / 2f) - UiScale.Px(5, s), UiScale.Px(4, s, 3), UiScale.Px(10, s, 8), acc * alpha);
        float tx = x + UiScale.Px(10, s);
        DrawText(sb, label, new Vector2(tx, y), acc * alpha, f);
        string n = count.ToString(CultureInfo.InvariantCulture);
        int nf = UiScale.FontPx(12, s);
        var nsz = MeasureText(n, nf);
        float nx = tx + sz.X + UiScale.Px(8, s);
        DrawText(sb, n, new Vector2(nx, y + (sz.Y - nsz.Y) / 2f), Color.White * (0.45f * alpha), nf);
        int lineX = (int)(nx + nsz.X + UiScale.Px(10, s));
        if (x + w - lineX > 8)
            DrawRect(sb, lineX, (int)(y + sz.Y / 2f), x + w - lineX, 1, acc * (0.35f * alpha));
    }

    private static string MegaBytes(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

    // ── Drawing pieces used only online ──────────────────────────────────────

    /// <summary>Squares of the players who have a song, ending at <paramref name="right"/> (returns their width).</summary>
    private int DrawHolderSquares(SpriteBatch sb, IReadOnlyList<(Color Color, string Name)> holders, int right, int centerY, float s, float alpha)
    {
        if (holders.Count == 0) return 0;
        int size = UiScale.Px(11, s, 8);
        int gap = UiScale.Px(3, s, 2);
        int shown = Math.Min(holders.Count, 6);
        int w = shown * size + (shown - 1) * gap;
        int x = right - w;
        for (int i = 0; i < shown; i++)
        {
            var r = new Rectangle(x + i * (size + gap), centerY - size / 2, size, size);
            DrawRect(sb, r.X - 1, r.Y - 1, r.Width + 2, r.Height + 2, Color.Black * (0.55f * alpha));
            DrawRect(sb, r.X, r.Y, r.Width, r.Height, holders[i].Color * alpha);
        }
        return w;
    }

    /// <summary>Row extras online (right side, before the star pill): OFFICIAL tag or the holders' squares.</summary>
    private int DrawOnlineRowTag(SpriteBatch sb, string path, int right, int rowY, int rowH, int pillH, int pillFont, float s, float alpha)
    {
        var song = OnlineSongOf(path);
        if (song == null) return right;
        if (song.Disabled && song.DisabledTag.Length > 0)
        {
            var tsz = MeasureText(song.DisabledTag, pillFont);
            int tw = (int)tsz.X + UiScale.Px(12, s);
            var tr = new Rectangle(right - tw, rowY + (rowH - pillH) / 2, tw, pillH);
            Color red = new(255, 90, 90);
            DrawRect(sb, tr.X, tr.Y, tr.Width, tr.Height, new Color(40, 14, 20) * (0.9f * alpha));
            DrawRect(sb, tr.X, tr.Bottom - 2, tr.Width, 2, red * alpha);
            DrawText(sb, song.DisabledTag, new Vector2(tr.X + (tw - tsz.X) / 2f, tr.Y + (pillH - tsz.Y) / 2f), red * alpha, pillFont);
            right = tr.X - UiScale.Px(6, s);
        }
        if (song.Official)
        {
            string tag = song.OnDevice ? "OFFICIAL" : "OFFICIAL  +DL";
            var sz = MeasureText(tag, pillFont);
            int w = (int)sz.X + UiScale.Px(12, s);
            var r = new Rectangle(right - w, rowY + (rowH - pillH) / 2, w, pillH);
            DrawRect(sb, r.X, r.Y, r.Width, r.Height, new Color(16, 16, 28) * (0.85f * alpha));
            DrawRect(sb, r.X, r.Bottom - 2, r.Width, 2, TabAccent(FreeplayTab.Official) * alpha);
            DrawText(sb, tag, new Vector2(r.X + (w - sz.X) / 2f, r.Y + (pillH - sz.Y) / 2f), TabAccent(FreeplayTab.Official) * alpha, pillFont);
            return r.X - UiScale.Px(6, s);
        }
        // Osu! rows show the set's holders (anybody with any difficulty of it).
        var holders = song.Holders;
        if (OsuSetOf(path) is { } set)
            holders = set.Difficulties.Select(d => OnlineSongOf(d.Path)).Where(x => x != null)
                .SelectMany(x => x!.Holders).GroupBy(h => h.Name).Select(g => g.First()).ToList();
        int hw = DrawHolderSquares(sb, holders, right, rowY + rowH / 2, s, alpha);
        return hw > 0 ? right - hw - UiScale.Px(8, s) : right;
    }

    /// <summary>Details online, instead of the personal best / modifiers: who has it and what happens if you pick it.</summary>
    private void DrawOnlineAvailability(SpriteBatch sb, Rectangle r, string path, float s, float alpha)
    {
        var song = OnlineSongOf(path);
        Color acc = song?.Disabled == true ? new Color(255, 90, 90)
            : song?.Official == true ? TabAccent(FreeplayTab.Official) : new Color(46, 224, 150);
        DrawCard(sb, r, alpha, acc);
        int lp = UiScale.Px(12, s);
        DrawCardLabel(sb, song?.Disabled == true ? "CAN'T BE PICKED" : song?.Official == true ? "OFFICIAL LEVEL" : "IN THIS ROOM", r.X + lp, r.Y + UiScale.Px(6, s), alpha, s);
        if (song == null) return;

        int f = UiScale.FontPx(14, s);
        float y = r.Y + UiScale.Px(26, s);
        string line1, line2;
        if (song.Disabled)
        {
            line1 = song.DisabledTitle;
            line2 = song.DisabledReason;
        }
        else if (song.Official)
        {
            line1 = song.OnDevice ? "Already on this device." : "Not downloaded yet.";
            line2 = "Everybody gets it from the server when it's picked.";
        }
        else
        {
            bool you = song.OnDevice;
            var others = song.Holders.Where(h => !h.Name.EndsWith("(you)", StringComparison.Ordinal)).Select(h => h.Name).ToList();
            line1 = you
                ? (others.Count > 0 ? $"You and {string.Join(", ", others)} have it." : "Only you have it.")
                : $"From {string.Join(", ", others)}.";
            long size = song.Size;
            string sizeText = song.SizeNoVideo > 0 && song.SizeNoVideo < song.Size
                ? $"{MegaBytes(song.Size)} ({MegaBytes(song.SizeNoVideo)} without video)"
                : MegaBytes(size);
            line2 = you
                ? $"Sent to whoever lacks it ({sizeText})."
                : $"It will be sent to you when picked ({sizeText}).";
        }
        // Holders' squares on the right of the first line
        int hw = DrawHolderSquares(sb, song.Holders, r.Right - lp, (int)y + UiScale.Px(8, s), s, alpha);
        float maxW = r.Width - lp * 2 - hw - UiScale.Px(10, s);
        DrawText(sb, TruncateText(Safe(line1), f, maxW), new Vector2(r.X + lp, y), Color.White * alpha, f);
        y += MeasureText("A", f).Y + UiScale.Px(4, s);
        int f2 = UiScale.FontPx(13, s);
        DrawText(sb, TruncateText(Safe(line2), f2, r.Width - lp * 2), new Vector2(r.X + lp, y), Color.White * (0.6f * alpha), f2);
    }

    /// <summary>Room facts card online (where the Freeplay modifiers bar is).</summary>
    private void DrawOnlineRoomCard(SpriteBatch sb, Rectangle r, float s, float alpha)
    {
        Color acc = new(190, 140, 255);
        DrawCard(sb, r, alpha, acc);
        int lp = UiScale.Px(12, s);
        DrawCardLabel(sb, "ROOM", r.X + lp, r.Y + UiScale.Px(6, s), alpha, s);
        int f = UiScale.FontPx(13, s);
        string text = _online?.RoomInfo ?? "";
        DrawText(sb, TruncateText(Safe(text), f, r.Width - lp * 2), new Vector2(r.X + lp, r.Y + UiScale.Px(28, s)), Color.White * (0.75f * alpha), f);
    }

    /// <summary>Text of an empty tab online (instead of the import drop zone).</summary>
    private void DrawOnlineEmpty(SpriteBatch sb, int panelLeft, int panelW, int top, int height, float s)
    {
        var (main, sub) = _online!.EmptyText(_tab);
        int mainSize = FitFontSize(main, UiScale.FontPx(22, s), 13, panelW - UiScale.Px(32, s));
        int subSize = FitFontSize(sub, UiScale.FontPx(14, s), 10, panelW - UiScale.Px(32, s));
        var m = MeasureText(main, mainSize);
        var su = MeasureText(sub.Length > 0 ? sub : " ", subSize);
        float y = top + height * 0.4f - (m.Y + su.Y) / 2f;
        float cx = panelLeft + panelW / 2f;
        DrawText(sb, main, new Vector2(cx - m.X / 2f, y), new Color(255, 215, 0) * 0.85f, mainSize);
        if (sub.Length > 0)
            DrawText(sb, sub, new Vector2(cx - su.X / 2f, y + m.Y + UiScale.Px(10, s)), Color.White * 0.6f, subSize);
    }
}
