using System;
using System.Text;

namespace FunkyMakerPlayer;

/// <summary>
/// Crash logging system that captures unhandled exceptions
/// On Android: Shows crash dialog with copy-to-clipboard button
/// On Desktop: Writes to crash.log file
/// </summary>
public static class CrashLogger
{
    private static readonly object _lock = new();
    private static readonly StringBuilder _crashLog = new StringBuilder();
    private static Action<string>? _onCrashCallback;
    #pragma warning disable CS0414 // Field assigned but never used
    private static bool _crashDialogShown = false;  // Prevenir múltiples diálogos
    #pragma warning restore CS0414
    
    /// <summary>
    /// Initialize crash logger and hook into unhandled exception handlers
    /// </summary>
    public static void Initialize(Action<string>? onCrashCallback = null)
    {
        lock (_lock)
        {
            _onCrashCallback = onCrashCallback;
        }
        
        // Hook unhandled exceptions (unsubscribe first so Activity recreation never duplicates handlers)
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        
        Console.WriteLine("[CrashLogger] Initialized");
    }
    
    /// <summary>
    /// Log an exception manually
    /// </summary>
    public static string LogException(Exception ex, string context = "")
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var crashReport = BuildCrashReport(ex, context, timestamp);
        
#if ANDROID
        Action<string>? cbToInvoke = null;
#endif
        lock (_lock)
        {
            _crashLog.AppendLine(crashReport);
#if ANDROID
            if (!_crashDialogShown)
            {
                _crashDialogShown = true;
                cbToInvoke = _onCrashCallback;
            }
#endif
        }
        
        Console.WriteLine($"[CrashLogger] Exception logged: {ex.Message}");
        
#if ANDROID
        if (cbToInvoke != null)
        {
            Console.WriteLine("[CrashLogger] Showing crash dialog (first crash only)");
            cbToInvoke(crashReport);
        }
        else
        {
            Console.WriteLine("[CrashLogger] Crash dialog already shown, skipping duplicate");
        }
#else
        // On Desktop, write to file
        WriteCrashToFile(crashReport);
#endif
        
        return crashReport;
    }
    
    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogException(ex, "Unhandled Exception");
        }
    }
    
    private static string BuildCrashReport(Exception ex, string context, string timestamp)
    {
        var sb = new StringBuilder();
        sb.AppendLine("═══════════════════════════════════════════════");
        sb.AppendLine($"CRASH REPORT - {timestamp}");
        sb.AppendLine("═══════════════════════════════════════════════");
        sb.AppendLine();
        
        if (!string.IsNullOrEmpty(context))
        {
            sb.AppendLine($"Context: {context}");
            sb.AppendLine();
        }
        
        sb.AppendLine($"Exception Type: {ex.GetType().Name}");
        sb.AppendLine($"Message: {ex.Message}");
        sb.AppendLine();
        sb.AppendLine("Stack Trace:");
        sb.AppendLine(ex.StackTrace ?? "(no stack trace)");
        
        // Include inner exceptions
        var inner = ex.InnerException;
        int depth = 1;
        while (inner != null && depth <= 3)
        {
            sb.AppendLine();
            sb.AppendLine($"─── Inner Exception #{depth} ───");
            sb.AppendLine($"Type: {inner.GetType().Name}");
            sb.AppendLine($"Message: {inner.Message}");
            sb.AppendLine("Stack Trace:");
            sb.AppendLine(inner.StackTrace ?? "(no stack trace)");
            
            inner = inner.InnerException;
            depth++;
        }
        
        sb.AppendLine();
        sb.AppendLine("═══════════════════════════════════════════════");
        
        return sb.ToString();
    }
    
    private static void WriteCrashToFile(string crashReport)
    {
        try
        {
            var crashFile = System.IO.Path.Combine(
                PlatformPaths.GetAppBaseDirectory(), 
                "crash.log"
            );
            
            System.IO.File.AppendAllText(crashFile, crashReport);
            Console.WriteLine($"[CrashLogger] Crash report written to: {crashFile}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CrashLogger] Failed to write crash log: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Get all accumulated crash logs
    /// </summary>
    public static string GetCrashLog()
    {
        lock (_lock) return _crashLog.ToString();
    }
    
    /// <summary>
    /// Clear crash log
    /// </summary>
    public static void Clear()
    {
        lock (_lock)
        {
            _crashLog.Clear();
            _crashDialogShown = false;  // Reset dialog flag
        }
    }
}
