using System.Collections.Generic;
using System.Linq;
using System;
using UniversalDisplayLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace UniversalDisplayLib.OptionalDisplays
{
    /// <summary>
    /// Client: the placing ghost (and Sprint / Sneak + wheel) on bare ground, before a pile exists. Answers the lib's
    /// PlacementGhostRenderer.BareGhostSources with a phantom pile where the new one would go - the same checks as
    /// GroundStorageHook's step 3 - or with the real pile when the aim is the top of a block that already has one.
    /// Shows while ground placement is toggled on (or, in the Shift / Ctrl modes, while either is held).
    /// </summary>
    public static class GroundGhost
    {
        static BEUniversalDisplay phantom;
        static long phantomMade;

        public static void Register() => PlacementGhostRenderer.BareGhostSources.Add(Source);
        public static void Unregister() { PlacementGhostRenderer.BareGhostSources.Remove(Source); phantom = null; lastAsk = default; lastAnswer = default; }

        static bool Source(ICoreClientAPI capi, ItemSlot held, BlockSelection aimed,
                           out BEUniversalDisplay target, out BlockSelection onTarget)
        {
            target = null; onTarget = null;
            if (!GroundSettings.IsEnabled(capi.World)) return false;
            EntityControls controls = capi.World.Player?.Entity?.Controls;
            if (controls == null) return false;
            // Toggle: while it is on. Shift / Ctrl modes: while either is held (one may be turning / raising the item).
            bool toggle = GroundSettings.Mode(capi.World) == "Toggle";
            if (toggle ? !GroundToggle.ClientOn : !(controls.ShiftKey || controls.CtrlKey)) return false;
            return TargetThisFrame(capi, held, aimed, toggle, out target, out onTarget);
        }

        static (long Ms, ItemStack Held, BlockPos Pos, double X, double Y, double Z, BlockFacing Face, bool Any) lastAsk;
        static (bool Found, BEUniversalDisplay Target, BlockSelection On) lastAnswer;

        /// <summary>
        /// <see cref="Target"/> for the frame being drawn. The ghost and a chiselled block's outline (ChiselShelfSystem)
        /// both ask it every frame with the same aim, and on a carved block the answer is not cheap - so the second ask in
        /// the same game millisecond, same aim, held stack and mode gets the first answer (code check 2026-10-22).
        /// Main thread only. A click asks <see cref="Target"/> itself.
        /// </summary>
        public static bool TargetThisFrame(ICoreClientAPI capi, ItemSlot held, BlockSelection aimed, bool anyDisplayable,
                                           out BEUniversalDisplay target, out BlockSelection onTarget)
        {
            var hit = aimed?.HitPosition;
            (long Ms, ItemStack Held, BlockPos Pos, double X, double Y, double Z, BlockFacing Face, bool Any) ask =
                (capi.World.ElapsedMilliseconds, held?.Itemstack, aimed?.Position, hit?.X ?? double.NaN, hit?.Y ?? double.NaN,
                 hit?.Z ?? double.NaN, aimed?.Face, anyDisplayable);
            if (ask.Held != null && ask.Pos != null && ask.Ms == lastAsk.Ms && ReferenceEquals(ask.Held, lastAsk.Held)
                && ask.Pos.Equals(lastAsk.Pos) && ask.X == lastAsk.X && ask.Y == lastAsk.Y && ask.Z == lastAsk.Z
                && ask.Face == lastAsk.Face && ask.Any == lastAsk.Any)
            {
                target = lastAnswer.Target; onTarget = lastAnswer.On;
                return lastAnswer.Found;
            }
            bool found = Target(capi, held, aimed, anyDisplayable, out target, out onTarget);
            lastAsk = ask;
            lastAnswer = (found, target, onTarget);
            return found;
        }

        /// <summary>
        /// Where the held item would go for this aim: a phantom pile on bare ground, or the real pile when the aim is the
        /// top of the block under one. The ghost and the toggle's click (GroundToggle) both ask this, so what is shown is
        /// what is placed. <paramref name="anyDisplayable"/>: anything the lib can display (the toggle), else only the
        /// layouts the game's own ground storage hands over.
        /// </summary>
        public static bool Target(ICoreClientAPI capi, ItemSlot held, BlockSelection aimed, bool anyDisplayable,
                                  out BEUniversalDisplay target, out BlockSelection onTarget)
        {
            target = null; onTarget = null;
            if (aimed?.Position == null || aimed.HitPosition == null || aimed.Face != BlockFacing.UP || held?.Itemstack == null) return false;
            if (GroundStorageHook.IsChiselling(held, capi.World.Player)) return false;

            if (BEBehaviorChiselShelf.TryFloor(capi.World, aimed, out var carved, out float floor)
                && GroundStorageHook.TakesOver(held, anyDisplayable))
                return carved.PreviewFloor(capi, aimed, floor, out target, out onTarget);

            // An aim ON a display block (a pile, a window, a shelf) or reaching one through another block (a chiselled
            // block carrying a pile's boxes, a tall stack's filler) is that block's own right-click - place, stack, take,
            // pour into a pot, fill a bowl (Calm: "It still needs to be able to take liquids like the lib already does").
            // Items on a pile are solid, so without this the top of one read as ground to start a new pile on.
            IBlockAccessor aimBa = capi.World.BlockAccessor;
            if (aimBa.GetBlockEntity(aimed.Position)?.GetBehavior<BEBehaviorChiselShelf>()?.Display is { } embedded
                && ChiselShelfSystem.EmbeddedAim(capi.World, aimed) != null) return false;
            if (aimBa.GetBlockEntity(aimed.Position) is BEUniversalDisplay) return false;
            BlockSelection owner = BEUniversalDisplay.WindowSelection(aimBa, aimed);
            if (owner?.Position != null && !owner.Position.Equals(aimed.Position)) return false;

            if (!DisplayFeatures.Ground(capi)) return false;
            Block pile = capi.World.GetBlock(DisplayPlacementSystem.PileCode);
            if (pile == null) return false;
            IBlockAccessor ba = capi.World.BlockAccessor;
            BlockPos above = aimed.Position.UpCopy();
            var hit = new Vec3d(aimed.HitPosition.X, 0, aimed.HitPosition.Z);

            // A pile already there (the aim landed on the top of the block under it): its own pad.
            if (DisplayPlacementSystem.IsPile(ba.GetBlock(above).Code) && ba.GetBlockEntity(above) is BEUniversalDisplay real)
            {
                target = real;
                onTarget = real.PadSelectionAt(hit);
                return onTarget != null;
            }

            if (!GroundStorageHook.TakesOver(held, anyDisplayable)) return false;
            if (ba.GetBlockEntity(aimed.Position) is BlockEntityGroundStorage || ba.GetBlockEntity(above) is BlockEntityGroundStorage) return false;
            Block onBlock = ba.GetBlock(aimed.Position);
            if (!onBlock.CanAttachBlockAt(ba, pile, aimed.Position, BlockFacing.UP) && !GroundStorageHook.TopUnderAim(ba, onBlock, aimed)) return false;
            if (ba.GetBlock(above).Replaceable < 6000) return false;

            // One phantom, remade when the spot changes or every second (the support under it may have been chiselled).
            long now = capi.World.ElapsedMilliseconds;
            if (phantom == null || !phantom.Pos.Equals(above) || now - phantomMade > 1000)
            {
                phantom = BEUniversalDisplay.Phantom(capi, pile, above);
                phantomMade = now;
            }
            target = phantom;
            onTarget = phantom.PadSelectionAt(hit);
            return onTarget != null;
        }
    }
}
