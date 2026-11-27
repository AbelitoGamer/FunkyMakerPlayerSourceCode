#if ANDROID
using Android.Content;
using Android.Database;
using Android.OS;
using Android.Provider;
using Android.Runtime;
using Android.Webkit;
using System;
using System.IO;
using System.Linq;

namespace FunkyMakerPlayer.Platforms.Android
{
    /// <summary>
    /// Custom DocumentsProvider to expose the mods folder in Android's system Files app.
    /// Registered in Platforms/Android/AndroidManifest.xml with the required DOCUMENTS_PROVIDER intent-filter.
    /// </summary>
    [Register("com.funkymakergame.player.ModsDocumentProvider")]
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
                // Fork: expose the folder the game actually reads (PlatformPaths.GetModsBasePath():
                // <external app files>/mods). This used to point at Documents/FunkyMakerPlayerPublicMods/mods,
                // a different folder, so the Files app showed mods the game never loaded and the
                // game's own levels could not be reached from the phone.
                var baseDir = Context?.GetExternalFilesDir(null) ?? Context?.FilesDir;
                if (baseDir == null) return false;
                _modsDirectory = new Java.IO.File(baseDir, "mods");

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
                
                // FLAG_SUPPORTS_CREATE = 1, FLAG_SUPPORTS_IS_CHILD = 16 (fork: the old 8 | 2 meant
                // SUPPORTS_SEARCH | LOCAL_ONLY; recents is not implemented, so it is not offered)
                var flags = 1 | 16;
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
                
                var accessMode = ParcelFileDescriptor.ParseMode(mode);
                return ParcelFileDescriptor.Open(file, accessMode);
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
                if (string.IsNullOrEmpty(parentDocumentId) || string.IsNullOrWhiteSpace(displayName))
                    return null;
                if (displayName.Contains('/') || displayName.Contains('\\') || displayName == "." || displayName == "..")
                    return null;
                
                var parent = GetFileForDocId(parentDocumentId);
                if (parent == null || !parent.Exists() || !parent.IsDirectory || _modsDirectory == null) return null;
                
                var file = new Java.IO.File(parent, displayName);
                string rootCanon = _modsDirectory.CanonicalPath.TrimEnd('/') + "/";
                string fileCanon = file.CanonicalPath;
                if (!fileCanon.StartsWith(rootCanon, StringComparison.Ordinal))
                    return null;
                
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
                var parent = GetFileForDocId(parentDocumentId);
                var child = GetFileForDocId(documentId);
                if (parent == null || child == null) return false;
                string parentPath = parent.CanonicalPath.TrimEnd('/') + "/";
                return child.CanonicalPath.StartsWith(parentPath, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        // Fork: documents advertised FLAG_SUPPORTS_DELETE / RENAME, but these overrides were
        // missing, so deleting or renaming from the Files app failed.
        public override void DeleteDocument(string? documentId)
        {
            var file = GetFileForDocId(documentId);
            if (file == null || documentId == "root")
                throw new Java.IO.FileNotFoundException($"Cannot delete {documentId}");
            if (!DeleteRecursive(file))
                throw new Java.IO.IOException($"Failed to delete {documentId}");
        }

        public override string? RenameDocument(string? documentId, string? displayName)
        {
            var file = GetFileForDocId(documentId);
            if (file == null || documentId == "root" || string.IsNullOrWhiteSpace(displayName) ||
                displayName.Contains('/') || displayName == "." || displayName == "..")
                throw new Java.IO.FileNotFoundException($"Cannot rename {documentId}");
            var parent = file.ParentFile;
            if (parent == null) throw new Java.IO.FileNotFoundException($"Cannot rename {documentId}");
            var target = new Java.IO.File(parent, displayName);
            if (target.Exists() || !file.RenameTo(target))
                throw new Java.IO.IOException($"Failed to rename {documentId}");
            return GetDocIdForFile(target);
        }

        private static bool DeleteRecursive(Java.IO.File file)
        {
            if (file.IsDirectory)
            {
                var children = file.ListFiles();
                if (children != null)
                    foreach (var child in children)
                        DeleteRecursive(child);
            }
            return file.Delete();
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

            // Fork: refuse ids that escape the mods folder ("../..").
            var file = new Java.IO.File(_modsDirectory, docId);
            string root = _modsDirectory.CanonicalPath.TrimEnd('/') + "/";
            return file.CanonicalPath.StartsWith(root, StringComparison.Ordinal) ? file : null;
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