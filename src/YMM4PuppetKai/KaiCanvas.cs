using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Vector = System.Windows.Vector;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PuppetKai.Core;
using YukkuriMovieMaker.Controls;

namespace YMM4PuppetKai;

internal enum KaiTab { Pin, Bone }

/// <summary>編集キャンバスに出す入力画像（このエフェクトの手前まで）と、その輪郭。</summary>
internal sealed record KaiImage(BitmapSource Image, Rect Bounds, AlphaOutline? Outline);

/// <summary>
/// 編集キャンバス（元の仕様書 6章・この仕様書 9章）。座標はすべてアイテムのローカル座標（画像の中心付近が原点）。
/// 選択・追加・削除などの編集は <see cref="KaiEditor"/> に任せ、ここでは表示と当たり判定とドラッグを受け持つ。
/// </summary>
internal sealed class KaiCanvas : FrameworkElement
{
    static readonly Color PinColor = Color.FromRgb(52, 132, 246);
    static readonly Color BoneColor = Color.FromRgb(245, 158, 11);
    static readonly Color OutlineColor = Color.FromRgb(34, 197, 94);
    static readonly Color CutColor = Color.FromRgb(239, 68, 68);
    const double DisabledOpacity = 110 / 255.0;
    const double InactiveLayerOpacity = 0.4;

    const double PinRadius = 5, BoneRadius = 6.5, CutEndRadius = 5, HoverBonus = 2, RingGap = 3.5;
    const double PointHitRadius = 10, LineHitRadius = 6, LineEndSkip = 0.05;
    const double MinZoom = 0.25, MaxZoom = 16, ZoomStep = 1.2;
    const double DragThreshold = 2, PanThreshold = 3;
    public static double ScrollBarSize => SystemParameters.VerticalScrollBarWidth;

    static readonly Brush PinBrush = Frozen(new SolidColorBrush(PinColor));
    static readonly Brush BoneBrush = Frozen(new SolidColorBrush(BoneColor));
    static readonly Brush CutBrush = Frozen(new SolidColorBrush(CutColor));
    static readonly Brush GhostBrush = Frozen(new SolidColorBrush(Color.FromArgb(176, 245, 158, 11)));
    static readonly Pen WhitePen = Frozen(new Pen(Brushes.White, 1.5));
    static readonly Pen HaloPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)), 3.5));
    static readonly Pen PinRingPen = Frozen(new Pen(new SolidColorBrush(PinColor), 2));
    static readonly Pen BoneRingPen = Frozen(new Pen(new SolidColorBrush(BoneColor), 2));
    static readonly Pen CutRingPen = Frozen(new Pen(new SolidColorBrush(CutColor), 2));
    static readonly Pen BoneLinePen = Frozen(new Pen(new SolidColorBrush(BoneColor), 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    static readonly Pen BoneLineHoverPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(253, 196, 104)), 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    static readonly Pen BindPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(200, 245, 158, 11)), 1.5) { DashStyle = new DashStyle(new double[] { 2, 2 }, 0) });
    static readonly Pen CutLinePen = Frozen(new Pen(new SolidColorBrush(CutColor), 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    static readonly Pen CutEdgePen = Frozen(new Pen(Brushes.White, 4.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    static readonly Pen ArrowPen = Frozen(new Pen(Brushes.White, 1));

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    enum HitKind { None, Pin, Bone, CutPoint, CutLine, BoneLine }

    /// <summary>Index：CutLine のときの線分の番号（点 Index と Index+1 の間）。</summary>
    readonly record struct Hit(HitKind Kind, object? Model, Point Local, int Index = 0);

    readonly record struct View(double Scale, Point Origin, Rect Content, double ViewportW, double ViewportH,
        double ExtentW, double ExtentH, bool NeedH, bool NeedV)
    {
        public Point ToDisplay(double x, double y) => new(Origin.X + x * Scale, Origin.Y + y * Scale);
        public Point ToDisplay(Point p) => ToDisplay(p.X, p.Y);
        public Point ToLocal(Point d) => new((d.X - Origin.X) / Scale, (d.Y - Origin.Y) / Scale);
        public double MaxX => Math.Max(0, ExtentW - ViewportW);
        public double MaxY => Math.Max(0, ExtentH - ViewportH);
    }

    readonly KaiEditor host;
    readonly CheckerBackground checker;
    KaiImage? image;
    Geometry? outlineGeometry;
    string? status = "画像を読み込んでいます…";

    double zoom = 1, offsetX, offsetY;
    bool resetPending = true;

    // ドラッグ（左ボタン）
    object? dragModel;
    Vector dragGrab;
    Point dragStart;
    bool dragMoved;
    Rect? frozenContent;

    // 移動（中ボタン）
    bool panning, panMoved;
    Point panLast;

    // ホバー
    object? hoverModel;
    Point? hoverCutGhost;
    Point? hoverGhost;
    KaiBone? hoverLine;

    bool viewChangedQueued;

    /// <summary>拡大率・表示位置・スクロールできる範囲が変わった（スクロールバーの更新用）。</summary>
    public event EventHandler? ViewChanged;

    public KaiCanvas(KaiEditor host)
    {
        this.host = host;
        checker = new CheckerBackground(this);
        ClipToBounds = true;
        Focusable = true;
        FocusVisualStyle = null;
        SnapsToDevicePixels = true;
    }

    public KaiImage? Image => image;

    public void SetImage(KaiImage? img, string? statusText)
    {
        bool sizeChanged = image == null || img == null || image.Bounds != img.Bounds;
        image = img;
        status = img == null ? statusText : null;
        outlineGeometry = img?.Outline is { } o ? BuildOutlineGeometry(o) : null;
        if (sizeChanged) resetPending = true;
        Refresh();
    }

    public void SetStatus(string? text)
    {
        if (image != null) return;
        status = text;
        InvalidateVisual();
    }

    /// <summary>モデルが変わったときなどに呼ぶ（描き直してスクロールの範囲も更新する）。</summary>
    public void Refresh()
    {
        InvalidateVisual();
        if (viewChangedQueued) return;
        viewChangedQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            viewChangedQueued = false;
            ApplyPendingReset();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    public void ResetView()
    {
        zoom = 1;
        resetPending = true;
        Refresh();
    }

    public void SetOffset(double? x, double? y)
    {
        if (x is double vx) offsetX = vx;
        if (y is double vy) offsetY = vy;
        InvalidateVisual();
    }

    /// <summary>スクロールバーに渡す値。</summary>
    public (double MaxX, double MaxY, double ViewportW, double ViewportH, double X, double Y, bool NeedH, bool NeedV) ScrollInfo()
    {
        var v = GetView();
        if (v is not View view) return (0, 0, 0, 0, 0, 0, false, false);
        return (view.MaxX, view.MaxY, view.ViewportW, view.ViewportH, offsetX, offsetY, view.NeedH, view.NeedV);
    }

    void ApplyPendingReset()
    {
        if (!resetPending) return;
        if (GetView() is not View v) return;
        resetPending = false;
        var img = ImageRect();
        double padX = Math.Max(0, (v.ViewportW - v.ExtentW) / 2), padY = Math.Max(0, (v.ViewportH - v.ExtentH) / 2);
        offsetX = Math.Clamp(padX + (img.X + img.Width / 2 - v.Content.Left) * v.Scale - v.ViewportW / 2, 0, v.MaxX);
        offsetY = Math.Clamp(padY + (img.Y + img.Height / 2 - v.Content.Top) * v.Scale - v.ViewportH / 2, 0, v.MaxY);
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Refresh();
    }

    // ───────────── 表示の範囲 ─────────────

    Rect ImageRect()
    {
        if (image != null && image.Bounds.Width > 0 && image.Bounds.Height > 0) return image.Bounds;
        return new Rect(-320, -180, 640, 360); // 画像が無いとき（読み込み中・失敗）の仮の範囲
    }

    Rect ContentRect()
    {
        if (frozenContent is Rect f) return f;
        var r = ImageRect();
        var eff = host.KaiEffect;
        if (eff == null) return r;
        foreach (var p in eff.Pins) r.Union(new Point(p.RestX, p.RestY));
        foreach (var b in eff.Bones) r.Union(new Point(b.JointX, b.JointY));
        foreach (var c in eff.Cuts) foreach (var p in c.Points) r.Union(new Point(p.X, p.Y));
        return r;
    }

    View? GetView()
    {
        double w = ActualWidth, h = ActualHeight;
        if (!(w > 1) || !(h > 1)) return null;
        var img = ImageRect();
        double fit = Math.Min(w / img.Width, h / img.Height);
        if (!(fit > 0) || !double.IsFinite(fit)) return null;
        double s = fit * zoom;
        var content = ContentRect();
        double ew = content.Width * s, eh = content.Height * s;
        double sb = ScrollBarSize;
        bool needV = eh > h + 0.5, needH = ew > w + 0.5;
        if (needV && !needH) needH = ew > w - sb + 0.5;
        if (needH && !needV) needV = eh > h - sb + 0.5;
        double vw = w - (needV ? sb : 0), vh = h - (needH ? sb : 0);
        offsetX = Math.Clamp(offsetX, 0, Math.Max(0, ew - vw));
        offsetY = Math.Clamp(offsetY, 0, Math.Max(0, eh - vh));
        double padX = Math.Max(0, (vw - ew) / 2), padY = Math.Max(0, (vh - eh) / 2);
        var origin = new Point(padX - offsetX - content.Left * s, padY - offsetY - content.Top * s);
        return new View(s, origin, content, vw, vh, ew, eh, needH, needV);
    }

    static Geometry BuildOutlineGeometry(AlphaOutline o)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            foreach (var loop in o.Loops)
            {
                if (loop.Length < 2) continue;
                ctx.BeginFigure(new Point(loop[0].X, loop[0].Y), false, true);
                var pts = new List<Point>(loop.Length - 1);
                for (int i = 1; i < loop.Length; i++) pts.Add(new Point(loop[i].X, loop[i].Y));
                ctx.PolyLineTo(pts, true, false);
            }
        }
        g.Freeze();
        return g;
    }

    bool IsOpenEnd(double x, double y)
    {
        var o = image?.Outline;
        return o != null && !o.Contains(new Vector2((float)x, (float)y));
    }

    // ───────────── 描画 ─────────────

    protected override void OnRender(DrawingContext dc)
    {
        var all = new Rect(RenderSize);
        dc.DrawRectangle(checker.GetBrush(), null, all);
        if (GetView() is not View v) return;
        var eff = host.KaiEffect;

        var img = ImageRect();
        if (image != null)
            dc.DrawImage(image.Image, new Rect(v.ToDisplay(img.TopLeft), new Size(img.Width * v.Scale, img.Height * v.Scale)));
        else
        {
            var frame = new Rect(v.ToDisplay(img.TopLeft), new Size(img.Width * v.Scale, img.Height * v.Scale));
            dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(80, 128, 128, 128)), 1) { DashStyle = DashStyles.Dash }, frame);
            if (!string.IsNullOrEmpty(status)) DrawText(dc, status, all);
        }

        if (outlineGeometry != null)
        {
            dc.PushTransform(new MatrixTransform(v.Scale, 0, 0, v.Scale, v.Origin.X, v.Origin.Y));
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(OutlineColor), 1 / v.Scale), outlineGeometry);
            dc.Pop();
        }

        if (eff == null) return;
        if (host.Tab == KaiTab.Bone)
        {
            DrawPins(dc, v, eff);
            DrawBones(dc, v, eff, true);
        }
        else
        {
            dc.PushOpacity(InactiveLayerOpacity);
            DrawBones(dc, v, eff, false);
            dc.Pop();
            DrawPins(dc, v, eff);
        }
        DrawCuts(dc, v, eff);
    }

    void DrawText(DrawingContext dc, string text, Rect area)
    {
        var fg = GetValue(System.Windows.Documents.TextElement.ForegroundProperty) as Brush ?? Brushes.Gray;
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            12, fg, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxTextWidth = Math.Max(10, area.Width - 20), TextAlignment = TextAlignment.Center };
        dc.PushOpacity(0.75);
        dc.DrawText(ft, new Point(area.X + 10, area.Y + (area.Height - ft.Height) / 2));
        dc.Pop();
    }

    void DrawPins(DrawingContext dc, View v, PuppetKaiEffect eff)
    {
        var selPin = host.Tab == KaiTab.Pin ? host.Selection as KaiPin : null;
        var selBone = host.Tab == KaiTab.Bone ? host.Selection as KaiBone : null;
        foreach (var pin in eff.Pins)
        {
            var c = v.ToDisplay(pin.RestX, pin.RestY);
            double r = ReferenceEquals(hoverModel, pin) ? PinRadius + HoverBonus : PinRadius;
            if (!pin.IsEnabled) dc.PushOpacity(DisabledOpacity);
            if (ReferenceEquals(pin, selPin)) dc.DrawEllipse(null, PinRingPen, c, r + RingGap, r + RingGap);
            if (selBone != null && pin.BoneId == selBone.Id) dc.DrawEllipse(null, BoneRingPen, c, r + RingGap, r + RingGap);
            dc.DrawEllipse(null, HaloPen, c, r, r);
            dc.DrawEllipse(PinBrush, WhitePen, c, r, r);
            if (!pin.IsEnabled) dc.Pop();
        }
    }

    void DrawBones(DrawingContext dc, View v, PuppetKaiEffect eff, bool active)
    {
        var map = new Dictionary<Guid, KaiBone>();
        foreach (var b in eff.Bones) map.TryAdd(b.Id, b);
        var sel = active ? host.Selection as KaiBone : null;

        // ピンとボーンの紐付け（点線）
        foreach (var pin in eff.Pins)
        {
            if (pin.BoneId == Guid.Empty || !map.TryGetValue(pin.BoneId, out var b)) continue;
            dc.DrawLine(BindPen, v.ToDisplay(pin.RestX, pin.RestY), v.ToDisplay(b.JointX, b.JointY));
        }

        // 親から子への線と矢印
        foreach (var b in eff.Bones)
        {
            if (b.ParentId == Guid.Empty || b.ParentId == b.Id || !map.TryGetValue(b.ParentId, out var parent)) continue;
            var a = v.ToDisplay(parent.JointX, parent.JointY);
            var c = v.ToDisplay(b.JointX, b.JointY);
            bool dim = !b.IsEnabled;
            if (dim) dc.PushOpacity(DisabledOpacity);
            dc.DrawLine(ReferenceEquals(hoverLine, b) ? BoneLineHoverPen : BoneLinePen, a, c);
            var d = c - a;
            if (d.Length > 14)
            {
                d.Normalize();
                var n = new Vector(-d.Y, d.X);
                var mid = a + (c - a) / 2;
                var tip = mid + d * 4;
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(tip, true, true);
                    ctx.LineTo(tip - d * 8 + n * 4.5, true, true);
                    ctx.LineTo(tip - d * 8 - n * 4.5, true, true);
                }
                g.Freeze();
                dc.DrawGeometry(BoneBrush, ArrowPen, g);
            }
            if (dim) dc.Pop();
        }

        // ジョイント（ひし形）
        foreach (var b in eff.Bones)
        {
            var c = v.ToDisplay(b.JointX, b.JointY);
            double r = ReferenceEquals(hoverModel, b) ? BoneRadius + HoverBonus : BoneRadius;
            if (!b.IsEnabled) dc.PushOpacity(DisabledOpacity);
            if (ReferenceEquals(b, sel)) dc.DrawEllipse(null, BoneRingPen, c, r + RingGap + 0.5, r + RingGap + 0.5);
            var g = Diamond(c, r);
            dc.DrawGeometry(null, HaloPen, g);
            dc.DrawGeometry(BoneBrush, WhitePen, g);
            if (!b.IsEnabled) dc.Pop();
        }

        // 線の上でクリックしたときに挿入される位置
        if (active && hoverGhost is Point gp)
        {
            var g = Diamond(gp, BoneRadius - 1);
            dc.DrawGeometry(GhostBrush, WhitePen, g);
        }
    }

    static Geometry Diamond(Point c, double r)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(c.X, c.Y - r), true, true);
            ctx.LineTo(new Point(c.X + r, c.Y), true, true);
            ctx.LineTo(new Point(c.X, c.Y + r), true, true);
            ctx.LineTo(new Point(c.X - r, c.Y), true, true);
        }
        g.Freeze();
        return g;
    }

    void DrawCuts(DrawingContext dc, View v, PuppetKaiEffect eff)
    {
        var selPoint = host.Selection as KaiCutPoint;
        foreach (var cut in eff.Cuts)
        {
            var pts = cut.Points;
            if (pts.Count == 0) continue;
            if (!cut.IsEnabled) dc.PushOpacity(DisabledOpacity);
            if (pts.Count >= 2)
            {
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(v.ToDisplay(pts[0].X, pts[0].Y), false, false);
                    var rest = new List<Point>(pts.Count - 1);
                    for (int i = 1; i < pts.Count; i++) rest.Add(v.ToDisplay(pts[i].X, pts[i].Y));
                    ctx.PolyLineTo(rest, true, true);
                }
                g.Freeze();
                dc.DrawGeometry(null, CutEdgePen, g);
                dc.DrawGeometry(null, CutLinePen, g);
            }
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                bool isEnd = i == 0 || i == pts.Count - 1;
                // 開いた端（輪郭の外）だけ四角。中間点は端と同じ大きさの丸（仕様書 9.2）
                bool square = isEnd && IsOpenEnd(p.X, p.Y);
                DrawCutPoint(dc, v.ToDisplay(p.X, p.Y), square, ReferenceEquals(hoverModel, p), ReferenceEquals(p, selPoint));
            }
            if (!cut.IsEnabled) dc.Pop();
        }
        if (hoverCutGhost is Point gp)
        {
            dc.PushOpacity(0.7);
            dc.DrawEllipse(CutBrush, WhitePen, gp, CutEndRadius - 1, CutEndRadius - 1);
            dc.Pop();
        }
    }

    /// <summary>切れ込みの点。選択の印（赤い輪）は選んでいる点だけに付ける（開いた端は四角の輪）。</summary>
    static void DrawCutPoint(DrawingContext dc, Point c, bool square, bool hover, bool selected)
    {
        double r = hover ? CutEndRadius + HoverBonus : CutEndRadius;
        double q = r + RingGap + 1;
        if (square)
        {
            if (selected) dc.DrawRectangle(null, CutRingPen, new Rect(c.X - q, c.Y - q, 2 * q, 2 * q));
            var rect = new Rect(c.X - r, c.Y - r, 2 * r, 2 * r);
            dc.DrawRectangle(null, HaloPen, rect);
            dc.DrawRectangle(CutBrush, WhitePen, rect);
        }
        else
        {
            if (selected) dc.DrawEllipse(null, CutRingPen, c, q, q);
            dc.DrawEllipse(null, HaloPen, c, r, r);
            dc.DrawEllipse(CutBrush, WhitePen, c, r, r);
        }
    }

    // ───────────── 当たり判定 ─────────────

    Hit HitTest(Point d, View v)
    {
        var eff = host.KaiEffect;
        if (eff == null) return default;
        var best = new Hit(HitKind.None, null, default);
        double bestD2 = PointHitRadius * PointHitRadius;

        void Try(HitKind kind, object model, double x, double y)
        {
            var p = v.ToDisplay(x, y);
            double d2 = (p - d).LengthSquared;
            if (d2 <= bestD2) { bestD2 = d2; best = new Hit(kind, model, new Point(x, y)); }
        }

        // 下に描くものから順に調べ、同じ距離なら上に描いたものを選ぶ
        if (host.Tab == KaiTab.Bone)
        {
            foreach (var p in eff.Pins) Try(HitKind.Pin, p, p.RestX, p.RestY);
            foreach (var b in eff.Bones) Try(HitKind.Bone, b, b.JointX, b.JointY);
        }
        else
        {
            foreach (var p in eff.Pins) Try(HitKind.Pin, p, p.RestX, p.RestY);
        }
        foreach (var c in eff.Cuts)
            foreach (var p in c.Points) Try(HitKind.CutPoint, p, p.X, p.Y);
        if (best.Kind != HitKind.None) return best;

        // 切れ込みの線（線から 6px 以内）。両方のタブで、ボーン間の線より優先する
        double bestC2 = LineHitRadius * LineHitRadius;
        foreach (var c in eff.Cuts)
        {
            var pts = c.Points;
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                var a = v.ToDisplay(pts[i].X, pts[i].Y);
                var b = v.ToDisplay(pts[i + 1].X, pts[i + 1].Y);
                var ab = b - a;
                double len2 = ab.LengthSquared;
                if (len2 < 1e-9) continue;
                double t = Math.Clamp(Vector.Multiply(d - a, ab) / len2, 0, 1);
                var q = a + ab * t;
                double d2 = (q - d).LengthSquared;
                if (d2 <= bestC2) { bestC2 = d2; best = new Hit(HitKind.CutLine, c, v.ToLocal(q), i); }
            }
        }
        if (best.Kind != HitKind.None || host.Tab != KaiTab.Bone) return best;

        // ボーン間の線（線から 6px 以内、両端 5% を除く）
        var map = new Dictionary<Guid, KaiBone>();
        foreach (var b in eff.Bones) map.TryAdd(b.Id, b);
        double bestL2 = LineHitRadius * LineHitRadius;
        foreach (var b in eff.Bones)
        {
            if (b.ParentId == Guid.Empty || b.ParentId == b.Id || !map.TryGetValue(b.ParentId, out var parent)) continue;
            var a = v.ToDisplay(parent.JointX, parent.JointY);
            var c = v.ToDisplay(b.JointX, b.JointY);
            var ab = c - a;
            double len2 = ab.LengthSquared;
            if (len2 < 1e-9) continue;
            double t = Vector.Multiply(d - a, ab) / len2;
            if (t < LineEndSkip || t > 1 - LineEndSkip) continue;
            var q = a + ab * t;
            double d2 = (q - d).LengthSquared;
            if (d2 <= bestL2) { bestL2 = d2; best = new Hit(HitKind.BoneLine, b, v.ToLocal(q)); }
        }
        return best;
    }

    // ───────────── マウス ─────────────

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        e.Handled = true;
        if (panning || dragModel != null) return;
        Focus();
        if (GetView() is not View v || host.KaiEffect == null) return;
        var pos = e.GetPosition(this);
        var hit = HitTest(pos, v);
        object? drag = null;
        switch (hit.Kind)
        {
            case HitKind.Pin:
                if (host.Tab == KaiTab.Pin) { host.SelectFromCanvas(hit.Model); drag = hit.Model; }
                else if (host.Selection is KaiBone) host.TogglePinBinding((KaiPin)hit.Model!);
                break;
            case HitKind.Bone:
                host.SelectFromCanvas(hit.Model);
                drag = hit.Model;
                break;
            case HitKind.CutPoint:
                host.SelectFromCanvas(hit.Model);
                drag = hit.Model;
                break;
            case HitKind.CutLine:
                drag = host.InsertCutPoint((KaiCut)hit.Model!, hit.Index, hit.Local);
                break;
            case HitKind.BoneLine:
                drag = host.InsertBoneOnLine((KaiBone)hit.Model!, hit.Local);
                break;
            default:
                var local = v.ToLocal(pos);
                drag = host.Tab == KaiTab.Pin ? host.AddPinAt(local) : host.AddBoneAt(local);
                break;
        }
        ClearHover();
        if (drag == null) return;
        dragModel = drag;
        dragStart = pos;
        dragMoved = false;
        var cur = ModelPoint(drag);
        dragGrab = cur - v.ToLocal(pos);
        CaptureMouse();
    }

    static Point ModelPoint(object model) => model switch
    {
        KaiPin p => new Point(p.RestX, p.RestY),
        KaiBone b => new Point(b.JointX, b.JointY),
        KaiCutPoint c => new Point(c.X, c.Y),
        _ => default,
    };

    static void SetModelPoint(object model, Point p)
    {
        double x = Math.Round(p.X, 1), y = Math.Round(p.Y, 1);
        switch (model)
        {
            case KaiPin pin: pin.RestX = x; pin.RestY = y; break;
            case KaiBone b: b.JointX = x; b.JointY = y; break;
            case KaiCutPoint c: c.X = x; c.Y = y; break;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var pos = e.GetPosition(this);
        if (panning)
        {
            var d = pos - panLast;
            if (!panMoved && Math.Abs(pos.X - panLast.X) < PanThreshold && Math.Abs(pos.Y - panLast.Y) < PanThreshold) return;
            panMoved = true;
            offsetX -= d.X;
            offsetY -= d.Y;
            panLast = pos;
            InvalidateVisual();
            ViewChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (GetView() is not View v) return;
        if (dragModel != null)
        {
            if (!dragMoved)
            {
                if ((pos - dragStart).Length < DragThreshold) return;
                dragMoved = true;
                frozenContent = v.Content; // ドラッグ中は表示の範囲を変えない（点がマウスから離れないように）
                host.RaiseBeginEdit();
            }
            try { SetModelPoint(dragModel, v.ToLocal(pos) + dragGrab); }
            catch (Exception ex) { KaiLog.Error("点の移動に失敗しました", ex); }
            return;
        }
        UpdateHover(pos, v);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (dragModel != null)
        {
            e.Handled = true;
            ReleaseMouseCapture(); // OnLostMouseCapture で終える
            EndDrag();
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        EndDrag();
        if (panning) { panning = false; panMoved = false; }
    }

    void EndDrag()
    {
        if (dragModel == null) return;
        bool moved = dragMoved;
        dragModel = null;
        dragMoved = false;
        frozenContent = null;
        if (moved) host.RaiseEndEdit();
        Refresh();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        e.Handled = true;
        if (dragModel != null || panning) return;
        if (GetView() is not View v) return;
        var hit = HitTest(e.GetPosition(this), v);
        // 中間点の右クリックはその点を消す（両方のタブ。始点・終点は何もしない）
        if (hit.Kind == HitKind.CutPoint) { ClearHover(); host.RemoveCutPoint((KaiCutPoint)hit.Model!); return; }
        if (host.Tab == KaiTab.Bone && hit.Kind == HitKind.None) host.SelectFromCanvas(null);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton != MouseButton.Middle || dragModel != null) return;
        panning = true;
        panMoved = false;
        panLast = e.GetPosition(this);
        Focus();
        CaptureMouse();
        ClearHover();
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton != MouseButton.Middle || !panning) return;
        panning = false;
        panMoved = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || e.Delta == 0) return; // Ctrl なしはプロパティ欄のスクロールに回す
        e.Handled = true;
        if (GetView() is not View v) return;
        var pos = e.GetPosition(this);
        var local = v.ToLocal(pos);
        double z = Math.Clamp(zoom * (e.Delta > 0 ? ZoomStep : 1 / ZoomStep), MinZoom, MaxZoom);
        if (z == zoom) return;
        zoom = z;
        if (GetView() is View v2)
        {
            var p = v2.ToDisplay(local);
            offsetX += p.X - pos.X;
            offsetY += p.Y - pos.Y;
        }
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (dragModel == null) ClearHover();
    }

    void UpdateHover(Point pos, View v)
    {
        var hit = HitTest(pos, v);
        object? model = null;
        Point? ghost = null, cutGhost = null;
        KaiBone? line = null;
        switch (hit.Kind)
        {
            case HitKind.Pin:
                if (host.Tab == KaiTab.Pin || host.Selection is KaiBone) model = hit.Model;
                break;
            case HitKind.Bone:
            case HitKind.CutPoint:
                model = hit.Model;
                break;
            case HitKind.CutLine:
                if (((KaiCut)hit.Model!).Points.Count < KaiCut.MaxPoints) cutGhost = v.ToDisplay(hit.Local);
                break;
            case HitKind.BoneLine:
                line = (KaiBone)hit.Model!;
                ghost = v.ToDisplay(hit.Local);
                break;
        }
        if (ReferenceEquals(model, hoverModel) && ReferenceEquals(line, hoverLine) && Nullable.Equals(ghost, hoverGhost) && Nullable.Equals(cutGhost, hoverCutGhost)) return;
        hoverModel = model;
        hoverLine = line;
        hoverGhost = ghost;
        hoverCutGhost = cutGhost;
        InvalidateVisual();
    }

    void ClearHover()
    {
        if (hoverModel == null && hoverLine == null && hoverGhost == null && hoverCutGhost == null) return;
        hoverModel = null;
        hoverLine = null;
        hoverGhost = null;
        hoverCutGhost = null;
        InvalidateVisual();
    }
}
