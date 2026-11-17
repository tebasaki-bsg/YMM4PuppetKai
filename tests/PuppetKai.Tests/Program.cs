using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using PuppetKai.Core;

static class Program
{
    static int failed, passed;
    static string outDir = "out";

    static void Check(bool ok, string name, string detail = "")
    {
        if (ok) passed++; else failed++;
        Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {name}{(detail.Length > 0 ? "  (" + detail + ")" : "")}");
    }

    static int Main(string[] args)
    {
        Directory.CreateDirectory(outDir);
        TestRig();
        TestOutline();
        TestMeshConforms();
        TestCutOpens();
        TestSlit();
        TestSeparatedPiece();
        TestPinOnCut();
        TestPolyline();
        TestIdentity();
        TestDeterminism();
        TestPentagonSamples();
        if (args.Contains("--bench")) Bench(args);
        if (args.Contains("--tol")) { TolBench(); return 0; }
        Console.WriteLine($"\n{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    // ---------- 入力の用意 ----------
    static byte[] Rect(int w, int h, int x0, int y0, int x1, int y1)
    {
        var a = new byte[w * h];
        for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) a[y * w + x] = 255;
        return a;
    }

    static byte[] Pentagon(int w, int h, out Vector2[] poly)
    {
        var c = new Vector2(w / 2f, h / 2f + 12); float r = 190;
        poly = Enumerable.Range(0, 5).Select(i => { double a = (-90 + 72 * i) * Math.PI / 180; return c + new Vector2((float)(r * Math.Cos(a)), (float)(r * Math.Sin(a))); }).ToArray();
        var a = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f); bool inside = true;
                for (int i = 0; i < 5 && inside; i++)
                {
                    var q0 = poly[i]; var q1 = poly[(i + 1) % 5];
                    inside = (q1.X - q0.X) * (p.Y - q0.Y) - (q1.Y - q0.Y) * (p.X - q0.X) >= 0;
                }
                if (inside) a[y * w + x] = 255;
            }
        return a;
    }

    static ControlSet Controls(params (Vector2 rest, Vector2 target)[] pts)
        => new ControlSet { Rest = pts.Select(p => p.rest).ToArray(), Target = pts.Select(p => p.target).ToArray() };

    static Vector2 V(float x, float y) => new(x, y);

    // ---------- テスト ----------
    static void TestRig()
    {
        var bones = new List<BoneInput>
        {
            new(true, V(0, 0), -1, 0, 100, 0, 1, 0, 100, 60),
            new(true, V(100, 0), 0, 90, 100, 0, 1, 0, 100, 60),
            new(true, V(200, 0), 1, 0, 50, 0, 1, 0, 100, 60),
        };
        var pins = new List<PinInput> { new(true, V(150, 10), V(0, 0), 1), new(false, V(5, 5), V(9, 9), -1), new(true, V(1, 2), V(3, 4), -1) };
        var cs = Rig.Compute(pins, bones, 0);
        // ボーン1：親(0,0)中心に90°回る → (0,100)
        // ボーン2：累積90° → 1 + R(90)*(100,0)*0.5 = (0,100)+(0,50) = (0,150)
        Check(Vector2.Distance(cs.Target[0], V(-10, 150)) < 1e-3, "ボーンに紐付いたピンの回転", cs.Target[0].ToString());
        Check(cs.Target[1] == V(5, 5), "無効なピンは動かない");
        Check(cs.Target[2] == V(4, 6), "紐付きなしのピンは移動X/Yだけ");
        // ボーン2の u=1 の点
        int idx = 3 + 1 + 4 + 3; // ピン3 + 根元1 + ボーン1の4点 + ボーン2の4点目
        Check(Vector2.Distance(cs.Target[idx], V(0, 150)) < 1e-3, "ボーンの伸縮", cs.Target[idx].ToString());
        Check(cs.Count == 3 + 4 + 4 + 1, "制御点の数（根元は子があるので1点）", cs.Count.ToString());
        // 揺れ：ボーン1に揺れ 30°、周期 2 秒、伝播 50%、しなり 60°。t=0.5 で sin(π/2)=1
        bones[1] = bones[1] with { Angle = 0, SwingAngle = 30, SwingPeriod = 2, SwingPropagation = 50, Whip = 60 };
        var parent = new[] { -1, 0, 1 };
        var sw = Rig.SwingAngles(bones, parent, 0.5);
        Check(Math.Abs(sw[1] - 30) < 1e-9 && Math.Abs(sw[2] - 30 * 0.5 * Math.Sin(Math.PI / 2 - Math.PI / 3)) < 1e-9 && sw[0] == 0, "揺れの式", string.Join(",", sw.Select(v => v.ToString("F3"))));
    }

    static void TestOutline()
    {
        var a = Rect(100, 80, 20, 10, 80, 70);
        // 穴をあける
        for (int y = 30; y < 50; y++) for (int x = 40; x < 60; x++) a[y * 100 + x] = 0;
        var o = AlphaOutline.Build(a, 100, 80);
        Check(o.Loops.Count == 2, "輪郭：外側と穴の2周", o.Loops.Count.ToString());
        Check(o.Contains(V(-25, 0)) && !o.Contains(V(0, 0)) && !o.Contains(V(-49, -39)), "輪郭：内外判定（穴の中は外）");
        Check(o.Contains(V(-30 - 1.5f, 0)) && !o.Contains(V(-30 - 3.5f, 0)), "輪郭：2px 広げてある");
    }

    static bool SegCross(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        // 符号付き距離が両側とも 0.01px を超えて反対側にあるときだけ「またぐ」
        float lab = (b - a).Length(), lcd = (d - c).Length();
        float o1 = Cross(b - a, c - a) / lab, o2 = Cross(b - a, d - a) / lab, o3 = Cross(d - c, a - c) / lcd, o4 = Cross(d - c, b - c) / lcd;
        return ((o1 > 0.01f && o2 < -0.01f) || (o1 < -0.01f && o2 > 0.01f)) && ((o3 > 0.01f && o4 < -0.01f) || (o3 < -0.01f && o4 > 0.01f));
    }
    static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    static void TestMeshConforms()
    {
        var a = Pentagon(440, 440, out _);
        var o = AlphaOutline.Build(a, 440, 440);
        var cuts = new List<CutSegment> { new(V(-70, 240), V(8, 18)), new(V(-150, -50), V(120, 40)) };
        var sw = Stopwatch.StartNew();
        var m = MeshBuilder.Build(o, cuts)!;
        double ms = sw.Elapsed.TotalMilliseconds;
        int crossing = 0, flipped = 0;
        for (int t = 0; t < m.TriangleCount; t++)
        {
            var p = new[] { m.Rest[m.Triangles[3 * t]], m.Rest[m.Triangles[3 * t + 1]], m.Rest[m.Triangles[3 * t + 2]] };
            if (Cross(p[1] - p[0], p[2] - p[0]) <= 0) flipped++;
            foreach (var c in cuts)
                for (int k = 0; k < 3; k++) if (SegCross(p[k], p[(k + 1) % 3], c.Start, c.End)) crossing++;
        }
        Check(m.TriangleCount <= MeshBuilder.MaxTriangles, "網目：三角形の上限", $"{m.TriangleCount} 個, 頂点 {m.VertexCount}, 間隔 {m.Spacing:F1}px, {ms:F0}ms");
        Check(crossing == 0, "網目：切れ込みをまたぐ辺が無い", crossing.ToString());
        Check(flipped == 0, "網目：裏返った三角形が無い", flipped.ToString());
        // 2本の切れ込みが交わる → 4つに分かれる所がある。全体はつながっている（閉じた端があるので）
        Check(m.ComponentCount == 1, "網目：閉じた端のある切れ込みでは全体はつながったまま", m.ComponentCount.ToString());
    }

    static (DeformResult r, PuppetDeformer d) Run(byte[] alpha, int w, int h, List<CutSegment> cuts, ControlSet cs, DeformMode mode, double rigidity = 2)
    {
        var d = new PuppetDeformer();
        d.SetAlpha(alpha, w, h);
        return (d.Deform(cuts, cs, mode, rigidity)!, d);
    }

    /// <summary>同じ位置にある頂点の組（切れ込みで複製された頂点）のうち、一番離れたものの距離。</summary>
    static float MaxSplitGap(DeformResult r, Func<Vector2, bool> where)
    {
        var m = r.Mesh; float best = 0;
        var byPos = new Dictionary<(float, float), List<int>>();
        for (int v = 0; v < m.VertexCount; v++) { var k = (m.Rest[v].X, m.Rest[v].Y); if (!byPos.TryGetValue(k, out var l)) byPos[k] = l = new(); l.Add(v); }
        foreach (var (k, l) in byPos)
        {
            if (l.Count < 2 || !where(new Vector2(k.Item1, k.Item2))) continue;
            for (int i = 0; i < l.Count; i++) for (int j = i + 1; j < l.Count; j++) best = Math.Max(best, Vector2.Distance(r.Deformed[l[i]], r.Deformed[l[j]]));
        }
        return best;
    }

    static int DuplicateCount(PuppetMesh m, Func<Vector2, bool> where)
        => m.Rest.Select((p, i) => (p, i)).Where(x => where(x.p)).GroupBy(x => (x.p.X, x.p.Y)).Count(g => g.Count() > 1);

    static void TestCutOpens()
    {
        // 300x200 の四角。上端の外から中央まで縦の切れ込み
        var a = Rect(400, 300, 50, 50, 350, 250);
        var cuts = new List<CutSegment> { new(V(0, -120), V(0, 0)) };
        var cs = Controls((V(-120, -80), V(-170, -70)), (V(120, -80), V(170, -70)), (V(-120, 80), V(-120, 80)), (V(120, 80), V(120, 80)));
        foreach (var mode in new[] { DeformMode.Rigid, DeformMode.Smooth })
        {
            var (r, _) = Run(a, 400, 300, cuts, cs, mode);
            float topGap = MaxSplitGap(r, p => p.Y < -95);
            Check(topGap > 40, $"切れ込みが開く（{mode}）", $"上端の開き {topGap:F1}px, 反復 {r.Iterations}");
            Check(DuplicateCount(r.Mesh, p => p.Y > -1 && Math.Abs(p.X) < 0.01) == 0, $"閉じた端は複製しない（{mode}）");
            Dump(r, $"cut_{mode}");
        }
        // 比較：切れ込みなし
        var (r0, _) = Run(a, 400, 300, new(), cs, DeformMode.Rigid);
        Check(MaxSplitGap(r0, _ => true) == 0 && r0.Mesh.VertexCount > 0, "切れ込みなしでは分かれない");
    }

    static void TestSlit()
    {
        var a = Rect(400, 300, 50, 50, 350, 250);
        var cuts = new List<CutSegment> { new(V(-80, 0), V(80, 0)) };
        var cs = Controls((V(0, -25), V(0, -55)), (V(0, 25), V(0, 55)), (V(-130, -80), V(-130, -80)), (V(130, -80), V(130, -80)), (V(-130, 80), V(-130, 80)), (V(130, 80), V(130, 80)));
        var (r, _) = Run(a, 400, 300, cuts, cs, DeformMode.Rigid);
        float mid = MaxSplitGap(r, p => Math.Abs(p.X) < 10);
        Check(mid > 30, "両端とも閉じた切れ込みは穴として開く", $"中央の開き {mid:F1}px");
        Check(DuplicateCount(r.Mesh, p => Math.Abs(Math.Abs(p.X) - 80) < 0.01 && Math.Abs(p.Y) < 0.01) == 0, "両端の点は共有");
        Dump(r, "slit");
    }

    static void TestSeparatedPiece()
    {
        var a = Rect(400, 300, 50, 50, 350, 250);
        var cuts = new List<CutSegment> { new(V(-200, 40), V(200, 40)) }; // 両端とも外 → 上下に分かれる
        // 上の部分だけにピン。全体を右へ 60px
        var cs = Controls((V(-100, -60), V(-40, -60)), (V(100, -60), V(160, -60)), (V(0, -20), V(60, -20)));
        var (r, _) = Run(a, 400, 300, cuts, cs, DeformMode.Rigid);
        Check(r.Mesh.ComponentCount == 2, "両端とも開いた切れ込みで2つに分かれる", r.Mesh.ComponentCount.ToString());
        var m = r.Mesh;
        var lower = Enumerable.Range(0, m.VertexCount).Where(v => m.Rest[v].Y > 45).ToList();
        var shift = lower.Select(v => r.Deformed[v] - m.Rest[v]).Aggregate(Vector2.Zero, (s, d) => s + d) / lower.Count;
        Check(shift.X > 30, "ピンの無いかたまりは直線距離の MLS で近くのピンに合わせて動く", $"平均の移動 ({shift.X:F1}, {shift.Y:F1})");
        Dump(r, "separated");
    }

    static void TestPinOnCut()
    {
        var a = Rect(400, 300, 50, 50, 350, 250);
        var cut = new CutSegment(V(0, -120), V(0, 0)); // 上から下へ：右側 = (-dy,dx) = (-1,0)*... = X が負の側
        var o = AlphaOutline.Build(a, 400, 300);
        var m = MeshBuilder.Build(o, new List<CutSegment> { cut })!;
        var at = m.Locate(V(0, -50));
        var cen = (m.Rest[at.V0] + m.Rest[at.V1] + m.Rest[at.V2]) / 3;
        var d = cut.End - cut.Start; var right = new Vector2(-d.Y, d.X);
        Check(Vector2.Dot(cen - cut.Start, right) > 0, "切れ込みの線上のピンは、始点から終点を見て右側に付く", $"三角形の重心 {cen}");
    }

    static void TestPolyline()
    {
        // L 字の折れ線：上の輪郭の外 (0,-120) → 曲がり角 (0,0) → 閉じた端 (80,0)
        var a = Rect(400, 300, 50, 50, 350, 250);
        var cuts = new List<CutSegment> { new(V(0, -120), V(0, 0)), new(V(0, 0), V(80, 0)) };
        // 曲がり角の右上の部分を上へ、下の部分を下へ。左側はとめておく
        var cs = Controls((V(40, -40), V(40, -75)), (V(40, 40), V(40, 75)), (V(-120, -80), V(-120, -80)), (V(-120, 80), V(-120, 80)), (V(130, -80), V(130, -80)), (V(130, 80), V(130, 80)));
        var (r, _) = Run(a, 400, 300, cuts, cs, DeformMode.Rigid);
        Check(DuplicateCount(r.Mesh, p => p.LengthSquared() < 1e-4f) > 0, "折れ線の曲がり角は分かれる");
        Check(DuplicateCount(r.Mesh, p => Vector2.DistanceSquared(p, V(80, 0)) < 1e-4f) == 0, "折れ線の閉じた端は共有");
        float gap = MaxSplitGap(r, p => Math.Abs(p.Y) < 0.01f && p.X > 20 && p.X < 60);
        Check(gap > 20, "折れ線の2本目の線分に沿って開く", $"開き {gap:F1}px");
        Check(r.Mesh.ComponentCount == 1, "閉じた端があるので全体はつながったまま", r.Mesh.ComponentCount.ToString());
        Dump(r, "polyline");
    }

    static void TestIdentity()
    {
        var a = Pentagon(440, 440, out _);
        var cuts = new List<CutSegment> { new(V(-70, 240), V(8, 18)) };
        var pts = new[] { V(0, -150), V(-150, -50), V(150, -50), V(-100, 150), V(100, 150) };
        var cs = Controls(pts.Select(p => (p, p)).ToArray());
        foreach (var mode in new[] { DeformMode.Rigid, DeformMode.Smooth })
        {
            var (r, _) = Run(a, 440, 440, cuts, cs, mode);
            float mx = 0; for (int v = 0; v < r.Mesh.VertexCount; v++) mx = Math.Max(mx, Vector2.Distance(r.Deformed[v], r.Mesh.Rest[v]));
            Check(mx < 0.05f, $"ピンを動かさなければ変形しない（{mode}）", $"最大のずれ {mx:F4}px");
        }
    }

    static void TestDeterminism()
    {
        var a = Pentagon(440, 440, out _);
        var cuts = new List<CutSegment> { new(V(-70, 240), V(8, 18)) };
        var cs = Controls((V(0, -150), V(30, -140)), (V(-100, 150), V(-170, 175)), (V(100, 150), V(145, 160)), (V(-150, -50), V(-150, -50)));
        var (r1, _) = Run(a, 440, 440, cuts, cs, DeformMode.Rigid);
        var (r2, _) = Run(a, 440, 440, cuts, cs, DeformMode.Rigid);
        Check(r1.Deformed.SequenceEqual(r2.Deformed), "同じ入力なら同じ結果（作り直しても）");
    }

    static void TestPentagonSamples()
    {
        var a = Pentagon(440, 440, out _);
        File.WriteAllBytes(Path.Combine(outDir, "pentagon.raw"), a);
        // 下端の外から中央へ斜めの切れ込み、下の2本を引き離す
        var cuts = new List<CutSegment> { new(V(-70, 250), V(8, 18)) };
        var rest = new[] { V(-20, -162), V(-160, -62), V(160, -62), V(-102, 148), V(102, 148) };
        var tgt = new[] { V(-20, -162), V(-160, -62), V(160, -62), V(-172, 173), V(147, 158) };
        var cs = Controls(rest.Zip(tgt).ToArray());
        foreach (var mode in new[] { DeformMode.Rigid, DeformMode.Smooth })
        {
            var (r, _) = Run(a, 440, 440, cuts, cs, mode);
            Dump(r, $"pentagon_{mode}");
        }
    }

    static void Dump(DeformResult r, string name)
    {
        var o = new
        {
            rest = r.Mesh.Rest.SelectMany(p => new[] { p.X, p.Y }).ToArray(),
            deformed = r.Deformed.SelectMany(p => new[] { p.X, p.Y }).ToArray(),
            tris = r.Mesh.Triangles,
            cuts = r.Mesh.Cuts.Select(c => new[] { c.Start.X, c.Start.Y, c.End.X, c.End.Y }).ToArray(),
        };
        File.WriteAllText(Path.Combine(outDir, name + ".json"), JsonSerializer.Serialize(o));
    }

    // ---------- 計測 ----------
    static void Bench(string[] args)
    {
        string path = args.SkipWhile(x => x != "--bench").Skip(1).FirstOrDefault() ?? "hand_mask.raw";
        int W = 560, H = 600;
        var a = File.ReadAllBytes(path).Select(b => b != 0 ? (byte)255 : (byte)0).ToArray();
        Vector2 C(float x, float y) => new(x - W / 2f, y - H / 2f);
        var rests = new[] { C(145, 123), C(280, 60), C(405, 135), C(515, 235), C(45, 352), C(280, 480), C(200, 560), C(380, 560) };
        Vector2[] Targets(int f)
        {
            var t = (Vector2[])rests.Clone(); float s = MathF.Sin(f * 0.1f);
            t[0] += new Vector2(-60 * s, 20 * s); t[1] += new Vector2(0, -50 * s); t[3] += new Vector2(70 * s, 40 * s); t[4] += new Vector2(-40 * s, -60 * s);
            return t;
        }
        var sw = Stopwatch.StartNew();
        var o = AlphaOutline.Build(a, W, H); double tOutline = sw.Elapsed.TotalMilliseconds;
        sw.Restart(); var m = MeshBuilder.Build(o, new List<CutSegment>())!; double tMesh = sw.Elapsed.TotalMilliseconds;
        sw.Restart(); var field = new ControlField(m, rests); double tField = sw.Elapsed.TotalMilliseconds;
        sw.Restart(); var arap = new ArapSolver(m, field.Attach); double tFactor = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"\n[計測] 手 {W}x{H}: 頂点 {m.VertexCount}, 三角形 {m.TriangleCount}, 帯の幅 {arap.Bandwidth}, 行列の要素 {arap.MatrixEntries}");
        Console.WriteLine($"  輪郭 {tOutline:F1}ms, 網目 {tMesh:F1}ms, 距離 {tField:F1}ms, 分解 {tFactor:F1}ms");
        var init = new Vector2[m.VertexCount]; var res = new Vector2[m.VertexCount];
        for (int f = 0; f < 20; f++) { field.Deform(Targets(f), 1, init); arap.Solve(init, Targets(f), res); }
        int N = 200; double tm = 0, ta = 0; int its = 0;
        for (int f = 0; f < N; f++)
        {
            sw.Restart(); field.Deform(Targets(f), 1, init); tm += sw.Elapsed.TotalMilliseconds;
            sw.Restart(); arap.Solve(init, Targets(f), res); ta += sw.Elapsed.TotalMilliseconds; its += arap.LastIterations;
        }
        Console.WriteLine($"  1フレーム：MLS {tm / N:F2}ms + ARAP {ta / N:F2}ms（平均 {(double)its / N:F1} 回）");
        // 精度：許容量を極小にした解との差
        var t16 = Targets(16);
        field.Deform(t16, 1, init); arap.Solve(init, t16, res);
        Console.WriteLine($"  t=16 の反復 {arap.LastIterations} 回");
        // 精度：十分に計算した解との差（いくつかのフレームの最大）
        var refSolver = new ArapSolver(m, field.Attach) { Tolerance = 1e-7, MaxIterations = 3000 };
        var refRes = new Vector2[m.VertexCount];
        float worst = 0;
        for (int f = 0; f < N; f += 10)
        {
            var tg = Targets(f);
            field.Deform(tg, 1, init); arap.Solve(init, tg, res); refSolver.Solve(init, tg, refRes);
            for (int v = 0; v < m.VertexCount; v++) worst = Math.Max(worst, Vector2.Distance(res[v], refRes[v]));
        }
        Console.WriteLine($"  十分に計算した解とのずれ：最大 {worst:F3}px");
        foreach (var tol in new[] { 0.02, 0.01, 0.005, 0.002 })
        {
            var sv = new ArapSolver(m, field.Attach) { Tolerance = tol };
            double tt = 0; int it2 = 0; float w2 = 0; int cnt = 0;
            for (int f = 0; f < N; f += 5)
            {
                var tg = Targets(f); field.Deform(tg, 1, init);
                sw.Restart(); sv.Solve(init, tg, res); tt += sw.Elapsed.TotalMilliseconds; it2 += sv.LastIterations; cnt++;
                refSolver.Solve(init, tg, refRes);
                for (int v = 0; v < m.VertexCount; v++) w2 = Math.Max(w2, Vector2.Distance(res[v], refRes[v]));
            }
            Console.WriteLine($"  打ち切り {tol}: {tt / cnt:F2}ms, 平均 {(double)it2 / cnt:F1} 回, 最大のずれ {w2:F3}px");
        }
    }

    // 打ち切りと「前のフレームから始める」の比較（揺れのように毎フレーム少しずつ動く場合）
    static void TolBench()
    {
        int W = 560, H = 600;
        var a = File.ReadAllBytes("hand_mask.raw").Select(b => b != 0 ? (byte)255 : (byte)0).ToArray();
        Vector2 C(float x, float y) => new(x - W / 2f, y - H / 2f);
        var rests = new[] { C(145, 123), C(280, 60), C(405, 135), C(515, 235), C(45, 352), C(280, 480), C(200, 560), C(380, 560) };
        // 30fps で周期 1 秒の揺れ（1フレームで 12 度ずつ位相が進む）
        Vector2[] Targets(int f)
        {
            var t = (Vector2[])rests.Clone(); float s = MathF.Sin(f * 2 * MathF.PI / 30f);
            t[0] += new Vector2(-60 * s, 20 * s); t[1] += new Vector2(0, -50 * s); t[3] += new Vector2(70 * s, 40 * s); t[4] += new Vector2(-40 * s, -60 * s);
            return t;
        }
        var o = AlphaOutline.Build(a, W, H);
        var m = MeshBuilder.Build(o, new List<CutSegment>())!;
        var field = new ControlField(m, rests);
        int n = m.VertexCount, N = 120;
        var init = new Vector2[n]; var res = new Vector2[n]; var prev = new Vector2[n];
        var refSolver = new ArapSolver(m, field.Attach) { Tolerance = 1e-7, MaxIterations = 3000 };
        var refs = new Vector2[N][];
        for (int f = 0; f < N; f++) { field.Deform(Targets(f), 1, init); refs[f] = new Vector2[n]; refSolver.Solve(init, Targets(f), refs[f]); }
        Console.WriteLine($"[打ち切りの比較] 手 {W}x{H}、頂点 {n}、{N} フレーム（30fps・周期1秒の揺れ）");
        var sw = new Stopwatch();
        foreach (bool warm in new[] { false, true })
        foreach (var tol in new[] { 0.02, 0.05, 0.1, 0.25, 0.5 })
        {
            var sv = new ArapSolver(m, field.Attach) { Tolerance = tol };
            // 1周目は温め、2周目を測る
            double tt = 0; int its = 0; float worst = 0; double mean = 0; bool havePrev = false;
            for (int pass = 0; pass < 2; pass++)
            {
                tt = 0; its = 0; worst = 0; mean = 0; havePrev = false;
                for (int f = 0; f < N; f++)
                {
                    var tg = Targets(f); field.Deform(tg, 1, init);
                    sw.Restart();
                    if (warm && havePrev) sv.Solve(init, tg, res, prev); else sv.Solve(init, tg, res);
                    tt += sw.Elapsed.TotalMilliseconds; its += sv.LastIterations;
                    float fw = 0; double fm = 0;
                    for (int v = 0; v < n; v++) { float d = Vector2.Distance(res[v], refs[f][v]); fw = Math.Max(fw, d); fm += d; }
                    worst = Math.Max(worst, fw); mean += fm / n;
                    res.CopyTo(prev, 0); havePrev = true;
                }
            }
            Console.WriteLine($"  {(warm ? "前のフレームから" : "毎回はじめから")} 打ち切り {tol,4}: {tt / N,6:F2}ms  繰り返し {(double)its / N,5:F1}回  ずれ 平均 {mean / N:F3}px・最大 {worst:F3}px");
        }
    }
}
