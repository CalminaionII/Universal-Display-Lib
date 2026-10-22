using System;
using System.Runtime.CompilerServices;
using Vintagestory.API.MathTools;

namespace UniversalDisplayLib;

// Siding 1.4.1 creates fresh panel arrays for decks/steps. Its identity-keyed
// Combined cache retains a full host copy for each one. Keep only the latest
// combination per immutable host array, comparing the small panel tail by value.
internal sealed class SidingCombinedBoxes
{
    readonly ConditionalWeakTable<Cuboidf[], Entry> entries = new();
    sealed class Entry
    {
        public readonly object Gate = new();
        public Cuboidf[] Result;
    }

    public Cuboidf[] Get(Cuboidf[] host, Cuboidf[] panel)
    {
        if (panel.Length == 0) return host;
        if (host == null || host.Length == 0) return panel;
        var entry = entries.GetValue(host, _ => new Entry());
        lock (entry.Gate)
        {
            var old = entry.Result;
            bool same = old != null && old.Length == host.Length + panel.Length;
            for (int i = 0; same && i < panel.Length; i++)
                same = Equal(old[host.Length + i], panel[i]);
            if (same) return old;
            var result = new Cuboidf[host.Length + panel.Length];
            Array.Copy(host, result, host.Length);
            Array.Copy(panel, 0, result, host.Length, panel.Length);
            return entry.Result = result;
        }
    }

    static bool Equal(Cuboidf a, Cuboidf b) => a.GetType() == b.GetType()
        && a.X1 == b.X1 && a.Y1 == b.Y1 && a.Z1 == b.Z1
        && a.X2 == b.X2 && a.Y2 == b.Y2 && a.Z2 == b.Z2
        && (a as CuboidfWithId)?.Id == (b as CuboidfWithId)?.Id;
}
