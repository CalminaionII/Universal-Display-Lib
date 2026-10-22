using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace UniversalDisplayLib;

// Optional soft integration: Lumos replaces the vanilla block-light flood entirely.
// Keep its ray tracer, batching, neighbouring geometry and sunlight intact.
public sealed class LumosDisplayCompatibility : ModSystem
{
    const string PatchId = "universaldisplaylib.lumos-display-light";
    static Harmony harmony;
    static int users;
    bool registered;

    public override void AssetsFinalize(ICoreAPI api)
    {
        if (registered) return;
        registered = true;
        if (users++ != 0) return;
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("Lumos.Core.LumosChunkIlluminator")).FirstOrDefault(t => t != null);
        if (type == null) return;
        try
        {
            var ray = AccessTools.Method(type, "ProcessRay");
            if (ray == null) throw new MissingMethodException(type.FullName, "ProcessRay");
            harmony = new Harmony(PatchId);
            harmony.Patch(ray, transpiler: new HarmonyMethod(typeof(LumosDisplayCompatibility), nameof(FilterSource)));
            api.Logger.Notification("[UniversalDisplayLib] Lumos display-light compatibility enabled.");
        }
        catch (Exception e)
        {
            harmony?.UnpatchAll(PatchId);
            harmony = null;
            api.Logger.Warning("[UniversalDisplayLib] Lumos display-light compatibility unavailable: {0}", e.Message);
        }
    }

    internal static float SourceAbsorption(float original, float originX, float originY, float originZ,
        int bounceCount, IBlockAccessor accessor, BlockPos sample)
    {
        if (original <= 0 || bounceCount != 0 || sample == null || accessor == null
            || sample.X != (int)MathF.Floor(originX) || sample.Y != (int)MathF.Floor(originY)
            || sample.Z != (int)MathF.Floor(originZ)) return original;
        // Only chiselled embedded storage. Ordinary shelves/windows/ground use Lumos unchanged.
        // Only published light; this executes on Lumos's worker. Do not resolve items.
        try
        {
            var display = BEUniversalDisplay.At(accessor, sample);
            return display is { IsEmbedded: true, EmbeddedHost: BlockEntityMicroBlock }
                && display.CachedLightHsv is { Length: >= 3 } light && light[2] > 0 ? 0 : original;
        }
        catch (Exception) { return original; } // a host/chunk may be unloading

    }

    static IEnumerable<CodeInstruction> FilterSource(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var rayType = original.GetParameters().Single().ParameterType;
        var origin = new[] { "OriginX", "OriginY", "OriginZ", "BounceCount" }
            .Select(n => AccessTools.Field(rayType, n) ?? throw new MissingFieldException(rayType.FullName,n)).ToArray();
        var accessor = AccessTools.Field(original.DeclaringType, "readBlockAccess")
            ?? throw new MissingFieldException(original.DeclaringType.FullName,"readBlockAccess");
        var position = AccessTools.Field(original.DeclaringType, "tmpPos")
            ?? throw new MissingFieldException(original.DeclaringType.FullName,"tmpPos");
        var filter = AccessTools.Method(typeof(LumosDisplayCompatibility), nameof(SourceAbsorption));
        int count = 0;
        foreach (var instruction in instructions)
        {
            yield return instruction;
            if (instruction.operand is not MethodInfo method || method.Name != "GetEffectiveAbsorption") continue;
            if (method.ReturnType != typeof(float)) throw new InvalidOperationException("Unexpected Lumos absorption result");
            count++;
            foreach (var field in origin)
            {
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return new CodeInstruction(OpCodes.Ldfld, field);
            }
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Ldfld, accessor);
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Ldfld, position);
            yield return new CodeInstruction(OpCodes.Call, filter);
        }
        if (count == 0) throw new InvalidOperationException("Lumos ray absorption calls not found");
    }

    public override void Dispose()
    {
        if (registered && --users == 0) { harmony?.UnpatchAll(PatchId); harmony = null; }
        registered = false;
        base.Dispose();
    }
}
