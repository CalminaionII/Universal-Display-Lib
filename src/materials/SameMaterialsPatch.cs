using HarmonyLib;
using Vintagestory.API.Common;

namespace UniversalDisplayLib.Materials
{
    /// <summary>
    /// THE PLANKS MUST BE THE WINDOW'S WOOD, enforced on the game's own recipe check (Calm, 2026-10-09: "Oak boards
    /// can still be used with an acacia window to make the display unit"). The first version listened to
    /// api.Event.MatchesGridRecipe and answered false, and the craft went through anyway (server-audit.log, both test
    /// rounds) - the game's code that calls that event is not public, but a C# event with several listeners hands back
    /// only the LAST listener's answer, so another mod's "true" can override ours. This postfix on vsapi
    /// GridRecipe.Matches cannot be overridden that way, and it only runs for a recipe whose pattern already matched
    /// (not for every recipe the grid tests - the lag).
    ///
    /// Harmony patches are process-wide, so in singleplayer client and server share ONE patch: it is applied by the
    /// first MaterialsSystem to start and removed by the last to stop, and the side comes from the world it is given.
    /// Not marked [HarmonyPatch], so the lib's client PatchAll (VanillaDisplayBoundsPatch) does not apply it a second time.
    /// </summary>
    public static class SameMaterialsPatch
    {
        private const string HarmonyId = "universaldisplaylib.samematerials";
        private static readonly object gate = new();
        private static Harmony harmony;
        private static int users;

        public static void Apply()
        {
            lock (gate)
            {
                if (users++ > 0) return;
                harmony = new Harmony(HarmonyId);
                var matches = AccessTools.Method(typeof(GridRecipe), nameof(GridRecipe.Matches),
                    new[] { typeof(IPlayer), typeof(IWorldAccessor), typeof(ItemSlot[]), typeof(int) });
                harmony.Patch(matches, postfix: new HarmonyMethod(typeof(SameMaterialsPatch), nameof(Postfix)));
            }
        }

        public static void Remove()
        {
            lock (gate)
            {
                if (users == 0 || --users > 0) return;
                harmony?.UnpatchAll(HarmonyId);
                harmony = null;
            }
        }

        public static void Postfix(GridRecipe __instance, IPlayer forPlayer, IWorldAccessor world, ItemSlot[] ingredients, int gridWidth, ref bool __result)
        {
            if (!__result || __instance?.CopyAttributesFrom == null) return;
            var materials = world?.Api?.ModLoader.GetModSystem<MaterialsSystem>();
            if (materials != null && !materials.SameMaterialsInGrid(forPlayer, __instance, ingredients, gridWidth)) __result = false;
        }
    }
}
