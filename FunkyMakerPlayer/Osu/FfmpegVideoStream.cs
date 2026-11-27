using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace FunkyMakerPlayer.Osu;

/// <summary>
/// Desktop video decoder for <see cref="OsuVideo"/> (fork): runs ffmpeg and reads raw RGBA frames
/// from its stdout:
/// <c>ffmpeg -ss T -i video -an -vf scale+pad=WxH -r FPS -f rawvideo -pix_fmt rgba -</c>
/// (640×360 at 30 fps, or the video's own size / frame rate at full quality, see <see cref="Probe"/>).
/// The shipped ffmpeg is a minimal LGPL build made by Tools/build-ffmpeg-min.sh (only these
/// demuxers/decoders/filters, a few MB); any full ffmpeg works too. It is looked for next to the
/// game, in mods/tools and on PATH.
/// </summary>
internal sealed class FfmpegVideoStream : IOsuVideoStream
{
    private static string? _ffmpegPath;
    private static bool _ffmpegSearched;
    private static bool _useLegacyVsync; // true for FFmpeg < 5.1 (e.g. Ubuntu 22.04 LTS FFmpeg 4.4)

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
                if (!File.Exists(p)) continue;
                if (!OperatingSystem.IsWindows())
                {
                    // Skip non-executable local copies (e.g. after zip extract) so PATH fallback works
                    var mode = File.GetUnixFileMode(p);
                    if ((mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
                        continue;
                }
                return _ffmpegPath = p;
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// The video's size and frame rate, read from <c>ffmpeg -i file</c>'s stream info (the bundled
    /// minimal build has no ffprobe), e.g. "Stream #0:0: Video: h264 …, 1280x720, …, 24 fps, 24 tbr".
    /// Null when it can't be read.
    /// </summary>
    public static (int Width, int Height, double Fps)? Probe(string ffmpeg, string file)
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "-nostdin", "-i", file }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return null;
            var stderr = p.StandardError.ReadToEndAsync();
            p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return null; }
            string errText = stderr.Result;
            var ver = System.Text.RegularExpressions.Regex.Match(errText, @"ffmpeg version\s+n?(\d+)\.(\d+)");
            if (ver.Success &&
                int.TryParse(ver.Groups[1].Value, out int maj) &&
                int.TryParse(ver.Groups[2].Value, out int min))
            {
                _useLegacyVsync = maj < 5 || (maj == 5 && min < 1);
            }
            foreach (var line in errText.Split('\n'))
            {
                if (!line.Contains("Video:", StringComparison.Ordinal)) continue;
                var size = System.Text.RegularExpressions.Regex.Match(line, @"\b(\d{2,5})x(\d{2,5})\b");
                if (!size.Success) continue;
                var fps = System.Text.RegularExpressions.Regex.Match(line, @"([\d.]+)\s*fps");
                if (!fps.Success) fps = System.Text.RegularExpressions.Regex.Match(line, @"([\d.]+)\s*tbr");
                double f = fps.Success && double.TryParse(fps.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
                return (int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture),
                        int.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture), f);
            }
        }
        catch (Exception ex) { Console.WriteLine($"[OsuVideo] Probe failed: {ex.Message}"); }
        return null;
    }

    private readonly Process? _process;
    private readonly Stream? _stdout;
    private readonly double _start;
    private readonly VideoFormat _format;
    private int _frame;

    public FfmpegVideoStream(string ffmpeg, string file, double startSec, VideoFormat format)
    {
        _start = startSec;
        _format = format;
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            CreateNoWindow = true,
        };
        int w = format.Width, h = format.Height;
        foreach (var a in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-nostdin",
                     "-ss", startSec.ToString("0.###", CultureInfo.InvariantCulture),
                     "-i", file, "-an",
                     "-vf", $"scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2",
                     "-r", format.Fps.ToString("0.###", CultureInfo.InvariantCulture),
                     // Constant frame rate: frames land on their real timestamps (ReadFrame counts
                     // frames). Without it the rawvideo muxer passes frames through, so gaps (AVI null
                     // frames, a stream whose first frame is late, e.g. Mesmerizer's ~2.5 s) were
                     // dropped and the whole video ran early.
                     _useLegacyVsync ? "-vsync" : "-fps_mode", "cfr",
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
        while (read < _format.FrameBytes)
        {
            int got = _stdout.Read(rgba, read, _format.FrameBytes - read);
            if (got <= 0) return null; // end of the video, or ffmpeg killed / failed
            read += got;
        }
        return _start + _frame++ / _format.Fps;
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
