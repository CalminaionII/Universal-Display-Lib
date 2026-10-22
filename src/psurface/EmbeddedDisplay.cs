using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace UniversalDisplayLib;

public partial class BEUniversalDisplay
{
    public BlockEntity EmbeddedHost { get; private set; }
    public bool IsEmbedded => EmbeddedHost != null;
    public float EmbeddedFloorVoxels { get; set; }
    public System.Func<Cuboidf, bool> EmbeddedRoomCheck { get; set; }
    public const string EmbeddedBoxPrefix = "embedded:";
    public static readonly List<System.Func<IBlockAccessor, BlockPos, BEUniversalDisplay>> DisplayResolvers = new();

    public static BEUniversalDisplay At(IBlockAccessor accessor, BlockPos pos)
    {
        if (pos == null) return null;
        if (accessor.GetBlockEntity(pos) is BEUniversalDisplay direct) return direct;
        foreach (var resolver in DisplayResolvers)
            if (resolver(accessor, pos) is { } display) return display;
        return null;
    }

    public static BEUniversalDisplay Embedded(BlockEntity host, Block displayBlock, ITreeAttribute saved = null)
    {
        var display = new BEUniversalDisplay { EmbeddedHost = host, Block = displayBlock, Pos = host.Pos.Copy() };
        if (saved != null) display.FromTreeAttributes(saved, host.Api.World);
        display.Initialize(host.Api);
        return display;
    }

    public static BEUniversalDisplay EmbeddedPhantom(ICoreClientAPI api, BlockEntity host, Block displayBlock)
    {
        var display = Phantom(api, displayBlock, host.Pos);
        display.EmbeddedHost = host;
        return display;
    }

    // Surface 0 and embeddedFloor remain the exact legacy single-floor storage.
    // New surfaces 1..16 represent voxel floors 0..15; no old slot is renamed.
    public int EmbeddedSurfaceForFloor(float floorVoxels)
    {
        if (!float.IsFinite(floorVoxels)) return -1;
        int floor = (int)Math.Round(floorVoxels);
        if (floor < 0 || floor > 15 || Math.Abs(floorVoxels - floor) > .01f) return -1;
        if (Math.Abs(EmbeddedFloorVoxels - floorVoxels) < .01f
            && SlotsSnapshot().Any(kv => kv.Value is { Empty: false } && DisplaySlotId.Decode(kv.Key)?.SurfaceIndex == 0)) return 0;
        return floor + 1;
    }

    public bool EmbeddedPlacementFits(DisplaySlotId loc, DisplayableAttributes attr, float rotation, IPlayer player = null)
        => EmbeddedPlacementFitsCore(loc,attr,rotation,player,null,null);

    bool EmbeddedPlacementFitsCore(DisplaySlotId loc, DisplayableAttributes attr, float rotation, IPlayer player,
        string excludeSlot, float? nudgeOverride)
    {
        if (!IsEmbedded && !UnboundedStacking) return true;
        var surface = SurfaceBehavior?.GetSurface(loc?.SurfaceIndex ?? -1);
        if (surface == null || attr?.Size == null) return false;
        var slots = SlotsSnapshot();
        var box = EmbeddedBounds(loc, surface, attr, rotation + SurfaceFacingDeg(surface), slots,
            nudgeOverride ?? PlacementHeight.For(Api, player ?? (Api as ICoreClientAPI)?.World.Player));
        foreach (var check in PlacementRoomChecks) if (!check(this, box)) return false;
        if (player != null)
            for (int dy = Math.Max(1, (int)Math.Floor(box.Y1)); dy < Math.Ceiling(box.Y2 - .0001f); dy++)
                if (!Api.World.Claims.TryAccess(player, Pos.UpCopy(dy), EnumBlockAccessFlags.Use)) return false;
        if (!IsEmbedded) return true;
        float floor = (surface.VoxelPosition.Y + SupportUnder(loc, surface, attr, slots)) / 16
            + (IsEmbedded && loc.SurfaceIndex == 0 ? EmbeddedFloorVoxels / 16 : 0);
        if (box.X1 < -FitSlack || box.Y1 < floor - FitSlack || box.Z1 < -FitSlack || box.X2 > 1 + FitSlack || box.Z2 > 1 + FitSlack
            || EmbeddedRoomCheck?.Invoke(box) != true) return false;
        if (!ServerRules.ItemOverlapAllowed)
            foreach (var kv in slots)
            {
                if (kv.Value is not { Empty: false } || kv.Key == excludeSlot) continue;
                var otherLoc = DisplaySlotId.Decode(kv.Key);
                if (otherLoc == null) continue;
                // A stack is also an intentional composition: a lowered candle
                // may sit inside the hollow bowl below, as it does on shelves.
                // Item bounding boxes do not describe those hollow interiors.
                if (FreeData(loc.Encoded) != null)
                {
                    if (FreeDescendsFrom(loc.Encoded, kv.Key)) continue;
                }
                else if (otherLoc.Y < loc.Y && SameEmbeddedColumn(loc, otherLoc)) continue;
                var otherSurface = SurfaceBehavior.GetSurface(otherLoc.SurfaceIndex);
                if (otherSurface == null) continue;
                var otherAttr = BlockBehaviorDisplaySurfaces.GetDisplayableAttributes(kv.Value, otherSurface.DisplayCategory);
                if (otherAttr?.Size == null) continue;
                var otherBox = EmbeddedBounds(otherLoc, otherSurface, otherAttr,
                    ItemRotationDeg(kv.Key, otherAttr, otherSurface), slots, null);
                if (EmbeddedItemsOverlap(box, otherBox)) return false;
            }
        return true;
    }

    public bool UnboundedStacking => Block?.Attributes?["unboundedStacking"].AsBool(false) == true;

    public bool StackTargetFits(DisplaySlotId loc, DisplaySurface surface, DisplayableAttributes attr, float rotation, IPlayer player = null)
    {
        var size = BlockBehaviorDisplaySurfaces.RotatedFootprint(attr.Size, rotation + SurfaceFacingDeg(surface));
        if (loc.X < size.Width / 2 || loc.Z < size.Length / 2
            || loc.X + size.Width / 2 > surface.Size.Width || loc.Z + size.Length / 2 > surface.Size.Length) return false;
        // Preview reads resolved snapshots. Calling ContentCuboids here would
        // resolve the entire inventory every frame just to inspect one stack level.
        if (!ServerRules.ItemOverlapAllowed)
            foreach (var kv in SlotsSnapshot())
            {
                if (kv.Value is not { Empty: false }) continue;
                var other = DisplaySlotId.Decode(kv.Key);
                if (other == null || other.SurfaceIndex != loc.SurfaceIndex || other.Y != loc.Y) continue;
                var data = BlockBehaviorDisplaySurfaces.GetDisplayableAttributes(kv.Value, surface.DisplayCategory);
                if (data?.Size == null) continue;
                var occupied = BlockBehaviorDisplaySurfaces.RotatedFootprint(data.Size, ItemRotationDeg(kv.Key, data, surface));
                if (loc.X + size.Width / 2 > other.X - occupied.Width / 2
                    && loc.X - size.Width / 2 < other.X + occupied.Width / 2
                    && loc.Z + size.Length / 2 > other.Z - occupied.Length / 2
                    && loc.Z - size.Length / 2 < other.Z + occupied.Length / 2) return false;
            }
        return EmbeddedPlacementFits(loc, attr, rotation, player);
    }
    public static bool IsChisellingPair(ItemSlot main, ItemSlot off)
    {
        var first = main?.Itemstack?.Collectible?.GetTool(main);
        var second = off?.Itemstack?.Collectible?.GetTool(off);
        return first == EnumTool.Chisel && second == EnumTool.Hammer
            || first == EnumTool.Hammer && second == EnumTool.Chisel;
    }

    public static bool ChisellingToolsHeld(ItemSlot held, IPlayer player)
        => IsChisellingPair(held, player?.Entity?.LeftHandItemSlot);

    public bool SuppressPlacement(ItemSlot held)
        => SuppressPlacement(held, (Api as ICoreClientAPI)?.World.Player);

    public bool SuppressPlacement(ItemSlot held, IPlayer player)
        => (IsEmbedded || UnboundedStacking) && ChisellingToolsHeld(held, player);

    bool EmbeddedEditFits(string slotId, float? rotation, float? height, IPlayer player)
    {
        var slots = SlotsSnapshot();
        var slot = slots.FirstOrDefault(kv => kv.Key == slotId).Value;
        var loc = DisplaySlotId.Decode(slotId);
        var surface = SurfaceBehavior?.GetSurface(loc?.SurfaceIndex ?? -1);
        if (surface == null || slot is not { Empty: false }) return false;
        var attr = BlockBehaviorDisplaySurfaces.GetDisplayableAttributes(slot, surface.DisplayCategory);
        if (attr?.Size == null) return false;
        float angle = ItemRotationDeg(slotId, attr, surface);
        if (rotation.HasValue) angle += rotation.Value - ChosenRotationDeg(slotId);
        var box = EmbeddedBounds(loc, surface, attr, angle, slots, height ?? ChosenHeightVoxels(slotId));
        float floor = (surface.VoxelPosition.Y + (loc.SurfaceIndex == 0 ? EmbeddedFloorVoxels : 0)) / 16;
        if (box.X1 < 0 || box.Z1 < 0 || box.X2 > 1 || box.Z2 > 1 || box.Y1 < floor
            || EmbeddedRoomCheck?.Invoke(box) != true) return false;
        if (player != null)
            for (int dy = 0; dy < Math.Ceiling(box.Y2 - .0001f); dy++)
                if (!Api.World.Claims.TryAccess(player, Pos.UpCopy(dy), EnumBlockAccessFlags.Use)) return false;
        if (!ServerRules.ItemOverlapAllowed)
            foreach (var kv in slots)
            {
                if (kv.Key == slotId || kv.Value is not { Empty: false }) continue;
                var other = DisplaySlotId.Decode(kv.Key);
                if (other != null && SameEmbeddedColumn(loc, other)) continue;
                var plane = SurfaceBehavior.GetSurface(other?.SurfaceIndex ?? -1);
                if (plane == null) continue;
                var properties = BlockBehaviorDisplaySurfaces.GetDisplayableAttributes(kv.Value, plane.DisplayCategory);
                if (properties?.Size != null && EmbeddedItemsOverlap(box, EmbeddedBounds(other, plane, properties,
                    ItemRotationDeg(kv.Key, properties, plane), slots, null))) return false;
            }
        return true;
    }
    public static readonly List<System.Func<BEUniversalDisplay, Cuboidf, bool>> PlacementRoomChecks = new();

    // Index only the cells actually reached. Aiming never scans a tall vertical
    // column or every loaded display. The world accessor separates client/server.
    sealed class ReachedStorageCell
    {
        readonly HashSet<BEUniversalDisplay> owners = new();
        volatile BEUniversalDisplay[] snapshot = Array.Empty<BEUniversalDisplay>();

        void Publish() => snapshot = owners.OrderBy(be => be.Pos.Y)
            .ThenBy(be => be.Pos.X).ThenBy(be => be.Pos.Z).ToArray();

        public void Add(BEUniversalDisplay owner)
        {
            lock (owners) if (owners.Add(owner)) Publish();
        }

        public void Remove(BEUniversalDisplay owner)
        {
            lock (owners) if (owners.Remove(owner)) Publish();
        }

        public BEUniversalDisplay[] Read()
        {
            var current = snapshot;
            foreach (var owner in current)
                if (owner._removed)
                {
                    lock (owners)
                    {
                        if (owners.RemoveWhere(be => be._removed) != 0) Publish();
                        return snapshot;
                    }
                }
            return current;
        }
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IBlockAccessor,
        System.Collections.Concurrent.ConcurrentDictionary<BlockPos, ReachedStorageCell>> embeddedReach = new();

    // Published arrays are read-only, like cached selection boxes. Sorting and
    // allocation happen when ownership changes, rather than on every aim query.
    public static BEUniversalDisplay[] StorageGuestsAt(IBlockAccessor accessor, BlockPos cell)
    {
        if (embeddedReach.TryGetValue(accessor, out var index) && index.TryGetValue(cell, out var owners))
            return owners.Read();
        return Array.Empty<BEUniversalDisplay>();
    }

    void PublishStorageReach(IEnumerable<(int, int, int)> before, IEnumerable<(int, int, int)> after)
    {
        if ((!IsEmbedded && !UnboundedStacking) || Api?.World?.BlockAccessor is not { } accessor || Pos == null) return;
        var index = embeddedReach.GetOrCreateValue(accessor);
        var wanted = after.ToHashSet();
        foreach (var (x, y, z) in before)
            if (!wanted.Contains((x, y, z)) && index.TryGetValue(Pos.AddCopy(x, y, z), out var old))
            {
                old.Remove(this);
                if (accessor.GetBlockEntity(Pos.AddCopy(x, y, z)) is BEUniversalDisplay host && host != this) host.GuestsChanged();
            }
        foreach (var (x, y, z) in wanted)
            index.GetOrAdd(Pos.AddCopy(x, y, z), _ => new()).Add(this);
    }

    Cuboidf EmbeddedBounds(DisplaySlotId loc, DisplaySurface surface, DisplayableAttributes attr, float rotation,
                          KeyValuePair<string, ItemSlot>[] slots, float? nudge)
    {
        var size = BlockBehaviorDisplaySurfaces.RotatedFootprint(attr.Size, rotation);
        float x = (surface.VoxelPosition.X + loc.X - size.Width / 2) / 16;
        float z = (surface.VoxelPosition.Z + loc.Z - size.Length / 2) / 16;
        float y = (surface.VoxelPosition.Y + SlotRestY(loc, surface, attr, slots, nudge)) / 16;
        return new Cuboidf(x, y, z, x + size.Width / 16, y + size.Height / 16, z + size.Length / 16);
    }

    // Fractional measured heights accumulate along the stack. Computing the next
    // item's bottom and the previous item's top can differ by a float rounding
    // step (e.g. two 1.6-voxel bowls), which is contact rather than penetration.
    // Use the same small tolerance as the native-voxel fit check, in block units.
    internal static bool SameEmbeddedColumn(DisplaySlotId a, DisplaySlotId b)
        => a.SurfaceIndex == b.SurfaceIndex && a.X == b.X && a.Z == b.Z;

    /// <summary>
    /// FIT SLACK (lib 1.3.1, Calm: "be looser with how the surfaces count the items as going into another box ... some
    /// items should go but the box is a touch too big"). Every fit check lets an item's box reach up to half a voxel
    /// into another item, a surface edge, a carved wall or a block above - measured boxes come out a little larger than
    /// what the eye sees. Only the CHECKS use it; the item is drawn and placed at its own size.
    /// </summary>
    internal const float FitSlackVoxels = .5f;
    internal const float FitSlack = FitSlackVoxels / 16f;

    /// <summary>A footprint as the fit checks see it: half a voxel smaller on every side (never below 0.1).</summary>
    internal static Size3f FitSize(Size3f size) => size == null ? null : new Size3f(
        Math.Max(.1f, size.Width - 2 * FitSlackVoxels), Math.Max(.1f, size.Height - 2 * FitSlackVoxels),
        Math.Max(.1f, size.Length - 2 * FitSlackVoxels));

    internal static bool EmbeddedItemsOverlap(Cuboidf a, Cuboidf b)
    {
        const float epsilon = FitSlack;
        return a.X2 > b.X1 + epsilon && a.X1 < b.X2 - epsilon
            && a.Y2 > b.Y1 + epsilon && a.Y1 < b.Y2 - epsilon
            && a.Z2 > b.Z1 + epsilon && a.Z1 < b.Z2 - epsilon;
    }

    /// <summary>
    /// CHISEL EDITING (trial, Calm 2026-10-19: chisel a block while items are in it, "edit around items"): every stored
    /// item's box in its host block (block units) and whether it stands on the carved floor itself - an item resting on
    /// another item (a stack or free stack) has nothing in the carving to lose.
    /// </summary>
    internal List<(Cuboidf Box, bool OnFloor)> EmbeddedItemFootprints()
    {
        var result = new List<(Cuboidf, bool)>();
        var slots = SlotsSnapshot();
        foreach (var kv in slots)
        {
            if (kv.Value is not { Empty: false }) continue;
            var loc = DisplaySlotId.Decode(kv.Key);
            var surface = SurfaceBehavior?.GetSurface(loc?.SurfaceIndex ?? -1);
            if (surface == null) continue;
            var attr = BlockBehaviorDisplaySurfaces.GetDisplayableAttributes(kv.Value, surface.DisplayCategory);
            if (attr?.Size == null) continue;
            result.Add((EmbeddedBounds(loc, surface, attr, ItemRotationDeg(kv.Key, attr, surface), slots, null),
                loc.Y == 0 && FreeData(kv.Key) == null));
        }
        return result;
    }

    public bool EmbeddedItemsIntersect(Cuboidf box)
    {
        var slots = SlotsSnapshot();
        foreach (var kv in slots)
        {
            if (kv.Value is not { Empty: false }) continue;
            var loc = DisplaySlotId.Decode(kv.Key);
            var surface = SurfaceBehavior?.GetSurface(loc?.SurfaceIndex ?? -1);
            if (surface == null) continue;
            var attr = BlockBehaviorDisplaySurfaces.GetDisplayableAttributes(kv.Value, surface.DisplayCategory);
            if (attr?.Size != null && EmbeddedItemsOverlap(box, EmbeddedBounds(loc, surface, attr,
                    ItemRotationDeg(kv.Key, attr, surface), slots, null))) return true;
        }
        return false;
    }

    volatile EmbeddedMesh[] embeddedMeshes = Array.Empty<EmbeddedMesh>();
    bool embeddedDirty = true, embeddedQueued;
    sealed record EmbeddedMesh(MeshData Mesh, ColorMapData? ColorMap, int Lod);
    bool embeddedFailureLogged;

    void QueueEmbeddedMeshes()
    {
        embeddedDirty = true;
        if (capi == null || embeddedQueued || _removed) return;
        embeddedQueued = true;
        capi.Event.EnqueueMainThreadTask(() => { embeddedQueued = false; RefreshEmbeddedMeshes(); }, "udl-embedded-mesh");
    }

    // Host tesselation only consumes this immutable main-thread snapshot. It never
    // resolves collectibles, enumerates the inventory, or creates embedded displays.
    void RefreshEmbeddedMeshes()
    {
        if (!IsEmbedded || capi == null || _removed || (!embeddedDirty && meshesGenerated)) return;
        try
        {
            var sink = new EmbeddedMeshSink();
            OnTesselation(sink, capi.Tesselator);
            GetSelectionBoxes();
            embeddedMeshes = sink.Meshes.ToArray();
            embeddedDirty = false;
            EmbeddedHost.MarkDirty(true);
        }
        catch (Exception e)
        {
            if (!embeddedFailureLogged) capi.Logger.Warning("[UniversalDisplayLib] Embedded display mesh failed: {0}", e);
            embeddedFailureLogged = true;
        }
    }

    public void AddEmbeddedMeshes(ITerrainMeshPool pool)
    {
        foreach (var part in embeddedMeshes)
        {
            if (part.ColorMap == null) pool.AddMeshData(part.Mesh.Clone(), part.Lod);
            else pool.AddMeshData(part.Mesh.Clone(), part.ColorMap.Value, part.Lod);
        }
    }

    sealed class EmbeddedMeshSink : ITerrainMeshPool
    {
        public readonly List<EmbeddedMesh> Meshes = new();
        public void AddMeshData(MeshData mesh, int lodLevel = 1) => Meshes.Add(new(mesh.Clone(), null, lodLevel));
        public void AddMeshData(MeshData mesh, float[] matrix, int lodLevel = 1)
            => Meshes.Add(new(mesh.Clone().MatrixTransform(matrix), null, lodLevel));
        public void AddMeshData(MeshData mesh, ColorMapData map, int lodLevel = 1)
            => Meshes.Add(new(mesh.Clone(), map, lodLevel));
    }
}
