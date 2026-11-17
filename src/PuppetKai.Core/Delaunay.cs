using System;
using System.Collections.Generic;

namespace PuppetKai.Core;

/// <summary>
/// 点を1つずつ足す Delaunay 三角形分割（Bowyer–Watson）。必ず辺にしたい線分は、
/// 辺にならなければ中点を足して分割する（conforming）。
/// </summary>
internal sealed class Delaunay
{
    public readonly List<double> X = new();
    public readonly List<double> Y = new();
    // 三角形 t の頂点 V[3t+i]、頂点 i の向かいの隣 N[3t+i]（無ければ -1）
    readonly List<int> V = new();
    readonly List<int> N = new();
    readonly List<bool> alive = new();
    readonly Stack<int> free = new();
    readonly List<int> vertTri = new();
    int lastTri;

    public int SuperCount => 3;

    public Delaunay(double minX, double minY, double maxX, double maxY)
    {
        double dx = maxX - minX, dy = maxY - minY, d = Math.Max(dx, dy) * 20 + 100;
        double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
        AddVertex(cx - d, cy - d);
        AddVertex(cx + d, cy - d);
        AddVertex(cx, cy + d);
        int t = NewTri(0, 1, 2);
        if (Orient(0, 1, 2) < 0) { V[3 * t + 1] = 2; V[3 * t + 2] = 1; }
        lastTri = t;
    }

    int AddVertex(double x, double y)
    {
        X.Add(x); Y.Add(y); vertTri.Add(-1);
        return X.Count - 1;
    }

    int NewTri(int a, int b, int c)
    {
        int t;
        if (free.Count > 0)
        {
            t = free.Pop();
            V[3 * t] = a; V[3 * t + 1] = b; V[3 * t + 2] = c;
            N[3 * t] = N[3 * t + 1] = N[3 * t + 2] = -1;
            alive[t] = true;
        }
        else
        {
            t = alive.Count;
            V.Add(a); V.Add(b); V.Add(c);
            N.Add(-1); N.Add(-1); N.Add(-1);
            alive.Add(true);
        }
        vertTri[a] = t; vertTri[b] = t; vertTri[c] = t;
        return t;
    }

    double Orient(int a, int b, int c)
        => (X[b] - X[a]) * (Y[c] - Y[a]) - (Y[b] - Y[a]) * (X[c] - X[a]);

    static double Orient(double ax, double ay, double bx, double by, double cx, double cy)
        => (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);

    bool InCircle(int t, double px, double py)
    {
        int a = V[3 * t], b = V[3 * t + 1], c = V[3 * t + 2];
        double adx = X[a] - px, ady = Y[a] - py;
        double bdx = X[b] - px, bdy = Y[b] - py;
        double cdx = X[c] - px, cdy = Y[c] - py;
        double det = (adx * adx + ady * ady) * (bdx * cdy - cdx * bdy)
                   - (bdx * bdx + bdy * bdy) * (adx * cdy - cdx * ady)
                   + (cdx * cdx + cdy * cdy) * (adx * bdy - bdx * ady);
        return det > 1e-12 * (Math.Abs(adx) + Math.Abs(bdx) + Math.Abs(cdx) + 1) * (Math.Abs(ady) + Math.Abs(bdy) + Math.Abs(cdy) + 1);
    }

    int Locate(double px, double py)
    {
        int t = lastTri;
        if (t < 0 || t >= alive.Count || !alive[t]) { t = 0; while (!alive[t]) t++; }
        int guard = 0, limit = alive.Count * 3 + 16;
        while (guard++ < limit)
        {
            bool moved = false;
            for (int i = 0; i < 3; i++)
            {
                int a = V[3 * t + (i + 1) % 3], b = V[3 * t + (i + 2) % 3];
                if (Orient(X[a], Y[a], X[b], Y[b], px, py) < 0)
                {
                    int n = N[3 * t + i];
                    if (n >= 0) { t = n; moved = true; break; }
                }
            }
            if (!moved) return t;
        }
        // 歩き方が回ってしまったときは全部調べる
        for (int s = 0; s < alive.Count; s++)
        {
            if (!alive[s]) continue;
            bool ok = true;
            for (int i = 0; i < 3 && ok; i++)
            {
                int a = V[3 * s + (i + 1) % 3], b = V[3 * s + (i + 2) % 3];
                if (Orient(X[a], Y[a], X[b], Y[b], px, py) < -1e-9) ok = false;
            }
            if (ok) return s;
        }
        return t;
    }

    readonly List<int> cavity = new();
    readonly HashSet<int> inCavity = new();
    readonly Stack<int> stack = new();
    readonly Dictionary<int, int> startAt = new();
    readonly Dictionary<int, int> endAt = new();

    /// <summary>点を足す。ほぼ同じ位置の頂点があれば、その番号を返す。</summary>
    public int Insert(double px, double py)
    {
        int t0 = Locate(px, py);
        for (int i = 0; i < 3; i++)
        {
            int v = V[3 * t0 + i];
            double ddx = X[v] - px, ddy = Y[v] - py;
            if (ddx * ddx + ddy * ddy < 1e-8) return v;
        }
        int p = AddVertex(px, py);
        cavity.Clear(); inCavity.Clear(); stack.Clear();
        stack.Push(t0); inCavity.Add(t0);
        while (stack.Count > 0)
        {
            int t = stack.Pop();
            cavity.Add(t);
            for (int i = 0; i < 3; i++)
            {
                int n = N[3 * t + i];
                if (n < 0 || inCavity.Contains(n)) continue;
                if (InCircle(n, px, py)) { inCavity.Add(n); stack.Push(n); }
            }
        }
        // 境界の辺ごとに新しい三角形
        startAt.Clear(); endAt.Clear();
        var boundary = new List<(int a, int b, int outside, int outsideSlot)>();
        foreach (int t in cavity)
            for (int i = 0; i < 3; i++)
            {
                int n = N[3 * t + i];
                if (n >= 0 && inCavity.Contains(n)) continue;
                int a = V[3 * t + (i + 1) % 3], b = V[3 * t + (i + 2) % 3];
                int slot = -1;
                if (n >= 0) for (int k = 0; k < 3; k++) if (N[3 * n + k] == t) { slot = k; break; }
                boundary.Add((a, b, n, slot));
            }
        foreach (int t in cavity) { alive[t] = false; free.Push(t); }
        var created = new List<int>(boundary.Count);
        foreach (var (a, b, outside, slot) in boundary)
        {
            int nt = NewTri(a, b, p);
            N[3 * nt + 2] = outside;
            if (outside >= 0 && slot >= 0) N[3 * outside + slot] = nt;
            startAt[a] = nt; endAt[b] = nt;
            created.Add(nt);
        }
        foreach (int nt in created)
        {
            int a = V[3 * nt], b = V[3 * nt + 1];
            // 頂点 a の向かい = 辺 (b,p) を共有する三角形 = b から始まる三角形
            N[3 * nt] = startAt.TryGetValue(b, out var tb) ? tb : -1;
            // 頂点 b の向かい = 辺 (p,a) を共有する三角形 = a で終わる三角形
            N[3 * nt + 1] = endAt.TryGetValue(a, out var ta) ? ta : -1;
        }
        lastTri = created.Count > 0 ? created[0] : lastTri;
        return p;
    }

    /// <summary>頂点 a と b を結ぶ辺があるか。</summary>
    public bool HasEdge(int a, int b)
    {
        int t0 = vertTri[a];
        if (t0 < 0 || !alive[t0] || !Has(t0, a)) t0 = FindTriWith(a);
        if (t0 < 0) return false;
        // a を含む三角形を隣づたいにたどる
        var seen = new HashSet<int>();
        var st = new Stack<int>();
        st.Push(t0); seen.Add(t0);
        while (st.Count > 0)
        {
            int t = st.Pop();
            if (Has(t, b)) return true;
            for (int i = 0; i < 3; i++)
            {
                int n = N[3 * t + i];
                if (n >= 0 && alive[n] && !seen.Contains(n) && Has(n, a)) { seen.Add(n); st.Push(n); }
            }
        }
        return false;
    }

    bool Has(int t, int v) => V[3 * t] == v || V[3 * t + 1] == v || V[3 * t + 2] == v;

    int FindTriWith(int v)
    {
        for (int t = 0; t < alive.Count; t++) if (alive[t] && Has(t, v)) { vertTri[v] = t; return t; }
        return -1;
    }

    /// <summary>線分 (a,b) が辺になるまで中点を足す。線分上に足した頂点を順に返す。</summary>
    public void Enforce(int a, int b, List<int> chainOut, int depth = 0)
    {
        if (HasEdge(a, b) || depth > 14)
        {
            chainOut.Add(b);
            return;
        }
        int m = Insert((X[a] + X[b]) / 2, (Y[a] + Y[b]) / 2);
        if (m == a || m == b) { chainOut.Add(b); return; }
        Enforce(a, m, chainOut, depth + 1);
        Enforce(m, b, chainOut, depth + 1);
    }

    /// <summary>生きている三角形（超三角形の頂点を含むものを除く）。</summary>
    public IEnumerable<(int a, int b, int c)> Triangles()
    {
        for (int t = 0; t < alive.Count; t++)
        {
            if (!alive[t]) continue;
            int a = V[3 * t], b = V[3 * t + 1], c = V[3 * t + 2];
            if (a < 3 || b < 3 || c < 3) continue;
            yield return (a, b, c);
        }
    }
}
