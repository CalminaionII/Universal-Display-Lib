using System;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace UniversalDisplayLib
{
    /// <summary>
    /// VS SIDING (Graphight, github.com/Graphight/VS-Siding) - 2026-10-17, Calm: shelves put into a siding wall's open
    /// side showed the ghost and the placing outline apart from where the shelf and items are drawn.
    ///
    /// Siding moves a block standing in a wall's space away from the wall panel: the chunk mesh (our placed items with
    /// it, they are tesselated there) and the boxes the game asks for. What the lib draws by itself - the placing ghost
    /// and the placing outline - it cannot reach, so they ask here how far it moved this block and follow.
    ///
    /// Read by name, no dependency: zero when siding is not installed, its method is missing or it throws.
    /// Its own answer is (dx, dz) in blocks, from SidingModSystem.GapShiftAt(BlockPos, Block).
    /// </summary>
    public static class SidingShift
    {
        static bool looked;
        static MethodInfo gapShiftAt;
        /// <summary>The same method as a direct call - built once; MethodInfo.Invoke allocates on every call and this
        /// is asked several times a frame (the ghost, the outlines, every filler's boxes). Null: fall back to Invoke.</summary>
        static System.Func<BlockPos, Block, ValueTuple<double, double>> direct;
        static bool failed;

        public static Vec3f At(BlockPos pos, Block block)
        {
            if (failed || pos == null || block == null) return null;
            if (!looked)
            {
                looked = true;
                Type t = null;
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    t = a.GetType("VSSiding.SidingModSystem", false);
                    if (t != null) break;
                }
                gapShiftAt = t?.GetMethod("GapShiftAt", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                                          null, new[] { typeof(BlockPos), typeof(Block) }, null);
                if (gapShiftAt?.ReturnType == typeof(ValueTuple<double, double>))
                {
                    try { direct = (System.Func<BlockPos, Block, ValueTuple<double, double>>)gapShiftAt.CreateDelegate(typeof(System.Func<BlockPos, Block, ValueTuple<double, double>>)); }
                    catch (Exception) { direct = null; }   // Invoke below still works
                }
            }
            if (gapShiftAt == null) return null;
            try
            {
                ValueTuple<double, double> s;
                if (direct != null) s = direct(pos, block);
                else if (gapShiftAt.Invoke(null, new object[] { pos, block }) is ValueTuple<double, double> boxed) s = boxed;
                else return null;
                if (s.Item1 != 0 || s.Item2 != 0) return new Vec3f((float)s.Item1, 0f, (float)s.Item2);
                return null;
            }
            catch (Exception)
            {
                failed = true;   // never worth more than the one try - draw unshifted from here on
                return null;
            }
        }
    }
}
