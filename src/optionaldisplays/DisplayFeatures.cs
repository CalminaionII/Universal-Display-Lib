using System.Collections.Generic;
using System.Linq;
using System;
using Vintagestory.API.Common;

namespace UniversalDisplayLib.OptionalDisplays;

/// <summary>Companions activate features independently. The library alone changes neither placement route.</summary>
public static class DisplayFeatures
{
    public static bool Ground(ICoreAPI api) => api?.ModLoader?.IsModEnabled("grounddisplay") == true;
    public static bool Chiseled(ICoreAPI api) => api?.ModLoader?.IsModEnabled("chiseledblockdisplay") == true;
    public static bool Any(ICoreAPI api) => Ground(api) || Chiseled(api);
}
