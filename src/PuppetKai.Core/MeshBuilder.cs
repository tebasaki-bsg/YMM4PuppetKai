using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace PuppetKai.Core;

/// <summary>輪郭と切れ込みに沿った網目を作る（仕様書 6.1）。</summary>
public static class MeshBuilder
{
    public const int MaxTriangles = 5440;
    /// <summary>間隔を決めるときに目安にする三角形の数（上限より少なめにして速くする）。</summary>
    public const int TargetTriangles = 3000;
    public const float MinSpacing = 8f;

    public static PuppetMesh? Build(AlphaOutline outline, IReadOnlyList<CutSegment> cuts, int maxTriangles = MaxTriangles)
    {
        if (outline.IsEmpty) return null;
        double area = outline.FilledPixels;
        float s = (float)Math.Max(MinSpacing, Math.Sqrt(area / (0.433 * Math.Min(maxTriangles, TargetTriangles))));
        var usable = cuts.Where(c => Vector2.DistanceSquared(c.Start, c.End) > 0.01f).ToList();
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var mesh = BuildWithSpacing(outline, usable, s);
            if (mesh == null) return null;
            if (mesh.TriangleCount <= maxTriangles) return mesh;
            s *= 1.12f;
        }
        return BuildWithSpacing(outline, usable, s);
    }

    sealed class SegmentGrid
    {
        readonly float x0, y0, cell; readonly int w, h;
        readonly List<(Vector2 a, Vector2 b)>[] cells;
        public SegmentGrid(float minX, float minY, float maxX, float maxY, float cell)
        {
            this.cell = cell; x0 = minX - cell; y0 = minY - cell;
            w = (int)((maxX - x0) / cell) + 3; h = (int)((maxY - y0) / cell) + 3;
            cells = new List<(Vector2, Vector2)>[w * h];
        }
        int Cx(float x) => Math.Clamp((int)((x - x0) / cell), 0, w - 1);
        int Cy(float y) => Math.Clamp((int)((y - y0) / cell), 0, h - 1);
        public void Add(Vector2 a, Vector2 b)
        {
            int xa = Cx(Math.Min(a.X, b.X)), xb = Cx(Math.Max(a.X, b.X)), ya = Cy(Math.Min(a.Y, b.Y)), yb = Cy(Math.Max(a.Y, b.Y));
            for (int y = ya; y <= yb; y++) for (int x = xa; x <= xb; x++) (cells[y * w + x] ??= new()).Add((a, b));
        }
        public float MinDistSq(Vector2 p)
        {
            float best = float.MaxValue;
            int cx = Cx(p.X), cy = Cy(p.Y);
            for (int y = Math.Max(0, cy - 1); y <= Math.Min(h - 1, cy + 1); y++)
                for (int x = Math.Max(0, cx - 1); x <= Math.Min(w - 1, cx + 1); x++)
                {
                    var l = cells[y * w + x]; if (l == null) continue;
                    foreach (var (a, b) in l) best = Math.Min(best, PuppetMesh.DistSegSq(p, a, b));
                }
            return best;
        }
    }

    static bool SegIntersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d, out float t, out float u)
    {
        var r = b - a; var s = d - c;
        float den = r.X * s.Y - r.Y * s.X;
        t = u = 0;
        if (MathF.Abs(den) < 1e-9f) return false;
        var ac = c - a;
        t = (ac.X * s.Y - ac.Y * s.X) / den;
        u = (ac.X * r.Y - ac.Y * r.X) / den;
        return t >= 0 && t <= 1 && u >= 0 && u <= 1;
    }

    static PuppetMesh? BuildWithSpacing(AlphaOutline outline, List<CutSegment> cuts, float s)
    {
        // --- 必ず辺にする線（輪郭・切れ込み）を、区切りの位置で分ける ---
        var loopBreaks = outline.Loops.Select(l => Enumerable.Range(0, l.Length).Select(_ => new List<float> { 0f, 1f }).ToArray()).ToArray();
        var cutBreaks = cuts.Select(_ => new List<float> { 0f, 1f }).ToList();
        for (int c = 0; c < cuts.Count; c++)
        {
            for (int li = 0; li < outline.Loops.Count; li++)
            {
                var loop = outline.Loops[li];
                for (int i = 0; i < loop.Length; i++)
                    if (SegIntersect(cuts[c].Start, cuts[c].End, loop[i], loop[(i + 1) % loop.Length], out var t, out var u))
                    {
                        cutBreaks[c].Add(t);
                        loopBreaks[li][i].Add(u);
                    }
            }
            for (int d = c + 1; d < cuts.Count; d++)
                if (SegIntersect(cuts[c].Start, cuts[c].End, cuts[d].Start, cuts[d].End, out var t, out var u))
                {
                    cutBreaks[c].Add(t); cutBreaks[d].Add(u);
                }
        }

        var points = new List<Vector2>();
        var constraints = new List<(int a, int b)>();
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var l in outline.Loops) foreach (var p in l) { minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); }
        var grid = new SegmentGrid(minX, minY, maxX, maxY, s);

        void AddPolyline(Vector2 a, Vector2 b, float step)
        {
            float len = Vector2.Distance(a, b);
            int n = Math.Max(1, (int)MathF.Ceiling(len / step));
            int prev = points.Count; points.Add(a);
            for (int k = 1; k <= n; k++)
            {
                points.Add(Vector2.Lerp(a, b, k / (float)n));
                constraints.Add((points.Count - 2, points.Count - 1));
            }
            grid.Add(a, b);
        }

        for (int li = 0; li < outline.Loops.Count; li++)
        {
            var loop = outline.Loops[li];
            for (int i = 0; i < loop.Length; i++)
            {
                var a = loop[i]; var b = loop[(i + 1) % loop.Length];
                var br = loopBreaks[li][i].Distinct().OrderBy(x => x).ToList();
                for (int k = 0; k + 1 < br.Count; k++)
                {
                    var p0 = Vector2.Lerp(a, b, br[k]); var p1 = Vector2.Lerp(a, b, br[k + 1]);
                    if (Vector2.DistanceSquared(p0, p1) < 1e-6f) continue;
                    AddPolyline(p0, p1, s);
                }
            }
        }
        for (int c = 0; c < cuts.Count; c++)
        {
            var br = cutBreaks[c].Distinct().OrderBy(x => x).ToList();
            var a = cuts[c].Start; var b = cuts[c].End;
            for (int k = 0; k + 1 < br.Count; k++)
            {
                var p0 = Vector2.Lerp(a, b, br[k]); var p1 = Vector2.Lerp(a, b, br[k + 1]);
                if (Vector2.DistanceSquared(p0, p1) < 1e-6f) continue;
                if (!outline.Contains((p0 + p1) * 0.5f)) continue;
                AddPolyline(p0, p1, s * 0.7f);
            }
        }

        // --- 内側の点（ほぼ正三角形の格子） ---
        int constraintPointCount = points.Count;
        float rowH = s * 0.8660254f, keep = 0.45f * s;
        int row = 0;
        for (float y = minY + rowH * 0.5f; y < maxY; y += rowH, row++)
        {
            float off = (row & 1) == 0 ? 0 : s * 0.5f;
            for (float x = minX + off + s * 0.25f; x < maxX; x += s)
            {
                var p = new Vector2(x, y);
                if (!outline.Contains(p)) continue;
                if (grid.MinDistSq(p) < keep * keep) continue;
                points.Add(p);
            }
        }

        // --- 三角形分割 ---
        var dt = new Delaunay(minX - s, minY - s, maxX + s, maxY + s);
        var map = new int[points.Count];
        // 入れる順：行ごとに左右へ蛇行（歩いて探す距離を短くする）
        var order = Enumerable.Range(0, points.Count)
            .OrderBy(i => (int)((points[i].Y - minY) / (s * 4)))
            .ThenBy(i => (((int)((points[i].Y - minY) / (s * 4))) & 1) == 0 ? points[i].X : -points[i].X)
            .ToArray();
        foreach (int i in order) map[i] = dt.Insert(points[i].X, points[i].Y);

        var chains = new List<List<int>>();
        for (int pass = 0; pass < 6; pass++)
        {
            bool changed = false;
            chains.Clear();
            foreach (var (a, b) in constraints)
            {
                int va = map[a], vb = map[b];
                if (va == vb) continue;
                var chain = new List<int> { va };
                if (!dt.HasEdge(va, vb)) changed = true;
                dt.Enforce(va, vb, chain);
                chains.Add(chain);
            }
            if (!changed) break;
        }

        // --- 輪郭の内側の三角形だけ残す ---
        var used = new Dictionary<int, int>();
        var verts = new List<Vector2>();
        var tris = new List<int>();
        foreach (var (a, b, c) in dt.Triangles())
        {
            var pa = new Vector2((float)dt.X[a], (float)dt.Y[a]);
            var pb = new Vector2((float)dt.X[b], (float)dt.Y[b]);
            var pc = new Vector2((float)dt.X[c], (float)dt.Y[c]);
            var cen = (pa + pb + pc) / 3f;
            if (!outline.Contains(cen)) continue;
            float area2 = (pb.X - pa.X) * (pc.Y - pa.Y) - (pb.Y - pa.Y) * (pc.X - pa.X);
            if (MathF.Abs(area2) < 1e-6f) continue;
            foreach (int v in new[] { a, b, c })
                if (!used.ContainsKey(v)) { used[v] = verts.Count; verts.Add(new Vector2((float)dt.X[v], (float)dt.Y[v])); }
            tris.Add(used[a]); tris.Add(used[b]); tris.Add(used[c]);
        }
        if (tris.Count == 0) return null;

        var triArr = tris.ToArray();
        var vertList = verts;
        SplitAlongCuts(vertList, triArr, cuts, outline, s);
        return new PuppetMesh(vertList.ToArray(), triArr, s, cuts);
    }

    /// <summary>
    /// 切れ込みの線の上の頂点を複製し、線の両側の三角形が頂点を共有しないようにする。
    /// 頂点のまわりの三角形を「切れ込みの辺をまたがずにたどれる」まとまりに分け、まとまりごとに別の頂点を持たせる。
    /// こうすると、折れ線の曲がり角・切れ込みどうしの交点は分かれ、輪郭の内側で止まった端（切れ込みの辺が1本だけ）は
    /// つながったまま残る（閉じた端）。
    /// </summary>
    static void SplitAlongCuts(List<Vector2> verts, int[] tris, List<CutSegment> cuts, AlphaOutline outline, float s)
    {
        if (cuts.Count == 0) return;
        int nt = tris.Length / 3, nv = verts.Count;
        float eps = Math.Max(1e-3f, s * 1e-4f), eps2 = eps * eps;

        // 切れ込みの線分の上にある頂点と、その線分の番号
        var onCut = new List<int>?[nv];
        for (int c = 0; c < cuts.Count; c++)
        {
            var a = cuts[c].Start; var b = cuts[c].End;
            float x0 = Math.Min(a.X, b.X) - eps, x1 = Math.Max(a.X, b.X) + eps, y0 = Math.Min(a.Y, b.Y) - eps, y1 = Math.Max(a.Y, b.Y) + eps;
            for (int v = 0; v < nv; v++)
            {
                var p = verts[v];
                if (p.X < x0 || p.X > x1 || p.Y < y0 || p.Y > y1) continue;
                if (PuppetMesh.DistSegSq(p, a, b) <= eps2) (onCut[v] ??= new List<int>(2)).Add(c);
            }
        }
        bool IsCutEdge(int a, int b)
        {
            var la = onCut[a]; var lb = onCut[b];
            if (la == null || lb == null) return false;
            foreach (int x in la) if (lb.Contains(x)) return true;
            return false;
        }

        var incident = new List<int>?[nv];
        for (int t = 0; t < nt; t++)
            for (int k = 0; k < 3; k++) (incident[tris[3 * t + k]] ??= new List<int>(6)).Add(t);
        var orig = (int[])tris.Clone();

        for (int v = 0; v < nv; v++)
        {
            if (onCut[v] == null) continue;
            var list = incident[v];
            if (list == null || list.Count < 2) continue;
            int m = list.Count;
            var parent = new int[m];
            for (int i = 0; i < m; i++) parent[i] = i;
            int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
            for (int i = 0; i < m; i++)
                for (int j = i + 1; j < m; j++)
                {
                    int w = SharedOther(orig, list[i], list[j], v);
                    if (w < 0 || IsCutEdge(v, w)) continue;
                    int ri = Find(i), rj = Find(j);
                    if (ri != rj) parent[ri] = rj;
                }
            int keep = Find(0);
            var copies = new Dictionary<int, int>();
            for (int i = 0; i < m; i++)
            {
                int r = Find(i);
                if (r == keep) continue;
                if (!copies.TryGetValue(r, out int copy)) { copy = verts.Count; verts.Add(verts[v]); copies[r] = copy; }
                int t = list[i];
                for (int k = 0; k < 3; k++) if (orig[3 * t + k] == v) tris[3 * t + k] = copy;
            }
        }
    }

    /// <summary>三角形 t1・t2 がどちらも v を含むとき、v 以外に共有している頂点（無ければ -1）。</summary>
    static int SharedOther(int[] tris, int t1, int t2, int v)
    {
        for (int a = 0; a < 3; a++)
        {
            int w = tris[3 * t1 + a];
            if (w == v) continue;
            for (int b = 0; b < 3; b++) if (tris[3 * t2 + b] == w) return w;
        }
        return -1;
    }
}
