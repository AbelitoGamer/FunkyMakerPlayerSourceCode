#if ANDROID
using System;
using Android.App;
using Android.Content;

namespace FunkyMakerPlayer.Platforms.Android;

/// <summary>
/// Queries the real physical and available RAM of the Android device via <see cref="ActivityManager"/>
/// and computes safe, device-tailored memory budgets for GPU textures (<see cref="AtlasPacker"/> /
/// <see cref="FnmmSceneRenderer"/>) and streaming PCM audio (<see cref="OggLoader"/> / <see cref="AudioDecoder"/>).
///
/// - 32-bit processes stay capped tightly regardless of physical RAM because of the hard 32-bit
///   virtual address space limit.
/// - 64-bit devices scale smoothly from 2-3 GB low-RAM phones up to 8-16 GB flagships, and
///   clamp dynamically against live <c>AvailMem</c> so background apps cannot trigger an LMK kill.
/// </summary>
public static class AndroidMemoryBudget
{
    private const long MB = 1024L * 1024L;

    public readonly struct Snapshot
    {
        public long TotalRamBytes { get; init; }
        public long AvailRamBytes { get; init; }
        public bool IsLowRamDevice { get; init; }
        public bool IsLowMemoryNow { get; init; }
        public bool Is64Bit { get; init; }

        public long LevelVramBudgetBytes { get; init; }
        public long StripVramBudgetBytes { get; init; }
        public float MinAdaptiveScale { get; init; }
        public long GcCompactThresholdBytes { get; init; }
        public long AudioHalfRateThresholdBytes { get; init; }
        public long AudioMonoThresholdBytes { get; init; }
    }

    /// <summary>
    /// Reads live Android memory metrics and returns computed budgets for the current device state.
    /// </summary>
    public static Snapshot Query()
    {
        long totalBytes = 0;
        long availBytes = 0;
        bool isLowRam = false;
        bool isLowMemNow = false;

        try
        {
            var ctx = Application.Context;
            if (ctx?.GetSystemService(Context.ActivityService) is ActivityManager am)
            {
                var memInfo = new ActivityManager.MemoryInfo();
                am.GetMemoryInfo(memInfo);
                totalBytes = memInfo.TotalMem;
                availBytes = memInfo.AvailMem;
                isLowMemNow = memInfo.LowMemory;
                isLowRam = am.IsLowRamDevice;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AndroidMemory] ActivityManager query failed: {ex.Message}");
        }

        if (totalBytes <= 0)
        {
            long gcAvail = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            totalBytes = gcAvail > 0 ? gcAvail : (Environment.Is64BitProcess ? 4096L * MB : 1536L * MB);
        }

        bool is64Bit = Environment.Is64BitProcess;
        long totalMb = totalBytes / MB;

        long levelBudget;
        long stripBudget;
        float minScale;
        long gcThreshold;
        long audioHalf;
        long audioMono;

        if (!is64Bit)
        {
            // 32-bit process: virtual address space (~1.5-2 GB total) is the binding ceiling even on 4 GB phones.
            if (isLowRam || totalMb <= 1600)
            {
                levelBudget = 96L * MB;
                stripBudget = 8L * MB;
            }
            else if (totalMb <= 2500)
            {
                levelBudget = 112L * MB;
                stripBudget = 10L * MB;
            }
            else
            {
                levelBudget = 128L * MB;
                stripBudget = 12L * MB;
            }

            minScale = 0.15f;
            gcThreshold = 12L * MB;
            audioHalf = 24L * MB;
            audioMono = 64L * MB;

            if (availBytes > 0)
            {
                long availCap = Math.Max(64L * MB, availBytes / 4);
                if (levelBudget > availCap)
                    levelBudget = availCap;
            }
        }
        else
        {
            // 64-bit Android: note that a "4 GB" phone reports ~3600-3900 MB TotalMem after kernel/modem carveout.
            if (isLowRam || totalMb <= 2200)
            {
                levelBudget = 144L * MB;
                stripBudget = 16L * MB;
                minScale = 0.16f;
                gcThreshold = 16L * MB;
                audioHalf = 36L * MB;
                audioMono = 96L * MB;
            }
            else if (totalMb <= 3300) // ~3 GB device
            {
                levelBudget = 192L * MB;
                stripBudget = 20L * MB;
                minScale = 0.18f;
                gcThreshold = 20L * MB;
                audioHalf = 48L * MB;
                audioMono = 112L * MB;
            }
            else if (totalMb <= 4500) // ~4 GB device
            {
                levelBudget = 256L * MB;
                stripBudget = 24L * MB;
                minScale = 0.20f;
                gcThreshold = 24L * MB;
                audioHalf = 80L * MB;
                audioMono = 180L * MB;
            }
            else if (totalMb <= 6500) // ~6 GB device
            {
                levelBudget = 384L * MB;
                stripBudget = 36L * MB;
                minScale = 0.22f;
                gcThreshold = 36L * MB;
                audioHalf = 128L * MB;
                audioMono = 256L * MB;
            }
            else if (totalMb <= 9000) // ~8 GB device
            {
                levelBudget = 512L * MB;
                stripBudget = 48L * MB;
                minScale = 0.25f;
                gcThreshold = 48L * MB;
                audioHalf = 192L * MB;
                audioMono = 384L * MB;
            }
            else // 12+ GB flagship
            {
                levelBudget = 768L * MB;
                stripBudget = 64L * MB;
                minScale = 0.25f;
                gcThreshold = 64L * MB;
                audioHalf = 256L * MB;
                audioMono = 512L * MB;
            }

            // Clamp against currently available RAM so heavy background apps don't trigger Android LMK kills.
            if (availBytes > 0)
            {
                long availCap = Math.Max(96L * MB, availBytes / 3);
                if (levelBudget > availCap)
                    levelBudget = availCap;
            }
        }

        if (isLowMemNow)
        {
            levelBudget = Math.Max(64L * MB, (long)(levelBudget * 0.75));
            stripBudget = Math.Max(8L * MB, (long)(stripBudget * 0.75));
        }

        if (stripBudget > levelBudget / 4)
            stripBudget = Math.Max(8L * MB, levelBudget / 4);

        return new Snapshot
        {
            TotalRamBytes = totalBytes,
            AvailRamBytes = availBytes,
            IsLowRamDevice = isLowRam,
            IsLowMemoryNow = isLowMemNow,
            Is64Bit = is64Bit,
            LevelVramBudgetBytes = levelBudget,
            StripVramBudgetBytes = stripBudget,
            MinAdaptiveScale = minScale,
            GcCompactThresholdBytes = gcThreshold,
            AudioHalfRateThresholdBytes = audioHalf,
            AudioMonoThresholdBytes = audioMono,
        };
    }

    public static long GetLevelVramBudgetBytes() => Query().LevelVramBudgetBytes;
    public static long GetStripVramBudgetBytes() => Query().StripVramBudgetBytes;
    public static float GetMinAdaptiveScale() => Query().MinAdaptiveScale;
    public static long GetGcCompactThresholdBytes() => Query().GcCompactThresholdBytes;

    public static void LogSnapshot(in Snapshot s)
    {
        Console.WriteLine(
            $"[AndroidMemory] arch={(s.Is64Bit ? "64-bit" : "32-bit")}, " +
            $"totalRAM={s.TotalRamBytes / MB}MB, availRAM={s.AvailRamBytes / MB}MB, " +
            $"lowRam={s.IsLowRamDevice}, lowMemNow={s.IsLowMemoryNow} -> " +
            $"levelBudget={s.LevelVramBudgetBytes / MB}MB, stripBudget={s.StripVramBudgetBytes / MB}MB, " +
            $"minScale={s.MinAdaptiveScale:0.##}, audioHalf={s.AudioHalfRateThresholdBytes / MB}MB, audioMono={s.AudioMonoThresholdBytes / MB}MB");
    }
}
#endif
