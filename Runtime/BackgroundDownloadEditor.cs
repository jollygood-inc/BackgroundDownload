#if UNITY_EDITOR

using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace Unity.Networking
{
    class BackgroundDownloadEditor : BackgroundDownload
    {
        readonly UnityWebRequest _request;
        UnityWebRequestAsyncOperation _operation;
        bool _disposed;

        public BackgroundDownloadEditor(BackgroundDownloadConfig config)
            : base(config)
        {
            try
            {
                if (_config.url == null)
                {
                    _status = BackgroundDownloadStatus.Failed;
                    _error = "URL is null.";
                    return;
                }

                if (string.IsNullOrEmpty(_config.filePath))
                {
                    _status = BackgroundDownloadStatus.Failed;
                    _error = "filePath is null or empty.";
                    return;
                }

                var fullPath = Path.Combine(Application.persistentDataPath, _config.filePath);
                var dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                _request = new UnityWebRequest(_config.url.AbsoluteUri, UnityWebRequest.kHttpVerbGET);
                _request.downloadHandler = new DownloadHandlerFile(fullPath, true);

                if (_config.requestHeaders != null)
                {
                    foreach (var kv in _config.requestHeaders)
                    {
                        var headerName = kv.Key;
                        var values = kv.Value;
                        if (string.IsNullOrEmpty(headerName) || values == null) continue;

                        foreach (var v in values)
                        {
                            if (v != null)
                                _request.SetRequestHeader(headerName, v);
                        }
                    }
                }

                _operation = _request.SendWebRequest();
                _operation.completed += OnCompleted;
                _status = BackgroundDownloadStatus.Downloading;
            }
            catch (System.Exception ex)
            {
                _status = BackgroundDownloadStatus.Failed;
                _error = ex.Message;
            }
        }

        void OnCompleted(AsyncOperation _)
        {
            if (_disposed) return;

#if UNITY_2020_2_OR_NEWER
            if (_request.result == UnityWebRequest.Result.Success)
#else
            if (!(_request.isNetworkError || _request.isHttpError))
#endif
            {
                _status = BackgroundDownloadStatus.Done;
                _error = null;
            }
            else
            {
                _status = BackgroundDownloadStatus.Failed;
                _error = _request.error;
            }

            lock (typeof(BackgroundDownload))
            {
                if (_downloads != null)
                    _downloads.Remove(_config.filePath);
            }
        }

        public override bool keepWaiting
        {
            get { return _status == BackgroundDownloadStatus.Downloading; }
        }

        protected override float GetProgress()
        {
            if (_status == BackgroundDownloadStatus.Done) return 1f;
            if (_status == BackgroundDownloadStatus.Failed) return 1f;
            if (_operation == null) return 0f;
            return _operation.progress;
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_status == BackgroundDownloadStatus.Downloading && _request != null)
            {
                _request.Abort();
                _status = BackgroundDownloadStatus.Failed;
                _error = "Aborted";
            }

            if (_operation != null)
                _operation.completed -= OnCompleted;

            _request?.Dispose();

            base.Dispose();
        }

        internal static Dictionary<string, BackgroundDownload> LoadDownloads()
        {
            // Editorでは前回セッション復元は行わない
            return new Dictionary<string, BackgroundDownload>();
        }

        internal static void SaveDownloads(Dictionary<string, BackgroundDownload> downloads)
        {
            // Editorでは永続化しない
        }
    }
}

#endif