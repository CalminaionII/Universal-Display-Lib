using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace UniversalDisplayLib;   // CHANGE: the namespace your own code files use (the word after "namespace" at their top)

/// <summary>
/// Measures a block's box from its own model file - every element where the game draws it (rotation
/// origin, rotations, scale, children inside their parent), then one box round all of them, turned with
/// the block (rotateX/Y/Z, as the game turns a written box). Both sides read the same file, so the
/// server and the client agree. Measured once per model.
/// </summary>
public static class AutoBoxesMeasure
{
    static readonly Dictionary<string, Cuboidf> Cache = new();

    /// <param name="withShapeTurn">
    /// true (the default): turn the box with the block's facing versions (rotateX/Y/Z, e.g. rotateYByType).
    /// false: leave that turn out - for a block whose own code turns its model (an angle in the block
    /// entity), so the box is only turned once, by that code.
    /// </param>
    public static Cuboidf FromModel(ICoreAPI api, Block block, bool withShapeTurn = true)
    {
        CompositeShape cs = block?.Shape;
        if (cs?.Base == null) return null;
        AssetLocation loc = cs.Base.Clone().WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json");
        string key = loc + "|" + string.Join(",", cs.IgnoreElements ?? []);
        Cuboidf raw;
        lock (Cache)
        {
            if (!Cache.TryGetValue(key, out raw))
            {
                raw = Measure(Shape.TryGet(api, loc), cs.IgnoreElements);
                if (raw == null) api.Logger.Warning("[AutoBoxes] Could not measure {0} from {1} - no auto boxes for it.", block.Code, loc);
                Cache[key] = raw;
            }
        }
        if (raw == null) return null;
        if (!withShapeTurn || (cs.rotateX == 0 && cs.rotateY == 0 && cs.rotateZ == 0)) return raw.Clone();
        return raw.RotatedCopy(cs.rotateX, cs.rotateY, cs.rotateZ, new Vec3d(0.5, 0.5, 0.5));
    }

    static Cuboidf Measure(Shape shape, string[] ignore)
    {
        if (shape?.Elements == null) return null;
        var skip = new HashSet<string>(ignore ?? []);
        double[] lo = { double.MaxValue, double.MaxValue, double.MaxValue }, hi = { double.MinValue, double.MinValue, double.MinValue };
        foreach (var e in shape.Elements) Walk(e, Ident(), skip, lo, hi);
        if (lo[0] > hi[0]) return null;
        return new Cuboidf((float)lo[0], (float)lo[1], (float)lo[2], (float)hi[0], (float)hi[1], (float)hi[2]);
    }

    // As the game places an element (ShapeElement, animation version 0), in blocks:
    // T(origin) * Rx * Ry * Rz * S * T(from - origin); a child's matrix is its parent's times its own.
    static void Walk(ShapeElement e, double[,] parent, HashSet<string> skip, double[] lo, double[] hi)
    {
        if (e == null || (e.Name != null && skip.Contains(e.Name))) return;
        double[] o = (e.RotationOrigin ?? new double[3]).Select(v => v / 16).ToArray();
        double[] f = (e.From ?? new double[3]).Select(v => v / 16).ToArray();
        double[] t = (e.To ?? new double[3]).Select(v => v / 16).ToArray();
        var m = Mul(parent, Trans(o[0], o[1], o[2]));
        m = Mul(m, Rot('x', e.RotationX));
        m = Mul(m, Rot('y', e.RotationY));
        m = Mul(m, Rot('z', e.RotationZ));
        m = Mul(m, Scale(e.ScaleX, e.ScaleY, e.ScaleZ));
        m = Mul(m, Trans(f[0] - o[0], f[1] - o[1], f[2] - o[2]));
        double sx = t[0] - f[0], sy = t[1] - f[1], sz = t[2] - f[2];
        if (Math.Abs(sx) > 1e-6 && Math.Abs(sy) > 1e-6 && Math.Abs(sz) > 1e-6)   // a zero-size element is a pivot, not a part
        {
            foreach (double dx in new[] { 0, sx }) foreach (double dy in new[] { 0, sy }) foreach (double dz in new[] { 0, sz })
                for (int i = 0; i < 3; i++)
                {
                    double v = m[i, 0] * dx + m[i, 1] * dy + m[i, 2] * dz + m[i, 3];
                    lo[i] = Math.Min(lo[i], v); hi[i] = Math.Max(hi[i], v);
                }
        }
        if (e.Children != null) foreach (var c in e.Children) Walk(c, m, skip, lo, hi);
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
