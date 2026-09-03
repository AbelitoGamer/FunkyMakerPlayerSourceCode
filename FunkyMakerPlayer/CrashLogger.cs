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
    private static readonly StringBuilder _crashLog = new StringBuilder();
    private static Action<string>? _onCrashCallback;
    private static bool _crashDialogShown = false;  // Prevenir múltiples diálogos
    
    /// <summary>
    /// Initialize crash logger and hook into unhandled exception handlers
    /// </summary>
    public static void Initialize(Action<string>? onCrashCallback = null)
    {
        _onCrashCallback = onCrashCallback;
        
        // Hook unhandled exceptions
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        
        Console.WriteLine("[CrashLogger] Initialized");
    }
    
    /// <summary>
    /// Log an exception manually
    /// </summary>
    public static void LogException(Exception ex, string context = "")
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var crashReport = BuildCrashReport(ex, context, timestamp);
        
        _crashLog.AppendLine(crashReport);
        
        Console.WriteLine($"[CrashLogger] Exception logged: {ex.Message}");
        
#if ANDROID
        // On Android, show crash dialog only ONCE (prevent multiple dialogs)
        if (!_crashDialogShown)
        {
            _crashDialogShown = true;
            Console.WriteLine("[CrashLogger] Showing crash dialog (first crash only)");
            _onCrashCallback?.Invoke(crashReport);
        }
        else
        {
            Console.WriteLine("[CrashLogger] Crash dialog already shown, skipping duplicate");
        }
#else
        // On Desktop, write to file
        WriteCrashToFile(crashReport);
#endif
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
                AppDomain.CurrentDomain.BaseDirectory, 
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
        return _crashLog.ToString();
    }
    
    /// <summary>
    /// Clear crash log
    /// </summary>
    public static void Clear()
    {
        _crashLog.Clear();
        _crashDialogShown = false;  // Reset dialog flag
    }
}
