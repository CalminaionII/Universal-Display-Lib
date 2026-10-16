using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Util;
using HarmonyLib;
using UniversalDisplayLib.Handbook;
using Vintagestory.GameContent;

namespace UniversalDisplayLib.Materials
{
    /// <summary>
    /// One material from <c>assets/&lt;your modid&gt;/config/materials/&lt;kind&gt;.json</c> (KitSettings.Folder). The FILE
    /// NAME is the kind - wood.json, glass.json, metal.json... A wood mod's woods are added by a patch onto that file
    /// (<c>addeach</c> + <c>dependsOn</c>) - no recipe or blocktype changes. Each copy of the kit reads ONLY its own mod's
    /// files, so two mods carrying it never see each other's materials.
    ///
    /// <code>{ "code": "oak", "name": "game:material-oak", "item": "game:plank-oak",
    ///   "textures": { "planks": "game:block/wood/planks/oak1", "debarked": "game:block/wood/debarked/oak" },
    ///   "overlays": { "glass": [ "game:block/overlay/dust-sparse" ] } }</code>
    ///
    /// A code of <c>*</c> answers any code not listed, with <c>{code}</c> filled in - what ARL's
    /// <c>wood-*</c> rule did, so every vanilla wood and glass needs no entry of its own.
    /// </summary>
    public sealed class MaterialDefinition
    {
        public string Code { get; set; }

        /// <summary>Lang key for its name ("Oak", "Plain"). May contain {code}.</summary>
        public string Name { get; set; }

        /// <summary>The ingredient that means this material in a recipe (a plank, a pane...). For recipes made from the materials.</summary>
        public string Item { get; set; }

        /// <summary>Its textures by role (planks, debarked, pane...). A block's slots say which shape texture takes which role.</summary>
        public Dictionary<string, string> Textures { get; set; } = new();

        /// <summary>Overlays this material adds to a SHAPE texture code, whatever fills it (very aged wood dusts the glass).</summary>
        public Dictionary<string, string[]> Overlays { get; set; } = new();

        /// <summary>
        /// On a <c>*</c> entry: its item pattern only answers for codes the file LISTS (Calm, 2026-10-14 - support beams for
        /// Wooden Display Shelves' frames: <c>game:supportbeam-{code}</c> also matches rotten and very rotten beams, whose
        /// textures exist, and those are not frame woods). Off: any code whose textures exist.
        /// </summary>
        public bool ListedOnly { get; set; }

        /// <summary>
        /// Which mod this material belongs to, for the handbook's by-mod lists and the per-mod creative stacks (SourceOf).
        /// Optional - without it, the domain of its <c>item</c>. For a mod that puts its items in the game's namespace
        /// (Calm, 2026-10-14: Pewter's rods are game:rod-pewter, "and pewter can have add ons for other metals"): its patch
        /// writes <c>"source": "pewter"</c>, each add-on its own mod id.
        /// </summary>
        public string Source { get; set; }

        public MaterialDefinition For(string code)
        {
            if (Code != "*") return this;
            return new MaterialDefinition
            {
                Code = code,
                Name = Name?.Replace("{code}", code),
                Item = Item?.Replace("{code}", code),
                Source = Source,
                Textures = Textures.ToDictionary(kv => kv.Key, kv => kv.Value?.Replace("{code}", code)),
                Overlays = Overlays.ToDictionary(kv => kv.Key, kv => kv.Value?.Select(o => o.Replace("{code}", code)).ToArray())
            };
        }

        public string DisplayName()
        {
            if (!string.IsNullOrEmpty(Name) && Lang.HasTranslation(Name)) return Lang.Get(Name);
            return string.IsNullOrEmpty(Code) ? Code : char.ToUpperInvariant(Code[0]) + Code.Substring(1);
        }
    }

    /// <summary>
    /// What the creative tab or the handbook shows of a block's materials - from its <c>creative</c> / <c>handbook</c>:
    /// <code>"creative": { "vanilla": "all", "modded": "each" }</code>
    /// vanilla (the game's materials): "all" every combination (default), "each" each material once, "written" only the
    /// stacks the blocktype writes. modded (other mods' materials): "none" (default - still craftable), "each" each once,
    /// "one" one stack per mod, "all" every combination. A plain string sets vanilla only: <c>"creative": "each"</c>.
    /// The handbook shows what the creative tab does unless it has its own.
    /// </summary>
    public sealed class ShowRule
    {
        public string Vanilla = "all";
        public string Modded = "none";

        public static ShowRule Parse(JsonObject json, ShowRule fallback)
        {
            fallback ??= new ShowRule();
            if (json == null || !json.Exists) return fallback;
            if (json.Token is Newtonsoft.Json.Linq.JValue) return new ShowRule { Vanilla = json.AsString(fallback.Vanilla), Modded = fallback.Modded };
            return new ShowRule { Vanilla = json["vanilla"].AsString(fallback.Vanilla), Modded = json["modded"].AsString(fallback.Modded) };
        }
    }

    /// <summary>
    /// Loads this mod's material files and answers "the wood called oak". Both sides load the same files
    /// (names and textures on the client; recipes and drops on the server).
    /// </summary>
    public sealed class MaterialsSystem : ModSystem
    {
        public const string Folder = KitSettings.Folder;

        /// <summary>This mod's id - the only domain whose files, blocks and recipes this copy of the kit handles.</summary>
        public string OwnDomain { get; private set; }

        /// <summary>The kit's Harmony patches on the client (handbook, ground piles); the recipe ones are in SameMaterialsPatch.</summary>
        private Harmony clientHarmony;

        public static MaterialsSystem Instance { get; private set; }

        private Dictionary<string, Dictionary<string, MaterialDefinition>> kinds = new();

        /// <summary>domain -> kind -> code: what each mod's OWN files define (patches onto a file count as that file's mod).</summary>
        private Dictionary<string, Dictionary<string, Dictionary<string, MaterialDefinition>>> byDomain = new();

        public IReadOnlyDictionary<string, Dictionary<string, MaterialDefinition>> Kinds => kinds;

        public override double ExecuteOrder() => 0.1;

        private ICoreAPI api;

        public override void Start(ICoreAPI api)
        {
            Instance = this;
            this.api = api;
            OwnDomain = Mod.Info.ModID;
            MaterialRecipes.Logger = api.Logger;
            api.RegisterBlockBehaviorClass(KitSettings.BehaviorName, typeof(BlockBehaviorMaterials));
            api.RegisterBlockEntityBehaviorClass(KitSettings.BehaviorName, typeof(BEBehaviorMaterials));
            // the same, for items (one code, the material in its attributes - like Window Display's glass panes)
            api.RegisterCollectibleBehaviorClass(KitSettings.BehaviorName, typeof(CollectibleBehaviorMaterials));
            SameMaterialsPatch.Apply(OwnDomain + ".materialskit.recipes");
        }

        public override void Dispose()
        {
            if (api != null) SameMaterialsPatch.Remove();
            clientHarmony?.UnpatchAll(clientHarmony.Id);
            clientHarmony = null;
            HandbookGroups.Remove();
            api = null;
        }

        // ---------- the planks must be the window's wood ----------

        /// <summary>
        /// A recipe that copies its materials from a block in the grid (copyAttributesFrom - the Display Unit
        /// Window from an awning, the vanilla pane shelf from a vanilla pane window) takes only the planks of
        /// THAT block's wood (Calm, 2026-10-09: "is there a way to make this only take the correct boards?").
        /// Any ingredient that IS a material (a material's `item`, e.g. game:plank-pine = wood pine) must be
        /// the material the block carries for that kind. Only for recipes whose output uses the kit's materials behaviour. Both
        /// sides, so the grid's output preview agrees with the server. Called from SameMaterialsPatch (a postfix on
        /// GridRecipe.Matches) - NOT the MatchesGridRecipe event, whose "no" was overridden in game.
        /// </summary>
        internal bool SameMaterialsInGrid(IPlayer player, GridRecipe recipe, ItemSlot[] ingredients, int gridWidth)
        {
            if (recipe == null || ingredients == null) return true;
            // a material recipe (MaterialRecipes): each letter's items must be ONE material of its kind
            if (MaterialRecipes.LettersOf(recipe).Count > 0 && MaterialRecipes.OutputUsesMaterials(recipe)
                && MaterialRecipes.ReadGrid(recipe, ingredients, gridWidth, recipe.Output?.ResolvedItemStack?.Collectible?.Code?.Domain) == null) return false;
            if (recipe.CopyAttributesFrom == null) return true;
            var outputBlock = recipe.Output?.ResolvedItemStack?.Collectible as Block;
            if (outputBlock?.GetBehavior<BlockBehaviorMaterials>() == null) return true;

            Dictionary<string, string> from = null;
            foreach (var slot in ingredients)
            {
                var stack = slot?.Itemstack;
                if (stack?.Collectible is not Block block || block.GetBehavior<BlockBehaviorMaterials>() == null) continue;
                var types = BlockBehaviorMaterials.TypesOf(stack.Attributes);
                if (types.Count > 0) { from = types; break; }
            }
            if (from == null) return true;

            foreach (var slot in ingredients)
            {
                var stack = slot?.Itemstack;
                if (stack?.Collectible?.Code == null) continue;
                if (TryMaterialOfItem(stack.Collectible.Code, out string kind, out string code, outputBlock.Code?.Domain)
                    && from.TryGetValue(kind, out string wanted) && wanted != code) return false;
            }
            return true;
        }

        /// <summary>Item -> material, per mod (the block's own mod's materials first, then the shared pool). Key "" = shared only.</summary>
        private readonly Dictionary<string, Dictionary<string, (string kind, string code)>> itemIndex = new();

        /// <summary>
        /// Which material an ingredient item IS, from the materials' <c>item</c> (game:plank-pine -> wood, pine); a <c>*</c>
        /// entry's pattern answers unlisted ones. FOR A BLOCK OF <paramref name="domain"/> (2026-10-10): that mod's own
        /// materials first, as drawing and the creative tab already did - two mods may give one code different items
        /// (Glass Display Shelves' metals are rods, Metal Windows' are plates), and the shared pool knew only the first.
        /// </summary>
        public bool TryMaterialOfItem(AssetLocation itemCode, out string kind, out string code, string domain = null)
        {
            kind = code = null;
            if (itemCode == null) return false;
            string scope = domain ?? "";
            string key = itemCode.ToString();
            lock (itemIndex)
            {
                if (!itemIndex.TryGetValue(scope, out var index))
                {
                    index = new Dictionary<string, (string, string)>();
                    foreach (var k in kinds.Keys)
                        foreach (var c in ListFor(k, domain))
                            if (Get(k, c, domain)?.Item is { Length: > 0 } item) index.TryAdd(new AssetLocation(item).ToString(), (k, c));
                    itemIndex[scope] = index;
                }
                if (index.TryGetValue(key, out var found)) { (kind, code) = found; return true; }

                // Not listed: the `*` patterns (the mod's own first), answered ONCE per item code - the crafting grid asks
                // this for every recipe it tests (Calm, 2026-10-09: "the crafting grid has become incredibly laggy").
                string askedKey = scope + "|" + key;
                if (!notListed.TryGetValue(askedKey, out var answer))
                {
                    answer = (null, null);
                    foreach (var k in kinds.Keys)
                    {
                        foreach (var any in StarsFor(k, domain))
                        {
                            if (string.IsNullOrEmpty(any.Item) || !any.Item.Contains("{code}")) continue;
                            var pattern = new AssetLocation(any.Item.Replace("{code}", "*"));
                            var m = new Regex("^" + Regex.Escape(pattern.ToString()).Replace("\\*", "(.+)") + "$").Match(key);
                            if (m.Success) { answer = (k, m.Groups[1].Value); break; }
                        }
                        if (answer.Item1 != null) break;
                    }
                    notListed[askedKey] = answer;
                }
                (kind, code) = answer;
            }
            return kind != null;
        }

        private readonly Dictionary<string, (string kind, string code)> notListed = new();

        /// <summary>
        /// Which material OF ONE KIND an item is - for material recipes, where two kinds can take the same items (Wooden
        /// Display Shelves: framewood and standwood are both planks), so "which kind is this plank" has no single answer.
        /// The kind's listed items first (the block's own mod first), then its <c>*</c> patterns. Answered once per item.
        /// </summary>
        public bool TryMaterialOfItem(AssetLocation itemCode, string kind, string domain, out string code)
        {
            code = null;
            if (itemCode == null || kind == null || !kinds.ContainsKey(kind)) return false;
            string key = (domain ?? "") + "|" + kind + "|" + itemCode;
            lock (itemIndex)
            {
                if (!ofKind.TryGetValue(key, out code))
                {
                    string item = itemCode.ToString();
                    foreach (var c in ListFor(kind, domain))
                        if (Get(kind, c, domain)?.Item is { Length: > 0 } listed && new AssetLocation(listed).ToString() == item) { code = c; break; }
                    if (code == null)
                        foreach (var any in StarsFor(kind, domain))
                        {
                            if (string.IsNullOrEmpty(any.Item) || !any.Item.Contains("{code}")) continue;
                            var pattern = new AssetLocation(any.Item.Replace("{code}", "*"));
                            var m = new Regex("^" + Regex.Escape(pattern.ToString()).Replace("\\*", "(.+)") + "$").Match(item);
                            if (!m.Success) continue;
                            string found = m.Groups[1].Value;
                            // listed codes only, if the `*` entry says so; and never one that cannot be drawn - the textures
                            // must exist (game:supportbeam-* also matches tarnishedmetal-copper, which is no wood)
                            if (any.ListedOnly && !ListFor(kind, domain).Contains(found)) continue;
                            if (api != null && Get(kind, found, domain) is { } def && !TexturesInstalled(api, def)) continue;
                            code = found; break;
                        }
                    ofKind[key] = code;
                }
            }
            return code != null;
        }

        private readonly Dictionary<string, string> ofKind = new();

        /// <summary>A kind's <c>*</c> entries for a block of <paramref name="domain"/>: its own mod's, then the shared one.</summary>
        private IEnumerable<MaterialDefinition> StarsFor(string kind, string domain)
        {
            if (domain != null && byDomain.TryGetValue(domain, out var ownKinds) && ownKinds.TryGetValue(kind, out var own)
                && own.TryGetValue("*", out var ownAny)) yield return ownAny;
            if (kinds.TryGetValue(kind, out var shared) && shared.TryGetValue("*", out var any)) yield return any;
        }

        /// <summary>Whether a recipe ingredient pattern (game:plank-*) is one for this kind's items, for a block of <paramref name="domain"/>.</summary>
        public bool IsItemPatternOf(string kind, AssetLocation pattern, string domain = null) =>
            pattern != null && kinds.ContainsKey(kind) &&
            ListFor(kind, domain).Any(c => Get(kind, c, domain)?.Item is { Length: > 0 } item && WildcardUtil.Match(pattern, new AssetLocation(item)));

        public override void StartClientSide(ICoreClientAPI capi)
        {
            // the handbook and ground-pile patches, one by one by name - never PatchAll (it would also apply the host mod's own
            // [HarmonyPatch] classes again), and the kit's classes carry no [HarmonyPatch] so a host's PatchAll skips them
            clientHarmony = new Harmony(OwnDomain + ".materialskit.client");
            var slideshow = AccessTools.Constructor(typeof(SlideshowGridRecipeTextComponent),
                new[] { typeof(ICoreClientAPI), typeof(GridRecipe[]), typeof(double), typeof(EnumFloat), typeof(Action<ItemStack>), typeof(ItemStack[]) });
            clientHarmony.Patch(slideshow, prefix: new HarmonyMethod(typeof(MaterialsHandbookPatch), nameof(MaterialsHandbookPatch.Prefix)),
                postfix: new HarmonyMethod(typeof(MaterialsHandbookPatch), nameof(MaterialsHandbookPatch.Postfix)));
            clientHarmony.Patch(AccessTools.Method(typeof(CollectibleBehaviorHandbookTextAndExtraInfo), "addCreatedByInfo"),
                prefix: new HarmonyMethod(typeof(CreatedByPagePatch), nameof(CreatedByPagePatch.Prefix)),
                finalizer: new HarmonyMethod(typeof(CreatedByPagePatch), nameof(CreatedByPagePatch.Finalizer)));
            clientHarmony.Patch(AccessTools.Method(typeof(CollectibleObject), nameof(CollectibleObject.Satisfies)),
                postfix: new HarmonyMethod(typeof(MaterialsHandbookOutputPatch), nameof(MaterialsHandbookOutputPatch.Postfix)));
            clientHarmony.Patch(GroundPileMaterialsPatch.TargetMethod(),
                postfix: new HarmonyMethod(typeof(GroundPileMaterialsPatch), nameof(GroundPileMaterialsPatch.Postfix)));
            // "compactHandbook": true - one page per block or item (HandbookGroups, registered in AssetsFinalize)
            HandbookGroups.Apply(capi, OwnDomain + ".handbookgroups", KitSettings.AllDomains ? "*" : OwnDomain);   // + any JSON "handbookGroup" of this mod

            capi.Event.LeaveWorld += () => { lock (modsSectionDone) modsSectionDone.Clear(); lock (cycleCache) cycleCache.Clear(); lock (kindsFor) kindsFor.Clear(); MaterialTextures.Clear(); BlockBehaviorMaterials.ClearMeshes(capi); CollectibleBehaviorMaterials.ClearMeshes(); };
            // Every listed material texture into the block atlas once - textures only - so placed
            // blocks can be built on the tesselation thread without waiting.
            capi.Event.LevelFinalize += () =>
            {
                MaterialTextures.Prepare(capi, AllTextures());
                PrepareOverlays(capi);
            };
        }

        /// <summary>
        /// Every texture that carries an OVERLAY, made into the block atlas at load too (Calm, 2026-10-09 - "anything that could
        /// cause stutter"): the plank pane window darkens EVERY wood and very aged wood dusts the glass, and each of those is a
        /// new combined texture - made on the main thread, one at a time, the first time a window needed it, so new windows
        /// hitched while playing. Each block's texture codes are resolved for every listed material of the kind that fills
        /// them, with and without each material that adds an overlay there - the same Resolve the build uses, so the keys match.
        /// </summary>
        private void PrepareOverlays(ICoreClientAPI capi)
        {
            int made = 0;
            var done = new HashSet<string>();
            var behaviours = new HashSet<string>();
            foreach (var block in capi.World.Blocks)
            {
                var behaviour = block?.GetBehavior<BlockBehaviorMaterials>();
                if (behaviour == null) continue;
                // blocks of one blocktype share their slots / fixed / overlays - each set is worked through once, PER MOD
                // (2026-10-10): the same set in another mod can resolve to that mod's own materials and textures
                string signature = (block.Code?.Domain ?? "") + "@" + string.Join("|", behaviour.Slots.Select(s => s.Key + ":" + string.Join(",", s.Value.Select(r => r.Key + "=" + r.Value))))
                    + "#" + string.Join(",", behaviour.Fixed.Select(f => f.Key + "=" + f.Value))
                    + "#" + string.Join(",", behaviour.Overlays.Select(o => o.Key + "=" + string.Join("+", o.Value ?? Array.Empty<string>())));
                if (!behaviours.Add(signature)) continue;

                foreach (var textureCode in behaviour.TextureCodes)
                {
                    string owner = behaviour.Slots.FirstOrDefault(s => s.Value.ContainsKey(textureCode)).Key;
                    var dusters = kinds.SelectMany(k => k.Value.Values.Where(d => d.Code != "*" && d.Overlays.ContainsKey(textureCode)).Select(d => (kind: k.Key, code: d.Code))).ToList();
                    if (!behaviour.Overlays.ContainsKey(textureCode) && dusters.Count == 0) continue;

                    var ownerCodes = owner != null && kinds.ContainsKey(owner)
                        ? ListFor(owner, block.Code?.Domain)
                        : new List<string> { null };
                    foreach (var ownerCode in ownerCodes)
                    {
                        var plain = new Dictionary<string, string>();
                        if (owner != null && ownerCode != null) plain[owner] = ownerCode;
                        var combinations = new List<Dictionary<string, string>> { plain };
                        foreach (var (kind, code) in dusters)
                            if (kind != owner) combinations.Add(new Dictionary<string, string>(plain) { [kind] = code });
                        foreach (var types in combinations)
                        {
                            var (texture, overlays) = behaviour.Resolve(types, textureCode);
                            if (texture == null || overlays.Count == 0 || !done.Add(MaterialTextures.KeyOf(texture, overlays))) continue;
                            if (MaterialTextures.TryGet(capi, texture, overlays, null, out _)) made++;
                        }
                    }
                }
            }
            if (made > 0) capi.Logger.Event("[Materials Kit] {0} material texture(s) with overlays made at load", made);
        }

        /// <summary>
        /// This mod's files (and with KitSettings.AllDomains every mod's: this one first, then KitSettings.FirstDomains, then
        /// the rest), in path order. Within one mod a later entry with the same code replaces the earlier.
        /// </summary>
        public override void AssetsLoaded(ICoreAPI api)
        {
            var domains = new Dictionary<string, Dictionary<string, Dictionary<string, MaterialDefinition>>>();
            var domainOrder = new List<string>();
            var assets = (KitSettings.AllDomains ? api.Assets.GetMany(Folder) : api.Assets.GetMany(Folder, OwnDomain))
                .OrderBy(a => DomainOrder(a.Location.Domain)).ThenBy(a => a.Location.ToString());
            foreach (var asset in assets)
            {
                string file = asset.Location.Path.Substring(Folder.Length);
                if (!file.EndsWith(".json") || file.Contains('/')) continue;
                string kind = file.Substring(0, file.Length - 5);
                string domain = asset.Location.Domain;
                try
                {
                    if (!domains.TryGetValue(domain, out var ownKinds)) { domains[domain] = ownKinds = new(); domainOrder.Add(domain); }
                    if (!ownKinds.TryGetValue(kind, out var byCode)) ownKinds[kind] = byCode = new Dictionary<string, MaterialDefinition>();
                    foreach (var def in asset.ToObject<MaterialDefinition[]>() ?? Array.Empty<MaterialDefinition>())
                    {
                        if (string.IsNullOrWhiteSpace(def?.Code))
                        {
                            api.Logger.Error("[Materials Kit] A {0} material in {1} has no code - skipped", kind, asset.Location);
                            continue;
                        }
                        def.Textures ??= new Dictionary<string, string>();
                        def.Overlays ??= new Dictionary<string, string[]>();
                        byCode[def.Code] = def;
                    }
                }
                catch (Exception e) { api.Logger.Error("[Materials Kit] Cannot read {0}: {1}", asset.Location, e.Message); }
            }
            // An entry that only LISTS a material (just its code - the vanilla woods and glasses, so the creative
            // tab can be made from the list) takes whatever it leaves out from its own mod's `*` entry for the kind...
            foreach (var ownKinds in domains.Values)
                foreach (var byCode in ownKinds.Values)
                    if (byCode.TryGetValue("*", out var any)) FillFrom(any, byCode.Values);

            // ...the shared pool: the first definition of each code, in load order (with one domain, simply this mod's)
            var loaded = new Dictionary<string, Dictionary<string, MaterialDefinition>>();
            var definedBy = new Dictionary<(string kind, string code), string>();
            foreach (var domain in domainOrder)
                foreach (var (kind, byCode) in domains[domain])
                {
                    if (!loaded.TryGetValue(kind, out var shared)) loaded[kind] = shared = new Dictionary<string, MaterialDefinition>();
                    foreach (var def in byCode.Values)
                    {
                        if (!shared.TryGetValue(def.Code, out var first))
                        {
                            shared[def.Code] = def;
                            definedBy[(kind, def.Code)] = domain;
                        }
                        else if (def.Code != "*" && !SameDefinition(first, def))
                            api.Logger.Warning("[Materials Kit] {0} '{1}' is defined differently by {2} and {3} - each mod's blocks use their own; others use {2}'s",
                                kind, def.Code, definedBy[(kind, def.Code)], domain);
                    }
                }

            // ...and anything still missing from the shared `*` (a mod that lists codes without a `*` of its own)
            foreach (var (kind, shared) in loaded)
                if (shared.TryGetValue("*", out var any))
                    foreach (var ownKinds in domains.Values)
                        if (ownKinds.TryGetValue(kind, out var byCode)) FillFrom(any, byCode.Values);

            kinds = loaded;
            byDomain = domains;
            lock (itemIndex) { itemIndex.Clear(); notListed.Clear(); ofKind.Clear(); }
            lock (cycleCache) cycleCache.Clear();
            api.Logger.Event("[Materials Kit] Materials: {0}", string.Join(", ", kinds.Select(k => k.Value.Count + " " + k.Key)));

            if (api.Side == EnumAppSide.Server) MakeRecipes(api);
        }


        // ---------- the cycle: which materials a handbook recipe or a creative icon flips through ----------

        /// <summary>
        /// Which mod a material comes from: the domain of its <c>item</c> (vanillavarnished:plank-bleached-walnut -> vanillavarnished;
        /// game:rod-pewter -> game, as the Pewter mod puts its rods there). A material with no item counts as the game's.
        /// </summary>
        public string SourceOf(string kind, string code, string domain)
        {
            var def = Get(kind, code, domain);
            if (!string.IsNullOrEmpty(def?.Source)) return def.Source;
            return string.IsNullOrEmpty(def?.Item) ? "game" : new AssetLocation(def.Item).Domain;
        }

        /// <summary>The most material sets a handbook page flips through - its recipe copies and its drop alike.</summary>
        /// <remarks>1: no cycling - the recipe shows the page's own materials and the drop is the page's own item (Calm, 2026-10-14:
        /// "Drop the cycling, just show the page's own pair" - the materials list says what can be used). Raise it to cycle again
        /// (it was 1024, then 64 after the handbook lagged).</remarks>
        public const int MaxHandbookCycle = 1;

        /// <summary>The material recipe (MaterialRecipes) that makes this block or item, or null.</summary>
        public GridRecipe MaterialRecipeFor(IWorldAccessor world, CollectibleObject collectible) =>
            collectible == null ? null : world?.GridRecipes?.FirstOrDefault(r =>
                r?.Output?.ResolvedItemStack?.Collectible == collectible && MaterialRecipes.LettersOf(r).Count > 0);

        /// <summary>
        /// The materials to flip through, from <paramref name="start"/> (a stack's own): that first, then - when it MIXES them
        /// (oak shelves on a walnut frame) - every other mixed combination, the first kind held while the last runs through the
        /// others (an odometer); else - one material throughout (oak on oak) - every material in step, the same in every kind
        /// that has it. Only materials whose item the game has, so modded ones come and go with their mods. Shared by the
        /// handbook recipe copies and the creative icons, so the two always agree. Empty if a kind has nothing.
        /// </summary>
        public List<Dictionary<string, string>> CycleCombos(IWorldAccessor world, IList<string> kinds, string domain, IReadOnlyDictionary<string, string> start, int max)
        {
            if (kinds == null || kinds.Count == 0 || max <= 0) return new List<Dictionary<string, string>>();
            // worked out once per question - the handbook asks this for every block on every page open (2026-10-14 lag)
            string cacheKey = (domain ?? "") + "|" + string.Join(",", kinds) + "|" + BlockBehaviorMaterials.KeyOf(start) + "|" + max;
            lock (cycleCache)
            {
                if (cycleCache.TryGetValue(cacheKey, out var cached)) return cached;
            }
            var result = CycleCombosUncached(world, kinds, domain, start, max);
            // only keep it once the world has its items (the recipes and items reach the client late)
            if (result.Count > 0) lock (cycleCache) cycleCache[cacheKey] = result;
            return result;
        }

        private readonly Dictionary<string, List<Dictionary<string, string>>> cycleCache = new();

        private List<Dictionary<string, string>> CycleCombosUncached(IWorldAccessor world, IList<string> kinds, string domain, IReadOnlyDictionary<string, string> start, int max)
        {
            var combos = new List<Dictionary<string, string>>();
            var codes = new Dictionary<string, List<string>>();
            foreach (var kind in kinds)
            {
                var list = new List<string>();
                foreach (var code in ListFor(kind, domain))
                {
                    if (Get(kind, code, domain)?.Item is not { Length: > 0 } item) continue;
                    var location = new AssetLocation(item);
                    if (world?.GetItem(location) != null || world?.GetBlock(location) != null) list.Add(code);
                }
                if (list.Count == 0) return combos;
                codes[kind] = list;
            }

            var startCombo = start != null && kinds.All(k => start.ContainsKey(k)) ? kinds.ToDictionary(k => k, k => start[k]) : null;
            // EACH SOURCE ITS OWN CYCLE (Calm, 2026-10-14: "Vanilla shows vanilla and vanilla varnished shows its own, so you
            // know the added mod is working"): per kind, only the materials from the same mod as the start's (SourceOf - the
            // domain of the material's item)
            if (startCombo != null)
                foreach (var kind in kinds)
                {
                    string source = SourceOf(kind, startCombo[kind], domain);
                    var own = codes[kind].Where(c => SourceOf(kind, c, domain) == source).ToList();
                    if (own.Count > 0) codes[kind] = own;
                }
            bool mixed = startCombo != null && startCombo.Values.Distinct().Count() > 1;
            var seen = new HashSet<string>();
            void Add(Dictionary<string, string> combo)
            {
                if (combos.Count < max && seen.Add(BlockBehaviorMaterials.KeyOf(combo))) combos.Add(combo);
            }
            if (startCombo != null) Add(startCombo);

            if (mixed && kinds.Count > 1)
            {
                var at = kinds.Select(k => Math.Max(0, codes[k].IndexOf(startCombo[k]))).ToArray();
                long total = kinds.Aggregate(1L, (n, k) => n * codes[k].Count);
                for (long step = 0; step < total && combos.Count < max; step++)
                {
                    var combo = new Dictionary<string, string>();
                    for (int k = 0; k < kinds.Count; k++) combo[kinds[k]] = codes[kinds[k]][at[k]];
                    if (combo.Values.Distinct().Count() > 1) Add(combo);
                    for (int k = kinds.Count - 1; k >= 0; k--)
                    {
                        at[k] = (at[k] + 1) % codes[kinds[k]].Count;
                        if (at[k] != 0) break;
                    }
                }
            }
            else
            {
                var all = codes[kinds[0]].Concat(codes.Values.SelectMany(l => l)).Distinct().ToList();
                int from = startCombo != null ? Math.Max(0, all.IndexOf(startCombo[kinds[0]])) : 0;
                for (int n = 0; n < all.Count && combos.Count < max; n++)
                {
                    int i = (from + n) % all.Count;
                    var combo = new Dictionary<string, string>();
                    foreach (var kind in kinds) combo[kind] = codes[kind].Contains(all[i]) ? all[i] : codes[kind][i % codes[kind].Count];
                    Add(combo);
                }
            }
            return combos;
        }

        /// <summary>
        /// The kinds a block's cycle runs over, in order: the kinds of its material recipe (the order the recipe lists them -
        /// Metal Display Shelves puts the metal first, so a metal holds while the woods run), else its the kit's materials behaviour slots.
        /// </summary>
        public IList<string> KindsFor(IWorldAccessor world, Block block)
        {
            if (block == null) return Array.Empty<string>();
            lock (kindsFor)
            {
                if (kindsFor.TryGetValue(block.Id, out var known)) return known;
                var recipe = world?.GridRecipes?.FirstOrDefault(r => r?.Output?.ResolvedItemStack?.Collectible == block && MaterialRecipes.LettersOf(r).Count > 0);
                IList<string> kinds = recipe != null
                    ? MaterialRecipes.LettersOf(recipe).Values.Distinct().ToList()
                    : (block.GetBehavior<BlockBehaviorMaterials>()?.Slots.Keys.ToList() ?? new List<string>());
                // only cache once the recipes are there (they reach the client late)
                if (recipe != null || (world?.GridRecipes?.Count ?? 0) > 0) kindsFor[block.Id] = kinds;
                return kinds;
            }
        }

        private readonly Dictionary<int, IList<string>> kindsFor = new();

        // ---------- the handbook's "Can be made with" line ----------

        /// <summary>Collectibles whose line was already worked out (added, or found empty).</summary>
        private readonly HashSet<CollectibleObject> modsSectionDone = new();

        /// <summary>
        /// "handbookMods": true - one section on the page, after Created by: the mods whose materials the block or item can be
        /// made with, by their own names ("Vanilla, Vanilla Varnished"). Only INSTALLED materials count (textures there, and
        /// the item for a mod's), so it is never wrong for this player. One line when every kind takes the same mods, else a
        /// line per kind ("Frame: ..."). Worked out once per collectible - the section is shared by all its pages.
        /// </summary>
        internal void AddModsSection(ICoreClientAPI capi, CollectibleBehaviorHandbookTextAndExtraInfo handbook, CollectibleObject collectible)
        {
            if (capi == null || handbook == null || collectible == null) return;
            bool wanted = (collectible as Block)?.GetBehavior<BlockBehaviorMaterials>()?.HandbookMods == true
                || (collectible is Item && collectible.GetCollectibleBehavior<CollectibleBehaviorMaterials>(true)?.HandbookMods == true);
            if (!wanted) return;
            lock (modsSectionDone)
            {
                if (!modsSectionDone.Add(collectible)) return;
                string text = ModsText(capi, collectible);
                if (string.IsNullOrEmpty(text)) return;
                string domain = collectible.Code?.Domain;
                var section = new ExtraHandbookSection { Title = HandbookLangKey(domain, "handbook-mods-title") ?? "Can be made with", Text = text };
                handbook.ExtraHandBookSections = (handbook.ExtraHandBookSections ?? Array.Empty<ExtraHandbookSection>()).Append(section).ToArray();
            }
        }

        private string ModsText(ICoreClientAPI capi, CollectibleObject collectible)
        {
            string domain = collectible.Code?.Domain;
            var recipe = MaterialRecipeFor(capi.World, collectible);
            // Made by grid recipes that pick no material - stacked from blocks that already carry theirs (tall windows,
            // Calm 2026-10-15: "it doesnt need to say what is used as they are stacked"): no line, theirs says it.
            if (recipe == null && capi.World.GridRecipes?.Any(r => r?.Output?.ResolvedItemStack?.Collectible == collectible) == true) return null;
            var slots = ((collectible as Block)?.GetBehavior<BlockBehaviorMaterials>()?.Slots.Keys
                         ?? collectible.GetCollectibleBehavior<CollectibleBehaviorMaterials>(true)?.Slots.Keys)?.ToList() ?? new List<string>();
            // every kind the block holds, not only the recipe's letters - a window's glass comes in on its pane, and a mod
            // may add glass too (Calm, 2026-10-15)
            var kinds = recipe != null ? slots.Concat(MaterialRecipes.LettersOf(recipe).Values).Distinct().ToList() : slots;
            if (kinds.Count == 0) return null;
            var lines = kinds.Select(kind => (kind, mods: InstalledSources(capi, kind, domain))).Where(l => l.mods.Count > 0).ToList();
            if (lines.Count == 0) return null;
            if (lines.All(l => l.mods.SequenceEqual(lines[0].mods))) return string.Join(", ", lines[0].mods);
            return string.Join("\n", lines.Select(l => KindName(l.kind, domain) + ": " + string.Join(", ", l.mods)));
        }

        /// <summary>The names of the mods whose materials of this kind are installed, the game's first ("Vanilla").</summary>
        private List<string> InstalledSources(ICoreClientAPI capi, string kind, string domain)
        {
            var sources = new List<string>();
            foreach (var code in ListFor(kind, domain))
            {
                if (Get(kind, code, domain) is not { } def || !TexturesInstalled(capi, def)) continue;
                if (!string.IsNullOrEmpty(def.Item))
                {
                    var item = new AssetLocation(def.Item);
                    if (capi.World.GetItem(item) == null && capi.World.GetBlock(item) == null) continue;
                }
                string source = SourceOf(kind, code, domain);
                if (!sources.Contains(source)) sources.Add(source);
            }
            return sources.OrderBy(s => s == "game" ? 0 : 1).Select(s => s == "game"
                ? (HandbookLangKey(domain, "handbook-source-game") is { } key ? Lang.Get(key) : "Vanilla")
                : capi.ModLoader.GetMod(s)?.Info?.Name ?? s).ToList();
        }

        /// <summary>"Frame", "Shelves"...: lang <c>materialkind-name-&lt;kind&gt;</c> (the block's mod, then KitSettings.LangDomain), else the kind with a capital.</summary>
        private static string KindName(string kind, string domain) =>
            HandbookLangKey(domain, "materialkind-name-" + kind) is { } key ? Lang.Get(key) : char.ToUpperInvariant(kind[0]) + kind.Substring(1);

        private static string HandbookLangKey(string domain, string key)
        {
            if (domain != null && Lang.HasTranslation(domain + ":" + key)) return domain + ":" + key;
            return KitSettings.LangDomain != null && Lang.HasTranslation(KitSettings.LangDomain + ":" + key) ? KitSettings.LangDomain + ":" + key : null;
        }

        // ---------- creative tab and handbook entries made from the materials ----------

        /// <summary>
        /// THE CREATIVE TAB AND THE HANDBOOK ARE MADE FROM THE MATERIALS. A blocktype writes ONE stack per creative list (which
        /// tabs, which kinds of material it takes); the kit makes the rest from the materials files, in the files' order. What
        /// is made is the block's choice (ShowRule, from its <c>creative</c> / <c>handbook</c> / <c>showMaterials</c>):
        /// the GAME's materials ("vanilla") and the other mods' ("modded", SourceOf) separately - every combination, each once,
        /// one per mod, or none. Stacks the handbook should show but the creative tab should not go into a HIDDEN list (no tabs)
        /// - the game gives those handbook pages too (and with compactHandbook they join the block's one cycling page).
        /// Crafting is never affected: a recipe takes every material whatever is shown. Materials whose textures (or, for a
        /// mod's, whose item) are not installed are left out. A list that already has no tabs stays hidden, but is filled the
        /// same way, so a wrench-only style still has its handbook pages.
        /// </summary>
        public override void AssetsFinalize(ICoreAPI api)
        {
            int made = 0;
            foreach (var block in api.World.Collectibles)
            {
                // one block's trouble must not stop the rest (2026-10-15: a crash here left every window without its stacks)
                try { made += MakeStacks(api, block); }
                catch (Exception e) { api.Logger.Error("[Materials Kit] Could not make the creative / handbook stacks of {0}: {1}", block?.Code, e); }
            }
            if (made > 0) api.Logger.Event("[Materials Kit] {0} creative / handbook stack(s) made from the materials", made);
        }

        /// <summary>One block's or item's creative and handbook stacks (AssetsFinalize). Returns how many were added.</summary>
        private int MakeStacks(ICoreAPI api, CollectibleObject block)
        {
            int made = 0;
            {
                var behaviour = (block as Block)?.GetBehavior<BlockBehaviorMaterials>();
                var itemBehaviour = block is Item ? block.GetCollectibleBehavior<CollectibleBehaviorMaterials>(true) : null;
                // "compactHandbook": one handbook page for the block or item (HandbookGroups)
                if (behaviour?.CompactHandbook == true) HandbookGroups.Register(block);
                else if (itemBehaviour?.CompactHandbook == true)
                {
                    string titleKey = itemBehaviour.HandbookTitle;
                    HandbookGroups.Register(block, title: titleKey == null ? null : _ => Lang.Get(titleKey));
                }
                // an item only when it asks (its creative list may be hand-made)
                if ((behaviour == null && itemBehaviour?.ShowSet != true) || block.CreativeInventoryStacks == null) return 0;
                // the block's settings, else the item's - never "block's ?? item's": a block with no showMaterials (null) has no
                // item behaviour to fall back to (2026-10-15: that crashed the whole creative phase on the first window)
                var creative = behaviour != null ? behaviour.CreativeShow : itemBehaviour.CreativeShow;
                var handbook = behaviour != null ? behaviour.HandbookShow : itemBehaviour.HandbookShow;
                var only = behaviour != null ? behaviour.ShowMaterials : itemBehaviour.ShowMaterials;
                var slots = behaviour != null ? behaviour.Slots : itemBehaviour.Slots;

                var hidden = new List<JsonItemStack>();
                foreach (var list in block.CreativeInventoryStacks)
                {
                    if (list?.Stacks == null || list.Stacks.Length == 0) continue;
                    var template = list.Stacks[0];
                    if (template.Attributes?.Token?["types"] is not JObject) continue;
                    var written = list.Stacks.Select(js => TypesOfJson(js)).Where(t => t.Count > 0).ToList();
                    var axes = AxesFor(api, block, slots, written, only);
                    if (axes.Count == 0) continue;

                    var shown = Combos(axes, written, creative);
                    var keys = new HashSet<string>(shown.Select(BlockBehaviorMaterials.KeyOf));
                    var extra = Combos(axes, written, handbook).Where(c => !keys.Contains(BlockBehaviorMaterials.KeyOf(c))).ToList();

                    var stacks = Stacks(api, template, shown);
                    made += stacks.Count - list.Stacks.Length;
                    list.Stacks = stacks.ToArray();
                    var more = Stacks(api, template, extra);
                    made += more.Count;
                    hidden.AddRange(more);
                }
                if (hidden.Count > 0)
                    block.CreativeInventoryStacks = block.CreativeInventoryStacks
                        .Append(new CreativeTabAndStackList { Tabs = Array.Empty<string>(), Stacks = hidden.ToArray() }).ToArray();
            }
            return made;
        }

        /// <summary>
        /// One kind's materials for a list: the game's and each mod's, in file order. A kind with a <c>showMaterials</c> list
        /// shows EXACTLY what it names - material codes, <c>"vanilla"</c> (every game material) or a mod id (every material
        /// from that mod) - all in <see cref="Vanilla"/>, so the vanilla rule combines them and the modded rule leaves the
        /// kind alone.
        /// </summary>
        private sealed class Axis
        {
            public string Kind;
            public List<string> Vanilla = new();
            /// <summary>source (mod) -> its materials of this kind</summary>
            public Dictionary<string, List<string>> Modded = new();
            public IEnumerable<string> AllModded => Modded.Values.SelectMany(l => l);
        }

        private List<Axis> AxesFor(ICoreAPI api, CollectibleObject block, Dictionary<string, Dictionary<string, string>> slots,
            List<Dictionary<string, string>> written, IReadOnlyDictionary<string, string[]> only)
        {
            string domain = block.Code?.Domain;
            var axes = new List<Axis>();
            foreach (var kind in slots.Keys)
            {
                if (!written.Any(w => w.ContainsKey(kind))) continue;
                var axis = new Axis { Kind = kind };
                var allowed = only != null && only.TryGetValue(kind, out var list) && list?.Length > 0
                    ? new HashSet<string>(list.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim().ToLowerInvariant())) : null;
                foreach (var code in ListFor(kind, domain))
                {
                    if (Get(kind, code, domain) is not { } def || !TexturesInstalled(api, def)) continue;
                    string source = SourceOf(kind, code, domain);
                    if (allowed != null && !allowed.Contains(code.ToLowerInvariant()) && !allowed.Contains(source == "game" ? "vanilla" : source.ToLowerInvariant())) continue;
                    if (source != "game" && !string.IsNullOrEmpty(def.Item))
                    {
                        var item = new AssetLocation(def.Item);
                        if (api.World.GetItem(item) == null && api.World.GetBlock(item) == null) continue;   // that mod is not installed
                    }
                    if (source == "game" || allowed != null) { axis.Vanilla.Add(code); continue; }
                    if (!axis.Modded.TryGetValue(source, out var codes)) axis.Modded[source] = codes = new List<string>();
                    codes.Add(code);
                }
                // a material a written stack names that no file lists still shows (as written) - with a list only if the list
                // gave nothing installed, so the block never drops out of the creative tab
                if (allowed == null || axis.Vanilla.Count == 0)
                    foreach (var w in written)
                        if (w.TryGetValue(kind, out var code) && !axis.Vanilla.Contains(code) && !axis.AllModded.Contains(code)) axis.Vanilla.Add(code);
                axes.Add(axis);
            }
            return axes;
        }

        /// <summary>The material sets a rule shows, written stacks first, no set twice.</summary>
        private static List<Dictionary<string, string>> Combos(List<Axis> axes, List<Dictionary<string, string>> written, ShowRule rule)
        {
            var result = new List<Dictionary<string, string>>();
            var seen = new HashSet<string>();
            void Add(Dictionary<string, string> combo) { if (seen.Add(BlockBehaviorMaterials.KeyOf(combo))) result.Add(combo); }
            // a written stack's materials, each swapped for the kind's first shown one if it is not shown (showMaterials)
            Dictionary<string, string> Partner(Dictionary<string, string> w) =>
                axes.ToDictionary(a => a.Kind, a => w.TryGetValue(a.Kind, out var c) && (a.Vanilla.Contains(c) || a.AllModded.Contains(c))
                    ? c : a.Vanilla.FirstOrDefault() ?? a.AllModded.FirstOrDefault());
            List<Dictionary<string, string>> Product(System.Func<Axis, IEnumerable<string>> codesOf)
            {
                var combos = new List<Dictionary<string, string>> { new() };
                foreach (var axis in axes)
                    combos = combos.SelectMany(c => codesOf(axis).Select(code => new Dictionary<string, string>(c) { [axis.Kind] = code })).ToList();
                return combos;
            }

            // the game's materials
            foreach (var w in written) Add(Partner(w));
            if (rule.Vanilla == "all") foreach (var combo in Product(a => a.Vanilla)) Add(combo);
            else if (rule.Vanilla == "each" && written.Count > 0)
                foreach (var axis in axes)
                    foreach (var code in axis.Vanilla) Add(new Dictionary<string, string>(Partner(written[0])) { [axis.Kind] = code });

            // the other mods' materials
            switch (rule.Modded)
            {
                case "all":
                    foreach (var combo in Product(a => a.Vanilla.Concat(a.AllModded))) Add(combo);
                    break;
                case "each":
                    // once each, on each written stack: in its kind, and in every other kind the stack gives the same material
                    foreach (var w in written.Select(Partner))
                        foreach (var axis in axes)
                            foreach (var code in axis.AllModded)
                            {
                                var combo = new Dictionary<string, string>(w);
                                foreach (var other in axes)
                                    if (other == axis || (w[other.Kind] == w[axis.Kind] && other.AllModded.Contains(code))) combo[other.Kind] = code;
                                Add(combo);
                            }
                    break;
                case "one":
                    // one per mod, on each written stack: a matching stack stays matching where the mod has a shared material
                    foreach (var source in axes.SelectMany(a => a.Modded.Keys).Distinct().ToList())
                        foreach (var w in written.Select(Partner))
                        {
                            bool matching = w.Values.Distinct().Count() <= 1;
                            var lists = axes.Where(a => a.Modded.ContainsKey(source)).ToDictionary(a => a.Kind, a => a.Modded[source]);
                            string shared = matching ? lists.Values.Aggregate((IEnumerable<string>)null, (acc, l) => acc == null ? l : acc.Intersect(l))?.FirstOrDefault() : null;
                            var combo = new Dictionary<string, string>(w);
                            int index = 0;
                            foreach (var (kind, codes) in lists) combo[kind] = shared ?? codes[Math.Min(matching ? 0 : index++, codes.Count - 1)];
                            if (lists.Count > 0) Add(combo);
                        }
                    break;
            }
            return result;
        }

        private static Dictionary<string, string> TypesOfJson(JsonItemStack js)
        {
            var result = new Dictionary<string, string>();
            if (js?.Attributes?.Token?["types"] is JObject types)
                foreach (var p in types.Properties()) if ((string)p.Value is { Length: > 0 } v) result[p.Name] = v;
            return result;
        }

        private static List<JsonItemStack> Stacks(ICoreAPI api, JsonItemStack template, List<Dictionary<string, string>> combos)
        {
            var stacks = new List<JsonItemStack>();
            foreach (var combo in combos)
            {
                var copy = template.Clone();
                var token = copy.Attributes.Token.DeepClone();
                token["types"] = new JObject(combo.Select(kv => new JProperty(kv.Key, kv.Value)));
                copy.Attributes = new JsonObject(token);
                if (copy.Resolve(api.World, "materials kit", false)) stacks.Add(copy);
            }
            return stacks;
        }

        private static bool TexturesInstalled(ICoreAPI api, MaterialDefinition def) =>
            def.Textures.Values.All(t => string.IsNullOrEmpty(t) || api.Assets.Exists(new AssetLocation(t).WithPathPrefixOnce("textures/").WithPathAppendixOnce(".png")));

        // ---------- recipes made from the materials ----------

        /// <summary>
        /// A NEW MATERIAL NEEDS NO RECIPE FILE (Calm, 2026-10-08: "easy to add in new woods"). A recipe is
        /// a TEMPLATE when its output carries <c>types: { "&lt;kind&gt;": "{&lt;name&gt;}" }</c> and an ingredient
        /// carries that <c>name</c> - the game's own placeholder recipes, e.g. a window from any
        /// <c>game:plank-*</c>. For every material of that kind whose <c>item</c> fits the ingredient's
        /// pattern but lives in ANOTHER domain (<c>bdtree:plank-ash</c> against <c>game:plank-*</c>), the
        /// template is copied with that domain's ingredient and <c>allowedVariants</c> = those materials'
        /// codes - one copy per domain, so a wood mod's whole range is one recipe per window.
        ///
        /// The copies are written into the template's own recipe asset, in memory, before the game reads
        /// recipes (vssurvivalmod RecipeLoader, recipes/grid, server, ExecuteOrder 1; this runs at 0.1,
        /// after JsonPatchLoader 0.05 - so materials added by patches count). The pattern match keeps
        /// ingredients that are not the material's item out of it (the leaded-pane routes name the wood
        /// on a glass pane, not a plank). The game fills {name} from the wildcard, so a copy is made only
        /// where the item's wildcard part IS the material's code.
        /// </summary>
        private void MakeRecipes(ICoreAPI api)
        {
            // ONLY recipes for blocks that use the kit's materials behaviour (found 2026-10-08: the old Window Storage mod's
            // recipes have the same "any plank -> {wood}" shape and were being copied for bdtree too).
            // Blocks are not built yet at this point, so their blocktype files are read for the behaviour.
            var ours = BlockCodesUsingMaterials(api);
            if (ours.Count == 0) return;
            int made = 0, files = 0;
            foreach (var asset in api.Assets.GetMany("recipes/grid/"))
            {
                if (!asset.Location.Path.EndsWith(".json")) continue;
                JToken root;
                try { root = JToken.Parse(Encoding.UTF8.GetString(asset.Data).TrimStart('\uFEFF')); }
                catch { continue; }
                var recipes = root as JArray;
                var single = root as JObject;
                if (recipes == null && single == null) continue;

                var copies = new List<JObject>();
                foreach (var recipe in recipes?.OfType<JObject>() ?? new[] { single })
                    if (MakesOurBlock(recipe, asset.Location.Domain, ours)) copies.AddRange(CopiesFor(recipe, OutputDomain(recipe, asset.Location.Domain)));
                if (copies.Count == 0) continue;

                var all = recipes ?? new JArray(single);
                foreach (var copy in copies) all.Add(copy);
                asset.Data = Encoding.UTF8.GetBytes(all.ToString(Formatting.None));
                made += copies.Count;
                files++;
            }
            if (made > 0) api.Logger.Event("[Materials Kit] {0} recipe(s) made from the materials, in {1} recipe file(s)", made, files);
        }

        /// <summary>The load order of a mod's materials files: this mod, then KitSettings.FirstDomains, then the rest.</summary>
        private int DomainOrder(string domain)
        {
            if (domain == OwnDomain) return 0;
            int i = Array.IndexOf(KitSettings.FirstDomains, domain);
            return i >= 0 ? 1 + i : 1 + KitSettings.FirstDomains.Length;
        }

        /// <summary>domain + blocktype code of every blocktype file of this mod (every mod with AllDomains) that declares the kit's behaviour.</summary>
        private HashSet<string> BlockCodesUsingMaterials(ICoreAPI api)
        {
            var codes = new HashSet<string>();
            foreach (var asset in KitSettings.AllDomains ? api.Assets.GetMany("blocktypes/") : api.Assets.GetMany("blocktypes/", OwnDomain))
            {
                string text = Encoding.UTF8.GetString(asset.Data);
                if (!text.Contains(KitSettings.BehaviorName)) continue;
                try
                {
                    if (JToken.Parse(text.TrimStart('\uFEFF')) is not JObject block) continue;
                    if (block["behaviors"] is not JArray behaviors || !behaviors.Any(b => (string)b?["name"] == KitSettings.BehaviorName)) continue;
                    if ((string)block["code"] is { } code) codes.Add(asset.Location.Domain + ":" + code);
                }
                catch { }
            }
            return codes;
        }

        /// <summary>The mod of the recipe's output block (a code with no domain is in the recipe file's).</summary>
        private static string OutputDomain(JObject recipe, string fileDomain)
        {
            string code = (string)recipe["output"]?["code"];
            return code != null && code.Contains(':') ? new AssetLocation(code).Domain : fileDomain;
        }

        /// <summary>Whether the recipe's output is one of those blocks (a code with no domain is in the recipe file's).</summary>
        private static bool MakesOurBlock(JObject recipe, string fileDomain, HashSet<string> ours)
        {
            if ((string)recipe["output"]?["code"] is not { } code) return false;
            var location = code.Contains(':') ? new AssetLocation(code) : new AssetLocation(fileDomain, code);
            return ours.Any(o =>
            {
                int colon = o.IndexOf(':');
                string domain = o.Substring(0, colon), baseCode = o.Substring(colon + 1);
                return location.Domain == domain && (location.Path == baseCode || location.Path.StartsWith(baseCode + "-"));
            });
        }

        /// <summary>The copies of a template recipe, from the materials of its output block's mod (<paramref name="domain"/>) - its own first.</summary>
        private List<JObject> CopiesFor(JObject recipe, string domain)
        {
            var result = new List<JObject>();
            if (recipe["ingredients"] is not JObject ingredients) return result;

            // (kind, ingredient name) pairs: the output's {placeholders}; and, for a recipe that copies its materials
            // from a block in the grid (the Display Unit Window, the vanilla pane shelf - Calm 2026-10-09: planks of
            // the window's wood), an ingredient NAMED after a kind ("name": "wood") - so a wood mod gets a copy taking
            // its own planks, and SameMaterialsInGrid holds them to the block's wood.
            var pairs = new List<(string kind, string name)>();
            if (recipe["output"]?["attributes"]?["types"] is JObject types)
                foreach (var typeProp in types.Properties())
                    if ((string)typeProp.Value is { } value && value.StartsWith("{") && value.EndsWith("}"))
                        pairs.Add((typeProp.Name, value.Substring(1, value.Length - 2)));
            if (recipe["copyAttributesFrom"] != null)
                foreach (var p in ingredients.Properties())
                    if ((string)p.Value?["name"] is { } name && kinds.ContainsKey(name) && !pairs.Any(x => x.name == name))
                        pairs.Add((name, name));

            var current = new List<JObject> { recipe };
            foreach (var (kind, placeholder) in pairs)
            {
                if (!kinds.ContainsKey(kind)) continue;
                var materials = ListFor(kind, domain).Select(c => Get(kind, c, domain)).Where(d => d != null).ToList();
                var ingredient = ingredients.Properties().FirstOrDefault(p => (string)p.Value?["name"] == placeholder);
                if (ingredient == null || (string)ingredient.Value["code"] is not { } ingredientCode) continue;

                var pattern = new AssetLocation(ingredientCode);
                if (pattern.Path.Count(c => c == '*') != 1) continue;
                var match = new Regex("^" + Regex.Escape(pattern.Path).Replace("\\*", "(.+)") + "$");

                var byDomain = new SortedDictionary<string, List<string>>();
                foreach (var def in materials)
                {
                    if (def.Code == "*" || string.IsNullOrEmpty(def.Item)) continue;
                    var item = new AssetLocation(def.Item);
                    if (item.Domain == pattern.Domain) continue;   // the template already covers its own domain
                    var m = match.Match(item.Path);
                    if (!m.Success || m.Groups[1].Value != def.Code) continue;
                    if (!byDomain.TryGetValue(item.Domain, out var codes)) byDomain[item.Domain] = codes = new List<string>();
                    codes.Add(def.Code);
                }
                if (byDomain.Count == 0) continue;

                var added = new List<JObject>();
                foreach (var source in current)
                    foreach (var (itemDomain, codes) in byDomain)
                    {
                        var copy = (JObject)source.DeepClone();
                        var ing = (JObject)copy["ingredients"][ingredient.Name];
                        ing["code"] = itemDomain + ":" + pattern.Path;
                        ing["allowedVariants"] = new JArray(codes);
                        ing.Remove("skipVariants");
                        added.Add(copy);
                    }
                current.AddRange(added);
                result.AddRange(added);
            }
            return result;
        }

        /// <summary>
        /// The material of this kind called this code, for a block of <paramref name="domain"/>: that mod's own entry, else the
        /// shared one, else that mod's own <c>*</c>, else the shared <c>*</c>. Null if none. No domain = the shared pool only.
        /// </summary>
        public MaterialDefinition Get(string kind, string code, string domain = null)
        {
            if (string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(code)) return null;
            Dictionary<string, MaterialDefinition> own = null;
            if (domain != null && byDomain.TryGetValue(domain, out var ownKinds)) ownKinds.TryGetValue(kind, out own);
            kinds.TryGetValue(kind, out var shared);

            if (own != null && code != "*" && own.TryGetValue(code, out var mine)) return mine;
            if (shared != null && shared.TryGetValue(code, out var def)) return def;
            if (own != null && own.TryGetValue("*", out var ownAny)) return ownAny.For(code);
            return shared != null && shared.TryGetValue("*", out var any) ? any.For(code) : null;
        }

        /// <summary>The listed codes of a kind for a block of <paramref name="domain"/>: its own mod's files' order first, then the rest of the shared pool.</summary>
        public List<string> ListFor(string kind, string domain)
        {
            var codes = new List<string>();
            if (domain != null && byDomain.TryGetValue(domain, out var ownKinds) && ownKinds.TryGetValue(kind, out var own))
                codes.AddRange(own.Keys.Where(c => c != "*"));
            if (kinds.TryGetValue(kind, out var shared))
                foreach (var c in shared.Keys) if (c != "*" && !codes.Contains(c)) codes.Add(c);
            return codes;
        }

        private static void FillFrom(MaterialDefinition any, IEnumerable<MaterialDefinition> defs)
        {
            foreach (var def in defs)
            {
                if (def == any || def.Code == "*") continue;
                var filled = any.For(def.Code);
                def.Name ??= filled.Name;
                def.Item ??= filled.Item;
                foreach (var kv in filled.Textures) if (!def.Textures.ContainsKey(kv.Key)) def.Textures[kv.Key] = kv.Value;
            }
        }

        /// <summary>Whether two mods' entries for one code say the same thing (then there is nothing to warn about).</summary>
        private static bool SameDefinition(MaterialDefinition a, MaterialDefinition b) =>
            a.Name == b.Name && a.Item == b.Item
            && a.Textures.Count == b.Textures.Count && a.Textures.All(kv => b.Textures.TryGetValue(kv.Key, out var v) && v == kv.Value)
            && a.Overlays.Count == b.Overlays.Count && a.Overlays.All(kv => b.Overlays.TryGetValue(kv.Key, out var v) && (v ?? Array.Empty<string>()).SequenceEqual(kv.Value ?? Array.Empty<string>()));

        /// <summary>Every texture the listed (not <c>*</c>) materials name - for loading them into the atlas up front.</summary>
        private IEnumerable<string> AllTextures() =>
            kinds.Values.SelectMany(k => k.Values)
                .Concat(byDomain.Values.SelectMany(d => d.Values).SelectMany(k => k.Values))
                .Where(d => d.Code != "*").SelectMany(d => d.Textures.Values).Where(t => !string.IsNullOrEmpty(t)).Distinct();
    }
}

