using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace UniversalDisplayLib.Reach
{
    /// <summary>
    /// Converts tall windows built by the OLD chain system (Window Display 1.0.5 and earlier, lib 1.1.3 - one window block
    /// entity per height) as their chunks load. Rebuilt on the AutoBoxes Kit 2026-10-06 from the 2026-10-05 ReachLegacy.
    ///
    /// The block codes never changed: an old chain's BOTTOM block has the code the one-block tall window now uses, so it
    /// simply loads as one. Its upper segments are still in the world under their old codes, which Window Display marks
    /// <c>"legacySegment": true</c> (no shape, no item). Each is cleared (anything stored in it dropped - upper segments
    /// never had surfaces, so nothing should be) and the window below puts the kit's fillers there instead.
    ///
    /// Both ends start it, because either can load first (a window across a chunk edge); unloaded chunks are waited
    /// for, never treated as "nothing there". Every window also re-syncs its fillers once on load, which repairs any
    /// that a world edit or an older test build left missing.
    /// </summary>
    public static class LegacyTallWindows
    {
        const int FirstLookMs = 500, RetryMs = 2000, MaxBelow = 7;

        public static bool IsLegacySegment(Block block) => block?.Attributes?["legacySegment"].AsBool(false) == true;

        /// <summary>Server, from BEUniversalDisplay.Initialize.</summary>
        public static void Schedule(BEUniversalDisplay be)
        {
            if (be?.Api?.Side != EnumAppSide.Server) return;
            if (IsLegacySegment(be.Block)) be.RegisterDelayedCallback(_ => ConvertSegment(be), FirstLookMs);
            else be.RegisterDelayedCallback(_ => Adopt(be), FirstLookMs);
        }

        /// <summary>Clears any old segment in the window's cells, then lets the window place its fillers (AutoBoxes.Sync).</summary>
        public static void Adopt(BEUniversalDisplay window)
        {
            IBlockAccessor ba = window?.Api?.World?.BlockAccessor;
            if (ba == null || ba.GetBlockEntity(window.Pos) != window) return;

            BlockPos[] cells = window.CoveredCells().ToArray();
            if (cells.Any(at => ba.GetChunkAtBlockPos(at) == null))
            {
                window.RegisterDelayedCallback(_ => Adopt(window), RetryMs);
                return;
            }
            bool changed = false;
            foreach (BlockPos at in cells)
            {
                if (!IsLegacySegment(ba.GetBlock(at))) continue;
                DropContents(window.Api.World, at);
                ba.SetBlock(0, at);
                changed = true;
            }
            // fillers: a converted window gets them here; any other only if one is missing (nothing sent otherwise)
            if (changed || cells.Any(at => !(ba.GetBlockEntity(at) is BEAutoBoxesFiller f && f.Owner?.Equals(window.Pos) == true)))
            {
                window.AutoBoxesAngleChanged();
                window.MarkDirty();
            }
        }

        /// <summary>An old upper segment: hand it to the window below it, or - a broken chain with no window under it - remove it.</summary>
        public static void ConvertSegment(BEUniversalDisplay segment)
        {
            IWorldAccessor world = segment?.Api?.World;
            IBlockAccessor ba = world?.BlockAccessor;
            if (ba == null || ba.GetBlockEntity(segment.Pos) != segment) return;

            for (int dy = 1; dy <= MaxBelow; dy++)
            {
                BlockPos below = segment.Pos.DownCopy(dy);
                if (ba.GetChunkAtBlockPos(below) == null)
                {
                    segment.RegisterDelayedCallback(_ => ConvertSegment(segment), RetryMs);
                    return;
                }
                if (IsLegacySegment(ba.GetBlock(below))) continue;
                if (ba.GetBlockEntity(below) is BEUniversalDisplay window && window.CoveredCells().Any(at => at.Equals(segment.Pos)))
                {
                    Adopt(window);
                    return;
                }
                break;
            }

            world.Logger.Notification("[UniversalDisplayLib] Removed an old tall-window segment with no window below it at {0}.", segment.Pos);
            DropContents(world, segment.Pos);
            ba.SetBlock(0, segment.Pos);
        }

        static void DropContents(IWorldAccessor world, BlockPos at)
        {
            if (world.BlockAccessor.GetBlockEntity(at) is BEUniversalDisplay old && !old.Inventory.Empty)
            {
                old.Inventory.DropAll(at.ToVec3d().Add(0.5, 0.5, 0.5));
            }
        }
    }
}
