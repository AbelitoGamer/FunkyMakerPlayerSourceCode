using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FunkyMakerPlayer.Multiplayer.Rooms;

namespace FunkyMakerPlayer.Multiplayer.Transfer
{
    /// <summary>
    /// HTTP side of room level transfers and online replays (fork). The online server tells a holder
    /// to upload the picked level once (<see cref="TransferUploadPacket"/>) and everybody who lacks it
    /// to download it (<see cref="TransferDownloadPacket"/>); the bytes go through
    /// <c>/api/transfers/{id}/file</c> with the token from the packet. Coordination lives in <see cref="RoomClient"/>.
    /// </summary>
    public static class LevelTransferClient
    {
        private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
        private const int BufferSize = 64 * 1024;

        private static string ServerUrl => NetworkManager.Instance.ServerUrl;

        /// <summary>Streams a local level file to the server.</summary>
        public static async Task UploadAsync(TransferUploadPacket t, string path, IProgress<long>? progress, CancellationToken ct)
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
            // FNF level files must match the listed size; packed osu! sets just have to fit.
            if (t.Kind == Protocol.KindOsu ? t.MaxSize > 0 && file.Length > t.MaxSize : file.Length != t.Size)
                throw new IOException(t.Kind == Protocol.KindOsu ? "The packed set is larger than the server allows." : "The level file changed since it was listed.");

            using var request = new HttpRequestMessage(HttpMethod.Put, $"{ServerUrl}/api/transfers/{t.TransferId}/file");
            request.Headers.Add(Protocol.TokenHeader, t.Token);
            request.Content = new ProgressStreamContent(file, BufferSize, progress);
            request.Content.Headers.ContentLength = file.Length;
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new IOException($"Upload refused ({(int)response.StatusCode}).");
        }

        /// <summary>Downloads the level into <paramref name="destination"/> and returns its SHA-256 (lower-case hex).</summary>
        public static async Task<string> DownloadAsync(TransferDownloadPacket t, string destination, IProgress<long>? progress, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ServerUrl}/api/transfers/{t.TransferId}/file");
            request.Headers.Add(Protocol.TokenHeader, t.Token);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new IOException($"Download refused ({(int)response.StatusCode}).");

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
            var buffer = new byte[BufferSize];
            long total = 0;
            long lastReport = 0;
            int n;
            while ((n = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                total += n;
                if (total > t.Size)
                    throw new IOException("The server sent more data than the level size.");
                sha.AppendData(buffer, 0, n);
                await target.WriteAsync(buffer.AsMemory(0, n), ct);
                if (total - lastReport >= 256 * 1024 || total == t.Size)
                {
                    lastReport = total;
                    progress?.Report(total);
                }
            }
            if (total != t.Size)
                throw new IOException("The download ended early.");
            return Convert.ToHexStringLower(sha.GetHashAndReset());
        }

        /// <summary>Request body stream that reports how many bytes were sent.</summary>
        private sealed class ProgressStreamContent(Stream stream, int bufferSize, IProgress<long>? progress) : HttpContent
        {
            protected override async Task SerializeToStreamAsync(Stream target, System.Net.TransportContext? context)
            {
                var buffer = new byte[bufferSize];
                long total = 0, lastReport = 0;
                int n;
                while ((n = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, n));
                    total += n;
                    if (total - lastReport >= 256 * 1024)
                    {
                        lastReport = total;
                        progress?.Report(total);
                    }
                }
                progress?.Report(total);
            }

            protected override bool TryComputeLength(out long length)
            {
                length = stream.Length;
                return true;
            }
        }

        // ── Online replays ───────────────────────────────────────────────────

        private static string ReplayQuery(string player, string opponent, string levelHash) =>
            $"player={Uri.EscapeDataString(player)}&opponent={Uri.EscapeDataString(opponent)}&level={Uri.EscapeDataString(levelHash)}";

        /// <summary>Uploads this player's replay track for a finished match. False on failure.</summary>
        public static async Task<bool> UploadReplayTrackAsync(string player, string opponent, string levelHash, byte[] gzipTrack)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(100)); // a sleeping server can take a while
                using var content = new ByteArrayContent(gzipTrack);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
                using var resp = await Http.PutAsync($"{ServerUrl}/api/replays?{ReplayQuery(player, opponent, levelHash)}", content, cts.Token);
                return resp.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Replay] Upload failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>The opponent's replay track for a match, or null if it isn't there (yet).</summary>
        public static async Task<byte[]?> DownloadReplayTrackAsync(string player, string opponent, string levelHash)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(100));
                using var resp = await Http.GetAsync($"{ServerUrl}/api/replays?{ReplayQuery(player, opponent, levelHash)}", cts.Token);
                return resp.IsSuccessStatusCode ? await resp.Content.ReadAsByteArrayAsync(cts.Token) : null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Replay] Download failed: {ex.Message}");
                return null;
            }
        }
    }
}
