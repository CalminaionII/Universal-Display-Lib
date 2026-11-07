using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace UniversalDisplayLib
{
    /// <summary>
    /// SEE-THROUGH PARTS DO NOT COUNT. A displayed item's box - its measured footprint, its outline, the placing
    /// ghost's outline, the levitating centre - is taken from its mesh (BEUniversalDisplay.MeshBounds). Every vertex
    /// counted, so a model with a big clear sheet in it was measured by the sheet: vanilla's onion is a small bulb with
    /// two crossed 13 x 13 leaf sheets, nearly all of them transparent texture, and it took a whole block.
    ///
    /// So the mesh's extent is taken from what is VISIBLE. Each face (a quad, as the tesselator makes them) is traced
    /// back to its texture through its UVs: the atlas page from the face's texture id, the texture's place on that page
    /// from the UV (every atlas position, gridded), and the texture's name from the item's own textures and shape
    /// textures. The texture's image is loaded once and its opaque texels counted (summed-area table), then per face:
    ///   * no opaque texel under it - the face is left out;
    ///   * all of it opaque - its four corners count, as before;
    ///   * part - the opaque texels' bounding rectangle is mapped back onto the face (UV -> position is affine on a quad) and
    ///     only that counts.
    ///
    /// SAFE BY DEFAULT: a face whose texture cannot be traced (a mesh without UVs or texture ids, a texture whose name
    /// is not known, an image that will not load) counts whole, which is exactly the old behaviour. A mesh with nothing
    /// visible at all keeps its full extent. Client only; the extent is worked out once per mesh
    /// (BEUniversalDisplay.meshExtents).
    /// </summary>
    public static class VisibleExtent
    {
        /// <summary>A texel at least this opaque (0-255) is seen. Faint glass still counts; a cut-out sheet's clear part does not.</summary>
        const int AlphaCutoff = 24;

        /// <summary>Cells per side of the UV grid each atlas page is indexed by.</summary>
        const int GridCells = 64;

        static ICoreClientAPI capi;
        static readonly object gate = new();

        /// <summary>What a mesh was built from, so the textures it uses can be named (NoteOwner).</summary>
        sealed class Owner
        {
            public CollectibleObject Collectible;
            public CompositeShape DisplayShape;
        }

        static readonly ConditionalWeakTable<MeshData, Owner> owners = new();

        /// <summary>Atlas position -> the texture name it was put in under (null: tried, unknown).</summary>
        static readonly ConcurrentDictionary<TextureAtlasPosition, AssetLocation> names = new(ReferenceEqualityComparer.Instance);
        /// <summary>Atlas position -> its texture's opaque texels (null: could not load).</summary>
        static readonly ConcurrentDictionary<TextureAtlasPosition, Mask> masks = new(ReferenceEqualityComparer.Instance);
        static readonly ConcurrentDictionary<CollectibleObject, byte> namedOwners = new(ReferenceEqualityComparer.Instance);
        static readonly ConcurrentDictionary<string, byte> namedShapes = new();

        static Dictionary<int, List<TextureAtlasPosition>[]> grids;
        static int gridPositionCount = -1;
        static bool everythingNamed;

        public static void Start(ICoreClientAPI api)
        {
            lock (gate)
            {
                capi = api;
                grids = null;
                gridPositionCount = -1;
                everythingNamed = false;
            }
            names.Clear();
            masks.Clear();
            namedOwners.Clear();
            namedShapes.Clear();
        }

        public static void Stop() => Start(null);

        /// <summary>
        /// Client: what an item's display mesh was built from - its textures and shapes are then named before its extent is
        /// taken. <paramref name="displayShape"/>: a display shape drawn instead of the item's own (a folded garment).
        /// </summary>
        public static void NoteOwner(MeshData mesh, CollectibleObject collectible, CompositeShape displayShape)
        {
            if (mesh == null || collectible == null) return;
            owners.AddOrUpdate(mesh, new Owner { Collectible = collectible, DisplayShape = displayShape });
        }

        /// <summary>
        /// The visible extent of <paramref name="mesh"/> in its own space - {x1, y1, z1, x2, y2, z2} - or null when nothing in
        /// it could be traced to a texture (the caller then takes every vertex, as before). Any thread.
        /// </summary>
        public static float[] Of(MeshData mesh)
        {
            ICoreClientAPI api = capi;
            if (api == null || mesh?.xyz == null || mesh.Uv == null) return null;
            int faces = mesh.VerticesCount / 4;
            if (faces == 0 || mesh.VerticesCount % 4 != 0 || mesh.Uv.Length < mesh.VerticesCount * 2) return null;

            try
            {
                if (owners.TryGetValue(mesh, out Owner owner)) NameOwnerTextures(api, owner);
                NameEverything(api);

                var b = new Box();
                bool traced = false;
                TextureAtlasPosition last = null;
                for (int f = 0; f < faces; f++)
                {
                    int v0 = f * 4;
                    int texId = TextureIdOf(api, mesh, f);
                    float cu = 0, cv = 0;
                    for (int k = 0; k < 4; k++) { cu += mesh.Uv[(v0 + k) * 2]; cv += mesh.Uv[(v0 + k) * 2 + 1]; }
                    cu /= 4; cv /= 4;

                    TextureAtlasPosition pos = last != null && last.atlasTextureId == texId && last.ContainsUV(cu, cv)
                        ? last : Find(api, texId, cu, cv);
                    Mask mask = pos == null ? null : MaskOf(api, pos);
                    if (mask == null)
                    {
                        AddCorners(mesh, v0, b);
                        continue;
                    }
                    last = pos;
                    traced = true;
                    AddVisible(mesh, v0, pos, mask, b);
                }

                if (!traced || b.Empty) return null;
                return new[] { b.X1, b.Y1, b.Z1, b.X2, b.Y2, b.Z2 };
            }
            catch (Exception e)
            {
                api.Logger.Debug("[UniversalDisplayLib] visible extent failed, using the whole mesh: {0}", e.Message);
                return null;
            }
        }

        // ── One face ─────────────────────────────────────────────────────────

        static void AddVisible(MeshData mesh, int v0, TextureAtlasPosition pos, Mask mask, Box b)
        {
            float pw = pos.x2 - pos.x1, ph = pos.y2 - pos.y1;
            if (pw <= 0 || ph <= 0) { AddCorners(mesh, v0, b); return; }

            // the face's UVs in texels of the texture's own image
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int k = 0; k < 4; k++)
            {
                float x = (mesh.Uv[(v0 + k) * 2] - pos.x1) / pw * mask.W;
                float y = (mesh.Uv[(v0 + k) * 2 + 1] - pos.y1) / ph * mask.H;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
            // the tesselator insets UVs a hair - a texel is under the face if most of the way in
            int x0 = Math.Clamp((int)MathF.Floor(minX + 0.05f), 0, mask.W), x1 = Math.Clamp((int)MathF.Ceiling(maxX - 0.05f), 0, mask.W);
            int y0 = Math.Clamp((int)MathF.Floor(minY + 0.05f), 0, mask.H), y1 = Math.Clamp((int)MathF.Ceiling(maxY - 0.05f), 0, mask.H);
            if (x1 <= x0) x1 = Math.Min(mask.W, x0 + 1);
            if (x1 <= x0) x0 = x1 - 1;
            if (y1 <= y0) y1 = Math.Min(mask.H, y0 + 1);
            if (y1 <= y0) y0 = y1 - 1;

            int count = mask.Count(x0, y0, x1, y1);
            if (count == 0) return;                                          // all clear: not seen
            if (count == (x1 - x0) * (y1 - y0)) { AddCorners(mesh, v0, b); return; }

            // part clear: shrink to the opaque texels' bounding rectangle
            while (x0 < x1 && mask.Count(x0, y0, x0 + 1, y1) == 0) x0++;
            while (x1 > x0 && mask.Count(x1 - 1, y0, x1, y1) == 0) x1--;
            while (y0 < y1 && mask.Count(x0, y0, x1, y0 + 1) == 0) y0++;
            while (y1 > y0 && mask.Count(x0, y1 - 1, x1, y1) == 0) y1--;

            // back to atlas UV, then onto the face. A shape face's UV rectangle only partly covering a texel range rounds out
            // to whole texels above, so clamp back inside what the face actually shows.
            float u0 = Math.Max(pos.x1 + x0 / (float)mask.W * pw, pos.x1 + minX / mask.W * pw);
            float u1 = Math.Min(pos.x1 + x1 / (float)mask.W * pw, pos.x1 + maxX / mask.W * pw);
            float w0 = Math.Max(pos.y1 + y0 / (float)mask.H * ph, pos.y1 + minY / mask.H * ph);
            float w1 = Math.Min(pos.y1 + y1 / (float)mask.H * ph, pos.y1 + maxY / mask.H * ph);

            if (!FaceMap(mesh, v0, out Affine a)) { AddCorners(mesh, v0, b); return; }
            a.Add(mesh, u0, w0, b);
            a.Add(mesh, u1, w0, b);
            a.Add(mesh, u0, w1, b);
            a.Add(mesh, u1, w1, b);
        }

        /// <summary>UV -> position on a face: affine through three of its corners whose UVs are not in a line.</summary>
        struct Affine
        {
            public int P0, P1, P2;
            public float U0, V0, Du1, Dv1, Du2, Dv2, Det;

            public void Add(MeshData m, float u, float v, Box b)
            {
                float du = u - U0, dv = v - V0;
                float s = (du * Dv2 - Du2 * dv) / Det;
                float t = (Du1 * dv - du * Dv1) / Det;
                float[] xyz = m.xyz;
                b.Add(xyz[P0 * 3] + s * (xyz[P1 * 3] - xyz[P0 * 3]) + t * (xyz[P2 * 3] - xyz[P0 * 3]),
                      xyz[P0 * 3 + 1] + s * (xyz[P1 * 3 + 1] - xyz[P0 * 3 + 1]) + t * (xyz[P2 * 3 + 1] - xyz[P0 * 3 + 1]),
                      xyz[P0 * 3 + 2] + s * (xyz[P1 * 3 + 2] - xyz[P0 * 3 + 2]) + t * (xyz[P2 * 3 + 2] - xyz[P0 * 3 + 2]));
            }
        }

        static readonly int[][] Triples = { new[] { 0, 1, 2 }, new[] { 0, 1, 3 }, new[] { 0, 2, 3 }, new[] { 1, 2, 3 } };

        static bool FaceMap(MeshData m, int v0, out Affine a)
        {
            a = default;
            foreach (int[] t in Triples)
            {
                int p0 = v0 + t[0], p1 = v0 + t[1], p2 = v0 + t[2];
                float u0 = m.Uv[p0 * 2], w0 = m.Uv[p0 * 2 + 1];
                float du1 = m.Uv[p1 * 2] - u0, dv1 = m.Uv[p1 * 2 + 1] - w0;
                float du2 = m.Uv[p2 * 2] - u0, dv2 = m.Uv[p2 * 2 + 1] - w0;
                float det = du1 * dv2 - du2 * dv1;
                if (Math.Abs(det) < 1e-12f) continue;
                a = new Affine { P0 = p0, P1 = p1, P2 = p2, U0 = u0, V0 = w0, Du1 = du1, Dv1 = dv1, Du2 = du2, Dv2 = dv2, Det = det };
                return true;
            }
            return false;
        }

        static void AddCorners(MeshData m, int v0, Box b)
        {
            for (int k = 0; k < 4; k++)
            {
                int i = (v0 + k) * 3;
                b.Add(m.xyz[i], m.xyz[i + 1], m.xyz[i + 2]);
            }
        }

        sealed class Box
        {
            public float X1 = float.MaxValue, Y1 = float.MaxValue, Z1 = float.MaxValue;
            public float X2 = float.MinValue, Y2 = float.MinValue, Z2 = float.MinValue;
            public bool Empty => X1 > X2;

            public void Add(float x, float y, float z)
            {
                if (x < X1) X1 = x; if (x > X2) X2 = x;
                if (y < Y1) Y1 = y; if (y > Y2) Y2 = y;
                if (z < Z1) Z1 = z; if (z > Z2) Z2 = z;
            }
        }

        // ── Which texture a face shows ───────────────────────────────────────

        /// <summary>The GL texture (atlas page) a face is drawn from, 0 if the mesh does not say.</summary>
        static int TextureIdOf(ICoreClientAPI api, MeshData m, int face)
        {
            int[] ids = m.TextureIds;
            if (ids == null || ids.Length == 0) return api.BlockTextureAtlas.AtlasTextures[0].TextureId;
            if (m.TextureIndices == null || face >= m.TextureIndices.Length) return ids[0];
            int index = m.TextureIndices[face];
            return index < ids.Length ? ids[index] : 0;
        }

        /// <summary>The atlas position on page <paramref name="texId"/> under UV (u, v), or null.</summary>
        static TextureAtlasPosition Find(ICoreClientAPI api, int texId, float u, float v)
        {
            Dictionary<int, List<TextureAtlasPosition>[]> g = Grids(api);
            if (g == null || !g.TryGetValue(texId, out var cells)) return null;
            List<TextureAtlasPosition> cell = cells[Cell(v) * GridCells + Cell(u)];
            if (cell == null) return null;
            foreach (TextureAtlasPosition p in cell)
                if (p.ContainsUV(u, v)) return p;
            return null;
        }

        static int Cell(float f) => Math.Clamp((int)(f * GridCells), 0, GridCells - 1);

        /// <summary>Every position on the block and item atlases, by page and UV cell. Rebuilt when a texture is added.</summary>
        static Dictionary<int, List<TextureAtlasPosition>[]> Grids(ICoreClientAPI api)
        {
            TextureAtlasPosition[] block = api.BlockTextureAtlas.Positions, item = api.ItemTextureAtlas.Positions;
            int count = (block?.Length ?? 0) + (item?.Length ?? 0);
            lock (gate)
            {
                if (grids != null && gridPositionCount == count) return grids;
                var g = new Dictionary<int, List<TextureAtlasPosition>[]>();
                foreach (TextureAtlasPosition[] all in new[] { block, item })
                {
                    if (all == null) continue;
                    foreach (TextureAtlasPosition p in all)
                    {
                        if (p == null || p.x2 <= p.x1 || p.y2 <= p.y1) continue;
                        if (!g.TryGetValue(p.atlasTextureId, out var cells)) g[p.atlasTextureId] = cells = new List<TextureAtlasPosition>[GridCells * GridCells];
                        for (int cy = Cell(p.y1); cy <= Cell(p.y2); cy++)
                            for (int cx = Cell(p.x1); cx <= Cell(p.x2); cx++)
                                (cells[cy * GridCells + cx] ??= new List<TextureAtlasPosition>()).Add(p);
                    }
                }
                grids = g;
                gridPositionCount = count;
                return g;
            }
        }

        // ── Naming textures ──────────────────────────────────────────────────

        /// <summary>
        /// Once per world: every item's and block's own textures and the textures of its shape - what the game put in the
        /// atlases at load. A mesh built from something else (a mod's own mesh code) is still traced if it uses any of these.
        /// </summary>
        static void NameEverything(ICoreClientAPI api)
        {
            if (everythingNamed) return;
            lock (gate)
            {
                if (everythingNamed) return;
                foreach (CollectibleObject c in api.World.Collectibles)
                {
                    if (c?.Code == null) continue;
                    NameCollectible(api, c);
                }
                everythingNamed = true;
            }
        }

        static void NameOwnerTextures(ICoreClientAPI api, Owner owner)
        {
            NameCollectible(api, owner.Collectible);
            // its own shapes read from disk if the game has not kept them - once per item, not for every item in the world
            foreach (CompositeShape cs in new[] { owner.DisplayShape, ShapeOf(owner.Collectible), (owner.Collectible as Block)?.Shape })
                if (cs?.Base != null) NameShape(api, cs.Base, fromDisk: true);

            // a plant's potted textures (BuildContainedTextureSource reads them for a display shape)
            PlantContainerProps potted = BlockBehaviorDisplaySurfaces.PottedProps(owner.Collectible);
            if (potted?.Textures != null)
                foreach (CompositeTexture ct in potted.Textures.Values) Name(api, ct);
        }

        static void NameCollectible(ICoreClientAPI api, CollectibleObject c)
        {
            if (!namedOwners.TryAdd(c, 0)) return;
            IDictionary<string, CompositeTexture> textures = c is Item item ? item.Textures : (c as Block)?.Textures;
            if (textures != null)
                foreach (CompositeTexture ct in textures.Values) Name(api, ct);
            if (c is Block block && block.TexturesInventory != null)
                foreach (CompositeTexture ct in block.TexturesInventory.Values) Name(api, ct);

            CompositeShape shape = ShapeOf(c);
            if (shape?.Base != null) NameShape(api, shape.Base, fromDisk: false);
            if (c is Block b2 && b2.Shape?.Base != null) NameShape(api, b2.Shape.Base, fromDisk: false);
        }

        static CompositeShape ShapeOf(CollectibleObject c) => c is Item i ? i.Shape : (c as Block)?.ShapeInventory ?? (c as Block)?.Shape;

        /// <summary>
        /// A shape's textures. <paramref name="fromDisk"/>: read the file if the game has not kept the shape - false for the
        /// once-per-world pass over every item, which would otherwise read thousands of files on the first item placed.
        /// </summary>
        static void NameShape(ICoreClientAPI api, AssetLocation shapeBase, bool fromDisk)
        {
            string key = shapeBase.ToString();
            if (namedShapes.ContainsKey(key)) return;
            Shape shape = api.TesselatorManager.GetCachedShape(shapeBase);
            if (shape == null && fromDisk)
                shape = Shape.TryGet(api, shapeBase.Clone().WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json"));
            if (shape == null && !fromDisk) return;   // left for the item's own, from-disk call
            if (!namedShapes.TryAdd(key, 0)) return;
            if (shape?.Textures == null) return;
            foreach (AssetLocation loc in shape.Textures.Values) Name(api, loc);
        }

        static void Name(ICoreClientAPI api, CompositeTexture ct)
        {
            if (ct == null) return;
            if (ct.Baked?.BakedName != null) Name(api, ct.Baked.BakedName);
            if (ct.Baked?.BakedVariants != null)
                foreach (BakedCompositeTexture v in ct.Baked.BakedVariants)
                    if (v?.BakedName != null) Name(api, v.BakedName);
            if (ct.Base != null) Name(api, ct.Base);
        }

        static void Name(ICoreClientAPI api, AssetLocation loc)
        {
            if (loc == null) return;
            TextureAtlasPosition p = api.BlockTextureAtlas[loc];
            if (p != null) names.TryAdd(p, loc);
            p = api.ItemTextureAtlas[loc];
            if (p != null) names.TryAdd(p, loc);
        }

        // ── Texture images ───────────────────────────────────────────────────

        static Mask MaskOf(ICoreClientAPI api, TextureAtlasPosition pos)
        {
            if (masks.TryGetValue(pos, out Mask known)) return known;
            Mask mask = names.TryGetValue(pos, out AssetLocation name) && name != null ? Load(api, name) : null;
            // an unnamed position is not remembered as unloadable: its name may turn up with the next item's textures
            if (name != null) masks[pos] = mask;
            return mask;
        }

        static Mask Load(ICoreClientAPI api, AssetLocation name)
        {
            IBitmap bmp = null;
            try
            {
                bmp = api.BlockTextureAtlas.LoadCompositeBitmap(new AssetLocationAndSource(name));
                if (bmp == null || bmp.Width <= 0 || bmp.Height <= 0) return null;
                return new Mask(bmp);
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                (bmp as IDisposable)?.Dispose();
            }
        }

        /// <summary>A texture's opaque texels as a summed-area table: any rectangle's count in four reads.</summary>
        sealed class Mask
        {
            public readonly int W, H;
            readonly int[] sat;

            public Mask(IBitmap bmp)
            {
                W = bmp.Width;
                H = bmp.Height;
                int[] px = bmp.Pixels;
                sat = new int[(W + 1) * (H + 1)];
                for (int y = 0; y < H; y++)
                {
                    int row = 0;
                    for (int x = 0; x < W; x++)
                    {
                        if (((px[y * W + x] >> 24) & 0xff) >= AlphaCutoff) row++;
                        sat[(y + 1) * (W + 1) + x + 1] = sat[y * (W + 1) + x + 1] + row;
                    }
                }
            }

            /// <summary>Opaque texels in [x0, x1) x [y0, y1).</summary>
            public int Count(int x0, int y0, int x1, int y1)
                => sat[y1 * (W + 1) + x1] - sat[y0 * (W + 1) + x1] - sat[y1 * (W + 1) + x0] + sat[y0 * (W + 1) + x0];
        }
    }
}
