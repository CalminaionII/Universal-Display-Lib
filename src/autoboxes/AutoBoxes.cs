using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace UniversalDisplayLib;   // CHANGE: the namespace your own code files use (the word after "namespace" at their top)

// ─────────────────────────────────────────────────────────────────────────────────────────────
// AUTO BOXES - a block bigger than one block, clickable and solid in every block it reaches.
//
// The game only tests a block's box where your line of sight passes through that block's own
// space, so the part of a big block that reaches into the next space is drawn but cannot be
// clicked or bumped into. Auto Boxes puts an invisible FILLER block in every EMPTY space the box
// reaches; the filler shows the whole outline, is solid there, and passes the click, the break,
// the name, the info and the help on to the block it belongs to. A space that already holds
// something is left alone.
//
// Files: AutoBoxes.cs (this - the tools), AutoBoxesFiller.cs (the filler block), AutoBoxesMeasure.cs
// (the box from a block's model) + assets blocktypes/autoboxesfiller.json. Guide: AUTOBOXES-GUIDE.md.
// From Taxidermy by Calminaion - free to copy into any mod.
// ─────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>A block entity whose box reaches past its block. The filler asks it for its share of each space.</summary>
public interface IAutoBoxesOwner
{
    /// <summary>The owner's boxes in <paramref name="cell"/> (a world position), in that cell's own 0..1 frame. Empty when it has none there.</summary>
    Cuboidf[] AutoBoxesAt(BlockPos cell);

    /// <summary>What is SOLID in <paramref name="cell"/> - that space's own piece of the box (collision is tested space by space); empty for walk-through.</summary>
    Cuboidf[] AutoCollisionAt(BlockPos cell);
}

public static class AutoBoxes
{
    /// <summary>The filler's block code (assets blocktypes/autoboxesfiller.json).</summary>
    public const string FillerCode = "universaldisplaylib:autoboxesfiller";   // CHANGE: your mod id (lowercase) before the colon

    /// <summary>Less than this into a space is a graze: that strip stays on the block it came from and claims nothing.</summary>
    public const float Slack = 1 / 32f;
    const float Eps = 1e-4f;

    /// <summary>
    /// A box in its owner's block frame cut into the spaces it reaches: offset from the owner ->
    /// that space's share, in the space's own 0..1 frame. A reach of less than Slack into a space
    /// is not cut off there - it stays on the space it came from, just past the edge.
    /// </summary>
    public static Dictionary<(int x, int y, int z), Cuboidf> Cut(Cuboidf box)
    {
        var pieces = new Dictionary<(int, int, int), Cuboidf>();
        if (box == null) return pieces;
        var (xLo, xHi) = Cells(box.X1, box.X2);
        var (yLo, yHi) = Cells(box.Y1, box.Y2);
        var (zLo, zHi) = Cells(box.Z1, box.Z2);
        for (int cx = xLo; cx < xHi; cx++)
        for (int cy = yLo; cy < yHi; cy++)
        for (int cz = zLo; cz < zHi; cz++)
        {
            float a1 = (cx == xLo ? box.X1 : Math.Max(box.X1, cx)) - cx, a2 = (cx == xHi - 1 ? box.X2 : Math.Min(box.X2, cx + 1)) - cx;
            float b1 = (cy == yLo ? box.Y1 : Math.Max(box.Y1, cy)) - cy, b2 = (cy == yHi - 1 ? box.Y2 : Math.Min(box.Y2, cy + 1)) - cy;
            float c1 = (cz == zLo ? box.Z1 : Math.Max(box.Z1, cz)) - cz, c2 = (cz == zHi - 1 ? box.Z2 : Math.Min(box.Z2, cz + 1)) - cz;
            if (a2 - a1 <= Eps || b2 - b1 <= Eps || c2 - c1 <= Eps) continue;
            pieces[(cx, cy, cz)] = new Cuboidf(a1, b1, c1, a2, b2, c2);
        }
        return pieces;
    }

    static (int lo, int hi) Cells(float v1, float v2)
    {
        int lo = (int)Math.Floor(v1 + Slack), hi = (int)Math.Ceiling(v2 - Slack);
        if (hi > lo) return (lo, hi);
        int mid = (int)Math.Floor((v1 + v2) / 2);
        return (mid, mid + 1);
    }

    /// <summary>
    /// The whole box as seen from the space at <paramref name="offset"/> (that space's own frame) - for an
    /// owner that wants the full outline from every part instead of each space's own piece.
    /// </summary>
    public static Cuboidf Whole(Cuboidf box, (int x, int y, int z) offset) =>
        box.OffsetCopy(-offset.x, -offset.y, -offset.z);

    /// <summary>The space at <paramref name="offset"/>'s own piece of the box (for collision), or nothing.</summary>
    public static Cuboidf[] PieceAt(Cuboidf box, (int x, int y, int z) offset) =>
        box != null && Cut(box).TryGetValue(offset, out var piece) ? [piece] : [];

    /// <summary>
    /// Server: makes the fillers round <paramref name="owner"/> match <paramref name="wanted"/>
    /// (offsets; the owner's own space is skipped). A filler goes only into an EMPTY space - air, no
    /// fluid; a space with anything in it, or another owner's filler, stays as it is and that part
    /// of the box is simply not clickable. Fillers no longer wanted are removed. Returns the
    /// offsets that now hold this owner's filler - keep it with the owner and pass it back next time.
    /// </summary>
    public static List<(int x, int y, int z)> Sync(IWorldAccessor world, BlockPos owner, IEnumerable<(int x, int y, int z)> wanted, IEnumerable<(int x, int y, int z)> held)
    {
        var want = new HashSet<(int, int, int)>(wanted.Where(o => o != (0, 0, 0)));
        var result = new List<(int, int, int)>();
        foreach (var o in held ?? [])
        {
            if (want.Contains(o) && IsOurs(world, owner, o)) { result.Add(o); continue; }
            Release(world, owner, o);
        }
        Block filler = world.GetBlock(new AssetLocation(FillerCode));
        if (filler == null) return result;
        foreach (var o in want)
        {
            if (result.Contains(o)) continue;
            var pos = owner.AddCopy(o.Item1, o.Item2, o.Item3);
            var ba = world.BlockAccessor;
            // UDL: another owner's filler already here (two shutters folding into one gap) - share the space; the window's
            // room check before opening made sure the two do not overlap here (Calm, 2026-10-07, "like we had before")
            if (ba.GetBlockEntity(pos) is BEAutoBoxesFiller shared && shared.Owner != null && !shared.Owner.Equals(owner)
                && ba.GetBlockEntity(shared.Owner) is IAutoBoxesOwner)
            {
                shared.AddSharer(owner);
                result.Add(o);
                continue;
            }
            if (ba.GetBlock(pos, BlockLayersAccess.Solid).Id != 0 || ba.GetBlock(pos, BlockLayersAccess.Fluid).Id != 0) continue;
            ba.SetBlock(filler.Id, pos);
            if (ba.GetBlockEntity(pos) is BEAutoBoxesFiller be) be.SetOwner(owner);
            result.Add(o);
        }
        return result;
    }

    // ── ADVANCED - owners you cannot change (another mod's or the game's blocks) ─────────────────────────────────────────────────────────────────────────
    // A vanilla block entity cannot implement the interface, so its fillers CARRY the box (the
    // owner's whole box, in the owner's frame) and ask OwnerStillValid whether the owner still
    // holds the thing the box was measured for. The owner's own outline comes from the mod's own
    // hook into that block (Taxidermy does this for heads and pelts on the ground).

    /// <summary>For owners without the interface: does the block at this position still hold what the carried box was made for? Unset = it exists.</summary>
    public static System.Func<IBlockAccessor, BlockPos, bool> OwnerStillValid;

    /// <summary>Both sides: a filler carrying a box was loaded or synced (owner, whole box, solid) - lets the mod know an owner's box after a restart.</summary>
    public static System.Action<BlockPos, Cuboidf, bool> CarriedSeen;

    internal static bool Valid(IBlockAccessor ba, BlockPos owner) =>
        ba.GetBlockEntity(owner) is IAutoBoxesOwner || (OwnerStillValid?.Invoke(ba, owner) ?? ba.GetBlockEntity(owner) != null);

    /// <summary>
    /// Server: fillers for an owner that cannot implement the interface. Each filler gets the whole
    /// box to carry. The owner keeps no list - its fillers are found by looking round it (up to
    /// <paramref name="reach"/> blocks), so a box that shrinks or moves leaves nothing behind.
    /// </summary>
    public static void SyncCarried(IWorldAccessor world, BlockPos owner, Cuboidf box, bool solid = false, int reach = 7)
    {
        var want = new HashSet<(int, int, int)>(Cut(box).Keys.Where(o => o != (0, 0, 0)));
        var ba = world.BlockAccessor;
        for (int x = -reach; x <= reach; x++)
        for (int y = -reach; y <= reach; y++)
        for (int z = -reach; z <= reach; z++)
        {
            if (want.Contains((x, y, z)) || (x, y, z) == (0, 0, 0)) continue;
            Release(world, owner, (x, y, z));
        }
        Block filler = world.GetBlock(new AssetLocation(FillerCode));
        if (filler == null) return;
        foreach (var o in want)
        {
            var pos = owner.AddCopy(o.Item1, o.Item2, o.Item3);
            if (ba.GetBlockEntity(pos) is BEAutoBoxesFiller mine && mine.Owner?.Equals(owner) == true)
            {
                mine.SetOwner(owner, box, solid);
                continue;
            }
            if (ba.GetBlock(pos, BlockLayersAccess.Solid).Id != 0 || ba.GetBlock(pos, BlockLayersAccess.Fluid).Id != 0) continue;
            ba.SetBlock(filler.Id, pos);
            if (ba.GetBlockEntity(pos) is BEAutoBoxesFiller be) be.SetOwner(owner, box, solid);
        }
    }

    /// <summary>Server: removes every filler this owner holds (the owner was broken or picked up).</summary>
    public static void ReleaseAll(IWorldAccessor world, BlockPos owner, IEnumerable<(int x, int y, int z)> held)
    {
        foreach (var o in held ?? []) Release(world, owner, o);
    }

    static bool IsOurs(IWorldAccessor world, BlockPos owner, (int x, int y, int z) o) =>
        world.BlockAccessor.GetBlockEntity(owner.AddCopy(o.x, o.y, o.z)) is BEAutoBoxesFiller be && (be.Owner?.Equals(owner) == true || be.HasSharer(owner));   // UDL: sharers too

    static void Release(IWorldAccessor world, BlockPos owner, (int x, int y, int z) o)
    {
        var pos = owner.AddCopy(o.x, o.y, o.z);
        if (world.BlockAccessor.GetBlockEntity(pos) is not BEAutoBoxesFiller be) return;
        // UDL: a shared space - a sharer just leaves it; the owner hands the filler on to the next sharer
        if (be.HasSharer(owner)) { be.RemoveSharer(owner); return; }
        if (be.Owner?.Equals(owner) != true) return;
        BlockPos next = be.TakeFirstSharer();
        if (next == null) { world.BlockAccessor.SetBlock(0, pos); return; }
        Block clear = world.GetBlock(new AssetLocation(FillerCode));   // a dark filler (a shut tall shutter's own space) goes back to clear
        if (clear != null && world.BlockAccessor.GetBlock(pos).Id != clear.Id) world.BlockAccessor.ExchangeBlock(clear.Id, pos);
        (world.BlockAccessor.GetBlockEntity(pos) as BEAutoBoxesFiller)?.SetOwner(next);
    }

    /// <summary>Saves held offsets as "x,y,z;x,y,z".</summary>
    public static string Write(IEnumerable<(int x, int y, int z)> held) => string.Join(";", (held ?? []).Select(o => $"{o.x},{o.y},{o.z}"));

    public static List<(int x, int y, int z)> Read(string text)
    {
        var list = new List<(int, int, int)>();
        foreach (var entry in (text ?? "").Split(';'))
        {
            var p = entry.Split(',');
            if (p.Length == 3 && int.TryParse(p[0], out int x) && int.TryParse(p[1], out int y) && int.TryParse(p[2], out int z)) list.Add((x, y, z));
        }
        return list;
    }
}
