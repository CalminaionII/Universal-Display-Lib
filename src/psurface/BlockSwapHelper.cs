using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace UniversalDisplayLib
{
    /// <summary>
    /// In-place block swapping driven by the <c>wrenchSwapTo</c> block attribute.
    ///
    /// <code>
    /// "wrenchSwapTo": "windowstorage:windowstorage-awningedge-{state}"
    /// "wrenchSwapTo": ["windowstorage:windowstorage-awningedge-{state}",
    ///                  "windowstorage:windowstorage-hopper-{state}"]
    /// </code>
    ///
    /// Any <c>{variantcode}</c> placeholder is filled from the current block's own
    /// variants, so <c>{state}</c> preserves open/closed across the swap.
    ///
    /// Triggered by Ctrl (sprint) + wrench + right-click on a frame or pane box.
    /// Refuses while anything is stored: the target block has different psurface
    /// geometry, so the string slot ids would not survive the exchange.
    /// </summary>
    public static class BlockSwapHelper
    {
        private static readonly Regex PlaceholderPattern = new Regex(@"\{([a-zA-Z0-9_]+)\}", RegexOptions.Compiled);

        /// <summary>
        /// The sound a block makes when swapped, from its own <c>attributes.displaySounds.swap</c>,
        /// falling back to the planks sound this mod has always used.
        ///
        /// A domain-less path resolves in the BLOCK'S own domain, not <c>game:</c> — so a
        /// modder naming their own asset gets their own asset, and a vanilla one needs an
        /// explicit <c>game:</c>. That distinction once left the custom window silent.
        /// </summary>
        private static AssetLocation SwapSound(Block block)
        {
            string declared = block?.Attributes?["displaySounds"]?["swap"]?.AsString(null);
            if (string.IsNullOrWhiteSpace(declared)) return new AssetLocation("game:sounds/block/planks");

            return AssetLocation.Create(declared, block.Code?.Domain ?? "game");
        }

        public static bool HasSwapTargets(Block block) => GetSwapTargets(block).Length > 0;

        public static string[] GetSwapTargets(Block block)
        {
            JsonObject attr = block?.Attributes?["wrenchSwapTo"];
            if (attr == null || !attr.Exists) return Array.Empty<string>();

            // Accept a bare string or an array of strings
            string single = attr.AsString(null);
            if (single != null) return new[] { single };

            return attr.AsArray<string>(null) ?? Array.Empty<string>();
        }

        /// <summary>Substitutes {variant} placeholders using the source block's own variants.</summary>
        public static string ResolveCode(Block sourceBlock, string template)
        {
            return PlaceholderPattern.Replace(template, match =>
            {
                string variantCode = match.Groups[1].Value;
                string value = sourceBlock.Variant[variantCode];
                return value ?? match.Value;
            });
        }

        /// <summary>
        /// Resolves the swap target for the current block: the FIRST listed template that resolves to a real block
        /// other than this one. Returns null when nothing usable is configured.
        ///
        /// An array is a FALLBACK LIST, not a cycle — the current block's position in it is never consulted, so `["a",
        /// "b"]` always yields "a" while "a" exists.
        ///
        /// Cycles are expressed across BLOCKS instead, each with a single target pointing at the next: `left → leftedge
        /// → right → rightedge → left` is four one-line attributes and needs no array. That is how every cycle in the
        /// content is built.
        /// </summary>
        public static Block GetNextTarget(IWorldAccessor world, Block currentBlock)
        {
            string[] templates = GetSwapTargets(currentBlock);
            if (templates.Length == 0) return null;

            var resolved = new List<Block>();
            foreach (string template in templates)
            {
                Block candidate = world.GetBlock(new AssetLocation(ResolveCode(currentBlock, template)));
                if (candidate != null && candidate.Id != currentBlock.Id) resolved.Add(candidate);
            }

            return resolved.Count > 0 ? resolved[0] : null;
        }

        /// <summary>
        /// Performs the swap. Server-side only; the client gets it via the block
        /// update. Returns true when the interaction was consumed, whether or not
        /// the swap actually happened — a refusal still counts as handled.
        /// </summary>
        public static bool TrySwap(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, BEUniversalDisplay be)
        {
            if (be == null) return false;

            Block currentBlock = be.Block;
            if (!HasSwapTargets(currentBlock)) return false;

            // The whole window is this one block entity (tall ones too), so it is the only inventory that has to be
            // empty
            if (!be.Inventory.Empty)
            {
                (world.Api as ICoreClientAPI)?.TriggerIngameError(be, "notempty",
                    Lang.Get("universaldisplaylib:swap-notempty"));
                return true;
            }

            // Every pane shut first: the next style's leaves, boxes and fillers are measured shut, and an open leaf
            // would otherwise change shape mid-swing. Both sides refuse, so the server never swaps either.
            if (be.paneStates != null && Array.IndexOf(be.paneStates, true) >= 0)
            {
                (world.Api as ICoreClientAPI)?.TriggerIngameError(be, "swapclosed",
                    Lang.Get("universaldisplaylib:reach-swapclosed"));
                return true;
            }

            Block target = GetNextTarget(world, currentBlock);

            if (world.Side != EnumAppSide.Server) return true;

            // a latched window stays as it is for anyone who could not unlatch it
            if (!be.MayUnlatch(byPlayer))
            {
                (byPlayer as Vintagestory.API.Server.IServerPlayer)?.SendIngameError("latched", Lang.Get("universaldisplaylib:latched-locked"));
                return true;
            }

            if (target == null)
            {
                world.Logger.Warning("[UniversalDisplayLib] {0} at {1} has no resolvable wrenchSwapTo; swap skipped.",
                    currentBlock.Code, pos);
                return true;
            }

            // synchronize: false, relight: true — do NOT change this.
            //
            // synchronize:true does not fix a stale block name and makes the swap visibly non-instant.
            IBlockAccessor relightAccessor = world.GetBlockAccessor(false, true, false);

            // Snapshot before the exchange; ExchangeBlock keeps the block entity
            ITreeAttribute carried = new TreeAttribute();
            be.ToTreeAttributes(carried);

            relightAccessor.ExchangeBlock(target.Id, pos);

            if (relightAccessor.GetBlockEntity(pos) is BEUniversalDisplay newBe)
            {
                newBe.FromTreeAttributes(carried, world);
                newBe.ClampPaneStatesToBlock();
                newBe.RefreshAfterSwap();
                // a different model: measure again and move the fillers to match (AutoBoxes Kit)
                newBe.AutoBoxesAngleChanged();
                newBe.MarkDirty(true);
            }

            // dualCallByPlayer is NULL here, unlike the rest of this mod, on purpose.
            //
            // `dualCall` is for calls made on both sides. This one is server only (the client returned above), so
            // naming the swapping player would suppress the sound for that player alone. Nameable per block, like every
            // other sound the lib plays — planks is right for a wooden window and wrong for a stone cabinet.
            world.PlaySoundAt(SwapSound(currentBlock),
                pos.X + 0.5, pos.Y + 0.5, pos.Z + 0.5, null, randomizePitch: true, range: 16f);

            return true;
        }
    }
}
