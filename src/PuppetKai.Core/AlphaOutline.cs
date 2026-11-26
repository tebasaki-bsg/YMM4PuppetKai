using System;
using System.Collections.Generic;
using System.Numerics;

namespace PuppetKai.Core;

/// <summary>
/// 入力画像の不透明な部分（α &gt; 0）の輪郭。少し外側に広げてから画素の境目をなぞり、
/// 折れ線にして単純化する。座標は画像の中心が原点。
/// </summary>
public sealed class AlphaOutline
{
    /// <summary>いちばん細かい段階（段階 0）の、外側に広げる幅（画素）。</summary>
    public const int Dilation = 2;
    /// <summary>いちばん細かい段階の、折れ線を間引く基準（画素）。v1.0.0 までは 0.75 で、画素の境目の斜めの階段（ずれ約 0.71）が
    /// 間引かれず、輪郭の細かい画像で網目の三角形が上限を超えていた。</summary>
    public const double SimplifyTolerance = 1.5;

    /// <summary>この面積（画素）以下の不透明なかたまりを「小さな島」として扱う（ほぼ透明な点・消し残りなど）。</summary>
    public const int SmallIslandPixels = 64;

    /// <summary>粗さの段階：(間引く基準, 大きなかたまりを広げる幅, 小さな島を広げる幅)。
    /// 広げる幅は間引く基準より 0.5 画素以上大きくし、間引いても不透明な画素が輪郭の外に出ないようにする。
    /// 小さな島は大きく広げて近くの島どうしをまとめ、島の数（＝輪郭の数）を減らす。</summary>
    static readonly (double Tol, int Big, int Small)[] Levels =
    {
        (1.5, 2, 2), (2.5, 3, 6), (4, 5, 12), (6.5, 8, 24), (10, 12, 48), (16, 18, 96),
    };
    public static int MaxLevel => Levels.Length - 1;
    /// <summary>粗さの段階（0 がいちばん細かい）。</summary>
    public int Level { get; }

    // 粗い段階を作り直すための元の情報（不透明かどうかと、小さな島の印）
    readonly Source? source;
    AlphaOutline? coarser;
    bool coarserBuilt;

    sealed class Source
    {
        public required byte[] Mask;   // 0：透明、1：大きなかたまり、2：小さな島（段階 1 以上で使う。最初は 0 か 1）
        public required int Width, Height;
        public required float OriginX, OriginY, PixelSize;
        public bool Labeled;
    }

    public int Width { get; }
    public int Height { get; }
    /// <summary>閉じた折れ線（最後の点と最初の点がつながる）。穴は向きが逆になる。</summary>
    public IReadOnlyList<Vector2[]> Loops { get; }
    /// <summary>広げた後の不透明な画素の数（網目の間隔を決めるのに使う）。</summary>
    public long FilledPixels { get; }
    public bool IsEmpty => Loops.Count == 0;

    // 内外判定を速くするための、Y で並べた辺
    readonly Edge[] edges;
    readonly float minY, maxY;
    readonly int[] bucketStart;
    readonly int[] bucketItems;
    const int Buckets = 256;

    readonly struct Edge
    {
        public readonly float X0, Y0, X1, Y1;
        public Edge(Vector2 a, Vector2 b) { X0 = a.X; Y0 = a.Y; X1 = b.X; Y1 = b.Y; }
    }

    AlphaOutline(int w, int h, List<Vector2[]> loops, long filled, int level, Source? source)
    {
        Width = w; Height = h; Loops = loops; FilledPixels = filled; Level = level; this.source = source;
        var list = new List<Edge>();
        foreach (var loop in loops)
            for (int i = 0; i < loop.Length; i++)
                list.Add(new Edge(loop[i], loop[(i + 1) % loop.Length]));
        edges = list.ToArray();
        minY = float.MaxValue; maxY = float.MinValue;
        foreach (var e in edges) { minY = Math.Min(minY, Math.Min(e.Y0, e.Y1)); maxY = Math.Max(maxY, Math.Max(e.Y0, e.Y1)); }
        bucketStart = new int[Buckets + 1];
        var tmp = new List<int>[Buckets];
        for (int i = 0; i < Buckets; i++) tmp[i] = new List<int>();
        if (edges.Length > 0)
        {
            for (int i = 0; i < edges.Length; i++)
            {
                var e = edges[i];
                int b0 = Bucket(Math.Min(e.Y0, e.Y1)), b1 = Bucket(Math.Max(e.Y0, e.Y1));
                for (int b = b0; b <= b1; b++) tmp[b].Add(i);
            }
        }
        var items = new List<int>();
        for (int b = 0; b < Buckets; b++) { bucketStart[b] = items.Count; items.AddRange(tmp[b]); }
        bucketStart[Buckets] = items.Count;
        bucketItems = items.ToArray();
    }

    int Bucket(float y)
    {
        if (maxY <= minY) return 0;
        int b = (int)((y - minY) / (maxY - minY) * Buckets);
        return Math.Clamp(b, 0, Buckets - 1);
    }

    /// <summary>点が輪郭の内側か（偶奇規則）。</summary>
    public bool Contains(Vector2 p)
    {
        if (edges.Length == 0 || p.Y < minY || p.Y > maxY) return false;
        int b = Bucket(p.Y);
        bool inside = false;
        for (int k = bucketStart[b]; k < bucketStart[b + 1]; k++)
        {
            var e = edges[bucketItems[k]];
            if ((e.Y0 > p.Y) != (e.Y1 > p.Y))
            {
                double x = e.X0 + (p.Y - e.Y0) * (double)(e.X1 - e.X0) / (e.Y1 - e.Y0);
                if (x > p.X) inside = !inside;
            }
        }
        return inside;
    }

    /// <summary>α 値（幅×高さ、行優先）から輪郭を作る。座標は画像の中心が原点。</summary>
    public static AlphaOutline Build(ReadOnlySpan<byte> alpha, int width, int height)
        => Build(alpha, width, height, -width / 2f, -height / 2f, 1f);

    /// <summary>
    /// α 値から輪郭を作る（いちばん細かい段階）。画素 (x, y) の左上の角が (originX + x·pixelSize, originY + y·pixelSize) になる座標で返す。
    /// </summary>
    public static AlphaOutline Build(ReadOnlySpan<byte> alpha, int width, int height, float originX, float originY, float pixelSize)
    {
        var mask = new byte[width * height];
        for (int i = 0; i < mask.Length; i++) if (alpha[i] != 0) mask[i] = 1;
        var src = new Source { Mask = mask, Width = width, Height = height, OriginX = originX, OriginY = originY, PixelSize = pixelSize };
        return BuildLevel(src, 0);
    }

    /// <summary>
    /// 1段階粗い輪郭（間引きを粗くし、外側に広げる幅も大きくする。小さな島はさらに広げて近くのものとまとめる）。
    /// 網目の三角形が上限を超えるときに使う。いちばん粗い段階なら null。一度作ったものは使い回す。
    /// </summary>
    public AlphaOutline? Coarser()
    {
        if (coarserBuilt) return coarser;
        coarserBuilt = true;
        if (source == null || Level >= MaxLevel) return null;
        lock (source)
        {
            if (!source.Labeled) { MarkSmallIslands(source.Mask, source.Width, source.Height); source.Labeled = true; }
        }
        coarser = BuildLevel(source, Level + 1);
        return coarser;
    }

    static AlphaOutline BuildLevel(Source src, int level)
    {
        var (tol, rBig, rSmall) = Levels[level];
        if (!src.Labeled) rSmall = rBig;   // 段階 0：小さな島も同じ幅
        int width = src.Width, height = src.Height;
        int pad = Math.Max(rBig, rSmall) + 1;
        int W = width + 2 * pad, H = height + 2 * pad;
        var big = new bool[W * H];
        var small = rSmall != rBig ? new bool[W * H] : big;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                byte v = src.Mask[y * width + x];
                if (v == 1) big[(y + pad) * W + (x + pad)] = true;
                else if (v == 2) small[(y + pad) * W + (x + pad)] = true;
            }
        var d = Dilate(big, W, H, rBig);
        if (!ReferenceEquals(small, big))
        {
            var ds = Dilate(small, W, H, rSmall);
            for (int i = 0; i < d.Length; i++) d[i] |= ds[i];
        }
        long filled = 0;
        for (int i = 0; i < d.Length; i++) if (d[i]) filled++;

        var loops = TraceLoops(d, W, H);
        var result = new List<Vector2[]>(loops.Count);
        float ps = src.PixelSize;
        foreach (var loop in loops)
        {
            var simp = SimplifyClosed(loop, tol);
            if (simp.Count < 3) continue;
            var arr = new Vector2[simp.Count];
            for (int i = 0; i < arr.Length; i++)
                arr[i] = new Vector2(src.OriginX + (simp[i].X - pad) * ps, src.OriginY + (simp[i].Y - pad) * ps);
            result.Add(arr);
        }
        return new AlphaOutline(width, height, result, (long)Math.Round(filled * (double)ps * ps), level, src);
    }

    /// <summary>正方形で r 画素広げる（横 → 縦）。</summary>
    static bool[] Dilate(bool[] m, int W, int H, int r)
    {
        var t = new bool[W * H];
        for (int y = 0; y < H; y++)
        {
            int run = -1;
            for (int x = 0; x < W; x++)
            {
                if (m[y * W + x]) run = x;
                if (run >= 0 && x - run <= r) t[y * W + x] = true;
            }
            run = -1;
            for (int x = W - 1; x >= 0; x--)
            {
                if (m[y * W + x]) run = x;
                if (run >= 0 && run - x <= r) t[y * W + x] = true;
            }
        }
        var d = new bool[W * H];
        for (int x = 0; x < W; x++)
        {
            int run = -1;
            for (int y = 0; y < H; y++)
            {
                if (t[y * W + x]) run = y;
                if (run >= 0 && y - run <= r) d[y * W + x] = true;
            }
            run = -1;
            for (int y = H - 1; y >= 0; y--)
            {
                if (t[y * W + x]) run = y;
                if (run >= 0 && run - y <= r) d[y * W + x] = true;
            }
        }
        return d;
    }

    /// <summary>不透明な画素のかたまり（8連結）のうち、面積が SmallIslandPixels 以下のものの印を 2 にする。</summary>
    static void MarkSmallIslands(byte[] mask, int w, int h)
    {
        var label = new int[mask.Length];
        var stack = new Stack<int>();
        var members = new List<int>();
        int next = 0;
        for (int start = 0; start < mask.Length; start++)
        {
            if (mask[start] == 0 || label[start] != 0) continue;
            next++;
            members.Clear();
            label[start] = next; stack.Push(start);
            bool tooBig = false;
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                if (!tooBig) { members.Add(i); if (members.Count > SmallIslandPixels) tooBig = true; }
                int x = i % w, y = i / w;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if ((dx | dy) == 0 || nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int j = ny * w + nx;
                        if (mask[j] == 0 || label[j] != 0) continue;
                        label[j] = next; stack.Push(j);
                    }
            }
            if (!tooBig) foreach (int i in members) mask[i] = 2;
        }
    }

    static readonly int[] DX = { 1, 0, -1, 0 };
    static readonly int[] DY = { 0, 1, 0, -1 };

    /// <summary>不透明な側を右手に見て、画素の境目を一周ずつなぞる（4連結）。</summary>
    static List<List<Vector2>> TraceLoops(bool[] m, int W, int H)
    {
        int VW = W + 1;
        // 頂点ごとに出ていく辺（最大2本）。方向 0:+x 1:+y 2:-x 3:-y。-1 は無し
        var out0 = new sbyte[VW * (H + 1)];
        var out1 = new sbyte[VW * (H + 1)];
        Array.Fill(out0, (sbyte)-1); Array.Fill(out1, (sbyte)-1);
        bool M(int x, int y) => x >= 0 && y >= 0 && x < W && y < H && m[y * W + x];
        void Add(int vx, int vy, int dir)
        {
            int v = vy * VW + vx;
            if (out0[v] < 0) out0[v] = (sbyte)dir; else out1[v] = (sbyte)dir;
        }
        int edgeCount = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                if (!m[y * W + x]) continue;
                if (!M(x, y - 1)) { Add(x, y, 0); edgeCount++; }
                if (!M(x + 1, y)) { Add(x + 1, y, 1); edgeCount++; }
                if (!M(x, y + 1)) { Add(x + 1, y + 1, 2); edgeCount++; }
                if (!M(x - 1, y)) { Add(x, y + 1, 3); edgeCount++; }
            }
        var loops = new List<List<Vector2>>();
        for (int sv = 0; sv < out0.Length; sv++)
        {
            while (out0[sv] >= 0 || out1[sv] >= 0)
            {
                var loop = new List<Vector2>();
                int v = sv;
                int dir = out0[v] >= 0 ? out0[v] : out1[v];
                int guard = 0;
                while (guard++ <= edgeCount + 4)
                {
                    // dir の辺を使う
                    if (out0[v] == dir) out0[v] = -1; else if (out1[v] == dir) out1[v] = -1; else break;
                    int x = v % VW, y = v / VW;
                    loop.Add(new Vector2(x, y));
                    int nx = x + DX[dir], ny = y + DY[dir];
                    v = ny * VW + nx;
                    if (out0[v] < 0 && out1[v] < 0) break;
                    // 右折 → 直進 → 左折 の順で選ぶ
                    int right = (dir + 1) & 3, straight = dir, left = (dir + 3) & 3;
                    if (out0[v] == right || out1[v] == right) dir = right;
                    else if (out0[v] == straight || out1[v] == straight) dir = straight;
                    else if (out0[v] == left || out1[v] == left) dir = left;
                    else break;
                }
                if (loop.Count >= 4) loops.Add(loop);
            }
        }
        return loops;
    }

    static List<Vector2> SimplifyClosed(List<Vector2> pts, double tol)
    {
        int n = pts.Count;
        // まず一直線に並ぶ点を除く
        var c = new List<Vector2>(n);
        for (int i = 0; i < n; i++)
        {
            var a = pts[(i + n - 1) % n]; var b = pts[i]; var d = pts[(i + 1) % n];
            var cr = (b.X - a.X) * (d.Y - b.Y) - (b.Y - a.Y) * (d.X - b.X);
            if (cr != 0) c.Add(b);
        }
        n = c.Count;
        if (n < 3) return c;
        int far = 0; float best = -1;
        for (int i = 1; i < n; i++) { float dd = Vector2.DistanceSquared(c[0], c[i]); if (dd > best) { best = dd; far = i; } }
        var keep = new bool[n];
        keep[0] = keep[far] = true;
        DouglasPeucker(c, 0, far, tol, keep);
        DouglasPeucker(c, far, n, tol, keep);
        var r = new List<Vector2>();
        for (int i = 0; i < n; i++) if (keep[i]) r.Add(c[i]);
        return r;
    }

    static void DouglasPeucker(List<Vector2> p, int i0, int i1, double tol, bool[] keep)
    {
        int n = p.Count;
        var a = p[i0 % n]; var b = p[i1 % n];
        double best = -1; int idx = -1;
        var ab = b - a; double len = ab.Length();
        for (int i = i0 + 1; i < i1; i++)
        {
            var q = p[i % n];
            double dist = len < 1e-9 ? (q - a).Length() : Math.Abs(ab.X * (q.Y - a.Y) - ab.Y * (q.X - a.X)) / len;
            if (dist > best) { best = dist; idx = i; }
        }
        if (idx >= 0 && best > tol)
        {
            keep[idx % n] = true;
            DouglasPeucker(p, i0, idx, tol, keep);
            DouglasPeucker(p, idx, i1, tol, keep);
        }
    }
}
