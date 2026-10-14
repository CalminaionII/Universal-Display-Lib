using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace UniversalDisplayLib
{
    /// <summary>
    /// WORN ITEMS ARE DRAWN ON AN INVISIBLE SERAPH (Calm, 2026-10-07: "Must be the seraph" ... "do that so it works for all
    /// equipment"). A clothing model's parts name the seraph bone they hang from (<c>stepParentName</c>: a boot's
    /// LowerFootR / LowerFootL), and the game builds the item's mesh on the seraph so the pair lands on its feet - the
    /// seraph's own geometry stays in that mesh, invisible, up to head height. Measured as is, a pair of boots was 27 voxels
    /// tall and fitted nowhere; its outline reached the ceiling.
    ///
    /// So for any item whose model attaches to bones, the lib places just the item's OWN parts on the seraph's skeleton
    /// (rest pose, as the game does), puts them through the item's display transform, and records that as the mesh's extent
    /// (BEUniversalDisplay.SetMeshExtent). The outline, the placing ghost's outline and the measured footprint all read it.
    /// Checked against vanilla's own shelf sizes: high leather boots 6.8 x 6.4 x 4.2 (vanilla 8 x 6.5 x 4), commoner
    /// 6.6 x 7.3 x 4.0 (8 x 7.5 x 4), hunter 7.0 x 8.2 x 4.3 (8 x 8.5 x 4).
    ///
    /// Falls back to the whole mesh (as before) if the model has no step parents or names a bone the seraph does not have.
    ///
    /// ONLY WHEN THE DRAWN MODEL IS THE SERAPH ONE (2026-10-10, Calm: "would it detect everything now say someone made their
    /// own with folding stuff?"): the correction is used only if the drawn mesh CONTAINS the garment's own parts and is
    /// clearly bigger than them - the invisible seraph round them. A folded model (the game's display shape, or one a mod
    /// builds in its own code) does not, so it keeps the size it is drawn at. Decided by the model, not by where it came from.
    /// </summary>
    public static class ClothingExtent
    {
        const string SeraphShape = "game:shapes/entity/humanoid/seraph-faceless.json";

        static Dictionary<string, double[,]> seraphBones;
        static bool seraphTried;
        static readonly object seraphLock = new();

        /// <summary>Client, after the display transform is applied to <paramref name="mesh"/>: record a worn item's own extent for it.</summary>
        public static void Register(ICoreClientAPI capi, ItemSlot slot, ModelTransform transform, MeshData mesh)
        {
            if (capi == null || mesh == null) return;
            AssetLocation shapeLoc = slot?.Itemstack?.Item?.Shape?.Base;
            if (shapeLoc == null) return;
            Shape shape = capi.TesselatorManager.GetCachedShape(shapeLoc);
            if (shape?.Elements == null || !HasStepParent(shape.Elements)) return;

            Dictionary<string, double[,]> bones = SeraphBones(capi);
            if (bones == null) return;

            var points = new List<double[]>();
            if (!Walk(shape.Elements, Ident(), bones, points) || points.Count == 0) return;

            var probe = new MeshData(points.Count, 0, false, true, true, true);
            foreach (double[] pt in points) probe.AddVertexSkipTex((float)pt[0], (float)pt[1], (float)pt[2]);
            if (transform != null)
            {
                transform.EnsureDefaultValues();
                probe.ModelTransform(transform);
            }

            float[] e = ExtentOf(probe);
            float[] drawn = ExtentOf(mesh);
            if (e == null || drawn == null || !IsSeraphMesh(drawn, e)) return;
            BEUniversalDisplay.SetMeshExtent(mesh, e);
        }

        /// <summary>Within this of the garment's own extent counts as containing it (two voxels, in blocks) - room for our bone
        /// maths and the game's to differ a little, so the correction never drops out for boots.</summary>
        const float Slack = 2f / 16f;
        /// <summary>Bigger than the garment by more than this on some side = something else is in the mesh (four voxels; the
        /// seraph is about 30 tall, and a folded garment sits nowhere near where it is worn).</summary>
        const float Extra = 4f / 16f;

        /// <summary>The drawn mesh holds the garment's own parts where they are worn AND reaches clearly beyond them - the seraph.</summary>
        static bool IsSeraphMesh(float[] drawn, float[] own)
        {
            bool beyond = false;
            for (int a = 0; a < 3; a++)
            {
                if (drawn[a] > own[a] + Slack || drawn[a + 3] < own[a + 3] - Slack) return false;
                if (own[a] - drawn[a] > Extra || drawn[a + 3] - own[a + 3] > Extra) beyond = true;
            }
            return beyond;
        }

        static float[] ExtentOf(MeshData m)
        {
            if (m?.xyz == null || m.VerticesCount == 0) return null;
            float[] e = { float.MaxValue, float.MaxValue, float.MaxValue, float.MinValue, float.MinValue, float.MinValue };
            for (int i = 0; i < m.VerticesCount; i++)
                for (int a = 0; a < 3; a++)
                {
                    float v = m.xyz[i * 3 + a];
                    if (v < e[a]) e[a] = v;
                    if (v > e[a + 3]) e[a + 3] = v;
                }
            return e;
        }

        static bool HasStepParent(ShapeElement[] elements)
        {
            foreach (ShapeElement el in elements ?? Array.Empty<ShapeElement>())
            {
                if (el == null) continue;
                if (!string.IsNullOrEmpty(el.StepParentName) || HasStepParent(el.Children)) return true;
            }
            return false;
        }

        /// <summary>The seraph's bones, each element's matrix in the rest pose, by lower-case name. Loaded once.</summary>
        static Dictionary<string, double[,]> SeraphBones(ICoreClientAPI capi)
        {
            lock (seraphLock)
            {
                if (seraphTried) return seraphBones;
                seraphTried = true;
                Shape seraph = Shape.TryGet(capi, new AssetLocation(SeraphShape));
                if (seraph?.Elements == null) return null;
                var bones = new Dictionary<string, double[,]>();
                void Bones(ShapeElement[] els, double[,] parent)
                {
                    foreach (ShapeElement el in els ?? Array.Empty<ShapeElement>())
                    {
                        if (el == null) continue;
                        double[,] m = Mul(parent, Local(el));
                        if (el.Name != null) bones[el.Name.ToLowerInvariant()] = m;
                        Bones(el.Children, m);
                    }
                }
                Bones(seraph.Elements, Ident());
                return seraphBones = bones;
            }
        }

        /// <summary>The item's own parts, each hung from its step parent when it names one. False if a named bone is missing.</summary>
        static bool Walk(ShapeElement[] els, double[,] parent, Dictionary<string, double[,]> bones, List<double[]> points)
        {
            foreach (ShapeElement el in els ?? Array.Empty<ShapeElement>())
            {
                if (el == null) continue;
                double[,] from = parent;
                if (!string.IsNullOrEmpty(el.StepParentName) && !bones.TryGetValue(el.StepParentName.ToLowerInvariant(), out from)) return false;
                double[,] m = Mul(from, Local(el));
                if (Drawn(el))
                {
                    double sx = (el.To[0] - el.From[0]) / 16, sy = (el.To[1] - el.From[1]) / 16, sz = (el.To[2] - el.From[2]) / 16;
                    foreach (double dx in new[] { 0, sx }) foreach (double dy in new[] { 0, sy }) foreach (double dz in new[] { 0, sz })
                    {
                        points.Add(new[]
                        {
                            m[0, 0] * dx + m[0, 1] * dy + m[0, 2] * dz + m[0, 3],
                            m[1, 0] * dx + m[1, 1] * dy + m[1, 2] * dz + m[1, 3],
                            m[2, 0] * dx + m[2, 1] * dy + m[2, 2] * dz + m[2, 3]
                        });
                    }
                }
                if (!Walk(el.Children, m, bones, points)) return false;
            }
            return true;
        }

        /// <summary>A part that is actually drawn: a real size on every side and at least one face switched on.</summary>
        static bool Drawn(ShapeElement el)
        {
            if (el.From == null || el.To == null) return false;
            for (int i = 0; i < 3; i++) if (Math.Abs(el.To[i] - el.From[i]) < 1e-6) return false;
            if (el.FacesResolved != null)
            {
                foreach (var f in el.FacesResolved) if (f != null && f.Enabled) return true;
                return false;
            }
            if (el.Faces != null)
            {
                foreach (var f in el.Faces.Values) if (f != null && f.Enabled) return true;
                return false;
            }
            return false;
        }

        // As the game places an element (and as PaneBoxes / AutoBoxesMeasure do): T(origin) * Rx * Ry * Rz * S * T(from - origin),
        // in blocks; a child's matrix is its parent's times its own.
        static double[,] Local(ShapeElement e)
        {
            double[] o = e.RotationOrigin ?? new double[3];
            double[] f = e.From ?? new double[3];
            var m = Trans(o[0] / 16, o[1] / 16, o[2] / 16);
            m = Mul(m, Rot('x', e.RotationX));
            m = Mul(m, Rot('y', e.RotationY));
            m = Mul(m, Rot('z', e.RotationZ));
            m = Mul(m, Scale(e.ScaleX, e.ScaleY, e.ScaleZ));
            return Mul(m, Trans((f[0] - o[0]) / 16, (f[1] - o[1]) / 16, (f[2] - o[2]) / 16));
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
