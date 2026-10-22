using System.Collections.Generic;
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UniversalDisplayLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace UniversalDisplayLib.OptionalDisplays
{
    /// <summary>
    /// ITEMS SUNK INTO A CHISELLED BLOCK STAY AIMABLE. A pile on a chiselled top draws its items down inside the
    /// chiselled block's space (the lib's followSupport), but the game's aiming only tests a block's own boxes when the
    /// ray passes through that block's space - so aimed at from the side, the chiselled block won and the item could only
    /// be reached from its top (Calm, 2026-10-16: "you have to look at the top of the placed thing").
    ///
    /// So the chiselled block carries the pile's boxes as GUESTS - the lib's own mechanism between its blocks
    /// (ids "guest:0,1,0:&lt;id&gt;", read by BEUniversalDisplay.GuestSelection / WindowSelection) - and hands a click or a
    /// break on one to the pile. Its own voxel boxes stay first and unchanged.
    ///
    /// NOT WHILE A CHISEL IS HELD (client): the chisel works out the voxel from the aimed box's index, so it must only ever
    /// see the chiselled block's own boxes. The server always adds them; the voxel indices are the same either way, the
    /// guests only ever come after them.
    /// </summary>
    public static class ChiselGuests
    {
        const string HarmonyId = "grounddisplay.chisel-guests";
        static Harmony harmony;
        static int users;
        static ICoreClientAPI capi;

        /// <summary>own boxes array -> (the pile's boxes it was built from, the combined array)</summary>
        static readonly ConditionalWeakTable<Cuboidf[], Tuple<Cuboidf[], Cuboidf[]>> cache = new();

        public static void Apply(ICoreAPI api)
        {
            if (api is ICoreClientAPI c) capi = c;
            if (users++ > 0) return;
            harmony = new Harmony(HarmonyId);
            MethodInfo sel = typeof(BlockMicroBlock).GetMethod(nameof(BlockMicroBlock.GetSelectionBoxes), new[] { typeof(IBlockAccessor), typeof(BlockPos) });
            MethodInfo interact = typeof(BlockChisel).GetMethod(nameof(BlockChisel.OnBlockInteractStart), new[] { typeof(IWorldAccessor), typeof(IPlayer), typeof(BlockSelection) });
            MethodInfo broken = typeof(BlockMicroBlock).GetMethod(nameof(BlockMicroBlock.OnBlockBroken), new[] { typeof(IWorldAccessor), typeof(BlockPos), typeof(IPlayer), typeof(float) });
            var me = typeof(ChiselGuests);
            if (!SafePatch.Group(harmony, api.Logger, "ground items sunk into chiselled tops",
                    "those items can only be aimed at from above",
                    SafePatch.On(sel, "BlockMicroBlock.GetSelectionBoxes").After(me, nameof(SelectionPostfix)),
                    SafePatch.On(interact, "BlockChisel.OnBlockInteractStart").Before(me, nameof(InteractPrefix)),
                    SafePatch.On(broken, "BlockMicroBlock.OnBlockBroken").Before(me, nameof(BrokenPrefix))))
                return;

            // What the info box and the help say: the pile's (and its aimed item's), as the lib does for items under a
            // shelf (Calm: "It is targetting it but like the issue when stacking before with a shelf on top no info").
            MethodInfo name = typeof(BlockMicroBlock).GetMethod(nameof(Block.GetPlacedBlockName), new[] { typeof(IWorldAccessor), typeof(BlockPos) });
            MethodInfo info = typeof(Block).GetMethod(nameof(Block.GetPlacedBlockInfo), new[] { typeof(IWorldAccessor), typeof(BlockPos), typeof(IPlayer) });
            MethodInfo help = typeof(BlockChisel).GetMethod(nameof(Block.GetPlacedBlockInteractionHelp), new[] { typeof(IWorldAccessor), typeof(BlockSelection), typeof(IPlayer) });
            SafePatch.Group(harmony, api.Logger, "the name and help of ground items sunk into chiselled tops",
                "aiming at one shows the chiselled block's name and help instead of the item's",
                SafePatch.On(name, "BlockMicroBlock.GetPlacedBlockName").Before(me, nameof(NamePrefix)),
                SafePatch.On(info, "Block.GetPlacedBlockInfo").Before(me, nameof(InfoPrefix)),
                SafePatch.On(help, "BlockChisel.GetPlacedBlockInteractionHelp").Before(me, nameof(HelpPrefix)));
        }

        /// <summary>Client: the local player's aim at <paramref name="pos"/>, as the pile behind a guest box sees it - or null.</summary>
        static BlockSelection LocalAim(IWorldAccessor world, BlockPos pos)
        {
            if (world?.Side != EnumAppSide.Client || pos == null) return null;
            BlockSelection sel = (world.Api as ICoreClientAPI)?.World.Player?.CurrentBlockSelection;
            if (sel?.Position == null || !sel.Position.Equals(pos)) return null;
            return PileSelection(world, sel);
        }

        static bool NamePrefix(IWorldAccessor world, BlockPos pos, ref string __result)
        {
            BlockSelection pile = LocalAim(world, pos);
            if (pile == null) return true;
            __result = world.BlockAccessor.GetBlock(pile.Position).GetPlacedBlockName(world, pile.Position);
            return false;
        }

        static bool InfoPrefix(Block __instance, IWorldAccessor world, BlockPos pos, IPlayer forPlayer, ref string __result)
        {
            if (__instance is not BlockMicroBlock) return true;
            BlockSelection pile = LocalAim(world, pos);
            if (pile == null) return true;
            __result = world.BlockAccessor.GetBlock(pile.Position).GetPlacedBlockInfo(world, pile.Position, forPlayer);
            return false;
        }

        static bool HelpPrefix(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer, ref WorldInteraction[] __result)
        {
            BlockSelection pile = PileSelection(world, selection);
            if (pile == null) return true;
            __result = world.BlockAccessor.GetBlock(pile.Position).GetPlacedBlockInteractionHelp(world, pile, forPlayer) ?? Array.Empty<WorldInteraction>();
            return false;
        }

        public static void Remove()
        {
            if (--users > 0) return;
            harmony?.UnpatchAll(HarmonyId);
            harmony = null;
            capi = null;
        }

        static void SelectionPostfix(IBlockAccessor blockAccessor, BlockPos pos, ref Cuboidf[] __result)
        {
            try
            {
                if (__result == null || pos == null || blockAccessor == null) return;
                if (capi != null && GroundStorageHook.IsChiselling(capi.World.Player?.InventoryManager?.ActiveHotbarSlot, capi.World.Player)) return;
                BlockPos above = pos.UpCopy();
                Block pile = blockAccessor.GetBlock(above);
                if (!DisplayPlacementSystem.IsPile(pile?.Code)) return;
                if (blockAccessor.GetBlockEntity(above) is not BEUniversalDisplay be) return;

                Cuboidf[] guests = be.GetSelectionBoxes();
                if (guests == null || guests.Length == 0) return;
                if (cache.TryGetValue(__result, out var hit) && ReferenceEquals(hit.Item1, guests)) { __result = hit.Item2; return; }

                string prefix = BEUniversalDisplay.GuestBoxPrefix + "0,1,0:";
                Cuboidf[] combined = __result.Concat(guests
                        .OfType<CuboidfWithId>()
                        .Where(g => g.Id != null)
                        .Select(g => (Cuboidf)new CuboidfWithId(g.X1, g.Y1 + 1f, g.Z1, g.X2, g.Y2 + 1f, g.Z2) { Id = prefix + g.Id }))
                    .ToArray();
                cache.AddOrUpdate(__result, Tuple.Create(guests, combined));
                __result = combined;
            }
            catch (Exception)
            {
                // aiming is never worth a crash - the chiselled block keeps its own boxes
            }
        }

        /// <summary>The pile selection behind an aim at one of the guest boxes, or null.</summary>
        static BlockSelection PileSelection(IWorldAccessor world, BlockSelection sel)
        {
            if (sel?.Position == null) return null;
            string id = sel.SelectionBoxId;
            if (id == null && sel.SelectionBoxIndex >= 0)
            {
                Cuboidf[] boxes = world.BlockAccessor.GetBlock(sel.Position)?.GetSelectionBoxes(world.BlockAccessor, sel.Position);
                if (boxes != null && sel.SelectionBoxIndex < boxes.Length) id = (boxes[sel.SelectionBoxIndex] as CuboidfWithId)?.Id;
            }
            if (!BEUniversalDisplay.GuestSelection(sel, id, out BlockSelection forwarded)) return null;
            Block pile = world.BlockAccessor.GetBlock(forwarded.Position);
            return DisplayPlacementSystem.IsPile(pile?.Code) ? forwarded : null;
        }

        static bool InteractPrefix(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref bool __result)
        {
            BlockSelection forwarded = PileSelection(world, blockSel);
            if (forwarded == null) return true;
            __result = world.BlockAccessor.GetBlock(forwarded.Position).OnBlockInteractStart(world, byPlayer, forwarded);
            return false;
        }

        static bool BrokenPrefix(IWorldAccessor world, BlockPos pos, IPlayer byPlayer)
        {
            BlockSelection sel = byPlayer?.CurrentBlockSelection;
            if (sel?.Position == null || !sel.Position.Equals(pos)) return true;
            BlockSelection forwarded = PileSelection(world, sel);
            if (forwarded == null) return true;
            // the pile goes, not the chiselled block under it (if the player may break the pile)
            if (world.Side == EnumAppSide.Server
                && world.Claims.TestAccess(byPlayer, forwarded.Position, EnumBlockAccessFlags.BuildOrBreak) == EnumWorldAccessResponse.Granted)
                world.BlockAccessor.BreakBlock(forwarded.Position, byPlayer);
            return false;
        }
    }
}
