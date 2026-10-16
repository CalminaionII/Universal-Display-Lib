using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace UniversalDisplayLib.Handbook
{
    /// <summary>
    /// COMPACT HANDBOOK - one handbook page for many stacks of one block or item, its list icon flipping through them one a
    /// second, instead of a page per stack. Stands alone: it needs nothing else from the kit, so a mod can carry just this file
    /// (<c>install-kit.py ... --handbook-only</c>).
    /// <code>
    /// // client, before the handbook is built (AssetsFinalize or StartClientSide):
    /// HandbookGroups.Apply(capi, Mod.Info.ModID + ".handbookgroups");
    /// HandbookGroups.Register(item);                                         // one page for all of the item's stacks
    /// HandbookGroups.Register(item, stack => CreatureOf(stack),              // or one page per creature
    ///                               stack => Lang.Get("mymod:figurine-of", CreatureName(stack)));
    /// </code>
    /// OR IN JSON, no code per block - in the block's or item's attributes (needs <c>Apply</c> with your mod id, which the
    /// whole kit's MaterialsSystem already does):
    /// <code>"attributes": { "handbookGroup": { "by": "types/wood", "title": "mymod:chair-group", "names": "game:material-{0}" } }</code>
    /// <c>by</c> - the stack attribute (a path, or a list of paths) whose value picks the page; left out, one page for all.
    /// <c>title</c> - a lang key, {0}, {1}... = those values; <c>names</c> - a lang key pattern turning a value into its name
    /// ("oak" -> "Oak"), else the value with a capital.
    /// A stack's own page code still opens its group with that stack showing, so H on an item and links in recipes land on
    /// the right one. The group's search text is its title. Each mod carrying this file groups only its own.
    /// <para>SECTIONS OF YOUR OWN - text after Created by on a page, worked out when it is first drawn:</para>
    /// <code>
    /// HandbookGroups.AddMadeWith(item, capi => InstalledMaterials().Select(m => m.Source ?? "game"));   // "Can be made with: Vanilla, Pewter"
    /// HandbookGroups.AddSection(item, "mymod:cast-title", capi => "...");                               // anything else
    /// </code>
    /// </summary>
    public static class HandbookGroups
    {
        internal sealed class Rule
        {
            public System.Func<ItemStack, string> Key;
            public System.Func<ItemStack, string> Title;
            public ItemStack[] ExtraStacks;
        }

        private static readonly ConditionalWeakTable<CollectibleObject, Rule> rules = new();
        /// <summary>Collectibles whose JSON was already read and had no handbookGroup.</summary>
        private static readonly ConditionalWeakTable<CollectibleObject, object> noJsonRule = new();
        private static readonly object gate = new();
        private static Harmony harmony;
        /// <summary>
        /// The mod ids whose blocks and items may carry a JSON <c>handbookGroup</c> (Apply's jsonDomain). Replaced whole under
        /// <c>gate</c> and read under it too - not <c>volatile</c>: Attribute Rendering Library's code scanner cannot read a
        /// volatile field and logged an error for every mod carrying this file (Calm's log, 2026-10-15).
        /// </summary>
        private static string[] jsonDomains = Array.Empty<string>();

        /// <summary>
        /// Group this block's or item's handbook pages. <paramref name="groupKey"/>: stacks with the same answer share a page
        /// (null = one page for all of them). <paramref name="title"/>: the page's name from its first stack (null = the
        /// block's or item's own name). <paramref name="extraStacks"/>: stacks that get handbook pages (joining their groups)
        /// WITHOUT being in the creative tab - every metal on a figurine's page while creative shows a few (Codex for Calm,
        /// 2026-10-15).
        /// </summary>
        public static void Register(CollectibleObject collectible, System.Func<ItemStack, string> groupKey = null, System.Func<ItemStack, string> title = null,
            IEnumerable<ItemStack> extraStacks = null)
        {
            if (collectible != null) rules.AddOrUpdate(collectible, new Rule { Key = groupKey, Title = title, ExtraStacks = extraStacks?.ToArray() });
        }

        /// <summary>
        /// Client: put the two handbook patches in, once per mod. Undo with <see cref="Remove"/> (Dispose).
        /// <paramref name="jsonDomain"/>: your mod id - its blocks and items with a JSON <c>handbookGroup</c> are grouped too
        /// ("*" = every mod's, for a library).
        /// </summary>
        public static void Apply(ICoreClientAPI capi, string harmonyId, string jsonDomain = null)
        {
            lock (gate)
            {
                if (jsonDomain != null && !jsonDomains.Contains(jsonDomain)) jsonDomains = jsonDomains.Append(jsonDomain).ToArray();
                if (harmony != null) return;
                harmony = new Harmony(harmonyId);
                harmony.Patch(AccessTools.Method(typeof(ModSystemSurvivalHandbook), "onCreatePagesAsync"),
                    postfix: new HarmonyMethod(typeof(HandbookGroupsCreatePatch), nameof(HandbookGroupsCreatePatch.Postfix)));
                harmony.Patch(AccessTools.Method(typeof(GuiDialogHandbook), nameof(GuiDialogHandbook.OpenDetailPageFor)),
                    prefix: new HarmonyMethod(typeof(HandbookGroupsOpenPatch), nameof(HandbookGroupsOpenPatch.Prefix)));
                harmony.Patch(AccessTools.Method(typeof(CollectibleBehaviorHandbookTextAndExtraInfo), "addCreatedByInfo"),
                    prefix: new HarmonyMethod(typeof(HandbookSectionsPatch), nameof(HandbookSectionsPatch.Prefix)));
            }
        }

        public static void Remove()
        {
            lock (gate)
            {
                harmony?.UnpatchAll(harmony.Id);
                harmony = null;
                jsonDomains = Array.Empty<string>();
            }
        }

        // ---------- sections of your own on a page ----------

        internal sealed class Section
        {
            public string Title;
            public System.Func<ICoreClientAPI, ItemStack, string> Text;
        }

        private static readonly ConditionalWeakTable<CollectibleObject, List<Section>> sections = new();
        /// <summary>The sections last put on a collectible's handbook behaviour - replaced on every page built.</summary>
        private static readonly ConditionalWeakTable<CollectibleObject, ExtraHandbookSection[]> shown = new();

        /// <summary>
        /// A section of your own on this block's or item's handbook page, right after Created by. <paramref name="title"/>: a
        /// lang key or the text itself. <paramref name="text"/>: worked out as each page is drawn (so installed mods are known)
        /// - null or empty = no section on that page. Needs <c>Apply</c>.
        /// </summary>
        public static void AddSection(CollectibleObject collectible, string title, System.Func<ICoreClientAPI, string> text)
        {
            if (text != null) AddSection(collectible, title, (capi, stack) => text(capi));
        }

        /// <summary>
        /// The same, worked out for the page's own stack - so one item's pages can differ: a collector figurine's page leaves
        /// out the metals it could be cast in (Calm, 2026-10-16). On a grouped page it is the stack the page opened on.
        /// </summary>
        public static void AddSection(CollectibleObject collectible, string title, System.Func<ICoreClientAPI, ItemStack, string> text)
        {
            if (collectible == null || text == null) return;
            lock (gate) sections.GetOrCreateValue(collectible).Add(new Section { Title = title, Text = text });
        }

        /// <summary>
        /// The "Can be made with: Vanilla, Pewter" section, as Universal Display's "handbookMods": <paramref name="sources"/>
        /// gives the mod id of every material the block or item can be made of that is INSTALLED now ("game" = the game's).
        /// Named the game's first, then each mod by its own name. Lang (your mod's domain): <c>handbook-mods-title</c>,
        /// <c>handbook-source-game</c> - else "Can be made with" / "Vanilla".
        /// </summary>
        public static void AddMadeWith(CollectibleObject collectible, System.Func<ICoreClientAPI, IEnumerable<string>> sources)
        {
            if (collectible?.Code == null || sources == null) return;
            string domain = collectible.Code.Domain;
            AddSection(collectible, Lang.HasTranslation(domain + ":handbook-mods-title") ? domain + ":handbook-mods-title" : "Can be made with",
                capi => SourcesText(capi, sources(capi), domain));
        }

        /// <summary>"Vanilla, Pewter": the game's first ("game", "" or null), then each mod's name, each once.</summary>
        public static string SourcesText(ICoreClientAPI capi, IEnumerable<string> sources, string langDomain)
        {
            var list = (sources ?? Enumerable.Empty<string>()).Select(s => string.IsNullOrEmpty(s) ? "game" : s).Distinct()
                .OrderBy(s => s == "game" ? 0 : 1).ToList();
            return string.Join(", ", list.Select(s => s == "game"
                ? (Lang.HasTranslation(langDomain + ":handbook-source-game") ? Lang.Get(langDomain + ":handbook-source-game") : "Vanilla")
                : capi.ModLoader.GetMod(s)?.Info?.Name ?? s));
        }

        /// <summary>Called as a page's Created by is built: adds this collectible's sections the first time.</summary>
        internal static void AddSections(ICoreClientAPI capi, CollectibleBehaviorHandbookTextAndExtraInfo handbook, ItemStack stack)
        {
            var collectible = stack?.Collectible;
            if (capi == null || handbook == null || collectible == null) return;
            List<Section> list;
            ExtraHandbookSection[] before;
            lock (gate)
            {
                if (!sections.TryGetValue(collectible, out list)) return;
                list = list.ToList();
                shown.TryGetValue(collectible, out before);
            }
            var made = new List<ExtraHandbookSection>();
            foreach (var section in list)
            {
                string text;
                try { text = section.Text(capi, stack); }
                catch { continue; }   // the handbook must never break over a section
                if (string.IsNullOrEmpty(text)) continue;
                string title = section.Title != null && Lang.HasTranslation(section.Title) ? Lang.Get(section.Title) : section.Title;
                made.Add(new ExtraHandbookSection { Title = title, Text = text });
            }
            // the behaviour is shared by all the collectible's pages: take this mod's sections from the last page off first
            var others = (handbook.ExtraHandBookSections ?? Array.Empty<ExtraHandbookSection>()).Where(s => before == null || !before.Contains(s));
            handbook.ExtraHandBookSections = others.Concat(made).ToArray();
            lock (gate) shown.AddOrUpdate(collectible, made.ToArray());
        }

        internal static Rule RuleFor(ItemStack stack)
        {
            var collectible = stack?.Collectible;
            if (collectible == null) return null;
            if (rules.TryGetValue(collectible, out var rule)) return rule;
            if (noJsonRule.TryGetValue(collectible, out _)) return null;
            rule = JsonRule(collectible);
            if (rule == null) noJsonRule.AddOrUpdate(collectible, null);
            else rules.AddOrUpdate(collectible, rule);
            return rule;
        }

        /// <summary>The rule a block's or item's <c>attributes.handbookGroup</c> describes, or null (none, or another mod's).</summary>
        private static Rule JsonRule(CollectibleObject collectible)
        {
            string[] domains;
            lock (gate) domains = jsonDomains;
            if (!domains.Contains(collectible.Code?.Domain) && !domains.Contains("*")) return null;
            var json = collectible.Attributes?["handbookGroup"];
            if (json == null || !json.Exists) return null;
            string[] paths = json["by"].IsArray() ? json["by"].AsArray<string>() ?? Array.Empty<string>()
                : json["by"].AsString() is { Length: > 0 } one ? new[] { one } : Array.Empty<string>();
            string title = json["title"].AsString(null);
            string names = json["names"].AsString(null);

            string[] Values(ItemStack stack) => paths.Select(p => ValueAt(stack.Attributes, p) ?? "").ToArray();
            string Name(string value)
            {
                if (names != null)
                {
                    string key = names.Replace("{0}", value);
                    if (Lang.HasTranslation(key)) return Lang.Get(key);
                }
                return value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value.Substring(1);
            }
            return new Rule
            {
                Key = paths.Length == 0 ? null : stack => string.Join("|", Values(stack)),
                Title = title == null ? null : stack => Lang.Get(title, Values(stack).Select(Name).ToArray<object>())
            };
        }

        /// <summary>The value at a slash path in a stack's attributes ("types/wood"), as text; null if there is none.</summary>
        private static string ValueAt(Vintagestory.API.Datastructures.ITreeAttribute tree, string path)
        {
            Vintagestory.API.Datastructures.IAttribute node = null;
            foreach (var part in path.Split('/'))
            {
                if (tree == null) return null;
                node = tree[part];
                tree = node as Vintagestory.API.Datastructures.ITreeAttribute;
            }
            return node is Vintagestory.API.Datastructures.StringAttribute s ? s.value : node?.GetValue()?.ToString();
        }

        internal static string KeyOf(ItemStack stack, Rule rule)
        {
            try { return rule.Key?.Invoke(stack) ?? ""; }
            catch { return ""; }
        }

        internal static string TitleOf(ItemStack stack, Rule rule)
        {
            try { if (rule.Title?.Invoke(stack) is { Length: > 0 } title) return title; }
            catch { /* fall back to the plain name */ }
            var collectible = stack.Collectible;
            string key = collectible.Code.Domain + (collectible is Block ? ":block-" : ":item-") + collectible.Code.Path;
            return Lang.HasTranslation(key) ? Lang.Get(key) : stack.GetName();
        }
    }

    /// <summary>One handbook page standing for a group of stacks; the list entry flips through them.</summary>
    public sealed class HandbookGroupPage : GuiHandbookItemStackPage
    {
        public readonly List<ItemStack> Variants;
        private readonly string groupCode;
        private readonly string title;
        public override string PageCode => groupCode;
        public override bool IsDuplicate => false;

        public HandbookGroupPage(ICoreClientAPI api, List<ItemStack> variants, string key, string title)
            : base(api, variants[0])
        {
            Variants = variants;
            groupCode = "handbookgroup-" + variants[0].Class + "-" + variants[0].Collectible.Code + (key.Length > 0 ? "-" + key : "");
            this.title = title;
            TextCacheTitle = title.ToSearchFriendly();
            TextCacheAll = TextCacheTitle;
        }

        /// <summary>Show this stack's page (H on it, a recipe link, or the list icon turning).</summary>
        public void Select(ItemStack variant)
        {
            Stack = variant;
            dummySlot.Itemstack = variant;
        }

        public override void RenderListEntryTo(ICoreClientAPI api, float dt, double x, double y, double cellWidth, double cellHeight)
        {
            Select(Variants[(int)(api.ElapsedMilliseconds / 1000 % Variants.Count)]);
            float size = (float)GuiElement.scaled(25);
            float padding = (float)GuiElement.scaled(10);
            api.Render.RenderItemstackToGui(dummySlot, x + padding + size / 2, y + size / 2, 100, size, -1,
                shading: true, rotate: false, showStackSize: false);
            if (Texture == null) Texture = new TextTextureUtil(api).GenTextTexture(title, CairoFont.WhiteSmallText());
            api.Render.Render2DTexturePremultipliedAlpha(Texture.TextureId, x + size + GuiElement.scaled(25),
                y + size / 4 - GuiElement.scaled(3), Texture.Width, Texture.Height);
        }
    }

    /// <summary>
    /// After the game made one page per stack (vssurvivalmod ModSystemSurvivalHandbook.onCreatePagesAsync): the pages of each
    /// registered group become ONE page, where the first of them stood; their page codes are kept as aliases.
    /// </summary>
    // No [HarmonyPatch] attribute on purpose: a host mod that applies PatchAll to its own assembly would otherwise apply
    // the kit's patches a second time. The kit applies each one by name (see the Apply methods).
    public static class HandbookGroupsCreatePatch
    {
        private sealed class GroupIndex
        {
            public Dictionary<string, (HandbookGroupPage page, ItemStack stack)> Aliases = new();
        }
        private static readonly ConditionalWeakTable<ICoreClientAPI, GroupIndex> indexes = new();

        public static void Postfix(ICoreClientAPI ___capi, ref List<GuiHandbookPage> __result)
        {
            if (__result == null) return;
            // Register's extraStacks: a page each (then grouped like the rest, so their codes become aliases too), once per
            // collectible, skipping any the creative tab already gave a page
            var existingCodes = new HashSet<string>(__result.Select(p => p.PageCode));
            var expanded = new HashSet<CollectibleObject>();
            var extras = new List<GuiHandbookPage>();
            foreach (var page in __result.OfType<GuiHandbookItemStackPage>())
            {
                if (page.Stack?.Collectible == null || !expanded.Add(page.Stack.Collectible)) continue;
                var extraStacks = HandbookGroups.RuleFor(page.Stack)?.ExtraStacks;
                if (extraStacks == null) continue;
                foreach (var stack in extraStacks)
                {
                    if (stack?.Collectible == null) continue;
                    var extra = new GuiHandbookItemStackPage(___capi, stack);
                    if (existingCodes.Add(extra.PageCode)) extras.Add(extra);
                    else extra.Dispose();
                }
            }
            __result.AddRange(extras);
            var buckets = new Dictionary<(CollectibleObject, string), (HandbookGroups.Rule rule, List<GuiHandbookItemStackPage> pages)>();
            foreach (var page in __result.OfType<GuiHandbookItemStackPage>())
            {
                var rule = HandbookGroups.RuleFor(page.Stack);
                if (rule == null) continue;
                var key = (page.Stack.Collectible, HandbookGroups.KeyOf(page.Stack, rule));
                if (!buckets.TryGetValue(key, out var bucket)) buckets[key] = bucket = (rule, new List<GuiHandbookItemStackPage>());
                bucket.pages.Add(page);
            }
            if (buckets.Count == 0) return;

            var index = new GroupIndex();
            var groupOf = new Dictionary<GuiHandbookItemStackPage, HandbookGroupPage>();
            foreach (var ((_, key), (rule, pages)) in buckets)
            {
                var stacks = pages.Select(p => p.Stack).ToList();
                var group = new HandbookGroupPage(___capi, stacks, key, HandbookGroups.TitleOf(stacks[0], rule));
                foreach (var page in pages)
                {
                    groupOf[page] = group;
                    index.Aliases[page.PageCode] = (group, page.Stack);
                }
            }
            var result = new List<GuiHandbookPage>();
            var added = new HashSet<HandbookGroupPage>();
            foreach (var page in __result)
            {
                if (page is GuiHandbookItemStackPage item && groupOf.TryGetValue(item, out var group))
                {
                    if (added.Add(group)) result.Add(group);
                    page.Dispose();
                }
                else result.Add(page);
            }
            indexes.Remove(___capi);
            indexes.Add(___capi, index);
            __result = result;
        }

        public static bool Redirect(ICoreClientAPI api, ref string pageCode)
        {
            if (api != null && pageCode != null && indexes.TryGetValue(api, out var index)
                && index.Aliases.TryGetValue(pageCode, out var alias))
            {
                alias.page.Select(alias.stack);
                pageCode = alias.page.PageCode;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// The sections of <see cref="HandbookGroups.AddSection"/>: vssurvivalmod CollectibleBehaviorHandbookTextAndExtraInfo
    /// .addCreatedByInfo runs as a page is built, before its extra sections are drawn, so a section added here shows on it.
    /// </summary>
    // No [HarmonyPatch] attribute on purpose (see HandbookGroupsCreatePatch).
    public static class HandbookSectionsPatch
    {
        public static void Prefix(CollectibleBehaviorHandbookTextAndExtraInfo __instance, ICoreClientAPI capi, ItemStack stack)
        {
            try { HandbookGroups.AddSections(capi, __instance, stack); }
            catch { /* the handbook must never break over this */ }
        }
    }

    /// <summary>A grouped stack's own page code opens its group, showing that stack.</summary>
    // No [HarmonyPatch] attribute on purpose: a host mod that applies PatchAll to its own assembly would otherwise apply
    // the kit's patches a second time. The kit applies each one by name (see the Apply methods).
    public static class HandbookGroupsOpenPatch
    {
        public static void Prefix(GuiDialogHandbook __instance, ICoreClientAPI ___capi,
            Stack<BrowseHistoryElement> ___browseHistory, ref string pageCode)
        {
            if (HandbookGroupsCreatePatch.Redirect(___capi, ref pageCode)
                && ___browseHistory.Count > 0 && ___browseHistory.Peek().Page?.PageCode == pageCode)
                __instance.ReloadPage();
        }
    }
}
