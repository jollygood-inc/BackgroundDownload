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
        public static void Log(string msg)
        {
#if ENABLE_LOG
            Debug.Log(msg);
#endif
        }
        
        public static void Warn(string msg)
        {
#if ENABLE_LOG
            Debug.LogWarning(msg);
#endif
        }
        
        public static void Error(string msg)
        {
#if ENABLE_LOG
            Debug.LogError(msg);
#endif
        }
    }
}
