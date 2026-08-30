using System;

namespace FunkyMakerPlayer;

/// <summary>
/// Handles time conversion for FNMM files.
/// 
/// Format history:
///   • Version < 2.0: Times stored in BEATS, need conversion to seconds using BPM
///   • Version >= 2.0: Times stored in SECONDS, no conversion needed
/// 
/// Special case: If BPM <= 0, times are always treated as seconds regardless of version.
/// </summary>
public static class FnmmTimeConverter
{
    /// <summary>
    /// Determines if the times in the data are already in seconds.
    /// </summary>
    /// <param name="version">File format version</param>
    /// <param name="bpm">Beats per minute (if <= 0, times are in seconds)</param>
    /// <returns>True if times are in seconds, false if in beats</returns>
    public static bool AreTimesInSeconds(float version, float bpm)
    {
        // Version 2.0+ uses seconds
        if (version >= 2.0f)
            return true;

        // If no BPM specified, assume seconds
        if (bpm <= 0f)
            return true;

        // Old format with BPM uses beats
        return false;
    }

    /// <summary>
    /// Converts a time value to seconds.
    /// If the time is already in seconds, returns it unchanged.
    /// If the time is in beats, converts using BPM.
    /// </summary>
    /// <param name="noteTime">Time value (in beats or seconds)</param>
    /// <param name="version">File format version</param>
    /// <param name="bpm">Beats per minute</param>
    /// <returns>Time in seconds</returns>
    public static float ToSeconds(float noteTime, float version, float bpm)
    {
        if (AreTimesInSeconds(version, bpm))
            return noteTime;

        // Convert from beats to seconds: (beats / BPM) * 60
        return noteTime / bpm * 60f;
    }

    /// <summary>
    /// Gets the effective BPM for scroll speed calculations.
    /// Returns a default of 120 BPM if not specified.
    /// </summary>
    /// <param name="bpm">Specified BPM</param>
    /// <returns>BPM to use for calculations</returns>
    public static float GetEffectiveBpm(float bpm)
    {
        return bpm > 0f ? bpm : 120f;
    }
}
