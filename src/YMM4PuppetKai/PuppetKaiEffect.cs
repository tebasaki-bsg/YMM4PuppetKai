using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.Effects;

namespace YMM4PuppetKai;

/// <summary>パペット変形改（映像エフェクト。表示名は「改パペット変形」）。</summary>
[PluginDetails(AuthorName = "Tebasaki", ContentId = "nc516642")]
[VideoEffect("改パペット変形", new[] { "加工" }, new[] { "パペット", "ピン", "ボーン", "切れ込み", "変形", "puppet" }, isEffectItemSupported: true, isAviUtlSupported: false)]
public sealed class PuppetKaiEffect : VideoEffectBase
{
    public const int PinCapacity = 256;
    public const int BoneCapacity = 256;
    public const int CutCapacity = 64;

    KaiDeformMode mode = KaiDeformMode.Rigid;
    ImmutableList<KaiPin> pins = ImmutableList<KaiPin>.Empty;
    ImmutableList<KaiBone> bones = ImmutableList<KaiBone>.Empty;
    ImmutableList<KaiCut> cuts = ImmutableList<KaiCut>.Empty;

    public override string Label
    {
        get
        {
            var s = $"改パペット変形 - ピン: {Pins.Count}";
            if (Bones.Count > 0) s += $" ボーン: {Bones.Count}";
            if (Cuts.Count > 0) s += $" 切れ込み: {Cuts.Count}";
            return s;
        }
    }

    [Display(GroupName = "改パペット変形", Name = "変形方式", Description = "剛体（ARAP）：形を保つ／なめらか（MLS）：なめらかに曲がる", Order = 1)]
    [EnumComboBox]
    public KaiDeformMode Mode { get => mode; set => Set(ref mode, value); }

    [Display(GroupName = "改パペット変形", Name = "剛性", Description = "大きいほど、ピンの近くだけが動きます", Order = 2)]
    [SmoothOnlyVisible]
    [AnimationSlider("F2", "", 0.5, 4.0)]
    public Animation Stiffness { get; } = new Animation(2.0, 0.1, 8.0);

    [Display(GroupName = "改パペット変形", Name = "プレビュー精度", Description = "プレビューの再生中に使う、剛体（ARAP）の計算の精度です。1回の繰り返しでの形の動きがこの値より小さくなったら計算を止めます。大きいほど速く、形は粗くなります", Order = 3)]
    [EnumComboBox]
    [RigidOnlyVisible]
    public KaiArapPrecision PlaybackPrecision { get => playbackPrecision; set => Set(ref playbackPrecision, value); }
    KaiArapPrecision playbackPrecision = KaiArapPrecision.P025;

    [Display(GroupName = "改パペット変形", Name = "書き出し精度", Description = "書き出しに使う、剛体（ARAP）の計算の精度です。プレビューを止めているときも使います。1回の繰り返しでの形の動きがこの値より小さくなったら計算を止めます。大きいほど速く、形は粗くなります", Order = 4)]
    [EnumComboBox]
    [RigidOnlyVisible]
    public KaiArapPrecision RenderPrecision { get => renderPrecision; set => Set(ref renderPrecision, value); }
    KaiArapPrecision renderPrecision = KaiArapPrecision.P002;

    [Display(GroupName = "改パペット変形", Description = "ピン・ボーン・切れ込み", Order = 10)]
    [KaiEditor]
    public ImmutableList<KaiPin> Pins
    {
        get => pins;
        set { if (Set(ref pins, value ?? ImmutableList<KaiPin>.Empty)) OnPropertyChanged(nameof(Label)); }
    }

    public ImmutableList<KaiBone> Bones
    {
        get => bones;
        set { if (Set(ref bones, value ?? ImmutableList<KaiBone>.Empty)) OnPropertyChanged(nameof(Label)); }
    }

    public ImmutableList<KaiCut> Cuts
    {
        get => cuts;
        set { if (Set(ref cuts, value ?? ImmutableList<KaiCut>.Empty)) OnPropertyChanged(nameof(Label)); }
    }


    public override IEnumerable<string> CreateExoVideoFilters(int keyFrameIndex, ExoOutputDescription exoOutputDescription) => Array.Empty<string>();

    public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices) => new KaiProcessor(devices, this);

    protected override IEnumerable<IAnimatable> GetAnimatables()
    {
        yield return Stiffness;
        foreach (var p in Pins) yield return p;
        foreach (var b in Bones) yield return b;
        foreach (var c in Cuts) yield return c;
    }
}
