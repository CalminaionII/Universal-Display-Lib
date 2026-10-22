using System.Collections.Generic;
using System.Linq;
using System;
using System.Text;
using UniversalDisplayLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace UniversalDisplayLib.OptionalDisplays;

// One embedded inventory; independent surface IDs distinguish carved voxel floors.
// Empty chiselled blocks have no display, mesh generation or ticking inventory.
public sealed class BEBehaviorChiselShelf : BlockEntityBehavior
{
    public BEBehaviorChiselShelf(BlockEntity host) : base(host) { }
    volatile BEUniversalDisplay display;
    public BEUniversalDisplay Display { get => display; private set => display = value; }
    ITreeAttribute pending;
    bool ended;
    public static readonly HashSet<BEBehaviorChiselShelf> LoadedOnServer = new();
    static readonly AssetLocation DisplayCode = new("chiseledblockdisplay", "chiselshelf");
    BEUniversalDisplay preview;
    byte[] publishedLight;
    bool publishedParticles;

    public static bool TryFloor(IWorldAccessor world, BlockSelection aim, out BEBehaviorChiselShelf shelf, out float floor)
    {
        shelf = null; floor = 0;
        if (ChiselShelfSystem.StorageBlocked) return false;
        if (aim?.Position == null || aim.Face != BlockFacing.UP || aim.HitPosition == null) return false;
        var hit = aim.HitPosition;
        if (ChiselShelfSystem.EmbeddedAim(world, aim) != null) return false;
        // At a block join, the carved space belongs to the microblock ABOVE.
        // It can rest on an untouched full block; no shaved voxel layer is needed.
        if (double.IsFinite(hit.X) && double.IsFinite(hit.Y) && double.IsFinite(hit.Z)
            && hit.X >= 0 && hit.X < 1 && hit.Z >= 0 && hit.Z < 1 && Math.Abs(hit.Y - 1) < .0001
            && world.BlockAccessor.GetBlockEntity(aim.Position.UpCopy()) is BlockEntityMicroBlock upper)
        {
            var ownerAim = BEUniversalDisplay.WindowSelection(world.BlockAccessor, aim);
            if (ownerAim?.Position?.Equals(aim.Position) != true) return false;
            var support = world.BlockAccessor.GetBlock(aim.Position)?.GetCollisionBoxes(world.BlockAccessor, aim.Position);
            if (support?.Any(box => box != null && hit.X >= box.X1 && hit.X <= box.X2
                && hit.Z >= box.Z1 && hit.Z <= box.Z2 && Math.Abs(box.Y2 - 1) < .0001) != true) return false;
            shelf = upper.GetBehavior<BEBehaviorChiselShelf>();
            return shelf != null;
        }
        if (!ValidFloorHit(hit)) return false;
        var host = world.BlockAccessor.GetBlockEntity(aim.Position) as BlockEntityMicroBlock;
        shelf = host?.GetBehavior<BEBehaviorChiselShelf>();
        if (shelf == null) return false;
        // A pile already resting down into this block remains its existing owner.
        if (DisplayPlacementSystem.IsPile(world.BlockAccessor.GetBlock(aim.Position.UpCopy())?.Code)) return false;
        var forwarded = BEUniversalDisplay.WindowSelection(world.BlockAccessor, aim);
        if (forwarded?.Position?.Equals(aim.Position) != true) return false;
        var boxes = host.GetCollisionBoxes(world.BlockAccessor, aim.Position);
        if (boxes == null || !boxes.Any(box => box != null && hit.X >= box.X1 && hit.X <= box.X2
            && hit.Z >= box.Z1 && hit.Z <= box.Z2 && Math.Abs(hit.Y - box.Y2) < .001)) return false;
        floor = (float)hit.Y * 16;
        return true;
    }

    public static bool ValidFloorHit(Vec3d hit) => hit != null
        && double.IsFinite(hit.X) && double.IsFinite(hit.Y) && double.IsFinite(hit.Z)
        && hit.X >= 0 && hit.X < 1 && hit.Z >= 0 && hit.Z < 1 && hit.Y > 0 && hit.Y < 1;

    public bool PreviewFloor(ICoreClientAPI client, BlockSelection aim, float floor,
                             out BEUniversalDisplay target, out BlockSelection selection)
    {
        target = null; selection = null;
        if (Display != null)
            target = Display;
        else
        {
            var block = Api.World.GetBlock(DisplayCode);
            if (block == null) return false;
            preview ??= BEUniversalDisplay.EmbeddedPhantom(client, Blockentity, block);
            preview.EmbeddedRoomCheck = Fits;
            target = preview;
        }
        int surface = target.EmbeddedSurfaceForFloor(floor);
        if (surface < 0) return false;
        selection = target.PadSelectionAt(HitInHost(aim), surface);
        return selection != null;
    }

    public bool PlaceOnFloor(IPlayer player, BlockSelection aim, float floor)
    {
        if (GroundStorageHook.IsChiselling(player?.InventoryManager?.ActiveHotbarSlot, player)) return false;
        if (!Api.World.Claims.TryAccess(player, Pos, EnumBlockAccessFlags.Use)) return true;
        var current = Ensure();
        if (current == null) return true;
        int surface = current.EmbeddedSurfaceForFloor(floor);
        if (surface < 0) return true;
        return current.TryPutAt(player, HitInHost(aim), surface);
    }

    Vec3d HitInHost(BlockSelection aim) => aim.HitPosition.AddCopy(
        aim.Position.X - Pos.X, aim.Position.Y - Pos.Y, aim.Position.Z - Pos.Z);

    public override void Initialize(ICoreAPI api, JsonObject properties)
    {
        base.Initialize(api, properties);
        if (pending != null) Ensure();
    }

    public BEUniversalDisplay Ensure()
    {
        if (Display != null || ended || Api == null) return Display;
        Block displayBlock = Api.World.GetBlock(DisplayCode);
        if (displayBlock == null) return null;
        Display = BEUniversalDisplay.Embedded(Blockentity, displayBlock, pending);
        Display.EmbeddedRoomCheck = Fits;
        Display.EmbeddedEffectsChanged = RefreshHostEffects;
        if (Api.Side == EnumAppSide.Server) LoadedOnServer.Add(this);
        pending = null;
        return Display;
    }

    void RefreshHostEffects()
    {
        if (ended || Api == null) return;
        var block = Api.World.BlockAccessor.GetBlock(Pos);
        if (!ReferenceEquals(Api.World.BlockAccessor.GetBlockEntity(Pos), Blockentity)
            || block == null || block.Id != Blockentity.Block.Id)
        {
            Display?.MarkEmbeddedEffectsStale();   // not in the world yet - the display retries next second
            return;
        }
        var light = ChiselShelfSystem.StrongerLight(block.GetLightHsv(Api.World.BlockAccessor, Pos), Display?.CachedLightHsv);
        if ((Display?.CachedLightHsv?[2] ?? 0) > 0) EmbeddedLightPropagation.Register(Pos);
        bool particles = Display?.HasParticleEmittingItem() == true;
        bool changed = !SameLight(publishedLight, light) || publishedParticles != particles;
        var previous = publishedLight;
        publishedLight = light?.Length >= 3 ? new[] { light[0], light[1], light[2] } : null;
        publishedParticles = particles;
        if (!changed) return;
        if ((previous?[2] ?? 0) > 0 && (publishedLight?[2] ?? 0) == 0)
            Api.World.BlockAccessor.RemoveBlockLight(previous, Pos);
        // Keep the real microblock and its entity. This also refreshes the particle gate.
        Api.World.GetBlockAccessor(false, true, false).ExchangeBlock(block.Id, Pos.Copy());
    }

    static bool SameLight(byte[] a, byte[] b) => (a?[2] ?? 0) == (b?[2] ?? 0)
        && ((a?[2] ?? 0) == 0 || (a[0] == b[0] && a[1] == b[1]));

    bool Fits(Cuboidf item)
    {
        return FitsWorld(Api.World.BlockAccessor, Pos, item);
    }

    public static bool FitsWorld(IBlockAccessor accessor, BlockPos owner, Cuboidf item)
        => FitsWorldCore(accessor, owner, item, false);

    internal static bool FitsGround(BEUniversalDisplay display, Cuboidf item)
        => !DisplayPlacementSystem.IsPile(display.Block?.Code)
            || FitsWorldCore(display.Api.World.BlockAccessor, display.Pos, item, true);

    static bool FitsWorldCore(IBlockAccessor accessor, BlockPos owner, Cuboidf item, bool ground)
    {
        if (item == null || !float.IsFinite(item.Y1) || !float.IsFinite(item.Y2) || (!ground && item.Y1 < 0)) return false;
        for (int dy = (int)Math.Floor(item.Y1 + .0001f); dy < Math.Ceiling(item.Y2 - .0001f); dy++)
        {
            var at = owner.UpCopy(dy);
            if (!accessor.IsValidPos(at)) return false;
            var local = item.OffsetCopy(0, -dy, 0);
            var block = accessor.GetBlock(at);
            if (block == null) return false;
            var entity = accessor.GetBlockEntity(at);
            var boxes = ground && dy == 0 ? Array.Empty<Cuboidf>()
                : entity is BlockEntityMicroBlock micro ? micro.GetCollisionBoxes(accessor, at)
                : entity is BEAutoBoxesFiller filler ? filler.AllOwners().Where(pos => !pos.Equals(owner))
                    .SelectMany(pos => BEUniversalDisplay.At(accessor, pos)?.AutoCollisionAt(at) ?? Array.Empty<Cuboidf>()).ToArray()
                : entity is BEUniversalDisplay display ? display.CollisionExcept(owner)
                : block.GetCollisionBoxes(accessor, at);
            if (!FitsVoxels(local, boxes ?? Array.Empty<Cuboidf>())) return false;
            // Independent inventories can occupy the upper carved block too.
            if (!ServerRules.ItemOverlapAllowed)
                foreach (var other in BEUniversalDisplay.StorageGuestsAt(accessor, at)
                    .Append(BEUniversalDisplay.At(accessor, at)).Where(be => be != null).Distinct())
                {
                    if (!other.Pos.Equals(owner)
                        && other.EmbeddedItemsIntersect(item.OffsetCopy(0, owner.Y - other.Pos.Y, 0))) return false;
                }
        }
        return true;
    }

    public static bool FitsVoxels(Cuboidf item, Cuboidf[] boxes)
    {
        if (item == null || boxes == null) return false;
        const float epsilon = BEUniversalDisplay.FitSlack;
        foreach (var voxel in boxes)
            if (voxel != null && item.X2 > voxel.X1 + epsilon && item.X1 < voxel.X2 - epsilon
                && item.Y2 > voxel.Y1 + epsilon && item.Y1 < voxel.Y2 - epsilon
                && item.Z2 > voxel.Z1 + epsilon && item.Z1 < voxel.Z2 - epsilon) return false;
        return true;
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        if (Display != null)
        {
            var saved = new TreeAttribute();
            Display.ToTreeAttributes(saved);
            tree["chiseledBlockDisplay"] = saved;
        }
        else if (pending != null) tree["chiseledBlockDisplay"] = pending.Clone();
        tree.RemoveAttribute("ugsShelf");
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor world)
    {
        pending = (tree.GetTreeAttribute("chiseledBlockDisplay") ?? tree.GetTreeAttribute("ugsShelf"))?.Clone();
        if (Api == null) return;
        if (Display != null) Display.FromTreeAttributes(pending ?? new TreeAttribute(), world);
        else if (pending != null) Ensure();
        pending = null;
    }

    public override bool OnTesselation(ITerrainMeshPool pool, ITesselatorAPI tesselator)
    {
        Display?.AddEmbeddedMeshes(pool);
        return false; // the host's voxel mesh is still drawn by the game
    }

    public override void GetBlockInfo(IPlayer player, StringBuilder text)
    {
        var aimed = BEUniversalDisplay.WindowSelection(Api.World.BlockAccessor, player?.CurrentBlockSelection);
        var target = aimed?.Position == null ? null : BEUniversalDisplay.At(Api.World.BlockAccessor, aimed.Position);
        (target is { IsEmbedded: true } ? target : Display)?.GetBlockInfo(player, text);
    }
    public override void OnBlockBroken(IPlayer player = null) => Finish(true, player);
    public override void OnBlockRemoved() => Finish(true, null);
    public override void OnBlockUnloaded() => Finish(false, null);
    public void DropStored()
    {
        if (Display == null || Api.Side != EnumAppSide.Server) return;
        Display.Inventory.DropAll(Pos.ToVec3d().Add(.5, .5, .5));
        Display.OnContentsChanged();
    }
    void Finish(bool drop, IPlayer player)
    {
        if (ended) return;
        ended = true;
        LoadedOnServer.Remove(this);
        if (Display == null) return;
        if (drop && (publishedLight?[2] ?? 0) > 0) Api.World.BlockAccessor.RemoveBlockLight(publishedLight, Pos);
        Display.EmbeddedEffectsChanged = null;
        Display.ClearEmbeddedEffects();
        if (drop && Api.Side == EnumAppSide.Server) Display.Inventory.DropAll(Pos.ToVec3d().Add(.5, .5, .5));
        if (drop) Display.OnBlockRemoved();
        else Display.OnBlockUnloaded();
    }

    public override void OnStoreCollectibleMappings(Dictionary<int, AssetLocation> blocks, Dictionary<int, AssetLocation> items)
        => Display?.OnStoreCollectibleMappings(blocks, items);
    public override void OnLoadCollectibleMappings(IWorldAccessor world, Dictionary<int, AssetLocation> blocks,
        Dictionary<int, AssetLocation> items, int seed, bool resolveImports)
        => Display?.OnLoadCollectibleMappings(world, blocks, items, seed, resolveImports);
}
