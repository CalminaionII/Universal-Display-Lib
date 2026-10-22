using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace UniversalDisplayLib;

// Optional client compatibility, without a dependency on the Siding assembly.
// Preserve Siding's shifting, panel marker types, ordering and interactions.
public sealed class SidingBoxCompatibility : ModSystem
{
    const string PatchId = "universaldisplaylib.siding-combined-boxes";
    static SidingCombinedBoxes cache = new();
    Harmony harmony;

    public override void StartClientSide(ICoreClientAPI api)
    {
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("VSSiding.GapShiftCollisionPatches", false)).FirstOrDefault(t => t != null);
        if (type == null) return;
        var method = type.GetMethod("Combined", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { typeof(Cuboidf[]), typeof(Cuboidf[]) }, null);
        if (method?.ReturnType != typeof(Cuboidf[]))
        {
            api.Logger.Warning("[UniversalDisplayLib] Siding combined-box optimisation unavailable: method signature changed.");
            return;
        }
        try
        {
            cache = new SidingCombinedBoxes();
            harmony = new Harmony(PatchId);
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(SidingBoxCompatibility), nameof(Combine)));
            api.Logger.Notification("[UniversalDisplayLib] Siding combined-box cache enabled.");
        }
        catch (Exception e)
        {
            harmony?.UnpatchAll(PatchId);
            api.Logger.Warning("[UniversalDisplayLib] Siding combined-box optimisation unavailable: {0}", e.Message);
        }
    }

    // Argument indices avoid relying on names from another mod's build.
    static bool Combine(Cuboidf[] __0, Cuboidf[] __1, ref Cuboidf[] __result)
    {
        __result = cache.Get(__0, __1);
        return false;
    }

    public override void Dispose()
    {
        if (harmony != null)
        {
            harmony.UnpatchAll(PatchId);
            cache = new SidingCombinedBoxes();
        }
        base.Dispose();
    }
}
