using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;

namespace UniversalDisplayLib.OptionalDisplays;

/// <summary>
/// Applies a group of Harmony hooks all together or not at all (lib 1.3.1). The companions hook the game's chisel,
/// ground-storage and lighting code, and a game update can rename or change any of those methods. A hook that
/// cannot be applied used to throw out of a ModSystem's Start and stop the game loading; now every target is found
/// before anything is patched, a group that fails half way takes back what it had applied, and the result is one
/// warning in the log naming what stops working. Callers decide what to switch off when a group is missing.
/// </summary>
internal static class SafePatch
{
    internal sealed class Hook
    {
        public MethodBase Target;
        public string Name;
        public HarmonyMethod Prefix, Postfix;
    }

    /// <summary>A hook on <paramref name="type"/>.<paramref name="method"/>, found as AccessTools.Method finds it.</summary>
    internal static Hook On(Type type, string method)
    {
        MethodBase target = null;
        try { target = type == null ? null : AccessTools.Method(type, method); } catch (AmbiguousMatchException) { }
        return new Hook { Target = target, Name = (type?.Name ?? "?") + "." + method };
    }

    /// <summary>A hook on a method the caller has already looked up (null = not found).</summary>
    internal static Hook On(MethodBase target, string name) => new() { Target = target, Name = name };

    internal static Hook Before(this Hook hook, Type owner, string handler) { hook.Prefix = new HarmonyMethod(owner, handler); return hook; }

    internal static Hook After(this Hook hook, Type owner, string handler) { hook.Postfix = new HarmonyMethod(owner, handler); return hook; }

    /// <summary>
    /// Patches every hook of the group, or none. <paramref name="what"/> names the group in the warning,
    /// <paramref name="withoutIt"/> says what players lose. True = all applied.
    /// </summary>
    internal static bool Group(Harmony harmony, ILogger logger, string what, string withoutIt, params Hook[] hooks)
    {
        var patched = new List<MethodBase>();
        try
        {
            foreach (var hook in hooks)
                if (hook.Target == null) throw new MissingMethodException(hook.Name + " not found");
            foreach (var hook in hooks)
            {
                patched.Add(hook.Target);
                harmony.Patch(hook.Target, prefix: hook.Prefix, postfix: hook.Postfix);
            }
            return true;
        }
        catch (Exception e)
        {
            foreach (var target in patched)
                try { harmony.Unpatch(target, HarmonyPatchType.All, harmony.Id); } catch { }
            logger?.Warning("[UniversalDisplayLib] Could not hook the game for {0}, so {1}. Everything else works. ({2}: {3})",
                what, withoutIt, e.GetType().Name, e.Message);
            return false;
        }
    }
}
