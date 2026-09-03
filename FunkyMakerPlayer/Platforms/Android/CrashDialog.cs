#if ANDROID
using Android.App;
using Android.Content;
using Android.Widget;
using System;

namespace FunkyMakerPlayer.Platforms.Android;

/// <summary>
/// Android crash dialog that shows exception details with copy-to-clipboard functionality
/// </summary>
public static class CrashDialog
{
    /// <summary>
    /// Show crash dialog with the crash report
    /// </summary>
    public static void Show(Activity activity, string crashReport)
    {
        activity.RunOnUiThread(() =>
        {
            try
            {
                var builder = new AlertDialog.Builder(activity);
                builder.SetTitle("⚠️ Application Crashed");
                builder.SetMessage("The app encountered an error. You can copy the error details to share with the developer.");
                builder.SetCancelable(false);
                
                // Copy to clipboard button
                builder.SetPositiveButton("Copy Error & Close", (sender, args) =>
                {
                    CopyToClipboard(activity, crashReport);
                    Toast.MakeText(activity, "Error copied to clipboard!", ToastLength.Long)?.Show();
                    
                    // Close the app after copying
                    activity.FinishAffinity();
                });
                
                // Just close button
                builder.SetNegativeButton("Close", (sender, args) =>
                {
                    activity.FinishAffinity();
                });
                
                // Show details button
                builder.SetNeutralButton("View Details", (sender, args) =>
                {
                    ShowDetailsDialog(activity, crashReport);
                });
                
                var dialog = builder.Create();
                dialog?.Show();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CrashDialog] Failed to show crash dialog: {ex.Message}");
                // Fallback: just copy to clipboard
                CopyToClipboard(activity, crashReport);
            }
        });
    }
    
    /// <summary>
    /// Show detailed crash report in a scrollable dialog
    /// </summary>
    private static void ShowDetailsDialog(Activity activity, string crashReport)
    {
        try
        {
            var scrollView = new ScrollView(activity);
            var textView = new TextView(activity)
            {
                Text = crashReport,
                TextSize = 12
            };
            textView.SetPadding(20, 20, 20, 20);
            scrollView.AddView(textView);
            
            var builder = new AlertDialog.Builder(activity);
            builder.SetTitle("Crash Details");
            builder.SetView(scrollView);
            builder.SetPositiveButton("Copy & Close", (sender, args) =>
            {
                CopyToClipboard(activity, crashReport);
                Toast.MakeText(activity, "Error copied to clipboard!", ToastLength.Long)?.Show();
                activity.FinishAffinity();
            });
            builder.SetNegativeButton("Close", (sender, args) =>
            {
                activity.FinishAffinity();
            });
            
            var dialog = builder.Create();
            dialog?.Show();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CrashDialog] Failed to show details dialog: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Copy text to Android clipboard
    /// </summary>
    private static void CopyToClipboard(Activity activity, string text)
    {
        try
        {
            var clipboard = activity.GetSystemService(Context.ClipboardService) as ClipboardManager;
            if (clipboard != null)
            {
                var clip = ClipData.NewPlainText("Crash Report", text);
                clipboard.PrimaryClip = clip;
                Console.WriteLine("[CrashDialog] Crash report copied to clipboard");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CrashDialog] Failed to copy to clipboard: {ex.Message}");
        }
    }
}
#endif
