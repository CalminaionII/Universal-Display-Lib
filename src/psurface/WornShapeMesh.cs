using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace UniversalDisplayLib
{
    /// <summary>
    /// ITEMS WHOSE LOOK CHANGES WITH THE STACK, SHOWN AS THEY ARE (Calm, 2026-10-09: modular backpacks with pouches -
    /// "make it generic so things do that in general ... just like we did with the antlers").
    ///
    /// A display only gets an item's look from a displayable shape, an IContainedMeshSource, or the item's plain model.
    /// Gear mods that change what an item looks like (Modular Backpacks adds pouches) often do it only in the hand (a
    /// swapped GPU model the lib cannot read back) and on the wearer - through the game's IWearableShapeSupplier, which
    /// hands out the worn shape for that very stack. This builds the display mesh from that worn shape, the way the game
    /// puts gear on an entity (EntityBehaviorContainer.addGearToShape, public vsessentialsmod), with the wearer's own body
    /// left out.
    ///
    /// Only for an item that IS an IWearableShapeSupplier, can be attached to an entity, and has NO IContainedMeshSource
    /// (vanilla clothes have one - their folded shelf model - so they never come here) and no displayable shape of its
    /// own (folded clothes in the patches keep theirs; BEUniversalDisplay checks that first).
    ///
    /// The worn shape sits where it is worn, so the mesh is moved to where the item's plain model sits (bottom centre of
    /// its box); the item's display transform then applies as before. It turns the way it is worn - a wrong facing is a
    /// per-item patch (rendering fix: patches first).
    ///
    /// ASKED FOR THE LOCAL PLAYER (2026-10-09, first try showed a plain bag): Modular Backpacks only composes its addons when
    /// the wearer is an EntityPlayer - for no wearer it returns null and the game falls back to the plain attached shape. The
    /// worn shape is what a player would wear, so the local player's entity is passed; nothing on it is changed.
    ///
    /// Built on the MAIN thread (it inserts textures into the block atlas). Until it is ready the plain model stands in -
    /// MeasuredItemSizes keeps that size only until the real one is measured - and the key ends "|wait" so the real mesh
    /// is asked for once it exists (the figurines' way).
    /// </summary>
    public static class WornShapeMesh
    {
        static readonly ConcurrentDictionary<string, bool> appliesByCode = new();
        static readonly ConcurrentDictionary<string, MeshData> built = new();
        static readonly ConcurrentDictionary<string, ConcurrentDictionary<BlockPos, byte>> waiting = new();
        static readonly ConcurrentDictionary<string, byte> logged = new();

        public static void ForgetAll()
        {
            appliesByCode.Clear();
            built.Clear();
            waiting.Clear();
            logged.Clear();
        }

        /// <summary>Both sides: whether this item's display look comes from its worn shape. Plain interfaces and attributes - safe on the server.</summary>
        public static bool Applies(CollectibleObject c)
        {
            if (c?.Code == null || c is not IWearableShapeSupplier) return false;
            return appliesByCode.GetOrAdd(c.Code.ToString(), _ =>
                c.GetCollectibleInterface<IContainedMeshSource>() == null && IAttachableToEntity.FromCollectible(c) != null);
        }

        /// <summary>
        /// Both sides: which LOOK this stack has - a stable hash of its attributes (pouches and the like live there), or null
        /// for items that do not come here. Part of the mesh key and the measured-size key, so a backpack with pouches and
        /// one without each get their own model and footprint. Freshness and temperature tick all the time and are left out.
        /// </summary>
        public static string LookKey(ItemStack stack)
        {
            if (stack == null || !Applies(stack.Collectible)) return null;
            var sb = new StringBuilder();
            Append(sb, stack.Attributes);
            return "look" + Fnv(sb.ToString()).ToString("x16");
        }

        static readonly HashSet<string> Volatile = new() { "transitionstate", "temperature" };

        static void Append(StringBuilder sb, ITreeAttribute tree)
        {
            if (tree == null) return;
            sb.Append('{');
            foreach (var kv in tree.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (Volatile.Contains(kv.Key)) continue;
                sb.Append(kv.Key).Append(':');
                if (kv.Value is ITreeAttribute sub) Append(sb, sub);
                else sb.Append(kv.Value?.ToJsonToken());
                sb.Append(',');
            }
            sb.Append('}');
        }

        // FNV-1a: the same on client and server (string.GetHashCode is not - it is seeded per process)
        static ulong Fnv(string s)
        {
            ulong h = 14695981039346656037UL;
            foreach (char ch in s)
            {
                h ^= ch;
                h *= 1099511628211UL;
            }
            return h;
        }

        /// <summary>Client: "worn:<look>" once built, with "|wait" while it is not - for the display's and the ghost's mesh keys.</summary>
        public static string MeshKeyPart(ItemStack stack)
        {
            string look = LookKey(stack);
            if (look == null) return null;
            string key = stack.Collectible.Code + "|" + look;
            return built.ContainsKey(key) ? "worn:" + look : "worn:" + look + "|wait";
        }

        /// <summary>
        /// Client, any thread: the worn-shape mesh (a copy, ours to transform), or null with <paramref name="pending"/> set
        /// while it is being built - the build is queued on the main thread and the window at <paramref name="pos"/> redrawn
        /// when it is done. Null and not pending: it could not be built, the plain model is the look.
        /// </summary>
        public static MeshData TryGet(ICoreClientAPI capi, ItemStack stack, BlockPos pos, out bool pending)
        {
            pending = false;
            string look = LookKey(stack);
            if (capi == null || look == null) return null;
            string key = stack.Collectible.Code + "|" + look;
            if (built.TryGetValue(key, out MeshData mesh)) return mesh?.Clone();

            pending = true;
            // no player yet (very early in loading): stand in now, ask again on the next redraw
            if (capi.World?.Player?.Entity == null) return null;
            var positions = waiting.GetOrAdd(key, _ => new ConcurrentDictionary<BlockPos, byte>());
            bool first = positions.IsEmpty;
            if (pos != null) positions[pos.Copy()] = 0;
            if (!first) return null;

            ItemStack copy = stack.Clone();
            capi.Event.EnqueueMainThreadTask(() =>
            {
                MeshData result = null;
                try { result = Build(capi, copy); }
                catch (Exception e) { capi.Logger.Warning("[UniversalDisplayLib] worn shape of {0} could not be built, showing its plain model: {1}", copy.Collectible.Code, e.Message); }
                built[key] = result;
                if (logged.TryAdd(copy.Collectible.Code.ToString(), 0))
                    capi.Logger.Notification("[UniversalDisplayLib] worn shape for {0}: {1}", copy.Collectible.Code,
                        result == null ? "nothing extra - plain model used" : result.VerticesCount + " vertices (look " + look + ")");

                if (waiting.TryRemove(key, out var redraw))
                    foreach (BlockPos p in redraw.Keys)
                    {
                        if (capi.World.BlockAccessor.GetBlockEntity(p) is BEUniversalDisplay be) be.MarkMeshesDirty();
                        capi.World.BlockAccessor.MarkBlockDirty(p);
                    }
            }, "universaldisplaylib-wornshape");
            return null;
        }

        /// <summary>Main thread: the stack's worn shape on the wearer's skeleton, the wearer's own faces removed, moved onto the plain model's spot.</summary>
        static MeshData Build(ICoreClientAPI capi, ItemStack stack)
        {
            IAttachableToEntity iatta = IAttachableToEntity.FromCollectible(stack.Collectible);
            if (iatta == null) return null;

            string wearer = stack.Collectible.Attributes?["wearerEntityCode"].AsString() ?? "player";
            Shape body = capi.World.GetEntityType(new AssetLocation(wearer))?.Client?.LoadedShape;
            if (body?.Elements == null) return null;

            // as the game's own full-body wearable mesh does (CollectibleBehaviorWearableAttachment.genFullBodyMesh)
            var shape = new Shape
            {
                Elements = body.CloneElements(),
                Animations = body.CloneAnimations(),
                AnimationsByCrc32 = body.AnimationsByCrc32,
                JointsById = body.JointsById,
                TextureWidth = body.TextureWidth,
                TextureHeight = body.TextureHeight,
                Textures = null
            };
            var own = new HashSet<ShapeElement>(ReferenceEqualityComparer.Instance);
            Collect(shape.Elements, own);

            string[] willDelete = Array.Empty<string>();
            var textures = new Dictionary<string, CompositeTexture>();
            EntityBehaviorContainer.addGearToShape(capi, capi.World.Player?.Entity, capi.BlockTextureAtlas, shape, stack, iatta, "default",
                "universaldisplaylib worn " + stack.Collectible.Code, ref willDelete, textures);

            // The wearer's own elements stay as the skeleton the gear hangs from, but draw nothing. A NEW face array -
            // CloneElements copies the array, not the faces, and those belong to the game's loaded seraph.
            bool gear = false;
            Strip(shape.Elements, own, ref gear);
            if (!gear) return null;

            var source = new Textures(capi, textures);
            capi.Tesselator.TesselateShape("universaldisplaylib worn shape", shape, out MeshData mesh, source);
            if (mesh == null || mesh.VerticesCount == 0) return null;
            if (source.Missing.Count > 0)
                capi.Logger.Debug("[UniversalDisplayLib] worn shape of {0}: no texture for {1}", stack.Collectible.Code, string.Join(", ", source.Missing));

            // onto the plain model's spot: bottom centre to bottom centre
            Cuboidf worn = BEUniversalDisplay.MeshBounds(mesh, Identity);
            Cuboidf plain = PlainBounds(capi, stack);
            if (worn != null)
            {
                float tx = (plain != null ? (plain.X1 + plain.X2) / 2 : 0.5f) - (worn.X1 + worn.X2) / 2;
                float ty = (plain?.Y1 ?? 0f) - worn.Y1;
                float tz = (plain != null ? (plain.Z1 + plain.Z2) / 2 : 0.5f) - (worn.Z1 + worn.Z2) / 2;
                mesh.Translate(tx, ty, tz);
            }
            mesh.RenderPassesAndExtraBits.Fill((short)EnumChunkRenderPass.OpaqueNoCull);
            return mesh;
        }

        static Cuboidf PlainBounds(ICoreClientAPI capi, ItemStack stack)
        {
            MeshData plain = null;
            if (stack.Class == EnumItemClass.Block) plain = capi.TesselatorManager.GetDefaultBlockMesh(stack.Block);
            else if (stack.Item != null) capi.Tesselator.TesselateItem(stack.Item, out plain);
            return plain == null || plain.VerticesCount == 0 ? null : BEUniversalDisplay.MeshBounds(plain, Identity);
        }

        static readonly float[] Identity = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

        static void Collect(ShapeElement[] elements, HashSet<ShapeElement> into)
        {
            if (elements == null) return;
            foreach (ShapeElement el in elements)
            {
                if (el == null) continue;
                into.Add(el);
                Collect(el.Children, into);
            }
        }

        static void Strip(ShapeElement[] elements, HashSet<ShapeElement> own, ref bool gear)
        {
            if (elements == null) return;
            foreach (ShapeElement el in elements)
            {
                if (el == null) continue;
                if (own.Contains(el)) el.FacesResolved = new ShapeElementFace[6];
                else gear = true;
                Strip(el.Children, own, ref gear);
            }
        }

        /// <summary>The textures addGearToShape collected (already in the block atlas), by the gear's own codes.</summary>
        sealed class Textures : ITexPositionSource
        {
            readonly ICoreClientAPI capi;
            readonly Dictionary<string, CompositeTexture> codes;
            public readonly HashSet<string> Missing = new();

            public Textures(ICoreClientAPI capi, Dictionary<string, CompositeTexture> codes)
            {
                this.capi = capi;
                this.codes = codes;
            }

            public Size2i AtlasSize => capi.BlockTextureAtlas.Size;

            public TextureAtlasPosition this[string code]
            {
                get
                {
                    if (code != null && codes.TryGetValue(code, out CompositeTexture ct) && ct.Baked != null)
                    {
                        TextureAtlasPosition pos = capi.BlockTextureAtlas.Positions[ct.Baked.TextureSubId];
                        if (pos != null) return pos;
                    }
                    Missing.Add(code ?? "?");
                    return capi.BlockTextureAtlas.UnknownTexturePosition;
                }
            }
        }
    }
}
