using System;
using System.Collections.Generic;
using System.Numerics;

namespace PuppetKai.Core;

/// <summary>
/// 剛体（ARAP）変形（仕様書 6.3）。初期値 → 相似変換の範囲で形を保つ解 → 局所（回転）と大域（位置）の
/// 繰り返し（Anderson 加速）。分解は網目と制御点の変形前の位置が同じあいだ使い回す。
/// </summary>
public sealed class ArapSolver
{
    public const double PinWeight = 1e4;
    public const double AnchorWeight = 1e-4;
    public const double DefaultTolerance = 0.02;
    public const int DefaultMaxIterations = 60;
    /// <summary>頂点の移動量の最大値（|Δx|＋|Δy|）がこれ未満になったら打ち切る。</summary>
    public double Tolerance { get; set; } = DefaultTolerance;
    public int MaxIterations { get; set; } = DefaultMaxIterations;
    const int AndersonDepth = 5;

    readonly PuppetMesh mesh;
    readonly Attachment[] attach;
    readonly int n;
    readonly int[] pos;            // 頂点 → 並べ替え後の位置
    readonly int[] triEdgeA, triEdgeB; readonly double[] triEdgeW; // 三角形ごとに3辺（3t+k）
    int[] ePA = null!, ePB = null!; double[] ePX = null!, ePY = null!; // 並べ替え後の位置と、変形前の辺のベクトル
    readonly SkylineCholesky global;
    readonly SkylineCholesky similarity;
    public int Bandwidth { get; }
    public int LastIterations { get; private set; }
    public long MatrixEntries => global.Entries;

    public ArapSolver(PuppetMesh mesh, Attachment[] attach)
    {
        this.mesh = mesh; this.attach = attach; n = mesh.VertexCount;
        var (_, p, bw) = Rcm.Compute(n, mesh.NeighborStart, mesh.NeighborIndex);
        pos = p; Bandwidth = bw;

        int nt = mesh.TriangleCount;
        triEdgeA = new int[3 * nt]; triEdgeB = new int[3 * nt]; triEdgeW = new double[3 * nt];
        var rest = mesh.Rest; var tri = mesh.Triangles;
        for (int t = 0; t < nt; t++)
            for (int k = 0; k < 3; k++)
            {
                int a = tri[3 * t + (k + 1) % 3], b = tri[3 * t + (k + 2) % 3], o = tri[3 * t + k];
                var ea = rest[a] - rest[o]; var eb = rest[b] - rest[o];
                double cross = Math.Abs(ea.X * eb.Y - ea.Y * eb.X);
                double cot = cross > 1e-12 ? (ea.X * eb.X + ea.Y * eb.Y) / cross : 0;
                triEdgeA[3 * t + k] = a; triEdgeB[3 * t + k] = b;
                triEdgeW[3 * t + k] = Math.Clamp(0.5 * cot, 0.01, 5.0);
            }

        ePA = new int[3 * nt]; ePB = new int[3 * nt]; ePX = new double[3 * nt]; ePY = new double[3 * nt];
        for (int e = 0; e < 3 * nt; e++)
        {
            ePA[e] = pos[triEdgeA[e]]; ePB[e] = pos[triEdgeB[e]];
            ePX[e] = rest[triEdgeA[e]].X - rest[triEdgeB[e]].X; ePY[e] = rest[triEdgeA[e]].Y - rest[triEdgeB[e]].Y;
        }

        // 大域の行列
        // 行ごとの最初の列（並べ替え後の隣の位置の最小）
        var firstV = new int[n];
        for (int v = 0; v < n; v++)
        {
            int f = pos[v];
            for (int k = mesh.NeighborStart[v]; k < mesh.NeighborStart[v + 1]; k++) f = Math.Min(f, pos[mesh.NeighborIndex[k]]);
            firstV[pos[v]] = f;
        }
        global = new SkylineCholesky(firstV);
        for (int e = 0; e < triEdgeA.Length; e++)
        {
            int a = pos[triEdgeA[e]], b = pos[triEdgeB[e]]; double w = triEdgeW[e];
            global.Add(a, a, w); global.Add(b, b, w); global.Add(a, b, -w);
        }
        foreach (var at in attach) AddPin(global, at, 1, 0);
        for (int v = 0; v < n; v++) global.Add(pos[v], pos[v], AnchorWeight);
        if (!global.Factorize()) throw new InvalidOperationException("ARAP の行列を分解できませんでした");

        // 相似変換の段階（x,y を交互に並べた 2n）
        var first2 = new int[2 * n];
        for (int k = 0; k < n; k++) { first2[2 * k] = 2 * firstV[k]; first2[2 * k + 1] = 2 * firstV[k]; }
        similarity = new SkylineCholesky(first2);
        Span<double> C = stackalloc double[12]; // Ci, Cj, Ck（各 2x2）
        for (int t = 0; t < nt; t++)
            for (int k = 0; k < 3; k++)
            {
                int vi = tri[3 * t + k], vj = tri[3 * t + (k + 1) % 3], vk = tri[3 * t + (k + 2) % 3];
                var e = rest[vj] - rest[vi]; var r = rest[vk] - rest[vi];
                double l2 = e.LengthSquared(); if (l2 < 1e-12) continue;
                double x = (r.X * e.X + r.Y * e.Y) / l2;
                double y = (r.X * -e.Y + r.Y * e.X) / l2;  // J e = (-e.y, e.x)
                // Ci = -(1-x)I + yJ, Cj = -xI - yJ, Ck = I。J = [[0,-1],[1,0]]
                SetBlock(C, 0, -(1 - x), -y, y, -(1 - x));
                SetBlock(C, 1, -x, y, -y, -x);
                SetBlock(C, 2, 1, 0, 0, 1);
                int[] vs = { vi, vj, vk };
                for (int p1 = 0; p1 < 3; p1++)
                    for (int p2 = 0; p2 < 3; p2++)
                        for (int r1 = 0; r1 < 2; r1++)       // A^T A の (p1,r1),(p2,r2) 成分
                            for (int r2 = 0; r2 < 2; r2++)
                            {
                                int gi = 2 * pos[vs[p1]] + r1, gj = 2 * pos[vs[p2]] + r2;
                                if (gj > gi) continue;
                                double s = 0;
                                for (int row = 0; row < 2; row++)
                                    s += C[p1 * 4 + row * 2 + r1] * C[p2 * 4 + row * 2 + r2];
                                if (s != 0) similarity.Add(gi, gj, s);
                            }
            }
        foreach (var at in attach) { AddPin(similarity, at, 2, 0); AddPin(similarity, at, 2, 1); }
        for (int i = 0; i < 2 * n; i++) similarity.Add(i, i, AnchorWeight);
        if (!similarity.Factorize()) throw new InvalidOperationException("相似変換の行列を分解できませんでした");
    }

    static void SetBlock(Span<double> c, int block, double m00, double m01, double m10, double m11)
    {
        c[block * 4] = m00; c[block * 4 + 1] = m01; c[block * 4 + 2] = m10; c[block * 4 + 3] = m11;
    }

    void AddPin(SkylineCholesky m, Attachment at, int dim, int comp)
    {
        Span<int> v = stackalloc int[] { at.V0, at.V1, at.V2 };
        Span<double> b = stackalloc double[] { at.B0, at.B1, at.B2 };
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                int gi = dim * pos[v[i]] + comp, gj = dim * pos[v[j]] + comp;
                if (gj > gi) continue;
                if (gi == gj && i != j && v[i] == v[j]) { m.Add(gi, gj, PinWeight * b[i] * b[j]); continue; }
                m.Add(gi, gj, PinWeight * b[i] * b[j]);
            }
    }

    /// <summary>init（MLS の結果）から解く。結果は result に入る。</summary>
    public void Solve(ReadOnlySpan<Vector2> init, ReadOnlySpan<Vector2> targets, Span<Vector2> result)
        => Solve(init, targets, result, default);

    /// <summary>start を渡すと、相似変換の段階を飛ばしてそこから繰り返しを始める（前のフレームの結果など）。
    /// 収束先（init へのつなぎ止めを含むエネルギーの最小）は同じで、途中の道筋だけが変わる。</summary>
    public void Solve(ReadOnlySpan<Vector2> init, ReadOnlySpan<Vector2> targets, Span<Vector2> result, ReadOnlySpan<Vector2> start)
    {
        var rest = mesh.Rest;
        bool warm = start.Length == n;
        // --- 第1段階：相似変換 ---
        var b2 = new double[2 * n];
        for (int v = 0; v < n; v++) { b2[2 * pos[v]] = AnchorWeight * init[v].X; b2[2 * pos[v] + 1] = AnchorWeight * init[v].Y; }
        for (int c = 0; c < attach.Length; c++)
        {
            var at = attach[c]; var q = targets[c];
            b2[2 * pos[at.V0]] += PinWeight * at.B0 * q.X; b2[2 * pos[at.V0] + 1] += PinWeight * at.B0 * q.Y;
            b2[2 * pos[at.V1]] += PinWeight * at.B1 * q.X; b2[2 * pos[at.V1] + 1] += PinWeight * at.B1 * q.Y;
            b2[2 * pos[at.V2]] += PinWeight * at.B2 * q.X; b2[2 * pos[at.V2] + 1] += PinWeight * at.B2 * q.Y;
        }
        if (warm) for (int v = 0; v < n; v++) { b2[2 * pos[v]] = start[v].X; b2[2 * pos[v] + 1] = start[v].Y; }
        else similarity.Solve(b2);

        // 以降は並べ替え後の順で x[2k], x[2k+1] に持つ
        int dim = 2 * n;
        var x = new double[dim];
        Array.Copy(b2, x, dim);
        var initP = new double[dim];
        for (int v = 0; v < n; v++) { initP[2 * pos[v]] = init[v].X; initP[2 * pos[v] + 1] = init[v].Y; }

        // 定数の右辺（ピンと初期値へのつなぎ止め）
        var constX = new double[n]; var constY = new double[n];
        for (int k = 0; k < n; k++) { constX[k] = AnchorWeight * initP[2 * k]; constY[k] = AnchorWeight * initP[2 * k + 1]; }
        for (int c = 0; c < attach.Length; c++)
        {
            var at = attach[c]; var q = targets[c];
            constX[pos[at.V0]] += PinWeight * at.B0 * q.X; constY[pos[at.V0]] += PinWeight * at.B0 * q.Y;
            constX[pos[at.V1]] += PinWeight * at.B1 * q.X; constY[pos[at.V1]] += PinWeight * at.B1 * q.Y;
            constX[pos[at.V2]] += PinWeight * at.B2 * q.X; constY[pos[at.V2]] += PinWeight * at.B2 * q.Y;
        }

        int nt = mesh.TriangleCount;
        var cosT = new double[nt]; var sinT = new double[nt];
        var g = new double[dim]; var f = new double[dim];
        var gPrev = new double[dim]; var fPrev = new double[dim];
        var dF = new List<double[]>(); var dG = new List<double[]>();
        var bx = new double[n]; var by = new double[n];
        double ePrev = double.MaxValue; bool accelerated = false, havePrev = false;
        var lastPlain = new double[dim];
        Array.Copy(x, lastPlain, dim);
        int it = 0;
        for (; it < MaxIterations; it++)
        {
            double e = Local(x, cosT, sinT, targets, initP);
            if (accelerated && e > ePrev)
            {
                // 加速でエネルギーが増えた：通常の反復の結果に戻してやり直す
                Array.Copy(lastPlain, x, dim);
                e = Local(x, cosT, sinT, targets, initP);
                dF.Clear(); dG.Clear(); havePrev = false;
            }
            ePrev = e;
            Global(cosT, sinT, constX, constY, bx, by, g);
            Array.Copy(g, lastPlain, dim);
            double maxMove = 0;
            for (int k = 0; k < n; k++) maxMove = Math.Max(maxMove, Math.Abs(g[2 * k] - x[2 * k]) + Math.Abs(g[2 * k + 1] - x[2 * k + 1]));
            if (maxMove < Tolerance) { it++; break; }
            for (int k = 0; k < dim; k++) f[k] = g[k] - x[k];
            if (havePrev)
            {
                var df = new double[dim]; var dg = new double[dim];
                for (int k = 0; k < dim; k++) { df[k] = f[k] - fPrev[k]; dg[k] = g[k] - gPrev[k]; }
                dF.Add(df); dG.Add(dg);
                if (dF.Count > AndersonDepth) { dF.RemoveAt(0); dG.RemoveAt(0); }
            }
            Array.Copy(f, fPrev, dim); Array.Copy(g, gPrev, dim); havePrev = true;
            int m = dF.Count;
            if (m == 0) { Array.Copy(g, x, dim); accelerated = false; continue; }
            var A = new double[m, m]; var rhs = new double[m];
            for (int i = 0; i < m; i++)
            {
                for (int j = 0; j <= i; j++) { double s = BandCholesky.Dot(dF[i], dF[j]); A[i, j] = A[j, i] = s; }
                rhs[i] = BandCholesky.Dot(dF[i], f);
            }
            for (int i = 0; i < m; i++) A[i, i] += 1e-10 * (A[i, i] + 1);
            var gamma = SolveSmall(A, rhs, m);
            if (gamma == null) { Array.Copy(g, x, dim); accelerated = false; dF.Clear(); dG.Clear(); havePrev = false; continue; }
            for (int k = 0; k < dim; k++)
            {
                double v = g[k];
                for (int i = 0; i < m; i++) v -= gamma[i] * dG[i][k];
                x[k] = v;
            }
            accelerated = true;
        }
        LastIterations = it;
        for (int v = 0; v < n; v++) result[v] = new Vector2((float)lastPlain[2 * pos[v]], (float)lastPlain[2 * pos[v] + 1]);
    }

    /// <summary>三角形ごとの最適な回転を求め、そのときの ARAP エネルギー（ピン・つなぎ止めを含む）を返す。</summary>
    double Local(double[] x, double[] cosT, double[] sinT, ReadOnlySpan<Vector2> targets, double[] initP)
    {
        var rest = mesh.Rest; int nt = mesh.TriangleCount;
        double energy = 0;
        for (int t = 0; t < nt; t++)
        {
            double s00 = 0, s01 = 0, s10 = 0, s11 = 0, sq = 0;
            for (int e = 3 * t; e < 3 * t + 3; e++)
            {
                double w = triEdgeW[e], px = ePX[e], py = ePY[e];
                int pa = ePA[e], pb = ePB[e];
                double qx = x[2 * pa] - x[2 * pb], qy = x[2 * pa + 1] - x[2 * pb + 1];
                s00 += w * px * qx; s01 += w * px * qy; s10 += w * py * qx; s11 += w * py * qy;
                sq += w * (qx * qx + qy * qy + px * px + py * py);
            }
            double ca = s00 + s11, sb = s01 - s10, r = Math.Sqrt(ca * ca + sb * sb);
            if (r > 1e-300) { cosT[t] = ca / r; sinT[t] = sb / r; } else { cosT[t] = 1; sinT[t] = 0; }
            energy += sq - 2 * r;
        }
        for (int i = 0; i < attach.Length; i++)
        {
            var at = attach[i];
            double ax = at.B0 * x[2 * pos[at.V0]] + at.B1 * x[2 * pos[at.V1]] + at.B2 * x[2 * pos[at.V2]] - targets[i].X;
            double ay = at.B0 * x[2 * pos[at.V0] + 1] + at.B1 * x[2 * pos[at.V1] + 1] + at.B2 * x[2 * pos[at.V2] + 1] - targets[i].Y;
            energy += PinWeight * (ax * ax + ay * ay);
        }
        for (int k = 0; k < 2 * n; k++) { double d = x[k] - initP[k]; energy += AnchorWeight * d * d; }
        return energy;
    }

    void Global(double[] cosT, double[] sinT, double[] constX, double[] constY, double[] bx, double[] by, double[] outX)
    {
        var rest = mesh.Rest; int nt = mesh.TriangleCount;
        Array.Copy(constX, bx, n); Array.Copy(constY, by, n);
        for (int t = 0; t < nt; t++)
        {
            double c = cosT[t], s = sinT[t];
            for (int e = 3 * t; e < 3 * t + 3; e++)
            {
                double w = triEdgeW[e], px = ePX[e], py = ePY[e];
                double rx = w * (c * px - s * py), ry = w * (s * px + c * py);
                int pa = ePA[e], pb = ePB[e];
                bx[pa] += rx; by[pa] += ry; bx[pb] -= rx; by[pb] -= ry;
            }
        }
        global.SolvePair(bx, by);
        for (int k = 0; k < n; k++) { outX[2 * k] = bx[k]; outX[2 * k + 1] = by[k]; }
    }

    static double[]? SolveSmall(double[,] A, double[] b, int n)
    {
        var M = (double[,])A.Clone(); var x = (double[])b.Clone();
        for (int i = 0; i < n; i++)
        {
            int p = i;
            for (int r = i + 1; r < n; r++) if (Math.Abs(M[r, i]) > Math.Abs(M[p, i])) p = r;
            if (Math.Abs(M[p, i]) < 1e-300) return null;
            if (p != i) { for (int c = 0; c < n; c++) (M[i, c], M[p, c]) = (M[p, c], M[i, c]); (x[i], x[p]) = (x[p], x[i]); }
            for (int r = i + 1; r < n; r++)
            {
                double f = M[r, i] / M[i, i];
                for (int c = i; c < n; c++) M[r, c] -= f * M[i, c];
                x[r] -= f * x[i];
            }
        }
        for (int i = n - 1; i >= 0; i--)
        {
            double v = x[i];
            for (int c = i + 1; c < n; c++) v -= M[i, c] * x[c];
            x[i] = v / M[i, i];
        }
        return x;
    }
}
