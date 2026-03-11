#if UNITY_ANDROID

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Unity.Networking
{
    /// <summary>
    /// OkHttp を使用した Android 向け BackgroundDownload の実装。
    /// DownloadManager ベースの BackgroundDownloadAndroid と同じインターフェースを提供しますが、
    /// OkHttp を HTTP 通信に使用することで、リアルタイムの進捗追跡やバイト数取得が可能です。
    /// </summary>
    class BackgroundDownloadAndroidOkHttp : BackgroundDownload
    {
        private const string TEMP_FILE_SUFFIX = ".part";
        private const string TAG = "BackgroundDownloadAndroidOkHttp";

        static AndroidJavaClass _playerClass;
        static AndroidJavaClass _backgroundDownloadClass;

        /// <summary>
        /// Java からのダウンロード完了コールバックを受け取り、
        /// アクティブなすべてのダウンロードに対してステータスチェックをトリガーするプロキシ。
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
        /// 初回使用時に静的な Java クラス参照と完了コールバックを初期化します。
        /// 複数回呼び出しても安全です。
        /// </summary>
        static void SetupBackendStatics()
        {
            if (_backgroundDownloadClass == null)
                _backgroundDownloadClass = new AndroidJavaClass("com.unity3d.backgrounddownload.BackgroundDownloadOkHttp");

            if (_finishedCallback == null)
            {
                _finishedCallback = new Callback();

                var receiver = new AndroidJavaClass("com.unity3d.backgrounddownload.CompletionReceiver");
                receiver.CallStatic("setCallback", _finishedCallback);

                _backgroundDownloadClass.CallStatic("setCompletionCallback", _finishedCallback);
            }

            if (_playerClass == null)
                _playerClass = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
        }

        // ------------------------------------------------------------------ //
        // Constructors
        // ------------------------------------------------------------------ //

        /// <summary>
        /// <paramref name="config"/> に基づいて新しいダウンロードを開始します。
        /// ダウンロード完了後に最終パスへリネームされる一時 <c>.part</c> ファイルを作成します。
        /// </summary>
        /// <param name="config">URL・保存先パス・ポリシー・ヘッダーを含むダウンロード設定。</param>
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
                    BackgroundDownloadLog.Log($"[{TAG}] Ensure directory: {dir}, exists={Directory.Exists(dir)}");
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

                BackgroundDownloadLog.Log($"[{TAG}] start id={_id}, url={config.url.AbsoluteUri}, temp={_tempFilePath}, fileUri={fileUri}");
            }
            catch (Exception e)
            {
                _status = BackgroundDownloadStatus.Failed;
                _error = $"Failed to start download: {e.Message}";
                BackgroundDownloadLog.Error($"[{TAG}] {_error}\n{e}");
            }
        }

        /// <summary>
        /// 以前に永続化された ID から既存のダウンロードを復元します。
        /// <see cref="LoadDownloads"/> から内部的に使用されます。
        /// </summary>
        /// <param name="id">以前の <c>start</c> 呼び出しが返したダウンロード ID。</param>
        /// <param name="download"><c>recreate</c> で取得した Java 側のダウンロードオブジェクト。</param>
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
        /// Java レイヤーを呼び出して、以前に保存した ID からダウンロードを再構築しようとします。
        /// </summary>
        /// <param name="id">永続化されたダウンロード ID。</param>
        /// <returns>
        /// 再構築された <see cref="BackgroundDownloadAndroidOkHttp"/> インスタンス。
        /// Java レイヤーがこの ID を認識しない場合は <c>null</c>。
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
                BackgroundDownloadLog.Error($"[{TAG}] Failed to recreate background download with id {id}: {e.Message}");
            }

            return null;
        }

        /// <summary>
        /// Java レイヤーから元のダウンロード URL を読み取ります。
        /// </summary>
        Uri QueryDownloadUri()
        {
            return new Uri(_download.Call<string>("getDownloadUrl"));
        }

        /// <summary>
        /// Javaレイヤーから保存先URIを取得し、C#側の相対パスと.tempファイルの絶対パスを導出します。
        /// </summary>
        /// <param name="tempFilePath">出力: .part一時ファイルの絶対パス</param>
        /// <returns>保存先の相対パス（_downloadsのキー）</returns>
        string QueryDestinationPath(out string tempFilePath)
        {
            string destination = _download.Call<string>("getDestinationUri");
            string localPath = destination;

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
        /// Javaレイヤーからダウンロード失敗時のエラーメッセージを取得します。
        /// </summary>
        /// <returns>人間が読めるエラー文字列。エラーがなければnull。</returns>
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
        /// Javaレイヤーに完了状態を問い合わせ、_statusを更新します。
        /// 成功時は.tempファイルを最終パスにリネームします。
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
                BackgroundDownloadLog.Error($"[{TAG}] {_error}\n{e}");
                return;
            }

            if (status == 1)
            {
                if (!string.IsNullOrEmpty(_tempFilePath) && _tempFilePath.EndsWith(TEMP_FILE_SUFFIX, StringComparison.Ordinal))
                {
                    string filePath = _tempFilePath.Substring(0, _tempFilePath.Length - TEMP_FILE_SUFFIX.Length);
                    bool finalized = false;

                    if (File.Exists(_tempFilePath))
                    {
                        for (int i = 0; i < 10; i++)
                        {
                            try
                            {
                                // リトライ中に別スレッドが先に移動済みの場合
                                if (!File.Exists(_tempFilePath))
                                {
                                    if (File.Exists(filePath))
                                    {
                                        // 既に完成済み
                                        finalized = true;
                                        break;
                                    }

                                    // temp も final も存在しない（異常状態）
                                    BackgroundDownloadLog.Error($"[{TAG}] Temp file missing and final file not found. temp={_tempFilePath} final={filePath}");
                                    _status = BackgroundDownloadStatus.Failed;
                                    _error = "Downloaded file missing";
                                    return;
                                }

                                // File.Move はアトミックに近い操作のため Copy+Delete より安全
                                if (File.Exists(filePath))
                                    File.Delete(filePath);
                                File.Move(_tempFilePath, filePath);
                                finalized = true;
                                break;
                            }
                            catch (IOException e)
                            {
                                BackgroundDownloadLog.Warn($"[{TAG}] Retry finalize attempt {i + 1}/10 : {e.Message}");
                                // NOTE: Thread.Sleep はメインスレッドをブロックするため短めに設定
                                // ファイルロックが解けるのを少し待つ
                                System.Threading.Thread.Sleep(30);
                            }
                        }

                        if (!finalized)
                        {
                            BackgroundDownloadLog.Error($"[{TAG}] Failed to finalize download after retries. temp={_tempFilePath} final={filePath}");
                            _status = BackgroundDownloadStatus.Failed;
                            _error = "File finalize failed";
                            return;
                        }
                    }
                    else if (!File.Exists(filePath))
                    {
                        BackgroundDownloadLog.Warn($"[{TAG}] temp file not found at '{_tempFilePath}'. Checking Java-side destination.");

                        string destUri = null;
                        try
                        {
                            destUri = _download.Call<string>("getDestinationUri");
                        }
                        catch (Exception e)
                        {
                            BackgroundDownloadLog.Error($"[{TAG}] failed to query destination URI: {e.Message}");
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
                            BackgroundDownloadLog.Error($"[{TAG}] downloaded file not found at '{_tempFilePath}', '{filePath}', or '{destLocalPath}'.");
                            _status = BackgroundDownloadStatus.Failed;
                            _error = "Downloaded file not found after completion.";
                            return;
                        }
                    }
                }

                _status = BackgroundDownloadStatus.Done;
            }
            else if (status < 0)
            {
                _status = BackgroundDownloadStatus.Failed;
                _error = GetError();
                BackgroundDownloadLog.Error($"[{TAG}] Download failed. id={_id}, filePath={_config.filePath}, error={_error}");
            }
        }

        /// <summary>
        /// Javaレイヤーにこのダウンロードのキャンセル・削除を依頼します。
        /// </summary>
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
                BackgroundDownloadLog.Warn($"[{TAG}] remove failed: {e.Message}");
            }
        }

        // ------------------------------------------------------------------ //
        // BackgroundDownloadのオーバーライド
        // ------------------------------------------------------------------ //

        /// <summary>
        /// ダウンロードが進行中の間trueを返します。
        /// コルーチン内でyieldできるようにしつつ、毎回Javaレイヤーの完了状態も確認します。
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
        /// ダウンロード進捗を[0,1]で返します。サイズ不明時は負値。
        /// Javaレイヤーの完了状態も毎回確認します。
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
                BackgroundDownloadLog.Warn($"[{TAG}] getProgress failed: {e.Message}");
                return 0f;
            }
        }

        /// <summary>
        /// 現在までにダウンロード済みのバイト数を返します。
        /// JavaのBackgroundDownloadOkHttp.getBytesDownloaded()に委譲。
        /// 失敗時は-1、未開始時は0。
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
                BackgroundDownloadLog.Warn($"[{TAG}] getBytesDownloaded failed: {e.Message}");
                return (_status == BackgroundDownloadStatus.Failed) ? -1L : 0L;
            }
        }

        /// <summary>
        /// ダウンロードをキャンセルし、アクティブセットから削除します。
        /// </summary>
        public override void Dispose()
        {
            RemoveDownload();
            base.Dispose();
        }

        // ------------------------------------------------------------------ //
        // 永続化
        // ------------------------------------------------------------------ //

        /// <summary>
        /// 永続化されたダウンロードIDをディスクから読み出し、
        /// 各BackgroundDownloadAndroidOkHttpインスタンスを復元します。
        /// </summary>
        /// <returns>復元に成功したダウンロードの辞書（キーは保存先相対パス）</returns>
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
                        BackgroundDownloadLog.Warn($"[{TAG}] invalid download id line: '{line}'");
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
        /// アクティブな全ダウンロードのIDをディスクに保存します。
        /// ダウンロードがなければ永続化ファイルを削除します。
        /// </summary>
        /// <param name="downloads">現在のアクティブダウンロード集合</param>
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

