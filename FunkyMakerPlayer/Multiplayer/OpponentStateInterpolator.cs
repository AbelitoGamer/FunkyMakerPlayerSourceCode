using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace FunkyMakerPlayer.Multiplayer
{
    /// <summary>
    /// Interpolates opponent state for smooth visual updates even with network jitter.
    /// Stores recent state snapshots and interpolates between them.
    /// </summary>
    public class OpponentStateInterpolator
    {
        /// <summary>
        /// A snapshot of opponent state at a specific time.
        /// </summary>
        private class StateSnapshot
        {
            public long Timestamp { get; set; }
            public int Score { get; set; }
            public int Combo { get; set; }
            public float Health { get; set; }
            public int SickCount { get; set; }
            public int GoodCount { get; set; }
            public int BadCount { get; set; }
            public int ShitCount { get; set; }
            public int MissCount { get; set; }
        }

        private List<StateSnapshot> _snapshots = new List<StateSnapshot>();
        private const int MAX_SNAPSHOTS = 30;
        private const int INTERPOLATION_DELAY_MS = 100; // 100ms behind for smooth interpolation

        // Current interpolated state
        public int Score { get; private set; }
        public int Combo { get; private set; }
        public float Health { get; private set; } = 0.5f;
        public int SickCount { get; private set; }
        public int GoodCount { get; private set; }
        public int BadCount { get; private set; }
        public int ShitCount { get; private set; }
        public int MissCount { get; private set; }

        // Hit animation tracking
        private class HitEvent
        {
            public int Lane { get; set; }
            public string Rating { get; set; } = "";
            public long Timestamp { get; set; }
            public float TimeRemaining { get; set; }
        }

        private List<HitEvent> _recentHits = new List<HitEvent>();
        private const float HIT_DISPLAY_DURATION = 0.15f; // Show hit animation for 150ms

        /// <summary>
        /// Get recent hit events for visual display (lane indicators).
        /// </summary>
        public List<(int lane, string rating, float alpha)> GetRecentHits()
        {
            var results = new List<(int, string, float)>();
            foreach (var hit in _recentHits)
            {
                float alpha = hit.TimeRemaining / HIT_DISPLAY_DURATION;
                results.Add((hit.Lane, hit.Rating, alpha));
            }
            return results;
        }

        /// <summary>
        /// Add a new state snapshot from the server.
        /// </summary>
        public void AddSnapshot(long timestamp, int score, int combo, float health,
            int sickCount, int goodCount, int badCount, int shitCount, int missCount)
        {
            var snapshot = new StateSnapshot
            {
                Timestamp = timestamp,
                Score = score,
                Combo = combo,
                Health = health,
                SickCount = sickCount,
                GoodCount = goodCount,
                BadCount = badCount,
                ShitCount = shitCount,
                MissCount = missCount
            };

            _snapshots.Add(snapshot);

            // Keep list sorted by timestamp
            _snapshots.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

            // Remove old snapshots
            if (_snapshots.Count > MAX_SNAPSHOTS)
                _snapshots.RemoveAt(0);
        }

        /// <summary>
        /// Add a hit event for visual display.
        /// </summary>
        public void AddHitEvent(int lane, string rating, long timestamp)
        {
            _recentHits.Add(new HitEvent
            {
                Lane = lane,
                Rating = rating,
                Timestamp = timestamp,
                TimeRemaining = HIT_DISPLAY_DURATION
            });
        }

        /// <summary>
        /// Update interpolation based on current server time.
        /// Call this every frame with the synchronized server time.
        /// </summary>
        public void Update(float deltaTime, long currentServerTime)
        {
            // Update hit event timers
            for (int i = _recentHits.Count - 1; i >= 0; i--)
            {
                _recentHits[i].TimeRemaining -= deltaTime;
                if (_recentHits[i].TimeRemaining <= 0)
                    _recentHits.RemoveAt(i);
            }

            if (_snapshots.Count < 2)
            {
                // Not enough data for interpolation, use latest if available
                if (_snapshots.Count == 1)
                {
                    var snapshot = _snapshots[0];
                    Score = snapshot.Score;
                    Combo = snapshot.Combo;
                    Health = snapshot.Health;
                    SickCount = snapshot.SickCount;
                    GoodCount = snapshot.GoodCount;
                    BadCount = snapshot.BadCount;
                    ShitCount = snapshot.ShitCount;
                    MissCount = snapshot.MissCount;
                }
                return;
            }

            // Interpolate to a point slightly in the past to ensure smooth animation
            long targetTime = currentServerTime - INTERPOLATION_DELAY_MS;

            // Find the two snapshots to interpolate between
            StateSnapshot? before = null;
            StateSnapshot? after = null;

            for (int i = 0; i < _snapshots.Count - 1; i++)
            {
                if (_snapshots[i].Timestamp <= targetTime && _snapshots[i + 1].Timestamp >= targetTime)
                {
                    before = _snapshots[i];
                    after = _snapshots[i + 1];
                    break;
                }
            }

            if (before == null || after == null)
            {
                // Target time is outside our snapshot range, use closest available
                if (targetTime < _snapshots[0].Timestamp)
                {
                    // Too far in the past, use oldest snapshot
                    var snapshot = _snapshots[0];
                    Score = snapshot.Score;
                    Combo = snapshot.Combo;
                    Health = snapshot.Health;
                    SickCount = snapshot.SickCount;
                    GoodCount = snapshot.GoodCount;
                    BadCount = snapshot.BadCount;
                    ShitCount = snapshot.ShitCount;
                    MissCount = snapshot.MissCount;
                }
                else
                {
                    // Too far in the future, use newest snapshot
                    var snapshot = _snapshots[_snapshots.Count - 1];
                    Score = snapshot.Score;
                    Combo = snapshot.Combo;
                    Health = snapshot.Health;
                    SickCount = snapshot.SickCount;
                    GoodCount = snapshot.GoodCount;
                    BadCount = snapshot.BadCount;
                    ShitCount = snapshot.ShitCount;
                    MissCount = snapshot.MissCount;
                }
                return;
            }

            // Calculate interpolation factor
            long timeDiff = after.Timestamp - before.Timestamp;
            float t = timeDiff > 0 ? (float)(targetTime - before.Timestamp) / timeDiff : 0f;
            t = Math.Clamp(t, 0f, 1f);

            // Interpolate continuous values (health)
            Health = MathHelper.Lerp(before.Health, after.Health, t);

            // For discrete values, use step function (jump to new value halfway through)
            if (t < 0.5f)
            {
                Score = before.Score;
                Combo = before.Combo;
                SickCount = before.SickCount;
                GoodCount = before.GoodCount;
                BadCount = before.BadCount;
                ShitCount = before.ShitCount;
                MissCount = before.MissCount;
            }
            else
            {
                Score = after.Score;
                Combo = after.Combo;
                SickCount = after.SickCount;
                GoodCount = after.GoodCount;
                BadCount = after.BadCount;
                ShitCount = after.ShitCount;
                MissCount = after.MissCount;
            }
        }

        /// <summary>
        /// Reset interpolator state.
        /// </summary>
        public void Reset()
        {
            _snapshots.Clear();
            _recentHits.Clear();
            Score = 0;
            Combo = 0;
            Health = 0.5f;
            SickCount = 0;
            GoodCount = 0;
            BadCount = 0;
            ShitCount = 0;
            MissCount = 0;
        }

        /// <summary>
        /// Get diagnostic information.
        /// </summary>
        public string GetDiagnostics()
        {
            return $"Snapshots: {_snapshots.Count} | Hits: {_recentHits.Count} | Health: {Health:F2}";
        }
    }
}
