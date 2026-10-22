using System.Collections.Generic;
using System.Linq;
using System;
using HarmonyLib;
using System.Runtime.CompilerServices;
using UniversalDisplayLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace UniversalDisplayLib.OptionalDisplays;

public sealed class ChiselShelfSystem : ModSystem
{
    const string PatchId = "chiseledblockdisplay.embedded";
    static Harmony harmony;
    static int users;
    static bool loggedFailure;
    bool active, chiseled;
    static readonly ConditionalWeakTable<Cuboidf[], SelectionCache> selectionCache = new();
    sealed class CollisionMerge { public Cuboidf[] Boxes; }
    sealed class CollisionByNative
    {
        readonly Cuboidf[] native;
        readonly ConditionalWeakTable<Cuboidf[],CollisionMerge> items = new();
        readonly ConditionalWeakTable<Cuboidf[],CollisionMerge>.CreateValueCallback merge;
        public CollisionByNative(Cuboidf[] native) { this.native=native; merge=Merge; }
        CollisionMerge Merge(Cuboidf[] stored) => new() { Boxes=native.Concat(stored).ToArray() };
        public Cuboidf[] Get(Cuboidf[] stored) => items.GetValue(stored,merge).Boxes;
    }
    static readonly ConditionalWeakTable<Cuboidf[], CollisionByNative> collisionCache = new();
    sealed class SelectionCache
    {
        public Cuboidf[] Items, Result;
        public int Depth;
        public bool Embedded;
    }
    static readonly System.Func<IBlockAccessor, BlockPos, BEUniversalDisplay> resolver = (accessor, pos)
        => accessor.GetBlockEntity(pos)?.GetBehavior<BEBehaviorChiselShelf>()?.Display;

    public override void Start(ICoreAPI api)
    {
        active = DisplayFeatures.Any(api);
        chiseled = DisplayFeatures.Chiseled(api);
        if (!active) return;
        if (chiseled) api.RegisterBlockEntityBehaviorClass("ChiseledBlockDisplay", typeof(BEBehaviorChiselShelf));
        if (users++ != 0) return;
        BEUniversalDisplay.DisplayResolvers.Add(resolver);
        BEUniversalDisplay.PlacementRoomChecks.Add(BEBehaviorChiselShelf.FitsGround);
        harmony = new Harmony(PatchId);
        var me = typeof(ChiselShelfSystem);
        if (chiseled)
        {
            EmbeddedLightPropagation.Apply(harmony, api.Logger);
        }
        // Each group is applied whole or not at all (SafePatch, lib 1.3.1). A carved block's stored items always drop
        // when it is broken (BEBehaviorChiselShelf), so a missing group never loses items; without the boxes, the
        // chisel guard or the storage hooks no NEW item can be put into a chiselled block (StorageBlocked).
        bool boxes = SafePatch.Group(harmony, api.Logger, "items stored in chiselled blocks",
            "stored items there cannot be aimed at or bumped into",
            SafePatch.On(typeof(BlockMicroBlock), nameof(Block.GetCollisionBoxes)).After(me, nameof(Collision)),
            SafePatch.On(typeof(BlockMicroBlock), nameof(Block.GetSelectionBoxes)).After(me, nameof(Selection)));
        // Ground-only displays may reach into a carved support too. Preserve the
        // occupied-voxel edit guard without activating embedded storage or lights.
        bool guard = SafePatch.Group(harmony, api.Logger, "the chisel guard",
            "a chiselled block holding items can be chiselled",
            SafePatch.On(typeof(BlockEntityChisel), "UpdateVoxel").Before(me, nameof(Chisel)));
        if (!chiseled) return;
        bool storage = SafePatch.Group(harmony, api.Logger, "storage inside chiselled blocks",
            "new items cannot be put into chiselled blocks (items already there drop when the block is broken)",
            SafePatch.On(typeof(BlockChisel), nameof(Block.OnBlockInteractStart)).Before(me, nameof(Interact)),
            SafePatch.On(typeof(BlockMicroBlock), nameof(Block.OnBlockBroken)).Before(me, nameof(Break)),
            SafePatch.On(typeof(BlockMicroBlock), nameof(Block.DoPartialSelection)).After(me, nameof(Partial)),
            SafePatch.On(typeof(BlockMicroBlock), nameof(Block.OnPickBlock)).After(me, nameof(Pick)),
            SafePatch.On(typeof(BlockMicroBlock), "TryToRemoveSoilFirst").Before(me, nameof(Soil)));
        StorageBlocked = !(boxes && guard && storage);
        if (StorageBlocked) api.Logger.Warning("[UniversalDisplayLib] Chiseled Block Display: placing items into chiselled blocks is switched off.");
        SafePatch.Group(harmony, api.Logger, "light from items in chiselled blocks",
            "lamps stored in chiselled blocks give no light",
            SafePatch.On(typeof(BlockEntityMicroBlock), "GetLightHsv").After(me, nameof(EntityLight)),
            SafePatch.On(typeof(BlockMicroBlock), nameof(Block.GetLightHsv)).After(me, nameof(Light)));
        SafePatch.Group(harmony, api.Logger, "particles from items in chiselled blocks",
            "candles and lamps stored in chiselled blocks show no flame or smoke",
            SafePatch.On(typeof(Block), nameof(Block.ShouldReceiveClientParticleTicks)).After(me, nameof(ParticleGate)),
            SafePatch.On(typeof(Block), nameof(Block.OnAsyncClientParticleTick)).Before(me, nameof(NativeParticleTick)).After(me, nameof(Particles)));
    }

    /// <summary>
    /// True when a game update broke a hook carved storage needs (lib 1.3.1) - new items then cannot be put into
    /// chiselled blocks. Without Chiseled Block Display the storage behaviour is never registered, so nothing is placed
    /// there either way.
    /// </summary>
    internal static bool StorageBlocked { get; private set; }

    public override void AssetsFinalize(ICoreAPI api)
    {
        if (!active) return;
        var solidItems = new SortedDictionary<string,int>();
        foreach (Block block in api.World.Blocks)
        {
            if (block.Attributes?["itemCollision"].AsBool(false) == true && block.Code != null)
                solidItems[block.Code.Domain] = solidItems.GetValueOrDefault(block.Code.Domain) + 1;
            if (!chiseled || block is not BlockMicroBlock) continue;
            if (!block.BlockEntityBehaviors.Any(b => b.Name == "ChiseledBlockDisplay"))
                block.BlockEntityBehaviors = block.BlockEntityBehaviors.Append(new BlockEntityBehaviorType
                    { Name = "ChiseledBlockDisplay", properties = JsonObject.FromJson("{}") }).ToArray();
            if (api is ICoreClientAPI client && block.GetInterface<ICustomSelectionBoxRender>(api.World, null) == null)
            {
                var outline = new ChiselOutline(block, client);
                block.BlockBehaviors = block.BlockBehaviors.Append(outline).ToArray();
                block.CollectibleBehaviors = block.CollectibleBehaviors.Append(outline).ToArray();
            }
        }
        if (solidItems.Count > 0) api.Logger.Notification("[Display collision] Blocks with stored-item collision: {0}",
            string.Join(", ", solidItems.Select(kv => kv.Key + "=" + kv.Value)));
    }

    public static void RegisterCommands(ICoreServerAPI api)
    {
        api.ChatCommands.Create("chiseledblockdisplay").WithDescription(Lang.Get("universaldisplaylib:cmd-chiseledblockdisplay")).RequiresPrivilege(Privilege.controlserver)
            .BeginSubCommand("test")
            .WithDescription(Lang.Get("universaldisplaylib:cmd-chiseledblockdisplay-test"))
            .RequiresPrivilege(Privilege.controlserver)
            .HandleWith(args =>
            {
                var player = args.Caller.Player;
                var aim = player?.CurrentBlockSelection;
                if (aim?.Position == null || aim.Face != BlockFacing.UP || aim.HitPosition == null)
                    return TextCommandResult.Error(Lang.Get("universaldisplaylib:cmd-chiseledblockdisplay-test-aim"));
                if (player.Entity.Pos.XYZ.DistanceTo(aim.Position.ToVec3d()) > 8)
                    return TextCommandResult.Error(Lang.Get("universaldisplaylib:cmd-toofar"));
                if (!api.World.Claims.TryAccess(player, aim.Position, EnumBlockAccessFlags.Use))
                    return TextCommandResult.Error(Lang.Get("universaldisplaylib:cmd-noaccess"));
                var host = api.World.BlockAccessor.GetBlockEntity(aim.Position);
                var shelf = host?.GetBehavior<BEBehaviorChiselShelf>();
                if (shelf == null) return TextCommandResult.Error(Lang.Get("universaldisplaylib:cmd-chiseledblockdisplay-test-notchiselled"));
                var display = shelf.Ensure();
                if (display == null) return TextCommandResult.Error(Lang.Get("universaldisplaylib:cmd-chiseledblockdisplay-test-nodisplay"));
                float floor = (float)aim.HitPosition.Y * 16;
                int floorSurface = display.EmbeddedSurfaceForFloor(floor);
                if (floorSurface < 0) return TextCommandResult.Error(Lang.Get("universaldisplaylib:cmd-chiseledblockdisplay-test-nofloor"));
                var held = player.InventoryManager.ActiveHotbarSlot;
                var attr = BlockBehaviorDisplaySurfaces.GetDisplayableAttributes(held, DisplayPlacementSystem.Category);
                var target = display.PadSelectionAt(aim.HitPosition, floorSurface);
                if (attr == null || target == null) return TextCommandResult.Error(Lang.Get("universaldisplaylib:cmd-chiseledblockdisplay-test-noitem"));
                var loc = DisplaySlotId.Decode(target.SelectionBoxId);
                if (!display.EmbeddedPlacementFits(loc, attr, PlacementRotation.For(api, player), player))
                    return TextCommandResult.Error(Lang.Get("universaldisplaylib:cmd-chiseledblockdisplay-test-nofit"));
                int before = held.StackSize;
                display.TryPutAt(player, aim.HitPosition, floorSurface);
                return held.StackSize < before ? TextCommandResult.Success(Lang.Get("universaldisplaylib:cmd-chiseledblockdisplay-test-placed"))
                    : TextCommandResult.Error(Lang.Get("universaldisplaylib:cmd-chiseledblockdisplay-test-nothing"));
            }).EndSubCommand()
            .BeginSubCommand("revert")
            .WithDescription(Lang.Get("universaldisplaylib:cmd-chiseledblockdisplay-revert"))
            .HandleWith(_ =>
            {
                var shelves = BEBehaviorChiselShelf.LoadedOnServer.Where(s => s.Display?.Inventory.Empty == false).ToArray();
                foreach (var shelf in shelves) shelf.DropStored();
                return TextCommandResult.Success(Lang.Get("universaldisplaylib:cmd-chiseledblockdisplay-reverted", shelves.Length));
            }).EndSubCommand();
    }

    static void Failure(IWorldAccessor world, Exception error)
    {
        if (!loggedFailure) world?.Logger.Warning("[Display Placement] Embedded chisel interaction failed: {0}", error);
        loggedFailure = true;
    }

    internal static BlockSelection EmbeddedAim(IWorldAccessor world, BlockSelection selection)
    {
        if (selection?.Position == null) return null;
        var result = BEUniversalDisplay.WindowSelection(world.BlockAccessor, selection);
        if (resolver(world.BlockAccessor, result.Position) == null
            || DisplaySlotId.Decode(result.SelectionBoxId) is not { IsPlacedItem: true }) return null;
        return result;
    }

    static void Selection(IBlockAccessor blockAccessor, BlockPos pos, ref Cuboidf[] __result)
    {
        if (pos == null) return;
        try
        {
            foreach (var display in BEUniversalDisplay.StorageGuestsAt(blockAccessor, pos)
                .Append(resolver(blockAccessor, pos)).Where(be => be != null).Distinct())
            {
                if (display.Api is ICoreClientAPI client && GroundStorageHook.IsChiselling(client.World.Player?.InventoryManager?.ActiveHotbarSlot, client.World.Player)) return;
                int depth = pos.Y - display.Pos.Y;
                var items = depth == 0 ? display.GetSelectionBoxes() : display.AutoBoxesAt(pos);
                __result = AppendDisplayReach(__result ?? Array.Empty<Cuboidf>(), items, depth, display.IsEmbedded);
            }
        }
        catch (Exception e) { Failure(null, e); }
    }

    static void Collision(IBlockAccessor blockAccessor, BlockPos pos, ref Cuboidf[] __result)
    {
        if (pos == null) return;
        try
        {
            var guests = BEUniversalDisplay.StorageGuestsAt(blockAccessor,pos);
            var result = __result ?? Array.Empty<Cuboidf>();
            foreach (var display in guests) result = AppendCollision(result,display.ItemCollisionAt(pos));
            var own = resolver(blockAccessor,pos);
            if (own != null && Array.IndexOf(guests,own) < 0) result = AppendCollision(result,own.ItemCollisionAt(pos));
            __result = result;
        }
        catch (Exception e) { Failure(null,e); }
    }

    internal static Cuboidf[] AppendCollision(Cuboidf[] own, Cuboidf[] items)
    {
        if (items.Length == 0) return own;
        if (own.Length == 0) return items;
        return collisionCache.GetValue(own, static native => new CollisionByNative(native)).Get(items);
    }

    internal static Cuboidf[] AppendItems(Cuboidf[] own, Cuboidf[] items)
        => AppendReach(own, items, 0);

    internal static Cuboidf[] AppendReach(Cuboidf[] own, Cuboidf[] items, int depth)
        => AppendDisplayReach(own, items, depth, true);

    static Cuboidf[] AppendDisplayReach(Cuboidf[] own, Cuboidf[] items, int depth, bool embedded)
    {
        var entry = selectionCache.GetValue(own, _ => new SelectionCache());
        lock (entry)
        {
            if (ReferenceEquals(entry.Items, items) && entry.Depth == depth && entry.Embedded == embedded) return entry.Result;
            entry.Items = items;
            entry.Depth = depth;
            entry.Embedded = embedded;
            string prefix = (depth == 0 ? "" : BEUniversalDisplay.GuestBoxPrefix + "0," + (-depth) + ",0:") + (embedded ? BEUniversalDisplay.EmbeddedBoxPrefix : "");
            entry.Result = own.Concat(items.OfType<CuboidfWithId>()
                .Where(b => DisplaySlotId.Decode(b.Id) is { IsPlacedItem: true })
                .Select(b => (Cuboidf)new CuboidfWithId(b) { Id = prefix + b.Id })).ToArray();
            return entry.Result;
        }
    }

    static bool Interact(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref bool __result)
    {
        try
        {
            var aim = EmbeddedAim(world, blockSel);
            if (aim == null) return true;
            var display = resolver(world.BlockAccessor, aim.Position);
            __result = display?.OnBlockInteractStart(world, byPlayer, aim) == true;
            return false;
        }
        catch (Exception e) { Failure(world, e); __result = true; return false; }
    }

    /// <summary>
    /// CHISEL EDITING (trial, Calm 2026-10-19: "allow chiselling when an item is on there" - "edit around items"; rotate /
    /// flip refused while items are stored). Every chisel stroke reaches the game through UpdateVoxel with the aimed voxel,
    /// the face and add/remove; the mode decides the rest (public vssurvivalmod ChiselMode.Apply: a size-N mode removes the
    /// N-cube at voxelPos, or adds the N-cube one step out along the face). So the cube is known before anything changes:
    /// refused only if it would go into a stored item or take away the voxel layer an item stands on. Items never move.
    /// Rename always passes; rotate, flip and any mode that changes the block its own way are refused while items are in it.
    /// </summary>
    static bool Chisel(BlockEntityChisel __instance, IPlayer byPlayer, Vec3i voxelPos, BlockFacing facing, bool isBreak)
    {
        try
        {
            var items = new List<(Cuboidf Box, bool OnFloor)>();
            var own = __instance.GetBehavior<BEBehaviorChiselShelf>()?.Display;
            if (own?.Inventory.Empty == false) items.AddRange(own.EmbeddedItemFootprints());
            if (__instance.Api != null)
            {
                var accessor = __instance.Api.World.BlockAccessor;
                foreach (var lower in BEUniversalDisplay.StorageGuestsAt(accessor, __instance.Pos))
                    foreach (var box in lower.AutoBoxesAt(__instance.Pos)) items.Add((box, false));
                // ITEMS RESTING ON TOP (Calm, 2026-10-19: "if placed on a block you can chisel under the item ... the ground
                // display side of thing"): a Ground Display pile in the block above stands its items on this block's top
                // and follows it down, and items on the lowest floor of a chiselled block above rest on it too. Their
                // boxes are in the upper block, so move them down one block into this one.
                var above = BEUniversalDisplay.At(accessor, __instance.Pos.UpCopy());
                if (above != null && (above.FollowSupport || above.IsEmbedded) && above.Inventory?.Empty == false)
                    foreach (var (box, onFloor) in above.EmbeddedItemFootprints())
                        if (onFloor && box.Y1 < 1 / 16f + BEUniversalDisplay.FitSlack) items.Add((box.OffsetCopy(0, 1, 0), true));
            }
            if (items.Count == 0) return true;

            string refusal = ChiselRefusal(__instance.GetChiselModeData(byPlayer), voxelPos, facing, isBreak, items);
            if (refusal == null) return true;
            (__instance.Api as ICoreClientAPI)?.TriggerIngameError(__instance, "chiseledblockdisplayoccupied", Lang.Get(refusal));
            return false;
        }
        catch (Exception e) { Failure(__instance.Api.World, e); return false; }
    }

    /// <summary>Why this stroke may not happen with these items in the block (a lang key), or null when it may.</summary>
    internal static string ChiselRefusal(VSSurvivalMod.Systems.ChiselModes.ChiselMode mode, Vec3i voxelPos, BlockFacing facing,
        bool isBreak, List<(Cuboidf Box, bool OnFloor)> items)
    {
        if (mode == null || voxelPos == null || facing == null) return "universaldisplaylib:displayplacement-chisel-occupied";
        string name = mode.GetType().Name;
        if (name == "RenameChiselMode") return null;
        if (name == "RotateChiselMode" || name == "FlipChiselMode") return "universaldisplaylib:displayplacement-chisel-turn";
        // A mode that does its own thing instead of the plain cube (another mod's) cannot be checked: refuse while items are in.
        var apply = mode.GetType().GetMethod("Apply");
        if (apply?.DeclaringType != typeof(VSSurvivalMod.Systems.ChiselModes.ChiselMode)) return "universaldisplaylib:displayplacement-chisel-occupied";

        int size = Math.Max(1, mode.ChiselSize);
        var at = isBreak ? voxelPos : voxelPos.Clone().Add(size * facing.Normali.X, size * facing.Normali.Y, size * facing.Normali.Z);
        var cube = new Cuboidf(at.X / 16f, at.Y / 16f, at.Z / 16f, (at.X + size) / 16f, (at.Y + size) / 16f, (at.Z + size) / 16f);
        const float s = BEUniversalDisplay.FitSlack;
        foreach (var (box, onFloor) in items)
        {
            if (box == null) continue;
            // Adding material into an item (the same half voxel of give as placing).
            if (!isBreak && Overlap(cube, box, s)) return "universaldisplaylib:displayplacement-chisel-item";
            // Taking away the layer an item stands on.
            if (isBreak && onFloor && Overlap(cube, new Cuboidf(box.X1 + s, box.Y1 - 1 / 16f, box.Z1 + s, box.X2 - s, box.Y1, box.Z2 - s), .0001f))
                return "universaldisplaylib:displayplacement-chisel-item";
        }
        return null;
    }

    static bool Overlap(Cuboidf a, Cuboidf b, float give)
        => a.X2 > b.X1 + give && a.X1 < b.X2 - give && a.Y2 > b.Y1 + give && a.Y1 < b.Y2 - give && a.Z2 > b.Z1 + give && a.Z1 < b.Z2 - give;

    static void Pick(ref ItemStack __result)
    {
        __result?.Attributes?.RemoveAttribute("chiseledBlockDisplay");
        __result?.Attributes?.RemoveAttribute("ugsShelf");
    }
    static bool Soil(IWorldAccessor world, BlockPos pos, ref bool __result)
    {
        if (resolver(world.BlockAccessor, pos)?.Inventory.Empty != false) return true;
        __result = false; // break the whole host, dropping items, rather than changing its floor
        return false;
    }

    internal static byte[] StrongerLight(byte[] original, byte[] stored)
        => stored?.Length >= 3 && stored[2] > (original?.Length >= 3 ? original[2] : 0)
            ? new[] { stored[0], stored[1], stored[2] } : original;

    // Relight and particle workers read only the main-thread publication. Never
    // create a display, resolve a stack, or enumerate its inventory from here.
    static void Light(IBlockAccessor blockAccessor, BlockPos pos, ref byte[] __result)
    {
        if (pos == null) return;
        try { __result = StrongerLight(__result, resolver(blockAccessor, pos)?.CachedLightHsv); }
        catch (Exception e) { Failure(null, e); }
    }

    static void EntityLight(BlockEntityMicroBlock __instance, ref byte[] __result)
    {
        // Cover the native BE getter as well: the engine can call this through
        // an already compiled microblock wrapper. Read only the published snapshot.
        __result = StrongerLight(__result, __instance.GetBehavior<BEBehaviorChiselShelf>()?.Display?.CachedLightHsv);
    }

    static void ParticleGate(Block __instance, IWorldAccessor world, BlockPos pos, ref bool __3, ref bool __result)
    {
        if (__instance is not BlockMicroBlock) return;
        try
        {
            if (resolver(world.BlockAccessor, pos)?.HasParticleEmittingItem() != true) return;
            __result = true;
            __3 = true;
        }
        catch (Exception e) { Failure(world, e); }
    }

    static void Particles(Block __instance, IAsyncParticleManager manager, BlockPos pos, float windAffectednessAtPos)
    {
        if (__instance is not BlockMicroBlock) return;
        try { resolver(manager.BlockAccess, pos)?.SpawnStoredItemParticles(manager, pos, windAffectednessAtPos); }
        catch (Exception e) { Failure(null, e); }
    }

    // The stored-item gate can activate a host with no native particle array.
    // Vanilla's tick expects that array; our postfix still runs when it is skipped.
    static bool NativeParticleTick(Block __instance)
        => __instance is not BlockMicroBlock || __instance.ParticleProperties != null;

    static bool Break(IWorldAccessor world, BlockPos pos, IPlayer byPlayer)
    {
        try
        {
            var aim = byPlayer?.CurrentBlockSelection;
            if (aim?.Position?.Equals(pos) != true || EmbeddedAim(world, aim) == null) return true;
            // In this first test, take items with right-click. Never break the host
            // merely because its carried item's box was selected.
            return false;
        }
        catch (Exception e) { Failure(world, e); return false; }
    }

    static void Partial(Block __instance, IWorldAccessor world, BlockPos pos, ref bool __result)
    {
        if (__instance is BlockMicroBlock) __result = true;
    }

    sealed class ChiselOutline : BlockBehavior, ICustomSelectionBoxRender
    {
        static readonly Type decorBoxType = AccessTools.TypeByName("Vintagestory.API.Common.DecorSelectionBox");
        readonly ICoreClientAPI client;
        public ChiselOutline(Block block, ICoreClientAPI api) : base(block) { client = api; }
        public void RenderSelectionBoxes(BlockSelection selection, RenderBoxDelegate draw)
        {
            var aim = BEUniversalDisplay.WindowSelection(client.World.BlockAccessor, selection);
            var owner = aim?.Position == null ? null : BEUniversalDisplay.At(client.World.BlockAccessor, aim.Position);
            if (owner != null && DisplaySlotId.Decode(aim.SelectionBoxId) is { IsPlacedItem: true })
            {
                int dy = aim.Position.Y - selection.Position.Y;
                owner.SurfaceBehavior?.RenderSelectionBoxesOn(owner, aim,
                    (box, width, colour) => draw(box.OffsetCopy(0, dy, 0), width, colour));
                return;
            }
            var held = client.World.Player?.InventoryManager?.ActiveHotbarSlot;
            if (GroundSettings.IsEnabled(client.World) && GroundSettings.PlaceActive(client.World, client.World.Player,
                    client.World.Player?.Entity?.Controls)
                && held is { Empty: false }
                && GroundGhost.TargetThisFrame(client, held, selection, GroundSettings.Mode(client.World) == "Toggle", out var target, out var onTarget)
                && target.IsEmbedded)
            {
                target.SurfaceBehavior.RenderSelectionBoxesOn(target, onTarget,
                    (box, width, colour) => draw(box.OffsetCopy(target.Pos.X - selection.Position.X,
                        target.Pos.Y - selection.Position.Y, target.Pos.Z - selection.Position.Z), width, colour));
                return;
            }
            var boxes = block.GetSelectionBoxes(client.World.BlockAccessor, selection.Position);
            bool partial = block.DoPartialSelection(client.World, selection.Position);
            for (int i = 0; i < (boxes?.Length ?? 0); i++)
                if ((!partial || i == selection.SelectionBoxIndex) && decorBoxType?.IsInstanceOfType(boxes[i]) != true)
                    draw(boxes[i], 1.6f * client.Settings.Float["wireframethickness"], block.GetSelectionColor(client, selection.Position));
        }
    }

    public override void Dispose()
    {
        if (!active) return;
        if (--users == 0)
        {
            harmony?.UnpatchAll(PatchId);
            BEUniversalDisplay.DisplayResolvers.Remove(resolver);
            BEUniversalDisplay.PlacementRoomChecks.Remove(BEBehaviorChiselShelf.FitsGround);
            selectionCache.Clear();
            collisionCache.Clear();
            EmbeddedLightPropagation.Clear();
            loggedFailure = false;
            StorageBlocked = false;
        }
        base.Dispose();
    }
}
