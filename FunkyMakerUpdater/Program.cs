using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FunkyMakerUpdater
{
    class Program
    {
        private const string GITHUB_API_LEGACY = "https://api.github.com/repos/Thisisfenix/thisisfenix.github.io/releases/latest";
        private const string GITHUB_API = "https://api.github.com/repos/Thisisfenix/FunkyMakerPlayerSourceCode/releases/latest";
        private const string ASSET_NAME = "FunkyMakerPlayer_win.zip";

        static async Task<int> Main(string[] args)
        {
            Console.WriteLine("=== FunkyMaker Player Updater ===\n");

            string downloadUrl;
            int gameProcessId = 0;
            string version = "unknown";

            if (args.Length >= 3)
            {
                // Called from game with all parameters
                downloadUrl = args[0];
                gameProcessId = int.Parse(args[1]);
                version = args[2];
            }
            else if (args.Length == 1)
            {
                // Called with just process ID, need to fetch update info from GitHub
                gameProcessId = int.Parse(args[0]);
                Console.WriteLine("Fetching latest release from GitHub...");
                
                var updateInfo = await GetLatestRelease();
                if (updateInfo == null)
                {
                    Console.WriteLine("✗ Failed to fetch update information from GitHub.");
                    Console.WriteLine("Press any key to exit...");
                    Console.ReadKey();
                    return 1;
                }

                downloadUrl = updateInfo.Value.url;
                version = updateInfo.Value.version;
                Console.WriteLine($"Found version: {version}");
            }
            else
            {
                Console.WriteLine("Usage:");
                Console.WriteLine("  FunkyMakerUpdater.exe <download_url> <game_process_id> <version>");
                Console.WriteLine("  FunkyMakerUpdater.exe <game_process_id>  (will fetch from GitHub)");
                Console.WriteLine("\nPress any key to exit...");
                Console.ReadKey();
                return 1;
            }

            Console.WriteLine($"\nDownload URL: {downloadUrl}");
            Console.WriteLine($"Target Version: {version}");
            
            if (gameProcessId > 0)
            {
                Console.WriteLine($"Waiting for game process (PID: {gameProcessId}) to exit...\n");

                // Wait for game to exit
                try
                {
                    var gameProcess = Process.GetProcessById(gameProcessId);
                    int waited = 0;
                    while (!gameProcess.HasExited && waited < 30)
                    {
                        Thread.Sleep(1000);
                        waited++;
                        Console.Write($"\rWaiting... {waited}s");
                    }
                    Console.WriteLine();
                }
                catch
                {
                    Console.WriteLine("Game process already exited.");
                }

                // Additional wait to ensure file handles are released
                Thread.Sleep(2000);
            }

            string gameDir = AppDomain.CurrentDomain.BaseDirectory;
            // Navigate up from Assets/updater to game root
            gameDir = Path.GetFullPath(Path.Combine(gameDir, "..", ".."));
            
            Console.WriteLine($"Game directory: {gameDir}");

            // Download update
            string tempZip = Path.Combine(Path.GetTempPath(), $"FMP_Update_{version}.zip");
            string extractDir = Path.Combine(Path.GetTempPath(), $"FMP_Extract_{Guid.NewGuid()}");

            try
            {
                Console.WriteLine("\nDownloading update...");
                await DownloadFile(downloadUrl, tempZip);

                Console.WriteLine("Extracting files...");
                Directory.CreateDirectory(extractDir);
                ZipFile.ExtractToDirectory(tempZip, extractDir, overwriteFiles: true);

                Console.WriteLine("Updating files (preserving mods folder)...");
                
                // Copy all files except mods
                CopyDirectory(extractDir, gameDir, excludeDir: "mods");

                Console.WriteLine("Cleaning up...");
                File.Delete(tempZip);
                Directory.Delete(extractDir, true);

                Console.WriteLine($"\n✓ Update to v{version} completed successfully!");
                Console.WriteLine("\nRelaunching game in 3 seconds...");
                Thread.Sleep(3000);

                // Relaunch game
                string exePath = Path.Combine(gameDir, "FunkyMakerPlayer.exe");
                if (File.Exists(exePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exePath,
                        UseShellExecute = true,
                        WorkingDirectory = gameDir
                    });
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n✗ Update failed: {ex.Message}");
                Console.WriteLine("\nPress any key to exit...");
                Console.ReadKey();
                return 1;
            }
        }

        static async Task<(string url, string version)?> GetLatestRelease()
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "FunkyMakerUpdater");
                client.Timeout = TimeSpan.FromSeconds(30);

                string response;
                try { response = await client.GetStringAsync(GITHUB_API); }
                catch { response = await client.GetStringAsync(GITHUB_API_LEGACY); } // repo nuevo sin releases aún
                var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;

                string? tag = root.GetProperty("tag_name").GetString();
                if (tag == null) return null;

                string version = tag.TrimStart('v');

                // Find the Windows asset
                if (root.TryGetProperty("assets", out var assets))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        string? name = asset.GetProperty("name").GetString();
                        string? url = asset.GetProperty("browser_download_url").GetString();

                        if (name != null && url != null && 
                            name.Equals(ASSET_NAME, StringComparison.OrdinalIgnoreCase))
                        {
                            return (url, version);
                        }
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching release: {ex.Message}");
                return null;
            }
        }

        static async Task DownloadFile(string url, string destination)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "FunkyMakerUpdater");
            client.Timeout = TimeSpan.FromMinutes(10);

            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;
            using var contentStream = await response.Content.ReadAsStreamAsync();
            using var fileStream = File.Create(destination);

            byte[] buffer = new byte[8192];
            long downloaded = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                downloaded += bytesRead;

                if (totalBytes.HasValue)
                {
                    int percent = (int)(downloaded * 100 / totalBytes.Value);
                    double mb = downloaded / 1048576.0;
                    double totalMb = totalBytes.Value / 1048576.0;
                    Console.Write($"\rProgress: {percent}% ({mb:F1} MB / {totalMb:F1} MB)");
                }
            }
            Console.WriteLine();
        }

        static void CopyDirectory(string source, string destination, string? excludeDir = null)
        {
            foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(source, dir);
                if (excludeDir != null && relativePath.StartsWith(excludeDir, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"  Skipping: {relativePath}");
                    continue;
                }

                string targetDir = Path.Combine(destination, relativePath);
                Directory.CreateDirectory(targetDir);
            }

            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(source, file);
                if (excludeDir != null && relativePath.StartsWith(excludeDir, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string targetFile = Path.Combine(destination, relativePath);
                Console.WriteLine($"  Copying: {relativePath}");
                File.Copy(file, targetFile, overwrite: true);
            }
        }
    }
}
