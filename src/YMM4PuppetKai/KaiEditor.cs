using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using PuppetKai.Core;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Player.Video;

namespace YMM4PuppetKai;

/// <summary>ピン・ボーン・切れ込みの編集（編集キャンバス）。</summary>
internal sealed class KaiEditorAttribute : PropertyEditorAttribute2
{
    public KaiEditorAttribute()
    {
        PropertyEditorSize = PropertyEditorSize.FullWidth;
        IsMultiEditing = false;
    }

    public override FrameworkElement Create() => new KaiEditor();

    public override void SetBindings(FrameworkElement control, ItemProperty[] itemProperties)
    {
        if (control is KaiEditor e) e.Bind(itemProperties.Length > 0 ? itemProperties[0] : null);
    }

    public override void ClearBindings(FrameworkElement control)
    {
        if (control is KaiEditor e) e.Bind(null);
    }
}

/// <summary>入力画像（このエフェクトの手前まで）と輪郭を作る。UI スレッド以外から呼んでよい。</summary>
internal static class ItemImageLoader
{
    /// <summary>描画側（KaiProcessor）と同じく、長い辺が 2048px を超える画像は縮めてから輪郭を取る。</summary>
    const int OutlineMaxSize = 2048;

    public static KaiImage? Load(IEditorInfo info, PuppetKaiEffect effect)
    {
        try
        {
            using var src = info.CreateItemVideoSource(new ItemVideoSourceCreationParameter(VideoEffectSelection.UpTo(effect)) { ApplyItemTransform = false });
            if (src == null) return null;
            var time = info.ItemPosition.Time;
            if (time < TimeSpan.Zero) time = TimeSpan.Zero;
            else if (info.ItemDuration.Frame > 0 && info.ItemDuration.Time <= time) time = info.VideoInfo.GetTimeFrom(info.ItemDuration.Frame - 1);
            var usage = Enum.TryParse<TimelineSourceUsage>("Paused", out var u) ? u : default;
            src.Update(time, usage);
            var img = src.RenderBitmapSource(out var b);
            if (img == null || img.PixelWidth <= 0 || img.PixelHeight <= 0) return null;
            double w = b.Right - b.Left, h = b.Bottom - b.Top;
            if (!(w > 0) || !double.IsFinite(w)) w = img.PixelWidth;
            if (!(h > 0) || !double.IsFinite(h)) h = img.PixelHeight;
            double l = double.IsFinite(b.Left) ? b.Left : -w / 2, t = double.IsFinite(b.Top) ? b.Top : -h / 2;
            if (img.CanFreeze) img.Freeze();
            var bounds = new Rect(l, t, w, h);
            AlphaOutline? outline = null;
            try { outline = BuildOutline(img, bounds); }
            catch (Exception ex) { KaiLog.Error("輪郭の計算に失敗しました", ex); }
            return new KaiImage(img, bounds, outline);
        }
        catch (Exception ex)
        {
            KaiLog.Error("編集キャンバスの画像の読み込みに失敗しました", ex);
            return null;
        }
    }

    static AlphaOutline? BuildOutline(BitmapSource img, Rect bounds)
    {
        BitmapSource src = img.Format == PixelFormats.Pbgra32 || img.Format == PixelFormats.Bgra32
            ? img
            : new FormatConvertedBitmap(img, PixelFormats.Pbgra32, null, 0);
        int iw = src.PixelWidth, ih = src.PixelHeight;
        var px = new byte[checked(iw * ih * 4)];
        src.CopyPixels(px, iw * 4, 0);

        float w = (float)bounds.Width, h = (float)bounds.Height;
        float scale = Math.Min(1f, OutlineMaxSize / Math.Max(w, h));
        int pw = Math.Max(1, (int)MathF.Ceiling(w * scale)), ph = Math.Max(1, (int)MathF.Ceiling(h * scale));
        var alpha = new byte[pw * ph];
        for (int y = 0; y < ph; y++)
        {
            int sy = Math.Clamp((int)((y + 0.5f) / scale * ih / h), 0, ih - 1);
            for (int x = 0; x < pw; x++)
            {
                int sx = Math.Clamp((int)((x + 0.5f) / scale * iw / w), 0, iw - 1);
                alpha[y * pw + x] = px[(sy * iw + sx) * 4 + 3];
            }
        }
        var o = AlphaOutline.Build(alpha, pw, ph, (float)bounds.Left, (float)bounds.Top, 1f / scale);
        return o.IsEmpty ? null : o;
    }
}

/// <summary>
/// 編集の画面：「ピン」「ボーン」のタブ → 編集キャンバス → ボタン → 選択中のものの値（元の仕様書 6章）。
/// </summary>
internal sealed class KaiEditor : UserControl, IPropertyEditorControl2
{
    static KaiTab lastTab = KaiTab.Pin;
    static double canvasHeight = 300;
    const double MinCanvasHeight = 120, MaxCanvasHeight = 1600;
    const int FrameRefreshDelayMs = 250;

    ItemProperty? prop;
    PuppetKaiEffect? effect;
    IEditorInfo? editorInfo;
    KaiTab tab = lastTab;
    object? pinTabSelection, boneTabSelection;

    readonly KaiCanvas canvas;
    readonly Grid canvasArea;
    readonly ToggleButton pinTabButton = new() { Content = "ピン", Padding = new Thickness(0, 3, 0, 3), Focusable = false };
    readonly ToggleButton boneTabButton = new() { Content = "ボーン", Padding = new Thickness(0, 3, 0, 3), Focusable = false };
    readonly ScrollBar hBar = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed };
    readonly ScrollBar vBar = new() { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Right, Visibility = Visibility.Collapsed };
    readonly StackPanel viewButtons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 4, 0) };
    readonly Button cutButton, resetViewButton, refreshButton;
    readonly Button addButton = new() { Padding = new Thickness(8, 3, 8, 3), Focusable = false };
    readonly Button removeButton = new() { Content = "−", MinWidth = 30, Margin = new Thickness(2, 0, 0, 0), Focusable = false, ToolTip = "選択中のものを削除" };
    readonly Button resetMoveButton;
    readonly PropertiesEditor props = new();
    readonly TextBlock pointHeader = new() { Margin = new Thickness(4, 6, 0, 2), FontWeight = FontWeights.Bold, Visibility = Visibility.Collapsed };
    readonly PropertiesEditor pointProps = new() { Visibility = Visibility.Collapsed };
    readonly PropertiesEditor reserve = new() { Visibility = Visibility.Hidden, IsHitTestVisible = false, Focusable = false };
    readonly List<INotifyPropertyChanged> watched = new();
    readonly DispatcherTimer frameTimer;
    bool syncingBars;

    bool imageRequested, loadRunning, loadPending;
    int lastFrame = int.MinValue;

    public event EventHandler? BeginEdit;
    public event EventHandler? EndEdit;

    internal PuppetKaiEffect? KaiEffect => effect;
    internal KaiTab Tab => tab;

    /// <summary>いまのタブで選んでいるもの（ピンのタブ：ピンか切れ込み、ボーンのタブ：ボーンか切れ込み）。</summary>
    internal object? Selection => tab == KaiTab.Pin ? pinTabSelection : boneTabSelection;

    public KaiEditor()
    {
        canvas = new KaiCanvas(this);
        cutButton = IconButton(Icons.Scissors, "切れ込みを追加（画像の中央に置きます）");
        resetViewButton = IconButton(Icons.ResetView, "表示を初期化");
        refreshButton = IconButton(Icons.Refresh, "表示する画像を更新");
        resetMoveButton = IconButton(Icons.ResetMove, "すべてのピンの移動を 0 に戻す", 30);
        resetMoveButton.Margin = new Thickness(2, 0, 0, 0);
        resetMoveButton.Width = double.NaN;
        resetMoveButton.Height = double.NaN;
        viewButtons.Children.Add(cutButton);
        viewButtons.Children.Add(resetViewButton);
        viewButtons.Children.Add(refreshButton);

        var tabs = new UniformGrid { Columns = 2, Rows = 1 };
        tabs.Children.Add(pinTabButton);
        tabs.Children.Add(boneTabButton);

        canvasArea = new Grid { Height = canvasHeight, Margin = new Thickness(0, 2, 0, 0) };
        canvasArea.Children.Add(canvas);
        canvasArea.Children.Add(hBar);
        canvasArea.Children.Add(vBar);
        canvasArea.Children.Add(viewButtons);

        var resizeBorder = new FrameworkElementFactory(typeof(Border));
        resizeBorder.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var resize = new Thumb
        {
            Height = 6,
            Cursor = Cursors.SizeNS,
            Template = new ControlTemplate(typeof(Thumb)) { VisualTree = resizeBorder },
            ToolTip = "ドラッグで編集キャンバスの高さを変えます",
        };
        resize.DragDelta += (_, e) =>
        {
            canvasHeight = Math.Clamp(canvasArea.Height + e.VerticalChange, MinCanvasHeight, MaxCanvasHeight);
            canvasArea.Height = canvasHeight;
        };

        var buttons = new Grid { Margin = new Thickness(0, 8, 0, 0) }; // キャンバスの下端（高さの変更）との隙間
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(removeButton, 1);
        Grid.SetColumn(resetMoveButton, 2);
        buttons.Children.Add(addButton);
        buttons.Children.Add(removeButton);
        buttons.Children.Add(resetMoveButton);

        // 値の欄：ボーンを選んだときの高さを、見えない編集欄で確保しておく（キャンバスの位置が動かないように）
        var values = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        values.Children.Add(reserve);
        var shown = new StackPanel();
        shown.Children.Add(props);
        shown.Children.Add(pointHeader);
        shown.Children.Add(pointProps);
        values.Children.Add(shown);
        reserve.Target = new KaiBone();

        var panel = new StackPanel();
        panel.Children.Add(tabs);
        panel.Children.Add(canvasArea);
        panel.Children.Add(resize);
        panel.Children.Add(buttons);
        panel.Children.Add(values);
        Content = panel;

        pinTabButton.Click += (_, _) => SwitchTab(KaiTab.Pin);
        boneTabButton.Click += (_, _) => SwitchTab(KaiTab.Bone);
        cutButton.Click += (_, _) => Safe(AddCut);
        resetViewButton.Click += (_, _) => canvas.ResetView();
        refreshButton.Click += (_, _) => RequestImage();
        addButton.Click += (_, _) => Safe(() =>
        {
            if (tab == KaiTab.Pin) AddPinAt(new Point(0, 0));
            else AddBoneAt(new Point(0, 0));
        });
        removeButton.Click += (_, _) => Safe(RemoveSelected);
        resetMoveButton.Click += (_, _) => Safe(ResetMoves);
        props.BeginEdit += (_, _) => BeginEdit?.Invoke(this, EventArgs.Empty);
        props.EndEdit += (_, _) => EndEdit?.Invoke(this, EventArgs.Empty);
        pointProps.BeginEdit += (_, _) => BeginEdit?.Invoke(this, EventArgs.Empty);
        pointProps.EndEdit += (_, _) => EndEdit?.Invoke(this, EventArgs.Empty);

        canvas.ViewChanged += (_, _) => UpdateScrollBars();
        hBar.ValueChanged += (_, e) => { if (!syncingBars) canvas.SetOffset(e.NewValue, null); };
        vBar.ValueChanged += (_, e) => { if (!syncingBars) canvas.SetOffset(null, e.NewValue); };

        frameTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(FrameRefreshDelayMs) };
        frameTimer.Tick += (_, _) => { frameTimer.Stop(); RequestImage(); };
        Unloaded += (_, _) => frameTimer.Stop();

        UpdateUi();
    }

    static Button IconButton(string data, string tooltip, double size = 26)
    {
        var path = new Path
        {
            Data = Geometry.Parse(data),
            Width = 16,
            Height = 16,
            StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.None,
            SnapsToDevicePixels = false,
        };
        path.SetBinding(Shape.StrokeProperty, new Binding(nameof(Control.Foreground))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1),
        });
        return new Button
        {
            Content = path,
            Width = size,
            Height = size,
            Padding = new Thickness(0),
            Margin = new Thickness(2, 0, 0, 0),
            ToolTip = tooltip,
            Focusable = false,
        };
    }

    static class Icons
    {
        public const string Scissors = "M2,12.5 a2.2,2.2 0 1 0 4.4,0 a2.2,2.2 0 1 0 -4.4,0 M9.6,12.5 a2.2,2.2 0 1 0 4.4,0 a2.2,2.2 0 1 0 -4.4,0 M5.6,10.7 L12,1.5 M10.4,10.7 L4,1.5";
        public const string ResetView = "M8,1.5 L8,14.5 M1.5,8 L14.5,8 M6,3.5 L8,1.5 L10,3.5 M6,12.5 L8,14.5 L10,12.5 M3.5,6 L1.5,8 L3.5,10 M12.5,6 L14.5,8 L12.5,10";
        public const string Refresh = "M13.5,8 A5.5,5.5 0 1 1 11.9,4.1 M11.9,1.1 L11.9,4.1 L8.9,4.1";
        public const string ResetMove = "M2.5,8 A5.5,5.5 0 1 0 4.1,4.1 M4.1,1.1 L4.1,4.1 L7.1,4.1";
    }

    // ───────────── YMM4 とのやり取り ─────────────

    public void SetEditorInfo(IEditorInfo? info)
    {
        editorInfo = info;
        props.SetEditorInfo(info);
        pointProps.SetEditorInfo(info);
        reserve.SetEditorInfo(info);
        if (info == null) { frameTimer.Stop(); return; }
        int frame = info.ItemPosition.Frame;
        if (!imageRequested)
        {
            if (effect != null) RequestImage();
        }
        else if (frame != lastFrame)
        {
            // 再生位置が変わったら、少し待ってから画像を取り直す
            frameTimer.Stop();
            frameTimer.Start();
        }
    }

    public void Bind(ItemProperty? p)
    {
        if (effect != null) effect.PropertyChanged -= Effect_PropertyChanged;
        var old = effect;
        prop = p;
        effect = p?.PropertyOwner as PuppetKaiEffect;
        if (effect != null) effect.PropertyChanged += Effect_PropertyChanged;
        if (!ReferenceEquals(old, effect))
        {
            pinTabSelection = boneTabSelection = null;
            canvas.SetImage(null, "画像を読み込んでいます…");
            imageRequested = false;
            if (effect != null && editorInfo != null) RequestImage();
        }
        Rewatch();
        UpdateUi();
    }

    void Effect_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PuppetKaiEffect.Pins) or nameof(PuppetKaiEffect.Bones) or nameof(PuppetKaiEffect.Cuts))
        {
            Rewatch();
            ValidateSelection();
            UpdateUi();
        }
    }

    void Rewatch()
    {
        foreach (var w in watched) w.PropertyChanged -= Model_PropertyChanged;
        watched.Clear();
        if (effect == null) return;
        foreach (var x in effect.Pins) Watch(x);
        foreach (var x in effect.Bones) Watch(x);
        foreach (var x in effect.Cuts) { Watch(x); foreach (var p in x.Points) Watch(p); }

        void Watch(INotifyPropertyChanged n) { n.PropertyChanged += Model_PropertyChanged; watched.Add(n); }
    }

    void Model_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is KaiCut && e.PropertyName == nameof(KaiCut.Points))
        {
            // 点が増えた・減った（元に戻すを含む）
            Rewatch();
            ValidateSelection();
            UpdateUi();
            return;
        }
        canvas.Refresh();
    }

    /// <summary>切れ込みの点が、どの切れ込みの何番目か。</summary>
    internal (KaiCut Cut, int Index)? FindCutPoint(KaiCutPoint p)
    {
        if (effect == null) return null;
        foreach (var c in effect.Cuts)
        {
            int i = c.Points.IndexOf(p);
            if (i >= 0) return (c, i);
        }
        return null;
    }

    void ValidateSelection()
    {
        if (effect == null) { pinTabSelection = boneTabSelection = null; return; }
        if (!Exists(pinTabSelection)) pinTabSelection = null;
        if (!Exists(boneTabSelection)) boneTabSelection = null;

        bool Exists(object? o) => o switch
        {
            KaiPin p => effect.Pins.Contains(p),
            KaiBone b => effect.Bones.Contains(b),
            KaiCutPoint cp => FindCutPoint(cp) != null,
            _ => false,
        };
    }

    // ───────────── 画像 ─────────────

    async void RequestImage()
    {
        frameTimer.Stop();
        imageRequested = true;
        if (loadRunning) { loadPending = true; return; }
        loadRunning = true;
        try
        {
            do
            {
                loadPending = false;
                var info = editorInfo;
                var eff = effect;
                if (info == null || eff == null) break;
                lastFrame = info.ItemPosition.Frame;
                var result = await Task.Run(() => ItemImageLoader.Load(info, eff));
                if (!ReferenceEquals(eff, effect)) continue;
                if (result != null) canvas.SetImage(result, null);
                else if (canvas.Image == null) canvas.SetStatus("画像を読み込めませんでした（右上の円形の矢印で読み直せます）");
            }
            while (loadPending);
        }
        catch (Exception ex)
        {
            KaiLog.Error("編集キャンバスの画像の読み込みに失敗しました", ex);
        }
        finally
        {
            loadRunning = false;
        }
    }

    // ───────────── 表示の更新 ─────────────

    void SwitchTab(KaiTab t)
    {
        tab = t;
        lastTab = t;
        UpdateUi();
    }

    void UpdateUi()
    {
        pinTabButton.IsChecked = tab == KaiTab.Pin;
        boneTabButton.IsChecked = tab == KaiTab.Bone;
        var eff = effect;
        bool has = eff != null;
        addButton.Content = tab == KaiTab.Pin ? "ピンを追加" : "ボーンを追加";
        addButton.IsEnabled = has && (tab == KaiTab.Pin ? eff!.Pins.Count < PuppetKaiEffect.PinCapacity : eff!.Bones.Count < PuppetKaiEffect.BoneCapacity);
        removeButton.IsEnabled = has && Selection != null;
        resetMoveButton.Visibility = tab == KaiTab.Pin ? Visibility.Visible : Visibility.Collapsed;
        resetMoveButton.IsEnabled = has && eff!.Pins.Count > 0;
        cutButton.IsEnabled = has && eff!.Cuts.Count < PuppetKaiEffect.CutCapacity;
        // 切れ込みの点を選んでいるときは、上に切れ込み（有効）、下に選んだ点の X・Y（見出しで始点・中間点・終点）
        object? target = Selection;
        KaiCutPoint? point = null;
        if (Selection is KaiCutPoint cp && FindCutPoint(cp) is { } found)
        {
            target = found.Cut;
            point = cp;
            int last = found.Cut.Points.Count - 1;
            pointHeader.Text = found.Index == 0 ? "始点" : found.Index == last ? "終点" : $"中間点 {found.Index}";
        }
        if (!ReferenceEquals(props.Target, target)) props.Target = target;
        if (!ReferenceEquals(pointProps.Target, point)) pointProps.Target = point;
        pointHeader.Visibility = pointProps.Visibility = point != null ? Visibility.Visible : Visibility.Collapsed;
        canvas.Refresh();
    }

    void UpdateScrollBars()
    {
        var s = canvas.ScrollInfo();
        double sb = KaiCanvas.ScrollBarSize;
        syncingBars = true;
        try
        {
            hBar.Visibility = s.NeedH ? Visibility.Visible : Visibility.Collapsed;
            vBar.Visibility = s.NeedV ? Visibility.Visible : Visibility.Collapsed;
            hBar.Height = sb;
            vBar.Width = sb;
            hBar.Margin = new Thickness(0, 0, s.NeedV ? sb : 0, 0);
            vBar.Margin = new Thickness(0, 0, 0, s.NeedH ? sb : 0);
            hBar.Minimum = 0; hBar.Maximum = s.MaxX; hBar.ViewportSize = s.ViewportW;
            hBar.SmallChange = 16; hBar.LargeChange = Math.Max(16, s.ViewportW * 0.9); hBar.Value = s.X;
            vBar.Minimum = 0; vBar.Maximum = s.MaxY; vBar.ViewportSize = s.ViewportH;
            vBar.SmallChange = 16; vBar.LargeChange = Math.Max(16, s.ViewportH * 0.9); vBar.Value = s.Y;
            viewButtons.Margin = new Thickness(0, 4, 4 + (s.NeedV ? sb : 0), 0);
        }
        finally { syncingBars = false; }
    }

    // ───────────── 編集（キャンバスとボタンから） ─────────────

    internal void RaiseBeginEdit() => BeginEdit?.Invoke(this, EventArgs.Empty);
    internal void RaiseEndEdit() => EndEdit?.Invoke(this, EventArgs.Empty);

    internal void SelectFromCanvas(object? model)
    {
        if (tab == KaiTab.Pin) pinTabSelection = model;
        else boneTabSelection = model;
        UpdateUi();
    }

    void Commit(Action change)
    {
        BeginEdit?.Invoke(this, EventArgs.Empty);
        try { change(); }
        finally { EndEdit?.Invoke(this, EventArgs.Empty); }
    }

    void SetList(string name, object value)
    {
        if (prop == null) return;
        if (prop.PropertyInfo.Name == name) prop.SetValue(value);
        else prop.SetValue(typeof(PuppetKaiEffect).GetProperty(name)!, value);
    }

    internal KaiPin? AddPinAt(Point local)
    {
        var eff = effect;
        if (eff == null || eff.Pins.Count >= PuppetKaiEffect.PinCapacity) return null;
        var pin = KaiPin.Create(local.X, local.Y);
        Commit(() => SetList(nameof(PuppetKaiEffect.Pins), eff.Pins.Add(pin)));
        pinTabSelection = pin;
        UpdateUi();
        return pin;
    }

    internal KaiBone? AddBoneAt(Point local)
    {
        var eff = effect;
        if (eff == null || eff.Bones.Count >= PuppetKaiEffect.BoneCapacity) return null;
        var parent = boneTabSelection as KaiBone;
        var bone = KaiBone.Create(local.X, local.Y, parent?.Id ?? Guid.Empty);
        Commit(() => SetList(nameof(PuppetKaiEffect.Bones), eff.Bones.Add(bone)));
        boneTabSelection = bone;
        UpdateUi();
        return bone;
    }

    /// <summary>親 → child の線の上に新しいボーンを入れ、親 → 新しいボーン → child につなぎ直す。</summary>
    internal KaiBone? InsertBoneOnLine(KaiBone child, Point local)
    {
        var eff = effect;
        if (eff == null || eff.Bones.Count >= PuppetKaiEffect.BoneCapacity) return null;
        var bone = KaiBone.Create(local.X, local.Y, child.ParentId);
        Commit(() =>
        {
            child.ParentId = bone.Id;
            int i = eff.Bones.IndexOf(child);
            SetList(nameof(PuppetKaiEffect.Bones), i >= 0 ? eff.Bones.Insert(i, bone) : eff.Bones.Add(bone));
        });
        boneTabSelection = bone;
        UpdateUi();
        return bone;
    }

    /// <summary>選択中のボーンにピンを紐付ける。同じボーンに紐付いていれば解除する。</summary>
    internal void TogglePinBinding(KaiPin pin)
    {
        if (boneTabSelection is not KaiBone bone) return;
        Commit(() => pin.BoneId = pin.BoneId == bone.Id ? Guid.Empty : bone.Id);
        canvas.Refresh();
    }

    void AddCut()
    {
        var eff = effect;
        if (eff == null || eff.Cuts.Count >= PuppetKaiEffect.CutCapacity) return;
        // 画像の中心から左右に、画像の幅の 1/4（仕様書 9.1）
        double cx = 0, cy = 0, half = 100;
        var b = canvas.Image?.Bounds ?? Rect.Empty;
        if (!b.IsEmpty && b.Width > 0) { cx = b.X + b.Width / 2; cy = b.Y + b.Height / 2; half = b.Width / 4; }
        var cut = KaiCut.Create(cx - half, cy, cx + half, cy);
        Commit(() => SetList(nameof(PuppetKaiEffect.Cuts), eff.Cuts.Add(cut)));
        var first = cut.Points[0];
        if (tab == KaiTab.Pin) pinTabSelection = first; else boneTabSelection = first;
        UpdateUi();
    }

    void RemoveSelected()
    {
        var eff = effect;
        if (eff == null) return;
        switch (Selection)
        {
            case KaiPin p:
                Commit(() =>
                {
                    SetList(nameof(PuppetKaiEffect.Pins), eff.Pins.Remove(p));
                });
                pinTabSelection = null;
                break;
            case KaiCutPoint cp when FindCutPoint(cp) is { } found:
                // 中間点はその点だけ、始点・終点は切れ込みごと消す（仕様書 9.1）
                if (found.Index > 0 && found.Index < found.Cut.Points.Count - 1) RemoveCutPoint(cp);
                else Commit(() => SetList(nameof(PuppetKaiEffect.Cuts), eff.Cuts.Remove(found.Cut)));
                break;
            case KaiBone b:
                Commit(() =>
                {
                    // A → B → C の B を消すと A → C（B の子は B の親の子）。B に紐付いたピンは紐付きなし
                    foreach (var child in eff.Bones.Where(x => x.ParentId == b.Id)) child.ParentId = b.ParentId == b.Id ? Guid.Empty : b.ParentId;
                    foreach (var pin in eff.Pins.Where(x => x.BoneId == b.Id)) pin.BoneId = Guid.Empty;
                    SetList(nameof(PuppetKaiEffect.Bones), eff.Bones.Remove(b));
                });
                boneTabSelection = null;
                break;
        }
        ValidateSelection();
        UpdateUi();
    }

    /// <summary>切れ込みの index 番目の線分（点 index と index+1 の間）に点を入れる。上限なら null。</summary>
    internal KaiCutPoint? InsertCutPoint(KaiCut cut, int index, Point local)
    {
        if (cut.Points.Count >= KaiCut.MaxPoints || index < 0 || index + 1 >= cut.Points.Count) return null;
        var p = KaiCutPoint.Create(local.X, local.Y);
        Commit(() => cut.Points = cut.Points.Insert(index + 1, p));
        if (tab == KaiTab.Pin) pinTabSelection = p; else boneTabSelection = p;
        Rewatch();
        UpdateUi();
        return p;
    }

    /// <summary>中間点を消す（始点・終点は消さない）。</summary>
    internal void RemoveCutPoint(KaiCutPoint p)
    {
        if (FindCutPoint(p) is not { } found) return;
        if (found.Index == 0 || found.Index == found.Cut.Points.Count - 1) return;
        Commit(() => found.Cut.Points = found.Cut.Points.RemoveAt(found.Index));
        if (ReferenceEquals(pinTabSelection, p)) pinTabSelection = null;
        if (ReferenceEquals(boneTabSelection, p)) boneTabSelection = null;
        Rewatch();
        ValidateSelection();
        UpdateUi();
    }

    void ResetMoves()
    {
        var eff = effect;
        if (eff == null || eff.Pins.Count == 0) return;
        var owner = Window.GetWindow(this);
        const string text = "すべてのピンの移動X・移動Yを、すべての中間点で 0 に戻します。よろしいですか？";
        var r = owner != null
            ? MessageBox.Show(owner, text, "改パペット変形", MessageBoxButton.OKCancel, MessageBoxImage.Question)
            : MessageBox.Show(text, "改パペット変形", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (r != MessageBoxResult.OK) return;
        Commit(() =>
        {
            foreach (var pin in eff.Pins)
            {
                foreach (var v in pin.MoveX.Values) v.Value = 0;
                foreach (var v in pin.MoveY.Values) v.Value = 0;
            }
        });
    }

    void Safe(Action a)
    {
        try { a(); }
        catch (Exception ex)
        {
            KaiLog.Error("編集に失敗しました", ex);
            MessageBox.Show(ex.Message, "改パペット変形", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
