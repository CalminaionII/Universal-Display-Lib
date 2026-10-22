using System.Collections.Generic;
using System.Linq;
using System;
using System.Reflection;
using HarmonyLib;
using UniversalDisplayLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace UniversalDisplayLib.OptionalDisplays
{
    /// <summary>
    /// THE ONE HOOK. Shift + right-click with a ground-storable item normally runs the game's own
    /// <c>CollectibleBehaviorGroundStorable.Interact</c>, which puts its <c>groundstorage</c> block in the space above
    /// and stores the item on a fixed 2x2 grid. This prefix steps in first for the simple layouts and puts OUR block
    /// there instead - a Universal Display Lib floor surface - so the item goes down the lib's way: free grid, any
    /// angle, stacking, raise and lower, the placing ghost.
    ///
    /// WHAT IS LEFT TO THE GAME, on purpose (docs/ground-storage-plan.md in the lib copy): Stacking piles (ingots,
    /// planks, bricks, firewood - their own pile models and capacities), WallHalves and Messy12, pit kilns and
    /// burning piles, and every pile already in the world. Anything the lib could not display is left to the game too.
    ///
    /// The checks before placing are the game's own, in the same order (public vssurvivalmod
    /// CollectibleBehavior/BehaviorGroundStorable.cs): ground placement active (the place key toggled on, or Shift / Ctrl held - GroundSettings), a player, claims, aiming at the up face, a block
    /// below that supports it, replaceable space above.
    /// </summary>
    public static class GroundStorageHook
    {
        const string HarmonyId = "displayplacement.groundstorable";
        static Harmony harmony;
        static int users;

        public static void Apply(ICoreAPI api)
        {
            // Singleplayer runs client and server in one process: patch once, unpatch with the last side.
            if (users++ > 0) return;
            MethodInfo target = null;
            try { target = typeof(CollectibleBehaviorGroundStorable).GetMethod("Interact", BindingFlags.Public | BindingFlags.Static); }
            catch (AmbiguousMatchException) { }
            harmony = new Harmony(HarmonyId);
            SafePatch.Group(harmony, api.Logger, "the game's ground storage",
                "the Shift and Ctrl place-key modes leave ground storage to the game (the G toggle still works)",
                SafePatch.On(target, "CollectibleBehaviorGroundStorable.Interact").Before(typeof(GroundStorageHook), nameof(Prefix)));
        }

        public static void Remove()
        {
            if (--users > 0) return;
            harmony?.UnpatchAll(HarmonyId);
            harmony = null;
        }

        /// <summary>Returns false (skip the game's code) only when this mod handled the click.</summary>
        static bool Prefix(ItemSlot itemslot, EntityAgent byEntity, BlockSelection blockSel,
            ref EnumHandHandling handHandling, ref EnumHandling handling)
        {
            try
            {
                if (Handle(itemslot, byEntity, blockSel))
                {
                    handHandling = EnumHandHandling.PreventDefault;
                    handling = EnumHandling.PreventSubsequent;
                    return false;
                }
            }
            catch (Exception e)
            {
                byEntity?.World?.Logger.Error("[Display Placement] ground storage hook failed, left to the game: {0}", e);
            }
            return true;
        }

        static bool Handle(ItemSlot itemslot, EntityAgent byEntity, BlockSelection blockSel)
        {
            IWorldAccessor world = byEntity?.World;
            if (blockSel == null || world == null || itemslot?.Itemstack == null) return false;
            if (byEntity is not EntityPlayer ep) return false;
            IPlayer player = world.PlayerByUid(ep.PlayerUID);
            if (player == null) return false;
            if (IsChiselling(itemslot, player)) return false;
            if (!GroundSettings.IsEnabled(world) || !GroundSettings.PlaceActive(world, player, byEntity.Controls)) return false;
            // the block-placement route (BlockPlacementHook) already put this click's item down
            if (JustPlaced(world, player)) return true;
            return Place(world, player, itemslot, blockSel);
        }

        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> lastPlaced = new();

        /// <summary>
        /// One click, one item. A block-shaped item (crock, pot, bowl) can reach this mod by two routes - the game's
        /// ground-storage interaction and its block placement - so whichever handles the click first stamps it and the
        /// other stands down.
        /// </summary>
        public static bool JustPlaced(IWorldAccessor world, IPlayer player)
            => lastPlaced.TryGetValue(world.Side + player.PlayerUID, out long at) && Environment.TickCount64 - at < 150;

        static void Stamp(IWorldAccessor world, IPlayer player) => lastPlaced[world.Side + player.PlayerUID] = Environment.TickCount64;

        /// <summary>
        /// Puts the held item down this mod's way for an aim on a block (both routes end here, after their own checks of
        /// the place key). True = handled (the client only says so; the server does it).
        /// </summary>
        public static bool Place(IWorldAccessor world, IPlayer player, ItemSlot itemslot, BlockSelection blockSel, bool anyDisplayable = false)
        {
            if (blockSel == null || itemslot?.Itemstack == null || IsChiselling(itemslot, player)) return false;
            bool done = PlaceCore(world, player, itemslot, blockSel, anyDisplayable);
            if (done) Stamp(world, player);
            return done;
        }

        static bool PlaceCore(IWorldAccessor world, IPlayer player, ItemSlot itemslot, BlockSelection blockSel, bool anyDisplayable)
        {
            IBlockAccessor ba = world.BlockAccessor;

            var embeddedAim = ChiselShelfSystem.EmbeddedAim(world, blockSel);
            if (embeddedAim != null && BEUniversalDisplay.At(ba, embeddedAim.Position) is { IsEmbedded: true } embedded)
                return embedded.OnBlockInteractStart(world, player, embeddedAim);

            if (BEBehaviorChiselShelf.TryFloor(world, blockSel, out var carved, out float floor)
                && TakesOver(itemslot, anyDisplayable))
            {
                if (world.Side == EnumAppSide.Server) carved.PlaceOnFloor(player, blockSel, floor);
                return true;
            }

            if (!DisplayFeatures.Ground(world.Api)) return false;
            Block pile = world.GetBlock(DisplayPlacementSystem.PileCode);
            if (pile == null) return false;

            // Keep the exact item selected through a chisel/filler/siding guest. Turning
            // that click into a new pad selection can pick a different column and bypass
            // the shelf's contained liquid interaction on the intended bowl.
            BlockSelection owner = BEUniversalDisplay.WindowSelection(ba, blockSel);
            if (owner?.Position != null && !owner.Position.Equals(blockSel.Position))
            {
                if (!DisplayPlacementSystem.IsPile(ba.GetBlock(owner.Position).Code)) return false;
                return ba.GetBlock(owner.Position).OnBlockInteractStart(world, player, owner);
            }

            // 1. Aiming at one of our piles: it is the lib's click (place / stack where aimed).
            if (DisplayPlacementSystem.IsPile(ba.GetBlock(blockSel.Position).Code) && ba.GetBlockEntity(blockSel.Position) is BEUniversalDisplay aimed)
                return aimed.OnBlockInteractStart(world, player, blockSel);

            // 2. Aiming at the top of a block that already has our pile on it: place on that pile at the point aimed.
            BlockPos above = blockSel.Position.UpCopy();
            if (blockSel.Face == BlockFacing.UP
                && DisplayPlacementSystem.IsPile(ba.GetBlock(above).Code) && ba.GetBlockEntity(above) is BEUniversalDisplay onTop)
            {
                return onTop.TryPutAt(player, new Vec3d(blockSel.HitPosition.X, 0, blockSel.HitPosition.Z));
            }

            // 3. A new pile - only for the simple layouts, and only for what the lib can display.
            if (!TakesOver(itemslot, anyDisplayable)) return false;
            // never a new pile on top of a display block (a window, a shelf, an item on a pile) - that is its own click
            if (ba.GetBlockEntity(blockSel.Position) is BEUniversalDisplay) return false;
            if (blockSel.Face != BlockFacing.UP) return false;
            if (ba.GetBlockEntity(blockSel.Position) is BlockEntityGroundStorage || ba.GetBlockEntity(above) is BlockEntityGroundStorage) return false;
            if (!world.Claims.TryAccess(player, blockSel.Position, EnumBlockAccessFlags.BuildOrBreak)) return false;
            Block onBlock = ba.GetBlock(blockSel.Position);
            if (!onBlock.CanAttachBlockAt(ba, pile, blockSel.Position, BlockFacing.UP)
                && !TopUnderAim(ba, onBlock, blockSel)) return false;
            if (ba.GetBlock(above).Replaceable < 6000) return false;

            // The client only says "handled" - the server makes the pile and places the item, and the sync brings both.
            if (world.Side != EnumAppSide.Server) return true;

            ba.SetBlock(pile.Id, above);
            if (ba.GetBlockEntity(above) is BEUniversalDisplay made
                && made.TryPutAt(player, new Vec3d(blockSel.HitPosition.X, 0, blockSel.HitPosition.Z))
                && !made.Inventory.Empty)
            {
                return true;
            }

            // Nothing went down (no room, refused): no empty pile left behind.
            ba.SetBlock(0, above);
            return true;
        }

        /// <summary>
        /// A slab or a chiselled top: the aim lands on the top of one of the block's own collision boxes. The pile then goes
        /// in the space above and its items sink onto that top (the lib's followSupport).
        /// </summary>
        public static bool TopUnderAim(IBlockAccessor ba, Block onBlock, BlockSelection sel)
        {
            Cuboidf[] boxes = onBlock.GetCollisionBoxes(ba, sel.Position);
            if (boxes == null) return false;
            Vec3d hit = sel.HitPosition;
            foreach (Cuboidf b in boxes)
                if (b != null && hit.X >= b.X1 && hit.X <= b.X2 && hit.Z >= b.Z1 && hit.Z <= b.Z2 && Math.Abs(hit.Y - b.Y2) < 0.01)
                    return true;
            return false;
        }

        /// <summary>The held item is ground-storable with a layout this mod takes over, and the lib can display it.</summary>
        public static bool IsChiselling(ItemSlot slot, IPlayer player = null) => BEUniversalDisplay.ChisellingToolsHeld(slot, player);

        public static bool TakesOver(ItemSlot slot, bool anyDisplayable = false)
        {
            if (slot?.Itemstack?.Collectible == null) return false;
            // The toggle's own click (GroundToggle): anything the lib can display, as on a window or a shelf.
            if (anyDisplayable) return BlockBehaviorDisplaySurfaces.GetDisplayableAttributes(slot, DisplayPlacementSystem.Category) != null;
            var gs = slot.Itemstack.Collectible.GetBehavior<CollectibleBehaviorGroundStorable>();
            if (gs?.StorageProps == null) return false;
            switch (gs.StorageProps.Layout)
            {
                case EnumGroundStorageLayout.SingleCenter:
                case EnumGroundStorageLayout.Halves:
                case EnumGroundStorageLayout.Quadrants:
                    break;
                default:
                    return false;
            }
            return BlockBehaviorDisplaySurfaces.GetDisplayableAttributes(slot, DisplayPlacementSystem.Category) != null;
        }
    }
}
