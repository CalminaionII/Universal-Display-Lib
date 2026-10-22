using System;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace UniversalDisplayLib
{
    /// <summary>
    /// Block class for the psurface window storage prototype.
    ///
    /// Derives from <see cref="BlockGeneric"/> — this is load-bearing, not
    /// cosmetic: only BlockGeneric walks the StrongBlockBehavior list and
    /// aggregates GetSelectionBoxes / GetCollisionBoxes / DoPartialSelection.
    /// Plain Block silently ignores them, which is why ARL's own box overrides
    /// never fired on the pre-psurface BlockWindowStorageLib.
    /// </summary>
    public class BlockUniversalDisplay : BlockGeneric
    {
        public override bool TryPlaceBlock(IWorldAccessor world, IPlayer byPlayer, ItemStack itemstack, BlockSelection blockSel, ref string failureCode)
        {
            // Always place the closed variant, so pane state starts consistent
            Block closedBlock = world.GetBlock(CodeWithVariant("state", "closed"));
            ItemStack placeStack = itemstack;

            if (closedBlock != null && closedBlock.Id != Id)
            {
                placeStack = new ItemStack(closedBlock);
                if (itemstack?.Attributes != null) placeStack.Attributes.MergeTree(itemstack.Attributes);
            }

            float angle = SnapPlacementAngle(byPlayer, blockSel);

            // Height detection: every block the window reaches (a tall window's upper blocks) has to be free before any of
            // it is placed - otherwise it would draw through a ceiling and have no filler there.
            Block baseBlock = closedBlock ?? this;
            foreach (var cell in BEUniversalDisplay.ShutCells(world.Api, baseBlock, angle))
            {
                if (cell == (0, 0, 0)) continue;
                BlockPos at = blockSel.Position.AddCopy(cell.x, cell.y, cell.z);
                Block there = world.BlockAccessor.GetBlock(at, BlockLayersAccess.Solid);
                bool free = (there.Id == 0 || (there.Replaceable >= 6000 && there is not BlockAutoBoxesFiller))
                    && world.BlockAccessor.GetBlock(at, BlockLayersAccess.Fluid).Id == 0;
                bool allowed = byPlayer == null || world.Claims.TestAccess(byPlayer, at, EnumBlockAccessFlags.BuildOrBreak) == EnumWorldAccessResponse.Granted;
                if (!free || !allowed)
                {
                    failureCode = "requireair";
                    return false;
                }
            }

            if (!base.TryPlaceBlock(world, byPlayer, placeStack, blockSel, ref failureCode)) return false;

            if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is BEUniversalDisplay be)
            {
                be.MeshAngleRad = angle;
                // the box turns with the window, and a window taller than its block gets fillers (AutoBoxes Kit, 2C)
                be.AutoBoxesAngleChanged();
                be.MarkMeshesDirty();
                be.InvalidateBoxes();

                if (world.Side == EnumAppSide.Server) be.MarkDirty(true);
                else be.RefreshAnimator();   // same frame, so the window never renders unrotated
            }

            return true;
        }

        /// <summary>Faces the window at the player, snapped to 90°.</summary>
        private static float SnapPlacementAngle(IPlayer byPlayer, BlockSelection blockSel)
        {
            // placed by something that is not a player (another mod's code, a command): no one to face
            if (byPlayer?.Entity?.Pos == null || blockSel?.HitPosition == null) return 0f;
            BlockPos pos = blockSel.DidOffset ? blockSel.Position.AddCopy(blockSel.Face.Opposite) : blockSel.Position;
            double dx = byPlayer.Entity.Pos.X - (pos.X + blockSel.HitPosition.X);
            double dz = byPlayer.Entity.Pos.Z - (pos.Z + blockSel.HitPosition.Z);
            float yaw = (float)Math.Atan2(dx, dz);
            return (float)Math.Round(yaw / (GameMath.PI / 2f)) * (GameMath.PI / 2f);
        }

        public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            if (world.BlockAccessor.GetBlockEntity(blockSel?.Position) is BEUniversalDisplay be
                && be.OnBlockInteractStart(world, byPlayer, blockSel))
            {
                return true;
            }

            return base.OnBlockInteractStart(world, byPlayer, blockSel);
        }

        // ── Another window's leaf open into this block (BEUniversalDisplay Guests, 2026-10-07) ──
        // Breaking it, its sounds, its name and its info are that window's, as a click on it is.

        /// <summary>The guest selection the player at this block aims at, or null.</summary>
        static BlockSelection AimedGuest(IWorldAccessor world, BlockPos pos, BlockSelection sel)
        {
            if (sel?.Position == null || !sel.Position.Equals(pos)) return null;
            string id = world.BlockAccessor.GetBlockEntity(pos) is BEUniversalDisplay be ? be.AimedBoxId(sel) : sel.SelectionBoxId;
            return BEUniversalDisplay.GuestSelection(sel, id, out BlockSelection forwarded) ? forwarded : null;
        }

        static BlockSelection LocalPlayerAim(IWorldAccessor world, BlockPos pos) =>
            world.Side == EnumAppSide.Client ? AimedGuest(world, pos, (world.Api as Vintagestory.API.Client.ICoreClientAPI)?.World.Player?.CurrentBlockSelection) : null;

        public override void OnBlockBroken(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1f)
        {
            BlockSelection guest = AimedGuest(world, pos, byPlayer?.CurrentBlockSelection);
            if (guest != null)
            {
                if (world.Side != EnumAppSide.Server) return;
                if (BEUniversalDisplay.At(world.BlockAccessor, guest.Position) is { IsEmbedded: true }) return;
                if (byPlayer != null && world.Claims.TestAccess(byPlayer, guest.Position, EnumBlockAccessFlags.BuildOrBreak) != EnumWorldAccessResponse.Granted) return;
                world.BlockAccessor.BreakBlock(guest.Position, byPlayer, dropQuantityMultiplier);
                return;
            }
            base.OnBlockBroken(world, pos, byPlayer, dropQuantityMultiplier);
        }

        public override BlockSounds GetSounds(IBlockAccessor blockAccessor, BlockSelection blockSel, ItemStack stack = null)
        {
            if (BEUniversalDisplay.GuestSelection(blockSel, null, out BlockSelection forwarded))
                return blockAccessor.GetBlock(forwarded.Position).GetSounds(blockAccessor, forwarded, stack);
            return base.GetSounds(blockAccessor, blockSel, stack);
        }

        public override string GetPlacedBlockInfo(IWorldAccessor world, BlockPos pos, IPlayer forPlayer)
        {
            BlockSelection guest = LocalPlayerAim(world, pos);
            return guest != null
                ? world.BlockAccessor.GetBlock(guest.Position).GetPlacedBlockInfo(world, guest.Position, forPlayer)
                : base.GetPlacedBlockInfo(world, pos, forPlayer);
        }

        /// <summary>
        /// A window placed or broken directly below changes which surfaces are covered,
        /// so the cached selection boxes have to be rebuilt. Runs on both sides.
        /// </summary>
        public override void OnNeighbourBlockChange(IWorldAccessor world, BlockPos pos, BlockPos neibpos)
        {
            base.OnNeighbourBlockChange(world, pos, neibpos);

            // needsSupport (the ground pile): the block below stops holding it up -> it breaks and drops what it held,
            // the same test the game's own ground storage makes before it is placed.
            // With followSupport anything solid enough to have a collision box holds it (a slab, a chiselled top).
            if (world.Side == EnumAppSide.Server && Attributes?["needsSupport"].AsBool(false) == true
                && neibpos.X == pos.X && neibpos.Z == pos.Z && neibpos.Y == pos.Y - 1
                && !HoldsUp(world, neibpos))
            {
                world.BlockAccessor.BreakBlock(pos, null);
                return;
            }

            if (neibpos.X == pos.X && neibpos.Z == pos.Z && neibpos.Y == pos.Y - 1
                && world.BlockAccessor.GetBlockEntity(pos) is BEUniversalDisplay be)
            {
                be.InvalidateBoxes();
                be.MarkMeshesDirty();
            }
        }

        /// <summary>Does the block at <paramref name="below"/> hold this block up (needsSupport)?</summary>
        bool HoldsUp(IWorldAccessor world, BlockPos below)
        {
            Block b = world.BlockAccessor.GetBlock(below);
            if (Attributes?["followSupport"].AsBool(false) == true)
                return b?.GetCollisionBoxes(world.BlockAccessor, below)?.Length > 0;
            return b != null && b.CanAttachBlockAt(world.BlockAccessor, this, below, BlockFacing.UP);
        }

        /// <summary>Light comes from whatever is stored on the sills.</summary>
        public override byte[] GetLightHsv(IBlockAccessor blockAccessor, BlockPos pos, ItemStack stack = null)
        {
            if (pos != null)
            {
                try
                {
                    if (blockAccessor.GetBlockEntity(pos) is BEUniversalDisplay be && be.CachedLightHsv != null)
                    {
                        return be.CachedLightHsv;
                    }
                }
                catch (Exception)
                {
                    // Light is non-critical and this runs off-thread during relight;
                    // fall through to the block default rather than take the world down.
                }
            }

            return base.GetLightHsv(blockAccessor, pos, stack);
        }

        /// <summary>
        /// True when this window appears in at least one creative tab. A variant only
        /// reachable by wrench-swapping — an edge position, say — declares its stacks
        /// with no tabs, so it reports false.
        ///
        /// Asked of the *closed* variant deliberately. Content keys creative stacks on
        /// "*-closed", so the open variants have none of their own; testing the block
        /// as-is made an open main window look unobtainable and drop its edge
        /// counterpart. Drops are always closed anyway, so the closed form is the one
        /// whose availability actually matters.
        /// </summary>
        private bool IsObtainableDirectly(IWorldAccessor world)
        {
            Block closed = world.GetBlock(CodeWithVariant("state", "closed")) ?? this;
            return closed.CreativeInventoryStacks?.Any(s => s.Tabs != null && s.Tabs.Length > 0) == true;
        }

        /// <summary>
        /// Resolves what this block should drop as.
        ///
        /// Derived rather than configured: a block that is in no creative tab cannot be
        /// obtained on its own, so breaking it hands back its wrenchSwapTo counterpart —
        /// the version that *is* obtainable. Otherwise a player could break an edge
        /// window and re-place it directly, sidestepping the wrench entirely.
        ///
        /// The optional <c>dropsAs</c> attribute overrides this, using the same
        /// {variant} substitution as wrenchSwapTo, for cases the rule gets wrong.
        /// </summary>
        private Block ResolveDropBlock(IWorldAccessor world)
        {
            string template = Attributes?["dropsAs"].AsString(null);
            if (template != null)
            {
                return world.GetBlock(new AssetLocation(BlockSwapHelper.ResolveCode(this, template))) ?? this;
            }

            if (!IsObtainableDirectly(world))
            {
                return BlockSwapHelper.GetNextTarget(world, this) ?? this;
            }

            return this;
        }

        /// <summary>Drops the obtainable counterpart, always in its closed state.</summary>
        public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1f)
        {
            // An upper segment of a pre-1.1.4 chain window is not an item
            if (Attributes?["legacySegment"].AsBool(false) == true) return Array.Empty<ItemStack>();

            Block dropBlock = ResolveDropBlock(world);
            Block closedBlock = world.GetBlock(dropBlock.CodeWithVariant("state", "closed")) ?? dropBlock;

            ItemStack[] drops = base.GetDrops(world, pos, byPlayer, dropQuantityMultiplier);
            if (drops == null || drops.Length == 0) return drops;
            if (closedBlock.Id == Id) { MaterialsAt(world, pos)?.CopyTo(drops[0]); return drops; }

            // The materials (wood, glass...) go with it, re-pointed at the resolved block
            var closedStack = new ItemStack(closedBlock);
            if (drops[0].Attributes != null) closedStack.Attributes.MergeTree(drops[0].Attributes);
            drops[0] = closedStack;
            MaterialsAt(world, pos)?.CopyTo(drops[0]);
            return drops;
        }

        /// <summary>
        /// The name shown for a PLACED block — this block's own, not its drop's.
        ///
        /// Vanilla's <c>Block.GetPlacedBlockName</c> derives the name from
        /// <see cref="OnPickBlock"/>, and OnPickBlock here deliberately hands back the
        /// obtainable counterpart rather than this block. For an edge variant — which is
        /// never obtainable directly, so <c>ResolveDropBlock</c> falls through to
        /// <c>GetNextTarget</c> — that means the info box showed **the name of whatever the
        /// block swaps to**.
        ///
        /// It went unnoticed because each edge style swapped back to its own partner, which
        /// shares its lang entry: <c>leftedge</c> borrowed "Left Opening Window" from
        /// <c>left</c> and looked right. It only became visible once a swap chain crossed
        /// sides — which the left/right mirror was the first feature to do — at which point <c>leftedge</c> started
        /// announcing itself as the RIGHT window — reported as "the edge ones aren't
        /// updating their names", and misdiagnosed several times over as a lang problem, a
        /// stale cache, crossed shape files and a client sync issue. It was none of those:
        /// the server, the lang file and the swap were correct throughout.
        ///
        /// Drops and middle-click keep resolving to the obtainable block, which is
        /// deliberate and unchanged. Only the displayed name is corrected.
        /// </summary>
        public override string GetPlacedBlockName(IWorldAccessor world, BlockPos pos)
        {
            if (Code == null) return base.GetPlacedBlockName(world, pos);

            BlockSelection guest = LocalPlayerAim(world, pos);
            if (guest != null) return world.BlockAccessor.GetBlock(guest.Position).GetPlacedBlockName(world, guest.Position);

            string name = Lang.GetMatching(Code.Domain + ":block-" + Code.Path);
            return string.IsNullOrEmpty(name) ? base.GetPlacedBlockName(world, pos) : name;
        }

        /// <summary>Middle-click mirrors the drop, so it cannot hand out an unobtainable variant either.</summary>
        public override ItemStack OnPickBlock(IWorldAccessor world, BlockPos pos)
        {
            ItemStack stack = base.OnPickBlock(world, pos);

            Block dropBlock = ResolveDropBlock(world);
            Block closedBlock = world.GetBlock(dropBlock.CodeWithVariant("state", "closed")) ?? dropBlock;
            if (stack == null || closedBlock.Id == Id) { MaterialsAt(world, pos)?.CopyTo(stack); return stack; }

            var picked = new ItemStack(closedBlock);
            if (stack.Attributes != null) picked.Attributes.MergeTree(stack.Attributes);
            MaterialsAt(world, pos)?.CopyTo(picked);
            return picked;
        }

        /// <summary>
        /// The handbook's "Drops when broken" in the PAGE's materials (Calm, 2026-10-08: a larch / quartz window's
        /// page showed its drop plain). The game builds handbook drops from the block's drop list, which carries
        /// no materials; this gives each drop that uses UDMaterials the page stack's.
        ///
        /// THE DROP FLIPS WITH THE RECIPE (Calm, 2026-10-14: "Can we cycle the drop to match the recipe changing"): a block
        /// made by a material recipe gets one drop per material set, in the recipe slideshow's order (MaterialsSystem
        /// .CycleCombos, from the page's materials) - the handbook merges drops of one item into ONE flipping icon when the
        /// item has a handbook groupBy (vsapi SlideshowItemstackTextComponent), so only then; without one they would show
        /// side by side, and the drop stays the page's own. The two flip on their own one-second timers.
        /// </summary>
        public override BlockDropItemStack[] GetDropsForHandbook(ItemStack handbookStack, IPlayer forPlayer)
        {
            var drops = base.GetDropsForHandbook(handbookStack, forPlayer);
            var types = Materials.BlockBehaviorMaterials.TypesOf(handbookStack?.Attributes);
            if (drops == null || types.Count == 0) return drops;

            // The handbook asks EVERY block for its drops on EVERY page open (vssurvivalmod GetHandbookInfo, for "Obtained
            // through") - so the answer is worked out once per page stack and kept (2026-10-14: the handbook lagged).
            string cacheKey = Materials.BlockBehaviorMaterials.KeyOf(types);
            lock (handbookDrops)
            {
                if (handbookDrops.TryGetValue(cacheKey, out var known)) return known;
            }
            var made = HandbookDropsFor(drops, types);
            if (made.Length > drops.Length || (api?.World?.GridRecipes?.Count ?? 0) > 0)
                lock (handbookDrops) handbookDrops[cacheKey] = made;
            return made;
        }

        private readonly System.Collections.Generic.Dictionary<string, BlockDropItemStack[]> handbookDrops = new();

        private BlockDropItemStack[] HandbookDropsFor(BlockDropItemStack[] drops, System.Collections.Generic.IReadOnlyDictionary<string, string> types)
        {
            var system = Materials.MaterialsSystem.Instance;
            var world = api?.World;
            var result = new System.Collections.Generic.List<BlockDropItemStack>();
            foreach (var drop in drops)
            {
                var collectible = drop?.ResolvedItemstack?.Collectible;
                if (collectible?.GetCollectibleBehavior<Materials.BlockBehaviorMaterials>(true) == null) { result.Add(drop); continue; }

                var sets = new System.Collections.Generic.List<System.Collections.Generic.IReadOnlyDictionary<string, string>> { types };
                bool grouped = collectible.Attributes?["handbook"]?["groupBy"]?.Exists == true;
                if (grouped && system != null && collectible is Block dropBlock && system.MaterialRecipeFor(world, collectible) != null)
                {
                    var cycle = system.CycleCombos(world, system.KindsFor(world, dropBlock), collectible.Code?.Domain, types, Materials.MaterialsSystem.MaxHandbookCycle);
                    if (cycle.Count > 1) sets = cycle.Select(c => (System.Collections.Generic.IReadOnlyDictionary<string, string>)c).ToList();
                }
                foreach (var set in sets)
                {
                    var copy = drop.Clone();
                    copy.ResolvedItemstack = drop.ResolvedItemstack.Clone();
                    Materials.BlockBehaviorMaterials.WriteTypes(copy.ResolvedItemstack.Attributes, set);
                    result.Add(copy);
                }
            }
            return result.ToArray();
        }

        /// <summary>The placed block's materials (UDMaterials), for its drop and middle-click.</summary>
        private static Materials.BEBehaviorMaterials MaterialsAt(IWorldAccessor world, BlockPos pos) =>
            world.BlockAccessor.GetBlockEntity(pos)?.GetBehavior<Materials.BEBehaviorMaterials>();
    }
}


