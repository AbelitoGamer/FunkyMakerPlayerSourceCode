using System;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Xna.Framework.Audio;

namespace FunkyMakerPlayer;

/// <summary>
/// Where a playing <see cref="SoundEffectInstance"/> really is, and moving it in place (fork).
/// MonoGame has no public API for either, but on DesktopGL and Android every instance is an OpenAL
/// source, so this reads / sets its <c>AL_SAMPLE_OFFSET</c> through MonoGame's internal
/// <c>MonoGame.OpenAL.AL</c> (the same library and context MonoGame uses). The calls are bound once
/// with compiled expressions (no per-frame allocation). If MonoGame's internals ever change, binding
/// fails, <see cref="Available"/> is false and callers simply skip the sync.
/// </summary>
internal static class MusicPosition
{
    private const int AL_SAMPLE_OFFSET = 0x1025;

    private static readonly Func<SoundEffectInstance, int>? _sourceId; // -1 when the instance has no source
    private static readonly Func<int, int>? _getSampleOffset;
    private static readonly Action<int, int, int>? _setSourcei;
    private static readonly Func<int>? _getError;

    public static bool Available => _sourceId != null && _getSampleOffset != null;

    static MusicPosition()
    {
        try
        {
            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            var asm = typeof(SoundEffectInstance).Assembly;
            var al = asm.GetType("MonoGame.OpenAL.AL");
            var getSourceiEnum = asm.GetType("MonoGame.OpenAL.ALGetSourcei");
            if (al == null || getSourceiEnum == null) throw new MissingMemberException("MonoGame.OpenAL.AL");

            // instance => instance.HasSourceId ? instance.SourceId : -1
            var idField = typeof(SoundEffectInstance).GetField("SourceId", any);
            var hasField = typeof(SoundEffectInstance).GetField("HasSourceId", any);
            if (idField == null || hasField == null) throw new MissingMemberException("SoundEffectInstance.SourceId");
            var inst = Expression.Parameter(typeof(SoundEffectInstance), "inst");
            _sourceId = Expression.Lambda<Func<SoundEffectInstance, int>>(
                Expression.Condition(Expression.Field(inst, hasField), Expression.Field(inst, idField), Expression.Constant(-1)),
                inst).Compile();

            // id => { int v; AL.GetSource(id, ALGetSourcei.SampleOffset, out v); return v; }
            var getSource = al.GetMethod("GetSource", any, null,
                new[] { typeof(int), getSourceiEnum, typeof(int).MakeByRefType() }, null);
            if (getSource == null) throw new MissingMemberException("AL.GetSource");
            var id = Expression.Parameter(typeof(int), "id");
            var v = Expression.Variable(typeof(int), "v");
            _getSampleOffset = Expression.Lambda<Func<int, int>>(
                Expression.Block(new[] { v },
                    Expression.Assign(v, Expression.Constant(-1)),
                    Expression.Call(getSource, id, Expression.Constant(Enum.ToObject(getSourceiEnum, AL_SAMPLE_OFFSET)), v),
                    v),
                id).Compile();

            var sourcei = al.GetMethod("alSourcei", any, null, new[] { typeof(int), typeof(int), typeof(int) }, null);
            if (sourcei != null)
                _setSourcei = (Action<int, int, int>)Delegate.CreateDelegate(typeof(Action<int, int, int>), sourcei);

            var getError = al.GetMethod("GetError", any, null, Type.EmptyTypes, null);
            if (getError != null)
                _getError = Expression.Lambda<Func<int>>(Expression.Convert(Expression.Call(getError), typeof(int))).Compile();
        }
        catch (Exception ex)
        {
            _sourceId = null;
            _getSampleOffset = null;
            _setSourcei = null;
            Console.WriteLine($"[MusicPosition] Audio position unavailable, music sync disabled: {ex.Message}");
        }
    }

    /// <summary>Playback position in seconds from the start of the instance's buffer, or null.</summary>
    public static double? Get(SoundEffectInstance? inst, int sampleRate)
    {
        if (inst == null || sampleRate <= 0 || !Available) return null;
        try
        {
            if (inst.IsDisposed) return null;
            int src = _sourceId!(inst);
            if (src <= 0) return null;
            int offset = _getSampleOffset!(src);
            ClearError();
            return offset < 0 ? null : offset / (double)sampleRate;
        }
        catch { return null; }
    }

    /// <summary>
    /// Moves the instance to <paramref name="seconds"/> into its buffer without recreating it (no PCM
    /// copy, no restart latency). False when it can't (no source / unsupported / out of range).
    /// </summary>
    public static bool TrySet(SoundEffectInstance? inst, int sampleRate, double seconds, double bufferSeconds)
    {
        if (inst == null || _setSourcei == null || !Available || sampleRate <= 0) return false;
        if (seconds < 0 || seconds >= bufferSeconds - 0.05) return false;
        try
        {
            if (inst.IsDisposed) return false;
            int src = _sourceId!(inst);
            if (src <= 0) return false;
            ClearError();
            _setSourcei(src, AL_SAMPLE_OFFSET, (int)(seconds * sampleRate));
            // An error left in the queue would make MonoGame's next checked AL call throw.
            return ClearError() == 0;
        }
        catch { return false; }
    }

    private static int ClearError()
    {
        try { return _getError?.Invoke() ?? 0; }
        catch { return 0; }
    }
}
