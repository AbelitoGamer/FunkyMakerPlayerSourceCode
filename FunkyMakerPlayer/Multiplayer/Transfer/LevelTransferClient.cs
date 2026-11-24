using System;
using System.IO;
using System.Net;
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
    /// to download it (<see cref="TransferDownloadPacket"/>).
    /// Supports resilient chunked uploads (<c>PUT /api/transfers/{id}/chunks?offset=N</c> +
    /// <c>POST /api/transfers/{id}/complete</c>) with per-chunk retries so large uploads never hit
    /// reverse-proxy ~100 s request timeouts (Render/Cloudflare), with automatic fallback to single-PUT
    /// (<c>PUT /api/transfers/{id}/file</c>) against older servers.
    /// </summary>
    public static class LevelTransferClient
    {
        private static readonly HttpClient Http = new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            KeepAlivePingDelay = TimeSpan.FromSeconds(15),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            MaxConnectionsPerServer = 8
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        private const int BufferSize = 64 * 1024;
        private const int DefaultChunkBytes = 4 * 1024 * 1024;
        private const int MaxChunkRetries = 4;
        private const int MaxDownloadRetries = 3;

        private static string ServerUrl => NetworkManager.Instance.ServerUrl;

        /// <summary>Streams a local level file to the server (chunked with retries when supported, else single PUT).</summary>
        public static async Task UploadAsync(TransferUploadPacket t, string path, IProgress<long>? progress, CancellationToken ct)
        {
            bool packedVariant = t.Kind == Protocol.KindOsu || t.Variant == Protocol.VariantChartOnly;
            long fileLength = new FileInfo(path).Length;
            if (packedVariant ? (t.MaxSize > 0 && fileLength > t.MaxSize) : (fileLength != t.Size))
                throw new IOException(packedVariant ? "The packed level is larger than the server allows." : "The level file changed since it was listed.");

            if (NetworkManager.Instance.SupportsChunkedUpload)
            {
                bool fellBack = await TryUploadChunkedAsync(t, path, fileLength, progress, ct);
                if (!fellBack)
                    return;
            }

            await UploadSinglePutAsync(t, path, progress, ct);
        }

        /// <summary>
        /// Uploads the file in small chunks (default 4 MB) with automatic per-chunk retries.
        /// Returns true ONLY if the server does not implement chunked endpoints (404/405 on chunk 0),
        /// signalling the caller to fall back to the legacy single PUT endpoint.
        /// </summary>
        private static async Task<bool> TryUploadChunkedAsync(
            TransferUploadPacket t, string path, long fileLength, IProgress<long>? progress, CancellationToken ct)
        {
            int chunkSize = NetworkManager.Instance.UploadChunkBytes;
            if (chunkSize < 256 * 1024 || chunkSize > 16 * 1024 * 1024)
                chunkSize = DefaultChunkBytes;

            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
            byte[] chunkBuf = new byte[Math.Min(chunkSize, Math.Max(1, fileLength))];
            long offset = 0;

            while (offset < fileLength)
            {
                ct.ThrowIfCancellationRequested();
                int toSend = (int)Math.Min(chunkBuf.Length, fileLength - offset);
                file.Seek(offset, SeekOrigin.Begin);
                int readTotal = 0;
                while (readTotal < toSend)
                {
                    int r = await file.ReadAsync(chunkBuf.AsMemory(readTotal, toSend - readTotal), ct);
                    if (r <= 0) throw new IOException("The level file changed while uploading.");
                    readTotal += r;
                }

                Exception? lastError = null;
                bool chunkOk = false;
                for (int attempt = 0; attempt < MaxChunkRetries; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        using var chunkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        chunkCts.CancelAfter(TimeSpan.FromSeconds(85)); // stay well under Render/Cloudflare 100 s edge timeout

                        long baseOffset = offset;
                        using var request = new HttpRequestMessage(HttpMethod.Put, $"{ServerUrl}/api/transfers/{t.TransferId}/chunks?offset={baseOffset}");
                        request.Headers.Add(Protocol.TokenHeader, t.Token);
                        request.Content = new ChunkProgressContent(chunkBuf, readTotal, BufferSize, sent => progress?.Report(baseOffset + sent));
                        request.Content.Headers.ContentLength = readTotal;
                        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                        using var response = await Http.SendAsync(request, chunkCts.Token);
                        if (response.IsSuccessStatusCode)
                        {
                            chunkOk = true;
                            break;
                        }

                        int code = (int)response.StatusCode;
                        if (baseOffset == 0 && (code == 404 || code == 405))
                        {
                            Console.WriteLine("[Transfer] Server does not support chunked upload; falling back to single PUT.");
                            return true;
                        }

                        // Non-retryable client rejection (bad file signature, transfer cancelled/conflict, oversize)
                        if (code == 400 || code == 403 || code == 404 || code == 409 || code == 413)
                        {
                            // Retry once on 409 only if a prior aborted chunk is still unwinding its lock on the server
                            if (code == 409 && attempt == 0 && baseOffset > 0)
                            {
                                lastError = new IOException($"Chunk busy ({code}).");
                            }
                            else
                            {
                                throw new IOException($"Upload refused ({code}).");
                            }
                        }
                        else
                        {
                            lastError = new IOException($"Chunk upload failed ({code}).");
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (IOException ex) when (ex.Message.StartsWith("Upload refused (", StringComparison.Ordinal))
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                    }

                    int delayMs = Math.Min(6000, 1000 * (1 << attempt));
                    Console.WriteLine($"[Transfer] Chunk at offset {offset} attempt {attempt + 1}/{MaxChunkRetries} failed ({lastError?.Message}); retrying in {delayMs}ms...");
                    await Task.Delay(delayMs, ct);
                }

                if (!chunkOk)
                    throw new IOException($"Chunk upload failed after retries: {lastError?.Message}");

                offset += readTotal;
                progress?.Report(offset);
            }

            // Finalize the assembled file on the server (retry transient proxy errors).
            Exception? completeError = null;
            for (int attempt = 0; attempt < MaxChunkRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var doneCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    doneCts.CancelAfter(TimeSpan.FromSeconds(85));

                    using var request = new HttpRequestMessage(HttpMethod.Post, $"{ServerUrl}/api/transfers/{t.TransferId}/complete");
                    request.Headers.Add(Protocol.TokenHeader, t.Token);
                    using var response = await Http.SendAsync(request, doneCts.Token);
                    if (response.IsSuccessStatusCode)
                        return false;

                    int code = (int)response.StatusCode;
                    if (code == 400 || code == 403 || code == 404)
                        throw new IOException($"Upload finalize refused ({code}).");
                    completeError = new IOException($"Upload finalize failed ({code}).");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (IOException ex) when (ex.Message.StartsWith("Upload finalize refused (", StringComparison.Ordinal))
                {
                    throw;
                }
                catch (Exception ex)
                {
                    completeError = ex;
                }

                int delayMs = Math.Min(6000, 1000 * (1 << attempt));
                await Task.Delay(delayMs, ct);
            }

            throw new IOException($"Upload finalize failed: {completeError?.Message}");
        }

        private static async Task UploadSinglePutAsync(TransferUploadPacket t, string path, IProgress<long>? progress, CancellationToken ct)
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
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
            Exception? lastError = null;
            for (int attempt = 0; attempt < MaxDownloadRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (File.Exists(destination))
                        File.Delete(destination);

                    using var request = new HttpRequestMessage(HttpMethod.Get, $"{ServerUrl}/api/transfers/{t.TransferId}/file");
                    request.Headers.Add(Protocol.TokenHeader, t.Token);
                    using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (!response.IsSuccessStatusCode)
                    {
                        int code = (int)response.StatusCode;
                        if (code == 404 || code == 409 || code == 410)
                            throw new IOException($"Download refused ({code}).");
                        throw new HttpRequestException($"Download failed ({code}).");
                    }

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
                        throw new HttpRequestException("The download ended early.");
                    return Convert.ToHexStringLower(sha.GetHashAndReset());
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (IOException ex) when (ex.Message.StartsWith("Download refused (", StringComparison.Ordinal) ||
                                             ex.Message.Contains("more data than the level size", StringComparison.Ordinal))
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    if (attempt + 1 >= MaxDownloadRetries)
                        break;
                    int delayMs = 1500 * (attempt + 1);
                    Console.WriteLine($"[Transfer] Download attempt {attempt + 1}/{MaxDownloadRetries} failed ({ex.Message}); retrying in {delayMs}ms...");
                    await Task.Delay(delayMs, ct);
                }
            }

            throw lastError ?? new IOException("The download failed.");
        }

        private sealed class ChunkProgressContent(byte[] buffer, int count, int sliceSize, Action<long>? onBytes) : HttpContent
        {
            protected override Task SerializeToStreamAsync(Stream target, TransportContext? context) =>
                SerializeToStreamAsync(target, context, CancellationToken.None);

            protected override async Task SerializeToStreamAsync(Stream target, TransportContext? context, CancellationToken cancellationToken)
            {
                int sent = 0;
                int lastReport = 0;
                while (sent < count)
                {
                    int n = Math.Min(sliceSize, count - sent);
                    await target.WriteAsync(buffer.AsMemory(sent, n), cancellationToken);
                    sent += n;
                    if (sent - lastReport >= 256 * 1024 || sent == count)
                    {
                        lastReport = sent;
                        onBytes?.Invoke(sent);
                    }
                }
            }

            protected override bool TryComputeLength(out long length)
            {
                length = count;
                return true;
            }
        }

        /// <summary>Request body stream that reports how many bytes were sent.</summary>
        private sealed class ProgressStreamContent(Stream stream, int bufferSize, IProgress<long>? progress) : HttpContent
        {
            protected override Task SerializeToStreamAsync(Stream target, TransportContext? context) =>
                SerializeToStreamAsync(target, context, CancellationToken.None);

            protected override async Task SerializeToStreamAsync(Stream target, TransportContext? context, CancellationToken cancellationToken)
            {
                var buffer = new byte[bufferSize];
                long total = 0, lastReport = 0;
                int n;
                while ((n = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, n), cancellationToken);
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
