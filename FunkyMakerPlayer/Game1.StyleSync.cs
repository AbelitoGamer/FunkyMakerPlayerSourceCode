using System;

namespace FunkyMakerPlayer;

/// <summary>
/// Fork: keeps the chosen styles applied. Settings can change them from several places (the style
/// menus, Left / Right on the Settings rows, the pause menu), and some only stored the name, so the
/// change showed up after a restart of the game. Every frame this compares what is selected with
/// what is loaded and reloads what differs: the note style (also an osu! skin built for the other
/// scroll direction, and the beatmap skin choice), the note splash and the countdown.
/// </summary>
public partial class Game1
{
    private string? _loadedNoteStyleName;
    private string? _loadedSplashName;
    private NoteStyle? _appliedRendererStyle;

    private void SyncChosenStyles()
    {
        if (GraphicsDevice == null) return;

        bool osuSkinFlipped = _noteStyle?.OsuSkinFolder != null && _noteStyle.OsuBuiltForDownscroll != NoteRenderer.Downscroll;
        if (_loadedNoteStyleName != NoteStyleName || osuSkinFlipped)
        {
            if (_loadedNoteStyleName != null || osuSkinFlipped)
                _noteStyle = NoteStyle.LoadByName(NoteStyleName, GraphicsDevice);
            _loadedNoteStyleName = NoteStyleName;
        }

        if (_noteRenderers.Count > 0)
        {
            var wanted = LevelNoteStyle();
            if (!ReferenceEquals(wanted, _appliedRendererStyle))
            {
                foreach (var r in _noteRenderers) r.SetStyle(wanted);
                _appliedRendererStyle = wanted;
            }
        }

        if (_loadedSplashName != NoteSplashStyleName)
        {
            if (_loadedSplashName != null) ApplyNoteSplashStyle();
            _loadedSplashName = NoteSplashStyleName;
        }

        _countdownOverlay?.EnsureLoaded(SelectedCountdown);
    }
}
