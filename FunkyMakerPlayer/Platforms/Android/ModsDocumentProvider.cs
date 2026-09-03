#if ANDROID
using Android.Content;
using Android.Database;
using Android.OS;
using Android.Provider;
using Android.Webkit;
using System;
using System.IO;
using System.Linq;

namespace FunkyMakerPlayer.Platforms.Android
{
    /// <summary>
    /// Custom DocumentsProvider to expose the mods folder in Android file picker
    /// Similar to FNF v-slice's "OPEN DATA FOLDER" functionality
    /// Note: The intent-filter for DOCUMENTS_PROVIDER is automatically added by the Android SDK
    /// </summary>
    [ContentProvider(
        new[] { "com.funkymakergame.player.documents" },
        Exported = true,
        GrantUriPermissions = true,
        Permission = "android.permission.MANAGE_DOCUMENTS")]
    public class ModsDocumentProvider : DocumentsProvider
    {
        private const string ROOT_ID = "funkymaker_mods";
        private const string AUTHORITY = "com.funkymakergame.player.documents";
        
        // Column names for root cursor
        private static readonly string[] DEFAULT_ROOT_PROJECTION = new[]
        {
            DocumentsContract.Root.ColumnRootId,
            DocumentsContract.Root.ColumnIcon,
            DocumentsContract.Root.ColumnTitle,
            DocumentsContract.Root.ColumnFlags,
            DocumentsContract.Root.ColumnDocumentId,
            DocumentsContract.Root.ColumnAvailableBytes,
            DocumentsContract.Root.ColumnSummary
        };
        
        // Column names for document cursor
        private static readonly string[] DEFAULT_DOCUMENT_PROJECTION = new[]
        {
            DocumentsContract.Document.ColumnDocumentId,
            DocumentsContract.Document.ColumnMimeType,
            DocumentsContract.Document.ColumnDisplayName,
            DocumentsContract.Document.ColumnLastModified,
            DocumentsContract.Document.ColumnFlags,
            DocumentsContract.Document.ColumnSize
        };
        
        private Java.IO.File? _modsDirectory;
        
        public override bool OnCreate()
        {
            try
            {
                // Use the PUBLIC mods directory (Documents/FunkyMakerPlayerPublicMods/mods/)
                // This matches PlatformPaths.GetModsBasePath() for Android
                var documentsDir = global::Android.OS.Environment.GetExternalStoragePublicDirectory(global::Android.OS.Environment.DirectoryDocuments);
                
                if (documentsDir == null)
                {
                    Console.WriteLine("[ModsDocumentProvider] WARNING: Documents directory is null, using fallback");
                    // Fallback to app-private directory if Documents is unavailable
                    var externalFilesDir = Context?.GetExternalFilesDir(null);
                    if (externalFilesDir == null) return false;
                    _modsDirectory = new Java.IO.File(externalFilesDir, "mods");
                }
                else
                {
                    // Use Documents/FunkyMakerPlayerPublicMods/mods/ (user-accessible)
                    var funkyMakerPublicDir = new Java.IO.File(documentsDir, "FunkyMakerPlayerPublicMods");
                    _modsDirectory = new Java.IO.File(funkyMakerPublicDir, "mods");
                }
                
                // Create mods directory if it doesn't exist
                if (!_modsDirectory.Exists())
                {
                    _modsDirectory.Mkdirs();
                }
                
                Console.WriteLine($"[ModsDocumentProvider] Mods directory: {_modsDirectory.AbsolutePath}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ModsDocumentProvider] Error in OnCreate: {ex.Message}");
                return false;
            }
        }
        
        public override ICursor? QueryRoots(string[]? projection)
        {
            try
            {
                var result = new MatrixCursor(projection ?? DEFAULT_ROOT_PROJECTION);
                
                if (_modsDirectory == null || !_modsDirectory.Exists())
                    return result;
                
                var row = result.NewRow();
                row.Add(DocumentsContract.Root.ColumnRootId, ROOT_ID);
                row.Add(DocumentsContract.Root.ColumnIcon, global::Android.Resource.Drawable.IcMenuGallery);
                row.Add(DocumentsContract.Root.ColumnTitle, "FunkyMaker Mods");
                row.Add(DocumentsContract.Root.ColumnSummary, "Custom levels and mods");
                
                // Allow creating files and show recents
                // FLAG_SUPPORTS_CREATE = 8, FLAG_SUPPORTS_RECENTS = 2
                var flags = 8 | 2;  // SupportsCreate | SupportsRecents
                row.Add(DocumentsContract.Root.ColumnFlags, flags);
                
                row.Add(DocumentsContract.Root.ColumnDocumentId, GetDocIdForFile(_modsDirectory));
                row.Add(DocumentsContract.Root.ColumnAvailableBytes, _modsDirectory.UsableSpace);
                
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ModsDocumentProvider] Error in QueryRoots: {ex.Message}");
                return new MatrixCursor(projection ?? DEFAULT_ROOT_PROJECTION);
            }
        }
        
        public override ICursor? QueryDocument(string? documentId, string[]? projection)
        {
            try
            {
                var result = new MatrixCursor(projection ?? DEFAULT_DOCUMENT_PROJECTION);
                
                if (string.IsNullOrEmpty(documentId)) return result;
                
                var file = GetFileForDocId(documentId);
                if (file == null || !file.Exists()) return result;
                
                IncludeFile(result, file);
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ModsDocumentProvider] Error in QueryDocument: {ex.Message}");
                return new MatrixCursor(projection ?? DEFAULT_DOCUMENT_PROJECTION);
            }
        }
        
        public override ICursor? QueryChildDocuments(string? parentDocumentId, string[]? projection, string? sortOrder)
        {
            try
            {
                var result = new MatrixCursor(projection ?? DEFAULT_DOCUMENT_PROJECTION);
                
                if (string.IsNullOrEmpty(parentDocumentId)) return result;
                
                var parent = GetFileForDocId(parentDocumentId);
                if (parent == null || !parent.Exists() || !parent.IsDirectory) return result;
                
                var files = parent.ListFiles();
                if (files != null)
                {
                    foreach (var file in files)
                    {
                        IncludeFile(result, file);
                    }
                }
                
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ModsDocumentProvider] Error in QueryChildDocuments: {ex.Message}");
                return new MatrixCursor(projection ?? DEFAULT_DOCUMENT_PROJECTION);
            }
        }
        
        public override ParcelFileDescriptor? OpenDocument(string? documentId, string? mode, CancellationSignal? signal)
        {
            try
            {
                if (string.IsNullOrEmpty(documentId) || string.IsNullOrEmpty(mode)) return null;
                
                var file = GetFileForDocId(documentId);
                if (file == null || !file.Exists()) return null;
                
                var accessMode = (int)ParcelFileMode.ReadOnly;
                if (mode.Contains("w"))
                {
                    accessMode = (int)(ParcelFileMode.ReadWrite | ParcelFileMode.Truncate);
                }
                
                return ParcelFileDescriptor.Open(file, (ParcelFileMode)accessMode);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ModsDocumentProvider] Error in OpenDocument: {ex.Message}");
                return null;
            }
        }
        
        public override string? CreateDocument(string? parentDocumentId, string? mimeType, string? displayName)
        {
            try
            {
                if (string.IsNullOrEmpty(parentDocumentId) || string.IsNullOrEmpty(displayName))
                    return null;
                
                var parent = GetFileForDocId(parentDocumentId);
                if (parent == null || !parent.Exists() || !parent.IsDirectory) return null;
                
                var file = new Java.IO.File(parent, displayName);
                
                if (mimeType == DocumentsContract.Document.MimeTypeDir)
                {
                    file.Mkdirs();
                }
                else
                {
                    file.CreateNewFile();
                }
                
                return GetDocIdForFile(file);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ModsDocumentProvider] Error in CreateDocument: {ex.Message}");
                return null;
            }
        }
        
        public override bool IsChildDocument(string? parentDocumentId, string? documentId)
        {
            try
            {
                if (string.IsNullOrEmpty(parentDocumentId) || string.IsNullOrEmpty(documentId))
                    return false;
                
                return documentId.StartsWith(parentDocumentId);
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Convert a File to a document ID (path relative to mods directory)
        /// </summary>
        private string GetDocIdForFile(Java.IO.File file)
        {
            if (_modsDirectory == null) return "";
            
            var path = file.AbsolutePath;
            var rootPath = _modsDirectory.AbsolutePath;
            
            if (path.Equals(rootPath))
                return "root";
            
            if (path.StartsWith(rootPath))
            {
                return path.Substring(rootPath.Length + 1);
            }
            
            return path;
        }
        
        /// <summary>
        /// Convert a document ID to a File
        /// </summary>
        private Java.IO.File? GetFileForDocId(string? docId)
        {
            if (_modsDirectory == null || string.IsNullOrEmpty(docId)) return null;
            
            if (docId == "root")
                return _modsDirectory;
            
            return new Java.IO.File(_modsDirectory, docId);
        }
        
        /// <summary>
        /// Add file information to cursor
        /// </summary>
        private void IncludeFile(MatrixCursor cursor, Java.IO.File file)
        {
            var row = cursor.NewRow();
            
            var docId = GetDocIdForFile(file);
            row.Add(DocumentsContract.Document.ColumnDocumentId, docId);
            row.Add(DocumentsContract.Document.ColumnDisplayName, file.Name);
            
            // Determine MIME type
            string mimeType;
            if (file.IsDirectory)
            {
                mimeType = DocumentsContract.Document.MimeTypeDir;
            }
            else
            {
                var extension = Path.GetExtension(file.Name)?.ToLower();
                mimeType = extension switch
                {
                    ".fnmm" => "application/octet-stream",
                    ".rar" => "application/x-rar-compressed",
                    ".zip" => "application/zip",
                    ".json" => "application/json",
                    ".txt" => "text/plain",
                    _ => "application/octet-stream"
                };
            }
            row.Add(DocumentsContract.Document.ColumnMimeType, mimeType);
            
            // Flags
            var flags = 0;
            if (file.IsDirectory)
            {
                // FLAG_DIR_SUPPORTS_CREATE = 8
                flags |= 8;
            }
            if (file.CanWrite())
            {
                // FLAG_SUPPORTS_WRITE = 2, FLAG_SUPPORTS_DELETE = 4, FLAG_SUPPORTS_RENAME = 64
                flags |= (2 | 4 | 64);
            }
            row.Add(DocumentsContract.Document.ColumnFlags, flags);
            
            row.Add(DocumentsContract.Document.ColumnLastModified, file.LastModified());
            row.Add(DocumentsContract.Document.ColumnSize, file.IsDirectory ? 0 : file.Length());
        }
    }
}
#endif