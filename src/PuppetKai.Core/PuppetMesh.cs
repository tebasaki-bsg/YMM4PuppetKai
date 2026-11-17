using System;
using System.Collections.Generic;
using System.Numerics;

namespace PuppetKai.Core;

/// <summary>切れ込み（線分）。座標は画像の中心が原点。</summary>
public readonly record struct CutSegment(Vector2 Start, Vector2 End);

/// <summary>制御点を網目の三角形に結び付けたもの（重心座標。網目の外なら外挿）。</summary>
public readonly record struct Attachment(int V0, int V1, int V2, float B0, float B1, float B2)
{
    public Vector2 Eval(ReadOnlySpan<Vector2> p) => p[V0] * B0 + p[V1] * B1 + p[V2] * B2;
}

/// <summary>変形に使う網目（変形前）。</summary>
public sealed class PuppetMesh
{
    public Vector2[] Rest { get; }
    /// <summary>三角形の頂点番号（3つずつ）。</summary>
    public int[] Triangles { get; }
    public int VertexCount => Rest.Length;
    public int TriangleCount => Triangles.Length / 3;
    public float Spacing { get; }
    public IReadOnlyList<CutSegment> Cuts { get; }

    /// <summary>辺（a &lt; b）。</summary>
    public (int A, int B)[] Edges { get; }
    /// <summary>隣の頂点（CSR）。</summary>
    public int[] NeighborStart { get; }
    public int[] NeighborIndex { get; }
    public float[] NeighborLength { get; }
    /// <summary>つながった部分ごとの番号。</summary>
    public int[] Component { get; }
    public int ComponentCount { get; }

    // 三角形を探すための格子
    readonly float gx0, gy0, cell;
    readonly int gw, gh;
    readonly int[] cellStart, cellItems;

    public PuppetMesh(Vector2[] rest, int[] triangles, float spacing, IReadOnlyList<CutSegment> cuts)
    {
        Rest = rest; Triangles = triangles; Spacing = spacing; Cuts = cuts;
        int n = rest.Length;
        var set = new HashSet<long>();
        var edges = new List<(int, int)>();
        for (int t = 0; t < triangles.Length; t += 3)
            for (int k = 0; k < 3; k++)
            {
                int a = triangles[t + k], b = triangles[t + (k + 1) % 3];
                if (a > b) (a, b) = (b, a);
                if (set.Add(((long)a << 32) | (uint)b)) edges.Add((a, b));
            }
        Edges = edges.ToArray();
        var deg = new int[n + 1];
        foreach (var (a, b) in Edges) { deg[a]++; deg[b]++; }
        NeighborStart = new int[n + 1];
        for (int i = 0; i < n; i++) NeighborStart[i + 1] = NeighborStart[i] + deg[i];
        NeighborIndex = new int[NeighborStart[n]];
        NeighborLength = new float[NeighborStart[n]];
        var fill = new int[n];
        foreach (var (a, b) in Edges)
        {
            float len = Vector2.Distance(rest[a], rest[b]);
            int ia = NeighborStart[a] + fill[a]++, ib = NeighborStart[b] + fill[b]++;
            NeighborIndex[ia] = b; NeighborLength[ia] = len;
            NeighborIndex[ib] = a; NeighborLength[ib] = len;
        }
        Component = new int[n];
        Array.Fill(Component, -1);
        int cc = 0;
        var q = new Queue<int>();
        for (int s = 0; s < n; s++)
        {
            if (Component[s] >= 0) continue;
            Component[s] = cc; q.Enqueue(s);
            while (q.Count > 0)
            {
                int v = q.Dequeue();
                for (int k = NeighborStart[v]; k < NeighborStart[v + 1]; k++)
                {
                    int u = NeighborIndex[k];
                    if (Component[u] < 0) { Component[u] = cc; q.Enqueue(u); }
                }
            }
            cc++;
        }
        ComponentCount = cc;

        // 格子
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in rest) { minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); }
        if (n == 0) { minX = minY = 0; maxX = maxY = 1; }
        cell = Math.Max(spacing * 2, 1f);
        gx0 = minX; gy0 = minY;
        gw = Math.Max(1, (int)((maxX - minX) / cell) + 1);
        gh = Math.Max(1, (int)((maxY - minY) / cell) + 1);
        var lists = new List<int>[gw * gh];
        for (int t = 0; t < TriangleCount; t++)
        {
            var a = rest[triangles[3 * t]]; var b = rest[triangles[3 * t + 1]]; var c = rest[triangles[3 * t + 2]];
            int x0 = CellX(Math.Min(a.X, Math.Min(b.X, c.X))), x1 = CellX(Math.Max(a.X, Math.Max(b.X, c.X)));
            int y0 = CellY(Math.Min(a.Y, Math.Min(b.Y, c.Y))), y1 = CellY(Math.Max(a.Y, Math.Max(b.Y, c.Y)));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    (lists[y * gw + x] ??= new List<int>()).Add(t);
        }
        cellStart = new int[gw * gh + 1];
        var items = new List<int>();
        for (int i = 0; i < lists.Length; i++) { cellStart[i] = items.Count; if (lists[i] != null) items.AddRange(lists[i]); }
        cellStart[lists.Length] = items.Count;
        cellItems = items.ToArray();
    }

    int CellX(float x) => Math.Clamp((int)((x - gx0) / cell), 0, gw - 1);
    int CellY(float y) => Math.Clamp((int)((y - gy0) / cell), 0, gh - 1);

    /// <summary>
    /// 点を含む三角形の重心座標。切れ込みの線の上の点は、始点から終点を見て右側の三角形に付ける。
    /// 網目の外なら、一番近い三角形で外挿する。
    /// </summary>
    public Attachment Locate(Vector2 p)
    {
        if (TriangleCount == 0) return default;
        foreach (var cut in Cuts)
        {
            var d = cut.End - cut.Start; float len = d.Length();
            if (len < 1e-4f) continue;
            var u = d / len;
            var rel = p - cut.Start;
            float t = Vector2.Dot(rel, u), s = u.X * rel.Y - u.Y * rel.X;
            if (t >= -0.01f && t <= len + 0.01f && MathF.Abs(s) < 0.02f)
            {
                // 右側（Y 下向きの座標で (-dy, dx) の側）へ少しずらす
                var right = new Vector2(-u.Y, u.X);
                p += right * (0.05f - s);
                break;
            }
        }
        int cx = CellX(p.X), cy = CellY(p.Y);
        bool inGrid = p.X >= gx0 && p.Y >= gy0 && p.X <= gx0 + gw * cell && p.Y <= gy0 + gh * cell;
        if (inGrid)
        {
            int ci = cy * gw + cx;
            int bestT = -1; float bestMin = float.MinValue; (float, float, float) bestB = default;
            for (int k = cellStart[ci]; k < cellStart[ci + 1]; k++)
            {
                int t = cellItems[k];
                var b = Bary(t, p);
                float mn = Math.Min(b.Item1, Math.Min(b.Item2, b.Item3));
                if (mn > bestMin) { bestMin = mn; bestT = t; bestB = b; }
            }
            if (bestT >= 0 && bestMin >= -1e-4f)
                return new Attachment(Triangles[3 * bestT], Triangles[3 * bestT + 1], Triangles[3 * bestT + 2], bestB.Item1, bestB.Item2, bestB.Item3);
        }
        // 外：一番近い三角形
        int nearT = 0; float nearD = float.MaxValue;
        for (int t = 0; t < TriangleCount; t++)
        {
            float dd = DistToTriangleSq(t, p);
            if (dd < nearD) { nearD = dd; nearT = t; }
        }
        var bb = Bary(nearT, p);
        return new Attachment(Triangles[3 * nearT], Triangles[3 * nearT + 1], Triangles[3 * nearT + 2], bb.Item1, bb.Item2, bb.Item3);
    }

    (float, float, float) Bary(int t, Vector2 p)
    {
        var a = Rest[Triangles[3 * t]]; var b = Rest[Triangles[3 * t + 1]]; var c = Rest[Triangles[3 * t + 2]];
        var v0 = b - a; var v1 = c - a; var v2 = p - a;
        float den = v0.X * v1.Y - v1.X * v0.Y;
        if (MathF.Abs(den) < 1e-12f) return (1, 0, 0);
        float l1 = (v2.X * v1.Y - v1.X * v2.Y) / den;
        float l2 = (v0.X * v2.Y - v2.X * v0.Y) / den;
        return (1 - l1 - l2, l1, l2);
    }

    float DistToTriangleSq(int t, Vector2 p)
    {
        var b = Bary(t, p);
        if (b.Item1 >= 0 && b.Item2 >= 0 && b.Item3 >= 0) return 0;
        float best = float.MaxValue;
        for (int k = 0; k < 3; k++)
        {
            var a = Rest[Triangles[3 * t + k]]; var c = Rest[Triangles[3 * t + (k + 1) % 3]];
            best = Math.Min(best, DistSegSq(p, a, c));
        }
        return best;
    }

    internal static float DistSegSq(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a; float l = ab.LengthSquared();
        float t = l > 0 ? Math.Clamp(Vector2.Dot(p - a, ab) / l, 0, 1) : 0;
        return Vector2.DistanceSquared(p, a + ab * t);
    }
}
