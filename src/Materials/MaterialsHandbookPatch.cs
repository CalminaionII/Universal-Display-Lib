using System;
using System.Linq;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Util;

namespace UniversalDisplayLib.Materials
{
    /// <summary>
    /// The handbook's "Created by" for a recipe that copies the materials from a REPEATED ingredient - the tall
    /// windows (two or three casements, all the same key, <c>copyAttributesFrom</c> that key). Calm, 2026-10-08:
    /// the page for a larch / quartz tall casement showed the top casement in larch / quartz and the one under it
    /// plain.
    ///
    /// The game already gives the page's materials to the copy-from ingredient (vssurvivalmod
    /// CollectibleBehaviorHandbookTextAndExtraInfo, the CopyAttributesFrom branch before it builds the
    /// SlideshowGridRecipeTextComponent) - but it finds that ingredient with GetIngredientById, which returns
    /// the FIRST grid cell with that id (vsapi RecipeBase.GetIngredientById), so only one cell changes. This
    /// gives every other cell with the same id the same materials, just before the slideshow is built.
    /// Only for recipes whose output uses this kit's materials behaviour. Client only (applied by MaterialsSystem.StartClientSide).
    /// </summary>
    // No [HarmonyPatch] attribute on purpose: a host mod that applies PatchAll to its own assembly would otherwise apply
    // the kit's patches a second time. The kit applies each one by name (see the Apply methods).
    public static class MaterialsHandbookPatch
    {
        public static void Prefix(ICoreClientAPI api, ref GridRecipe[] gridRecipes)
        {
            if (gridRecipes == null) return;
            var kept = new System.Collections.Generic.List<GridRecipe>();
            bool changed = false;
            foreach (var recipe in gridRecipes)
            {
                bool keep = true;
                try
                {
                    if (!MatchesPage(recipe, CreatedByPagePatch.Page)) { changed = true; continue; }
                    var letters = MaterialRecipes.LettersOf(recipe);
                    if (letters.Count > 0 && MaterialRecipes.OutputUsesMaterials(recipe))
                    {
                        var copies = MaterialCopies(api, recipe, letters);
                        if (copies.Count > 0) { kept.AddRange(copies); changed = true; continue; }
                    }
                    SpreadCopiedMaterials(recipe); keep = OnlyTheBlocksMaterials(api, recipe);
                }
                catch { /* the handbook must never break over this */ }
                if (keep) kept.Add(recipe); else changed = true;
            }
            if (changed) gridRecipes = kept.ToArray();
        }

        /// <summary>
        /// A recipe that FIXES a material on its output (the game's leaded pane -> a leaded window) belongs only on the page of
        /// that material; a copy-from recipe (the window takes the pane's glass) shows on every page. 2026-10-14.
        /// </summary>
        public static bool MatchesPage(GridRecipe recipe, ItemStack page)
        {
            if (page == null || !MaterialRecipes.OutputUsesMaterials(recipe)) return true;
            if (recipe.CopyAttributesFrom != null) return true;
            if (recipe.Output?.ResolvedItemStack?.Collectible != page.Collectible) return true;
            var wanted = BlockBehaviorMaterials.TypesOf(page.Attributes);
            var fixedTypes = BlockBehaviorMaterials.TypesOf(recipe.Output?.ResolvedItemStack?.Attributes);
            return fixedTypes.All(t => !wanted.TryGetValue(t.Key, out var value) || value == t.Value);
        }

        /// <summary>The most copies one material recipe is shown as - the slideshow flips through them, one a second.</summary>
        private const int MaxCopies = MaterialsSystem.MaxHandbookCycle;

        /// <summary>Each handbook copy's place in the order it was made.</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GridRecipe, System.Runtime.CompilerServices.StrongBox<int>> Order = new();

        /// <summary>
        /// The slideshow SHUFFLES its recipes (vsapi SlideshowGridRecipeTextComponent, a fixed-seed Shuffle in the
        /// constructor), which would scatter the copies' order. Put them back: the copies in the order they were made, any
        /// other recipe after them as it was. Not when a recipe shows a trait line - those lines are kept by position.
        /// </summary>
        public static void Postfix(SlideshowGridRecipeTextComponent __instance)
        {
            try
            {
                var shown = __instance?.GridRecipesAndUnnamedIngredients;
                if (shown == null || shown.Length < 2) return;
                if (!shown.Any(r => r?.Recipe != null && Order.TryGetValue(r.Recipe, out _))) return;
                if (shown.Any(r => r?.Recipe?.RequiresTrait != null)) return;
                var sorted = shown.Select((r, i) => (r, i))
                    .OrderBy(x => x.r?.Recipe != null && Order.TryGetValue(x.r.Recipe, out var box) ? box.Value : int.MaxValue)
                    .ThenBy(x => x.i).Select(x => x.r).ToArray();
                Array.Copy(sorted, shown, shown.Length);
            }
            catch { /* the handbook must never break over this */ }
        }

        /// <summary>
        /// A MATERIAL RECIPE (MaterialRecipes) has wildcard cells, and the slideshow fills every wildcard cell with its OWN
        /// random item - frame planks of three woods, which reads as "mixing is fine". So it is shown as COPIES instead
        /// (never the real recipe - the handbook hands over the game's own recipe objects), each with ONE material per kind,
        /// and the slideshow flips through them (Calm, 2026-10-14: "if using one material cycles all the ones that can be
        /// used ... need one that shows mixed aswell"):
        /// the page's own materials first, then - on a page whose stack MIXES them (oak shelves on a walnut frame) - every
        /// other mixed pair, or - on a page with one material throughout (oak on oak) - every material in step. So a mod
        /// writes a matching and a mixed creative stack ("creative": "written") and each page's recipe shows its side.
        /// The output picture is made by running the copy on those items (GenerateCurrentVisibleOutputStack), so it shows the
        /// same materials. The page comes from <see cref="CreatedByPagePatch"/>.
        /// </summary>
        private static System.Collections.Generic.List<GridRecipe> MaterialCopies(ICoreClientAPI api, GridRecipe recipe, System.Collections.Generic.Dictionary<string, string> letters)
        {
            var copies = new System.Collections.Generic.List<GridRecipe>();
            var materials = MaterialsSystem.Instance;
            if (materials == null || recipe.ResolvedIngredients == null) return copies;
            string domain = recipe.Output?.ResolvedItemStack?.Collectible?.Code?.Domain;

            var kinds = letters.Values.Distinct().ToList();
            var page = CreatedByPagePatch.Page != null ? BlockBehaviorMaterials.TypesOf(CreatedByPagePatch.Page.Attributes) : null;
            var combos = materials.CycleCombos(api.World, kinds, domain, page, MaxCopies);

            foreach (var combo in combos)
            {
                var copy = recipe.Clone();
                copy.Attributes = recipe.Attributes;
                MaterialRecipes.Remember(copy, letters);
                bool fits = true;
                foreach (var cell in copy.ResolvedIngredients)
                {
                    if (cell?.Id == null || !letters.TryGetValue(cell.Id, out string kind)) continue;
                    var item = new AssetLocation(materials.Get(kind, combo[kind], domain).Item);
                    if (!WildcardUtil.Match(cell.Code, item, cell.AllowedVariants)) { fits = false; break; }
                    cell.Code = item;
                }
                if (fits) { Order.AddOrUpdate(copy, new System.Runtime.CompilerServices.StrongBox<int>(copies.Count)); copies.Add(copy); }
            }
            return copies;
        }

        private static bool OnlyTheBlocksMaterials(ICoreClientAPI api, GridRecipe recipe)
        {
            string id = recipe?.CopyAttributesFrom;
            var cells = recipe?.ResolvedIngredients;
            var materials = MaterialsSystem.Instance;
            if (id == null || cells == null || materials == null || !UsesMaterials(recipe)) return true;

            var source = cells.FirstOrDefault(c => c?.Id == id && c.ResolvedItemStack != null && BlockBehaviorMaterials.TypesOf(c.ResolvedItemStack.Attributes).Count > 0);
            if (source == null) return true;
            var types = BlockBehaviorMaterials.TypesOf(source.ResolvedItemStack.Attributes);
            // the output block's own mod's materials (2026-10-10 - one code can be a rod in one mod and a plate in another)
            string domain = recipe.Output?.ResolvedItemStack?.Collectible?.Code?.Domain;

            foreach (var cell in cells)
            {
                if (cell?.Code == null) continue;
                // A named wildcard (`"name": "wood"` on game:plank-*) is split by the game's recipe loader into ONE
                // RECIPE PER PLANK, each cell exact (Calm, 2026-10-09: "it still allows diffent boards at least it
                // shows that in the recipe") - so a recipe whose plank is another wood is left out here.
                if (cell.MatchingType == EnumRecipeMatchType.Exact)
                {
                    if (materials.TryMaterialOfItem(cell.Code, out string cellKind, out string cellCode, domain)
                        && types.TryGetValue(cellKind, out string wanted) && wanted != cellCode) return false;
                    continue;
                }
                foreach (var (kind, code) in types)
                {
                    if (!materials.IsItemPatternOf(kind, cell.Code, domain)) continue;
                    string item = materials.Get(kind, code, domain)?.Item;
                    if (string.IsNullOrEmpty(item)) break;
                    var itemCode = new AssetLocation(item);
                    if (!WildcardUtil.Match(cell.Code, itemCode, cell.AllowedVariants)) return false;
                    if (api?.World?.GetItem(itemCode) == null && api?.World?.GetBlock(itemCode) == null) break;
                    cell.Code = itemCode;
                    break;
                }
            }
            return true;
        }

        private static bool UsesMaterials(GridRecipe recipe) =>
            (recipe.Output?.ResolvedItemStack?.Collectible as Block)?.GetBehavior<BlockBehaviorMaterials>() != null;

        private static void SpreadCopiedMaterials(GridRecipe recipe)
        {
            string id = recipe?.CopyAttributesFrom;
            var cells = recipe?.ResolvedIngredients;
            if (id == null || cells == null) return;
            if (recipe.Output?.ResolvedItemStack?.Collectible?.GetCollectibleBehavior<BlockBehaviorMaterials>(true) == null) return;

            var same = cells.Where(c => c?.Id == id && c.ResolvedItemStack != null).ToList();
            var source = same.FirstOrDefault(c => BlockBehaviorMaterials.TypesOf(c.ResolvedItemStack.Attributes).Count > 0);
            if (source == null) return;
            var types = BlockBehaviorMaterials.TypesOf(source.ResolvedItemStack.Attributes);
            // A cloned recipe's cell holds its OWN copy of the stack, but the ResolvedItemStack SETTER writes to the
            // game's shared de-duplicated ingredient list (vsapi CraftingRecipeIngredient) - so give the cell's own
            // stack fresh attributes instead, as the handbook does for the first cell.
            foreach (var cell in same)
            {
                if (cell == source) continue;
                var stack = cell.ResolvedItemStack;
                var attributes = stack.Attributes?.Clone() ?? new TreeAttribute();
                BlockBehaviorMaterials.WriteTypes(attributes, types);
                stack.Attributes = attributes;
            }
        }
    }

    /// <summary>
    /// Which page the handbook is building "Created by" for - vssurvivalmod CollectibleBehaviorHandbookTextAndExtraInfo
    /// .addCreatedByInfo is handed the page's stack and builds the recipe slideshows inside it, so the stack is noted for
    /// <see cref="MaterialsHandbookPatch"/> while it runs (one page at a time, on the main thread). Client only.
    /// </summary>
    // No [HarmonyPatch] attribute on purpose: a host mod that applies PatchAll to its own assembly would otherwise apply
    // the kit's patches a second time. The kit applies each one by name (see the Apply methods).
    public static class CreatedByPagePatch
    {
        [ThreadStatic] public static ItemStack Page;

        public static void Prefix(Vintagestory.GameContent.CollectibleBehaviorHandbookTextAndExtraInfo __instance, ICoreClientAPI capi, ItemStack stack)
        {
            Page = stack;
            // "handbookMods": the "Can be made with" line, drawn right after Created by
            try { MaterialsSystem.Instance?.AddModsSection(capi, __instance, stack?.Collectible); }
            catch { /* the handbook must never break over this */ }
        }

        public static Exception Finalizer(Exception __exception) { Page = null; return __exception; }
    }

    /// <summary>
    /// The handbook finds a page's recipes with Satisfies, and the game's nested-tree subset check compares <c>types</c> the
    /// wrong way round (a nested tree must hold all of ours) - so a recipe fixing the glass but taking the wood from the grid (the game's
    /// leaded pane routes) never reached the leaded window's page. Only while a page is being built (CreatedByPagePatch.Page)
    /// and only for blocks and items with materials: the output's own types must all be on the page; crafting is untouched.
    /// 2026-10-14.
    /// </summary>
    // No [HarmonyPatch] attribute on purpose: a host mod that applies PatchAll to its own assembly would otherwise apply
    // the kit's patches a second time. The kit applies each one by name (see the Apply methods).
    public static class MaterialsHandbookOutputPatch
    {
        public static void Postfix(CollectibleObject __instance, ItemStack thisStack, ItemStack otherStack, ref bool __result)
        {
            if (__result || CreatedByPagePatch.Page != otherStack || __instance != otherStack?.Collectible) return;
            if (thisStack?.Class != otherStack.Class || thisStack.Id != otherStack.Id) return;
            if (__instance is Block block ? block.GetBehavior<BlockBehaviorMaterials>() == null
                : !(__instance.CollectibleBehaviors?.Any(b => b is CollectibleBehaviorMaterials) ?? false)) return;
            var required = BlockBehaviorMaterials.TypesOf(thisStack.Attributes);
            var actual = BlockBehaviorMaterials.TypesOf(otherStack.Attributes);
            if (required.Count == 0 || !required.All(t => actual.TryGetValue(t.Key, out var value) && value == t.Value)) return;
            var remaining = thisStack.Attributes.Clone();
            remaining.RemoveAttribute("types");
            __result = remaining.IsSubSetOf(null, otherStack.Attributes);
        }
    }
}
