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
    /// Only for recipes whose output uses UDMaterials. Client only (applied with the lib's other patch).
    /// </summary>
    [HarmonyPatch(typeof(SlideshowGridRecipeTextComponent), MethodType.Constructor,
        new[] { typeof(ICoreClientAPI), typeof(GridRecipe[]), typeof(double), typeof(EnumFloat), typeof(Action<ItemStack>), typeof(ItemStack[]) })]
    public static class MaterialsHandbookPatch
    {
        public static void Prefix(ICoreClientAPI api, ref GridRecipe[] gridRecipes)
        {
            if (gridRecipes == null) return;
            var kept = new System.Collections.Generic.List<GridRecipe>();
            foreach (var recipe in gridRecipes)
            {
                bool keep = true;
                try { SpreadCopiedMaterials(recipe); keep = OnlyTheBlocksMaterials(api, recipe); }
                catch { /* the handbook must never break over this */ }
                if (keep) kept.Add(recipe);
            }
            if (kept.Count > 0 && kept.Count < gridRecipes.Length) gridRecipes = kept.ToArray();
        }

        /// <summary>
        /// The planks shown are the page's own wood (Calm, 2026-10-09 - the crafting check is MaterialsSystem
        /// .SameMaterialsInGrid): in a copy-from recipe, every wildcard cell that stands for a kind's items
        /// (game:plank-*) is narrowed to the item of the material the window carries (game:plank-pine) - the
        /// slideshow then finds just that plank (it caches its matches by the cell's code). A recipe whose cell
        /// cannot take that item (the vanilla planks recipe on a bdtree ash page) cannot make this block, so it is
        /// left out (false). An item the game does not have leaves the cell as it was.
        /// </summary>
        private static bool OnlyTheBlocksMaterials(ICoreClientAPI api, GridRecipe recipe)
        {
            string id = recipe?.CopyAttributesFrom;
            var cells = recipe?.ResolvedIngredients;
            var materials = MaterialsSystem.Instance;
            if (id == null || cells == null || materials == null || !UsesMaterials(recipe)) return true;

            var source = cells.FirstOrDefault(c => c?.Id == id && c.ResolvedItemStack != null && BlockBehaviorUDMaterials.TypesOf(c.ResolvedItemStack.Attributes).Count > 0);
            if (source == null) return true;
            var types = BlockBehaviorUDMaterials.TypesOf(source.ResolvedItemStack.Attributes);
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
            (recipe.Output?.ResolvedItemStack?.Collectible as Block)?.GetBehavior<BlockBehaviorUDMaterials>() != null;

        private static void SpreadCopiedMaterials(GridRecipe recipe)
        {
            string id = recipe?.CopyAttributesFrom;
            var cells = recipe?.ResolvedIngredients;
            if (id == null || cells == null) return;
            if (recipe.Output?.ResolvedItemStack?.Collectible?.GetCollectibleBehavior<BlockBehaviorUDMaterials>(true) == null) return;

            var same = cells.Where(c => c?.Id == id && c.ResolvedItemStack != null).ToList();
            var source = same.FirstOrDefault(c => BlockBehaviorUDMaterials.TypesOf(c.ResolvedItemStack.Attributes).Count > 0);
            if (source == null) return;
            var types = BlockBehaviorUDMaterials.TypesOf(source.ResolvedItemStack.Attributes);
            // A cloned recipe's cell holds its OWN copy of the stack, but the ResolvedItemStack SETTER writes to the
            // game's shared de-duplicated ingredient list (vsapi CraftingRecipeIngredient) - so give the cell's own
            // stack fresh attributes instead, as the handbook does for the first cell.
            foreach (var cell in same)
            {
                if (cell == source) continue;
                var stack = cell.ResolvedItemStack;
                var attributes = stack.Attributes?.Clone() ?? new TreeAttribute();
                BlockBehaviorUDMaterials.WriteTypes(attributes, types);
                stack.Attributes = attributes;
            }
        }
    }
}
