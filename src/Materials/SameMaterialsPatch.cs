using HarmonyLib;
using Vintagestory.API.Common;

namespace UniversalDisplayLib.Materials
{
    /// <summary>
    /// The planks must be the block's wood, enforced on the game's own recipe check. Answering false from
    /// api.Event.MatchesGridRecipe does not work: the craft goes through anyway, because a C# event with several
    /// listeners hands back only the last listener's answer, so another mod's "true" can override ours. This postfix on
    /// vsapi GridRecipe.Matches cannot be overridden that way, and it only runs for a recipe whose pattern already
    /// matched, not for every recipe the grid tests.
    ///
    /// Harmony patches are process-wide, so in single player client and server share one patch: it is applied by the
    /// first MaterialsSystem to start and removed by the last to stop, and the side comes from the world it is given.
    /// Each mod carrying the kit applies its own copy under its own Harmony id, and each copy only judges its own mod's
    /// recipes.
    /// </summary>
    public static class SameMaterialsPatch
    {
        private static string harmonyId;
        private static readonly object gate = new();
        private static Harmony harmony;
        private static int users;

        public static void Apply(string id)
        {
            lock (gate)
            {
                if (users++ > 0) return;
                harmonyId = id;
                harmony = new Harmony(harmonyId);
                var matches = AccessTools.Method(typeof(GridRecipe), nameof(GridRecipe.Matches),
                    new[] { typeof(IPlayer), typeof(IWorldAccessor), typeof(ItemSlot[]), typeof(int) });
                harmony.Patch(matches, postfix: new HarmonyMethod(typeof(SameMaterialsPatch), nameof(Postfix)));
                // material recipes (MaterialRecipes): the made output takes the grid's materials. GridRecipe does not
                // override GenerateOutputStack, so the base method is the one every grid recipe runs.
                var output = AccessTools.Method(typeof(RecipeBase), nameof(RecipeBase.GenerateOutputStack),
                    new[] { typeof(ItemSlot[]), typeof(ItemSlot) });
                harmony.Patch(output, postfix: new HarmonyMethod(typeof(SameMaterialsPatch), nameof(OutputPostfix)));
            }
        }

        public static void Remove()
        {
            lock (gate)
            {
                if (users == 0 || --users > 0) return;
                harmony?.UnpatchAll(harmonyId);
                harmony = null;
            }
        }

        public static void Postfix(GridRecipe __instance, IPlayer forPlayer, IWorldAccessor world, ItemSlot[] ingredients, int gridWidth, ref bool __result)
        {
            if (!__result || __instance == null) return;
            if (__instance.CopyAttributesFrom == null && MaterialRecipes.LettersOf(__instance).Count == 0) return;
            var materials = world?.Api?.ModLoader.GetModSystem<MaterialsSystem>();
            if (materials != null && !materials.SameMaterialsInGrid(forPlayer, __instance, ingredients, gridWidth)) __result = false;
        }

        public static void OutputPostfix(RecipeBase __instance, ItemSlot[] inputSlots, ItemSlot outputSlot)
        {
            if (__instance is not GridRecipe recipe) return;
            try { MaterialRecipes.WriteOutput(recipe, inputSlots, outputSlot); }
            catch (System.Exception e) { MaterialRecipes.Logger?.Error("[Materials Kit] Could not give recipe {0} its materials: {1}", recipe.Name, e); }
        }
    }
}
