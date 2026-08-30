using System;
using System.Collections.Generic;

namespace FunkyMakerPlayer.Multiplayer
{
    /// <summary>
    /// Manages time synchronization between client and server for precise multiplayer gameplay.
    /// Uses clock offset calculation and lag compensation to ensure synchronized timing.
    /// </summary>
    public class TimeSyncManager
    {
        private static TimeSyncManager? _instance;
        public static TimeSyncManager Instance => _instance ??= new TimeSyncManager();

        // Clock offset (server time - client time)
        private long _clockOffset = 0;
        private List<long> _offsetSamples = new List<long>();
        private const int MAX_SAMPLES = 10;
        private bool _isSynced = false;

        // Lag compensation
        private int _estimatedLag = 0; // One-way latency estimate
        private const float LAG_SMOOTHING = 0.8f; // For exponential smoothing

        /// <summary>
        /// True if we have enough samples to trust the clock offset.
        /// </summary>
        public bool IsSynced => _isSynced;

        /// <summary>
        /// Get the current synchronized server time in milliseconds.
        /// </summary>
        public long GetServerTime()
        {
            long clientTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return clientTime + _clockOffset;
        }

        /// <summary>
        /// Convert a server timestamp to client time (for scheduling future events).
        /// </summary>
        public long ServerToClientTime(long serverTime)
        {
            return serverTime - _clockOffset;
        }

        /// <summary>
        /// Get the estimated one-way latency in milliseconds.
        /// </summary>
        public int GetEstimatedLag()
        {
            return _estimatedLag;
        }

        /// <summary>
        /// Update clock offset using a ping-pong measurement.
        /// Call this when receiving a pong packet.
        /// </summary>
        /// <param name="clientSendTime">When client sent ping</param>
        /// <param name="serverReceiveTime">When server received ping (server timestamp)</param>
        /// <param name="serverSendTime">When server sent pong (server timestamp)</param>
        /// <param name="clientReceiveTime">When client received pong</param>
        public void UpdateClockOffset(long clientSendTime, long serverReceiveTime, long serverSendTime, long clientReceiveTime)
        {
            // Calculate round-trip time (RTT)
            long rtt = clientReceiveTime - clientSendTime;

            // Estimate one-way latency (half of RTT)
            int oneWayLatency = (int)(rtt / 2);

            // Smooth the lag estimate
            if (_estimatedLag == 0)
                _estimatedLag = oneWayLatency;
            else
                _estimatedLag = (int)(_estimatedLag * LAG_SMOOTHING + oneWayLatency * (1 - LAG_SMOOTHING));

            // Calculate clock offset
            // Server time when pong was sent + estimated time to reach client = expected client time
            long expectedClientTime = serverSendTime + oneWayLatency;
            long offset = expectedClientTime - clientReceiveTime;

            // Add to samples
            _offsetSamples.Add(offset);
            if (_offsetSamples.Count > MAX_SAMPLES)
                _offsetSamples.RemoveAt(0);

            // Calculate median offset (more robust than average)
            _clockOffset = CalculateMedian(_offsetSamples);
            _isSynced = _offsetSamples.Count >= 3; // Need at least 3 samples

            Console.WriteLine($"[TimeSync] RTT: {rtt}ms, OneWay: {oneWayLatency}ms, Offset: {offset}ms, Median: {_clockOffset}ms");
        }

        /// <summary>
        /// Simpler clock offset update using only ping timestamp and current ping.
        /// Used when server doesn't send detailed timing info.
        /// </summary>
        public void UpdateClockOffsetSimple(long pingTimestamp, int currentPing)
        {
            // Estimate one-way latency from ping
            int oneWayLatency = currentPing / 2;

            // Smooth the lag estimate
            if (_estimatedLag == 0)
                _estimatedLag = oneWayLatency;
            else
                _estimatedLag = (int)(_estimatedLag * LAG_SMOOTHING + oneWayLatency * (1 - LAG_SMOOTHING));

            // Simple offset: assume symmetric latency
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long rtt = now - pingTimestamp;
            long offset = oneWayLatency; // Rough estimate

            _offsetSamples.Add(offset);
            if (_offsetSamples.Count > MAX_SAMPLES)
                _offsetSamples.RemoveAt(0);

            _clockOffset = CalculateMedian(_offsetSamples);
            _isSynced = _offsetSamples.Count >= 3;

            Console.WriteLine($"[TimeSync] Simple sync - Ping: {currentPing}ms, Offset: {_clockOffset}ms");
        }

        /// <summary>
        /// Adjust a server timestamp to account for client-side lag compensation.
        /// This makes remote events appear closer to when they "really" happened.
        /// </summary>
        public long CompensateLag(long serverTimestamp)
        {
            // Subtract the estimated lag so events appear to happen "in the past"
            return serverTimestamp - _estimatedLag;
        }

        /// <summary>
        /// Calculate time until a server event happens (in seconds).
        /// Positive = future, negative = past.
        /// </summary>
        public float TimeUntilServerEvent(long serverTimestamp)
        {
            long now = GetServerTime();
            long diff = serverTimestamp - now;
            return diff / 1000f;
        }

        /// <summary>
        /// Reset synchronization state (e.g., when disconnecting).
        /// </summary>
        public void Reset()
        {
            _clockOffset = 0;
            _offsetSamples.Clear();
            _estimatedLag = 0;
            _isSynced = false;
            Console.WriteLine("[TimeSync] Reset");
        }

        /// <summary>
        /// Calculate median of a list (more robust than average for outliers).
        /// </summary>
        private long CalculateMedian(List<long> values)
        {
            if (values.Count == 0)
                return 0;

            var sorted = new List<long>(values);
            sorted.Sort();

            int mid = sorted.Count / 2;
            if (sorted.Count % 2 == 0)
                return (sorted[mid - 1] + sorted[mid]) / 2;
            else
                return sorted[mid];
        }

        /// <summary>
        /// Get diagnostic information about synchronization state.
        /// </summary>
        public string GetDiagnostics()
        {
            if (!_isSynced)
                return "Not synchronized (collecting samples...)";

            return $"Synced | Offset: {_clockOffset}ms | Lag: {_estimatedLag}ms | Samples: {_offsetSamples.Count}";
        }
    }
}
