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
    /// The materials behaviour on a BLOCK (name: KitSettings.BehaviorName). The block says which of its shape's texture codes
    /// take which KIND of material and which texture role:
    /// <code>{ "name": "MyModMaterials", "properties": {
    ///     "slots": { "wood": { "frame": "planks" }, "glass": { "glass": "pane" } },
    ///     "fixed": { "putty": "game:block/coal/bituminous" } } }</code>
    /// The materials themselves come from the materials files (MaterialsSystem); which ones a block or stack has is its
    /// <c>types</c> tree. It is drawn in them in the hand, in slots, in the handbook and - with the block entity behaviour of
    /// the same name - placed in the world, and it drops / middle-clicks as itself in those materials.
    /// </summary>
    public sealed class BlockBehaviorMaterials : BlockBehavior
    {
        public const string TypesKey = "types";

        /// <summary>kind -> (shape texture code -> material texture role)</summary>
        public Dictionary<string, Dictionary<string, string>> Slots { get; private set; } = new();

        /// <summary>shape texture code -> texture, whatever the materials</summary>
        public Dictionary<string, string> Fixed { get; private set; } = new();

        /// <summary>shape texture code -> overlays this BLOCK always adds there (e.g. a darker plank).</summary>
        public Dictionary<string, string[]> Overlays { get; private set; } = new();

        /// <summary>The kinds named in the tooltip line, in order ("Oak & Leaded glass").</summary>
        public string[] Describe { get; private set; } = Array.Empty<string>();

        /// <summary>What the creative tab shows of the materials (<c>creative</c>) - see ShowRule.</summary>
        public ShowRule CreativeShow { get; private set; } = new();

        /// <summary>What the handbook shows (<c>handbook</c>); the creative tab's if not set.</summary>
        public ShowRule HandbookShow { get; private set; } = new();

        /// <summary>
        /// <c>showMaterials</c>: per kind, the only materials the creative tab and handbook show (<c>{ "wood": ["oak", "birch"] }</c>).
        /// Null: all. Crafting still takes every material.
        /// </summary>
        public Dictionary<string, string[]> ShowMaterials { get; private set; }

        /// <summary>True: the handbook shows ONE page for the block, flipping through its creative stacks (MaterialsHandbookGroups).</summary>
        public bool CompactHandbook { get; private set; }

        /// <summary>
        /// <c>"handbookMods": true</c>: the block's handbook page says which mods' materials it can be made with ("Can be made
        /// with: Vanilla, Vanilla Varnished") - the installed ones, worked out by itself. So modded materials can stay out of
        /// creative and the handbook, and players still learn they work.
        /// </summary>
        public bool HandbookMods { get; private set; }

        /// <summary>
        /// True (the default): the placed block is drawn in its materials by the block entity behaviour. False: your own block
        /// entity draws it (use MaterialTextureSource) - the behaviour then only keeps, saves and shows the materials.
        /// </summary>
        public bool DrawPlaced { get; private set; }

        public BlockBehaviorMaterials(Block block) : base(block) { }

        public override void Initialize(JsonObject properties)
        {
            base.Initialize(properties);
            Slots = properties["slots"].AsObject<Dictionary<string, Dictionary<string, string>>>() ?? new();
            Fixed = properties["fixed"].AsObject<Dictionary<string, string>>() ?? new();
            Overlays = properties["overlays"].AsObject<Dictionary<string, string[]>>() ?? new();
            Describe = properties["describe"].AsObject<string[]>() ?? Slots.Keys.ToArray();
            CreativeShow = ShowRule.Parse(properties["creative"], null);
            HandbookShow = ShowRule.Parse(properties["handbook"], CreativeShow);
            ShowMaterials = properties["showMaterials"].AsObject<Dictionary<string, string[]>>();
            CompactHandbook = properties["compactHandbook"].AsBool(false);
            HandbookMods = properties["handbookMods"].AsBool(false);
            DrawPlaced = properties["drawPlaced"].AsBool(KitSettings.HandlesPlacedBlocks);
        }

        public override void OnLoaded(ICoreAPI api)
        {
            base.OnLoaded(api);
            // the placed shape is read here, on the main thread - placed blocks are built on the tesselation thread
            if (api is ICoreClientAPI capi && DrawPlaced) placedShape = LoadShape(capi, block.Shape);
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
        /// of the block's materials - e.g. very aged wood dusting the glass). Null texture = not ours to fill.
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
        /// For a host that draws the placed block itself (KitSettings.HandlesPlacedBlocks false), before it builds: off the main
        /// thread, whether every texture these materials need is in the atlas. Any that is not is queued, and
        /// <paramref name="redrawAt"/> is redrawn once it is there. On the main thread they are simply put in (always true).
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

        /// <summary>
        /// "Oak & Leaded glass" - each described kind's name through its kind line, lang key
        /// <c>&lt;your modid&gt;:materialkind-&lt;kind&gt;</c> (e.g. "{0} glass"), joined by <c>&lt;your modid&gt;:material-join</c> (else " &amp; ").
        /// </summary>
        public string DescribeLine(IReadOnlyDictionary<string, string> types)
        {
            var system = MaterialsSystem.Instance;
            if (system == null || types == null) return null;
            string domain = block.Code?.Domain;
            var parts = new List<string>();
            foreach (var kind in Describe)
            {
                if (!types.TryGetValue(kind, out var code)) continue;
                var def = system.Get(kind, code, domain);
                string name = def?.DisplayName() ?? code;
                string kindKey = LangKey(domain, "materialkind-" + kind);
                parts.Add(kindKey != null ? Lang.Get(kindKey, name) : name);
            }
            string joinKey = LangKey(domain, "material-join");
            return parts.Count == 0 ? null : string.Join(joinKey != null ? Lang.Get(joinKey) : " & ", parts);
        }

        /// <summary>The block's own mod's lang key, else KitSettings.LangDomain's, else null.</summary>
        private static string LangKey(string domain, string key)
        {
            if (Lang.HasTranslation(domain + ":" + key)) return domain + ":" + key;
            return KitSettings.LangDomain != null && Lang.HasTranslation(KitSettings.LangDomain + ":" + key) ? KitSettings.LangDomain + ":" + key : null;
        }

        public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
        {
            base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
            string line = DescribeLine(TypesOf(inSlot.Itemstack?.Attributes));
            if (line != null) dsc.AppendLine(line);
        }

        // ---------- drops and middle-click keep the materials ----------

        public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, ref float dropChanceMultiplier, ref EnumHandling handling)
        {
            if (!KitSettings.HandlesPlacedBlocks) return null;   // the host's block class gives drops their materials
            var materials = MaterialsAt(world, pos);
            if (materials == null || materials.Types.Count == 0) return null;
            handling = EnumHandling.PreventDefault;
            // the block's own drops, as the game would make them - then each one that takes materials gets these
            var drops = new List<ItemStack>();
            if (block.Drops == null) drops.Add(new ItemStack(block));
            else
                foreach (var drop in block.Drops)
                {
                    var stack = drop?.GetNextItemStack(dropChanceMultiplier);
                    if (stack != null) drops.Add(stack);
                    if (drop?.LastDrop == true) break;
                }
            foreach (var stack in drops)
                if (stack.Collectible?.GetCollectibleBehavior<BlockBehaviorMaterials>(true) != null
                    || stack.Collectible?.GetCollectibleBehavior<CollectibleBehaviorMaterials>(true) != null) materials.CopyTo(stack);
            return drops.ToArray();
        }

        public override ItemStack OnPickBlock(IWorldAccessor world, BlockPos pos, ref EnumHandling handling)
        {
            if (!KitSettings.HandlesPlacedBlocks) return null;
            var materials = MaterialsAt(world, pos);
            if (materials == null || materials.Types.Count == 0) return null;
            handling = EnumHandling.PreventDefault;
            var stack = new ItemStack(block);
            materials.CopyTo(stack);
            return stack;
        }

        private static BEBehaviorMaterials MaterialsAt(IWorldAccessor world, BlockPos pos) =>
            world?.BlockAccessor.GetBlockEntity(pos)?.GetBehavior<BEBehaviorMaterials>();

        // ---------- placed in the world ----------

        private Shape placedShape;
        private static readonly ConcurrentDictionary<(int, string), MeshData> placedMeshes = new();

        /// <summary>
        /// The placed block in these materials (tesselation thread). Kept per block + materials; a mesh made while a texture
        /// was still on its way into the atlas is drawn but not kept - the block is redrawn once the texture is in.
        /// </summary>
        internal MeshData PlacedMesh(ICoreClientAPI capi, ITesselatorAPI tesselator, IReadOnlyDictionary<string, string> types, BlockPos pos)
        {
            string key = KeyOf(types);
            if (placedMeshes.TryGetValue((block.Id, key), out var known)) return known;
            var cshape = block.Shape;
            if (placedShape == null || cshape == null) return null;

            var source = new MaterialTextureSource(capi, block, this, types, placedShape, pos);
            tesselator.TesselateShape(new TesselationMetaData
            {
                TexSource = source,
                TypeForLogging = "materials kit " + block.Code,
                Rotation = new Vec3f(cshape.rotateX, cshape.rotateY, cshape.rotateZ),
                IgnoreElements = cshape.IgnoreElements
            }, placedShape, out var mesh);
            if (mesh == null) return null;
            if (cshape.Scale != 1f) mesh.Scale(new Vec3f(0.5f, 0f, 0.5f), cshape.Scale, cshape.Scale, cshape.Scale);
            if (cshape.offsetX != 0 || cshape.offsetY != 0 || cshape.offsetZ != 0) mesh.Translate(cshape.offsetX, cshape.offsetY, cshape.offsetZ);
            if (!source.Waiting) placedMeshes[(block.Id, key)] = mesh;
            return mesh;
        }

        private static Shape LoadShape(ICoreClientAPI capi, CompositeShape cshape) =>
            cshape?.Base == null ? null : Shape.TryGet(capi, cshape.Base.Clone().WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json"));

        // ---------- in hand, in the inventory, on the ground, in the handbook ----------

        private const string MeshRefsKey = "materialskit-meshes-" + KitSettings.BehaviorName;

        public static void ClearMeshes(ICoreClientAPI capi)
        {
            placedMeshes.Clear();
            var refs = ObjectCacheUtil.TryGet<Dictionary<(int, string), MultiTextureMeshRef>>(capi, MeshRefsKey);
            if (refs == null) return;
            foreach (var r in refs.Values) r?.Dispose();
            refs.Clear();
        }

        /// <summary>
        /// The materials key of a stack's <c>types</c> tree, worked out once per tree: this runs every frame for every stack
        /// drawn in a hand, slot or creative tab, and building the key each time made garbage every frame. A changed stack
        /// gets a new tree (WriteTypes replaces it), so a new key.
        /// </summary>
        private static readonly ConditionalWeakTable<ITreeAttribute, string> typeKeys = new();

        /// <summary>
        /// The block in its materials wherever it is drawn as an item - so a recipe output or a handbook stack shows the
        /// materials it really has. A stack with no materials keeps the game's own model.
        /// </summary>
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

        /// <summary>
        /// Shape elements the item model leaves out besides the shape's own ignoreElements - set by a host mod whose shapes
        /// carry marker elements of their own (Universal Display's display-surface markers).
        /// </summary>
        public static System.Func<Block, string[]> ExtraIgnoreElements;

        private MeshData InventoryMesh(ICoreClientAPI capi, IReadOnlyDictionary<string, string> types)
        {
            var cshape = block.ShapeInventory ?? block.Shape;
            var shape = LoadShape(capi, cshape);
            if (shape == null) return null;
            var source = new MaterialTextureSource(capi, block, this, types, shape, null);
            capi.Tesselator.TesselateShape(new TesselationMetaData
            {
                TexSource = source,
                TypeForLogging = "materials kit " + block.Code,
                Rotation = new Vec3f(cshape.rotateX, cshape.rotateY, cshape.rotateZ),
                IgnoreElements = (cshape.IgnoreElements ?? Array.Empty<string>()).Concat(ExtraIgnoreElements?.Invoke(block) ?? Array.Empty<string>()).ToArray()
            }, shape, out var mesh);
            return mesh;
        }
    }

    /// <summary>
    /// The materials behaviour on the BLOCK ENTITY (same name): which materials this placed block has - read from the placed
    /// stack, saved and loaded under <c>types</c> - drawn in them (unless the block says <c>"drawPlaced": false</c>) and named
    /// in the block info when looked at. The block needs a block entity: <c>"entityClass": "Generic"</c> if it has none.
    /// </summary>
    public sealed class BEBehaviorMaterials : BlockEntityBehavior
    {
        public Dictionary<string, string> Types { get; private set; } = new();

        public string Key => BlockBehaviorMaterials.KeyOf(Types);

        public BEBehaviorMaterials(BlockEntity blockentity) : base(blockentity) { }

        public override void OnBlockPlaced(ItemStack byItemStack = null)
        {
            base.OnBlockPlaced(byItemStack);
            if (byItemStack != null) Types = BlockBehaviorMaterials.TypesOf(byItemStack.Attributes);
        }

        /// <summary>The materials onto a stack (a drop, a middle-click).</summary>
        public void CopyTo(ItemStack stack)
        {
            if (stack != null && Types.Count > 0) BlockBehaviorMaterials.WriteTypes(stack.Attributes, Types);
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            BlockBehaviorMaterials.WriteTypes(tree, Types);
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
        {
            base.FromTreeAttributes(tree, worldAccessForResolve);
            var before = Key;
            Types = BlockBehaviorMaterials.TypesOf(tree);
            // a placed block the kit draws, whose materials changed while it was already in the world (just placed): draw it
            // again. Not while a chunk loads (Api not set yet - the chunk is drawn after this anyway), and not for a host that
            // draws its own blocks - each would be drawn twice.
            if (Key != before && Api is ICoreClientAPI && Blockentity.Pos != null
                && Blockentity.Block?.GetBehavior<BlockBehaviorMaterials>()?.DrawPlaced == true)
                Api.World.BlockAccessor.MarkBlockDirty(Blockentity.Pos);
        }

        public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
        {
            if (Types.Count == 0 || Api is not ICoreClientAPI capi) return base.OnTesselation(mesher, tessThreadTesselator);
            var materials = Blockentity.Block?.GetBehavior<BlockBehaviorMaterials>();
            if (materials?.DrawPlaced != true) return base.OnTesselation(mesher, tessThreadTesselator);
            var mesh = materials.PlacedMesh(capi, tessThreadTesselator, Types, Blockentity.Pos);
            if (mesh == null) return base.OnTesselation(mesher, tessThreadTesselator);
            mesher.AddMeshData(mesh);
            return true;
        }

        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
        {
            base.GetBlockInfo(forPlayer, dsc);
            string line = Blockentity.Block?.GetBehavior<BlockBehaviorMaterials>()?.DescribeLine(Types);
            if (line != null) dsc.AppendLine(line);
        }
    }
}
