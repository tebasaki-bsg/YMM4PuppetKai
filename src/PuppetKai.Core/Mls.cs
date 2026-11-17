using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

namespace PuppetKai.Core;

/// <summary>
/// 制御点から網目の各頂点までの距離（網目の辺をたどった最短距離）と、
/// それを使った MLS 剛体変形（仕様書 6.2）。
/// </summary>
public sealed class ControlField
{
    public PuppetMesh Mesh { get; }
    public Vector2[] ControlRest { get; }
    public Attachment[] Attach { get; }
    /// <summary>距離 [制御点 * 頂点数 + 頂点]。届かない頂点は +∞。</summary>
    public float[] Distance { get; }
    /// <summary>どの制御点からも網目をたどって届かない頂点（ピンもボーンも無いかたまり）。</summary>
    public bool[] Unreached { get; }

    public ControlField(PuppetMesh mesh, Vector2[] controlRest)
    {
        Mesh = mesh; ControlRest = controlRest;
        int n = mesh.VertexCount, m = controlRest.Length;
        Attach = new Attachment[m];
        for (int i = 0; i < m; i++) Attach[i] = mesh.Locate(controlRest[i]);
        Distance = new float[(long)m * n];
        Parallel.For(0, m, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) },
            i => Dijkstra(mesh, controlRest[i], Attach[i], Distance.AsSpan((int)((long)i * n), n)));
        Unreached = new bool[n];
        for (int v = 0; v < n; v++)
        {
            bool any = false;
            for (int i = 0; i < m && !any; i++) any = !float.IsPositiveInfinity(Distance[(long)i * n + v]);
            Unreached[v] = !any;
        }
    }

    static void Dijkstra(PuppetMesh mesh, Vector2 c, Attachment at, Span<float> dist)
    {
        dist.Fill(float.PositiveInfinity);
        var pq = new PriorityQueue<int, float>();
        foreach (int sv in new[] { at.V0, at.V1, at.V2 })
        {
            float d0 = Vector2.Distance(mesh.Rest[sv], c);
            if (d0 < dist[sv]) { dist[sv] = d0; pq.Enqueue(sv, d0); }
        }
        var st = mesh.NeighborStart; var idx = mesh.NeighborIndex; var len = mesh.NeighborLength;
        while (pq.TryDequeue(out int v, out float d))
        {
            if (d > dist[v]) continue;
            for (int k = st[v]; k < st[v + 1]; k++)
            {
                int u = idx[k]; float nd = d + len[k];
                if (nd < dist[u]) { dist[u] = nd; pq.Enqueue(u, nd); }
            }
        }
    }

    /// <summary>
    /// MLS 剛体変形を各頂点に適用する。重み w = (d_min² / max(d², d_min²))^α。
    /// 網目をたどって届かない頂点は直線距離で計算する（仕様書 5.2）。
    /// </summary>
    public void Deform(ReadOnlySpan<Vector2> target, double alpha, Span<Vector2> result)
    {
        int n = Mesh.VertexCount, m = ControlRest.Length;
        var rest = Mesh.Rest;
        if (m == 0) { rest.AsSpan().CopyTo(result); return; }
        if (m == 1)
        {
            var dv = target[0] - ControlRest[0];
            for (int v = 0; v < n; v++) result[v] = rest[v] + dv;
            return;
        }
        var ctrl = ControlRest; var dist = Distance; var unreached = Unreached;
        var tgt = target.ToArray();
        var res = new Vector2[n];
        Parallel.For(0, (n + 255) / 256, blk =>
        {
            Span<double> w = m <= 512 ? stackalloc double[m] : new double[m];
            int v0 = blk * 256, v1 = Math.Min(n, v0 + 256);
            for (int v = v0; v < v1; v++)
            {
                var p = rest[v];
                bool euclid = unreached[v];
                double dmin = double.MaxValue; int nearest = 0;
                for (int i = 0; i < m; i++)
                {
                    double d = euclid ? Vector2.Distance(p, ctrl[i]) : dist[(long)i * n + v];
                    w[i] = d;
                    if (d < dmin) { dmin = d; nearest = i; }
                }
                if (dmin < 1e-4)
                {
                    res[v] = p + (tgt[nearest] - ctrl[nearest]);
                    continue;
                }
                double dmin2 = dmin * dmin;
                double sw = 0, psx = 0, psy = 0, qsx = 0, qsy = 0;
                for (int i = 0; i < m; i++)
                {
                    double d = w[i];
                    double wi = double.IsPositiveInfinity(d) ? 0 : Math.Pow(dmin2 / Math.Max(d * d, dmin2), alpha);
                    w[i] = wi; sw += wi;
                    psx += wi * ctrl[i].X; psy += wi * ctrl[i].Y;
                    qsx += wi * tgt[i].X; qsy += wi * tgt[i].Y;
                }
                psx /= sw; psy /= sw; qsx /= sw; qsy /= sw;
                double a = 0, b = 0;
                for (int i = 0; i < m; i++)
                {
                    double wi = w[i]; if (wi == 0) continue;
                    double phx = ctrl[i].X - psx, phy = ctrl[i].Y - psy;
                    double qhx = tgt[i].X - qsx, qhy = tgt[i].Y - qsy;
                    a += wi * (phx * qhx + phy * qhy);
                    b += wi * (phx * qhy - phy * qhx);
                }
                double r = Math.Sqrt(a * a + b * b);
                double c = r > 1e-12 ? a / r : 1, s = r > 1e-12 ? b / r : 0;
                double dx = p.X - psx, dy = p.Y - psy;
                res[v] = new Vector2((float)(qsx + c * dx - s * dy), (float)(qsy + s * dx + c * dy));
            }
        });
        res.AsSpan().CopyTo(result);
    }
}
