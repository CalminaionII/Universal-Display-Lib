using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace UniversalDisplayLib
{
    /// <summary>
    /// An AutoBoxes owner some of whose filler spaces are PART OF the block itself - a tall window's upper blocks - rather
    /// than spaces a moving leaf only swings into. The lib's filler copy (src/autoboxes/AutoBoxesFiller.cs, "UDL:") then
    /// seals rooms and holds torches there exactly as the owner's own block does, open or shut (2026-10-07).
    /// </summary>
    public interface IAutoBoxesPartOfOwner
    {
        /// <summary>Whether <paramref name="cell"/> (a world position) is one of the owner's own spaces.</summary>
        bool IsPartOfOwner(BlockPos cell);
    }

    /// <summary>
    /// LIGHT in the owner's own spaces (2026-10-07, the old reachcolumn / reachcolumnclear pair brought back). Light is a
    /// block property the engine reads without asking, so the filler cannot answer it the way it answers rooms - instead
    /// there are two fillers, the clear one (assets blocktypes/autoboxesfiller.json) and a dark one
    /// (autoboxesfillerdark.json, same classes). Each filler in the owner's own spaces is swapped to match the OWNER'S
    /// block: dark while that absorbs light (a shut External Shutter), clear otherwise (glass windows, any open state).
    /// Spaces a leaf only swings into always stay clear.
    /// </summary>
    public static class PartOfOwnerLight
    {
        public const string DarkCode = "universaldisplaylib:autoboxesfillerdark";

        /// <summary>Server: call after AutoBoxes.Sync and after the owner's block changed (open/closed variant, wrench swap).</summary>
        public static void Match(IWorldAccessor world, BlockPos owner, IAutoBoxesPartOfOwner own, IEnumerable<(int x, int y, int z)> held)
        {
            if (world?.Side != EnumAppSide.Server || owner == null || own == null || held == null) return;
            Block clear = world.GetBlock(new AssetLocation(AutoBoxes.FillerCode));
            Block dark = world.GetBlock(new AssetLocation(DarkCode));
            if (clear == null || dark == null) return;

            bool ownerDark = world.BlockAccessor.GetBlock(owner).LightAbsorption > 0;
            IBlockAccessor relight = null;
            foreach (var o in held)
            {
                var pos = owner.AddCopy(o.x, o.y, o.z);
                Block now = world.BlockAccessor.GetBlock(pos);
                if (now is not BlockAutoBoxesFiller) continue;
                Block want = ownerDark && own.IsPartOfOwner(pos) ? dark : clear;
                if (now.Id == want.Id) continue;
                // relight on, as the window's own open/closed swap does (BEUniversalDisplay.SyncStateVariant); the
                // exchange keeps the filler's block entity, so it keeps its owner
                relight ??= world.GetBlockAccessor(false, true, false);
                relight.ExchangeBlock(want.Id, pos);
            }
        }
    }
}
