#if ANDROID
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using Android.Views;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.IO;

namespace FunkyMakerPlayer.Platforms.Android
{
    [Activity(
        Label = "@string/app_name",
        MainLauncher = true,
        AlwaysRetainTaskState = true,
        LaunchMode = LaunchMode.SingleInstance,
        ScreenOrientation = ScreenOrientation.UserLandscape,
        ConfigurationChanges = ConfigChanges.Orientation | 
                               ConfigChanges.Keyboard | 
                               ConfigChanges.KeyboardHidden | 
                               ConfigChanges.ScreenSize |
                               ConfigChanges.ScreenLayout)]
    public class MainActivity : AndroidGameActivity
    {
        private Game1 _game;
        private Action<global::Android.Net.Uri?, string?, Stream?>? _filePickerCallback;
        private Action<global::Android.Net.Uri?>? _folderPickerCallback;
        
        private const int FILE_PICKER_REQUEST_CODE = 1001;
        private const int OPEN_MODS_FOLDER_REQUEST_CODE = 1002;
        private const int FOLDER_PICKER_REQUEST_CODE = 1003;
        
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            
            // Keep screen on during gameplay
            Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
            
            // Request storage permissions for public mods folder
            RequestStoragePermissions();
            
            // Create game instance
            _game = new Game1();
            
            // Set the view FIRST
            SetContentView((View?)_game.Services.GetService(typeof(View)));
            
            // THEN hide system UI after view is set
            HideSystemUI();
            
            // Run the game
            _game.Run();
        }
        
        /// <summary>
        /// Hide system UI (status bar and navigation bar) for immersive fullscreen
        /// </summary>
        private void HideSystemUI()
        {
            if (Window?.DecorView == null) return;
            
            // Simple immersive mode that works on all Android versions
            var decorView = Window.DecorView;
            var uiOptions = (int)(
                SystemUiFlags.LayoutStable |
                SystemUiFlags.LayoutHideNavigation |
                SystemUiFlags.LayoutFullscreen |
                SystemUiFlags.HideNavigation |
                SystemUiFlags.Fullscreen |
                SystemUiFlags.ImmersiveSticky
            );
            decorView.SystemUiVisibility = (StatusBarVisibility)uiOptions;
        }
        
        protected override void OnResume()
        {
            base.OnResume();
            // Re-hide system UI when app resumes
            HideSystemUI();
        }
        
        public override void OnWindowFocusChanged(bool hasFocus)
        {
            base.OnWindowFocusChanged(hasFocus);
            // Re-hide system UI when window gains focus
            if (hasFocus)
            {
                HideSystemUI();
            }
        }
        
        /// <summary>
        /// Request storage permissions for accessing public mods folder
        /// </summary>
        private void RequestStoragePermissions()
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
            {
                var permissions = new List<string>();
                
                // For Android 10 and below
                if (Build.VERSION.SdkInt <= BuildVersionCodes.Q)
                {
                    if (CheckSelfPermission(global::Android.Manifest.Permission.ReadExternalStorage) != global::Android.Content.PM.Permission.Granted)
                        permissions.Add(global::Android.Manifest.Permission.ReadExternalStorage);
                    
                    if (CheckSelfPermission(global::Android.Manifest.Permission.WriteExternalStorage) != global::Android.Content.PM.Permission.Granted)
                        permissions.Add(global::Android.Manifest.Permission.WriteExternalStorage);
                }
                // For Android 13+
                else if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
                {
                    if (CheckSelfPermission(global::Android.Manifest.Permission.ReadMediaAudio) != global::Android.Content.PM.Permission.Granted)
                        permissions.Add(global::Android.Manifest.Permission.ReadMediaAudio);
                }
                
                if (permissions.Count > 0)
                {
                    RequestPermissions(permissions.ToArray(), 1);
                }
            }
        }
        
        /// <summary>
        /// Open Android file picker to select a file
        /// </summary>
        public void PickFile(Action<global::Android.Net.Uri?, string?, Stream?> callback)
        {
            _filePickerCallback = callback;
            
            var intent = new Intent(Intent.ActionGetContent);
            intent.AddCategory(Intent.CategoryOpenable);
            
            // Accept .FNMM and .rar files
            intent.SetType("*/*");
            var mimeTypes = new[] { "application/octet-stream", "application/x-rar-compressed", "application/x-rar", "application/zip" };
            intent.PutExtra(Intent.ExtraMimeTypes, mimeTypes);
            
            // Allow browsing all storage locations
            intent.PutExtra(Intent.ExtraLocalOnly, false);
            intent.PutExtra(Intent.ExtraAllowMultiple, false);
            
            // Try to start from external storage
            try
            {
                var externalStorageUri = global::Android.Net.Uri.Parse("content://com.android.externalstorage.documents/document/primary");
                intent.PutExtra("android.provider.extra.INITIAL_URI", externalStorageUri);
            }
            catch
            {
                // Fallback if initial URI fails
            }
            
            StartActivityForResult(Intent.CreateChooser(intent, "Seleccionar archivo de nivel"), FILE_PICKER_REQUEST_CODE);
        }
        
        /// <summary>
        /// Pick a folder using Storage Access Framework (SAF)
        /// This gives persistent read/write access to user-selected folders
        /// </summary>
        public void PickModsFolder(Action<global::Android.Net.Uri?> callback)
        {
            _folderPickerCallback = callback;
            
            try
            {
                // Use ACTION_OPEN_DOCUMENT_TREE for folder selection
                var intent = new Intent(Intent.ActionOpenDocumentTree);
                intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantPersistableUriPermission);
                
                // Try to start from Documents/FunkyMakerPlayerPublicMods
                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                {
                    try
                    {
                        var initialUri = global::Android.Net.Uri.Parse("content://com.android.externalstorage.documents/document/primary:Documents/FunkyMakerPlayerPublicMods");
                        intent.PutExtra(DocumentsContract.ExtraInitialUri, initialUri);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[MainActivity] Could not set initial URI: {ex.Message}");
                    }
                }
                
                StartActivityForResult(intent, FOLDER_PICKER_REQUEST_CODE);
                Console.WriteLine("[MainActivity] Launched folder picker");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MainActivity] Error launching folder picker: {ex.Message}");
                _folderPickerCallback?.Invoke(null);
                _folderPickerCallback = null;
            }
        }
        
        /// <summary>
        /// Open the mods folder directly in file manager (like FNF's "OPEN DATA FOLDER")
        /// Uses a public folder for easy access: /storage/emulated/0/FunkyMakerPlayerPublicMods/mods/
        /// </summary>
        public void OpenModsFolder()
        {
            try
            {
                // Use Documents directory as a safe public location
                var documentsDir = global::Android.OS.Environment.GetExternalStoragePublicDirectory(global::Android.OS.Environment.DirectoryDocuments);
                if (documentsDir == null)
                {
                    Console.WriteLine("[MainActivity] Documents directory is null, using fallback");
                    // Fallback to external files dir (app-private but accessible via file managers)
                    var externalFilesDir = GetExternalFilesDir(null);
                    if (externalFilesDir != null)
                    {
                        documentsDir = new Java.IO.File(externalFilesDir.ParentFile?.ParentFile?.ParentFile, "Documents");
                    }
                }
                
                if (documentsDir == null)
                {
                    ShowModsFolderError("No se pudo acceder al almacenamiento");
                    return;
                }
                
                // Create FunkyMakerPlayerPublicMods/mods/ in Documents
                var funkyMakerPublicDir = new Java.IO.File(documentsDir, "FunkyMakerPlayerPublicMods");
                var modsDir = new Java.IO.File(funkyMakerPublicDir, "mods");
                
                if (!modsDir.Exists())
                {
                    modsDir.Mkdirs();
                }
                
                // Create a README.txt file with instructions
                var readmeFile = new Java.IO.File(modsDir, "README.txt");
                if (!readmeFile.Exists())
                {
                    try
                    {
                        using var writer = new Java.IO.FileWriter(readmeFile);
                        writer.Write(
                            "=== FUNKYMAKER PLAYER - MODS FOLDER ===\n\n" +
                            "Coloca tus archivos .fnmm o .rar aqui.\n" +
                            "Los niveles apareceran en Freeplay al reiniciar la app.\n\n" +
                            "Formatos soportados:\n" +
                            "- .fnmm (formato nativo)\n" +
                            "- .rar (archivos comprimidos)\n" +
                            "- .zip (archivos comprimidos)\n\n" +
                            "Ruta completa: " + modsDir.AbsolutePath + "\n\n" +
                            "Disfruta! 🎵\n"
                        );
                        writer.Flush();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[MainActivity] Failed to create README: {ex.Message}");
                    }
                }
                
                Console.WriteLine($"[MainActivity] Public mods folder: {modsDir.AbsolutePath}");
                
                // Show success message with folder location
                RunOnUiThread(() =>
                {
                    var toast = global::Android.Widget.Toast.MakeText(
                        this,
                        "📁 Carpeta creada:\nDocuments/FunkyMakerPlayerPublicMods/mods/",
                        global::Android.Widget.ToastLength.Long
                    );
                    toast?.Show();
                });
                
                // Try multiple methods to open the folder in file manager
                
                // Method 1: Try to open with file:// URI (some file managers support this)
                try
                {
                    var fileUri = global::Android.Net.Uri.Parse("file://" + modsDir.AbsolutePath);
                    var intent = new Intent(Intent.ActionView);
                    intent.SetDataAndType(fileUri, "resource/folder");
                    intent.AddFlags(ActivityFlags.NewTask);
                    
                    StartActivity(intent);
                    Console.WriteLine("[MainActivity] Opened with file:// URI");
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MainActivity] file:// URI method failed: {ex.Message}");
                }
                
                // Method 2: Try with ACTION_GET_CONTENT to let user navigate
                try
                {
                    var intent = new Intent(Intent.ActionGetContent);
                    intent.SetType("*/*");
                    intent.AddCategory(Intent.CategoryOpenable);
                    
                    // Try to set initial directory (may not work on all file managers)
                    var uri = global::Android.Net.Uri.Parse("content://com.android.externalstorage.documents/document/primary:Documents/FunkyMakerPlayerPublicMods/mods");
                    intent.PutExtra("android.provider.extra.INITIAL_URI", uri);
                    
                    StartActivity(Intent.CreateChooser(intent, "Abrir carpeta de mods"));
                    Console.WriteLine("[MainActivity] Opened with ACTION_GET_CONTENT");
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MainActivity] ACTION_GET_CONTENT method failed: {ex.Message}");
                }
                
                // Method 3: Content URI (original method)
                try
                {
                    var uri = global::Android.Net.Uri.Parse("content://com.android.externalstorage.documents/document/primary:Documents/FunkyMakerPlayerPublicMods/mods");
                    var intent = new Intent(Intent.ActionView);
                    intent.SetDataAndType(uri, "vnd.android.document/directory");
                    intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission);
                    
                    StartActivity(intent);
                    Console.WriteLine("[MainActivity] Opened with content URI");
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MainActivity] Content URI method failed: {ex.Message}");
                }
                
                // Fallback: Show dialog with instructions
                ShowModsFolderInstructions(modsDir);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MainActivity] Error opening mods folder: {ex.Message}");
                ShowModsFolderError(ex.Message);
            }
        }
        
        /// <summary>
        /// Show error message when mods folder cannot be opened
        /// </summary>
        private void ShowModsFolderError(string error)
        {
            RunOnUiThread(() =>
            {
                var alertBuilder = new AlertDialog.Builder(this);
                alertBuilder.SetTitle("❌ Error");
                alertBuilder.SetMessage($"No se pudo abrir la carpeta de mods:\n\n{error}");
                alertBuilder.SetPositiveButton("OK", (sender, args) => { });
                alertBuilder.Show();
            });
        }
        
        /// <summary>
        /// Show a toast with instructions to navigate to mods folder
        /// </summary>
        private void ShowModsFolderInstructions(Java.IO.File modsDir)
        {
            RunOnUiThread(() =>
            {
                // Show toast notification
                var toast = global::Android.Widget.Toast.MakeText(
                    this,
                    "📁 Carpeta de mods:\nDocuments/FunkyMakerPlayerPublicMods/mods/",
                    global::Android.Widget.ToastLength.Long
                );
                toast?.Show();
                
                // Also show a more detailed dialog
                var alertBuilder = new AlertDialog.Builder(this);
                alertBuilder.SetTitle("📂 Carpeta de Mods Pública");
                alertBuilder.SetMessage(
                    "Tu carpeta de mods está lista! 🎵\n\n" +
                    "📍 Ubicación:\n" +
                    "Documents → FunkyMakerPlayerPublicMods → mods\n\n" +
                    "Pasos para agregar niveles:\n" +
                    "1. Abre tu File Manager (Archivos, Files, etc.)\n" +
                    "2. Ve a la carpeta 'Documents'\n" +
                    "3. Busca 'FunkyMakerPlayerPublicMods'\n" +
                    "4. Entra a la carpeta 'mods'\n" +
                    "5. Copia tus archivos .fnmm o .rar aquí\n" +
                    "6. Reinicia FunkyMaker Player\n\n" +
                    "💡 TIP: Algunos file managers necesitan actualizar/refrescar para ver la carpeta nueva.\n\n" +
                    "Ruta completa:\n" + modsDir.AbsolutePath
                );
                alertBuilder.SetPositiveButton("Entendido", (sender, args) => { });
                alertBuilder.Show();
            });
        }
        
        protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            
            if (requestCode == FILE_PICKER_REQUEST_CODE)
            {
                if (resultCode == Result.Ok && data?.Data != null)
                {
                    try
                    {
                        var uri = data.Data;
                        var stream = ContentResolver?.OpenInputStream(uri);
                        
                        // Get filename from URI
                        string? fileName = null;
                        var cursor = ContentResolver?.Query(uri, null, null, null, null);
                        if (cursor != null)
                        {
                            try
                            {
                                if (cursor.MoveToFirst())
                                {
                                    var nameIndex = cursor.GetColumnIndex(global::Android.Provider.OpenableColumns.DisplayName);
                                    if (nameIndex >= 0)
                                    {
                                        fileName = cursor.GetString(nameIndex);
                                    }
                                }
                            }
                            finally
                            {
                                cursor.Close();
                            }
                        }
                        
                        // Fallback filename
                        if (string.IsNullOrEmpty(fileName))
                        {
                            fileName = $"level_{DateTime.Now:yyyyMMdd_HHmmss}.fnmm";
                        }
                        
                        _filePickerCallback?.Invoke(uri, fileName, stream);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[MainActivity] Error opening file: {ex.Message}");
                        _filePickerCallback?.Invoke(null, null, null);
                    }
                }
                else
                {
                    // Cancelled or failed
                    _filePickerCallback?.Invoke(null, null, null);
                }
                
                _filePickerCallback = null;
            }
            else if (requestCode == FOLDER_PICKER_REQUEST_CODE)
            {
                // User selected a folder with ACTION_OPEN_DOCUMENT_TREE
                if (resultCode == Result.Ok && data?.Data != null)
                {
                    var folderUri = data.Data;
                    Console.WriteLine($"[MainActivity] User selected folder: {folderUri}");
                    
                    try
                    {
                        // Take persistent permission for this folder
                        var takeFlags = ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission;
                        ContentResolver?.TakePersistableUriPermission(folderUri, takeFlags);
                        Console.WriteLine($"[MainActivity] Granted persistent permissions for: {folderUri}");
                        
                        // Save the URI to preferences
                        var prefs = GetSharedPreferences("FunkyMakerPlayer", FileCreationMode.Private);
                        var editor = prefs?.Edit();
                        editor?.PutString("mods_folder_uri", folderUri.ToString());
                        editor?.Apply();
                        Console.WriteLine($"[MainActivity] Saved mods folder URI to preferences");
                        
                        // IMPORTANT: Import files from selected folder to app storage
                        Console.WriteLine($"[MainActivity] Starting import from selected folder...");
                        PlatformPaths.ImportFromCustomFolder(folderUri);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[MainActivity] Error taking persistent permission: {ex.Message}");
                    }
                    
                    _folderPickerCallback?.Invoke(folderUri);
                }
                else
                {
                    // Cancelled
                    Console.WriteLine($"[MainActivity] Folder picker cancelled");
                    _folderPickerCallback?.Invoke(null);
                }
                
                _folderPickerCallback = null;
            }
            else if (requestCode == OPEN_MODS_FOLDER_REQUEST_CODE)
            {
                // User selected a folder with ACTION_OPEN_DOCUMENT_TREE
                // We can optionally handle persistent permissions here
                if (resultCode == Result.Ok && data?.Data != null)
                {
                    Console.WriteLine($"[MainActivity] User selected folder: {data.Data}");
                }
            }
        }
        
        protected override void OnDestroy()
        {
            _game?.Dispose();
            base.OnDestroy();
        }
    }
}
#endif