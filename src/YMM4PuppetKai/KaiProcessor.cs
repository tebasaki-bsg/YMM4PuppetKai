using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using PuppetKai.Core;
using Vortice;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Player.Video.Effects;

namespace YMM4PuppetKai;

/// <summary>
/// 毎フレーム：値を読む → 入力の不透明な部分を読み戻す → 網目を変形する → 三角形の頂点を GPU へ送る。
/// 入力・値が前のフレームと同じなら、変形も送り直しもしない。
/// </summary>
internal sealed class KaiProcessor : VideoEffectProcessorBase
{
    const int ReadbackMaxSize = 2048;

    readonly PuppetKaiEffect item;
    ID2D1DeviceContext? dc;
    KaiMeshEffect? mesh;
    readonly PuppetDeformer deformer = new();
    DeformResult? uploaded;
    RawRectF uploadedIdentity;
    byte[] alphaBuffer = Array.Empty<byte>();
    ID2D1Bitmap1? rbTarget, rbRead;   // 読み戻し用（大きさが変わったときだけ作り直す）
    int rbW, rbH;
    bool loggedError;

    public KaiProcessor(IGraphicsDevicesAndContext devices, PuppetKaiEffect item) : base(devices)
    {
        this.item = item;
    }

    protected override ID2D1Image? CreateEffect(IGraphicsDevicesAndContext devices)
    {
        dc = devices.DeviceContext;
        if (!KaiShaders.TryEnsure(out var shaderError))
        {
            KaiLog.Error("シェーダーを用意できませんでした（エフェクトは何もしません）", shaderError);
            return null;
        }
        try
        {
            mesh = new KaiMeshEffect(devices);
            if (!mesh.IsEnabled) { mesh.Dispose(); mesh = null; return null; }
            disposer.Collect(mesh);
            var output = mesh.Output;
            disposer.Collect(output);
            return output;
        }
        catch (Exception ex)
        {
            KaiLog.Error("エフェクトを作れませんでした", ex);
            mesh = null;
            return null;
        }
    }

    protected override void setInput(ID2D1Image? input)
    {
        mesh?.SetInput(0, input, true);
        uploaded = null; uploadedIdentity = default;
    }

    protected override void ClearEffectChain()
    {
        mesh?.SetInput(0, null, true);
        uploaded = null; uploadedIdentity = default;
    }

    public override DrawDescription Update(EffectDescription desc)
    {
        if (IsPassThroughEffect || mesh == null || dc == null || input == null) return desc.DrawDescription;
        try
        {
            UpdateCore(desc);
        }
        catch (Exception ex)
        {
            if (!loggedError) { KaiLog.Error("変形の計算に失敗しました", ex); loggedError = true; }
            uploaded = null;
        }
        return desc.DrawDescription;
    }

    readonly long[] stage = new long[KaiStats.StageCount];
    static long Now => System.Diagnostics.Stopwatch.GetTimestamp();

    void UpdateCore(EffectDescription desc)
    {
        Array.Clear(stage);
        long tStart = Now;
        bool deformed = false;
        try { UpdateTimed(desc, ref deformed); }
        finally
        {
            stage[KaiStats.Total] = Now - tStart;
        }
    }

    void UpdateTimed(EffectDescription desc, ref bool deformed)
    {
        long tBegin = Now;
        long frame = desc.ItemPosition.Frame, length = desc.ItemDuration.Frame;
        int fps = desc.FPS;
        double t = fps > 0 ? frame / (double)fps : 0;

        // --- 値 ---
        var bones = item.Bones;
        int nb = Math.Min(bones.Count, PuppetKaiEffect.BoneCapacity);
        var boneIndex = new Dictionary<Guid, int>(nb);
        for (int i = 0; i < nb; i++) boneIndex.TryAdd(bones[i].Id, i);
        var boneInputs = new List<BoneInput>(nb);
        for (int i = 0; i < nb; i++)
        {
            var b = bones[i];
            int parent = b.ParentId != Guid.Empty && boneIndex.TryGetValue(b.ParentId, out var pi) ? pi : -1;
            boneInputs.Add(new BoneInput(b.IsEnabled, new Vector2((float)b.JointX, (float)b.JointY), parent,
                b.Angle.GetValue(frame, length, fps), b.Scale.GetValue(frame, length, fps),
                b.SwingAngle.GetValue(frame, length, fps), b.SwingPeriod.GetValue(frame, length, fps), b.SwingPhase,
                b.SwingPropagation.GetValue(frame, length, fps), b.Whip.GetValue(frame, length, fps)));
        }
        var pins = item.Pins;
        int np = Math.Min(pins.Count, PuppetKaiEffect.PinCapacity);
        var pinInputs = new List<PinInput>(np);
        for (int i = 0; i < np; i++)
        {
            var p = pins[i];
            int bi = p.BoneId != Guid.Empty && boneIndex.TryGetValue(p.BoneId, out var x) ? x : -1;
            pinInputs.Add(new PinInput(p.IsEnabled, new Vector2((float)p.RestX, (float)p.RestY),
                new Vector2((float)p.MoveX.GetValue(frame, length, fps), (float)p.MoveY.GetValue(frame, length, fps)), bi));
        }
        var controls = Rig.Compute(pinInputs, boneInputs, t);
        var cuts = new List<CutSegment>();
        int cutCount = 0;
        foreach (var c in item.Cuts)
        {
            if (cutCount++ >= PuppetKaiEffect.CutCapacity) break;
            if (!c.IsEnabled) continue;
            // 折れ線は、隣り合う点を結ぶ線分の並び（始点から終点の向き）として渡す
            var pts = c.Points;
            int np2 = Math.Min(pts.Count, KaiCut.MaxPoints);
            for (int k = 0; k + 1 < np2; k++)
                cuts.Add(new CutSegment(new Vector2((float)pts[k].X, (float)pts[k].Y), new Vector2((float)pts[k + 1].X, (float)pts[k + 1].Y)));
        }
        var mode = item.Mode == KaiDeformMode.Smooth ? DeformMode.Smooth : DeformMode.Rigid;
        double stiffness = item.Stiffness.GetValue(frame, length, fps);
        long tValues = Now;
        stage[KaiStats.Values] = tValues - tBegin;

        // --- 入力の不透明な部分（毎フレーム確かめる） ---
        var bounds = dc!.GetImageLocalBounds(input!);
        float w = bounds.Right - bounds.Left, h = bounds.Bottom - bounds.Top;
        if (!(w >= 1 && h >= 1) || !float.IsFinite(w) || !float.IsFinite(h))
        {
            UploadIdentity(bounds);
            return;
        }
        float scale = Math.Min(1f, ReadbackMaxSize / Math.Max(w, h));
        int pw = Math.Max(1, (int)MathF.Ceiling(w * scale)), ph = Math.Max(1, (int)MathF.Ceiling(h * scale));
        if (!ReadbackAlpha(bounds, pw, ph, scale))
        {
            UploadIdentity(bounds);
            return;
        }
        deformer.SetAlpha(alphaBuffer.AsSpan(0, pw * ph), pw, ph, bounds.Left, bounds.Top, 1f / scale);

        // 再生中だけ「プレビュー精度」、停止中と書き出しは「書き出し精度」（使い方は名前で見る）
        var precision = desc.Usage.ToString() == "Playing" ? item.PlaybackPrecision : item.RenderPrecision;
        var result = deformer.Deform(cuts, controls, mode, stiffness, precision.ToTolerance());
        deformed = true;
        var dt = deformer.Timing;
        stage[KaiStats.Hash] = dt.Hash; stage[KaiStats.Outline] = dt.Outline; stage[KaiStats.Mesh] = dt.Mesh;
        stage[KaiStats.Field] = dt.Field; stage[KaiStats.Mls] = dt.Mls; stage[KaiStats.Arap] = dt.Arap;
        if (result == null)
        {
            UploadIdentity(bounds);
            return;
        }
        if (ReferenceEquals(result, uploaded)) return;
        long tUp = Now;
        Upload(result);
        stage[KaiStats.Upload] = Now - tUp;
    }

    void Upload(DeformResult r)
    {
        var m = r.Mesh;
        int nt = Math.Min(m.TriangleCount, KaiMeshEffect.MaxTriangles);
        var bytes = new byte[nt * 3 * KaiMeshEffect.VertexStride];
        var f = MemoryMarshal.Cast<byte, float>(bytes.AsSpan());
        for (int t = 0, o = 0; t < nt; t++)
            for (int k = 0; k < 3; k++)
            {
                int v = m.Triangles[3 * t + k];
                f[o++] = r.Deformed[v].X; f[o++] = r.Deformed[v].Y;
                f[o++] = m.Rest[v].X; f[o++] = m.Rest[v].Y;
            }
        mesh!.VertexData = bytes;
        mesh.SetOutputBounds(r.Bounds.MinX, r.Bounds.MinY, r.Bounds.MaxX, r.Bounds.MaxY);
        mesh.VertexCount = nt * 3;
        uploaded = r; uploadedIdentity = default;
    }

    /// <summary>変形できないとき：入力をそのまま描く（2つの三角形）。</summary>
    void UploadIdentity(RawRectF b)
    {
        if (uploaded == null && uploadedIdentity.Equals(b)) return;
        float l = b.Left, tp = b.Top, r = b.Right, bt = b.Bottom;
        if (!(r > l && bt > tp)) { mesh!.VertexCount = 0; mesh.SetOutputBounds(0, 0, 0, 0); uploaded = null; uploadedIdentity = b; return; }
        var pts = new[] { l, tp, r, tp, r, bt, l, tp, r, bt, l, bt };
        var bytes = new byte[6 * KaiMeshEffect.VertexStride];
        var f = MemoryMarshal.Cast<byte, float>(bytes.AsSpan());
        for (int i = 0; i < 6; i++) { f[4 * i] = pts[2 * i]; f[4 * i + 1] = pts[2 * i + 1]; f[4 * i + 2] = pts[2 * i]; f[4 * i + 3] = pts[2 * i + 1]; }
        mesh!.VertexData = bytes;
        mesh.SetOutputBounds(l, tp, r, bt);
        mesh.VertexCount = 6;
        uploaded = null; uploadedIdentity = b;
    }

    /// <summary>入力を A8 の画像に描いて CPU へ読み戻す（α &gt; 0 を 255 にする）。</summary>
    unsafe bool ReadbackAlpha(RawRectF bounds, int pw, int ph, float scale)
    {
        if (alphaBuffer.Length < pw * ph) alphaBuffer = new byte[pw * ph];
        var ctx = dc!;
        var targetProps = new BitmapProperties1(new Vortice.DCommon.PixelFormat(Format.A8_UNorm, Vortice.DCommon.AlphaMode.Straight), ctx.Dpi.Width, ctx.Dpi.Height, BitmapOptions.Target);
        var readProps = new BitmapProperties1(new Vortice.DCommon.PixelFormat(Format.A8_UNorm, Vortice.DCommon.AlphaMode.Straight), ctx.Dpi.Width, ctx.Dpi.Height, BitmapOptions.CannotDraw | BitmapOptions.CpuRead);
        if (rbTarget == null || rbRead == null || rbW != pw || rbH != ph)
        {
            rbTarget?.Dispose(); rbRead?.Dispose();
            rbTarget = ctx.CreateBitmap(new SizeI(pw, ph), IntPtr.Zero, 0, targetProps);
            rbRead = ctx.CreateBitmap(new SizeI(pw, ph), IntPtr.Zero, 0, readProps);
            rbW = pw; rbH = ph;
        }
        var target = rbTarget; var read = rbRead;
        var oldTarget = ctx.Target;
        var oldTransform = ctx.Transform;
        try
        {
            long tDraw = Now;
            ctx.Target = target;
            ctx.Transform = Matrix3x2.CreateTranslation(-bounds.Left, -bounds.Top) * Matrix3x2.CreateScale(scale);
            ctx.BeginDraw();
            ctx.Clear(new Color4(0, 0, 0, 0));
            ctx.DrawImage(input!, null, null, InterpolationMode.NearestNeighbor, CompositeMode.SourceCopy);
            ctx.EndDraw().CheckError();
            stage[KaiStats.RbDraw] = Now - tDraw;
        }
        finally
        {
            ctx.Transform = oldTransform;
            ctx.Target = oldTarget;
            oldTarget?.Dispose();
        }
        long tWait = Now;
        read.CopyFromBitmap(target);
        var map = read.Map(MapOptions.Read);
        long tCopy = Now;
        stage[KaiStats.RbWait] = tCopy - tWait;
        try
        {
            byte* bits = (byte*)map.Bits;
            fixed (byte* dst = alphaBuffer)
            {
                if (map.Pitch == pw) Buffer.MemoryCopy(bits, dst, alphaBuffer.Length, (long)pw * ph);
                else
                    for (int y = 0; y < ph; y++)
                        Buffer.MemoryCopy(bits + (long)y * map.Pitch, dst + (long)y * pw, pw, pw);
            }
        }
        finally { read.Unmap(); }
        stage[KaiStats.RbCopy] = Now - tCopy;
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            rbTarget?.Dispose(); rbRead?.Dispose(); rbTarget = rbRead = null;
            mesh = null; dc = null; uploaded = null;
        }
        base.Dispose(disposing);
    }
}
