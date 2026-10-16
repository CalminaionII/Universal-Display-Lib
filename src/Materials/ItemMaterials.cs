using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace UniversalDisplayLib.Materials
{
    /// <summary>
    /// The materials behaviour (KitSettings.BehaviorName) on an ITEM - one item code whose material is in its attributes, like
    /// Window Display's glass panes. The item version of <see cref="BlockBehaviorMaterials"/>: the stack's <c>types</c> tree
    /// picks the materials, and the item is drawn in them in the hand, in slots, on shelves / sills / display cases
    /// (IContainedMeshSource) and in a ground-storage pile (<see cref="GroundPileMaterialsPatch"/>), and named from them.
    /// <code>{ "name": "MyModMaterials", "properties": {
    ///     "slots": { "glass": { "glass": "pane" } }, "name": "mymod:glasspane-name" } }</code>
    /// <c>name</c> is a lang key with {0}, {1}... = the material names in <c>describe</c> order (default: the slots' kinds).
    /// </summary>
    public sealed class CollectibleBehaviorMaterials : CollectibleBehavior, IContainedMeshSource
    {
        public Dictionary<string, Dictionary<string, string>> Slots { get; private set; } = new();
        public Dictionary<string, string> Fixed { get; private set; } = new();
        public string[] Describe { get; private set; } = Array.Empty<string>();
        public string NameKey { get; private set; }
        public bool CompactHandbook { get; private set; }
        public bool HandbookMods { get; private set; }
        public string HandbookTitle { get; private set; }
        public ShowRule CreativeShow { get; private set; } = new();
        public ShowRule HandbookShow { get; private set; } = new();
        public Dictionary<string, string[]> ShowMaterials { get; private set; }
        /// <summary>Whether the item asks for its creative list to be made from the materials (it has <c>creative</c> or <c>handbook</c>).</summary>
        public bool ShowSet { get; private set; }

        private static readonly ConcurrentDictionary<string, MeshData> meshes = new();
        private static readonly Dictionary<string, MultiTextureMeshRef> refs = new();
        private static readonly ConditionalWeakTable<ITreeAttribute, string> keys = new();

        private ICoreAPI api;

        public CollectibleBehaviorMaterials(CollectibleObject collObj) : base(collObj) { }

        public override void OnLoaded(ICoreAPI api)
        {
            base.OnLoaded(api);
            this.api = api;
        }

        public override void Initialize(JsonObject properties)
        {
            base.Initialize(properties);
            Slots = properties["slots"].AsObject<Dictionary<string, Dictionary<string, string>>>() ?? new();
            Fixed = properties["fixed"].AsObject<Dictionary<string, string>>() ?? new();
            Describe = properties["describe"].AsObject<string[]>() ?? Slots.Keys.ToArray();
            NameKey = properties["name"].AsString();
            CompactHandbook = properties["compactHandbook"].AsBool(false);
            HandbookMods = properties["handbookMods"].AsBool(false);
            HandbookTitle = properties["handbookTitle"].AsString(null);
            CreativeShow = ShowRule.Parse(properties["creative"], null);
            HandbookShow = ShowRule.Parse(properties["handbook"], CreativeShow);
            ShowMaterials = properties["showMaterials"].AsObject<Dictionary<string, string[]>>();
            ShowSet = properties["creative"].Exists || properties["handbook"].Exists;
        }

        public static void ClearMeshes()
        {
            meshes.Clear();
            foreach (var r in refs.Values) r?.Dispose();
            refs.Clear();
        }

        private string Domain => collObj.Code?.Domain;

        private string KeyOf(ItemStack stack)
        {
            var tree = stack?.Attributes?[BlockBehaviorMaterials.TypesKey] as ITreeAttribute;
            if (tree == null) return "";
            if (!keys.TryGetValue(tree, out var key))
            {
                key = BlockBehaviorMaterials.KeyOf(BlockBehaviorMaterials.TypesOf(stack.Attributes));
                keys.AddOrUpdate(tree, key);
            }
            return key;
        }

        /// <summary>The texture a shape texture code gets for these materials, or null for the item's own.</summary>
        public string TextureFor(IReadOnlyDictionary<string, string> types, string textureCode)
        {
            var system = MaterialsSystem.Instance;
            if (system != null && types != null)
                foreach (var (kind, slot) in Slots)
                {
                    if (!slot.TryGetValue(textureCode, out var role) || !types.TryGetValue(kind, out var code)) continue;
                    var def = system.Get(kind, code, Domain);
                    if (def != null && def.Textures.TryGetValue(role, out var t) && !string.IsNullOrEmpty(t)) return t;
                }
            return Fixed.TryGetValue(textureCode, out var f) ? f : null;
        }

        /// <summary>Any thread: the block-atlas position for a shape texture code, queued for the main thread if it is not in yet.</summary>
        public TextureAtlasPosition PositionFor(ICoreClientAPI capi, IReadOnlyDictionary<string, string> types, string textureCode, BlockPos redrawAt)
        {
            var texture = TextureFor(types, textureCode);
            if (texture == null) return null;
            return MaterialTextures.TryGet(capi, texture, null, redrawAt, out var position) ? position : null;
        }

        public override void GetHeldItemName(StringBuilder sb, ItemStack itemStack)
        {
            base.GetHeldItemName(sb, itemStack);
            if (NameKey == null || MaterialsSystem.Instance is not { } system) return;
            var types = BlockBehaviorMaterials.TypesOf(itemStack?.Attributes);
            if (types.Count == 0) return;
            var names = Describe.Select(kind => types.TryGetValue(kind, out var code) ? system.Get(kind, code, Domain)?.DisplayName() ?? code : "").ToArray();
            sb.Clear();
            sb.Append(Lang.Get(NameKey, names));
        }

        // ---------- drawing ----------

        /// <summary>The item's shape in these materials, block atlas. Null if a texture is not in the atlas yet (off the main thread).</summary>
        private MeshData MeshFor(ICoreClientAPI capi, ItemStack stack, BlockPos redrawAt)
        {
            string key = collObj.Id + "|" + KeyOf(stack);
            if (meshes.TryGetValue(key, out var cached)) return cached;

            var cshape = (collObj as Item)?.Shape ?? (collObj as Block)?.Shape;
            if (cshape?.Base == null) return null;
            var shape = Shape.TryGet(capi, cshape.Base.Clone().WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json"));
            if (shape == null) return null;

            var types = BlockBehaviorMaterials.TypesOf(stack.Attributes);
            var source = new ItemTextures(capi, this, types, shape, redrawAt);
            capi.Tesselator.TesselateShape(new TesselationMetaData
            {
                TexSource = source,
                TypeForLogging = "materials kit " + collObj.Code,
                Rotation = new Vec3f(cshape.rotateX, cshape.rotateY, cshape.rotateZ)
            }, shape, out var mesh);
            if (source.Waiting) return null;
            meshes[key] = mesh;
            return mesh;
        }

        public override void OnBeforeRender(ICoreClientAPI capi, ItemStack itemstack, EnumItemRenderTarget target, ref ItemRenderInfo renderinfo)
        {
            base.OnBeforeRender(capi, itemstack, target, ref renderinfo);
            string key = collObj.Id + "|" + KeyOf(itemstack);
            if (key.EndsWith("|")) return;   // no materials - the item's own model
            if (!refs.TryGetValue(key, out var meshRef))
            {
                var mesh = MeshFor(capi, itemstack, null);
                if (mesh == null) return;
                refs[key] = meshRef = capi.Render.UploadMultiTextureMesh(mesh);
            }
            renderinfo.ModelRef = meshRef;
        }

        public MeshData GenMesh(ItemSlot slot, ITextureAtlasAPI targetAtlas, BlockPos atBlockPos)
        {
            if (api is not ICoreClientAPI capi || slot?.Itemstack == null || KeyOf(slot.Itemstack).Length == 0) return null;
            return MeshFor(capi, slot.Itemstack, atBlockPos)?.Clone();
        }

        public string GetMeshCacheKey(ItemSlot slot) => collObj.Code + "|" + KeyOf(slot?.Itemstack);

        /// <summary>Materials first, then the item's own textures and the shape's own - all from the block atlas.</summary>
        private sealed class ItemTextures : ITexPositionSource
        {
            private readonly ICoreClientAPI capi;
            private readonly CollectibleBehaviorMaterials owner;
            private readonly IReadOnlyDictionary<string, string> types;
            private readonly Shape shape;
            private readonly BlockPos redrawAt;
            public bool Waiting { get; private set; }

            public ItemTextures(ICoreClientAPI capi, CollectibleBehaviorMaterials owner, IReadOnlyDictionary<string, string> types, Shape shape, BlockPos redrawAt)
            {
                this.capi = capi; this.owner = owner; this.types = types; this.shape = shape; this.redrawAt = redrawAt;
            }

            public Size2i AtlasSize => capi.BlockTextureAtlas.Size;

            public TextureAtlasPosition this[string textureCode]
            {
                get
                {
                    string texture = owner.TextureFor(types, textureCode);
                    if (texture == null)
                    {
                        var own = (owner.collObj as Item)?.Textures;
                        if (own != null && own.TryGetValue(textureCode, out var ct) && ct?.Base != null) texture = ct.Base.ToString();
                        else if (shape?.Textures != null && shape.Textures.TryGetValue(textureCode, out var loc) && loc != null) texture = loc.ToString();
                    }
                    if (texture == null) return capi.BlockTextureAtlas.UnknownTexturePosition;
                    if (MaterialTextures.TryGet(capi, texture, null, redrawAt, out var position)) return position;
                    if (!MaterialTextures.IsMissing(texture, null)) Waiting = true;
                    return capi.BlockTextureAtlas.UnknownTexturePosition;
                }
            }
        }
    }

    /// <summary>
    /// A ground-storage PILE of an item with materials is drawn in them. Vanilla's stacking pile takes its textures from the
    /// collectible's code (GroundStorable stackingTextures, placeholders filled from the variant - vssurvivalmod
    /// BlockEntityGroundStorage indexer), so one code would draw every pile in one glass. A postfix on that indexer: when the
    /// pile's item has <see cref="CollectibleBehaviorMaterials"/>, the shape texture code gets the material's texture. The
    /// pile's mesh is cached under the item's own GetMeshCacheKey (it is the IContainedMeshSource), so each material gets
    /// its own pile mesh. Client only - applied by MaterialsSystem.StartClientSide; only for this mod's items.
    /// </summary>
    // No [HarmonyPatch] attribute on purpose: a host mod that applies PatchAll to its own assembly would otherwise apply
    // the kit's patches a second time. The kit applies each one by name (see the Apply methods).
    public static class GroundPileMaterialsPatch
    {
        internal static MethodBase TargetMethod() => AccessTools.Method(typeof(BlockEntityGroundStorage), "get_Item", new[] { typeof(string) });

        internal static void Postfix(BlockEntityGroundStorage __instance, string textureCode, ref TextureAtlasPosition __result)
        {
            try
            {
                if (__instance?.StorageProps?.Layout != EnumGroundStorageLayout.Stacking || __instance.Api is not ICoreClientAPI capi) return;
                var stack = __instance.Inventory?[0]?.Itemstack;
                var materials = stack?.Collectible?.GetCollectibleBehavior<CollectibleBehaviorMaterials>(true);
                if (materials == null) return;
                var position = materials.PositionFor(capi, BlockBehaviorMaterials.TypesOf(stack.Attributes), textureCode, __instance.Pos);
                if (position != null) __result = position;
            }
            catch { /* a pile must never fail to draw over this */ }
        }
    }
}
