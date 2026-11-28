using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace FunkyMakerPlayer;

internal static class Program
{
    // Native Windows MessageBox
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
    
    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONERROR = 0x00000010;
    private const uint MB_ICONWARNING = 0x00000030;
    private const uint MB_TOPMOST = 0x00040000;
    
    [STAThread]
    static void Main(string[] args)
    {
        // Startup proceeds straight to the game window; update checks run asynchronously in-menu.
        // Load a level only if explicitly passed as argument.
        if (args.Length >= 1 && File.Exists(args[0]))
            Game1.FnmmPath = args[0];

        try
        {
            using var game = new Game1();
            game.Run();
        }
        catch (Exception ex)
        {
            HandleCrash(ex);
        }
    }
    
    /// <summary>
    /// Handle application crash with detailed logging and user notification.
    /// </summary>
    private static void HandleCrash(Exception ex)
    {
        try
        {
            // Create crash folder next to the real executable (not single-file temp dir)
            string baseDir = PlatformPaths.GetAppBaseDirectory();
            string crashFolder = Path.Combine(baseDir, "crash");
            Directory.CreateDirectory(crashFolder);
            
            // Generate timestamped log filename
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string logPath = Path.Combine(crashFolder, $"crash_{timestamp}.log");
            
            // Write detailed crash log
            string crashReport = GenerateCrashReport(ex);
            File.WriteAllText(logPath, crashReport);
            
            // Also keep latest crash in root for backward compatibility
            string latestLog = Path.Combine(baseDir, "crash.log");
            File.WriteAllText(latestLog, crashReport);
            
            // Show crash dialog (platform-specific)
            ShowCrashDialog(logPath, crashFolder);
            
            // Log to console as fallback
            Console.Error.WriteLine(crashReport);
        }
        catch (Exception logEx)
        {
            // Fallback if crash logging itself fails
            Console.Error.WriteLine($"CRITICAL: Failed to write crash log: {logEx.Message}");
            Console.Error.WriteLine($"Original crash: {ex}");
        }
    }
    
    /// <summary>
    /// Generate detailed crash report with system information.
    /// </summary>
    private static string GenerateCrashReport(Exception ex)
    {
        var report = new System.Text.StringBuilder();
        
        report.AppendLine("═══════════════════════════════════════════════════════════════");
        report.AppendLine("  FUNKYMAKER PLAYER - CRASH REPORT");
        report.AppendLine("═══════════════════════════════════════════════════════════════");
        report.AppendLine();
        report.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"Version: {typeof(Program).Assembly.GetName().Version}");
        report.AppendLine($"Platform: {RuntimeInformation.OSDescription}");
        report.AppendLine($"Architecture: {RuntimeInformation.ProcessArchitecture}");
        report.AppendLine($"Framework: {RuntimeInformation.FrameworkDescription}");
        report.AppendLine();
        report.AppendLine("───────────────────────────────────────────────────────────────");
        report.AppendLine("  EXCEPTION DETAILS");
        report.AppendLine("───────────────────────────────────────────────────────────────");
        report.AppendLine();
        report.AppendLine(ex.ToString());
        report.AppendLine();
        
        // Add inner exceptions if present
        if (ex.InnerException != null)
        {
            report.AppendLine("───────────────────────────────────────────────────────────────");
            report.AppendLine("  INNER EXCEPTION");
            report.AppendLine("───────────────────────────────────────────────────────────────");
            report.AppendLine();
            report.AppendLine(ex.InnerException.ToString());
            report.AppendLine();
        }
        
        report.AppendLine("═══════════════════════════════════════════════════════════════");
        
        return report.ToString();
    }
    
    /// <summary>
    /// Show platform-appropriate crash dialog window.
    /// </summary>
    private static void ShowCrashDialog(string logPath, string crashFolder)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            ShowWindowsCrashDialog(logPath, crashFolder);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            ShowLinuxCrashDialog(logPath, crashFolder);
        }
        else
        {
            // Fallback to console for other platforms
            ShowConsoleCrashMessage(logPath, crashFolder);
        }
    }
    
    /// <summary>
    /// Show Windows native MessageBox with crash information.
    /// </summary>
    private static void ShowWindowsCrashDialog(string logPath, string crashFolder)
    {
        string message = 
            "FunkyMaker Player has crashed unexpectedly.\n\n" +
            $"Crash log saved to:\n{logPath}\n\n" +
            "What to do:\n" +
            "• Check the crash log for details\n" +
            "• Report the issue on GitHub\n" +
            "• Try restarting the application\n\n" +
            "Press OK to open the crash folder.";
        
        string title = "FunkyMaker Player - Critical Error";
        
        try
        {
            // Show MessageBox
            MessageBox(IntPtr.Zero, message, title, MB_OK | MB_ICONERROR | MB_TOPMOST);
            
            // Open crash folder in Explorer
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = crashFolder,
                UseShellExecute = true
            });
        }
        catch
        {
            // Fallback to console if MessageBox fails
            ShowConsoleCrashMessage(logPath, crashFolder);
        }
    }
    
    /// <summary>
    /// Show Linux dialog using zenity (GNOME) or kdialog (KDE).
    /// </summary>
    private static void ShowLinuxCrashDialog(string logPath, string crashFolder)
    {
        string message = 
            $"FunkyMaker Player has crashed unexpectedly.\\n\\n" +
            $"Crash log saved to:\\n{logPath}\\n\\n" +
            "What to do:\\n" +
            "• Check the crash log for details\\n" +
            "• Report the issue on GitHub\\n" +
            "• Try restarting the application";
        
        string title = "FunkyMaker Player - Critical Error";
        
        // Try zenity first (GNOME/GTK)
        bool shown = TryShowZenityDialog(title, message, crashFolder);
        
        // If zenity failed, try kdialog (KDE)
        if (!shown)
        {
            shown = TryShowKDialogDialog(title, message, crashFolder);
        }
        
        // If both failed, fallback to console
        if (!shown)
        {
            ShowConsoleCrashMessage(logPath, crashFolder);
        }
    }
    
    /// <summary>
    /// Try to show zenity dialog (GNOME/GTK environments).
    /// </summary>
    private static bool TryShowZenityDialog(string title, string message, string crashFolder)
    {
        try
        {
            var zenityProcess = Process.Start(new ProcessStartInfo
            {
                FileName = "zenity",
                Arguments = $"--error --title=\"{title}\" --text=\"{message}\" --width=500",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            
            if (zenityProcess != null)
            {
                zenityProcess.WaitForExit();
                
                // Try to open file manager
                TryOpenLinuxFileManager(crashFolder);
                
                return true;
            }
        }
        catch
        {
            // zenity not available
        }
        
        return false;
    }
    
    /// <summary>
    /// Try to show kdialog (KDE environments).
    /// </summary>
    private static bool TryShowKDialogDialog(string title, string message, string crashFolder)
    {
        try
        {
            var kdialogProcess = Process.Start(new ProcessStartInfo
            {
                FileName = "kdialog",
                Arguments = $"--error \"{message}\" --title \"{title}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            
            if (kdialogProcess != null)
            {
                kdialogProcess.WaitForExit();
                
                // Try to open file manager
                TryOpenLinuxFileManager(crashFolder);
                
                return true;
            }
        }
        catch
        {
            // kdialog not available
        }
        
        return false;
    }
    
    /// <summary>
    /// Try to open Linux file manager to show crash folder.
    /// </summary>
    private static void TryOpenLinuxFileManager(string crashFolder)
    {
        // Try common Linux file managers
        string[] fileManagers = { "xdg-open", "nautilus", "dolphin", "thunar", "pcmanfm", "caja" };
        
        foreach (var fm in fileManagers)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = fm,
                    Arguments = crashFolder,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                break; // Success, stop trying
            }
            catch
            {
                // Try next file manager
                continue;
            }
        }
    }
    
    /// <summary>
    /// Fallback console crash message (used when GUI dialogs fail).
    /// </summary>
    private static void ShowConsoleCrashMessage(string logPath, string crashFolder)
    {
        bool canColor = !OperatingSystem.IsAndroid();
        if (canColor) try { Console.ForegroundColor = ConsoleColor.Red; } catch { }
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine("  ❌ FUNKYMAKER PLAYER CRASHED");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        if (canColor) try { Console.ResetColor(); } catch { }
        Console.WriteLine();
        Console.WriteLine("The application has encountered a critical error and must close.");
        Console.WriteLine();
        if (canColor) try { Console.ForegroundColor = ConsoleColor.Yellow; } catch { }
        Console.WriteLine("[!] Crash logs saved to:");
        if (canColor) try { Console.ResetColor(); } catch { }
        Console.WriteLine($"   {logPath}");
        Console.WriteLine();
        Console.WriteLine($"📂 All crash logs folder:");
        Console.WriteLine($"   {crashFolder}");
        Console.WriteLine();
        if (canColor) try { Console.ForegroundColor = ConsoleColor.Cyan; } catch { }
        Console.WriteLine("💡 What to do:");
        if (canColor) try { Console.ResetColor(); } catch { }
        Console.WriteLine("   1. Check the crash log for details");
        Console.WriteLine("   2. Report the issue on GitHub with the crash log");
        Console.WriteLine("   3. Try restarting the application");
        Console.WriteLine();
        Console.WriteLine("Press any key to exit...");
        
        if (!OperatingSystem.IsAndroid())
        {
            try
            {
                Console.ReadKey(true);
            }
            catch
            {
                // Ignore if console input not available
            }
        }
    }
}
