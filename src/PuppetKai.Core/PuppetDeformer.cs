using System;
using System.Collections.Generic;
using System.Numerics;

namespace PuppetKai.Core;

public enum DeformMode { Rigid = 0, Smooth = 1 }

/// <summary>1フレーム分の変形結果。</summary>
public sealed class DeformResult
{
    public required PuppetMesh Mesh { get; init; }
    public required Vector2[] Deformed { get; init; }
    public int Iterations { get; init; }
    /// <summary>変形後の網目を囲む範囲（画像の中心が原点）。</summary>
    public (float MinX, float MinY, float MaxX, float MaxY) Bounds { get; init; }
}

/// <summary>直前の SetAlpha と Deform で、段ごとにかかった時間（Stopwatch の tick）。内訳の計測用。</summary>
public struct DeformTiming
{
    public long Hash, Outline, Mesh, Field, Mls, Arap;
    public int Iterations;
    public bool OutlineRebuilt, MeshRebuilt, FieldRebuilt, Reused;
}

/// <summary>
/// 輪郭 → 網目 → 距離と分解 → 変形 の順に、入力が変わった段だけ作り直す。
/// 結果は「その時刻の値」だけで決まる（前のフレームの結果は使わない）。
/// </summary>
public sealed class PuppetDeformer
{
    AlphaOutline? outline;
    PuppetMesh? mesh;
    string meshKey = "";
    ControlField? field;
    ArapSolver? arap;
    Vector2[] fieldRest = Array.Empty<Vector2>();

    // 直前の結果（同じ入力ならそのまま返す）
    (DeformMode mode, double alpha, double tolerance, Vector2[] target)? resultKey;
    DeformResult? lastResult;

    public AlphaOutline? Outline => outline;
    public DeformTiming Timing;
    static long Now => System.Diagnostics.Stopwatch.GetTimestamp();
    public void Reset() { outline = null; mesh = null; meshKey = ""; field = null; arap = null; resultKey = null; lastResult = null; }
    public PuppetMesh? Mesh => mesh;

    /// <summary>α 値が前回と違えば輪郭を作り直す。作り直したら true。</summary>
    public bool SetAlpha(ReadOnlySpan<byte> alpha, int width, int height)
        => SetAlpha(alpha, width, height, -width / 2f, -height / 2f, 1f);

    byte[] prevAlpha = Array.Empty<byte>();
    int prevW, prevH;
    float prevOX, prevOY, prevPS;

    /// <summary>α 値と、その左上の座標・1画素の大きさ。前回と同じなら何もしない。作り直したら true。
    /// 輪郭に効くのは 0 か 0 以外かだけなので、生の値が違っても 0／0 以外が同じなら作り直さない。</summary>
    public bool SetAlpha(ReadOnlySpan<byte> alpha, int width, int height, float originX, float originY, float pixelSize)
    {
        Timing = default;
        long t0 = Now;
        bool sameFrame = outline != null && width == prevW && height == prevH && originX == prevOX && originY == prevOY && pixelSize == prevPS
            && alpha.Length == width * height && prevAlpha.Length >= alpha.Length;
        if (sameFrame)
        {
            var prev = prevAlpha.AsSpan(0, alpha.Length);
            if (alpha.SequenceEqual(prev)) { Timing.Hash = Now - t0; return false; }
            if (SameCoverage(alpha, prev)) { alpha.CopyTo(prev); Timing.Hash = Now - t0; return false; }
        }
        if (prevAlpha.Length < alpha.Length) prevAlpha = new byte[alpha.Length];
        alpha.CopyTo(prevAlpha);
        prevW = width; prevH = height; prevOX = originX; prevOY = originY; prevPS = pixelSize;
        long t1 = Now;
        Timing.Hash = t1 - t0;
        outline = AlphaOutline.Build(alpha, width, height, originX, originY, pixelSize);
        Timing.Outline = Now - t1;
        Timing.OutlineRebuilt = true;
        mesh = null; meshKey = ""; field = null; arap = null; resultKey = null;
        return true;
    }

    static bool SameCoverage(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        for (int i = 0; i < a.Length; i++) if ((a[i] != 0) != (b[i] != 0)) return false;
        return true;
    }

    public DeformResult? Deform(IReadOnlyList<CutSegment> cuts, ControlSet controls, DeformMode mode, double rigidity)
        => Deform(cuts, controls, mode, rigidity, ArapSolver.DefaultTolerance);

    /// <summary>tolerance：剛体（ARAP）の打ち切り（1回の繰り返しでの動きがこれより小さくなったら止める。px）。</summary>
    public DeformResult? Deform(IReadOnlyList<CutSegment> cuts, ControlSet controls, DeformMode mode, double rigidity, double tolerance)
    {
        if (outline == null || outline.IsEmpty) return null;
        string mk = MeshKey(cuts);
        long t0 = Now;
        if (mesh == null || mk != meshKey)
        {
            mesh = MeshBuilder.Build(outline, cuts);
            meshKey = mk; field = null; arap = null; resultKey = null;
            Timing.MeshRebuilt = true;
        }
        long t1 = Now;
        Timing.Mesh = t1 - t0;
        if (mesh == null) return null;

        if (field == null || !fieldRest.AsSpan().SequenceEqual(controls.Rest))
        {
            field = new ControlField(mesh, controls.Rest);
            fieldRest = (Vector2[])controls.Rest.Clone();
            arap = null; resultKey = null;
            Timing.FieldRebuilt = true;
        }
        double alpha = Math.Clamp(rigidity, 0.1, 8.0);
        double usedAlpha = mode == DeformMode.Smooth ? alpha : 1.0;
        double usedTol = mode == DeformMode.Smooth ? 0 : tolerance;
        if (lastResult != null && resultKey is { } rk && rk.mode == mode && rk.alpha == usedAlpha && rk.tolerance == usedTol && rk.target.AsSpan().SequenceEqual(controls.Target))
        {
            Timing.Reused = true;
            return lastResult;
        }

        int n = mesh.VertexCount;
        var mls = new Vector2[n];
        long t2 = Now;
        Timing.Field = t2 - t1;
        field.Deform(controls.Target, mode == DeformMode.Smooth ? alpha : 1.0, mls);
        long t3 = Now;
        Timing.Mls = t3 - t2;
        Vector2[] deformed;
        int iterations = 0;
        if (mode == DeformMode.Smooth || controls.Count == 0)
        {
            deformed = mls;
        }
        else
        {
            arap ??= new ArapSolver(mesh, field.Attach);
            arap.Tolerance = tolerance;
            deformed = new Vector2[n];
            arap.Solve(mls, controls.Target, deformed);
            iterations = arap.LastIterations;
        }
        Timing.Arap = Now - t3;
        Timing.Iterations = iterations;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in deformed) { minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); }
        lastResult = new DeformResult { Mesh = mesh, Deformed = deformed, Iterations = iterations, Bounds = (minX, minY, maxX, maxY) };
        resultKey = (mode, usedAlpha, usedTol, (Vector2[])controls.Target.Clone());
        return lastResult;
    }

    static string MeshKey(IReadOnlyList<CutSegment> cuts)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in cuts) sb.Append(c.Start.X).Append(',').Append(c.Start.Y).Append(',').Append(c.End.X).Append(',').Append(c.End.Y).Append(';');
        return sb.ToString();
    }

}
