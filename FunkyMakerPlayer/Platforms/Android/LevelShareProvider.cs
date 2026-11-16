#if ANDROID
using Android.App;
using Android.Content;
using Android.Database;
using Android.OS;
using Android.Provider;
using System;
using System.IO;

namespace FunkyMakerPlayer.Platforms.Android
{
    /// <summary>
    /// Minimal read-only provider that hands one shared level file to another app (Discord, Drive…)
    /// through the system share sheet. The file is copied into <c>cache/share/</c> first and exposed as
    /// <c>content://com.funkymakergame.player.share/&lt;file name&gt;</c>. Not exported: other apps can only
    /// read it through the temporary read grant attached to the share intent.
    /// </summary>
    [ContentProvider(new[] { AUTHORITY }, Exported = false, GrantUriPermissions = true)]
    public class LevelShareProvider : ContentProvider
    {
        public const string AUTHORITY = "com.funkymakergame.player.share";
        private const string SHARE_DIR = "share";
        private const string MIME = "application/octet-stream";
        private const string COL_NAME = "_display_name"; // OpenableColumns.DisplayName
        private const string COL_SIZE = "_size";         // OpenableColumns.Size

        /// <summary>Copies the file to the share cache and opens the share sheet. Returns false on failure.</summary>
        public static bool ShareFile(string path)
        {
            try
            {
                var activity = Microsoft.Xna.Framework.Game.Activity;
                var cacheDir = activity?.CacheDir?.AbsolutePath;
                if (activity == null || string.IsNullOrEmpty(cacheDir)) return false;

                string shareDir = Path.Combine(cacheDir, SHARE_DIR);
                if (Directory.Exists(shareDir)) Directory.Delete(shareDir, true); // keep only the latest share
                Directory.CreateDirectory(shareDir);

                string name = Path.GetFileName(path);
                File.Copy(path, Path.Combine(shareDir, name), true);

                var uri = global::Android.Net.Uri.Parse($"content://{AUTHORITY}/{global::Android.Net.Uri.Encode(name)}");
                var send = new Intent(Intent.ActionSend);
                send.SetType(MIME);
                send.PutExtra(Intent.ExtraStream, uri);
                send.PutExtra(Intent.ExtraTitle, name);
                send.ClipData = ClipData.NewRawUri(name, uri); // lets the read grant reach the chosen app
                send.AddFlags(ActivityFlags.GrantReadUriPermission);

                var chooser = Intent.CreateChooser(send, "Share level")!;
                chooser.AddFlags(ActivityFlags.GrantReadUriPermission);
                activity.RunOnUiThread(() => activity.StartActivity(chooser));
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LevelShareProvider] Share failed: {ex.Message}");
                return false;
            }
        }

        public override bool OnCreate() => true;

        /// <summary>Maps the URI to cache/share/&lt;name&gt;; rejects anything that is not a plain file name.</summary>
        private Java.IO.File? Resolve(global::Android.Net.Uri? uri)
        {
            string? name = uri?.LastPathSegment;
            var cacheDir = Context?.CacheDir;
            if (string.IsNullOrEmpty(name) || cacheDir == null || name.Contains('/') || name.Contains('\\') || name == "..")
                return null;
            var file = new Java.IO.File(new Java.IO.File(cacheDir, SHARE_DIR), name);
            return file.Exists() ? file : null;
        }

        public override ParcelFileDescriptor? OpenFile(global::Android.Net.Uri uri, string mode)
        {
            var file = Resolve(uri) ?? throw new Java.IO.FileNotFoundException(uri?.ToString());
            return ParcelFileDescriptor.Open(file, ParcelFileMode.ReadOnly);
        }

        public override ICursor? Query(global::Android.Net.Uri uri, string[]? projection, string? selection, string[]? selectionArgs, string? sortOrder)
        {
            var file = Resolve(uri);
            if (file == null) return null;

            var cols = projection ?? new[] { COL_NAME, COL_SIZE };
            var row = new Java.Lang.Object?[cols.Length];
            for (int i = 0; i < cols.Length; i++)
            {
                if (cols[i] == COL_NAME) row[i] = file.Name;
                else if (cols[i] == COL_SIZE) row[i] = file.Length();
            }
            var cursor = new MatrixCursor(cols, 1);
            cursor.AddRow(row);
            return cursor;
        }

        public override string? GetType(global::Android.Net.Uri uri) => MIME;

        public override global::Android.Net.Uri? Insert(global::Android.Net.Uri uri, ContentValues? values)
            => throw new Java.Lang.UnsupportedOperationException("read-only");

        public override int Delete(global::Android.Net.Uri uri, string? selection, string[]? selectionArgs) => 0;

        public override int Update(global::Android.Net.Uri uri, ContentValues? values, string? selection, string[]? selectionArgs) => 0;
    }
}
#endif
