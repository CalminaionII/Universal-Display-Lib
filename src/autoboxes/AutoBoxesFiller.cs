using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;   // UDL: ICustomSelectionBoxRender

namespace UniversalDisplayLib;   // CHANGE: the namespace your own code files use (the word after "namespace" at their top)

/// <summary>
/// The filler (<c>yourmod:autoboxesfiller</c>) - an invisible block in a space its owner's box reaches into.
/// Draws nothing, is solid only where its owner says so (its own piece of the box), gives the owner's box as its outline, and passes
/// a click, a break, the name, the info and the help on to the owner as if the owner's own block
/// had been aimed at. Placed and removed only by <see cref="AutoBoxes"/>; drops nothing.
///
/// UNIVERSAL DISPLAY LIB'S COPY differs from the kit (Tools/AutoBoxes Kit) in places marked "UDL:" (2026-10-06/07):
/// only the box aimed at is outlined (a tall window's two leaves are two outlines), a filler that is only solid there
/// (no outline) still counts as owned on reload, and in its owner's OWN spaces (a tall window's upper blocks -
/// IAutoBoxesPartOfOwner) it seals rooms and holds torches exactly as the owner's own block does, open or shut, it
/// sounds like its owner when hit or broken, and a space can be SHARED by more than one owner (two shutters folding into one
/// gap): every sharer's boxes show and are solid there, and a click or break goes to the one whose box is aimed at.
/// </summary>
public class BlockAutoBoxesFiller : Block, ICustomSelectionBoxRender
{
    // UDL: outlines drawn here, so an item's box from a window below follows that window's "Show Placed Item Box" setting
    // (Calm, 2026-10-10: "the boxes show for items when looking past the block, need that to be disabled when the setting
    // is changed like normal"). Everything else draws as the game would: the aimed box only (partial selection).
    public void RenderSelectionBoxes(BlockSelection blockSel, RenderBoxDelegate renderBoxHandler)
    {
        if (api is not ICoreClientAPI capi || blockSel?.Position == null) return;
        Cuboidf[] boxes = GetSelectionBoxes(capi.World.BlockAccessor, blockSel.Position);
        if (blockSel.SelectionBoxIndex < 0 || blockSel.SelectionBoxIndex >= boxes.Length) return;
        Cuboidf hovered = boxes[blockSel.SelectionBoxIndex];
        if (BEUniversalDisplay.IsHiddenPartBox((hovered as CuboidfWithId)?.Id)) return;
        // Both pads and placed items need the owner's complete preview. An upper
        // filler used to forward only pads, losing the flower preview on a pot.
        // ONLY those (lib 1.3.1, Calm 2026-10-20: "no box when open but is when closed" on every External Shutter): the
        // owner draws by looking the id up among its OWN block's boxes, and an open leaf lying wholly in this space is not
        // there - so it drew nothing and the leaf had no outline. Leaves and frame parts are drawn here, as before 1.3.0.
        DisplaySlotId slot = DisplaySlotId.Decode((hovered as CuboidfWithId)?.Id);
        if (slot != null)
        {
            BlockSelection aimedOwner = ToOwner(capi.World.BlockAccessor, blockSel);
            if (BlockBehaviorDisplaySurfaces.RenderOwnerSelection(capi.World, blockSel, aimedOwner, renderBoxHandler)) return;
        }
        if (slot is { IsPlacedItem: true } && !BlockBehaviorDisplaySurfaces.ShouldDrawPlacedItemBox()) return;
        renderBoxHandler(hovered, 1.6f * capi.Settings.Float["wireframethickness"], GetSelectionColor(capi, blockSel.Position));
    }

    // UDL: only the box aimed at is outlined - otherwise every box the owner has in this space shows at once
    public override bool DoPartialSelection(IWorldAccessor world, BlockPos pos) => true;

    static BEAutoBoxesFiller Be(IBlockAccessor ba, BlockPos pos) => ba.GetBlockEntity(pos) as BEAutoBoxesFiller;

    static BlockPos OwnerOf(IBlockAccessor ba, BlockPos pos)
    {
        var owner = Be(ba, pos)?.Owner;
        return owner != null && AutoBoxes.Valid(ba, owner) ? owner : null;
    }

    // UDL: the owners sharing this space after the owner (IAutoBoxesOwner only), in the order their boxes are listed
    static IEnumerable<IAutoBoxesOwner> Sharing(IBlockAccessor ba, BEAutoBoxesFiller be) =>
        be.Sharers.Select(s => AutoBoxes.OwnerAt(ba, s)).Where(o => o != null);

    // UDL: whose box is aimed at - by its index in GetSelectionBoxes (the owner's boxes first, then each sharer's); the owner when unsure
    static BlockPos AimedOwner(IBlockAccessor ba, BlockPos pos, int boxIndex)
    {
        var be = Be(ba, pos);
        var owner = OwnerOf(ba, pos);
        if (be == null || owner == null || be.Sharers.Count == 0) return owner;
        int n = boxIndex;
        foreach (var who in be.AllOwners())
        {
            if (AutoBoxes.OwnerAt(ba, who) is not { } o) continue;
            int count = o.AutoBoxesAt(pos).Length;
            if (n < count) return who;
            n -= count;
        }
        return owner;
    }

    public override Cuboidf[] GetSelectionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
    {
        var be = Be(blockAccessor, pos);
        var owner = OwnerOf(blockAccessor, pos);
        if (owner == null) return [];
        if (AutoBoxes.OwnerAt(blockAccessor, owner) is { } o)
            return be.Sharers.Count == 0 ? o.AutoBoxesAt(pos) : o.AutoBoxesAt(pos).Concat(Sharing(blockAccessor, be).SelectMany(s => s.AutoBoxesAt(pos))).ToArray();
        // an owner that is not ours: the box this filler carries, seen from here
        return be.Carried == null ? [] : [AutoBoxes.Whole(be.Carried, (pos.X - owner.X, pos.Y - owner.Y, pos.Z - owner.Z))];
    }

    /// <summary>This space's own piece of the owner's box when the owner is solid (collision is tested space by space).</summary>
    public override Cuboidf[] GetCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
    {
        var be = Be(blockAccessor, pos);
        var owner = be?.Owner;
        if (owner == null) return [];
        if (AutoBoxes.OwnerAt(blockAccessor, owner) is { } o)
            return be.Sharers.Count == 0 ? o.AutoCollisionAt(pos) ?? [] : (o.AutoCollisionAt(pos) ?? []).Concat(Sharing(blockAccessor, be).SelectMany(s => s.AutoCollisionAt(pos) ?? [])).ToArray();
        if (!be.Solid || be.Carried == null || !AutoBoxes.Valid(blockAccessor, owner)) return [];
        return be.CollisionPiece();
    }

    public override Cuboidf[] GetParticleCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos) => GetCollisionBoxes(blockAccessor, pos);

    /// <summary>The selection as the owner's own block would have seen it: its position, the hit point re-measured from there.</summary>
    static BlockSelection Forwarded(BlockSelection selection, BlockPos owner)
    {
        var forwarded = selection.Clone();
        forwarded.Position = owner.Copy();
        if (selection.HitPosition != null)
            forwarded.HitPosition = selection.HitPosition.AddCopy(selection.Position.X - owner.X, selection.Position.Y - owner.Y, selection.Position.Z - owner.Z);
        forwarded.SelectionBoxIndex = 0;
        return forwarded;
    }

    /// <summary>
    /// UDL: a selection of a filler as its aimed owner's own block would see it, the aimed box's id kept - for code that reads
    /// the player's selection itself rather than through a block method (the display's mouse wheel, placing ghost and editor).
    /// Any other selection comes back unchanged. (2026-10-10: an item stacked above its shelf could not be turned or raised
    /// and showed no stacking ghost - the aim had reached the filler above, and those only looked at the aimed block.)
    /// </summary>
    public static BlockSelection ToOwner(IBlockAccessor ba, BlockSelection selection)
    {
        if (selection?.Position == null || ba?.GetBlock(selection.Position) is not BlockAutoBoxesFiller filler) return selection;
        var owner = AimedOwner(ba, selection.Position, selection.SelectionBoxIndex);
        if (owner == null) return selection;
        var forwarded = Forwarded(selection, owner);
        if (forwarded.SelectionBoxId == null)
        {
            Cuboidf[] boxes = filler.GetSelectionBoxes(ba, selection.Position);
            int i = selection.SelectionBoxIndex;
            if (i >= 0 && i < boxes.Length) forwarded.SelectionBoxId = (boxes[i] as CuboidfWithId)?.Id;
        }
        return forwarded;
    }

    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        BlockSelection aimed = ToOwner(world.BlockAccessor, blockSel);
        if (BEUniversalDisplay.IsHiddenPartBox(aimed?.SelectionBoxId)) return true;
        var owner = AimedOwner(world.BlockAccessor, blockSel.Position, blockSel.SelectionBoxIndex);
        if (owner != null && BEUniversalDisplay.At(world.BlockAccessor, owner) is { IsEmbedded: true } embedded)
            return embedded.OnBlockInteractStart(world, byPlayer, aimed);
        return owner != null && world.BlockAccessor.GetBlock(owner).OnBlockInteractStart(world, byPlayer, Forwarded(blockSel, owner));
    }

    public override bool OnBlockInteractStep(float secondsUsed, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        var owner = AimedOwner(world.BlockAccessor, blockSel.Position, blockSel.SelectionBoxIndex);
        return owner != null && world.BlockAccessor.GetBlock(owner).OnBlockInteractStep(secondsUsed, world, byPlayer, Forwarded(blockSel, owner));
    }

    public override void OnBlockInteractStop(float secondsUsed, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        var owner = AimedOwner(world.BlockAccessor, blockSel.Position, blockSel.SelectionBoxIndex);
        if (owner != null) world.BlockAccessor.GetBlock(owner).OnBlockInteractStop(secondsUsed, world, byPlayer, Forwarded(blockSel, owner));
    }

    /// <summary>Breaking any part breaks the owner, which takes its fillers with it.</summary>
    public override void OnBlockBroken(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1)
    {
        if (world.Side != EnumAppSide.Server) return;
        var aimed = byPlayer?.CurrentBlockSelection;
        var owner = aimed?.Position?.Equals(pos) == true ? AimedOwner(world.BlockAccessor, pos, aimed.SelectionBoxIndex) : OwnerOf(world.BlockAccessor, pos);
        if (owner == null)
        {
            world.BlockAccessor.SetBlock(0, pos);
            return;
        }
        // Embedded item targets are taken with right-click, just as in their host.
        if (BEUniversalDisplay.At(world.BlockAccessor, owner) is { IsEmbedded: true }) return;
        if (byPlayer != null && world.Claims.TestAccess(byPlayer, owner, EnumBlockAccessFlags.BuildOrBreak) != EnumWorldAccessResponse.Granted) return;
        world.BlockAccessor.BreakBlock(owner, byPlayer, dropQuantityMultiplier);
    }

    public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1) => [];

    // UDL: the hit and break sounds the breaking player hears come from the block being broken (Block.OnGettingBroken asks
    // GetSounds) - so the top of a tall window sounded like the filler's leather, not glass. The owner's own break sound
    // (SpawnDropsAndRemoveBlock) only reaches the OTHER players.
    public override BlockSounds GetSounds(IBlockAccessor blockAccessor, BlockSelection blockSel, ItemStack stack = null)
    {
        var owner = blockSel?.Position == null ? null : AimedOwner(blockAccessor, blockSel.Position, blockSel.SelectionBoxIndex);
        return owner != null ? blockAccessor.GetBlock(owner).GetSounds(blockAccessor, Forwarded(blockSel, owner), stack) : base.GetSounds(blockAccessor, blockSel, stack);
    }

    // UDL: in its owner's own space, answer as the owner's block - a shut tall window seals the room in its upper blocks
    // too, an open one lets the air through there as it does in its own block; torches and signs attach as on the window.
    // Spaces a leaf only swings into (the External Shutter's sides) stay plain fillers.
    BlockPos WholeOf(IBlockAccessor ba, BlockPos pos)
    {
        var owner = ba == null ? null : OwnerOf(ba, pos);
        return owner != null && ba.GetBlockEntity(owner) is IAutoBoxesPartOfOwner o && o.IsPartOfOwner(pos) ? owner : null;
    }

    public override int GetRetention(BlockPos pos, BlockFacing facing, EnumRetentionType type)
    {
        var ba = api?.World?.BlockAccessor;
        var owner = WholeOf(ba, pos);
        return owner == null ? base.GetRetention(pos, facing, type) : ba.GetBlock(owner).GetRetention(owner, facing, type);
    }

    public override bool CanAttachBlockAt(IBlockAccessor blockAccessor, Block block, BlockPos pos, BlockFacing blockFace, Cuboidi attachmentArea = null)
    {
        var owner = WholeOf(blockAccessor, pos);
        return owner == null
            ? base.CanAttachBlockAt(blockAccessor, block, pos, blockFace, attachmentArea)
            : blockAccessor.GetBlock(owner).CanAttachBlockAt(blockAccessor, block, owner, blockFace, attachmentArea);
    }

    public override ItemStack OnPickBlock(IWorldAccessor world, BlockPos pos)
    {
        var owner = OwnerOf(world.BlockAccessor, pos);
        return owner == null ? null : world.BlockAccessor.GetBlock(owner).OnPickBlock(world, owner);
    }

    public override string GetPlacedBlockName(IWorldAccessor world, BlockPos pos)
    {
        var owner = OwnerOf(world.BlockAccessor, pos);
        return owner == null ? base.GetPlacedBlockName(world, pos) : world.BlockAccessor.GetBlock(owner).GetPlacedBlockName(world, owner);
    }

    public override string GetPlacedBlockInfo(IWorldAccessor world, BlockPos pos, IPlayer forPlayer)
    {
        var owner = OwnerOf(world.BlockAccessor, pos);
        return owner == null ? "" : world.BlockAccessor.GetBlock(owner).GetPlacedBlockInfo(world, owner, forPlayer);
    }

    public override WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer)
    {
        if (BEUniversalDisplay.IsHiddenPartBox(ToOwner(world.BlockAccessor, selection)?.SelectionBoxId)) return [];
        var owner = AimedOwner(world.BlockAccessor, selection.Position, selection.SelectionBoxIndex);
        return owner == null ? [] : world.BlockAccessor.GetBlock(owner).GetPlacedBlockInteractionHelp(world, Forwarded(selection, owner), forPlayer);
    }
}

/// <summary>The filler's memory: whose box it stands in for. An owner that is gone -> the filler removes itself.</summary>
public class BEAutoBoxesFiller : BlockEntity
{
    public BlockPos Owner { get; private set; }

    /// <summary>UDL: other owners whose boxes are in this space too (two shutters folding into one gap). IAutoBoxesOwner only.</summary>
    public List<BlockPos> Sharers { get; } = new();

    public bool HasSharer(BlockPos p) => p != null && Sharers.Any(s => s.Equals(p));

    public void AddSharer(BlockPos p)
    {
        if (p == null || HasSharer(p) || p.Equals(Owner)) return;
        Sharers.Add(p.Copy());
        MarkDirty(true);
    }

    public void RemoveSharer(BlockPos p)
    {
        if (Sharers.RemoveAll(s => s.Equals(p)) > 0) MarkDirty(true);
    }

    /// <summary>The first sharer, taken off the list (to become the owner when the owner leaves); null if none.</summary>
    public BlockPos TakeFirstSharer()
    {
        if (Sharers.Count == 0) return null;
        BlockPos first = Sharers[0];
        Sharers.RemoveAt(0);
        return first;
    }

    /// <summary>The owner, then every sharer.</summary>
    public IEnumerable<BlockPos> AllOwners()
    {
        if (Owner != null) yield return Owner;
        foreach (var s in Sharers) yield return s;
    }

    /// <summary>The owner's whole box (owner's frame) - only for an owner that cannot implement the interface.</summary>
    public Cuboidf Carried { get; private set; }

    /// <summary>Carried boxes only: whether this space's piece is solid.</summary>
    public bool Solid { get; private set; }

    private Cuboidf[] piece;

    /// <summary>This space's own piece of the carried box.</summary>
    public Cuboidf[] CollisionPiece() => piece ??= Owner == null ? [] : AutoBoxes.PieceAt(Carried, (Pos.X - Owner.X, Pos.Y - Owner.Y, Pos.Z - Owner.Z));

    public void SetOwner(BlockPos owner, Cuboidf carried = null, bool solid = false)
    {
        Owner = owner.Copy();
        Carried = carried?.Clone();
        Solid = solid;
        piece = null;
        MarkDirty(true);
    }

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        // An owner removed some other way (a world edit, the mod out and back) would leave an
        // invisible obstacle. Check once after load; an unloaded owner is waited for, not judged.
        // A carried box's owner is a block that is not ours and tells us nothing when it empties,
        // so those fillers keep checking (every 2 s); the others check once.
        if (api.Side == EnumAppSide.Server) RegisterDelayedCallback(_ => CheckOwner(), 2000);
    }

    void CheckOwner()
    {
        if (Owner != null && Api.World.BlockAccessor.GetChunkAtBlockPos(Owner) == null)
        {
            RegisterDelayedCallback(_ => CheckOwner(), 2000);
            return;
        }
        var ba = Api.World.BlockAccessor;
        bool owned = Owner != null && (AutoBoxes.OwnerAt(ba, Owner) is { } o
            ? o.AutoBoxesAt(Pos).Length > 0 || o.AutoCollisionAt(Pos).Length > 0   // UDL: solid-only counts too
              || o is IAutoBoxesPartOfOwner own && own.IsPartOfOwner(Pos)           // UDL: and the owner's own spaces, held even when empty
            : Carried != null && AutoBoxes.Valid(ba, Owner));
        // UDL: sharers that no longer reach here (unloaded ones are waited for) leave; a gone owner hands over to a sharer
        if (Sharers.RemoveAll(s => ba.GetChunkAtBlockPos(s) != null
                && !(AutoBoxes.OwnerAt(ba, s) is { } so && (so.AutoBoxesAt(Pos).Length > 0 || so.AutoCollisionAt(Pos).Length > 0))) > 0) MarkDirty(true);
        if (!owned && Sharers.Count > 0)
        {
            SetOwner(TakeFirstSharer());
            return;
        }
        if (!owned)
        {
            if (ba.GetBlock(Pos) is BlockAutoBoxesFiller) ba.SetBlock(0, Pos);
            return;
        }
        if (Carried != null) RegisterDelayedCallback(_ => CheckOwner(), 2000);
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        if (Owner != null) { tree.SetInt("ownerX", Owner.X); tree.SetInt("ownerY", Owner.Y); tree.SetInt("ownerZ", Owner.Z); }
        if (Sharers.Count > 0) tree.SetString("sharers", string.Join(";", Sharers.Select(s => $"{s.X},{s.Y},{s.Z}")));   // UDL
        if (Carried != null)
        {
            tree.SetFloat("boxX1", Carried.X1); tree.SetFloat("boxY1", Carried.Y1); tree.SetFloat("boxZ1", Carried.Z1);
            tree.SetFloat("boxX2", Carried.X2); tree.SetFloat("boxY2", Carried.Y2); tree.SetFloat("boxZ2", Carried.Z2);
            tree.SetBool("solid", Solid);
        }
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
    {
        base.FromTreeAttributes(tree, worldAccessForResolve);
        Owner = tree.HasAttribute("ownerX") ? new BlockPos(tree.GetInt("ownerX"), tree.GetInt("ownerY"), tree.GetInt("ownerZ"), Pos.dimension) : null;
        Carried = tree.HasAttribute("boxX1")
            ? new Cuboidf(tree.GetFloat("boxX1"), tree.GetFloat("boxY1"), tree.GetFloat("boxZ1"), tree.GetFloat("boxX2"), tree.GetFloat("boxY2"), tree.GetFloat("boxZ2"))
            : null;
        Solid = tree.GetBool("solid");
        Sharers.Clear();   // UDL
        foreach (var (x, y, z) in AutoBoxes.Read(tree.GetString("sharers"))) Sharers.Add(new BlockPos(x, y, z, Pos.dimension));
        piece = null;
        if (Owner != null && Carried != null) AutoBoxes.CarriedSeen?.Invoke(Owner, Carried, Solid);
        if (Api?.Side == EnumAppSide.Client) Api.World.BlockAccessor.MarkBlockDirty(Pos);
    }
}
