using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace UniversalDisplayLib.Materials
{
    /// <summary>
    /// <c>UDMaterials</c> - what Attribute Rendering Library did for these blocks, done by the lib
    /// (Calm, 2026-10-08: no ARL dependency, materials as data, simpler for other modders). A block
    /// says which of its shape's texture codes take which KIND of material and which texture role:
    /// <code>{ "name": "UDMaterials", "properties": {
    ///     "slots": { "wood": { "frame": "planks" }, "glass": { "glass": "pane" } },
    ///     "fixed": { "putty": "game:block/coal/bituminous", "vintage": "game:block/glass/vintage" } } }</code>
    /// The materials themselves come from the material files (MaterialsSystem); which ones a block or
    /// stack has is its <c>types</c> tree - the SAME tree ARL saved, so every window already in a world
    /// keeps its wood and glass.
    /// </summary>
    public sealed class BlockBehaviorUDMaterials : BlockBehavior
    {
        public const string TypesKey = "types";

        /// <summary>kind -> (shape texture code -> material texture role)</summary>
        public Dictionary<string, Dictionary<string, string>> Slots { get; private set; } = new();

        /// <summary>shape texture code -> texture, whatever the materials</summary>
        public Dictionary<string, string> Fixed { get; private set; } = new();

        /// <summary>shape texture code -> overlays this BLOCK always adds there (the plank window darkens its wood).</summary>
        public Dictionary<string, string[]> Overlays { get; private set; } = new();

        /// <summary>The kinds named in the tooltip line, in order ("Oak & Leaded glass").</summary>
        public string[] Describe { get; private set; } = Array.Empty<string>();

        public BlockBehaviorUDMaterials(Block block) : base(block) { }

        public override void Initialize(JsonObject properties)
        {
            base.Initialize(properties);
            Slots = properties["slots"].AsObject<Dictionary<string, Dictionary<string, string>>>() ?? new();
            Fixed = properties["fixed"].AsObject<Dictionary<string, string>>() ?? new();
            Overlays = properties["overlays"].AsObject<Dictionary<string, string[]>>() ?? new();
            Describe = properties["describe"].AsObject<string[]>() ?? Slots.Keys.ToArray();
        }

        /// <summary>The material codes on a stack or block entity tree, kind -> code.</summary>
        public static Dictionary<string, string> TypesOf(ITreeAttribute tree)
        {
            var result = new Dictionary<string, string>();
            if (tree?[TypesKey] is ITreeAttribute types)
                foreach (var kv in types) if (kv.Value is StringAttribute s && !string.IsNullOrEmpty(s.value)) result[kv.Key] = s.value;
            return result;
        }

        public static void WriteTypes(ITreeAttribute tree, IReadOnlyDictionary<string, string> types)
        {
            tree.RemoveAttribute(TypesKey);
            if (types == null || types.Count == 0) return;
            var t = tree.GetOrAddTreeAttribute(TypesKey);
            foreach (var kv in types) t.SetString(kv.Key, kv.Value);
        }

        /// <summary>A stable key for caches: "glass=plain;wood=oak".</summary>
        public static string KeyOf(IReadOnlyDictionary<string, string> types) =>
            types == null ? "" : string.Join(";", types.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value));

        /// <summary>
        /// What fills one shape texture code for these materials: the texture and any overlays (from ANY
        /// of the block's materials - very aged wood dusts the glass). Null texture = not ours to fill.
        /// </summary>
        public (string texture, IReadOnlyCollection<string> overlays) Resolve(IReadOnlyDictionary<string, string> types, string textureCode)
        {
            var system = MaterialsSystem.Instance;
            string texture = null;
            var overlays = new List<string>();
            if (system != null && types != null)
            {
                foreach (var (kind, slot) in Slots)
                {
                    if (!slot.TryGetValue(textureCode, out var role) || !types.TryGetValue(kind, out var code)) continue;
                    var def = system.Get(kind, code, block.Code?.Domain);
                    if (def != null && def.Textures.TryGetValue(role, out var t) && !string.IsNullOrEmpty(t)) { texture = t; break; }
                }
                foreach (var (kind, code) in types)
                {
                    var def = system.Get(kind, code, block.Code?.Domain);
                    if (def != null && def.Overlays.TryGetValue(textureCode, out var o) && o != null) overlays.AddRange(o);
                }
            }
            if (texture == null && Fixed.TryGetValue(textureCode, out var fixedTexture)) texture = fixedTexture;
            if (texture != null && Overlays.TryGetValue(textureCode, out var own) && own != null) overlays.InsertRange(0, own);
            return (texture, overlays);
        }

        /// <summary>Every shape texture code this behaviour fills - for loading textures ahead of a build.</summary>
        public IEnumerable<string> TextureCodes => Slots.Values.SelectMany(s => s.Keys).Concat(Fixed.Keys).Distinct();

        /// <summary>
        /// Off the main thread: whether every texture these materials need is in the atlas. Any that is
        /// not is queued, and <paramref name="redrawAt"/> is redrawn once it is there. On the main thread
        /// they are simply put in, so this is always true there.
        /// </summary>
        public bool TexturesReady(ICoreClientAPI capi, IReadOnlyDictionary<string, string> types, BlockPos redrawAt)
        {
            bool ready = true;
            foreach (var code in TextureCodes)
            {
                var (texture, overlays) = Resolve(types, code);
                if (texture == null) continue;
                if (!MaterialTextures.TryGet(capi, texture, overlays, redrawAt, out _) && !MaterialTextures.IsMissing(texture, overlays)) ready = false;
            }
            return ready;
        }

        /// <summary>"Oak & Leaded glass" - each described kind's name through its kind line ("{0} glass").</summary>
        public string DescribeLine(IReadOnlyDictionary<string, string> types)
        {
            var system = MaterialsSystem.Instance;
            if (system == null || types == null) return null;
            var parts = new List<string>();
            foreach (var kind in Describe)
            {
                if (!types.TryGetValue(kind, out var code)) continue;
                var def = system.Get(kind, code, block.Code?.Domain);
                string name = def?.DisplayName() ?? code;
                string kindKey = "universaldisplaylib:materialkind-" + kind;
                parts.Add(Lang.HasTranslation(kindKey) ? Lang.Get(kindKey, name) : name);
            }
            return parts.Count == 0 ? null : string.Join(Lang.Get("universaldisplaylib:material-join"), parts);
        }

        public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
        {
            base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
            string line = DescribeLine(TypesOf(inSlot.Itemstack?.Attributes));
            if (line != null) dsc.AppendLine(line);
        }

        // ---------- in hand, in the inventory, on the ground, in the handbook ----------

        private const string MeshRefsKey = "universaldisplaylib-materialmeshes";

        public static void ClearMeshes(ICoreClientAPI capi)
        {
            var refs = ObjectCacheUtil.TryGet<Dictionary<(int, string), MultiTextureMeshRef>>(capi, MeshRefsKey);
            if (refs == null) return;
            foreach (var r in refs.Values) r?.Dispose();
            refs.Clear();
        }

        /// <summary>
        /// The block in its materials wherever it is drawn as an item - so a recipe output or a handbook
        /// stack shows the wood and glass it really has (Calm: "a recipe can actually show the correct
        /// materials"). A stack with no materials keeps the game's own model.
        /// </summary>
        /// <summary>
        /// The materials key of a stack's <c>types</c> tree, worked out once per tree (Calm, 2026-10-09 - "anything that could
        /// cause stutter"): this runs every frame for every window drawn in a hand, slot or creative tab, and building the
        /// key each time made garbage every frame. A changed stack gets a new tree (WriteTypes replaces it), so a new key.
        /// </summary>
        private static readonly ConditionalWeakTable<ITreeAttribute, string> typeKeys = new();

        public override void OnBeforeRender(ICoreClientAPI capi, ItemStack itemstack, EnumItemRenderTarget target, ref ItemRenderInfo renderinfo)
        {
            if (itemstack?.Attributes?[TypesKey] is not ITreeAttribute tree) return;
            if (!typeKeys.TryGetValue(tree, out var typesKey))
            {
                typesKey = KeyOf(TypesOf(itemstack.Attributes));
                typeKeys.AddOrUpdate(tree, typesKey);
            }
            if (typesKey.Length == 0) return;
            var refs = ObjectCacheUtil.GetOrCreate(capi, MeshRefsKey, () => new Dictionary<(int, string), MultiTextureMeshRef>());
            if (!refs.TryGetValue((block.Id, typesKey), out var meshRef))
            {
                var mesh = InventoryMesh(capi, TypesOf(itemstack.Attributes));
                meshRef = mesh == null ? null : capi.Render.UploadMultiTextureMesh(mesh);
                refs[(block.Id, typesKey)] = meshRef;
            }
            if (meshRef != null) renderinfo.ModelRef = meshRef;
        }

        private MeshData InventoryMesh(ICoreClientAPI capi, IReadOnlyDictionary<string, string> types)
        {
            var cshape = block.ShapeInventory ?? block.Shape;
            if (cshape?.Base == null) return null;
            var location = cshape.Base.Clone().WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json");
            var shape = Shape.TryGet(capi, location);
            if (shape == null) return null;

            // The display markers are not part of the model.
            var ignore = (cshape.IgnoreElements ?? Array.Empty<string>())
                .Concat(block.GetBehavior<BlockBehaviorDisplaySurfaces>()?.SurfaceElementNames ?? Array.Empty<string>())
                .ToArray();
            var source = new MaterialTextureSource(capi, block, this, types, shape, null);
            capi.Tesselator.TesselateShape(new TesselationMetaData
            {
                TexSource = source,
                TypeForLogging = "universaldisplaylib " + block.Code,
                Rotation = new Vec3f(cshape.rotateX, cshape.rotateY, cshape.rotateZ),
                IgnoreElements = ignore
            }, shape, out var mesh);
            return mesh;
        }
    }

    /// <summary>
    /// <c>UDMaterials</c> on the block entity: which materials this placed block has - read from the
    /// placed stack, saved and loaded under the same <c>types</c> tree ARL used, so old worlds load as
    /// they were. Shows the materials line when looked at.
    /// </summary>
    public sealed class BEBehaviorUDMaterials : BlockEntityBehavior
    {
        public Dictionary<string, string> Types { get; private set; } = new();

        public string Key => BlockBehaviorUDMaterials.KeyOf(Types);

        public BEBehaviorUDMaterials(BlockEntity blockentity) : base(blockentity) { }

        public override void OnBlockPlaced(ItemStack byItemStack = null)
        {
            base.OnBlockPlaced(byItemStack);
            if (byItemStack != null) Types = BlockBehaviorUDMaterials.TypesOf(byItemStack.Attributes);
        }

        /// <summary>The materials onto a stack (a drop, a middle-click).</summary>
        public void CopyTo(ItemStack stack)
        {
            if (stack != null && Types.Count > 0) BlockBehaviorUDMaterials.WriteTypes(stack.Attributes, Types);
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            BlockBehaviorUDMaterials.WriteTypes(tree, Types);
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
        {
            base.FromTreeAttributes(tree, worldAccessForResolve);
            Types = BlockBehaviorUDMaterials.TypesOf(tree);
        }

        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
        {
            base.GetBlockInfo(forPlayer, dsc);
            string line = Blockentity.Block?.GetBehavior<BlockBehaviorUDMaterials>()?.DescribeLine(Types);
            if (line != null) dsc.AppendLine(line);
        }
    }
}
