using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.ItemEditor;

namespace YMM4PuppetKai;

/// <summary>変形方式。保存される値（0, 1）は変えないこと。</summary>
public enum KaiDeformMode
{
    [Display(Name = "剛体（ARAP）", Description = "網目の三角形ができるだけ形を保つように変形します")]
    Rigid = 0,
    [Display(Name = "なめらか（MLS）", Description = "ピンからの距離に応じてなめらかに変形します")]
    Smooth = 1,
}

/// <summary>剛体（ARAP）の計算の精度（打ち切り）。保存される値（0〜4）は変えないこと。足すときは末尾に。</summary>
public enum KaiArapPrecision
{
    [Display(Name = "0.02px（細かい）")]
    P002 = 0,
    [Display(Name = "0.05px")]
    P005 = 1,
    [Display(Name = "0.1px")]
    P010 = 2,
    [Display(Name = "0.25px")]
    P025 = 3,
    [Display(Name = "0.5px（速い）")]
    P050 = 4,
}

internal static class KaiArapPrecisionExtensions
{
    public static double ToTolerance(this KaiArapPrecision p) => p switch
    {
        KaiArapPrecision.P005 => 0.05,
        KaiArapPrecision.P010 => 0.1,
        KaiArapPrecision.P025 => 0.25,
        KaiArapPrecision.P050 => 0.5,
        _ => 0.02,
    };
}

/// <summary>ピン（仕様書 4章）。</summary>
public sealed class KaiPin : Animatable
{
    bool isEnabled = true;
    double restX, restY;
    Guid boneId = Guid.Empty;

    [Display(GroupName = "ピン", Name = "有効", Description = "オフにすると、基準位置に固定された点になります", Order = 0)]
    [ToggleSlider]
    public bool IsEnabled { get => isEnabled; set => Set(ref isEnabled, value); }

    [Display(GroupName = "ピン", Name = "基準X", Description = "変形前の位置（px）", Order = 1)]
    [TextBoxSlider("F1", "px", -500, 500)]
    [Range(-100000, 100000)]
    public double RestX { get => restX; set => Set(ref restX, Math.Clamp(value, -100000, 100000)); }

    [Display(GroupName = "ピン", Name = "基準Y", Description = "変形前の位置（px）", Order = 2)]
    [TextBoxSlider("F1", "px", -500, 500)]
    [Range(-100000, 100000)]
    public double RestY { get => restY; set => Set(ref restY, Math.Clamp(value, -100000, 100000)); }

    [Display(GroupName = "ピン", Name = "移動X", Description = "基準位置からの移動量（px）", Order = 3)]
    [AnimationSlider("F1", "px", -500, 500)]
    public Animation MoveX { get; } = new Animation(0, -100000, 100000);

    [Display(GroupName = "ピン", Name = "移動Y", Description = "基準位置からの移動量（px）", Order = 4)]
    [AnimationSlider("F1", "px", -500, 500)]
    public Animation MoveY { get; } = new Animation(0, -100000, 100000);

    /// <summary>紐付くボーン（無ければ Guid.Empty）。</summary>
    public Guid BoneId { get => boneId; set => Set(ref boneId, value); }

    public static KaiPin Create(double x, double y) => new() { RestX = Math.Round(x, 1), RestY = Math.Round(y, 1) };

    protected override IEnumerable<IAnimatable> GetAnimatables() => new IAnimatable[] { MoveX, MoveY };
}

/// <summary>ボーン（仕様書 4章）。</summary>
public sealed class KaiBone : Animatable
{
    Guid id = Guid.NewGuid();
    Guid parentId = Guid.Empty;
    bool isEnabled = true;
    double jointX, jointY, swingPhase;

    public Guid Id { get => id; set => Set(ref id, value); }
    public Guid ParentId { get => parentId; set => Set(ref parentId, value); }

    [Display(GroupName = "ボーン", Name = "有効", Description = "オフにすると角度 0°・伸縮 100% と同じ扱いになります", Order = 0)]
    [ToggleSlider]
    public bool IsEnabled { get => isEnabled; set => Set(ref isEnabled, value); }

    [Display(GroupName = "ボーン", Name = "ジョイントX", Description = "変形前のジョイントの位置（px）", Order = 1)]
    [TextBoxSlider("F1", "px", -500, 500)]
    [Range(-100000, 100000)]
    public double JointX { get => jointX; set => Set(ref jointX, Math.Clamp(value, -100000, 100000)); }

    [Display(GroupName = "ボーン", Name = "ジョイントY", Description = "変形前のジョイントの位置（px）", Order = 2)]
    [TextBoxSlider("F1", "px", -500, 500)]
    [Range(-100000, 100000)]
    public double JointY { get => jointY; set => Set(ref jointY, Math.Clamp(value, -100000, 100000)); }

    [Display(GroupName = "ボーン", Name = "角度", Description = "親のジョイントを中心に回す角度（時計回りが正）", Order = 3)]
    [AnimationSlider("F1", "°", -180, 180)]
    public Animation Angle { get; } = new Animation(0, -3600, 3600);

    [Display(GroupName = "ボーン", Name = "伸縮", Description = "親からの距離の倍率", Order = 4)]
    [AnimationSlider("F1", "%", 0, 200)]
    public Animation Scale { get; } = new Animation(100, 0, 1000);

    [Display(GroupName = "揺れ", Name = "揺れ角度", Description = "揺れの振れ幅", Order = 10)]
    [AnimationSlider("F1", "°", 0, 90)]
    public Animation SwingAngle { get; } = new Animation(0, 0, 360);

    [Display(GroupName = "揺れ", Name = "揺れ周期", Description = "1往復にかかる秒数", Order = 11)]
    [AnimationSlider("F2", "秒", 0.1, 5)]
    public Animation SwingPeriod { get; } = new Animation(1, 0.01, 60);

    [Display(GroupName = "揺れ", Name = "揺れ位相", Description = "揺れの始まりのずれ", Order = 12)]
    [TextBoxSlider("F0", "°", -360, 360)]
    [Range(-360, 360)]
    public double SwingPhase { get => swingPhase; set => Set(ref swingPhase, Math.Clamp(value, -360, 360)); }

    [Display(GroupName = "揺れ", Name = "揺れ伝播", Description = "子孫のボーンへ伝わる割合（1世代ごと）", Order = 13)]
    [AnimationSlider("F0", "%", 0, 200)]
    public Animation SwingPropagation { get; } = new Animation(100, 0, 200);

    [Display(GroupName = "揺れ", Name = "しなり", Description = "子孫のボーンへ伝わるときの遅れ（1世代ごと）", Order = 14)]
    [AnimationSlider("F0", "°", -180, 180)]
    public Animation Whip { get; } = new Animation(60, -180, 180);

    public static KaiBone Create(double x, double y, Guid parent) => new() { JointX = Math.Round(x, 1), JointY = Math.Round(y, 1), ParentId = parent };

    protected override IEnumerable<IAnimatable> GetAnimatables() => new IAnimatable[] { Angle, Scale, SwingAngle, SwingPeriod, SwingPropagation, Whip };
}

/// <summary>切れ込みの点（始点・中間点・終点）。値は固定値。</summary>
public sealed class KaiCutPoint : Animatable
{
    double x, y;

    [Display(Name = "X", Order = 1)]
    [TextBoxSlider("F1", "px", -500, 500)]
    [Range(-100000, 100000)]
    public double X { get => x; set => Set(ref x, Math.Clamp(value, -100000, 100000)); }

    [Display(Name = "Y", Order = 2)]
    [TextBoxSlider("F1", "px", -500, 500)]
    [Range(-100000, 100000)]
    public double Y { get => y; set => Set(ref y, Math.Clamp(value, -100000, 100000)); }

    public static KaiCutPoint Create(double x, double y) => new() { X = Math.Round(x, 1), Y = Math.Round(y, 1) };

    protected override IEnumerable<IAnimatable> GetAnimatables() => Array.Empty<IAnimatable>();
}

/// <summary>
/// 切れ込み（仕様書 5章）。点を順につないだ折れ線（2〜32 点）。最初の点が始点、最後の点が終点。
/// </summary>
public sealed class KaiCut : Animatable
{
    public const int MaxPoints = 32;

    bool isEnabled = true;
    ImmutableList<KaiCutPoint> points = ImmutableList.Create(new KaiCutPoint(), new KaiCutPoint());

    [Display(GroupName = "切れ込み", Name = "有効", Description = "オフにすると、無いものとして扱います", Order = 0)]
    [ToggleSlider]
    public bool IsEnabled { get => isEnabled; set => Set(ref isEnabled, value); }

    /// <summary>点の一覧（編集キャンバスで編集する。値の欄には選んだ点だけを出す）。</summary>
    public ImmutableList<KaiCutPoint> Points { get => points; set => Set(ref points, value ?? ImmutableList<KaiCutPoint>.Empty); }

    public static KaiCut Create(double x0, double y0, double x1, double y1)
        => new() { Points = ImmutableList.Create(KaiCutPoint.Create(x0, y0), KaiCutPoint.Create(x1, y1)) };

    protected override IEnumerable<IAnimatable> GetAnimatables() => points;
}

/// <summary>なめらか（MLS）のときだけ表示する。</summary>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class SmoothOnlyVisibleAttribute : Attribute, ICustomVisibilityAttribute2
{
    sealed class Conv : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is KaiDeformMode m && m == KaiDeformMode.Smooth ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    public Binding GetBinding(object item, object propertyOwner)
        => new(nameof(PuppetKaiEffect.Mode)) { Source = propertyOwner, Converter = new Conv() };
}

/// <summary>変形方式が剛体（ARAP）のときだけ表示する。</summary>
internal sealed class RigidOnlyVisibleAttribute : Attribute, ICustomVisibilityAttribute2
{
    sealed class Conv : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is KaiDeformMode m && m == KaiDeformMode.Rigid ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    public Binding GetBinding(object item, object propertyOwner)
        => new(nameof(PuppetKaiEffect.Mode)) { Source = propertyOwner, Converter = new Conv() };
}
