using System;
using System.IO;
using System.Runtime.InteropServices;
using Vortice;
using Vortice.D3DCompiler;
using Vortice.Direct2D1;
using Vortice.DXGI;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;

namespace YMM4PuppetKai;

/// <summary>シェーダーを実行時にコンパイルして持っておく（YMM4 同梱の D3DCompiler を使う）。</summary>
internal static class KaiShaders
{
    static byte[]? vs, ps;
    static readonly object gate = new();

    static Exception? failure;

    /// <summary>コンパイル済みのシェーダー（TryEnsure が true を返した後だけ使う）。</summary>
    public static byte[] VertexShader => vs ?? Array.Empty<byte>();
    public static byte[] PixelShader => ps ?? Array.Empty<byte>();

    /// <summary>
    /// シェーダーを用意する。失敗しても例外は投げない（Direct2D の呼び出しの中で例外を出すと描画が止まるため、
    /// エフェクトを作る前にここで済ませる）。
    /// </summary>
    public static bool TryEnsure(out Exception? error)
    {
        lock (gate)
        {
            if (vs != null && ps != null) { error = null; return true; }
            if (failure != null) { error = failure; return false; }
            try
            {
                vs = Compile("MeshVS.hlsl", "vs_4_1");
                ps = Compile("MeshPS.hlsl", "ps_4_1");
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                vs = ps = null; failure = ex; error = ex;
                return false;
            }
        }
    }

    static byte[] Compile(string name, string profile)
    {
        using var s = typeof(KaiShaders).Assembly.GetManifestResourceStream("YMM4PuppetKai.Shaders." + name)
            ?? throw new InvalidOperationException("シェーダーが見つかりません: " + name);
        using var r = new StreamReader(s);
        string src = r.ReadToEnd();
        // D3DCompiler には ANSI 文字列として渡るので、ASCII 以外が混ざると壊れる（Shift-JIS の 0x5C など）
        foreach (char ch in src) if (ch > 0x7F) throw new InvalidOperationException("シェーダーのソースに ASCII 以外の文字があります: " + name);
        var result = Compiler.Compile(src, "main", name, profile, out var blob, out var err);
        try
        {
            if (result.Failure || blob == null)
                throw new InvalidOperationException($"シェーダーのコンパイルに失敗しました（{name}）: {(err != null ? Marshal.PtrToStringAnsi(err.BufferPointer) : result.ToString())}");
            return blob.AsBytes();
        }
        finally
        {
            blob?.Dispose();
            err?.Dispose();
        }
    }
}

/// <summary>変形後の網目に、変形前の画像を貼って描く Direct2D エフェクト。</summary>
internal sealed class KaiMeshEffect : D2D1CustomShaderEffectBase
{
    public const int MaxTriangles = 5440;
    public const int MaxVertices = MaxTriangles * 3;
    public const int VertexStride = 16;
    public const int VertexBufferBytes = MaxVertices * VertexStride;

    [CustomEffect(1, null, null, null, null)]
    sealed class Impl : D2D1CustomShaderEffectImplBase<Impl>
    {
        static readonly Guid VertexShaderGuid = Guid.NewGuid();
        ID2D1VertexBuffer? vertexBuffer;
        readonly byte[] data = new byte[VertexBufferBytes];
        int vertexCount, uploadedBytes;
        bool dirty = true;
        float outL, outT, outR, outB;
        RawRect lastOutput;

        public Impl() : base(KaiShaders.PixelShader) { }

        [CustomEffectProperty(PropertyType.Float, 0)]
        public float VertexCount
        {
            get => vertexCount;
            set { vertexCount = Math.Clamp((int)value, 0, MaxVertices); UpdateConstants(); }
        }

        [CustomEffectProperty(PropertyType.Float, 1)] public float OutLeft { get => outL; set => outL = value; }
        [CustomEffectProperty(PropertyType.Float, 2)] public float OutTop { get => outT; set => outT = value; }
        [CustomEffectProperty(PropertyType.Float, 3)] public float OutRight { get => outR; set => outR = value; }
        [CustomEffectProperty(PropertyType.Float, 4)] public float OutBottom { get => outB; set => outB = value; }

        [CustomEffectProperty(PropertyType.Blob, 5)]
        public byte[] VertexData
        {
            get => data;
            set
            {
                if (value == null) return;
                int n = Math.Min(value.Length, data.Length);
                Array.Copy(value, data, n);
                uploadedBytes = n; dirty = true;
                UpdateConstants();
            }
        }

        [CustomEffectProperty(PropertyType.Bool, 6)]
        public bool ReleaseResources
        {
            get => vertexBuffer == null;
            set { if (value) { vertexBuffer?.Dispose(); vertexBuffer = null; } }
        }

        public override void Initialize(ID2D1EffectContext effectContext, ID2D1TransformGraph transformGraph)
        {
            // Direct2D から呼ばれる。例外を外へ出さない
            base.Initialize(effectContext, transformGraph);
            try
            {
                var vs = KaiShaders.VertexShader;
                if (vs.Length == 0) return;
                effectContext.LoadVertexShader(VertexShaderGuid, vs, vs.Length);
                vertexBuffer = effectContext.CreateVertexBuffer(
                    new VertexBufferProperties(1, VertexUsage.Dynamic, new byte[VertexBufferBytes], VertexBufferBytes),
                    null,
                    new CustomVertexBufferProperties(vs, VertexStride,
                        new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
                        new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 0, 8)));
            }
            catch (Exception ex)
            {
                vertexBuffer = null;
                KaiLog.Error("頂点シェーダーの準備に失敗しました", ex);
            }
        }

        public override void SetDrawInfo(ID2D1DrawInfo drawInfo)
        {
            base.SetDrawInfo(drawInfo);
            UpdateConstants();
        }

        protected override void UpdateConstants()
        {
            if (drawInformation == null || vertexBuffer == null) return;
            try
            {
                if (dirty)
                {
                    vertexBuffer.Map(out var ptr, VertexBufferBytes);
                    try
                    {
                        int n = Math.Clamp(uploadedBytes, 0, VertexBufferBytes);
                        if (n > 0) Marshal.Copy(data, 0, ptr, n);
                    }
                    finally { vertexBuffer.Unmap(); }
                    dirty = false;
                }
                if (vertexCount > 0)
                    drawInformation.SetVertexProcessing(vertexBuffer, VertexOptions.None, null, new VertexRange(0, vertexCount), VertexShaderGuid);
            }
            catch (Exception ex)
            {
                KaiLog.Error("頂点データの転送に失敗しました", ex);
            }
        }

        public override void MapInputRectsToOutputRect(RawRect[] inputRects, RawRect[] inputOpaqueSubRects, out RawRect outputRect, out RawRect outputOpaqueSubRect)
        {
            inputRect = inputRects.Length > 0 ? inputRects[0] : default;
            if (outR > outL && outB > outT)
            {
                outputRect = new RawRect((int)MathF.Floor(outL) - 1, (int)MathF.Floor(outT) - 1, (int)MathF.Ceiling(outR) + 1, (int)MathF.Ceiling(outB) + 1);
            }
            else outputRect = inputRect;
            lastOutput = outputRect;
            outputOpaqueSubRect = default;
        }

        public override void MapOutputRectToInputRects(RawRect outputRect, RawRect[] inputRects)
        {
            // 三角形は入力のどこからでも画素を取るので、入力全体が要る
            if (inputRects.Length > 0)
                inputRects[0] = new RawRect(inputRect.Left - 2, inputRect.Top - 2, inputRect.Right + 2, inputRect.Bottom + 2);
        }

        public override RawRect MapInvalidRect(int inputIndex, RawRect invalidInputRect) => lastOutput;

        protected override void DisposeCore(bool disposing)
        {
            if (disposing) { vertexBuffer?.Dispose(); vertexBuffer = null; }
            base.DisposeCore(disposing);
        }
    }

    public KaiMeshEffect(IGraphicsDevicesAndContext devices) : base(Create<Impl>(devices)) { }

    public int VertexCount { set => SetValue(0, (float)value); }
    public void SetOutputBounds(float l, float t, float r, float b)
    {
        SetValue(1, l); SetValue(2, t); SetValue(3, r); SetValue(4, b);
    }
    public byte[] VertexData { set => SetValue(5, value); }

    protected override void DisposeCore(nint nativePointer, bool disposing)
    {
        if (disposing && IsEnabled) SetValue(6, true);
        base.DisposeCore(nativePointer, disposing);
    }
}
