using System;
using System.Collections.Generic;
using System.Numerics;

namespace PuppetKai.Core;

/// <summary>帯行列のコレスキー分解（下三角を行ごとに持つ）。</summary>
internal sealed class BandCholesky
{
    readonly int n, bw, stride;
    readonly double[] data;   // 行 i の列 j（i-bw ≤ j ≤ i）は data[i*stride + (j - i + bw)]
    double[]? lt;             // 転置（列 k の k..k+bw 行）。後退代入を連続した読み出しにする

    public int Size => n;
    public int Bandwidth => bw;

    public BandCholesky(int size, int bandwidth)
    {
        n = size; bw = Math.Max(0, bandwidth); stride = bw + 1;
        data = new double[(long)n * stride];
    }

    public void Add(int i, int j, double v)
    {
        if (j > i) (i, j) = (j, i);
        int off = j - i + bw;
        if (off < 0) throw new ArgumentOutOfRangeException(nameof(j), "帯の外");
        data[i * stride + off] += v;
    }

    public bool Factorize()
    {
        var d = data.AsSpan();
        for (int i = 0; i < n; i++)
        {
            int j0 = Math.Max(0, i - bw);
            for (int j = j0; j <= i; j++)
            {
                int k0 = Math.Max(j0, j - bw);
                int len = j - k0;
                double sum = d[i * stride + (j - i + bw)];
                if (len > 0)
                    sum -= Dot(d.Slice(i * stride + (k0 - i + bw), len), d.Slice(j * stride + (k0 - j + bw), len));
                if (i == j)
                {
                    if (!(sum > 0)) return false;
                    d[i * stride + bw] = Math.Sqrt(sum);
                }
                else d[i * stride + (j - i + bw)] = sum / d[j * stride + bw];
            }
        }
        lt = new double[(long)n * stride];
        for (int i = 0; i < n; i++)
            for (int j = Math.Max(0, i - bw); j <= i; j++)
                lt[j * stride + (i - j)] = data[i * stride + (j - i + bw)];
        return true;
    }

    /// <summary>2つの右辺を同時に解く（上書き）。</summary>
    public void SolvePair(Span<double> bx, Span<double> by)
    {
        var d = data.AsSpan(); var t = lt.AsSpan();
        for (int i = 0; i < n; i++)
        {
            int j0 = Math.Max(0, i - bw), len = i - j0;
            var row = d.Slice(i * stride + (j0 - i + bw), len);
            double sx = bx[i] - Dot(row, bx.Slice(j0, len));
            double sy = by[i] - Dot(row, by.Slice(j0, len));
            double diag = d[i * stride + bw];
            bx[i] = sx / diag; by[i] = sy / diag;
        }
        for (int k = n - 1; k >= 0; k--)
        {
            int len = Math.Min(n - 1, k + bw) - k;
            var col = t.Slice(k * stride + 1, len);
            double sx = bx[k] - Dot(col, bx.Slice(k + 1, len));
            double sy = by[k] - Dot(col, by.Slice(k + 1, len));
            double diag = t[k * stride];
            bx[k] = sx / diag; by[k] = sy / diag;
        }
    }

    public void Solve(Span<double> b)
    {
        var d = data.AsSpan(); var t = lt.AsSpan();
        for (int i = 0; i < n; i++)
        {
            int j0 = Math.Max(0, i - bw), len = i - j0;
            b[i] = (b[i] - Dot(d.Slice(i * stride + (j0 - i + bw), len), b.Slice(j0, len))) / d[i * stride + bw];
        }
        for (int k = n - 1; k >= 0; k--)
        {
            int len = Math.Min(n - 1, k + bw) - k;
            b[k] = (b[k] - Dot(t.Slice(k * stride + 1, len), b.Slice(k + 1, len))) / t[k * stride];
        }
    }

    internal static double Dot(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        int i = 0, w = Vector<double>.Count;
        var acc = Vector<double>.Zero;
        for (; i <= a.Length - w; i += w)
            acc += new Vector<double>(a.Slice(i)) * new Vector<double>(b.Slice(i));
        double s = Vector.Sum(acc);
        for (; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }
}

/// <summary>
/// 行ごとに「最初の非ゼロの列」から対角までを持つ（スカイライン）コレスキー分解。
/// 並べ替え（RCM）と組み合わせると、帯行列より手間が少ない。
/// </summary>
internal sealed class SkylineCholesky
{
    readonly int n;
    readonly int[] first;     // 行 i の最初の列
    readonly long[] start;    // 行 i の先頭（data 上の位置）。列 j は start[i] + (j - first[i])
    readonly double[] data;

    public int Size => n;
    public long Entries => data.LongLength;

    public SkylineCholesky(int[] firstColumn)
    {
        n = firstColumn.Length; first = (int[])firstColumn.Clone();
        start = new long[n + 1];
        for (int i = 0; i < n; i++)
        {
            if (first[i] > i || first[i] < 0) first[i] = Math.Clamp(first[i], 0, i);
            start[i + 1] = start[i] + (i - first[i] + 1);
        }
        data = new double[start[n]];
    }

    public void Add(int i, int j, double v)
    {
        if (j > i) (i, j) = (j, i);
        if (j < first[i]) throw new ArgumentOutOfRangeException(nameof(j), "スカイラインの外");
        data[start[i] + (j - first[i])] += v;
    }

    public bool Factorize()
    {
        var d = data.AsSpan();
        for (int i = 0; i < n; i++)
        {
            int fi = first[i]; long si = start[i];
            for (int j = fi; j <= i; j++)
            {
                int fj = first[j]; long sj = start[j];
                int k0 = Math.Max(fi, fj), len = j - k0;
                double sum = d[(int)(si + (j - fi))];
                if (len > 0) sum -= BandCholesky.Dot(d.Slice((int)(si + (k0 - fi)), len), d.Slice((int)(sj + (k0 - fj)), len));
                if (i == j)
                {
                    if (!(sum > 0)) return false;
                    d[(int)(si + (i - fi))] = Math.Sqrt(sum);
                }
                else d[(int)(si + (j - fi))] = sum / d[(int)(sj + (j - fj))];
            }
        }
        return true;
    }

    public void SolvePair(Span<double> bx, Span<double> by)
    {
        var d = data.AsSpan();
        for (int i = 0; i < n; i++)
        {
            int fi = first[i], len = i - fi; long si = start[i];
            var row = d.Slice((int)si, len);
            double diag = d[(int)(si + len)];
            bx[i] = (bx[i] - BandCholesky.Dot(row, bx.Slice(fi, len))) / diag;
            by[i] = (by[i] - BandCholesky.Dot(row, by.Slice(fi, len))) / diag;
        }
        for (int i = n - 1; i >= 0; i--)
        {
            int fi = first[i], len = i - fi; long si = start[i];
            double diag = d[(int)(si + len)];
            double xi = bx[i] / diag, yi = by[i] / diag;
            bx[i] = xi; by[i] = yi;
            var row = d.Slice((int)si, len);
            Axpy(-xi, row, bx.Slice(fi, len));
            Axpy(-yi, row, by.Slice(fi, len));
        }
    }

    public void Solve(Span<double> b)
    {
        var d = data.AsSpan();
        for (int i = 0; i < n; i++)
        {
            int fi = first[i], len = i - fi; long si = start[i];
            b[i] = (b[i] - BandCholesky.Dot(d.Slice((int)si, len), b.Slice(fi, len))) / d[(int)(si + len)];
        }
        for (int i = n - 1; i >= 0; i--)
        {
            int fi = first[i], len = i - fi; long si = start[i];
            double xi = b[i] / d[(int)(si + len)];
            b[i] = xi;
            Axpy(-xi, d.Slice((int)si, len), b.Slice(fi, len));
        }
    }

    static void Axpy(double a, ReadOnlySpan<double> x, Span<double> y)
    {
        int i = 0, w = Vector<double>.Count;
        var va = new Vector<double>(a);
        for (; i <= x.Length - w; i += w)
            (new Vector<double>(y.Slice(i)) + va * new Vector<double>(x.Slice(i))).CopyTo(y.Slice(i));
        for (; i < x.Length; i++) y[i] += a * x[i];
    }
}

/// <summary>帯の幅が小さくなる頂点の並べ方（Reverse Cuthill–McKee）。</summary>
internal static class Rcm
{
    /// <summary>order[k] = k 番目に置く頂点、pos[v] = 頂点 v の位置。</summary>
    public static (int[] order, int[] pos, int bandwidth) Compute(int n, int[] start, int[] index)
    {
        var order = new List<int>(n);
        var visited = new bool[n];
        var deg = new int[n];
        for (int v = 0; v < n; v++) deg[v] = start[v + 1] - start[v];
        var nbrs = new List<int>();
        while (order.Count < n)
        {
            // まだの頂点のうち次数が最小のものから（成分ごと）
            int s = -1;
            for (int v = 0; v < n; v++) if (!visited[v] && (s < 0 || deg[v] < deg[s])) s = v;
            // 擬似周辺点：BFS で一番遠い点を2回たどる
            s = Farthest(s, start, index, n);
            s = Farthest(s, start, index, n);
            int head = order.Count;
            visited[s] = true; order.Add(s);
            while (head < order.Count)
            {
                int v = order[head++];
                nbrs.Clear();
                for (int k = start[v]; k < start[v + 1]; k++) { int u = index[k]; if (!visited[u]) { visited[u] = true; nbrs.Add(u); } }
                nbrs.Sort((a, b) => deg[a].CompareTo(deg[b]));
                order.AddRange(nbrs);
            }
        }
        order.Reverse();
        var pos = new int[n];
        for (int k = 0; k < n; k++) pos[order[k]] = k;
        int bwid = 0;
        for (int v = 0; v < n; v++)
            for (int k = start[v]; k < start[v + 1]; k++) bwid = Math.Max(bwid, Math.Abs(pos[v] - pos[index[k]]));
        return (order.ToArray(), pos, bwid);
    }

    static int Farthest(int s, int[] start, int[] index, int n)
    {
        var dist = new Dictionary<int, int> { [s] = 0 };
        var q = new Queue<int>(); q.Enqueue(s);
        int last = s;
        while (q.Count > 0)
        {
            int v = q.Dequeue(); last = v;
            for (int k = start[v]; k < start[v + 1]; k++)
            {
                int u = index[k];
                if (!dist.ContainsKey(u)) { dist[u] = dist[v] + 1; q.Enqueue(u); }
            }
        }
        return last;
    }
}
