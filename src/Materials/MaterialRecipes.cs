using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Vintagestory.API.Common;

namespace UniversalDisplayLib.Materials
{
    /// <summary>
    /// MATERIAL RECIPES - ONE RECIPE PER PIECE, ANY MATERIAL.
    /// A named wildcard (<c>"name": "framewood"</c>) makes the game write one recipe per variant, and with two names one per
    /// PAIR (vsapi RecipeBase.GenerateRecipesForAllIngredientCombinations) - ~27,000 for Wooden Display Shelves with the
    /// wood mods, and the vanilla grid tests them all on every change. An UNNAMED wildcard is never expanded, and
    /// WildcardUtil.Match accepts <c>*</c> as the domain, so <c>"*:plank-*"</c> is ONE recipe that takes every mod's planks.
    /// The recipe says which grid letters are which material kind:
    /// <code>"ingredients": { "F": { "type": "item", "code": "*:plank-*" }, "S": { "type": "item", "code": "*:plank-*" } },
    /// "attributes": { "materials": { "F": "framewood", "S": "standwood" } }</code>   (KitSettings.RecipeKey)
    /// and the kit works out the rest: <see cref="SameMaterialsPatch"/> lets the recipe match only when every letter's
    /// items are ONE material of its kind (listed in the materials files, or answered by its <c>*</c> entry), and writes
    /// those materials as the output's <c>types</c> when it is made (GenerateOutputStack - the grid preview, the craft and
    /// the handbook's output picture all go through it). Recipe attributes reach clients (vsapi RecipeBase.ToBytes).
    /// Works for any output of this mod with the materials behaviour, block or item.
    /// </summary>
    public static class MaterialRecipes
    {
        public const string Key = KitSettings.RecipeKey;

        /// <summary>Set by MaterialsSystem.Start - for the rare error in a Harmony postfix, which has no api of its own.</summary>
        internal static ILogger Logger;

        private static readonly ConditionalWeakTable<GridRecipe, Dictionary<string, string>> letters = new();
        private static readonly Dictionary<string, string> none = new();

        /// <summary>The recipe's letter -> material kind map, or an empty one for an ordinary recipe.</summary>
        public static Dictionary<string, string> LettersOf(GridRecipe recipe)
        {
            if (recipe == null) return none;
            return letters.GetValue(recipe, r =>
            {
                var map = r.Attributes?[Key];
                if (map == null || !map.Exists) return none;
                try { return map.AsObject<Dictionary<string, string>>() ?? none; }
                catch { return none; }
            });
        }

        /// <summary>For a handbook copy of a recipe: the same letters as the original (a clone may not carry the attributes).</summary>
        internal static void Remember(GridRecipe copy, Dictionary<string, string> map)
        {
            if (copy != null && map != null) letters.AddOrUpdate(copy, map);
        }

        /// <summary>The materials this grid gives, kind -> code, or null if a letter holds no material of its kind or two of them.</summary>
        public static Dictionary<string, string> ReadGrid(GridRecipe recipe, ItemSlot[] slots, int gridWidth, string domain)
        {
            var map = LettersOf(recipe);
            var materials = MaterialsSystem.Instance;
            if (map.Count == 0 || materials == null || slots == null || recipe.ResolvedIngredients == null) return null;

            var types = new Dictionary<string, string>();
            foreach (var (letter, stack) in CellsOf(recipe, slots, gridWidth))
            {
                if (!map.TryGetValue(letter, out string kind) || stack?.Collectible?.Code == null) continue;
                if (!materials.TryMaterialOfItem(stack.Collectible.Code, kind, domain, out string code)) return null;
                if (types.TryGetValue(kind, out string seen) && seen != code) return null;
                types[kind] = code;
            }
            // every letter must have been seen (a kind no cell gave is a recipe that cannot say what it is made of)
            foreach (var kind in map.Values) if (!types.ContainsKey(kind)) return null;
            return types;
        }

        /// <summary>
        /// Which grid stack stands in which recipe cell: the first placement where every cell's ingredient is satisfied (the
        /// game already said the recipe matches). Shapeless: each stack to the first unused cell it satisfies.
        /// </summary>
        private static IEnumerable<(string letter, ItemStack stack)> CellsOf(GridRecipe recipe, ItemSlot[] slots, int gridWidth)
        {
            var cells = recipe.ResolvedIngredients;
            if (gridWidth <= 0) gridWidth = (int)Math.Round(Math.Sqrt(slots.Length));
            if (recipe.Shapeless)
            {
                var used = new bool[cells.Length];
                var found = new List<(string, ItemStack)>();
                foreach (var slot in slots)
                {
                    var stack = slot?.Itemstack;
                    if (stack == null) continue;
                    for (int i = 0; i < cells.Length; i++)
                    {
                        if (used[i] || cells[i] == null || !cells[i].SatisfiesAsIngredient(stack, false)) continue;
                        used[i] = true;
                        found.Add((cells[i].Id, stack));
                        break;
                    }
                }
                return found;
            }

            int gridHeight = slots.Length / gridWidth;
            for (int col = 0; col <= gridWidth - recipe.Width; col++)
                for (int row = 0; row <= gridHeight - recipe.Height; row++)
                {
                    var found = new List<(string, ItemStack)>();
                    bool fits = true;
                    for (int y = 0; y < recipe.Height && fits; y++)
                        for (int x = 0; x < recipe.Width && fits; x++)
                        {
                            var cell = cells[y * recipe.Width + x];
                            var stack = slots[(row + y) * gridWidth + col + x]?.Itemstack;
                            if (cell == null) { fits = stack == null; continue; }
                            if (stack == null || !cell.SatisfiesAsIngredient(stack, false)) { fits = false; continue; }
                            found.Add((cell.Id, stack));
                        }
                    if (fits) return found;
                }
            return Array.Empty<(string, ItemStack)>();
        }

        /// <summary>Whether the recipe's output takes materials (this kit's behaviour on a block or an item).</summary>
        public static bool OutputUsesMaterials(GridRecipe recipe)
        {
            var collectible = recipe?.Output?.ResolvedItemStack?.Collectible;
            if (collectible is Block block) return block.GetBehavior<BlockBehaviorMaterials>() != null;
            return collectible?.CollectibleBehaviors?.Any(b => b is CollectibleBehaviorMaterials) == true;
        }

        /// <summary>After the game made the output: write the grid's materials onto it.</summary>
        internal static void WriteOutput(GridRecipe recipe, ItemSlot[] inputSlots, ItemSlot outputSlot)
        {
            var stack = outputSlot?.Itemstack;
            if (stack == null || LettersOf(recipe).Count == 0 || !OutputUsesMaterials(recipe)) return;   // another mod's recipe: not ours
            var types = ReadGrid(recipe, inputSlots, 0, stack.Collectible?.Code?.Domain);
            if (types == null) return;
            var all = BlockBehaviorMaterials.TypesOf(stack.Attributes);   // keep anything copied from another ingredient (a pane's glass)
            foreach (var kv in types) all[kv.Key] = kv.Value;
            BlockBehaviorMaterials.WriteTypes(stack.Attributes, all);
        }
    }
}
