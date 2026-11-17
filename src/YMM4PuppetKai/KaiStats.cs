using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using PuppetKai.Core;
using YukkuriMovieMaker.Commons;

namespace YMM4PuppetKai;

/// <summary>
/// 内訳の計測（試作6）。1回 = アイテム1個の1フレーム分の Update。
/// 使い方（書き出し・再生・停止）ごとに足し合わせ、1500回ごと、または使い方が変わったときに、平均を YMM4 のログに書く。
/// </summary>
internal static class KaiStats
{
    public const int Values = 0, RbDraw = 1, RbWait = 2, RbCopy = 3, Hash = 4, Outline = 5, Mesh = 6, Field = 7, Mls = 8, Arap = 9, Upload = 10, Total = 11, StageCount = 12;
    const int FlushEvery = 1500;

    sealed class Acc
    {
        public readonly long[] Sum = new long[StageCount];
        public int Count, IterSum, IterMax, IterCount, OutlineRebuilds, MeshRebuilds, FieldRebuilds, Reused;
        public void Clear() { Array.Clear(Sum); Count = IterSum = IterMax = IterCount = OutlineRebuilds = MeshRebuilds = FieldRebuilds = Reused = 0; }
    }

    static readonly object gate = new();
    static readonly Acc acc = new();
    static string usage = "";
    static int block;

    public static void Record(string use, long[] stages, in DeformTiming dt, bool deformed)
    {
        string? text = null, text2 = null;
        lock (gate)
        {
            if (use != usage)
            {
                if (acc.Count > 0) text = Build();
                acc.Clear();
                usage = use;
                block = 0;
            }
            for (int i = 0; i < StageCount; i++) acc.Sum[i] += stages[i];
            acc.Count++;
            if (deformed)
            {
                if (dt.OutlineRebuilt) acc.OutlineRebuilds++;
                if (dt.MeshRebuilt) acc.MeshRebuilds++;
                if (dt.FieldRebuilt) acc.FieldRebuilds++;
                if (dt.Reused) acc.Reused++;
                else if (dt.Arap > 0) { acc.IterSum += dt.Iterations; acc.IterMax = Math.Max(acc.IterMax, dt.Iterations); acc.IterCount++; }
            }
            if (acc.Count >= FlushEvery) { text2 = Build(); acc.Clear(); }
        }
        if (text != null) Write(text);
        if (text2 != null) Write(text2);
    }

    static string Build()
    {
        block++;
        static string Ms(long ticks, int n) => (ticks * 1000.0 / Stopwatch.Frequency / Math.Max(1, n)).ToString("0.000", CultureInfo.InvariantCulture);
        int n = acc.Count;
        var s = acc.Sum;
        var sb = new StringBuilder();
        sb.Append($"パペット変形改：内訳 {usage} #{block}（{n}回の平均 ms。1回 = アイテム1個の1フレーム）");
        sb.Append($" 合計={Ms(s[Total], n)}");
        sb.Append($" 値の計算={Ms(s[Values], n)}");
        sb.Append($" 読み戻し[描画={Ms(s[RbDraw], n)} GPU待ち={Ms(s[RbWait], n)} 写し={Ms(s[RbCopy], n)}]");
        sb.Append($" 輪郭の確認={Ms(s[Hash], n)}");
        sb.Append($" 輪郭の作り直し={Ms(s[Outline], n)}（{acc.OutlineRebuilds}回）");
        sb.Append($" 網目={Ms(s[Mesh], n)}（作り直し{acc.MeshRebuilds}回）");
        sb.Append($" 距離と分解={Ms(s[Field], n)}（{acc.FieldRebuilds}回）");
        sb.Append($" MLS={Ms(s[Mls], n)}");
        sb.Append($" ARAP={Ms(s[Arap], n)}（繰り返し 平均{(acc.IterCount > 0 ? (acc.IterSum / (double)acc.IterCount).ToString("0.0", CultureInfo.InvariantCulture) : "-")}・最大{acc.IterMax}）");
        sb.Append($" 使い回し={acc.Reused}回");
        sb.Append($" 頂点の転送={Ms(s[Upload], n)}");
        return sb.ToString();
    }

    static void Write(string text)
    {
        try { Log.Default.Write(text); } catch { }
    }
}
