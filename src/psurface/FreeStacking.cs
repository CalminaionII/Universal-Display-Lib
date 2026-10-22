using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace UniversalDisplayLib;

public partial class BEUniversalDisplay
{
    // IDs retain the legacy format; physical height and support are independent of
    // the ID's level. Immutable published maps are safe for mesh/selection readers.
    public static bool FreeStackingEnabled = true;
    sealed record FreeStackData(string Parent, float RestY);
    sealed record FreeProposal(string Slot, FreeStackData Data);
    volatile Dictionary<string, FreeStackData> freeStacks = new();
    volatile FreeProposal freeProposal;

    FreeStackData FreeData(string id)
    {
        if (id == null) return null;
        var map = freeStacks;
        if (map.TryGetValue(id, out var data)) return data;
        var preview = freeProposal;
        return preview?.Slot == id ? preview.Data : null;
    }

    bool FreeDescendsFrom(string child, string ancestor)
    {
        var seen = new HashSet<string>();
        while (child != null && seen.Add(child))
        {
            var record = FreeData(child);
            if (record == null) return false;
            if (record.Parent == ancestor) return true;
            child = record.Parent;
        }
        return false;
    }

    bool HasFreeChildren(string id) => freeStacks.Values.Any(row => row.Parent == id);

    /// <summary>
    /// TOP ITEM FIRST (Calm, 2026-10-19: "you have to take the top item of the stack off ... should work on all of
    /// them"): an item with anything resting on it cannot be taken or raised/lowered until what is on top is off -
    /// free-stacked children, and in stacks from before 1.3.1 the items above it in its column. Turning it is
    /// allowed (Calm: "turning is fine"). Every display: shelves, windows, ground, carved floors.
    /// </summary>
    bool RefuseFreeSupportEdit(string id)
    {
        var loc = DisplaySlotId.Decode(id);
        bool occupied = HasFreeChildren(id);
        if (!occupied && loc != null)
            foreach (var kv in SlotsSnapshot())
            {
                if (kv.Key == id || kv.Value is not { Empty: false }) continue;
                var other = DisplaySlotId.Decode(kv.Key);
                if (other != null && other.Y > loc.Y && SameEmbeddedColumn(loc, other)) { occupied = true; break; }
            }
        if (occupied) (Api as ICoreClientAPI)?.TriggerIngameError(this, "freestackoccupied",
            Lang.Get("universaldisplaylib:stack-take-top-first"));
        return occupied;
    }

    /// <summary>Ray hit in block coordinates -> display-local voxel coordinates.</summary>
    public Vec3f FreeStackPoint(Vec3d hit, DisplaySurface surface)
    {
        double x = hit.X - .5, z = hit.Z - .5;
        double c = Math.Cos(MeshAngleRad), s = Math.Sin(MeshAngleRad);
        // Same inverse as Rotated(), which publishes selection/collision boxes.
        return new Vec3f((float)((c*x-s*z+.5)*16)-surface.VoxelPosition.X, 0,
            (float)((s*x+c*z+.5)*16)-surface.VoxelPosition.Z);
    }

    public bool TryResolveFreeStack(DisplaySlotId aimed, DisplayableAttributes held,
        BlockSelection selection, IPlayer player, out DisplaySlotId target, out float rotation, out bool fits)
    {
        target = null; rotation = 0; fits = false;
        var surface = SurfaceBehavior?.GetSurface(aimed?.SurfaceIndex ?? -1);
        if (surface == null || selection?.HitPosition == null || held?.Size == null
            || !inv.SlotsByslotId.TryGetValue(aimed.Encoded, out var supportSlot) || supportSlot is not { Empty: false }) return false;
        var support = BlockBehaviorDisplaySurfaces.GetDisplayableAttributes(supportSlot, surface.DisplayCategory);
        if (support?.Size == null) return false;
        // Starts FACING THE PLAYER like a first placement, not at the angle of the item below (Calm, 2026-10-19:
        // "They dont have to face the same way as the first item ... Or the item below in general"). A turn chosen
        // with Sprint + wheel before placing still wins (ForSurface returns it).
        rotation = PlacementRotation.ForSurface(Api, player, this, surface);
        var point = FreeStackPoint(selection.HitPosition, surface);
        // The fine placement grid (server precise-placement rule); no forced centre snap.
        float grid = ServerRules.PrecisePlacementOn ? 2f : 1f;
        float x = (float)Math.Round(point.X*grid)/grid, z = (float)Math.Round(point.Z*grid)/grid;
        // OVERHANG ALLOWED (Calm, 2026-10-19): the item goes where it is aimed on the item below, even if it hangs
        // over its edges - it only has to stay on the display surface and clear of other items (FreePlacementFits).
        // IDs must not contain negative coordinates (legacy decoder uses '-').
        x = Math.Max(0, x); z = Math.Max(0, z);
        target = new DisplaySlotId { SurfaceIndex=aimed.SurfaceIndex, X=x, Z=z, Y=aimed.Y+1, IsPlacedItem=true };
        while (inv.SlotsByslotId.TryGetValue(target.Encoded, out var occupied) && occupied is { Empty: false }) target=target.UpCopy();
        float rest = SlotRestY(aimed, surface, support) + support.Size.Height;
        freeProposal = new FreeProposal(target.Encoded, new FreeStackData(aimed.Encoded, rest));
        fits = FreePlacementFits(target, held, rotation, PlacementHeight.For(Api, player), player);
        return true;
    }

    bool FreePlacementFits(DisplaySlotId loc, DisplayableAttributes attr, float rotation, float nudge, IPlayer player)
    {
        var record = FreeData(loc?.Encoded);
        var surface = SurfaceBehavior?.GetSurface(loc?.SurfaceIndex ?? -1);
        if (record == null || surface == null || attr?.Size == null
            || !inv.SlotsByslotId.TryGetValue(record.Parent, out var parentSlot) || parentSlot is not { Empty:false }) return false;
        var parentLoc = DisplaySlotId.Decode(record.Parent);
        var parent = BlockBehaviorDisplaySurfaces.GetDisplayableAttributes(parentSlot, surface.DisplayCategory);
        if (parent?.Size == null) return false;
        var size = BlockBehaviorDisplaySurfaces.RotatedFootprint(attr.Size, rotation + SurfaceFacingDeg(surface));
        if (loc.X-size.Width/2 < -FitSlackVoxels || loc.Z-size.Length/2 < -FitSlackVoxels
            || loc.X+size.Width/2 > surface.Size.Width+FitSlackVoxels || loc.Z+size.Length/2 > surface.Size.Length+FitSlackVoxels
            || (!UnboundedStacking && record.RestY >= surface.Size.Height)) return false;
        var slots = SlotsSnapshot();
        var candidate = EmbeddedBounds(loc, surface, attr, rotation+SurfaceFacingDeg(surface), slots, nudge);
        if (candidate.Y1 < surface.VoxelPosition.Y/16f-.0001f) return false;
        if (!ServerRules.ItemOverlapAllowed)
            foreach (var kv in slots)
            {
                if (kv.Value is not { Empty:false } || kv.Key==loc.Encoded || FreeDescendsFrom(loc.Encoded, kv.Key)) continue;
                var other=DisplaySlotId.Decode(kv.Key);
                var plane=SurfaceBehavior.GetSurface(other?.SurfaceIndex ?? -1);
                if (plane==null) continue;
                var properties=BlockBehaviorDisplaySurfaces.GetDisplayableAttributes(kv.Value, plane.DisplayCategory);
                if (properties?.Size!=null && EmbeddedItemsOverlap(candidate, EmbeddedBounds(other,plane,properties,
                    ItemRotationDeg(kv.Key,properties,plane),slots,null))) return false;
            }
        // Native carved voxels, shared-space room checks and claims stay authoritative.
        return EmbeddedPlacementFitsCore(loc,attr,rotation,player,loc.Encoded,nudge);
    }

    bool TryPutFreeStack(IPlayer player, BlockSelection selection, DisplaySlotId aimed, DisplayableAttributes held)
    {
        if (!TryResolveFreeStack(aimed,held,selection,player,out var target,out float rotation,out bool fits)) return false;
        if (!fits)
        {
            (Api as ICoreClientAPI)?.TriggerIngameError(this,"freestackfit",Lang.Get("universaldisplaylib:stack-needs-space"));
            return true;
        }
        var proposal=freeProposal;
        // Publish metadata BEFORE PlaceItem publishes meshes/save/light state.
        var before=freeStacks;
        freeStacks=new Dictionary<string,FreeStackData>(before) { [target.Encoded]=proposal.Data };
        bool placed=PlaceItem(player,player.InventoryManager.ActiveHotbarSlot,target.Encoded,(int)Math.Round(rotation));
        if (!placed) freeStacks=before;
        freeProposal=null;
        return placed;
    }

    void ForgetFreeStack(string id)
    {
        var next=new Dictionary<string,FreeStackData>(freeStacks); next.Remove(id); freeStacks=next; freeProposal=null;
    }

    void WriteFreeStacks(ITreeAttribute tree)
    {
        if (freeStacks.Count==0) { tree.RemoveAttribute("freeStacking"); return; }
        var saved=new TreeAttribute();
        foreach(var row in freeStacks)
        {
            var entry=new TreeAttribute(); entry.SetString("support",row.Value.Parent); entry.SetFloat("restY",row.Value.RestY); saved[row.Key]=entry;
        }
        tree["freeStacking"]=saved;
    }

    void ReadFreeStacks(ITreeAttribute tree)
    {
        var next=new Dictionary<string,FreeStackData>();
        if (tree.GetTreeAttribute("freeStacking") is { } saved)
            foreach(var row in saved)
                if (row.Value is ITreeAttribute entry)
                {
                    string parent=entry.GetString("support"); float y=entry.GetFloat("restY",float.NaN);
                    if (DisplaySlotId.Decode(row.Key)!=null && DisplaySlotId.Decode(parent)!=null && row.Key!=parent && float.IsFinite(y) && y>=0)
                        next[row.Key]=new FreeStackData(parent,y);
                }
        freeStacks=next; freeProposal=null;
    }
}
