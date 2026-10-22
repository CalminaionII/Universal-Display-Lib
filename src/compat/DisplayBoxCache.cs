using System;
using Vintagestory.API.MathTools;

namespace UniversalDisplayLib;

// Keep one immutable result per cell. Siding also caches by array identity, so a new
// array on every raycast forces it to copy all the placement pads again.
internal sealed class DisplayBoxCache
{
    readonly object gate = new();
    Cuboidf[] layout, pads, items, result;
    float x, z;

    public Cuboidf[] Get(Cuboidf[] layout, Cuboidf[] pads, Cuboidf[] items, float x, float z)
    {
        lock (gate)
        {
            if (result != null && ReferenceEquals(this.layout, layout) && ReferenceEquals(this.pads, pads)
                && ReferenceEquals(this.items, items) && this.x == x && this.z == z) return result;
            var next = new Cuboidf[pads.Length + layout.Length + items.Length];
            int i = 0;
            foreach (var source in new[] { pads, layout, items })
                foreach (var box in source)
                {
                    if (x == 0 && z == 0) { next[i++] = box; continue; }
                    var moved = box.OffsetCopy(x, 0, z);
                    next[i++] = box is CuboidfWithId id
                        ? new CuboidfWithId(moved.X1, moved.Y1, moved.Z1, moved.X2, moved.Y2, moved.Z2) { Id = id.Id }
                        : moved;
                }
            this.layout = layout; this.pads = pads; this.items = items;
            this.x = x; this.z = z;
            return result = next;
        }
    }
}
