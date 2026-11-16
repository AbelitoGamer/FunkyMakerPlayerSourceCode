using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FunkyMakerPlayer;

/// <summary>
/// Cross-platform updater. Checks GitHub Releases for a newer version and self-updates.
/// Windows: Win32 log window on main thread, updater logic on background thread.
/// Linux:   Console output + shell script.
/// </summary>
public static class Updater
{
    // Si el repo oficial ya tiene releases usa su página; si no, la del repo legacy.
    private const string OFFICIAL_RELEASES_PAGE = "https://github.com/Thisisfenix/FunkyMakerPlayerSourceCode/releases/latest";
    private const string LEGACY_RELEASES_PAGE   = "https://github.com/Thisisfenix/thisisfenix.github.io/releases/latest";
    private static bool _officialRepoHasReleases;
    public static string LatestReleasePageUrl => _officialRepoHasReleases ? OFFICIAL_RELEASES_PAGE : LEGACY_RELEASES_PAGE;
    private static readonly string[] API_URLS =
    {
        "https://api.github.com/repos/Thisisfenix/FunkyMakerPlayerSourceCode/releases",
        "https://api.github.com/repos/Thisisfenix/thisisfenix.github.io/releases", // legacy (<=0.4.8)
    };

    private static string WinAsset    => "FunkyMakerPlayer_win.zip";
    private static string LinuxAsset  => "FunkyMakerPlayer_linux.zip";
    private static string PlatformAsset => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? WinAsset : LinuxAsset;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
    private const uint MB_YESNO       = 0x00000004;
    private const uint MB_ICONINFO    = 0x00000040;
    private const uint MB_SYSTEMMODAL = 0x00001000;
    private const int  IDYES          = 6;

#if !DEBUG
    private static UpdateLogWindow? _logWin;
#endif

    private static void Log(string msg)
    {
        Console.WriteLine(msg);
#if !DEBUG
        _logWin?.AppendLine(msg);
#endif
    }

    // ── Public entry point ────────────────────────────────────────────────────
    public static bool CheckAndUpdate()
    {
#if DEBUG
        Console.WriteLine("[Updater] Debug build — skipping update check.");
        return false;
#else
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return RunUpdateLogic();

        // Windows: main thread owns the log window message loop.
        // Updater logic runs on a background thread so the window stays responsive
        // and MessageBox doesn't deadlock with the window's message pump.
        _logWin = new UpdateLogWindow();
        bool result = false;

        var worker = new Thread(() => { result = RunUpdateLogic(); });
        worker.IsBackground = true;
        worker.Start();

        _logWin.Show();   // blocks until window closes
        worker.Join(5000);
        return result;
#endif
    }

    // ── Core update logic (runs on background thread on Windows, main thread on Linux) ──
    private static bool RunUpdateLogic()
    {
        try
        {
            Log("[Updater] Checking for updates...");
            Log($"[Updater] Platform asset: {PlatformAsset}");

            var (latestTag, downloadUrl, latestVersion, changelog) = FetchLatestRelease().GetAwaiter().GetResult();

            if (latestTag == null || downloadUrl == null || latestVersion == null)
            {
                Log("[Updater] No matching release found. Up to date or no internet.");
#if !DEBUG
                _logWin?.CloseAfterDelay(1500);
#endif
                return false;
            }

            string localRaw = GetLocalVersion();
            Log($"[Updater] Local:  {localRaw}");
            Log($"[Updater] Latest: {latestVersion}");
            Log($"[Updater] URL:    {downloadUrl}");

            if (!System.Version.TryParse(localRaw, out var localVersion))
                localVersion = new System.Version(0, 0, 0);

            if (latestVersion <= localVersion)
            {
                Log("[Updater] Already up to date.");
#if !DEBUG
                _logWin?.CloseAfterDelay(1500);
#endif
                return false;
            }

            // Show changelog if available
            if (!string.IsNullOrWhiteSpace(changelog))
            {
                Log("");
                Log($"========== What's new in {latestTag} ==========");
                // Trim and format changelog
                string[] lines = changelog.Split('\n');
                foreach (string line in lines)
                {
                    string trimmed = line.TrimEnd();
                    if (!string.IsNullOrEmpty(trimmed))
                        Log(trimmed);
                }
                Log("=".PadRight(50, '='));
                Log("");
            }

            // Ask user — on Windows this runs on the background thread so the log window
            // message loop (on main thread) keeps pumping and doesn't freeze
            bool confirmed;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string msg = $"A new version is available!\n\n  Current: {localRaw}\n  Latest:  {latestVersion}\n\nDownload and install now?";
                int r = MessageBox(IntPtr.Zero, msg, "Funky Maker Player - Update Available",
                    MB_YESNO | MB_ICONINFO | MB_SYSTEMMODAL);
                confirmed = r == IDYES;
            }
            else
            {
                Console.WriteLine($"[Updater] New version: {localRaw} -> {latestVersion}");
                // On Linux: try zenity/kdialog for a GUI prompt, fall back to auto-accept
                // Set FMP_NO_UPDATE=1 to always skip updates
                if (Environment.GetEnvironmentVariable("FMP_NO_UPDATE") == "1")
                {
                    Log("[Updater] FMP_NO_UPDATE=1 — skipping update.");
                    confirmed = false;
                }
                else
                {
                    confirmed = AskUserLinux(localRaw, latestVersion.ToString(3));
                }
            }

            if (!confirmed)
            {
                Log("[Updater] User declined update.");
#if !DEBUG
                _logWin?.CloseAfterDelay(1000);
#endif
                return false;
            }

            Log($"[Updater] Downloading {PlatformAsset}...");

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return UpdateWindows(downloadUrl, latestVersion.ToString(3));
            else
                return UpdateLinux(downloadUrl, latestVersion.ToString(3));
        }
        catch (Exception ex)
        {
            Log($"[Updater] ERROR: {ex.Message}");
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                MessageBox(IntPtr.Zero, $"Update failed:\n{ex.Message}", "Update Error",
                    MB_SYSTEMMODAL | 0x00000010);
            else
                Console.Error.WriteLine($"[Updater] ERROR: {ex.Message}");
#if !DEBUG
            _logWin?.CloseAfterDelay(3000);
#endif
            return false;
        }
    }

    // ── Version fetch ─────────────────────────────────────────────────────────
    private static async Task<(string? tag, string? downloadUrl, System.Version? version, string? changelog)> FetchLatestRelease()
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "FunkyMakerPlayer-Updater");
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        client.Timeout = TimeSpan.FromSeconds(15);

        string target = PlatformAsset;
        string? bestTag = null;
        string? bestUrl = null;
        System.Version? bestVer = null;
        string? bestChangelog = null;

        foreach (string apiUrl in API_URLS)
        for (int page = 1; page <= 5; page++)
        {
            string pageUrl = $"{apiUrl}?per_page=30&page={page}";
            Log($"[Updater] Fetching releases page {page}...");

            HttpResponseMessage response;
            try { response = await client.GetAsync(pageUrl); }
            catch (Exception ex) { Log($"[Updater] HTTP error: {ex.Message}"); break; }

            if (!response.IsSuccessStatusCode)
            {
                Log($"[Updater] API returned HTTP {(int)response.StatusCode}");
                break;
            }

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var releases = doc.RootElement;

            if (releases.ValueKind != JsonValueKind.Array || releases.GetArrayLength() == 0)
            {
                Log($"[Updater] No more releases on page {page}.");
                break;
            }

            foreach (var release in releases.EnumerateArray())
            {
                if (apiUrl == API_URLS[0]) _officialRepoHasReleases = true;
                bool isDraft      = release.TryGetProperty("draft",      out var d) && d.GetBoolean();
                bool isPrerelease = release.TryGetProperty("prerelease", out var p) && p.GetBoolean();
                if (isDraft || isPrerelease) continue;

                string? tag = release.TryGetProperty("tag_name", out var tp) ? tp.GetString() : null;
                if (tag == null) continue;

                string vStr = tag.TrimStart('v');
                if (!System.Version.TryParse(vStr, out var ver)) continue;

                if (!release.TryGetProperty("assets", out var assets)) continue;

                foreach (var asset in assets.EnumerateArray())
                {
                    string? name = asset.TryGetProperty("name", out var np) ? np.GetString() : null;
                    string? url  = asset.TryGetProperty("browser_download_url", out var up) ? up.GetString() : null;
                    if (name == null || url == null) continue;

                    Log($"[Updater]   {tag} \u2192 {name}");

                    // Explicitly reject any asset with "latest" in the name
                    if (name.IndexOf("latest", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Log($"[Updater]   SKIP (contains 'latest'): {name}");
                        continue;
                    }

                    // Must be an exact match for the platform asset name
                    if (!string.Equals(name, target, StringComparison.OrdinalIgnoreCase)) continue;

                    Log($"[Updater]   \u2713 MATCH: {tag} ({url})");
                    if (bestVer == null || ver > bestVer)
                    {
                        bestVer = ver;
                        bestTag = tag;
                        bestUrl = url;
                        // Extract changelog/body
                        bestChangelog = release.TryGetProperty("body", out var body) ? body.GetString() : null;
                    }
                    break;
                }
            }
        }

        if (bestUrl != null)
            Log($"[Updater] Best: {bestTag} \u2192 {bestUrl}");
        else
            Log("[Updater] No platform asset found in any release.");

        return (bestTag, bestUrl, bestVer, bestChangelog);
    }

    // ── Local version ─────────────────────────────────────────────────────────
    public static string GetLocalVersionString()
    {
        string v = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        if (v == "0.0.0" || v == "1.0.0" || v.StartsWith("0.0.0."))
        {
            string vf = Path.Combine(AppContext.BaseDirectory, "version.txt");
            if (File.Exists(vf))
            {
                string s = File.ReadAllText(vf).Trim();
                if (!string.IsNullOrEmpty(s)) v = s;
            }
        }
        return v;
    }
    
    private static string GetLocalVersion()
    {
        return GetLocalVersionString();
    }

    // ── Public method for checking updates (for Android) ──────────────────────
    public static async Task<(string tag, System.Version version, string downloadUrl, string changelog)?> CheckForUpdatesAsync()
    {
        try
        {
            var (latestTag, downloadUrl, latestVersion, changelog) = await FetchLatestRelease();

            if (latestTag == null || latestVersion == null)
                return null;

            string localRaw = GetLocalVersion();
            if (!System.Version.TryParse(localRaw, out var localVersion))
                localVersion = new System.Version(0, 0, 0);

            if (latestVersion <= localVersion)
                return null;

            return (latestTag, latestVersion, downloadUrl ?? "", changelog ?? "");
        }
        catch
        {
            return null;
        }
    }

    // ── Linux user prompt ─────────────────────────────────────────────────────
    private static bool AskUserLinux(string local, string remote)
    {
        string msg = $"Funky Maker Player {remote} is available (you have {local}). Update now?";
        // Try zenity (GNOME/GTK)
        if (CommandExists("zenity"))
        {
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName  = "zenity",
                Arguments = $"--question --title=\"Funky Maker Player - Update\" --text=\"{msg}\" --ok-label=\"Update\" --cancel-label=\"Skip\"",
                UseShellExecute = false,
            });
            p?.WaitForExit(15000);
            return p?.ExitCode == 0;
        }
        // Try kdialog (KDE)
        if (CommandExists("kdialog"))
        {
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName  = "kdialog",
                Arguments = $"--yesno \"{msg}\" --title \"Funky Maker Player - Update\"",
                UseShellExecute = false,
            });
            p?.WaitForExit(15000);
            return p?.ExitCode == 0;
        }
        // No GUI available — skip update silently, user can update manually later
        Console.WriteLine($"[Updater] {msg}");
        Console.WriteLine("[Updater] No GUI dialog available (zenity/kdialog not found) — skipping update.");
        Console.WriteLine("[Updater] To update manually, run with FMP_NO_UPDATE=0 or install zenity.");
        return false;
    }

    private static bool CommandExists(string cmd)
    {
        try
        {
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "which", Arguments = cmd,
                UseShellExecute = false, RedirectStandardOutput = true,
            });
            p?.WaitForExit(2000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    // ── Windows update ────────────────────────────────────────────────────────
    private static bool UpdateWindows(string url, string version)
    {
#if !DEBUG
        _logWin?.SetProgress(0);
#endif

        string exeDir = AppContext.BaseDirectory;
        string updaterPath = Path.Combine(exeDir, "Assets", "updater", "FunkyMakerUpdater.exe");

        // Check if external updater exists
        if (File.Exists(updaterPath))
        {
            Log($"[Updater] Launching external updater...");
            try
            {
                // Launch updater with download URL, current process ID, and version
                var startInfo = new ProcessStartInfo
                {
                    FileName = updaterPath,
                    Arguments = $"\"{url}\" {Process.GetCurrentProcess().Id} \"{version}\"",
                    UseShellExecute = true,
                    WorkingDirectory = Path.Combine(exeDir, "Assets", "updater")
                };

#if !DEBUG
                _logWin?.SetProgress(100);
#endif
                Log("[Updater] Starting external updater...");
                Thread.Sleep(1000);

                Process.Start(startInfo);

#if !DEBUG
                _logWin?.CloseAfterDelay(500);
#endif

                // Exit the game so updater can replace files
                Environment.Exit(0);
                return true;
            }
            catch (Exception ex)
            {
                Log($"[Updater] Failed to launch external updater: {ex.Message}");
                Log("[Updater] Falling back to batch method...");
            }
        }
        else
        {
            Log($"[Updater] External updater not found at: {updaterPath}");
            Log("[Updater] Using fallback batch method...");
        }

        // Fallback to old batch method if updater doesn't exist or fails
        string tmpDir  = Path.Combine(Path.GetTempPath(), "FMPUpdate_" + Path.GetRandomFileName());
        string zipPath = tmpDir + ".zip";
        Directory.CreateDirectory(tmpDir);

        Log("[Updater] Downloading...");
        DownloadWithProgress(url, zipPath).GetAwaiter().GetResult();

        Log("[Updater] Extracting...");
#if !DEBUG
        _logWin?.SetProgress(95);
#endif
        ZipFile.ExtractToDirectory(zipPath, tmpDir, overwriteFiles: true);
        File.Delete(zipPath);
        Log($"[Updater] Extracted to: {tmpDir}");

        string exePath = Path.Combine(exeDir, "FunkyMakerPlayer.exe");
        string batPath = Path.Combine(Path.GetTempPath(), "fmp_update_" + Path.GetRandomFileName() + ".bat");
        string logPath = Path.Combine(exeDir, "update.log");

        string bat =
            "@echo off\r\n" +
            "title Funky Maker Player - Updating...\r\n" +
            $"set LOGFILE=\"{logPath}\"\r\n" +
            $"set EXEPATH=\"{exePath}\"\r\n" +
            $"set TMPDIR=\"{tmpDir}\"\r\n" +
            $"set EXEDIR=\"{exeDir}\"\r\n" +
            "echo [update.bat] Waiting for game to exit... > %LOGFILE%\r\n" +
            "echo Waiting for game to close...\r\n" +
            "timeout /t 3 /nobreak >nul\r\n" +
            "echo [update.bat] Copying files (excluding mods folder)... >> %LOGFILE%\r\n" +
            "echo Copying update files (preserving mods)...\r\n" +
            // Save old exe size for comparison
            "for %%F in (%EXEPATH%) do set OLD_SIZE=%%~zF\r\n" +
            // Exclude mods folder to preserve user content
            $"robocopy \"%TMPDIR%\" \"%EXEDIR%\" /E /IS /IT /NP /XD mods >> %LOGFILE% 2>&1\r\n" +
            "set ROBO_ERR=%ERRORLEVEL%\r\n" +
            "if %ROBO_ERR% GEQ 8 (\r\n" +
            "  echo [update.bat] robocopy failed ^(exit %ROBO_ERR%^), trying manual copy... >> %LOGFILE%\r\n" +
            "  echo robocopy failed, trying manual copy...\r\n" +
            // Manual copy excluding mods folder
            $"  for /D %%D in (\"%TMPDIR%\\*\") do (\r\n" +
            "    if /I not \"%%~nxD\"==\"mods\" (\r\n" +
            $"      xcopy /E /Y /I \"%%D\" \"%EXEDIR%\\%%~nxD\\\" >> %LOGFILE% 2>&1\r\n" +
            "    )\r\n" +
            "  )\r\n" +
            $"  for %%F in (\"%TMPDIR%\\*\") do (\r\n" +
            "    if not \"%%~xF\"==\"\" (\r\n" +
            $"      copy /Y \"%%F\" \"%EXEDIR%\\\" >> %LOGFILE% 2>&1\r\n" +
            "    )\r\n" +
            "  )\r\n" +
            ")\r\n" +
            // Verify exe was actually replaced
            "for %%F in (%EXEPATH%) do set NEW_SIZE=%%~zF\r\n" +
            "echo [update.bat] Old exe size: %OLD_SIZE% bytes >> %LOGFILE%\r\n" +
            "echo [update.bat] New exe size: %NEW_SIZE% bytes >> %LOGFILE%\r\n" +
            "echo Old exe: %OLD_SIZE% bytes\r\n" +
            "echo New exe: %NEW_SIZE% bytes\r\n" +
            "if \"%OLD_SIZE%\"==\"%NEW_SIZE%\" (\r\n" +
            "  echo [update.bat] WARNING: exe size unchanged - update may not have applied >> %LOGFILE%\r\n" +
            "  echo WARNING: exe size unchanged. Update may not have applied.\r\n" +
            "  echo Check update.log for details.\r\n" +
            "  pause\r\n" +
            ") else (\r\n" +
            "  echo [update.bat] SUCCESS: exe replaced successfully >> %LOGFILE%\r\n" +
            "  echo [update.bat] User mods folder preserved >> %LOGFILE%\r\n" +
            "  echo Update applied successfully! (mods preserved)\r\n" +
            ")\r\n" +
            $"rmdir /S /Q \"{tmpDir}\"\r\n" +
            "echo [update.bat] Relaunching... >> %LOGFILE%\r\n" +
            "echo Relaunching game...\r\n" +
            "timeout /t 2 /nobreak >nul\r\n" +
            $"start \"\" \"{exePath}\"\r\n" +
            "del \"%~f0\"\r\n";

        File.WriteAllText(batPath, bat);
        Log($"[Updater] Update script: {batPath}");

#if !DEBUG
        _logWin?.SetProgress(100);
#endif
        Log("[Updater] Done! Restarting in 2 seconds...");
        Thread.Sleep(2000);

        // Launch bat visible so user can see progress and any errors
        Process.Start(new ProcessStartInfo
        {
            FileName        = "cmd.exe",
            Arguments       = $"/c \"\"{batPath}\"\"",
            UseShellExecute = true,
            WindowStyle     = ProcessWindowStyle.Normal,
        });

#if !DEBUG
        _logWin?.CloseAfterDelay(500);
#endif
        
        // Exit the game immediately so the updater batch can replace the exe
        Environment.Exit(0);
        return true;
    }

    // ── Linux update ──────────────────────────────────────────────────────────
    private static bool UpdateLinux(string url, string version)
    {
        string exeDir  = AppContext.BaseDirectory;
        string tmpDir  = Path.Combine(exeDir, ".fmp_update_" + Path.GetRandomFileName());
        string zipPath = tmpDir + ".zip";
        Directory.CreateDirectory(tmpDir);

        Log("[Updater] Downloading...");
        DownloadWithProgress(url, zipPath).GetAwaiter().GetResult();

        Log("[Updater] Extracting...");
        ZipFile.ExtractToDirectory(zipPath, tmpDir, overwriteFiles: true);
        File.Delete(zipPath);

        string exePath = Path.Combine(exeDir, "FunkyMakerPlayer");
        string shPath  = Path.Combine(exeDir, "update.sh");
        string logPath = Path.Combine(exeDir, "update.log");

        string sh =
            "#!/bin/bash\n" +
            $"exec > \"{logPath}\" 2>&1\n" +
            "echo \"[update.sh] Waiting for game to exit...\"\n" +
            "sleep 3\n" +
            "echo \"[update.sh] Copying files (excluding mods folder)...\"\n" +
            // Copy everything except mods folder
            $"rsync -av --exclude='mods' \"{tmpDir}/\" \"{exeDir}/\" || " +
            $"(find \"{tmpDir}\" -mindepth 1 -maxdepth 1 ! -name 'mods' -exec cp -r {{}} \"{exeDir}/\" \\;)\n" +
            $"chmod +x \"{exePath}\"\n" +
            $"rm -rf \"{tmpDir}\"\n" +
            "echo \"[update.sh] User mods folder preserved\"\n" +
            $"export DOTNET_BUNDLE_EXTRACT_BASE_DIR=\"{exeDir}/.fmp_runtime\"\n" +
            $"mkdir -p \"$DOTNET_BUNDLE_EXTRACT_BASE_DIR\"\n" +
            $"nohup \"{exePath}\" </dev/null >>\"{logPath}\" 2>&1 &\n" +
            "echo \"[update.sh] Done.\"\n" +
            "rm -- \"$0\"\n";

        File.WriteAllText(shPath, sh);
        try { Process.Start("chmod", $"+x \"{shPath}\"")?.WaitForExit(2000); } catch { }

        string launcher = File.Exists("/usr/bin/setsid") ? "/usr/bin/setsid"
                        : File.Exists("/bin/setsid")     ? "/bin/setsid"
                        : "/bin/bash";
        string launcherArgs = launcher.EndsWith("setsid")
            ? $"/bin/bash \"{shPath}\""
            : $"\"{shPath}\"";

        Process.Start(new ProcessStartInfo
        {
            FileName        = launcher,
            Arguments       = launcherArgs,
            UseShellExecute = false,
            CreateNoWindow  = true,
        });

        Log($"[Updater] Update script launched. Log: {logPath}");
        
        // Exit immediately so the update script can replace the binary
        Environment.Exit(0);
        return true;
    }

    /// <summary>
    /// Downloads and applies an update in the background without blocking startup or spawning Win32 log windows.
    /// Reports download progress (0..1) and status text to the in-game UI.
    /// </summary>
    public static async Task<bool> DownloadAndApplyUpdateAsync(
        string url,
        string version,
        Action<float, string>? onProgress = null,
        Action<string>? onError = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                onError?.Invoke("No download URL available for this platform.");
                return false;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string exeDir = AppContext.BaseDirectory;
                string updaterPath = Path.Combine(exeDir, "Assets", "updater", "FunkyMakerUpdater.exe");

                if (File.Exists(updaterPath))
                {
                    onProgress?.Invoke(0.95f, "Launching FunkyMakerUpdater...");
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = updaterPath,
                        Arguments = $"\"{url}\" {Process.GetCurrentProcess().Id} \"{version}\"",
                        UseShellExecute = true,
                        WorkingDirectory = Path.Combine(exeDir, "Assets", "updater")
                    };
                    await Task.Delay(300);
                    Process.Start(startInfo);
                    onProgress?.Invoke(1.0f, "Restarting...");
                    await Task.Delay(300);
                    Environment.Exit(0);
                    return true;
                }

                string tmpDir = Path.Combine(Path.GetTempPath(), "FMPUpdate_" + Path.GetRandomFileName());
                string zipPath = tmpDir + ".zip";
                Directory.CreateDirectory(tmpDir);

                onProgress?.Invoke(0.02f, "Connecting to GitHub...");
                await DownloadWithProgress(url, zipPath, onProgress);

                onProgress?.Invoke(0.94f, "Extracting update files...");
                await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, tmpDir, overwriteFiles: true));
                try { File.Delete(zipPath); } catch { }

                onProgress?.Invoke(0.99f, "Applying update & restarting...");
                LaunchWindowsBatchUpdater(exeDir, tmpDir);
                await Task.Delay(400);
                Environment.Exit(0);
                return true;
            }
            else
            {
                string exeDir = AppContext.BaseDirectory;
                string tmpDir = Path.Combine(exeDir, ".fmp_update_" + Path.GetRandomFileName());
                string zipPath = tmpDir + ".zip";
                Directory.CreateDirectory(tmpDir);

                onProgress?.Invoke(0.02f, "Connecting to GitHub...");
                await DownloadWithProgress(url, zipPath, onProgress);

                onProgress?.Invoke(0.94f, "Extracting update files...");
                await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, tmpDir, overwriteFiles: true));
                try { File.Delete(zipPath); } catch { }

                onProgress?.Invoke(0.99f, "Applying update & restarting...");
                LaunchLinuxShellUpdater(exeDir, tmpDir);
                Environment.Exit(0);
                return true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Updater] In-menu update failed: {ex.Message}");
            onError?.Invoke(ex.Message);
            return false;
        }
    }

    private static void LaunchWindowsBatchUpdater(string exeDir, string tmpDir)
    {
        string exePath = Path.Combine(exeDir, "FunkyMakerPlayer.exe");
        string batPath = Path.Combine(Path.GetTempPath(), "fmp_update_" + Path.GetRandomFileName() + ".bat");
        string logPath = Path.Combine(exeDir, "update.log");

        string bat =
            "@echo off\r\n" +
            "title Funky Maker Player - Updating...\r\n" +
            $"set LOGFILE=\"{logPath}\"\r\n" +
            $"set EXEPATH=\"{exePath}\"\r\n" +
            $"set TMPDIR=\"{tmpDir}\"\r\n" +
            $"set EXEDIR=\"{exeDir}\"\r\n" +
            "echo [update.bat] Waiting for game to exit... > %LOGFILE%\r\n" +
            "timeout /t 3 /nobreak >nul\r\n" +
            "for %%F in (%EXEPATH%) do set OLD_SIZE=%%~zF\r\n" +
            $"robocopy \"%TMPDIR%\" \"%EXEDIR%\" /E /IS /IT /NP /XD mods >> %LOGFILE% 2>&1\r\n" +
            "set ROBO_ERR=%ERRORLEVEL%\r\n" +
            "if %ROBO_ERR% GEQ 8 (\r\n" +
            $"  for /D %%D in (\"%TMPDIR%\\*\") do (\r\n" +
            "    if /I not \"%%~nxD\"==\"mods\" (\r\n" +
            $"      xcopy /E /Y /I \"%%D\" \"%EXEDIR%\\%%~nxD\\\" >> %LOGFILE% 2>&1\r\n" +
            "    )\r\n" +
            "  )\r\n" +
            $"  for %%F in (\"%TMPDIR%\\*\") do (\r\n" +
            "    if not \"%%~xF\"==\"\" (\r\n" +
            $"      copy /Y \"%%F\" \"%EXEDIR%\\\" >> %LOGFILE% 2>&1\r\n" +
            "    )\r\n" +
            "  )\r\n" +
            ")\r\n" +
            $"rmdir /S /Q \"{tmpDir}\"\r\n" +
            "timeout /t 2 /nobreak >nul\r\n" +
            $"start \"\" \"{exePath}\"\r\n" +
            "del \"%~f0\"\r\n";

        File.WriteAllText(batPath, bat);
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{batPath}\"\"",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal,
        });
    }

    private static void LaunchLinuxShellUpdater(string exeDir, string tmpDir)
    {
        string exePath = Path.Combine(exeDir, "FunkyMakerPlayer");
        string shPath = Path.Combine(exeDir, "update.sh");
        string logPath = Path.Combine(exeDir, "update.log");

        string sh =
            "#!/bin/bash\n" +
            $"exec > \"{logPath}\" 2>&1\n" +
            "sleep 3\n" +
            $"rsync -av --exclude='mods' \"{tmpDir}/\" \"{exeDir}/\" || " +
            $"(find \"{tmpDir}\" -mindepth 1 -maxdepth 1 ! -name 'mods' -exec cp -r {{}} \"{exeDir}/\" \\;)\n" +
            $"chmod +x \"{exePath}\"\n" +
            $"rm -rf \"{tmpDir}\"\n" +
            $"export DOTNET_BUNDLE_EXTRACT_BASE_DIR=\"{exeDir}/.fmp_runtime\"\n" +
            $"mkdir -p \"$DOTNET_BUNDLE_EXTRACT_BASE_DIR\"\n" +
            $"nohup \"{exePath}\" </dev/null >>\"{logPath}\" 2>&1 &\n" +
            "rm -- \"$0\"\n";

        File.WriteAllText(shPath, sh);
        try { Process.Start("chmod", $"+x \"{shPath}\"")?.WaitForExit(2000); } catch { }

        string launcher = File.Exists("/usr/bin/setsid") ? "/usr/bin/setsid"
                        : File.Exists("/bin/setsid")     ? "/bin/setsid"
                        : "/bin/bash";
        string launcherArgs = launcher.EndsWith("setsid")
            ? $"/bin/bash \"{shPath}\""
            : $"\"{shPath}\"";

        Process.Start(new ProcessStartInfo
        {
            FileName = launcher,
            Arguments = launcherArgs,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    // ── Download helper ───────────────────────────────────────────────────────
    private static async Task DownloadWithProgress(string url, string destPath, Action<float, string>? onProgress = null)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "FunkyMakerPlayer-Updater");
        client.Timeout = TimeSpan.FromMinutes(5);

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;
        using var fs     = File.Create(destPath);
        using var stream = await response.Content.ReadAsStreamAsync();

        byte[] buffer = new byte[81920];
        long downloaded = 0;
        int read;
        var startTime = DateTime.UtcNow;
        var lastUpdate = startTime;
        
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            await fs.WriteAsync(buffer.AsMemory(0, read));
            downloaded += read;
            
            var now = DateTime.UtcNow;
            // Update UI every 100ms to avoid flooding
            if ((now - lastUpdate).TotalMilliseconds < 100 && downloaded < (total ?? long.MaxValue))
                continue;
            
            lastUpdate = now;
            
            if (total.HasValue && total > 0)
            {
                int pct = (int)(downloaded * 90 / total.Value);
                double elapsed = (now - startTime).TotalSeconds;
                double speed = elapsed > 0 ? downloaded / elapsed : 0; // bytes per second
                double speedMB = speed / 1048576.0;
                
                string status;
                if (speed > 0 && downloaded < total.Value)
                {
                    double remaining = (total.Value - downloaded) / speed;
                    int remainingSec = (int)remaining;
                    string eta = remainingSec < 60 
                        ? $"{remainingSec}s" 
                        : $"{remainingSec / 60}m {remainingSec % 60}s";
                    status = $"Downloading... {downloaded / 1048576f:F1} MB / {total.Value / 1048576f:F1} MB ({pct}%) - {speedMB:F1} MB/s - ETA: {eta}";
                }
                else
                {
                    status = $"Downloading... {downloaded / 1048576f:F1} MB / {total.Value / 1048576f:F1} MB ({pct}%)";
                }

                onProgress?.Invoke(Math.Clamp((float)downloaded / total.Value * 0.90f, 0f, 0.90f), status);
                
#if !DEBUG
                _logWin?.SetProgress(pct);
                _logWin?.SetLastLine(status);
#endif
                Console.Write($"\r[Updater] {status}   ");
            }
        }
        Console.WriteLine();
        Log($"[Updater] Download complete.");
    }
}

// ── Update Log Window (Windows only) ─────────────────────────────────────────
public class UpdateLogWindow
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string title,
        uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowText(IntPtr hWnd, string text);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("comctl32.dll")] private static extern void InitCommonControls();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint a, uint p, ref RECT r, uint f);
    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(int color);
    [DllImport("user32.dll")] private static extern IntPtr SetClassLongPtr(IntPtr hWnd, int index, IntPtr newLong);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int ptX, ptY; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int L, T, R, B; }

    private IntPtr _hwnd, _edit, _progress, _statusLabel;
    private volatile bool _running = true;
    private volatile bool _shouldClose = false;

    private const uint WM_DESTROY      = 0x0002;
    private const uint WM_CTLCOLORSTATIC = 0x0138;
    private const uint WS_OVERLAPPED   = 0x00000000;
    private const uint WS_CAPTION     = 0x00C00000;
    private const uint WS_SYSMENU     = 0x00080000;
    private const uint WS_VISIBLE     = 0x10000000;
    private const uint WS_CHILD       = 0x40000000;
    private const uint WS_VSCROLL     = 0x00200000;
    private const uint ES_MULTILINE   = 0x00000004;
    private const uint ES_AUTOVSCROLL = 0x00000040;
    private const uint ES_READONLY    = 0x00000800;
    private const uint SS_LEFT        = 0x00000000;
    private const uint PBM_SETRANGE32 = 0x0406;
    private const uint PBM_SETPOS     = 0x0402;
    private const uint EM_SETSEL      = 0x00B1;
    private const uint EM_SCROLLCARET = 0x00B7;
    private const uint EM_REPLACESEL  = 0x00C2;
    private const uint PM_REMOVE      = 0x0001;

    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();
    private int _pendingProgress = -1;

    public void Show()
    {
        InitCommonControls();
        IntPtr hInst = GetModuleHandle(null);
        int W = 700, H = 520;

        _hwnd = CreateWindowEx(0, "#32770", "Funky Maker Player - Updater",
            WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_VISIBLE,
            -1, -1, W, H, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero) return;
        CenterWindow(_hwnd);

        // Create a nicer background color (dark theme)
        IntPtr brush = CreateSolidBrush(0x1E1E1E); // Dark gray
        SetClassLongPtr(_hwnd, -10, brush); // GCL_HBRBACKGROUND

        // Title/header label with larger font
        _statusLabel = CreateWindowEx(0, "STATIC", "Checking for updates...",
            WS_CHILD | WS_VISIBLE | SS_LEFT,
            16, 12, W - 32, 24, _hwnd, IntPtr.Zero, hInst, IntPtr.Zero);
        
        // Set white text color for dark background
        SendMessage(_statusLabel, WM_CTLCOLORSTATIC, IntPtr.Zero, IntPtr.Zero);

        // Log text box with dark theme - larger to fit all content
        _edit = CreateWindowEx(0x00000200, "EDIT", "", // WS_EX_CLIENTEDGE
            WS_CHILD | WS_VISIBLE | WS_VSCROLL | ES_MULTILINE | ES_AUTOVSCROLL | ES_READONLY,
            16, 44, W - 32, H - 140, _hwnd, IntPtr.Zero, hInst, IntPtr.Zero);
        
        // Set dark background and light text for edit control
        SendMessage(_edit, 0x00C4, IntPtr.Zero, new IntPtr(0xD4D4D4)); // EM_SETBKGNDCOLOR

        // Status label above progress bar
        IntPtr statusTextLabel = CreateWindowEx(0, "STATIC", "Initializing...",
            WS_CHILD | WS_VISIBLE | SS_LEFT,
            16, H - 88, W - 32, 18, _hwnd, IntPtr.Zero, hInst, IntPtr.Zero);

        // Progress bar with modern style
        _progress = CreateWindowEx(0, "msctls_progress32", "",
            WS_CHILD | WS_VISIBLE | 0x01, // PBS_SMOOTH for smooth animation
            16, H - 64, W - 32, 24, _hwnd, IntPtr.Zero, hInst, IntPtr.Zero);

        SendMessage(_progress, PBM_SETRANGE32, IntPtr.Zero, new IntPtr(100));
        
        // Set progress bar color to cyan/blue theme
        SendMessage(_progress, 0x0409, IntPtr.Zero, new IntPtr(0xFF8800)); // PBM_SETBARCOLOR (orange)

        ShowWindow(_hwnd, 5);
        UpdateWindow(_hwnd);

        // Message loop with idle processing for log queue
        while (_running)
        {
            // Drain log queue
            while (_lines.TryDequeue(out string? line))
                AppendToEdit(line);

            // Apply pending progress
            int prog = _pendingProgress;
            if (prog >= 0)
            {
                _pendingProgress = -1;
                SendMessage(_progress, PBM_SETPOS, new IntPtr(prog), IntPtr.Zero);
            }

            // Check close flag
            if (_shouldClose)
            {
                _running = false;
                if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
                PostQuitMessage(0);
                break;
            }

            // Process one message if available, otherwise sleep briefly
            if (PeekMessage(out MSG msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
                if (msg.message == WM_DESTROY)
                {
                    _running = false;
                    PostQuitMessage(0);
                    break;
                }
            }
            else
            {
                Thread.Sleep(16); // ~60fps idle
            }
        }
    }

    private void AppendToEdit(string text)
    {
        if (_edit == IntPtr.Zero) return;
        SendMessage(_edit, EM_SETSEL, new IntPtr(int.MaxValue), new IntPtr(int.MaxValue));
        // Use P/Invoke overload that takes string
        AppendText(_edit, text + "\r\n");
        SendMessage(_edit, EM_SCROLLCARET, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr w, [MarshalAs(UnmanagedType.LPWStr)] string l);

    private static void AppendText(IntPtr edit, string text)
        => SendMessage(edit, EM_REPLACESEL, IntPtr.Zero, text);

    // Thread-safe: queue a line — processed in the message loop
    public void AppendLine(string text) => _lines.Enqueue(text);

    public void SetProgress(int pct) => _pendingProgress = Math.Clamp(pct, 0, 100);

    public void SetLastLine(string text)
    {
        if (_statusLabel != IntPtr.Zero)
            SetWindowText(_statusLabel, text);
    }

    public void Hide()
    {
        if (_hwnd != IntPtr.Zero) ShowWindow(_hwnd, 0);
    }

    public void CloseAfterDelay(int ms)
    {
        Task.Run(async () =>
        {
            await Task.Delay(ms);
            _shouldClose = true;
        });
    }

    public void Close() => _shouldClose = true;

    private static void CenterWindow(IntPtr hWnd)
    {
        RECT screen = default;
        SystemParametersInfo(0x0030, 0, ref screen, 0);
        GetWindowRect(hWnd, out RECT win);
        int w = win.R - win.L, h = win.B - win.T;
        int x = (screen.R - screen.L - w) / 2 + screen.L;
        int y = (screen.B - screen.T - h) / 2 + screen.T;
        SetWindowPos(hWnd, IntPtr.Zero, x, y, w, h, 0x0004);
    }
}

/// <summary>Stub kept for compatibility — UpdateLogWindow handles everything now.</summary>
public class UpdateProgressWindow
{
    public void Show() { }
    public void SetProgress(int pct) { }
    public void SetStatus(string text) { }
    public void Close() { }
}
