//#define ENABLE_LOG // このシンボルを定義するとBackgroundDownloadのログが出力されます。

using System.Diagnostics;
using UnityEngine;

namespace Unity.Networking
{
    /// <summary>
    /// BackgroundDownload 用の条件付きログラッパー。
    /// ENABLE_LOG シンボルを定義すると Log/Warn/Error の出力が有効になります。
    /// </summary>
    internal static class BackgroundDownloadLog
    {
        [Conditional("ENABLE_LOG")]
        public static void Log(string msg)
        {
            Debug.Log(msg);
        }

        [Conditional("ENABLE_LOG")]
        public static void Warn(string msg)
        {
            Debug.LogWarning(msg);
        }

        [Conditional("ENABLE_LOG")]
        public static void Error(string msg)
        {
            Debug.LogError(msg);
        }
    }
}
