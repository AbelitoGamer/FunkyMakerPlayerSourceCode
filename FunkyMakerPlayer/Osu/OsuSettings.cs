using System;

namespace FunkyMakerPlayer.Osu;

/// <summary>
/// Settings for osu! songs (fork), shown in Settings → osu! and saved in
/// settings.json ("Osu"). They only affect levels converted from .osu files.
/// </summary>
public static class OsuSettings
{
    /// <summary>
    /// True: a beatmap that ships its own mania note images (mania-note1.png… in the set folder)
    /// is shown with them; false: always the note style picked in Settings. Like osu!'s
    /// "Beatmap skins" option, only images are used — never the folder's skin.ini / [Mania] config.
    /// </summary>
    public static bool PreferBeatmapSkins = true;
    /// <summary>Scroll speed changes (SV, inherited timing points) and BPM-relative scrolling.</summary>
    public static bool ScrollSpeedChanges = true;
    public static bool Hitsounds = true;
    /// <summary>Hitsound volume 0..1 (scaled by master volume).</summary>
    public static float HitsoundVolume = 0.8f;
    /// <summary>Background videos (desktop: ffmpeg, Android: MediaCodec).</summary>
    public static bool Video = true;
    /// <summary>
    /// False: videos are decoded to 640×360 at 30 fps (cheap). True: at their own size and frame rate
    /// (capped to 4K / 60 fps; 1080p on Android, where frames are converted on the CPU).
    /// </summary>
    public static bool VideoFullQuality = false;
    /// <summary>True: the background image stays visible around a video that doesn't fill the screen.</summary>
    public static bool BackgroundBehindVideo = false;
    public static bool Storyboard = true;
    /// <summary>How much the background / video / storyboard is darkened, 0..1 (osu!'s background dim).</summary>
    public static float BackgroundDim = 0.7f;

    /// <summary>settings.json form.</summary>
    public sealed class Data
    {
        public bool PreferBeatmapSkins { get; set; } = true;
        public bool ScrollSpeedChanges { get; set; } = true;
        public bool Hitsounds { get; set; } = true;
        public float HitsoundVolume { get; set; } = 0.8f;
        public bool Video { get; set; } = true;
        public bool VideoFullQuality { get; set; } = false;
        public bool BackgroundBehindVideo { get; set; } = false;
        public bool Storyboard { get; set; } = true;
        public float BackgroundDim { get; set; } = 0.7f;
    }

    public static Data Export() => new()
    {
        PreferBeatmapSkins = PreferBeatmapSkins,
        ScrollSpeedChanges = ScrollSpeedChanges,
        Hitsounds = Hitsounds,
        HitsoundVolume = HitsoundVolume,
        Video = Video,
        VideoFullQuality = VideoFullQuality,
        BackgroundBehindVideo = BackgroundBehindVideo,
        Storyboard = Storyboard,
        BackgroundDim = BackgroundDim,
    };

    public static void Import(Data? d)
    {
        if (d == null) return;
        PreferBeatmapSkins = d.PreferBeatmapSkins;
        ScrollSpeedChanges = d.ScrollSpeedChanges;
        Hitsounds = d.Hitsounds;
        HitsoundVolume = Math.Clamp(d.HitsoundVolume, 0f, 1f);
        Video = d.Video;
        VideoFullQuality = d.VideoFullQuality;
        BackgroundBehindVideo = d.BackgroundBehindVideo;
        Storyboard = d.Storyboard;
        BackgroundDim = Math.Clamp(d.BackgroundDim, 0f, 1f);
    }
}
