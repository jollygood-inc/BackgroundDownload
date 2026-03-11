#if true//UNITY_ANDROID

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Unity.Networking
{
    /// <summary>
    /// Android implementation of BackgroundDownload using OkHttp via the
    /// BackgroundDownloadOkHttp Java class. Provides the same behaviour as
    /// BackgroundDownloadAndroid (DownloadManager-based) but uses OkHttp for
    /// HTTP communication, enabling real-time progress and byte-count tracking.
    /// </summary>
    class BackgroundDownloadAndroidOkHttp : BackgroundDownload
    {
        private const string TEMP_FILE_SUFFIX = ".part";

        static AndroidJavaClass _playerClass;
        static AndroidJavaClass _backgroundDownloadClass;

        /// <summary>
        /// Proxy that receives the download-completed callback from Java and
        /// triggers a status check on all active downloads.
        /// </summary>
        class Callback : AndroidJavaProxy
        {
            public Callback()
                : base("com.unity3d.backgrounddownload.CompletionReceiver$Callback")
            {}

            void downloadCompleted()
            {
                lock (typeof(BackgroundDownload))
                {
                    foreach (var download in _downloads.Values)
                        ((BackgroundDownloadAndroidOkHttp)download).CheckFinished();
                }
            }
        }

        static Callback _finishedCallback;

        AndroidJavaObject _download;
        long _id = 0;
        string _tempFilePath;

        // ------------------------------------------------------------------ //
        // Static setup
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Initialises the static Java class references and the completion callback
        /// on first use. Safe to call multiple times.
        /// </summary>
        static void SetupBackendStatics()
        {
            if (_backgroundDownloadClass == null)
                _backgroundDownloadClass = new AndroidJavaClass("com.unity3d.backgrounddownload.BackgroundDownloadOkHttp");

            if (_finishedCallback == null)
            {
                _finishedCallback = new Callback();

                // Register with CompletionReceiver for DownloadManager broadcast compatibility.
                var receiver = new AndroidJavaClass("com.unity3d.backgrounddownload.CompletionReceiver");
                receiver.CallStatic("setCallback", _finishedCallback);

                // Also register directly with BackgroundDownloadOkHttp so OkHttp completions
                // trigger CheckFinished() without relying on the DownloadManager broadcast.
                _backgroundDownloadClass.CallStatic("setCompletionCallback", _finishedCallback);
            }

            if (_playerClass == null)
                _playerClass = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
        }

        // ------------------------------------------------------------------ //
        // Constructors
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Starts a new download described by <paramref name="config"/>.
        /// Creates a temporary <c>.part</c> file that is renamed to the final
        /// destination once the download completes successfully.
        /// </summary>
        /// <param name="config">Download configuration including URL, destination path, policy, and headers.</param>
        internal BackgroundDownloadAndroidOkHttp(BackgroundDownloadConfig config)
            : base(config)
        {
            SetupBackendStatics();

            string filePath = Path.Combine(Application.persistentDataPath, config.filePath);
            _tempFilePath = filePath + TEMP_FILE_SUFFIX;

            try
            {
                if (File.Exists(_tempFilePath))
                    File.Delete(_tempFilePath);

                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                if (!string.IsNullOrEmpty(dir))
                {
                    Debug.Log($"[BackgroundDownloadAndroidOkHttp] Ensure directory: {dir}, exists={Directory.Exists(dir)}");
                }

                string fileUri = new Uri(Path.GetFullPath(_tempFilePath)).AbsoluteUri;

                bool allowMetered = false;
                bool allowRoaming = false;
                switch (_config.policy)
                {
                    case BackgroundDownloadPolicy.AllowMetered:
                        allowMetered = true;
                        break;
                    case BackgroundDownloadPolicy.AlwaysAllow:
                        allowMetered = true;
                        allowRoaming = true;
                        break;
                    default:
                        break;
                }

                _download = _backgroundDownloadClass.CallStatic<AndroidJavaObject>("create", config.url.AbsoluteUri, fileUri);
                _download.Call("setAllowMetered", allowMetered);
                _download.Call("setAllowRoaming", allowRoaming);

                if (config.requestHeaders != null)
                {
                    foreach (var header in config.requestHeaders)
                    {
                        if (header.Value == null)
                            continue;

                        foreach (var val in header.Value)
                            _download.Call("addRequestHeader", header.Key, val);
                    }
                }

                var activity = _playerClass.GetStatic<AndroidJavaObject>("currentActivity");
                _id = _download.Call<long>("start", activity);

                Debug.Log($"[BackgroundDownloadAndroidOkHttp] start id={_id}, url={config.url.AbsoluteUri}, temp={_tempFilePath}, fileUri={fileUri}");
            }
            catch (Exception e)
            {
                _status = BackgroundDownloadStatus.Failed;
                _error = $"Failed to start download: {e.Message}";
                Debug.LogError($"[BackgroundDownloadAndroidOkHttp] {_error}\n{e}");
            }
        }

        /// <summary>
        /// Restores an existing download from a previously persisted ID.
        /// Used internally by <see cref="LoadDownloads"/>.
        /// </summary>
        /// <param name="id">The download ID returned by a previous <c>start</c> call.</param>
        /// <param name="download">The Java-side download object retrieved via <c>recreate</c>.</param>
        BackgroundDownloadAndroidOkHttp(long id, AndroidJavaObject download)
        {
            _id = id;
            _download = download;
            _config.url = QueryDownloadUri();
            _config.filePath = QueryDestinationPath(out _tempFilePath);
            CheckFinished();
        }

        // ------------------------------------------------------------------ //
        // Internal helpers
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Attempts to recreate a download from a previously saved ID by
        /// calling into the Java layer.
        /// </summary>
        /// <param name="id">Persisted download ID.</param>
        /// <returns>
        /// A reconstructed <see cref="BackgroundDownloadAndroidOkHttp"/> instance,
        /// or <c>null</c> if the Java layer no longer knows about this ID.
        /// </returns>
        static BackgroundDownloadAndroidOkHttp Recreate(long id)
        {
            try
            {
                SetupBackendStatics();
                var activity = _playerClass.GetStatic<AndroidJavaObject>("currentActivity");
                var download = _backgroundDownloadClass.CallStatic<AndroidJavaObject>("recreate", activity, id);
                if (download != null)
                    return new BackgroundDownloadAndroidOkHttp(id, download);
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to recreate background download with id {id}: {e.Message}");
            }

            return null;
        }

        /// <summary>
        /// Reads the original download URL back from the Java layer.
        /// </summary>
        Uri QueryDownloadUri()
        {
            return new Uri(_download.Call<string>("getDownloadUrl"));
        }

        /// <summary>
        /// Derives the C# relative file path and the absolute temp file path
        /// from the destination URI stored in the Java layer.
        /// </summary>
        /// <param name="tempFilePath">
        /// Output: the absolute path of the <c>.part</c> temp file.
        /// </param>
        /// <returns>The relative destination path (key used in <c>_downloads</c>).</returns>
        string QueryDestinationPath(out string tempFilePath)
        {
            string destination = _download.Call<string>("getDestinationUri");
            string localPath = destination;

            // Robust URI -> local path conversion
            if (!string.IsNullOrEmpty(destination) && destination.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    localPath = new Uri(destination).LocalPath;
                }
                catch
                {
                    localPath = destination.Replace("file://", "");
                }
            }

            localPath = localPath.Replace("\\", "/");
            string basePath = Application.persistentDataPath.Replace("\\", "/");

            int pos = localPath.IndexOf(basePath, StringComparison.Ordinal);
            if (pos < 0)
            {
                // Fallback: unknown layout, return basename-ish path.
                tempFilePath = localPath;
                string fileName = Path.GetFileName(localPath);
                if (fileName.EndsWith(TEMP_FILE_SUFFIX, StringComparison.Ordinal))
                    return fileName.Substring(0, fileName.Length - TEMP_FILE_SUFFIX.Length);
                return fileName;
            }

            tempFilePath = localPath;
            pos += basePath.Length;

            if (pos < localPath.Length && localPath[pos] == '/')
                ++pos;

            int suffixPos = localPath.LastIndexOf(TEMP_FILE_SUFFIX, StringComparison.Ordinal);
            if (suffixPos > pos)
                return localPath.Substring(pos, suffixPos - pos);

            return localPath.Substring(pos);
        }

        /// <summary>
        /// Retrieves the error message from the Java layer when a download fails.
        /// </summary>
        /// <returns>Human-readable error string, or <c>null</c> if no error.</returns>
        string GetError()
        {
            try
            {
                return _download.Call<string>("getError");
            }
            catch (Exception e)
            {
                return $"Failed to get Java error: {e.Message}";
            }
        }

        /// <summary>
        /// Polls the Java layer for completion status and updates
        /// <see cref="BackgroundDownload._status"/> accordingly.
        /// On success the temporary <c>.part</c> file is renamed to the final path.
        /// </summary>
        void CheckFinished()
        {
            if (_status != BackgroundDownloadStatus.Downloading || _download == null)
                return;

            int status;
            try
            {
                status = _download.Call<int>("checkFinished");
            }
            catch (Exception e)
            {
                _status = BackgroundDownloadStatus.Failed;
                _error = $"checkFinished exception: {e.Message}";
                Debug.LogError($"[BackgroundDownloadAndroidOkHttp] {_error}\n{e}");
                return;
            }

            if (status == 1)
            {
                if (!string.IsNullOrEmpty(_tempFilePath) && _tempFilePath.EndsWith(TEMP_FILE_SUFFIX, StringComparison.Ordinal))
                {
                    string filePath = _tempFilePath.Substring(0, _tempFilePath.Length - TEMP_FILE_SUFFIX.Length);

                    if (File.Exists(_tempFilePath))
                    {
                        try
                        {
                            if (File.Exists(filePath))
                                File.Delete(filePath);

                            File.Move(_tempFilePath, filePath);
                        }
                        catch (Exception e)
                        {
                            Debug.LogError($"Failed to move downloaded file from '{_tempFilePath}' to '{filePath}': {e.Message}");
                            _status = BackgroundDownloadStatus.Failed;
                            _error = e.Message;
                            return;
                        }
                    }
                    else if (!File.Exists(filePath))
                    {
                        // Neither temp file nor final file exists.
                        // Ask Java layer for destination URI and check that.
                        Debug.LogWarning($"BackgroundDownloadAndroidOkHttp: temp file not found at '{_tempFilePath}'. Checking Java-side destination.");

                        string destUri = null;
                        try
                        {
                            destUri = _download.Call<string>("getDestinationUri");
                        }
                        catch (Exception e)
                        {
                            Debug.LogError($"BackgroundDownloadAndroidOkHttp: failed to query destination URI: {e.Message}");
                        }

                        string destLocalPath = destUri;
                        if (!string.IsNullOrEmpty(destUri) && destUri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                destLocalPath = new Uri(destUri).LocalPath;
                            }
                            catch
                            {
                                destLocalPath = destUri.Replace("file://", "");
                            }
                        }

                        bool existsAtDest = !string.IsNullOrEmpty(destLocalPath) && File.Exists(destLocalPath);
                        if (!existsAtDest)
                        {
                            Debug.LogError($"BackgroundDownloadAndroidOkHttp: downloaded file not found at '{_tempFilePath}', '{filePath}', or '{destLocalPath}'.");
                            _status = BackgroundDownloadStatus.Failed;
                            _error = "Downloaded file not found after completion.";
                            return;
                        }

                        // Java already moved it to destination path.
                    }
                    // else: temp not present but final exists => already moved.
                }

                _status = BackgroundDownloadStatus.Done;
            }
            else if (status < 0)
            {
                _status = BackgroundDownloadStatus.Failed;
                _error = GetError();
                Debug.LogError($"[BackgroundDownloadAndroidOkHttp] Download failed. id={_id}, filePath={_config.filePath}, error={_error}");
            }
        }

        /// <summary>Asks the Java layer to cancel and remove this download.</summary>
        void RemoveDownload()
        {
            if (_download == null)
                return;

            try
            {
                _download.Call("remove");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[BackgroundDownloadAndroidOkHttp] remove failed: {e.Message}");
            }
        }

        // ------------------------------------------------------------------ //
        // BackgroundDownload overrides
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Returns <c>true</c> while the download is still in progress, allowing
        /// this object to be yielded inside a coroutine.
        /// Also polls the Java layer for completion status on every call so that
        /// the download finishes even if the CompletionReceiver callback is missed.
        /// </summary>
        public override bool keepWaiting
        {
            get
            {
                CheckFinished();
                return _status == BackgroundDownloadStatus.Downloading;
            }
        }

        /// <summary>
        /// Returns the download progress in the range [0, 1].
        /// Returns a negative value when the total size is not yet known.
        /// Also polls the Java layer for completion so status stays current
        /// even when called outside a coroutine.
        /// </summary>
        protected override float GetProgress()
        {
            CheckFinished();

            try
            {
                return _download != null ? _download.Call<float>("getProgress") : 0f;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[BackgroundDownloadAndroidOkHttp] getProgress failed: {e.Message}");
                return 0f;
            }
        }

        /// <summary>
        /// Returns the number of bytes downloaded so far.
        /// Delegates to <c>BackgroundDownloadOkHttp.getBytesDownloaded()</c> which
        /// uses an <c>AtomicLong</c> updated on every OkHttp response body read.
        /// Returns <c>-1</c> if the download has failed, <c>0</c> if not yet started.
        /// Also polls the Java layer for completion so status stays current
        /// even when called outside a coroutine.
        /// </summary>
        protected override long GetBytesDownloaded()
        {
            CheckFinished();

            try
            {
                return _download != null ? _download.Call<long>("getBytesDownloaded") : 0L;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[BackgroundDownloadAndroidOkHttp] getBytesDownloaded failed: {e.Message}");
                return (_status == BackgroundDownloadStatus.Failed) ? -1L : 0L;
            }
        }

        /// <summary>
        /// Cancels the download (if in progress) and removes it from the active set.
        /// </summary>
        public override void Dispose()
        {
            RemoveDownload();
            base.Dispose();
        }

        // ------------------------------------------------------------------ //
        // Persistence
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Loads the persisted download IDs from disk and attempts to recreate
        /// each corresponding <see cref="BackgroundDownloadAndroidOkHttp"/> instance.
        /// Called once per session by the base class on first access to
        /// <see cref="BackgroundDownload.backgroundDownloads"/>.
        /// </summary>
        /// <returns>
        /// A dictionary keyed by relative destination file path containing all
        /// successfully recreated downloads.
        /// </returns>
        internal static Dictionary<string, BackgroundDownload> LoadDownloads()
        {
            var downloads = new Dictionary<string, BackgroundDownload>();
            var file = Path.Combine(Application.persistentDataPath, "unity_background_downloads.dl");

            if (File.Exists(file))
            {
                foreach (var line in File.ReadAllLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    if (!long.TryParse(line, out long id))
                    {
                        Debug.LogWarning($"[BackgroundDownloadAndroidOkHttp] invalid download id line: '{line}'");
                        continue;
                    }

                    var dl = Recreate(id);
                    if (dl != null)
                        downloads[dl.config.filePath] = dl;
                }
            }

            // Some loads might have failed; save the actual state.
            SaveDownloads(downloads);
            return downloads;
        }

        /// <summary>
        /// Persists the IDs of all active downloads to disk so they can be
        /// restored in a subsequent app session via <see cref="LoadDownloads"/>.
        /// Deletes the persistence file when there are no active downloads.
        /// </summary>
        /// <param name="downloads">The current set of active downloads.</param>
        internal static void SaveDownloads(Dictionary<string, BackgroundDownload> downloads)
        {
            var file = Path.Combine(Application.persistentDataPath, "unity_background_downloads.dl");

            if (downloads.Count > 0)
            {
                var ids = new string[downloads.Count];
                int i = 0;
                foreach (var dl in downloads)
                    ids[i++] = ((BackgroundDownloadAndroidOkHttp)dl.Value)._id.ToString();

                File.WriteAllLines(file, ids);
            }
            else if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }
}

#endif