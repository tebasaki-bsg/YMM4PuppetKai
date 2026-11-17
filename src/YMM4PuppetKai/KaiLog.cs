using System;
using YukkuriMovieMaker.Commons;

namespace YMM4PuppetKai;

/// <summary>YMM4 本体のログに書く。同じ内容は1回だけ。ログに失敗しても例外は出さない。</summary>
internal static class KaiLog
{
    static readonly System.Collections.Generic.HashSet<string> written = new();

    public static void Error(string message, Exception? ex)
    {
        try
        {
            string key = message + "|" + ex?.GetType().FullName + "|" + ex?.Message;
            lock (written) { if (!written.Add(key)) return; }
            if (ex != null) Log.Default.Write("パペット変形改：" + message, ex);
            else Log.Default.Write("パペット変形改：" + message);
        }
        catch { }
    }
}
