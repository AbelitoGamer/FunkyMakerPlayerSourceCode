using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace FunkyMakerPlayer.Osu;

/// <summary>
/// Desktop video decoder for <see cref="OsuVideo"/> (fork): runs ffmpeg and reads raw RGBA frames
/// from its stdout:
/// <c>ffmpeg -ss T -i video -an -vf scale+pad=640x360 -r 30 -f rawvideo -pix_fmt rgba -</c>.
/// The shipped ffmpeg is a minimal LGPL build made by Tools/build-ffmpeg-min.sh (only these
/// demuxers/decoders/filters, a few MB); any full ffmpeg works too. It is looked for next to the
/// game, in mods/tools and on PATH.
/// </summary>
internal sealed class FfmpegVideoStream : IOsuVideoStream
{
    private static string? _ffmpegPath;
    private static bool _ffmpegSearched;

    public static string? FindFfmpeg()
    {
        if (_ffmpegSearched) return _ffmpegPath;
        _ffmpegSearched = true;
        string exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        // Next to the real exe first: in a single-file Release, AppContext.BaseDirectory is the temp
        // extraction folder, and ffmpeg is excluded from the bundle (it sits beside the exe).
        var dirs = new[] { PlatformPaths.GetAppBaseDirectory(), AppContext.BaseDirectory, Path.Combine(PlatformPaths.GetModsBasePath(), "tools") }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        foreach (var d in dirs)
        {
            try
            {
                string p = Path.Combine(d.Trim().Trim('"'), exe);
                if (File.Exists(p)) return _ffmpegPath = p;
            }
            catch { }
        }
        return null;
    }

    private readonly Process? _process;
    private readonly Stream? _stdout;
    private readonly double _start;
    private int _frame;

    public FfmpegVideoStream(string ffmpeg, string file, double startSec)
    {
        _start = startSec;
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            CreateNoWindow = true,
        };
        int w = OsuVideo.Width, h = OsuVideo.Height;
        foreach (var a in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-nostdin",
                     "-ss", startSec.ToString("0.###", CultureInfo.InvariantCulture),
                     "-i", file, "-an",
                     "-vf", $"scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2",
                     "-r", OsuVideo.Fps.ToString(CultureInfo.InvariantCulture),
                     "-f", "rawvideo", "-pix_fmt", "rgba", "-",
                 })
            psi.ArgumentList.Add(a);
        try
        {
            _process = Process.Start(psi);
            _stdout = _process?.StandardOutput.BaseStream;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OsuVideo] ffmpeg failed to start: {ex.Message}");
        }
    }

    public double? ReadFrame(byte[] rgba)
    {
        if (_stdout == null) return null;
        int read = 0;
        while (read < OsuVideo.FrameBytes)
        {
            int got = _stdout.Read(rgba, read, OsuVideo.FrameBytes - read);
            if (got <= 0) return null; // end of the video, or ffmpeg killed / failed
            read += got;
        }
        return _start + _frame++ / (double)OsuVideo.Fps;
    }

    public void Cancel()
    {
        try { if (_process is { HasExited: false }) _process.Kill(); } catch { }
    }

    public void Dispose()
    {
        Cancel();
        _process?.Dispose();
    }
}
