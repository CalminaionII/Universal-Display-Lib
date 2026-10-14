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

namespace UniversalDisplayLib.Materials
{
    /// <summary>
    /// One material from <c>config/universaldisplay/materials/&lt;kind&gt;.json</c> (Calm, 2026-10-08:
    /// "like with the materials, I would like to have say glass metal and other bits so it is easy to
    /// add new materials"). The FILE NAME is the kind - wood.json, glass.json, metal.json... - and any
    /// mod adds to a kind by shipping the same path in its own domain, or patching ours with
    /// <c>addeach</c> + <c>dependsOn</c>.
    ///
    /// EACH MOD'S OWN DEFINITIONS FIRST (Calm, 2026-10-09 - Cabinet Prototype's "leaded" named "Leaded glass" replaced
    /// Window Display's "Leaded", so windows read "Leaded glass glass"; "some may want it named differently like the order
    /// or whatever"): a block looks a code up in its OWN domain's files first, then in the shared pool. The shared pool
    /// keeps the FIRST definition of a code (Universal Display's domains load first); another mod's different definition
    /// of the same code only applies to that mod's blocks, and is logged. New codes from any mod join the shared pool.
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

        public MaterialDefinition For(string code)
        {
            if (Code != "*") return this;
            return new MaterialDefinition
            {
                Code = code,
                Name = Name?.Replace("{code}", code),
                Item = Item?.Replace("{code}", code),
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
    /// Loads every material file and answers "the wood called oak". Both sides load the same files
    /// (names and textures on the client; recipes and drops on the server).
    /// </summary>
    public sealed class MaterialsSystem : ModSystem
    {
        public const string Folder = "config/universaldisplay/materials/";

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
            api.RegisterBlockBehaviorClass("UDMaterials", typeof(BlockBehaviorUDMaterials));
            api.RegisterBlockEntityBehaviorClass("UDMaterials", typeof(BEBehaviorUDMaterials));
            // the same, for items (one-code glass panes, 2026-10-09)
            api.RegisterCollectibleBehaviorClass("UDMaterials", typeof(CollectibleBehaviorUDMaterials));
            SameMaterialsPatch.Apply();
        }

        public override void Dispose()
        {
            if (api != null) SameMaterialsPatch.Remove();
            api = null;
        }

        // ---------- the planks must be the window's wood ----------

        /// <summary>
        /// A recipe that copies its materials from a block in the grid (copyAttributesFrom - the Display Unit
        /// Window from an awning, the vanilla pane shelf from a vanilla pane window) takes only the planks of
        /// THAT block's wood (Calm, 2026-10-09: "is there a way to make this only take the correct boards?").
        /// Any ingredient that IS a material (a material's `item`, e.g. game:plank-pine = wood pine) must be
        /// the material the block carries for that kind. Only for recipes whose output uses UDMaterials. Both
        /// sides, so the grid's output preview agrees with the server. Called from SameMaterialsPatch (a postfix on
        /// GridRecipe.Matches) - NOT the MatchesGridRecipe event, whose "no" was overridden in game.
        /// </summary>
        internal bool SameMaterialsInGrid(IPlayer player, GridRecipe recipe, ItemSlot[] ingredients, int gridWidth)
        {
            if (recipe?.CopyAttributesFrom == null || ingredients == null) return true;
            var outputBlock = recipe.Output?.ResolvedItemStack?.Collectible as Block;
            if (outputBlock?.GetBehavior<BlockBehaviorUDMaterials>() == null) return true;

            Dictionary<string, string> from = null;
            foreach (var slot in ingredients)
            {
                var stack = slot?.Itemstack;
                if (stack?.Collectible is not Block block || block.GetBehavior<BlockBehaviorUDMaterials>() == null) continue;
                var types = BlockBehaviorUDMaterials.TypesOf(stack.Attributes);
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
            capi.Event.LeaveWorld += () => { MaterialTextures.Clear(); BlockBehaviorUDMaterials.ClearMeshes(capi); CollectibleBehaviorUDMaterials.ClearMeshes(); };
            // Every listed material texture into the block atlas once - textures only - so placed
            // windows can be built on the tesselation thread without waiting.
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
                var behaviour = block?.GetBehavior<BlockBehaviorUDMaterials>();
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
            if (made > 0) capi.Logger.Event("[UniversalDisplayLib] {0} material texture(s) with overlays made at load", made);
        }

        /// <summary>
        /// Files load in path order, Universal Display's own domains first. Within one mod a later entry with the same
        /// code replaces the earlier; across mods the shared pool keeps the first, and each mod keeps its own (see the class).
        /// </summary>
        public override void AssetsLoaded(ICoreAPI api)
        {
            var domains = new Dictionary<string, Dictionary<string, Dictionary<string, MaterialDefinition>>>();
            var domainOrder = new List<string>();
            var assets = api.Assets.GetMany(Folder)
                .OrderBy(a => a.Location.Domain is "universaldisplaylib" or "windowdisplay" ? 0 : 1)
                .ThenBy(a => a.Location.ToString());
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
                            api.Logger.Error("[UniversalDisplayLib] A {0} material in {1} has no code - skipped", kind, asset.Location);
                            continue;
                        }
                        def.Textures ??= new Dictionary<string, string>();
                        def.Overlays ??= new Dictionary<string, string[]>();
                        byCode[def.Code] = def;
                    }
                }
                catch (Exception e) { api.Logger.Error("[UniversalDisplayLib] Cannot read {0}: {1}", asset.Location, e.Message); }
            }
            // An entry that only LISTS a material (just its code - the vanilla woods and glasses, so the creative
            // tab can be made from the list) takes whatever it leaves out from its own mod's `*` entry for the kind...
            foreach (var ownKinds in domains.Values)
                foreach (var byCode in ownKinds.Values)
                    if (byCode.TryGetValue("*", out var any)) FillFrom(any, byCode.Values);

            // ...the shared pool: the first definition of each code, in load order (Universal Display's domains first)
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
                            api.Logger.Warning("[UniversalDisplayLib] {0} '{1}' is defined differently by {2} and {3} - each mod's blocks use their own; others use {2}'s",
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
            lock (itemIndex) { itemIndex.Clear(); notListed.Clear(); }
            api.Logger.Event("[UniversalDisplayLib] Materials: {0}", string.Join(", ", kinds.Select(k => k.Value.Count + " " + k.Key)));

            if (api.Side == EnumAppSide.Server) MakeRecipes(api);
        }

        // ---------- creative tab entries made from the materials ----------

        /// <summary>
        /// THE CREATIVE TAB IS MADE FROM THE MATERIALS (Calm, 2026-10-09: "Do the creative stacks first") - a
        /// blocktype writes ONE stack per creative list: which tabs the style goes in, and which kinds of material
        /// it takes (wood, glass). That list becomes every listed material of each kind the stack names, in the
        /// order of the block's slots (wood outer, glass inner) and IN THE MATERIALS FILES' ORDER - Calm,
        /// 2026-10-09: "whatever the order in the json is the order it shows" (vanilla, then mod woods added by
        /// patches); a material the stack names that no file lists comes after them. Materials whose textures are
        /// not installed are left out. A list with no tabs is made the same way - hidden from the creative tab, but
        /// it gives a wrench-only style (the wide windows) its handbook pages (Calm: "the wide get the handbook back").
        /// </summary>
        public override void AssetsFinalize(ICoreAPI api)
        {
            int made = 0;
            foreach (var block in api.World.Blocks)
            {
                var behaviour = block?.GetBehavior<BlockBehaviorUDMaterials>();
                if (behaviour == null || block.CreativeInventoryStacks == null) continue;
                foreach (var list in block.CreativeInventoryStacks)
                {
                    if (list?.Stacks == null || list.Stacks.Length == 0) continue;
                    var template = list.Stacks[0];
                    var types = template.Attributes?.Token?["types"] as JObject;
                    if (types == null) continue;

                    // per kind the stack names: every listed one in file order, then any it names that no file lists
                    var axes = new List<(string kind, List<string> codes)>();
                    foreach (var kind in behaviour.Slots.Keys)
                    {
                        if (types[kind] == null) continue;
                        var codes = new List<string>();
                        string domain = block.Code?.Domain;
                        foreach (var code in ListFor(kind, domain))
                            if (Get(kind, code, domain) is { } def && TexturesInstalled(api, def)) codes.Add(code);
                        foreach (var written in list.Stacks.Select(js => (string)js.Attributes?.Token?["types"]?[kind]).Where(c => c != null).Distinct())
                            if (!codes.Contains(written)) codes.Add(written);
                        axes.Add((kind, codes));
                    }
                    if (axes.Count == 0) continue;

                    var combos = new List<Dictionary<string, string>> { new() };
                    foreach (var (kind, codes) in axes)
                        combos = combos.SelectMany(c => codes.Select(code => new Dictionary<string, string>(c) { [kind] = code })).ToList();

                    var stacks = new List<JsonItemStack>();
                    foreach (var combo in combos)
                    {
                        var copy = template.Clone();
                        var token = copy.Attributes.Token.DeepClone();
                        foreach (var kv in combo) token["types"][kv.Key] = kv.Value;
                        copy.Attributes = new JsonObject(token);
                        if (!copy.Resolve(api.World, "universaldisplaylib materials", false)) continue;
                        stacks.Add(copy);
                    }
                    made += stacks.Count - list.Stacks.Length;
                    list.Stacks = stacks.ToArray();
                }
            }
            if (made > 0) api.Logger.Event("[UniversalDisplayLib] {0} creative stack(s) made from the materials", made);
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
            // ONLY recipes for blocks that use UDMaterials (found 2026-10-08: the old Window Storage mod's
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
            if (made > 0) api.Logger.Event("[UniversalDisplayLib] {0} recipe(s) made from the materials, in {1} recipe file(s)", made, files);
        }

        /// <summary>domain + blocktype code of every blocktype file that declares the UDMaterials behaviour.</summary>
        private static HashSet<string> BlockCodesUsingMaterials(ICoreAPI api)
        {
            var codes = new HashSet<string>();
            foreach (var asset in api.Assets.GetMany("blocktypes/"))
            {
                string text = Encoding.UTF8.GetString(asset.Data);
                if (!text.Contains("UDMaterials")) continue;
                try
                {
                    if (JToken.Parse(text.TrimStart('\uFEFF')) is not JObject block) continue;
                    if (block["behaviors"] is not JArray behaviors || !behaviors.Any(b => (string)b?["name"] == "UDMaterials")) continue;
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
