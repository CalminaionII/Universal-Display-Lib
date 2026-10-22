using System.Collections.Generic;
using System.Linq;
using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.MathTools;
using Vintagestory.API.Common;

namespace UniversalDisplayLib.OptionalDisplays;

// A carved stone cell still absorbs like its material. Vanilla subtracts that
// absorption at the source before propagating block light, extinguishing a lamp
// inside it. Exempt just that source sample in the block-light routines: sunlight
// and every neighbouring wall keep their normal absorption.
internal static class EmbeddedLightPropagation
{
    static readonly ConcurrentDictionary<(int, int, int), byte> sources = new();
    internal readonly record struct SourceScope(bool Active, int X, int Y, int Z);
    [ThreadStatic] static SourceScope current;

    static readonly string[] TargetNames = { "CollectLightValuesForLightSource", "RemoveBlockLight" };

    /// <summary>True while the engine's block-light routines carry the patch.</summary>
    internal static bool Applied { get; private set; }

    // Keep source history until world disposal so queued removals still use the
    // same propagation radius after the item or its entire host has gone.
    internal static void Register(BlockPos position)
    {
        if (Applied) sources[(position.X, position.InternalY, position.Z)] = 0;
    }
    internal static void Clear() { sources.Clear(); current = default; Applied = false; }

    internal static bool Apply(Harmony harmony, ILogger logger)
        => Apply(harmony, logger, AccessTools.TypeByName("Vintagestory.Common.ChunkIlluminator")
            ?? Type.GetType("Vintagestory.Common.ChunkIlluminator, VintagestoryLib", false));

    /// <summary>
    /// Patches the engine's block-light routines. They live in VintagestoryLib, which is not public and can change in
    /// any game update, so nothing here may stop the game loading: every target is found before anything is patched,
    /// a patch that fails takes the ones before it back off, and the result is one warning in the log. Without the
    /// patch a lamp carved into a block is dimmed by the block's own material, as without this mod - everything else
    /// in Chiseled Block Display still works. True = applied.
    /// </summary>
    internal static bool Apply(Harmony harmony, ILogger logger, Type engine)
    {
        var patched = new List<MethodBase>();
        try
        {
            if (engine == null) throw new TypeLoadException("Vintagestory.Common.ChunkIlluminator not found");
            if (AccessTools.Field(engine, "tmpPos") == null) throw new MissingFieldException(engine.FullName, "tmpPos");
            var targets = TargetNames.Select(name => AccessTools.Method(engine, name)
                ?? throw new MissingMethodException(engine.FullName, name)).ToArray();
            foreach (var target in targets)
            {
                patched.Add(target);
                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(EmbeddedLightPropagation), nameof(Enter)),
                    transpiler: new HarmonyMethod(typeof(EmbeddedLightPropagation), nameof(FilterSourceSample)),
                    finalizer: new HarmonyMethod(typeof(EmbeddedLightPropagation), nameof(Leave)));
            }
            Applied = true;
            return true;
        }
        catch (Exception e)
        {
            foreach (var target in patched)
                try { harmony.Unpatch(target, HarmonyPatchType.All, harmony.Id); } catch { }
            Applied = false;
            sources.Clear();
            logger?.Warning("[Chiseled Block Display light] Could not hook the game's lighting, so lamps inside chiselled "
                + "blocks are dimmed by the block's material as in vanilla. Everything else works. ({0}: {1})",
                e.GetType().Name, e.Message);
            return false;
        }
    }

    static void Enter(int posX, int posY, int posZ, out SourceScope __state)
    {
        __state = current;
        current = new(sources.ContainsKey((posX, posY, posZ)), posX, posY, posZ);
    }

    static Exception Leave(Exception __exception, SourceScope __state)
    {
        current = __state;
        return __exception;
    }

    internal static int SourceAbsorption(int original, BlockPos sample)
    {
        if (!current.Active || sample == null || sample.X != current.X || sample.InternalY != current.Y || sample.Z != current.Z)
            return original;
        return 0;
    }

    static IEnumerable<CodeInstruction> FilterSourceSample(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var sampledPosition = AccessTools.Field(original.DeclaringType, "tmpPos")
            ?? throw new MissingFieldException(original.DeclaringType.FullName, "tmpPos");
        var filter = AccessTools.Method(typeof(EmbeddedLightPropagation), nameof(SourceAbsorption));
        int count = 0;
        foreach (var instruction in instructions)
        {
            yield return instruction;
            if (instruction.operand is not MethodInfo method || method.Name != "GetLightAbsorptionAt") continue;
            count++;
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Ldfld, sampledPosition);
            yield return new CodeInstruction(OpCodes.Call, filter);
        }
        if (count == 0) throw new InvalidOperationException("Block-light source absorption sample not found: " + original.Name);
    }
}
