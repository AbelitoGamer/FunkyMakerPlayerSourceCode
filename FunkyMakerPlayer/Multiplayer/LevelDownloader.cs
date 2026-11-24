using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace FunkyMakerPlayer.Multiplayer
{
    /// <summary>
    /// Handles downloading official levels from the server.
    /// Uses large buffered reads, exact array pre-allocation when Content-Length is known,
    /// and a SHA-256 verified local cache to avoid repeated multi-megabyte transfers.
    /// </summary>
    public class LevelDownloader
    {
        private const int DownloadChunkSize = 131072; // 128 KB chunks

        private static readonly HttpClient _httpClient = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            KeepAlivePingDelay = TimeSpan.FromSeconds(15),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            MaxConnectionsPerServer = 8
        })
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };

        public event Action<int>? OnDownloadProgress; // percentage 0-100
        public event Action<int, long, long>? OnDownloadBytesProgress; // percentage, downloaded, total
        public event Action<string, OfficialLevelInfo>? OnDownloadComplete; // levelId, info
        public event Action<string>? OnDownloadError; // error message

        /// <summary>
        /// Download a level from the server (or restore immediately from verified local cache).
        /// </summary>
        public async Task DownloadLevel(OfficialLevelInfo levelInfo, string serverUrl)
        {
            try
            {
                if (LevelCache.Instance.TryRestoreFromDisk(levelInfo))
                {
                    long cachedBytes = LevelCache.Instance.GetCachedFileSize(levelInfo.Id);
                    if (cachedBytes <= 0) cachedBytes = levelInfo.FileSize;
                    OnDownloadBytesProgress?.Invoke(100, cachedBytes, cachedBytes);
                    OnDownloadProgress?.Invoke(100);
                    OnDownloadComplete?.Invoke(levelInfo.Id, levelInfo);
                    return;
                }

                string downloadUrl = $"{serverUrl.TrimEnd('/')}/api/verified_levels/{levelInfo.Id}/download";
                Console.WriteLine($"[LevelDownloader] Downloading from: {downloadUrl}");

                using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);

                if (!response.IsSuccessStatusCode)
                {
                    OnDownloadError?.Invoke($"Server error: {response.StatusCode}");
                    return;
                }

                long totalBytes = response.Content.Headers.ContentLength ?? levelInfo.FileSize;
                long downloadedBytes = 0;
                int lastReportedPercent = -1;

                void ReportProgress(bool force = false)
                {
                    if (totalBytes <= 0)
                        return;

                    int progress = (int)Math.Clamp((downloadedBytes * 100L) / totalBytes, 0L, 100L);
                    if (force || progress != lastReportedPercent)
                    {
                        lastReportedPercent = progress;
                        OnDownloadBytesProgress?.Invoke(progress, downloadedBytes, totalBytes);
                        OnDownloadProgress?.Invoke(progress);
                    }
                }

                byte[] levelData;
                using (var contentStream = await response.Content.ReadAsStreamAsync())
                {
                    if (totalBytes > 0 && totalBytes <= int.MaxValue)
                    {
                        int expectedLength = (int)totalBytes;
                        byte[] preallocated = new byte[expectedLength];
                        int offset = 0;

                        while (offset < expectedLength)
                        {
                            int toRead = Math.Min(DownloadChunkSize, expectedLength - offset);
                            int bytesRead = await contentStream.ReadAsync(preallocated.AsMemory(offset, toRead));
                            if (bytesRead <= 0)
                                break;

                            offset += bytesRead;
                            downloadedBytes += bytesRead;
                            ReportProgress();
                        }

                        // Check cleanly if server sent extra bytes beyond Content-Length
                        byte[] tailProbe = new byte[DownloadChunkSize];
                        int extraRead = await contentStream.ReadAsync(tailProbe.AsMemory(0, tailProbe.Length));
                        if (extraRead > 0)
                        {
                            using var fallbackStream = new MemoryStream(offset + extraRead);
                            fallbackStream.Write(preallocated, 0, offset);
                            fallbackStream.Write(tailProbe, 0, extraRead);
                            downloadedBytes += extraRead;

                            while ((extraRead = await contentStream.ReadAsync(tailProbe.AsMemory(0, tailProbe.Length))) > 0)
                            {
                                fallbackStream.Write(tailProbe, 0, extraRead);
                                downloadedBytes += extraRead;
                                ReportProgress();
                            }

                            levelData = fallbackStream.ToArray();
                        }
                        else if (offset == expectedLength)
                        {
                            levelData = preallocated;
                        }
                        else
                        {
                            Array.Resize(ref preallocated, offset);
                            levelData = preallocated;
                        }
                    }
                    else
                    {
                        using var memoryStream = new MemoryStream(DownloadChunkSize);
                        byte[] buffer = new byte[DownloadChunkSize];
                        int bytesRead;

                        while ((bytesRead = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
                        {
                            memoryStream.Write(buffer, 0, bytesRead);
                            downloadedBytes += bytesRead;
                            ReportProgress();
                        }

                        levelData = memoryStream.ToArray();
                    }
                }

                ReportProgress(force: true);
                LevelCache.Instance.CacheLevel(levelInfo.Id, levelData, levelInfo);

                Console.WriteLine($"[LevelDownloader] Downloaded and cached: {levelInfo.Name} ({levelData.Length} bytes)");
                OnDownloadComplete?.Invoke(levelInfo.Id, levelInfo);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LevelDownloader] Error: {ex.Message}");
                OnDownloadError?.Invoke(ex.Message);
            }
        }
    }
}
