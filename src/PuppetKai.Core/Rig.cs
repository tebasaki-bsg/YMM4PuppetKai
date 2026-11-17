using System;
using System.Collections.Generic;
using System.Numerics;

namespace PuppetKai.Core;

/// <summary>ピン1本の、その時刻の値（座標は画像の中心が原点、X 右・Y 下、px）。</summary>
public readonly record struct PinInput(bool Enabled, Vector2 Rest, Vector2 Move, int Bone);

/// <summary>ボーン1本の、その時刻の値。角度は度（時計回りが正）、伸縮は %、揺れ周期は秒。</summary>
public readonly record struct BoneInput(
    bool Enabled, Vector2 Joint, int Parent,
    double Angle, double Scale,
    double SwingAngle, double SwingPeriod, double SwingPhase, double SwingPropagation, double Whip);

/// <summary>変形の手がかりにする点（変形前 → 変形後）の組。</summary>
public sealed class ControlSet
{
    public Vector2[] Rest = Array.Empty<Vector2>();
    public Vector2[] Target = Array.Empty<Vector2>();
    public int Count => Rest.Length;
}

/// <summary>ボーン・揺れ・ピンから制御点を求める（仕様書 6章、元の仕様書 4.1〜4.4）。</summary>
public static class Rig
{
    static readonly double[] BoneSamples = { 0.25, 0.5, 0.75, 1.0 };

    public static ControlSet Compute(IReadOnlyList<PinInput> pins, IReadOnlyList<BoneInput> bones, double timeSeconds)
    {
        int nb = bones.Count;
        var parent = new int[nb];
        for (int i = 0; i < nb; i++)
        {
            int p = bones[i].Parent;
            parent[i] = p >= 0 && p < nb && p != i ? p : -1;
        }
        // 親のループを断つ（壊れたデータへの備え）
        for (int i = 0; i < nb; i++)
        {
            int steps = 0, c = parent[i];
            while (c >= 0 && steps <= nb) { c = parent[c]; steps++; }
            if (steps > nb) parent[i] = -1;
        }

        var depthOrder = OrderByDepth(parent);
        var swing = SwingAngles(bones, parent, timeSeconds);

        var cur = new Vector2[nb];
        var rot = new double[nb];
        var hasChild = new bool[nb];
        for (int i = 0; i < nb; i++) if (parent[i] >= 0) hasChild[parent[i]] = true;

        foreach (int b in depthOrder)
        {
            var bi = bones[b];
            int p = parent[b];
            if (p < 0)
            {
                cur[b] = bi.Joint;
                rot[b] = 0;
                continue;
            }
            double ang = bi.Enabled ? bi.Angle : 0;
            double sc = bi.Enabled ? bi.Scale / 100.0 : 1.0;
            rot[b] = rot[p] + ang + swing[b];
            var d = bi.Joint - bones[p].Joint;
            cur[b] = cur[p] + Rotate(d, rot[b]) * (float)sc;
        }

        var rest = new List<Vector2>(pins.Count + nb * 4);
        var target = new List<Vector2>(pins.Count + nb * 4);
        foreach (var pin in pins)
        {
            rest.Add(pin.Rest);
            if (!pin.Enabled) { target.Add(pin.Rest); continue; }
            int b = pin.Bone;
            if (b >= 0 && b < nb)
                target.Add(cur[b] + Rotate(pin.Rest - bones[b].Joint, rot[b]) + pin.Move);
            else
                target.Add(pin.Rest + pin.Move);
        }
        for (int b = 0; b < nb; b++)
        {
            int p = parent[b];
            if (p >= 0)
            {
                var r0 = bones[p].Joint; var r1 = bones[b].Joint;
                var c0 = cur[p]; var c1 = cur[b];
                foreach (var u in BoneSamples)
                {
                    float fu = (float)u;
                    rest.Add(r0 + (r1 - r0) * fu);
                    target.Add(c0 + (c1 - c0) * fu);
                }
            }
            else if (hasChild[b])
            {
                rest.Add(bones[b].Joint);
                target.Add(cur[b]);
            }
        }
        return new ControlSet { Rest = rest.ToArray(), Target = target.ToArray() };
    }

    /// <summary>各ボーンが受ける揺れの角度（度）。</summary>
    public static double[] SwingAngles(IReadOnlyList<BoneInput> bones, int[] parent, double t)
    {
        int nb = bones.Count;
        var result = new double[nb];
        for (int a = 0; a < nb; a++)
        {
            var ba = bones[a];
            if (!ba.Enabled || ba.SwingAngle == 0 || !(ba.SwingPeriod > 0)) continue;
            double baseArg = 2 * Math.PI * t / ba.SwingPeriod + ba.SwingPhase * Math.PI / 180.0;
            double prop = ba.SwingPropagation / 100.0;
            double whip = ba.Whip * Math.PI / 180.0;
            // 自分（k=0）と子孫（k 世代下）
            var stack = new Stack<(int bone, int k)>();
            stack.Push((a, 0));
            int guard = 0;
            while (stack.Count > 0 && guard++ < nb * nb + 16)
            {
                var (b, k) = stack.Pop();
                result[b] += ba.SwingAngle * Math.Pow(prop, k) * Math.Sin(baseArg - k * whip);
                for (int c = 0; c < nb; c++) if (parent[c] == b) stack.Push((c, k + 1));
            }
        }
        return result;
    }

    static List<int> OrderByDepth(int[] parent)
    {
        int n = parent.Length;
        var depth = new int[n];
        for (int i = 0; i < n; i++)
        {
            int d = 0, c = parent[i];
            while (c >= 0 && d <= n) { d++; c = parent[c]; }
            depth[i] = d;
        }
        var order = new List<int>(n);
        for (int i = 0; i < n; i++) order.Add(i);
        order.Sort((x, y) => depth[x] != depth[y] ? depth[x].CompareTo(depth[y]) : x.CompareTo(y));
        return order;
    }

    public static Vector2 Rotate(Vector2 v, double degrees)
    {
        double r = degrees * Math.PI / 180.0;
        double c = Math.Cos(r), s = Math.Sin(r);
        return new Vector2((float)(c * v.X - s * v.Y), (float)(s * v.X + c * v.Y));
    }
}
