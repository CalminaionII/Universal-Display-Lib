using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace UniversalDisplayLib.Reach
{
    /// <summary>
    /// A window's boxes, measured from its model the way the AutoBoxes Kit measures (one box round the parts),
    /// split into what moves and what does not (2026-10-06, the rebuild on the kit):
    ///   fixed parts - every part no pane's open animation moves (frame edges, sill, fixed glass), EACH ITS OWN BOX
    ///                 (Calm: "each part has a box"); a part wholly inside another part's box is left out;
    ///   each pane   - the parts its open animation moves as ONE box, shut (as in the file) and open (at the animation's end).
    /// The kit itself measures the model standing still; this is the window's own addition for leaves that swing
    /// out of the block (the External Shutter's fold into the blocks beside it). Measured once per block type.
    /// In the model's frame, in blocks, before the window's own turn.
    /// </summary>
    public sealed class PaneBoxes
    {
        public Cuboidf[] Fixed { get; private init; } = Array.Empty<Cuboidf>();

        /// <summary>
        /// Every part in no leaf and no Static group - the casing, the sill - gets collision
        /// and an unoutlined selection blocker. It stops movement and aiming through, with no click action.
        /// </summary>
        public Cuboidf[] Solid { get; private init; } = Array.Empty<Cuboidf>();
        public Cuboidf[] Shut { get; private init; } = Array.Empty<Cuboidf>();
        public Cuboidf[] Open { get; private init; } = Array.Empty<Cuboidf>();

        /// <summary>
        /// Per pane, the path it swings through (Calm, 2026-10-07: a classic window opened through a closed shutter leaf - its
        /// end pose missed the leaf, its swing did not): the leaf's parts, EACH ITS OWN BOX so a diagonal leaf is not one
        /// huge box, at <see cref="SweepSteps"/> points along its open animation, the end included. Only for room checks -
        /// boxes and fillers still follow <see cref="Open"/>.
        /// </summary>
        public Cuboidf[][] Sweep { get; private init; } = Array.Empty<Cuboidf[]>();

        /// <summary>
        /// Per pane, where it really is when open: the leaf's parts, each its own box, at the end of its open animation - also
        /// for a pane whose box stays shut (boxStaysShut: an edge awning reaches into the block in front with no box there).
        /// For the check before something ELSE shuts where it is (BEUniversalDisplay.OpenNeighbourInTheWay).
        /// </summary>
        public Cuboidf[][] OpenParts { get; private init; } = Array.Empty<Cuboidf[]>();

        const int SweepSteps = 12;

        static readonly Dictionary<string, PaneBoxes> Cache = new();

        /// <summary>The boxes for a block whose shape is <paramref name="shape"/> (psurface markers already in its ignore list) and whose panes open with these animations.</summary>
        public static PaneBoxes For(ICoreAPI api, CompositeShape shape, string[] animOpen)
        {
            if (shape?.Base == null) return null;
            AssetLocation loc = shape.Base.Clone().WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json");
            string key = loc + "|" + string.Join(",", shape.IgnoreElements ?? Array.Empty<string>()) + "|" + string.Join(",", animOpen);
            lock (Cache)
            {
                if (Cache.TryGetValue(key, out var cached)) return cached;
                Shape model = Shape.TryGet(api, loc);
                PaneBoxes made = model == null ? null : Measure(model, animOpen, shape.IgnoreElements);
                Cache[key] = made;
                return made;
            }
        }

        static PaneBoxes Measure(Shape model, string[] animOpen, string[] ignore)
        {
            var skip = new HashSet<string>(ignore ?? Array.Empty<string>());
            var still = Parts(model, (Animation)null, skip);
            var moving = new HashSet<string>[animOpen.Length];
            var open = new Cuboidf[animOpen.Length];
            var shut = new Cuboidf[animOpen.Length];
            var sweep = new Cuboidf[animOpen.Length][];
            var openParts = new Cuboidf[animOpen.Length][];
            for (int i = 0; i < animOpen.Length; i++)
            {
                Animation anim = Find(model, animOpen[i]);
                moving[i] = Moved(anim);
                sweep[i] = Array.Empty<Cuboidf>();
                openParts[i] = Array.Empty<Cuboidf>();
                if (moving[i].Count == 0) continue;
                HashSet<string> roots = moving[i];
                shut[i] = Bounds(still.Where(p => p.chain.Any(roots.Contains)));
                var openLeaf = Parts(model, anim, skip).Where(p => p.chain.Any(roots.Contains)).ToList();
                open[i] = Bounds(openLeaf);
                openParts[i] = openLeaf.Select(p => Bounds(new[] { p })).Where(b => b != null).ToArray();

                double last = anim.KeyFrames.Where(k => k != null).Select(k => (double)k.Frame).DefaultIfEmpty(0).Max();
                var path = new List<Cuboidf>();
                for (int s = 1; s <= SweepSteps; s++)
                    foreach (var part in Parts(model, PoseAt(anim, last * s / SweepSteps), skip).Where(p => p.chain.Any(roots.Contains)))
                        path.Add(Bounds(new[] { part }));
                sweep[i] = path.Where(b => b != null).ToArray();
            }
            var allMoving = new HashSet<string>(moving.SelectMany(m => m));
            var fixedParts = still.Where(p => !p.chain.Any(allMoving.Contains)).ToList();
            Cuboidf[] groups = StaticGroups(fixedParts);
            return new PaneBoxes
            {
                // No Static group: fixed parts become unoutlined selection blockers through Solid.
                Fixed = groups ?? Array.Empty<Cuboidf>(),
                Solid = Joined(Distinct(fixedParts.Where(p => !p.chain.Any(IsStatic)).Select(p => Bounds(new[] { p })))),
                Shut = shut,
                Open = open,
                Sweep = sweep,
                OpenParts = openParts
            };
        }

        /// <summary>The model's animation with this code (or, with no code, this name in lower case), or null.</summary>
        static Animation Find(Shape model, string code)
        {
            if (string.IsNullOrEmpty(code) || model.Animations == null) return null;
            return model.Animations.FirstOrDefault(a => a != null && string.Equals(a.Code ?? a.Name?.ToLowerInvariant(), code, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The elements an animation turns or moves in any keyframe - with everything inside them, they are its leaf.</summary>
        static HashSet<string> Moved(Animation anim)
        {
            var names = new HashSet<string>();
            foreach (var frame in anim?.KeyFrames ?? Array.Empty<AnimationKeyFrame>())
            {
                if (frame?.Elements == null) continue;
                foreach (var (name, k) in frame.Elements)
                {
                    if (k == null) continue;
                    if ((k.RotationX ?? 0) != 0 || (k.RotationY ?? 0) != 0 || (k.RotationZ ?? 0) != 0
                        || (k.OffsetX ?? 0) != 0 || (k.OffsetY ?? 0) != 0 || (k.OffsetZ ?? 0) != 0) names.Add(name);
                }
            }
            return names;
        }

        /// <summary>Each element's turn and move at the animation's LAST keyframe: rotX, rotY, rotZ, offX, offY, offZ.</summary>
        static Dictionary<string, double[]> PoseAtEnd(Animation anim)
        {
            var pose = new Dictionary<string, double[]>();
            var last = anim?.KeyFrames?.Where(k => k != null).OrderBy(k => k.Frame).LastOrDefault();
            if (last?.Elements == null) return pose;
            foreach (var (name, k) in last.Elements)
            {
                if (k == null) continue;
                pose[name] = new[] { k.RotationX ?? 0, k.RotationY ?? 0, k.RotationZ ?? 0, k.OffsetX ?? 0, k.OffsetY ?? 0, k.OffsetZ ?? 0 };
            }
            return pose;
        }

        /// <summary>
        /// Each element's turn and move part way through the animation: straight lines between that element's own keyframes
        /// (the game blends keyframes the same way); before its first keyframe it starts from rest at frame 0.
        /// </summary>
        static Dictionary<string, double[]> PoseAt(Animation anim, double frame)
        {
            var keys = new Dictionary<string, List<(double frame, double[] v)>>();
            foreach (var k in anim?.KeyFrames ?? Array.Empty<AnimationKeyFrame>())
            {
                if (k?.Elements == null) continue;
                foreach (var (name, e) in k.Elements)
                {
                    if (e == null) continue;
                    if (!keys.TryGetValue(name, out var list)) keys[name] = list = new();
                    list.Add((k.Frame, new[] { e.RotationX ?? 0, e.RotationY ?? 0, e.RotationZ ?? 0, e.OffsetX ?? 0, e.OffsetY ?? 0, e.OffsetZ ?? 0 }));
                }
            }
            var pose = new Dictionary<string, double[]>();
            foreach (var (name, list) in keys)
            {
                list.Sort((a, b) => a.frame.CompareTo(b.frame));
                if (list[0].frame > 0) list.Insert(0, (0, new double[6]));
                var (f0, v0) = list[0];
                var (f1, v1) = list[0];
                foreach (var key in list)
                {
                    if (key.frame <= frame) { (f0, v0) = key; (f1, v1) = key; continue; }
                    (f1, v1) = key;
                    break;
                }
                double t = f1 > f0 ? Math.Clamp((frame - f0) / (f1 - f0), 0, 1) : 0;
                pose[name] = Enumerable.Range(0, 6).Select(i => v0[i] + (v1[i] - v0[i]) * t).ToArray();
            }
            return pose;
        }

        /// <summary>Every sized element's upright box (blocks), with the names of itself and every element it sits inside.</summary>
        static List<(string[] chain, double[] box)> Parts(Shape model, Animation anim, HashSet<string> skip) =>
            Parts(model, PoseAtEnd(anim), skip);

        static List<(string[] chain, double[] box)> Parts(Shape model, Dictionary<string, double[]> pose, HashSet<string> skip)
        {
            var parts = new List<(string[], double[])>();
            foreach (var e in model.Elements ?? Array.Empty<ShapeElement>()) Walk(e, Ident(), Array.Empty<string>(), pose, skip, parts);
            return parts;
        }

        // As the game places an element (as the kit's AutoBoxesMeasure), plus the animation's turn and move:
        // T(origin) * Rx * Ry * Rz * S * T(from + move - origin); a child's matrix is its parent's times its own.
        static void Walk(ShapeElement e, double[,] parent, string[] chain, Dictionary<string, double[]> pose, HashSet<string> skip, List<(string[], double[])> parts)
        {
            if (e == null || (e.Name != null && skip.Contains(e.Name))) return;
            string[] mine = chain.Append(e.Name ?? "").ToArray();
            double[] o = (e.RotationOrigin ?? new double[3]).Select(v => v / 16).ToArray();
            double[] f = (e.From ?? new double[3]).Select(v => v / 16).ToArray();
            double[] t = (e.To ?? new double[3]).Select(v => v / 16).ToArray();
            double[] p = pose.TryGetValue(e.Name ?? "", out var q) ? q : new double[6];
            var m = Mul(parent, Trans(o[0], o[1], o[2]));
            m = Mul(m, Rot('x', e.RotationX + p[0]));
            m = Mul(m, Rot('y', e.RotationY + p[1]));
            m = Mul(m, Rot('z', e.RotationZ + p[2]));
            m = Mul(m, Scale(e.ScaleX, e.ScaleY, e.ScaleZ));
            m = Mul(m, Trans(f[0] + p[3] / 16 - o[0], f[1] + p[4] / 16 - o[1], f[2] + p[5] / 16 - o[2]));
            double sx = t[0] - f[0], sy = t[1] - f[1], sz = t[2] - f[2];
            if (Math.Abs(sx) > 1e-6 && Math.Abs(sy) > 1e-6 && Math.Abs(sz) > 1e-6)   // a zero-size element is a pivot, not a part
            {
                double[] box = { double.MaxValue, double.MaxValue, double.MaxValue, double.MinValue, double.MinValue, double.MinValue };
                foreach (double dx in new[] { 0, sx }) foreach (double dy in new[] { 0, sy }) foreach (double dz in new[] { 0, sz })
                    for (int i = 0; i < 3; i++)
                    {
                        double v = m[i, 0] * dx + m[i, 1] * dy + m[i, 2] * dz + m[i, 3];
                        box[i] = Math.Min(box[i], v); box[i + 3] = Math.Max(box[i + 3], v);
                    }
                parts.Add((mine, box));
            }
            foreach (var c in e.Children ?? Array.Empty<ShapeElement>()) Walk(c, m, mine, pose, skip, parts);
        }

        static Cuboidf Bounds(IEnumerable<(string[] chain, double[] box)> parts)
        {
            double[] b = { double.MaxValue, double.MaxValue, double.MaxValue, double.MinValue, double.MinValue, double.MinValue };
            foreach (var (_, box) in parts)
                for (int i = 0; i < 3; i++) { b[i] = Math.Min(b[i], box[i]); b[i + 3] = Math.Max(b[i + 3], box[i + 3]); }
            if (b[0] > b[3]) return null;
            return new Cuboidf((float)b[0], (float)b[1], (float)b[2], (float)b[3], (float)b[4], (float)b[5]);
        }

        /// <summary>
        /// The shape decides (Calm, 2026-10-06): every element whose name starts with "Static" is a group - ONE box round
        /// everything inside it, as a leaf is one box round everything its animation moves. A Static group inside another is
        /// its own box (its parts not counted in the outer one); one inside a leaf is part of the leaf. A part in no Static
        /// group and no leaf gets collision and unoutlined selection (Solid). Null when the shape has no Static group at all.
        /// </summary>
        static bool IsStatic(string name) => name != null && name.StartsWith("static", StringComparison.OrdinalIgnoreCase);

        /// <param name="parts">The parts no animation moves.</param>
        static Cuboidf[] StaticGroups(List<(string[] chain, double[] box)> parts)
        {
            var groups = new Dictionary<string, List<(string[], double[])>>();
            var order = new List<string>();
            foreach (var part in parts)
            {
                string group = part.chain.LastOrDefault(IsStatic);   // the innermost Static group it sits in (or is)
                if (group == null) continue;
                if (!groups.TryGetValue(group, out var list)) { groups[group] = list = new(); order.Add(group); }
                list.Add(part);
            }
            if (order.Count == 0 && !parts.Any(p => p.chain.Any(IsStatic))) return null;
            return order.Select(g => Bounds(groups[g])).Where(b => b != null).ToArray();
        }

        /// <summary>The boxes, less any that sit wholly inside another (a frame's corner block inside its edge).</summary>
        static Cuboidf[] Distinct(IEnumerable<Cuboidf> boxes)
        {
            const float eps = 1e-4f;
            var list = boxes.Where(b => b != null).ToList();
            bool Inside(Cuboidf a, Cuboidf b) => a.X1 >= b.X1 - eps && a.Y1 >= b.Y1 - eps && a.Z1 >= b.Z1 - eps
                && a.X2 <= b.X2 + eps && a.Y2 <= b.Y2 + eps && a.Z2 <= b.Z2 + eps;
            var kept = new List<Cuboidf>();
            for (int i = 0; i < list.Count; i++)
            {
                // inside a later box, or inside an earlier KEPT one (two identical boxes keep the first)
                bool covered = list.Skip(i + 1).Any(b => Inside(list[i], b) && !Inside(b, list[i])) || kept.Any(k => Inside(list[i], k));
                if (!covered) kept.Add(list[i]);
            }
            return kept.ToArray();
        }

        /// <summary>
        /// Fixed parts that line up end to end - the same size across two directions, touching or overlapping along the
        /// third - become one box. A tall window's model is its old 1-block sections joined, so each frame edge is one
        /// piece per section; joined, the edge is one box the whole height, as the leaves are (Calm, 2026-10-06).
        /// </summary>
        static Cuboidf[] Joined(Cuboidf[] boxes)
        {
            const float eps = 1e-3f;
            var list = boxes.ToList();
            bool Same(float a, float b) => Math.Abs(a - b) <= eps;
            bool merged = true;
            while (merged)
            {
                merged = false;
                for (int i = 0; i < list.Count && !merged; i++)
                for (int j = i + 1; j < list.Count && !merged; j++)
                {
                    Cuboidf a = list[i], b = list[j];
                    bool x = Same(a.X1, b.X1) && Same(a.X2, b.X2), y = Same(a.Y1, b.Y1) && Same(a.Y2, b.Y2), z = Same(a.Z1, b.Z1) && Same(a.Z2, b.Z2);
                    bool touchX = a.X1 <= b.X2 + eps && b.X1 <= a.X2 + eps;
                    bool touchY = a.Y1 <= b.Y2 + eps && b.Y1 <= a.Y2 + eps;
                    bool touchZ = a.Z1 <= b.Z2 + eps && b.Z1 <= a.Z2 + eps;
                    if ((y && z && touchX) || (x && z && touchY) || (x && y && touchZ))
                    {
                        list[i] = new Cuboidf(Math.Min(a.X1, b.X1), Math.Min(a.Y1, b.Y1), Math.Min(a.Z1, b.Z1), Math.Max(a.X2, b.X2), Math.Max(a.Y2, b.Y2), Math.Max(a.Z2, b.Z2));
                        list.RemoveAt(j);
                        merged = true;
                    }
                }
            }
            return list.ToArray();
        }

        static double[,] Ident() => new double[,] { { 1, 0, 0, 0 }, { 0, 1, 0, 0 }, { 0, 0, 1, 0 }, { 0, 0, 0, 1 } };
        static double[,] Trans(double x, double y, double z) { var m = Ident(); m[0, 3] = x; m[1, 3] = y; m[2, 3] = z; return m; }
        static double[,] Scale(double x, double y, double z) { var m = Ident(); m[0, 0] = x; m[1, 1] = y; m[2, 2] = z; return m; }

        static double[,] Rot(char axis, double deg)
        {
            double r = deg * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
            var m = Ident();
            if (axis == 'x') { m[1, 1] = c; m[1, 2] = -s; m[2, 1] = s; m[2, 2] = c; }
            if (axis == 'y') { m[0, 0] = c; m[0, 2] = s; m[2, 0] = -s; m[2, 2] = c; }
            if (axis == 'z') { m[0, 0] = c; m[0, 1] = -s; m[1, 0] = s; m[1, 1] = c; }
            return m;
        }

        static double[,] Mul(double[,] a, double[,] b)
        {
            var r = new double[4, 4];
            for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++)
                r[i, j] = a[i, 0] * b[0, j] + a[i, 1] * b[1, j] + a[i, 2] * b[2, j] + a[i, 3] * b[3, j];
            return r;
        }
    }
}
