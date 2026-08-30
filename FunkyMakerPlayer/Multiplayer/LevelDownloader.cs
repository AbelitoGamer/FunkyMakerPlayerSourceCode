using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace FunkyMakerPlayer.Multiplayer
{
    /// <summary>
    /// Handles downloading official levels from the server.
    /// Levels are cached in memory, not saved to disk permanently.
    /// </summary>
    public class LevelDownloader
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        
        public event Action<int>? OnDownloadProgress; // percentage 0-100
        public event Action<string, OfficialLevelInfo>? OnDownloadComplete; // levelId, info
        public event Action<string>? OnDownloadError; // error message

        /// <summary>
        /// Download a level from the server and cache in memory.
        /// </summary>
        /// <param name="levelInfo">Level information from server</param>
        /// <param name="serverUrl">Base server URL</param>
        public async Task DownloadLevel(OfficialLevelInfo levelInfo, string serverUrl)
        {
            try
            {
                // Build download URL
                string downloadUrl = $"{serverUrl}/api/verified_levels/{levelInfo.Id}/download";
                Console.WriteLine($"[LevelDownloader] Downloading from: {downloadUrl}");

                // Download file
                var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
                
                if (!response.IsSuccessStatusCode)
                {
                    OnDownloadError?.Invoke($"Server error: {response.StatusCode}");
                    return;
                }

                // Download with progress
                var totalBytes = response.Content.Headers.ContentLength ?? levelInfo.FileSize;
                var downloadedBytes = 0L;
                
                using (var memoryStream = new MemoryStream())
                {
                    using (var contentStream = await response.Content.ReadAsStreamAsync())
                    {
                        var buffer = new byte[8192];
                        int bytesRead;

                        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await memoryStream.WriteAsync(buffer, 0, bytesRead);
                            downloadedBytes += bytesRead;

                            // Report progress
                            if (totalBytes > 0)
                            {
                                int progress = (int)((downloadedBytes * 100) / totalBytes);
                                OnDownloadProgress?.Invoke(progress);
                            }
                        }
                    }
                    
                    // Cache in memory
                    byte[] levelData = memoryStream.ToArray();
                    LevelCache.Instance.CacheLevel(levelInfo.Id, levelData, levelInfo);
                    
                    Console.WriteLine($"[LevelDownloader] Downloaded and cached: {levelInfo.Name} ({levelData.Length} bytes)");
                    OnDownloadComplete?.Invoke(levelInfo.Id, levelInfo);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LevelDownloader] Error: {ex.Message}");
                OnDownloadError?.Invoke(ex.Message);
            }
        }
    }
}
