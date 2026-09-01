using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AttributeRenderingLibrary;
using Vintagestory.Client.NoObf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace UniversalDisplayLib
{
    /// <summary>
    /// psurface-based window storage block entity.
    ///
    /// Derives from <see cref="BlockEntityContainer"/> rather than hosting vanilla's
    /// <c>BEBehaviorDisplay</c>, for three reasons vanilla cannot cover:
    ///
    ///  * <see cref="IContainedInteractable"/> takes a concrete BlockEntityContainer,
    ///    so cooking pots / crocks can only be interacted with from one
    ///  * stored items drive block light, which needs a relight on content change
    ///  * the block mesh is built through AttributeRenderingLibrary
    ///
    /// The psurface *format* is kept identical to vanilla — shape element names and
    /// string slot ids — so shapes authored for cabinets work unchanged.
    /// </summary>
    public class BEUniversalDisplay : BlockEntityContainer, ITexPositionSource
    {
        // ── Inventory ────────────────────────────────────────────────────────
        protected InventoryInfinite inv;
        public override InventoryBase Inventory => inv;
        /// <summary>
        /// **CHANGED 2026-08-06 from "windowstorage", and this one was tested rather than
        /// reasoned about.** It had been carrying the name of the mod two generations back.
        ///
        /// The risk was that this string is not purely cosmetic: it forms the inventory's
        /// runtime id (`InventoryClassName + "-" + position`), which the game uses for dialog
        /// opening and inventory sync. Stored CONTENTS save under the block entity's own tree
        /// and not under this name, so the expectation was that changing it is harmless — but
        /// the vanilla source could not be checked at the time (the public repo needs auth for
        /// code search), so it was verified the honest way: a world saved with the OLD name was
        /// loaded with the new one and the stored items were still there.
        ///
        /// If that ever turns out to be wrong, changing it BACK restores the old id and costs
        /// nothing — nothing else in the mod reads it.
        /// </summary>
        public override string InventoryClassName => "universaldisplay";

        // ── Placement / render state ─────────────────────────────────────────
        public float MeshAngleRad;
        public bool[] paneStates = Array.Empty<bool>();
        public Dictionary<string, float> customRotationDegBySlot;

        /// <summary>
        /// Per-slot height nudges in VOXELS, away from where the item would rest on its own.
        /// Absent means 0, so everything placed before this existed is unaffected.
        ///
        /// Keyed by SLOT like the angles, but it does NOT travel with the item when a stack
        /// collapses — it is CLEARED. A nudge is relative to what the item was resting on,
        /// so it stops meaning anything once that is taken away. See the collapse loop in
        /// <c>TryTake</c> for the full reasoning; this is the one place height deliberately
        /// behaves differently from rotation, and the difference is not an oversight.
        /// </summary>
        public Dictionary<string, float> customHeightBySlot;

        /// <summary>
        /// The ONE slot per surface holding a locked chiselled design: surface index → slot id.
        /// Absent means that surface has nothing locked yet.
        ///
        /// Claimed by the FIRST chiselled block placed on a surface the block lists in
        /// <c>lockChiselledSurfaces</c>, and never reassigned while that slot still holds it.
        /// Everything chiselled placed AFTERWARDS stays takeable — the structural piece is the
        /// one you fit first, later ones are decoration.
        ///
        /// Recorded rather than derived, which is the whole reason this is stored state: "is
        /// this a chiselled block on a locked surface" can be answered from the world at any
        /// time, but "was it the first" cannot.
        ///
        /// TRAVELS WITH THE ITEM when a stack collapses, like the angle and unlike the nudge —
        /// see the collapse loop in <c>TryTake</c>. A locked design sitting above a takeable
        /// item moves down when that item is removed, and the recorded id has to move with it
        /// or the lock is left pointing at a slot the design has left.
        /// </summary>
        public Dictionary<int, string> lockedSlotBySurface;
        public Dictionary<string, float[]> TfMatrices = new Dictionary<string, float[]>();

        protected ICoreClientAPI capi;
        protected MeshData blockMesh;
        protected bool meshesGenerated;

        private Cuboidf[] _padOnlyBoxes;
        private Cuboidf[] _allBoxes;
        private Cuboidf[] _collisionBoxes;

        private BlockEntityAnimationUtil animUtil;
        private DisplaySoundHandler soundHandler;
        private byte[] _cachedLightHsv;
        private bool _removed;

        // What the current AnimatableRenderer was actually built for. Rotation is
        // baked in at renderer construction and ExchangeBlock keeps the existing
        // block entity, so neither a placement nor a swap can be detected by
        // watching for a *change* — we have to compare against what was built.
        private string _animatorShapeKey;
        private float _animatorAngleRad = float.NaN;
        private int _meshBlockId = -1;
        private int _paneStateBlockId = -1;
        private long _clientTickListener;

        private CollectibleObject _nowTesselatingObj;
        private Shape _nowTesselatingShape;

        public byte[] CachedLightHsv => _cachedLightHsv;

        public BlockBehaviorDisplaySurfaces SurfaceBehavior => Block?.GetBehavior<BlockBehaviorDisplaySurfaces>();

        public string AttributeTransformCode => BlockBehaviorDisplaySurfaces.TransformTarget;

        protected Dictionary<string, MeshData> MeshCache =>
            ObjectCacheUtil.GetOrCreate(Api, "universaldisplaylib-itemmeshes", () => new Dictionary<string, MeshData>());

        /// <summary>
        /// Defaults to true. A group with no openFrameBox/closedFrameBox already cannot
        /// toggle, so styles that are purely static need no attribute — this flag is only
        /// needed to seal a window that *does* have openable pane groups.
        ///
        /// Note it gates the whole block: setting it false stops every pane, including
        /// any sealsRoom:false interior door.
        /// </summary>
        public bool CanOpen => Block?.Attributes?["canOpen"].AsBool(true) ?? true;

        /// <summary>
        /// Suppresses the two deliberate irregularities an item gets on placement: the
        /// random resting angle from its <c>RandYRotAngle</c> and the small vertical
        /// scale wobble in <see cref="BuildSlotMatrix"/>. Both exist so a row of stored
        /// clutter does not look machine-stamped, which is right for a sill of jars and
        /// wrong for anything meant to read as part of the building.
        ///
        /// Declared on the BLOCK rather than per item, because it is a property of what
        /// the surface is for: the custom chiselled window holds a block the player
        /// aligned by hand, so a 15 degree lean or a 3% stretch is a defect there. An
        /// item's own <c>randYRotAngle: 0</c> cannot cover it — a chiselled block gets
        /// its DisplayableAttributes from <c>BlockMicroBlock</c>'s IDisplayableProps
        /// implementation, which wins over attributes in the resolution order, so there
        /// is no JSON on the item side to patch.
        /// </summary>
        public bool NoPlacementJitter => Block?.Attributes?["noPlacementJitter"].AsBool(false) ?? false;

        /// <summary>
        /// Whether the random resting angle is off for this block, by config or block flag.
        ///
        /// This also gated a vertical scale wobble until that was removed on 2026-07-28 —
        /// see BuildSlotMatrix. The two were briefly gated separately, which left stacks
        /// gapping and z-fighting with the config off, so if anything random is ever added
        /// back it reads THIS and not a switch of its own.
        /// </summary>
        private bool JitterSuppressed =>
            UniversalDisplayLibConfig.Current?.PlacementJitter == false || NoPlacementJitter;

        public bool IsAnySealingPaneOpen
        {
            get
            {
                var groups = SurfaceBehavior?.FrameBoxGroups;
                if (groups == null || paneStates == null) return false;
                for (int i = 0; i < paneStates.Length && i < groups.Length; i++)
                {
                    if (groups[i].SealsRoom && paneStates[i]) return true;
                }
                return false;
            }
        }

        public BEUniversalDisplay()
        {
            inv = new InventoryInfinite((slotId, invBase) =>
            {
                DisplaySlotId loc = DisplaySlotId.Decode(slotId);
                string category = SurfaceBehavior?.GetDisplayCategory(loc?.SurfaceIndex ?? 0) ?? "shelf";
                return new ItemSlotDisplay(invBase, category);
            });
        }

        // ── Lifecycle ────────────────────────────────────────────────────────

        public override void Initialize(ICoreAPI api)
        {
            base.Initialize(api);
            capi = api as ICoreClientAPI;
            _removed = false;

            int paneCount = SurfaceBehavior?.FrameBoxGroups.Length ?? 0;
            if (paneStates == null || paneStates.Length != paneCount) ClampPaneStatesToBlock();

            inv.OnAcquireTransitionSpeed += OnAcquireTransitionSpeed;

            // NOTE: deliberately not subscribing to inv.SlotModified.
            // InventoryInfinite overrides DidModifyItemSlot to an empty method, so
            // SlotModified never fires for this inventory type. Content changes are
            // signalled explicitly through OnContentsChanged() instead.

            UpdateLightCache();

            // Server only: the claim is authoritative state and reaches the client through the
            // tree like everything else. Doing it on both sides would have them adopt
            // independently, which is fine while they agree and a silent divergence when they
            // do not.
            if (api.Side == EnumAppSide.Server) AdoptPreExistingLocks();

            if (capi != null)
            {
                animUtil = new BlockEntityAnimationUtil(capi, this);
                soundHandler = new DisplaySoundHandler(this, capi);
                EnsureAnimatorCurrent();
                UpdateAnimationState();

                // Main-thread safety net: catches client-side placement (where the
                // angle is set after Initialize) and wrenchSwapTo (where the block
                // changes under a surviving block entity).
                _clientTickListener = RegisterGameTickListener(OnClientTick, 250);

                // Join the live registry instead of registering an event bus listener.
                // See the comment on LiveClientInstances for why that distinction matters.
                LiveClientInstances.Add(this);
            }
        }

        /// <summary>
        /// Every client-side window currently loaded.
        ///
        /// EXISTS BECAUSE EVENT BUS LISTENERS CANNOT BE UNREGISTERED. `IEventAPI` offers
        /// `UnregisterCallback` and `UnregisterGameTickListener` and nothing for the event
        /// bus — `RegisterEventBusListener` is one-way. This block entity used to register
        /// one per instance for the transform editor, so every window on every chunk load
        /// added a permanent listener holding that block entity alive: memory that was
        /// never released, and a handler list that grew for the whole session.
        ///
        /// Now the mod system registers ONE listener and walks this set, which is
        /// maintained honestly — added in Initialize, removed in Cleanup, and Cleanup runs
        /// from all three teardown paths. So it tracks what is actually loaded rather than
        /// everything that ever was.
        ///
        /// Client only, main thread only: Initialize and Cleanup both run there, and so
        /// does the event bus.
        /// </summary>
        public static readonly HashSet<BEUniversalDisplay> LiveClientInstances = new HashSet<BEUniversalDisplay>();

        /// <summary>
        /// Applies an edited transform to this window, if it is close enough to be worth
        /// redrawing. Driven by the mod system's single event bus listener.
        ///
        /// Writing the value onto the collectible is global and only needs doing once, so
        /// the caller does that; this is the per-window half — dropping the cached meshes
        /// and boxes so the change shows.
        /// </summary>
        public void ApplyTransformEdit()
        {
            if (capi?.World.Player?.Entity == null || Pos == null) return;
            if (Pos.DistanceTo(capi.World.Player.Entity.Pos.XYZ) > 20f) return;

            MeshCache.Clear();
            InvalidateBoxes();   // an edited transform can change an item's footprint
            MarkMeshesDirty();
        }

        private void OnClientTick(float dt)
        {
            if (_removed || capi == null || Block == null) return;

            // Only the poll advances the give-up budget, so a hot path like tesselation
            // cannot burn through it and defeat the readiness gate
            if (!ArlVariantsReady) _variantWaitTicks++;

            // A wrenchSwapTo target can have a different number of pane groups
            if (_paneStateBlockId != Block.Id)
            {
                _paneStateBlockId = Block.Id;
                ClampPaneStatesToBlock();
            }

            EnsureAnimatorCurrent();

            // Idempotent — only starts or stops an animation when the active set does
            // not match pane state, so it is safe to run every tick and lets the panes
            // converge if any single update was missed.
            UpdateAnimationState();

            // Drives the shared rain-on-glass loop (EnableRainSound / Volume / Range)
            soundHandler?.Tick();
        }

        public override void OnBlockPlaced(ItemStack byItemStack = null)
        {
            // base runs the ARL behaviour, which is where Variants get filled from the
            // stack. Everything texture-dependent has to happen after this line, not in
            // Initialize — that runs first, with no variants, and is why a freshly
            // placed window used to show default textures before correcting itself.
            base.OnBlockPlaced(byItemStack);

            ClampPaneStatesToBlock();

            if (Api?.Side == EnumAppSide.Server) MarkDirty(true);
            else RefreshAnimator();
        }

        public override void OnBlockRemoved()
        {
            _removed = true;
            RemoveLight();
            Cleanup();
            base.OnBlockRemoved();
        }

        public override void OnBlockUnloaded()
        {
            _removed = true;
            Cleanup();
            base.OnBlockUnloaded();
        }

        public override void OnBlockBroken(IPlayer byPlayer = null)
        {
            _removed = true;
            RemoveLight();

            // Cleanup here too, matching OnBlockRemoved and OnBlockUnloaded. In practice
            // breaking sets the block to air and OnBlockRemoved follows, which cleaned up
            // anyway — but that was an assumption about ordering rather than something
            // guaranteed, and the asymmetry read as an oversight. Cleanup is idempotent:
            // the unsubscribe is safe twice, the tick listener is guarded on != 0, and the
            // disposals null their own fields.
            Cleanup();

            base.OnBlockBroken(byPlayer);
        }

        private void Cleanup()
        {
            // Leaving the registry is the whole point of it — see LiveClientInstances.
            // Cleanup runs from OnBlockRemoved, OnBlockUnloaded and OnBlockBroken, so a
            // window cannot stay in the set after it has gone.
            LiveClientInstances.Remove(this);

            inv.OnAcquireTransitionSpeed -= OnAcquireTransitionSpeed;
            if (_clientTickListener != 0)
            {
                UnregisterGameTickListener(_clientTickListener);
                _clientTickListener = 0;
            }
            animUtil?.Dispose();
            animUtil = null;
            soundHandler?.Dispose();
            soundHandler = null;
            blockMesh = null;
            meshesGenerated = false;
            _animatorShapeKey = null;
            _animatorAngleRad = float.NaN;
        }

        /// <summary>Resizes pane state to match the current block's frameBoxGroups, preserving overlap.</summary>
        public void ClampPaneStatesToBlock()
        {
            int paneCount = SurfaceBehavior?.FrameBoxGroups.Length ?? 0;
            bool[] resized = new bool[paneCount];
            if (paneStates != null)
            {
                Array.Copy(paneStates, resized, Math.Min(paneStates.Length, paneCount));
            }
            paneStates = resized;
            InvalidateBoxes();
        }

        /// <summary>
        /// Rebuilds the animator right now, on the caller's thread.
        ///
        /// Called client-side from TryPlaceBlock the moment MeshAngleRad is set. The
        /// block entity is created during base.TryPlaceBlock, so Initialize builds
        /// the renderer while the angle is still 0; without this the correction only
        /// lands on the next client tick and you see the window snap into place.
        /// </summary>
        /// <summary>
        /// A stored slot by its id, resolved and non-empty, or null.
        ///
        /// Exists because the string-keyed indexer belongs to <c>InventoryInfinite</c> while
        /// the public <c>Inventory</c> property is typed as <c>InventoryBase</c>, whose
        /// indexer takes an int — so a caller outside this class cannot ask for
        /// "psurface0-3-0-1" without casting. Resolving here also keeps the
        /// <c>ResolveBlocksOrItems</c> rule in one place rather than trusting each caller.
        /// </summary>
        public ItemSlot SlotById(string slotId)
        {
            if (string.IsNullOrEmpty(slotId) || inv == null) return null;

            // Same rule as TryResolveStackTarget: the indexer would CREATE the slot, and this
            // is polled from the editor's tick listener while tesselation enumerates.
            if (!inv.SlotsByslotId.TryGetValue(slotId, out ItemSlot slot) || slot == null || slot.Empty)
            {
                return null;
            }

            inv.ResolveBlocksOrItems();
            return slot.Empty ? null : slot;
        }

        public void RefreshAnimator()
        {
            if (capi == null) return;
            EnsureAnimatorCurrent();
            UpdateAnimationState();
        }

        /// <summary>Called after a wrenchSwapTo exchange to rebuild everything block-derived.</summary>
        public void RefreshAfterSwap()
        {
            InvalidateBoxes();
            MarkMeshesDirty();
            if (capi != null)
            {
                InitializeAnimator();
                UpdateAnimationState();
            }
            UpdateLightCache();
        }

        // ── Perish rate ──────────────────────────────────────────────────────

        private float OnAcquireTransitionSpeed(EnumTransitionType type, ItemStack stack, float baseMul)
        {
            if (type != EnumTransitionType.Perish || Api == null) return baseMul;

            Room room = container.Room;

            bool effectivelyOpen =
                UniversalDisplayLibConfig.Current != null && UniversalDisplayLibConfig.Current.RoomSafeOpening
                    ? room != null && room.ExitCount > 0
                    : IsAnySealingPaneOpen || (room != null && room.ExitCount > 0);

            return baseMul * (effectivelyOpen ? 2.0f : 0.5f);
        }

        /// <summary>
        /// Must be called after every inventory mutation. InventoryInfinite never
        /// raises SlotModified (DidModifyItemSlot is an empty override), so nothing
        /// else will do this for us.
        /// </summary>
        public void OnContentsChanged()
        {
            InvalidateBoxes();
            MarkMeshesDirty();

            // Both sides, matching vanilla BlockEntityGroundStorage, which calls
            // LightUpdate straight out of its interaction handler with no side check.
            // Light is computed client-side for rendering as well as server-side; if
            // only the server relights, the client shows nothing until the chunk is
            // reloaded — which is exactly the "only updates on reload" behaviour.
            if (Api != null)
            {
                byte[] oldLight = _cachedLightHsv;
                UpdateLightCache();

                bool changed = (oldLight == null) != (_cachedLightHsv == null)
                    || (oldLight != null && _cachedLightHsv != null &&
                        (oldLight[0] != _cachedLightHsv[0] || oldLight[1] != _cachedLightHsv[1] || oldLight[2] != _cachedLightHsv[2]));

                if (changed)
                {
                    // ONLY strip the old light when there is no new one to overwrite it.
                    //
                    // This used to be an unconditional RemoveBlockLight followed immediately
                    // by ApplyLight — which is, literally, turn the light off and then turn
                    // it back on, and the player sees the gap between the two as a flash.
                    // Vanilla does not do it: BlockEntityGroundStorage.LightUpdate is a bare
                    // ExchangeBlock(Block.Id, Pos) with no removal, because the exchange
                    // re-registers the block's light and overwrites whatever was there.
                    //
                    // The removal is still needed in exactly one case — the last light-
                    // emitting item leaving. ApplyLight early-returns when _cachedLightHsv is
                    // null, so with nothing to re-register the old light would otherwise sit
                    // there until the chunk reloaded.
                    if (oldLight != null && _cachedLightHsv == null)
                    {
                        Api.World.BlockAccessor.RemoveBlockLight(oldLight, Pos);
                    }

                    ApplyLight();
                }
            }

            MarkDirty(true);
        }

        // ── Light from stored items ──────────────────────────────────────────

        private void UpdateLightCache()
        {
            if (Api == null) return;

            // Resolve first, as every path that reads slot.Itemstack.Collectible must: an
            // unresolved stack reads Collectible as null and is silently skipped, so a
            // lantern would contribute no light and the window would stay dark until the
            // next OnContentsChanged — "the light only appears once you touch it".
            //
            // Both callers (Initialize and OnContentsChanged) are on the main thread, so
            // this is safe here. The ONE place that must not resolve is the off-thread
            // particle gate — see HasParticleEmittingItem, which explains why.
            inv.ResolveBlocksOrItems();

            byte bestH = 0, bestS = 0, bestV = 0;
            foreach (ItemSlot slot in inv)
            {
                ItemStack stack = slot?.Itemstack;
                if (stack?.Collectible == null) continue;

                // GetLightHsv(..., stack), not the raw LightHsv field. For lanterns and
                // anything else whose brightness depends on stack attributes the field
                // is null and only the method returns the real value.
                byte[] hsv = stack.Collectible.GetLightHsv(Api.World.BlockAccessor, null, stack);
                if (hsv == null || hsv.Length < 3) continue;
                if (hsv[2] > bestV) { bestH = hsv[0]; bestS = hsv[1]; bestV = hsv[2]; }
            }
            _cachedLightHsv = bestV > 0 ? new[] { bestH, bestS, bestV } : null;
        }

        private void RemoveLight()
        {
            if (Api != null && _cachedLightHsv != null)
            {
                Api.World.BlockAccessor.RemoveBlockLight(_cachedLightHsv, Pos);
            }
        }

        /// <summary>
        /// Forces the engine to re-read GetLightHsv by exchanging the block with
        /// itself — the same thing vanilla BlockEntityGroundStorage.LightUpdate does.
        ///
        /// This must run synchronously. Deferring it through EnqueueMainThreadTask
        /// leaves the relight out of the block-change batch, so the new light level
        /// only appears when the chunk is next loaded from disk. The interaction
        /// handler is already on the main thread, so there is nothing to defer for.
        ///
        /// No state snapshot is needed either: ExchangeBlock keeps the existing
        /// block entity, so `this` survives the call.
        /// </summary>
        private void ApplyLight()
        {
            if (Api == null || Pos == null || _cachedLightHsv == null) return;
            if (_removed) return;
            if (Api.World.BlockAccessor.GetBlock(Pos).Id != Block.Id) return;

            Api.World.BlockAccessor.ExchangeBlock(Block.Id, Pos);
        }

        // ── Particles from stored items ──────────────────────────────────────

        /// <summary>
        /// True when anything stored here can emit particles, so the block should be
        /// registered for client particle ticks.
        ///
        /// **Runs off the main thread.** `SystemClientTickingBlocks` asks this from two
        /// places — `OnBlockChanged` on the main thread, but also its periodic rescan from
        /// `OnSeperateThreadGameTick`, on the "blockticking" thread. Assuming main-thread
        /// only crashed the client straight to the main menu.
        ///
        /// So this must NOT call <c>ResolveBlocksOrItems</c>, despite that being the rule
        /// everywhere else in this class. That method dereferences the inventory's own
        /// <c>Api</c>, which is null until the inventory is initialised — the actual NPE —
        /// and it also *writes* (<c>slot.Itemstack = null</c> for stacks it cannot
        /// resolve), which is not something to do from a background thread at all. Vanilla
        /// agrees: <c>BlockGroundStorage.ShouldReceiveClientParticleTicks</c> enumerates
        /// and null-checks without resolving.
        ///
        /// An unresolved stack therefore just reads as null and registers nothing this
        /// pass. That self-corrects — the rescan runs every ~20s and on player movement,
        /// and the normal BE paths resolve on load — so the cost is a late flame, not a
        /// missing one.
        /// </summary>
        public bool HasParticleEmittingItem()
        {
            foreach (var kv in SlotsSnapshot())
            {
                if (ParticlesFor(kv.Value, CategoryForSlot(kv.Key), out _) != null) return true;
            }
            return false;
        }

        /// <summary>
        /// Copy of the stored (id, slot) pairs, for the two background threads that drive
        /// particles to read without tripping over the main thread.
        ///
        /// <c>InventoryInfinite</c> creates slots on demand, so the backing dictionary can
        /// grow while it is being enumerated — which throws, and a throw on either of
        /// those threads takes the client down rather than being caught anywhere. The copy
        /// itself can lose that race too, hence the catch: dropping one pass is fine, the
        /// next tick picks it up.
        /// </summary>
        private KeyValuePair<string, ItemSlot>[] SlotsSnapshot()
        {
            try
            {
                var slots = inv?.SlotsByslotId;
                if (slots == null) return Array.Empty<KeyValuePair<string, ItemSlot>>();
                return slots.ToArray();
            }
            catch (Exception)
            {
                return Array.Empty<KeyValuePair<string, ItemSlot>>();
            }
        }

        /// <summary>
        /// Our own key inside <c>displayable.windowdisplay</c>, in VOXELS, naming where an
        /// item's flame actually sits relative to the centre of its box.
        ///
        /// **Presence of this key is what switches particles on at all.** Emitting for
        /// anything that merely declares particleProperties looked wrong far more often
        /// than right, because most emitters are not centre-wicked and nothing in JSON
        /// says where their wick is — vanilla hardcodes those positions in C#
        /// (<c>BlockOilLamp</c>'s -5/32, <c>BlockBunchOCandles.candleWickPositions</c>) and
        /// <c>Block.TopMiddlePos</c> is only a box centre. So this is authored per item,
        /// the same way sizes are, and anything untuned stays silent — which is also what
        /// vanilla shelves do, so nothing can look broken by default.
        ///
        /// Read straight off the JSON because <c>DisplayableAttributes</c> is a vanilla
        /// POCO with no field for it; it ignores keys it does not know, so ours rides
        /// along harmlessly. <c>particleOffsetByType</c> works for free — Vintage Story's
        /// own solveByType resolves ByType keys recursively before we ever read this.
        ///
        /// Authored UNROTATED: the offset is turned by the item's own placement rotation
        /// at spawn time, so one value stays on the wick however the item or the window is
        /// turned. Y is measured from the top of the box, where particles already spawn,
        /// so 0 means "top of the item".
        /// </summary>
        /// <summary>
        /// **The CATEGORY was hardcoded to "windowdisplay" until 2026-08-06**, which quietly made
        /// this window-only: a block declaring `psurface0-...-cabinetdisplay` reads
        /// `displayable.cabinetdisplay` for everything else, so its author would write
        /// `particleOffset` there, and it would never be found. Their candles simply would not
        /// light, with nothing logged and no way to tell why.
        ///
        /// It now asks the surface's own category first and falls back to `windowdisplay`. The
        /// fallback is not just compatibility for the ~98 patches written against it: an item
        /// tuned for a window sill should still emit when it is stood on somebody else's shelf,
        /// since the wick is in the same place either way.
        /// </summary>
        private Vec3f ParticleOffsetFor(CollectibleObject collectible, string category)
        {
            JsonObject attrs = collectible?.Attributes?["displayable"];
            if (attrs == null) return null;

            JsonObject node = attrs[category ?? "windowdisplay"]?["particleOffset"];

            if (node?.Exists != true && category != "windowdisplay")
            {
                node = attrs["windowdisplay"]?["particleOffset"];
            }

            if (node?.Exists != true) return null;

            return new Vec3f(node["x"].AsFloat(0f), node["y"].AsFloat(0f), node["z"].AsFloat(0f));
        }

        /// <summary>The display category of the surface a slot id belongs to, or null.</summary>
        private string CategoryForSlot(string slotId)
        {
            DisplaySlotId loc = DisplaySlotId.Decode(slotId);
            return SurfaceBehavior?.GetSurface(loc?.SurfaceIndex ?? -1)?.DisplayCategory;
        }

        /// <summary>
        /// The particle set a stored stack should emit, or null.
        ///
        /// Read off the collectible's own <c>particleProperties</c> rather than through
        /// vanilla's <see cref="IGroundStoredParticleEmitter"/>. That interface exists,
        /// but every implementation of it is written for ground storage: BlockOilLamp
        /// hardcodes a -5/32 quadrant correction and reads
        /// <c>BlockEntityGroundStorage.MeshAngle</c>, which at our position is simply
        /// absent — so the flame would land about 2.5 voxels off and ignore the window's
        /// own rotation. We know where the item is far better than it does.
        ///
        /// Reading the JSON directly also generalises: <c>particleProperties</c> is
        /// content, not code, so any modded block that emits particles works with no
        /// patch from us, where the interface only covers mods that deliberately
        /// implemented it. The trade is that a particle effect driven by code rather
        /// than JSON is missed — BlockMeal's steam is the one vanilla case.
        ///
        /// <see cref="IGroundStoredParticleEmitter.ShouldSpawnGSParticles"/> is still
        /// honoured when present, so "only while lit / only while hot" conditions hold.
        ///
        /// Opt-in: nothing emits without an authored <c>particleOffset</c>. See
        /// <see cref="ParticleOffsetFor"/> for why.
        /// </summary>
        private AdvancedParticleProperties[] ParticlesFor(ItemSlot slot, string category, out Vec3f offsetVoxels)
        {
            offsetVoxels = null;

            CollectibleObject collectible = slot?.Itemstack?.Collectible;
            if (collectible == null) return null;

            offsetVoxels = ParticleOffsetFor(collectible, category);
            if (offsetVoxels == null) return null;

            Block block = collectible as Block ?? BlockBehindItem(collectible);
            if (block?.ParticleProperties == null || block.ParticleProperties.Length == 0) return null;

            var gate = collectible.GetCollectibleInterface<IGroundStoredParticleEmitter>();
            if (gate != null && Api != null && !gate.ShouldSpawnGSParticles(Api.World, slot.Itemstack)) return null;

            return block.ParticleProperties;
        }

        /// <summary>Resolved item -> block lookups. Blocks never change after load.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Block> _blockBehindItem
            = new System.Collections.Concurrent.ConcurrentDictionary<string, Block>();

        /// <summary>
        /// The block an item stands for, so an ITEM can be asked for particle properties
        /// that only ever live on a Block.
        ///
        /// A sill candle is `item/candle`, not `blocktypes/wax/candle`, and the two do NOT
        /// reliably share a code — vanilla's pair happens to, which made this look like it
        /// worked when it did not generalise at all:
        ///
        ///   vanilla candle          -> "bunchocandles"
        ///   censership candle       -> "censership:bunchocandlesceremonial"
        ///   colorfulcandles candle  -> "colorfulcandles:dyedbunchocandles{type}"
        ///
        /// All three declare it as <c>blockfirstcodepart</c>, the vanilla attribute an item
        /// uses to name the block it places, with variant placeholders already substituted
        /// by the time we read it. Following that is what makes modded candles work with no
        /// per-mod knowledge. It is a code PREFIX — "bunchocandles" has to find
        /// "bunchocandles-1" — so an exact lookup is tried first and a prefix scan second.
        /// </summary>
        private Block BlockBehindItem(CollectibleObject collectible)
        {
            if (Api?.World == null || collectible?.Code == null) return null;

            return _blockBehindItem.GetOrAdd(collectible.Code.ToShortString(), _ =>
            {
                Block exact = Api.World.GetBlock(collectible.Code);
                if (exact != null) return exact;

                string first = collectible.Attributes?["blockfirstcodepart"].AsString(null);
                if (first == null) return null;

                var loc = new AssetLocation(first);
                exact = Api.World.GetBlock(loc);
                if (exact != null) return exact;

                foreach (Block candidate in Api.World.Blocks)
                {
                    if (candidate?.Code != null
                        && candidate.Code.Domain == loc.Domain
                        && candidate.Code.Path.StartsWith(loc.Path, StringComparison.Ordinal))
                    {
                        return candidate;
                    }
                }
                return null;
            });
        }

        /// <summary>
        /// Spawns particles for every stored item that emits them, at that item's own
        /// position on its surface.
        ///
        /// Runs on the async particle thread, so it touches nothing but the inventory
        /// and the surface geometry — no mesh state, no relight, and no
        /// ResolveBlocksOrItems (an unresolved stack simply emits nothing this tick;
        /// HasParticleEmittingItem has already resolved on the main thread).
        /// </summary>
        public void SpawnStoredItemParticles(IAsyncParticleManager manager, BlockPos pos, float windAffectednessAtPos)
        {
            if (inv == null || SurfaceBehavior == null) return;

            // One snapshot for the whole pass: SlotEmitPosition needs it too, to measure the
            // pile below each emitter, and taking a second one per item would be pure waste.
            KeyValuePair<string, ItemSlot>[] slots = SlotsSnapshot();

            foreach (var kv in slots)
            {
                AdvancedParticleProperties[] props = ParticlesFor(kv.Value, CategoryForSlot(kv.Key), out Vec3f offsetVoxels);
                if (props == null) continue;

                Vec3f local = SlotEmitPosition(kv.Key, kv.Value, offsetVoxels, slots);
                if (local == null) continue;

                foreach (AdvancedParticleProperties prop in props)
                {
                    prop.WindAffectednesAtPos = windAffectednessAtPos;
                    prop.basePos.X = pos.X + local.X;
                    prop.basePos.Y = pos.InternalY + local.Y;
                    prop.basePos.Z = pos.Z + local.Z;
                    manager.Spawn(prop);
                }
            }
        }

        /// <summary>
        /// The authored wick offset with the entry's OWN display transform applied - the same
        /// rotation and scale <see cref="GenerateMeshes"/> bakes into the item mesh through
        /// <c>MeshData.ModelTransform</c>.
        ///
        /// **WITHOUT THIS THE MESH AND THE FLAME ARE IN DIFFERENT FRAMES**, and nothing says so.
        /// An entry carrying `rotation.y: 45` and `scale: 0.8` - the vanilla oil lamp does -
        /// draws a lamp turned and shrunk while the offset points straight ahead at full size,
        /// so the flame sits beside the lamp rather than on its wick. Author it against the
        /// model and the code puts it where the model went. Found 2026-08-30, when a lamp that
        /// had been correct for months drifted after its transform was edited: the offset had
        /// been tuned by eye to cancel the rotation, so changing the rotation broke it silently.
        ///
        /// **ROTATION AND SCALE ONLY. Translation and a non-centre `origin` are deliberately
        /// NOT applied**, because they move the item as a whole rather than turning it about
        /// itself, and where the item as a whole sits is already the slot's business - see
        /// SlotEmitPosition, which places it from the surface and the pile below it. Applying
        /// them here would move the flame twice.
        ///
        /// Reads the transform without calling <c>EnsureDefaultValues</c>: that WRITES to the
        /// shared attributes object, and this runs on the async particle thread. Every field is
        /// treated as optional instead, which is the same thing without the race.
        /// </summary>
        private static Vec3f TransformedOffset(Vec3f offsetVoxels, DisplayableAttributes dattr)
        {
            ModelTransform tf = dattr?.Transform;
            if (offsetVoxels == null || tf == null) return offsetVoxels;

            // ScaleXYZ is a STRUCT and `Scale` is set-only, so there is no null to test and no
            // getter to fall back on. An unset transform reads (0,0,0), which would collapse the
            // offset to nothing - and `EnsureDefaultValues` is what fills it, on the main thread
            // in GenerateMeshes, which may not have run yet on the first tick. A zero component
            // therefore means UNSET and is read as 1: an item genuinely scaled to zero has no
            // mesh to sit a flame on anyway.
            FastVec3f s = tf.ScaleXYZ;
            float sx = s.X == 0f ? 1f : s.X;
            float sy = s.Y == 0f ? 1f : s.Y;
            float sz = s.Z == 0f ? 1f : s.Z;

            // Matrixf rather than hand-rolled sin/cos, so the rotation convention cannot drift
            // from the one SlotEmitPosition and BuildSlotMatrix already use.
            Vec4f v = new Matrixf()
                .RotateYDeg(tf.Rotation.Y)
                .TransformVector(new Vec4f(offsetVoxels.X * sx,
                                           offsetVoxels.Y * sy,
                                           offsetVoxels.Z * sz, 1f));
            return new Vec3f(v.X, v.Y, v.Z);
        }
        /// <summary>
        /// Where a stored item's particles should come from, in block-local units
        /// (0..1), rotated to match how the block was placed.
        ///
        /// Deliberately NOT taken from <see cref="BuildSlotMatrix"/>: that method has a
        /// history of leaving TfMatrices short an entry when anything throws, and it is
        /// not worth reshaping for this. The two agree by construction instead —
        /// centring cancels, since BuildSlotMatrix offsets by -Size/2 and then back by
        /// +Size/2, leaving the item's centre at surface + slot. The same
        /// Translate/RotateY chain is reused verbatim so the rotation convention cannot
        /// drift from the renderer's.
        ///
        /// Height is the top of the item's declared box, which is where a flame sits:
        /// for a vanilla oil lamp that is 2/16, against the 9/64 vanilla itself uses —
        /// a quarter of a voxel apart.
        ///
        /// <paramref name="offsetVoxels"/> is the authored wick offset, applied in the
        /// item's OWN space — rotated by the item's placement rotation before the block's,
        /// exactly as BuildSlotMatrix rotates the mesh about its own centre. That is what
        /// keeps a flame on the wick after a wrench turn or on a rotated window. A zero
        /// offset reduces to the plain centre, which is what candles want.
        /// </summary>
        private Vec3f SlotEmitPosition(string slotId, ItemSlot slot, Vec3f offsetVoxels,
                                       KeyValuePair<string, ItemSlot>[] slots = null)
        {
            DisplaySlotId loc = DisplaySlotId.Decode(slotId);
            DisplaySurface surface = SurfaceBehavior.GetSurface(loc?.SurfaceIndex ?? -1);
            if (surface == null) return null;

            DisplayableAttributes dattr = BlockBehaviorDisplaySurfaces.GetDisplayableAttributes(
                slot, surface.DisplayCategory ?? BlockBehaviorDisplaySurfaces.DefaultDisplayCategory);
            if (dattr?.Size == null) return null;

            Vec3f surfacePos = surface.VoxelPosition;
            float stackY = SlotRestY(loc, surface, dattr, slots);

            // The item's own resting angle, same three terms and same order as
            // BuildSlotMatrix: placement jitter, then any wrench override, then the
            // surface facing added last so wrench steps stay relative to it.
            // ItemRotationDeg, not a local copy of the rule — it is chosen + jitter + facing,
            // and the mesh, the footprint and this must all read the SAME number.
            float rotDeg = ItemRotationDeg(slotId, dattr, surface);

            // The offset is authored against the MODEL, so it has to go through the entry's own
            // transform before the item's placement rotation - see TransformedOffset.
            Vec3f offset = TransformedOffset(offsetVoxels, dattr);

            return new Matrixf()
                .Translate(0.5f, 0f, 0.5f)
                .RotateYDeg(Block.Shape?.rotateY ?? 0f)
                .RotateY(MeshAngleRad)
                .Translate(
                    (surfacePos.X + loc.X) / 16f - 0.5f,
                    (surfacePos.Y + stackY + dattr.Size.Height) / 16f,
                    (surfacePos.Z + loc.Z) / 16f - 0.5f)
                .RotateY(GameMath.DEG2RAD * rotDeg)
                .TransformVector(new Vec4f(
                    (offset?.X ?? 0f) / 16f,
                    (offset?.Y ?? 0f) / 16f,
                    (offset?.Z ?? 0f) / 16f, 1f)).XYZ;
        }

        // ── Selection / collision boxes ──────────────────────────────────────

        // ── Stacking ─────────────────────────────────────────────────────────

        /// <summary>
        /// How high the base of the item at <paramref name="loc"/> sits above its surface,
        /// in voxels — the summed declared HEIGHTS of everything already stacked below it.
        ///
        /// **This was `loc.Y * <the item's own height>`**, which is only the same number when
        /// every item in a pile is the same height. Anything else floated or sank by the
        /// difference: a plant is 7 tall, so on a shorter pot it hung in the air above the rim
        /// and on a taller one it disappeared into it. Its own height says how much room it
        /// needs, not how far up the thing underneath lifts it.
        ///
        /// ONE definition, shared by the mesh, the selection box, the footprint outline, the
        /// particle origin and the headroom checks. All six derived it separately from the old
        /// expression, and a preview disagreeing with the placement is this codebase's
        /// recurring fault — hence a method rather than six copies of the loop.
        ///
        /// <paramref name="selfAttr"/> is only a FALLBACK height, used for a level that reads
        /// back empty or unresolved. Piles are contiguous (taking the bottom item collapses
        /// the rest down) so that should not happen; when it does, falling back to the old
        /// uniform-pile arithmetic keeps the item roughly where it was instead of dropping
        /// the whole stack to the floor.
        ///
        /// <paramref name="slots"/> lets a caller that already holds a <see cref="SlotsSnapshot"/>
        /// reuse it. Omitting it takes a fresh one, which is what makes this safe to call off
        /// the main thread: the live <c>SlotsByslotId</c> must never be touched there.
        /// </summary>
        public float StackBaseY(DisplaySlotId loc, DisplaySurface surface,
                                DisplayableAttributes selfAttr,
                                KeyValuePair<string, ItemSlot>[] slots = null)
        {
            if (loc == null || surface == null || loc.Y <= 0f) return 0f;

            slots ??= SlotsSnapshot();
            string category = surface.DisplayCategory ?? BlockBehaviorDisplaySurfaces.DefaultDisplayCategory;
            float fallback = selfAttr?.Size?.Height ?? 0f;

            float baseY = 0f;
            for (int level = 0; level < (int)loc.Y; level++)
            {
                DisplayableAttributes below = FindSlotAttributes(slots, loc.WithY(level).Encoded, category);
                baseY += below?.Size?.Height ?? fallback;
            }

            return baseY;
        }

        /// <summary>
        /// Where an item actually sits: the pile below it, plus the player's own nudge.
        ///
        /// **This is what everything visual should use** — the mesh, the selection box and
        /// the particle origin. <see cref="StackBaseY"/> alone is the natural resting height
        /// and is what the HEADROOM checks use, deliberately: a nudge is a cosmetic
        /// correction and must not change whether the next item is allowed on the pile.
        ///
        /// The nudge applies to this item ONLY. It does not cascade to whatever is stacked
        /// above, because it exists to correct one awkward item rather than to move a pile;
        /// cascading would spread the exception. Nudge the one above too if you want both.
        ///
        /// <paramref name="nudgeOverrideVoxels"/> is for the placement ghost, which is
        /// previewing an item that has no stored nudge yet — the same reason
        /// <see cref="BuildSlotMatrix"/> takes a rotation override.
        /// </summary>
        public float SlotRestY(DisplaySlotId loc, DisplaySurface surface,
                               DisplayableAttributes selfAttr,
                               KeyValuePair<string, ItemSlot>[] slots = null,
                               float? nudgeOverrideVoxels = null)
        {
            return StackBaseY(loc, surface, selfAttr, slots)
                 + (nudgeOverrideVoxels ?? ChosenHeightVoxels(loc?.Encoded));
        }

        /// <summary>
        /// The nudge stored against a slot, or 0. The height twin of ChosenRotationDeg —
        /// and like it, 0 is a real answer rather than a missing one.
        /// </summary>
        public float ChosenHeightVoxels(string slotId)
        {
            return slotId != null && customHeightBySlot != null
                && customHeightBySlot.TryGetValue(slotId, out float v) ? v : 0f;
        }

        /// <summary>
        /// Display properties of one stored slot, looked up in a snapshot rather than through
        /// <c>inv[id]</c> — the indexer on <c>InventoryInfinite</c> is a WRITE, and this runs
        /// on the tesselation and particle threads.
        /// </summary>
        private static DisplayableAttributes FindSlotAttributes(
            KeyValuePair<string, ItemSlot>[] slots, string slotId, string category)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                if (!string.Equals(slots[i].Key, slotId, StringComparison.Ordinal)) continue;

                ItemSlot slot = slots[i].Value;
                if (slot == null || slot.Empty) return null;

                // No ResolveBlocksOrItems: off the main thread it crashes the client, and an
                // unresolved stack simply reads as null here and takes the fallback height.
                return BlockBehaviorDisplaySurfaces.GetDisplayableAttributes(slot, category);
            }

            return null;
        }

        /// <summary>True when another window sits directly below this one.</summary>
        public bool IsStacked =>
            Pos != null && Api?.World.BlockAccessor.GetBlockEntity(Pos.DownCopy()) is BEUniversalDisplay;

        /// <summary>
        /// Which surfaces stay usable when this window is stacked on another, from the
        /// <c>showSurfacesWhenStacked</c> block attribute. Values are psurface indices.
        ///
        /// Opt-in by design: with the attribute absent every surface hides, because a
        /// window sitting on another window normally has its sills obstructed. A style
        /// that wants them anyway declares which ones.
        ///
        /// <code>
        /// // absent                          -> all surfaces hidden when stacked
        /// "showSurfacesWhenStacked": [0]      // only psurface0 stays usable
        /// "showSurfacesWhenStacked": [0, 2]   // psurface0 and psurface2 stay usable
        /// </code>
        ///
        /// Named for surfaces rather than groups: the pre-psurface content used
        /// <c>stackingHideGroups</c>, indexing <c>slotGroups</c> and listing what to HIDE.
        /// Neither groups nor that inversion survive here — this lists what to SHOW, by
        /// psurface index.
        /// </summary>
        public bool IsSurfaceStacked(int surfaceIndex)
            => IsStacked && IsSurfaceHiddenWhenStacked(surfaceIndex, ShownSurfacesWhenStacked());

        /// <summary>
        /// The declared <c>showSurfacesWhenStacked</c> list, or null when absent — which
        /// means every surface hides. Split out so a loop can read the attribute once
        /// instead of per item.
        /// </summary>
        private int[] ShownSurfacesWhenStacked()
        {
            JsonObject attr = Block?.Attributes?["showSurfacesWhenStacked"];
            return attr != null && attr.Exists ? attr.AsArray<int>(null) : null;
        }

        /// <summary>
        /// Whether a surface hides, given an already-resolved list. Callers that have
        /// established the block IS stacked use this directly; the list is tiny, so the
        /// scan is cheaper than re-reading the attribute.
        /// </summary>
        private static bool IsSurfaceHiddenWhenStacked(int surfaceIndex, int[] shown)
            => shown == null || Array.IndexOf(shown, surfaceIndex) < 0;

        /// <summary>
        /// Which way items on a surface start facing, in degrees about Y.
        ///
        /// Normally worked out from the surface's own geometry — see
        /// <see cref="DisplaySurface.FacingRotationDeg"/> — so nothing needs
        /// declaring. The optional <c>surfaceFacingDeg</c> block attribute overrides that
        /// per psurface index, for a surface whose side the geometry cannot infer (one
        /// centred on the block, or straddling the middle) or that simply wants a
        /// deliberate angle.
        ///
        /// <code>
        /// // absent                       -> every surface derives its own facing
        /// "surfaceFacingDeg": { "1": 90 }  // psurface1 faces east, the rest still derive
        /// </code>
        ///
        /// Keys are psurface indices as written in the element name, so they stay correct
        /// even when a shape's indices are not contiguous.
        /// </summary>
        public float SurfaceFacingDeg(DisplaySurface surface)
        {
            JsonObject attr = Block?.Attributes?["surfaceFacingDeg"];
            if (attr != null && attr.Exists)
            {
                JsonObject one = attr[surface.Index.ToString()];
                if (one != null && one.Exists) return one.AsFloat(surface.FacingRotationDeg);
            }
            return surface.FacingRotationDeg;
        }

        private bool IsSlotSurfaceStacked(string slotId)
        {
            DisplaySlotId loc = DisplaySlotId.Decode(slotId);
            return loc != null && IsSurfaceStacked(loc.SurfaceIndex);
        }

        /// <summary>
        /// Whether a chiselled design in this slot is FIXED: once placed it cannot be taken
        /// by hand, turned or nudged. Breaking the block still drops it.
        ///
        /// Added 2026-08-07 for the griefing report that also brought the land-claim check in
        /// <see cref="OnBlockInteractStart"/>. Claims only cover CLAIMED land; on an unclaimed
        /// build anyone could still right-click the chiselled insert out of a custom window
        /// and leave a hole. This is the second layer, and it is deliberately narrow.
        ///
        /// TWO conditions, and neither alone is enough:
        ///
        ///  1. the block lists the psurface in <c>lockChiselledSurfaces</c>. Absent means
        ///     nothing is locked, so no existing block and no modder's block changes
        ///     behaviour until it opts in — same opt-in shape as
        ///     <c>showSurfacesWhenStacked</c>, and the same list-of-psurface-indices format.
        ///  2. what is actually stored there is a <see cref="BlockMicroBlock"/>.
        ///
        /// The second condition is the point, and it is why this is not simply a "sealed
        /// surface" flag. A microblock is `IDisplayableProps`, so a chiselled ornament can be
        /// set on ANY surface this library exposes — locking on type alone would silently
        /// freeze every chiselled decoration on every window sill in every existing world.
        /// Locking on surface alone would be worse in the other direction: the custom
        /// window's psurface0 would swallow anything at all, so a stranger could push junk
        /// into it that the owner then has to BREAK the window to clear. Requiring both means
        /// the structural insert is permanent and everything else behaves exactly as before.
        ///
        /// <code>
        /// // absent                        -> nothing is locked (every block but customwindow)
        /// "lockChiselledSurfaces": [ 0 ]   // a chiselled block on psurface0 is there for good
        /// </code>
        ///
        /// AND ONLY THE FIRST ONE PLACED, which is the third condition and the reason
        /// <see cref="lockedSlotBySurface"/> exists as stored state. The first chiselled block
        /// fitted to a locked surface is the structural piece; anything chiselled added after
        /// it is decoration and stays takeable.
        ///
        /// Fails OPEN when the stack cannot be resolved — <see cref="SlotById"/> resolves
        /// first, so that is close to unreachable here, but refusing to unlock is the safer
        /// direction of the two: an item wrongly stuck is a bug report with no workaround
        /// short of breaking the block.
        /// </summary>
        public bool IsSlotLocked(string slotId)
        {
            DisplaySlotId loc = DisplaySlotId.Decode(slotId);
            if (loc == null || !IsLockableSurface(loc.SurfaceIndex)) return false;

            if (lockedSlotBySurface == null
                || !lockedSlotBySurface.TryGetValue(loc.SurfaceIndex, out string claimed)
                || claimed != slotId)
            {
                return false;
            }

            // The recorded id is still checked against what is actually there. A stale entry —
            // one whose design has gone, however that happened — must not lock whatever lands
            // in that slot next.
            //
            // SlotById, never inv[slotId] — the indexer on InventoryInfinite is a WRITE and
            // creates the slot, and this is reached from the client's wheel handler.
            return SlotById(slotId)?.Itemstack?.Block is BlockMicroBlock;
        }

        /// <summary>Whether the block lists this psurface in <c>lockChiselledSurfaces</c>.</summary>
        private bool IsLockableSurface(int surfaceIndex)
        {
            JsonObject attr = Block?.Attributes?["lockChiselledSurfaces"];
            int[] locked = attr != null && attr.Exists ? attr.AsArray<int>(null) : null;
            return locked != null && Array.IndexOf(locked, surfaceIndex) >= 0;
        }

        /// <summary>
        /// Records a newly placed chiselled block as this surface's locked design, if the
        /// surface is lockable and nothing holds the claim yet.
        ///
        /// Called from <see cref="PlaceItem"/>, which is the single choke point both TryPut and
        /// TryStack pass through, so there is one place where a design can become the first.
        ///
        /// An existing claim is honoured only while its slot still holds a chiselled block;
        /// a stale one is overwritten rather than blocking the surface for good.
        /// </summary>
        private void ClaimLockIfFirst(string slotId, ItemSlot placed)
        {
            if (placed?.Itemstack?.Block is not BlockMicroBlock) return;

            DisplaySlotId loc = DisplaySlotId.Decode(slotId);
            if (loc == null || !IsLockableSurface(loc.SurfaceIndex)) return;

            lockedSlotBySurface ??= new Dictionary<int, string>();
            if (lockedSlotBySurface.TryGetValue(loc.SurfaceIndex, out string claimed)
                && SlotById(claimed)?.Itemstack?.Block is BlockMicroBlock)
            {
                return;   // someone got there first, and it is still there
            }

            lockedSlotBySurface[loc.SurfaceIndex] = slotId;
        }

        /// <summary>
        /// Gives a chiselled design that was placed BEFORE this feature existed its lock.
        ///
        /// Without this every custom window already standing in a world would stay unlocked
        /// for good — the claim is recorded at placement time, and those placements are in the
        /// past. Published content 1.0.2 has been shipping the custom window since 2026-07-27,
        /// so those windows are the exact ones the griefing report was about.
        ///
        /// Server side only, once, on load: for each lockable surface with no claim, the
        /// LOWEST slot id holding a chiselled block takes it. Ordinal string order is not
        /// meaningful geometry — it is chosen because it is deterministic, so the client and
        /// server cannot adopt different slots. In practice a window in the wild has exactly
        /// one chiselled block in it and there is nothing to choose between.
        /// </summary>
        private void AdoptPreExistingLocks()
        {
            JsonObject attr = Block?.Attributes?["lockChiselledSurfaces"];
            int[] lockable = attr != null && attr.Exists ? attr.AsArray<int>(null) : null;
            if (lockable == null || lockable.Length == 0) return;

            bool changed = false;

            foreach (int surfaceIndex in lockable)
            {
                if (lockedSlotBySurface != null
                    && lockedSlotBySurface.TryGetValue(surfaceIndex, out string claimed)
                    && SlotById(claimed)?.Itemstack?.Block is BlockMicroBlock)
                {
                    continue;
                }

                string lowest = null;
                foreach (var kv in SlotsSnapshot())
                {
                    if (kv.Value == null || kv.Value.Empty) continue;
                    if (kv.Value.Itemstack?.Block is not BlockMicroBlock) continue;

                    DisplaySlotId loc = DisplaySlotId.Decode(kv.Key);
                    if (loc == null || loc.SurfaceIndex != surfaceIndex) continue;

                    if (lowest == null || string.CompareOrdinal(kv.Key, lowest) < 0) lowest = kv.Key;
                }

                if (lowest == null) continue;

                lockedSlotBySurface ??= new Dictionary<int, string>();
                lockedSlotBySurface[surfaceIndex] = lowest;
                changed = true;
            }

            // Persisted, not just held in memory, so the adoption happens once rather than on
            // every load. MarkDirty(false): the client needs the map, but nothing about the
            // block's appearance changed, so this must not force a re-tesselation or a relight.
            if (changed) MarkDirty(false);
        }

        public void InvalidateBoxes()
        {
            _padOnlyBoxes = null;
            _allBoxes = null;
            _collisionBoxes = null;
        }

        private CuboidfWithId Rotated(Cuboidf box, string id)
        {
            var rotated = new CuboidfWithId(box.RotatedCopyRad(0f, MeshAngleRad, 0f, new Vec3d(0.5, 0.0, 0.5)))
            {
                Id = id ?? (box as CuboidfWithId)?.Id
            };
            return rotated;
        }

        /// <summary>
        /// Never returns null, and never reads a field twice.
        ///
        /// This is called from the RENDER thread while InvalidateBoxes can fire from the
        /// network thread on any MarkDirty — placing, rotating or a pane sync. The old form
        /// tested `_padOnlyBoxes` for null, skipped the rebuild, and then returned `_allBoxes`,
        /// so an invalidation landing between those two statements returned **null** straight
        /// into vanilla's `BlockGeneric.GetSelectionBoxes`, which dereferences it: an
        /// instant client crash from the render thread, with nothing logged because the
        /// throw is in vanilla's frame, not ours.
        ///
        /// Both fields are snapshotted into locals first so a concurrent invalidation cannot
        /// pull one out from under the check, either is enough to force a rebuild, and an
        /// empty array is returned rather than null if it still comes back empty-handed.
        /// Boxes are cheap to rebuild and a crash is not.
        /// </summary>
        public Cuboidf[] GetSelectionBoxes()
        {
            Cuboidf[] pads = _padOnlyBoxes;
            Cuboidf[] all = _allBoxes;

            if (pads == null || all == null)
            {
                RegenSelectionBoxes();
                pads = _padOnlyBoxes;
                all = _allBoxes;
            }

            return (IsPlacingPreview() ? pads : all) ?? Array.Empty<Cuboidf>();
        }

        private void RegenSelectionBoxes()
        {
            BlockBehaviorDisplaySurfaces bh = SurfaceBehavior;
            var boxes = new List<Cuboidf>();

            // Resolve BEFORE building, for the same reason OnTesselation does: the item
            // loop below reads slot.Itemstack.Collectible through GetDisplayableAttributes,
            // and on an unresolved stack that is null, so dattr comes back null and the
            // slot's box is skipped without a word. Boxes are rebuilt off the raycast, not
            // off tesselation, so after a sync they could be built while stacks were still
            // unresolved — an item then had no box, the ray passed through it and hit the
            // one below, and the box only appeared once something else forced a rebuild.
            inv.ResolveBlocksOrItems();

            // Stacking is constant for the whole rebuild, so it is resolved once here and
            // shared by both loops below. Per call it costs a BlockPos allocation, a block
            // entity lookup and a JSON attribute read — and the pad loop runs 224 times on
            // a typical window, 308 on a display unit, since maxXDivisions/maxZDivisions
            // default to 32 and a surface grids to width x depth. Unstacked, which is the
            // usual case, the pad loop now skips its slot-id string parse entirely.
            bool stacked = IsStacked;
            int[] shownWhenStacked = stacked ? ShownSurfacesWhenStacked() : null;

            if (bh != null)
            {
                // Frame and pane boxes — ids let interaction tell them apart
                for (int i = 0; i < bh.FrameBoxGroups.Length; i++)
                {
                    FrameBoxGroup group = bh.FrameBoxGroups[i];

                    if (group.StaticFrameBoxes != null)
                    {
                        for (int n = 0; n < group.StaticFrameBoxes.Length; n++)
                        {
                            boxes.Add(Rotated(group.StaticFrameBoxes[n], "frame" + i + "-" + n));
                        }
                    }

                    bool open = i < paneStates.Length && paneStates[i];
                    Cuboidf paneBox = open ? group.OpenFrameBox : group.ClosedFrameBox;
                    if (paneBox != null) boxes.Add(Rotated(paneBox, "pane" + i));
                }

                // Placement pads, flattened to a 1-voxel lip like vanilla.
                // A stacked surface contributes none, so it cannot be aimed at — the
                // window above covers it.
                foreach (CuboidfWithId pad in bh.GridBoxes)
                {
                    if (stacked)
                    {
                        // An id that will not decode keeps its pad, as it did before —
                        // dropping it would silently remove a placement spot
                        DisplaySlotId padLoc = DisplaySlotId.Decode(pad.Id);
                        if (padLoc != null
                            && IsSurfaceHiddenWhenStacked(padLoc.SurfaceIndex, shownWhenStacked))
                        {
                            continue;
                        }
                    }

                    CuboidfWithId rotated = Rotated(pad, pad.Id);
                    rotated.Y1 -= 0.0625f;
                    rotated.Y2 = rotated.Y1 + 0.0625f;
                    boxes.Add(rotated);
                }
            }

            _padOnlyBoxes = boxes.ToArray();

            // Outlines of already-placed items, on top of the pads
            var withItems = new List<Cuboidf>(boxes);
            if (bh != null)
            {
                // Snapshot once for StackBaseY below rather than per item — it measures the
                // pile under each box, and this loop runs over every stored item.
                KeyValuePair<string, ItemSlot>[] slots = SlotsSnapshot();

                foreach (var kv in slots)
                {
                    ItemSlot slot = kv.Value;
                    if (slot.Empty) continue;

                    DisplaySlotId loc = DisplaySlotId.Decode(kv.Key);
                    DisplaySurface surface = bh.GetSurface(loc?.SurfaceIndex ?? -1);
                    if (surface == null) continue;

                    // Items on a stacked surface stay rendered but stop being selectable
                    if (stacked && IsSurfaceHiddenWhenStacked(loc.SurfaceIndex, shownWhenStacked))
                    {
                        continue;
                    }

                    DisplayableAttributes dattr = BlockBehaviorDisplaySurfaces
                        .GetDisplayableAttributes(slot, surface.DisplayCategory ?? "shelf");
                    if (dattr == null) continue;

                    // Same rule as ContentCuboids: the box follows the item's own rotation,
                    // so a wrenched item is clickable where it actually is.
                    float shownRot = ItemRotationDeg(kv.Key, dattr, surface);
                    Size3f shown = BlockBehaviorDisplaySurfaces.RotatedFootprint(dattr.Size, shownRot);

                    Vec3f offset = BlockBehaviorDisplaySurfaces.GetCentreOffset(shown);

                    // SlotRestY, not StackBaseY: the box has to follow a nudged item or you
                    // would be clicking empty air to take it back out.
                    float stackY = SlotRestY(loc, surface, dattr, slots);

                    var itemBox = new CuboidfWithId(0f, 0f, 0f,
                            shown.Width / 16f, shown.Height / 16f, shown.Length / 16f)
                        .Translate(
                            (surface.VoxelPosition.X + loc.X + offset.X) / 16f,
                            (surface.VoxelPosition.Y + stackY) / 16f,
                            (surface.VoxelPosition.Z + loc.Z + offset.Z) / 16f);

                    withItems.Add(Rotated(itemBox, DisplaySlotId.PlacedPrefix + kv.Key));
                }
            }

            _allBoxes = withItems.ToArray();
        }

        /// <summary>
        /// True while the player is lining up a placeable item, in which case only
        /// the pads are exposed so placed items do not block aiming.
        /// </summary>
        private bool IsPlacingPreview()
        {
            if (capi == null) return false;

            ItemSlot held = capi.World.Player?.InventoryManager?.ActiveHotbarSlot;
            BlockSelection sel = capi.World.Player?.CurrentBlockSelection;
            if (held == null || held.Empty || sel?.SelectionBoxId == null) return false;

            // The wrench was excluded here so its boxes stayed exposed for rotating a stored
            // item. Rotation moved to the mouse wheel on 2026-07-30 and the wrench became an
            // ordinary placeable item, so the exclusion had turned into a nuisance: placed
            // items kept blocking the pad you were aiming at while trying to put a wrench
            // down. It now behaves like anything else in hand.
            //
            // This does NOT affect swapping. Despite the name, _padOnlyBoxes carries the
            // frame and pane boxes too — only PLACED ITEM boxes are withheld — so
            // ctrl+wrench on a frame is unchanged either way. Checked before removing it.

            DisplaySlotId loc = DisplaySlotId.Decode(sel.SelectionBoxId);
            if (loc == null) return false;

            string category = SurfaceBehavior?.GetDisplayCategory(loc.SurfaceIndex) ?? "shelf";
            DisplayableAttributes dattr = BlockBehaviorDisplaySurfaces
                .GetDisplayableAttributes(held, category);
            if (dattr == null) return false;

            // Expose the item boxes when the held item stacks and something is already at
            // the aim point, as vanilla does. Pads lie flattened at the surface floor, so a
            // stack buries them: without this you had to aim at the very base of a stack to
            // add to it. With the item boxes visible, aiming at ANY item in the stack finds
            // it by collision and TryStack walks up to the first free level.
            if (dattr.Behavior == EnumDisplayableBehavior.Stacking
                && GetCollidingSlotId(loc, new Cuboidf(dattr.Size)) != null)
            {
                return false;
            }

            return true;
        }

        public Cuboidf[] GetCollisionBoxes()
        {
            // Snapshot for the same reason as GetSelectionBoxes: this is read off the main
            // thread while InvalidateBoxes can null the field from the network thread.
            Cuboidf[] cached = _collisionBoxes;
            if (cached != null) return cached;

            var result = new List<Cuboidf>();
            CollisionBoxGroup[] groups = SurfaceBehavior?.CollisionBoxGroups;

            if (groups != null && groups.Length > 0)
            {
                for (int i = 0; i < groups.Length; i++)
                {
                    CollisionBoxGroup group = groups[i];

                    if (group.StaticCollisionBoxes != null)
                    {
                        foreach (Cuboidf box in group.StaticCollisionBoxes) result.Add(Rotated(box, null));
                    }

                    bool open = i < paneStates.Length && paneStates[i];
                    Cuboidf dynamicBox = open ? group.OpenCollisionBox : group.ClosedCollisionBox;
                    if (dynamicBox != null) result.Add(Rotated(dynamicBox, null));
                }
            }
            else if (Block?.CollisionBoxes != null)
            {
                foreach (Cuboidf box in Block.CollisionBoxes) result.Add(Rotated(box, null));
            }

            // Return the local, not the field — the field can be nulled by a concurrent
            // InvalidateBoxes between the assignment and the return.
            cached = result.ToArray();
            _collisionBoxes = cached;
            return cached;
        }

        // ── Content geometry helpers ─────────────────────────────────────────

        private List<KeyValuePair<Cuboidf, string>> ContentCuboids(int surfaceIndex)
        {
            var result = new List<KeyValuePair<Cuboidf, string>>();
            BlockBehaviorDisplaySurfaces bh = SurfaceBehavior;
            if (bh == null) return result;

            // Same reason as RegenSelectionBoxes: GetItemSize reads the collectible, and an
            // unresolved stack contributes no cuboid — which would make an occupied slot
            // look free to collision, stacking and take-out alike.
            inv.ResolveBlocksOrItems();

            foreach (var kv in inv.SlotsByslotId)
            {
                if (kv.Value.Empty) continue;

                DisplaySlotId loc = DisplaySlotId.Decode(kv.Key);
                if (loc == null || loc.SurfaceIndex != surfaceIndex) continue;

                Size3f size = BlockBehaviorDisplaySurfaces.GetItemSize(kv.Value, bh.GetDisplayCategory(surfaceIndex));
                if (size == null) continue;

                // The space an item takes follows how it is turned, not how it was authored
                size = BlockBehaviorDisplaySurfaces.RotatedFootprint(size,
                    ItemRotationDeg(kv.Key,
                        BlockBehaviorDisplaySurfaces.GetDisplayableAttributes(kv.Value, bh.GetDisplayCategory(surfaceIndex)),
                        bh.GetSurface(surfaceIndex)));

                result.Add(new KeyValuePair<Cuboidf, string>(
                    new Cuboidf(loc.X - size.Width / 2f, loc.Y, loc.Z - size.Length / 2f,
                                loc.X + size.Width / 2f, loc.Y + 1f, loc.Z + size.Length / 2f),
                    kv.Key));
            }

            return result;
        }

        public string GetCollidingSlotId(DisplaySlotId at, Cuboidf placeBox)
        {
            if (at == null) return null;
            foreach (var entry in ContentCuboids(at.SurfaceIndex))
            {
                if (entry.Key.Intersects(placeBox, at.X, at.Y, at.Z)) return entry.Value;
            }
            return null;
        }

        public string GetSelectedNonEmptySlotId(BlockSelection blockSel)
        {
            DisplaySlotId loc = DisplaySlotId.Decode(blockSel?.SelectionBoxId);
            if (loc == null) return null;

            // A "p-" box already names its slot
            if (loc.IsPlacedItem)
            {
                string direct = blockSel.SelectionBoxId.Substring(DisplaySlotId.PlacedPrefix.Length);
                if (inv[direct] != null && !inv[direct].Empty) return direct;
            }

            // Falling back from a pad box, which always reports Y 0. Content cuboids span
            // one stack level each (loc.Y .. loc.Y + 1), so probing Y 0.1 only ever found
            // the BOTTOM of a stack — wrenching or taking while aimed at a pad would reach
            // past the items above it. Take the highest occupied level at this x/z instead,
            // which is the one actually visible and the one TryStack would have added.
            string topmost = null;
            float topmostY = float.NegativeInfinity;

            foreach (var entry in ContentCuboids(loc.SurfaceIndex))
            {
                if (!entry.Key.Contains(loc.X, entry.Key.Y1 + 0.1f, loc.Z)) continue;
                if (entry.Key.Y1 <= topmostY) continue;

                topmostY = entry.Key.Y1;
                topmost = entry.Value;
            }

            return topmost;
        }

        // ── Interaction ──────────────────────────────────────────────────────

        public bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            // ── LAND CLAIMS. Nothing here asked permission until 2026-08-07. ──────────
            //
            // Every vanilla container and display block calls this; this mod never did, so a
            // claim gave a display block no protection at all. Anyone could stand in someone
            // else's claim and right-click their windows empty. Reported against the older
            // Custom Window Storage as "the chiselled block can just be right-clicked out of
            // the frame by anyone and then the window is just a hole" — the same hole existed
            // here, and on every block any mod builds on this library.
            //
            // ONE guard at the entry point covers everything the block does: placing, taking,
            // opening a pane and swapping all arrive through here.
            //
            // **It returns TRUE, not false.** False falls through to
            // `BlockUniversalDisplay.OnBlockInteractStart` -> `base`, and the held item would
            // be PLACED — so a player refused permission to touch the block would end up
            // building on it instead. True consumes the interaction and nothing happens.
            //
            // TryAccess tells the player why itself, so there is no message to write.
            if (blockSel != null
                && !world.Claims.TryAccess(byPlayer, blockSel.Position, EnumBlockAccessFlags.Use))
            {
                return true;
            }

            string boxId = blockSel?.SelectionBoxId;
            ItemSlot held = byPlayer.InventoryManager.ActiveHotbarSlot;

            // WorldData.EntityControls, NOT Entity.Controls — vanilla's own
            // BEBehaviorDisplay.TryRotate reads exactly this for the same job. The two are
            // different pipelines: SystemPlayerControl fills Entity.Controls from the local
            // keyboard and it reaches the server through the synced flags int, so client
            // and server can disagree about a modifier for a frame. WorldData's set is the
            // one both sides agree on, and a divergence here shows up as "the tooltip says
            // one key but a different key works".
            EntityControls controls = byPlayer.WorldData.EntityControls;
            bool isWrench = held?.Itemstack?.Collectible?.GetTool(held) == EnumTool.Wrench;

            // Ctrl, and only ctrl — what vanilla trains people to reach for, and it must
            // stay in step with the HotKeyCode the interaction help advertises.
            //
            // This used to be `controls.Sprint || controls.CtrlKey`, which is the bug: it
            // ORed a rebindable movement ACTION (Sprint) with a raw modifier KEY (CtrlKey),
            // so which key actually swapped depended on the player's own bindings, and the
            // behaviour looked intermittent. EntityControls keeps all four separate —
            // Sneak/Sprint are movement actions, ShiftKey/CtrlKey are modifier keys — and
            // vanilla reads the KEY for modified block interactions (BEBehaviorMannequin,
            // BEBehaviorCabinetDoors). It is network-synced: EnumEntityAction.CtrlKey is 13
            // and EntityControls.ToInt serialises flags[13], so it is safe server-side.
            //
            // Reverse-rotation shares the flag, which is fine and always was: swapping
            // needs the FRAME, rotating needs an ITEM box, so they can never both fire.
            bool ctrl = controls.CtrlKey;

            bool onFrame = boxId == null
                           || boxId.StartsWith("pane", StringComparison.Ordinal)
                           || boxId.StartsWith("frame", StringComparison.Ordinal);

            // 1. Ctrl + wrench on the frame → swap the whole block
            if (BlockBehaviorDisplaySurfaces.IsSwapAimPoint(SurfaceBehavior, boxId) && ctrl && isWrench)
            {
                return BlockSwapHelper.TrySwap(world, Pos, byPlayer, this);
            }

            // 2. Frame or pane click → toggle that pane
            if (onFrame)
            {
                if (!CanOpen) return false;

                int paneIndex = 0;
                if (boxId != null && boxId.StartsWith("pane", StringComparison.Ordinal))
                {
                    int.TryParse(boxId.Substring(4), out paneIndex);
                }
                else if (boxId != null && boxId.StartsWith("frame", StringComparison.Ordinal))
                {
                    int dash = boxId.IndexOf('-');
                    int.TryParse(dash > 5 ? boxId.Substring(5, dash - 5) : boxId.Substring(5), out paneIndex);
                }

                if (world.Side == EnumAppSide.Server) TogglePane(paneIndex, byPlayer);
                return true;
            }

            // 3. Something already stored under the cursor
            string occupiedSlotId = GetSelectedNonEmptySlotId(blockSel);
            if (occupiedSlotId != null)
            {
                ItemSlot storedSlot = inv[occupiedSlotId];

                // Cooking pots, crocks, bowls — the thing vanilla psurface cannot do
                var contained = storedSlot.Itemstack?.Collectible?.GetCollectibleInterface<IContainedInteractable>();
                if (contained != null && contained.OnContainedInteractStart(this, storedSlot, byPlayer, blockSel))
                {
                    OnContentsChanged();
                    return true;
                }

                // WRENCH NO LONGER ROTATES A STORED ITEM. Removed 2026-07-30: sprint plus
                // the mouse wheel does the same job better — any angle, both directions, no
                // swapping to a tool first — so two ways of doing one thing was all the
                // wrench was still adding here.
                //
                // Deliberately NOT special-cased any more either. Briefly it consumed the
                // interaction, which stopped the wrench being placed on a sill like any
                // other item — and with rotation gone there is no reason for it to behave
                // differently from anything else in your hand. Its remaining job is
                // ctrl+wrench on a FRAME to swap the window, which is handled well before
                // this point.
            }

            if (held == null || held.Empty) return TryTake(byPlayer, blockSel);
            return TryPut(byPlayer, blockSel);
        }

        protected bool TryPut(IPlayer byPlayer, BlockSelection blockSel)
        {
            BlockBehaviorDisplaySurfaces bh = SurfaceBehavior;
            if (bh == null) return false;

            // A placed-item box is a valid aim point now: its decoded X/Y/Z is that item's
            // own position, so the collision test below finds it and TryStack adds on top.
            // Rejecting it here was the other half of "you must aim at the bottom one".
            DisplaySlotId loc = DisplaySlotId.Decode(blockSel?.SelectionBoxId);
            if (loc == null) return false;

            DisplaySurface surface = bh.GetSurface(loc.SurfaceIndex);
            if (surface == null) return false;
            if (IsSurfaceStacked(loc.SurfaceIndex)) return false;

            ItemSlot held = byPlayer.InventoryManager.ActiveHotbarSlot;
            string category = surface.DisplayCategory ?? "shelf";
            DisplayableAttributes dattr = BlockBehaviorDisplaySurfaces.GetDisplayableAttributes(held, category);
            if (dattr == null) return false;

            Size3i surfaceSize = surface.Size;

            // The player may have turned the item before placing it, which swaps width and
            // length on a quarter turn — that is the whole point, letting a long item be
            // fitted across a sill it will not fit along. Every check below therefore works
            // on the TURNED footprint, and the same angle is stored on the slot afterwards
            // so the model, the box and the collision all agree from the first frame.
            int placeRot = PlacementRotation.For(Api, byPlayer);

            // AIMING AT AN ITEM ALREADY THERE MEANS STACKING ONTO IT, so inherit that item's
            // angle — and inherit it HERE, before the fit checks below, not later in
            // TryStack.
            //
            // That ordering is the whole bug: the bounds check runs on the held item's
            // footprint, and a base item turned 90° sits at a depth that only its turned
            // footprint fits. An unrotated item aimed at it therefore failed "out of bounds"
            // and never reached the stacking path at all — reported as "no room" and as the
            // stack not detecting the item below. Deciding the angle first makes every check
            // below measure the shape the item will actually be placed as.
            if (loc.IsPlacedItem && inv.SlotsByslotId.TryGetValue(loc.Encoded, out ItemSlot baseSlot)
                && !baseSlot.Empty)
            {
                DisplayableAttributes baseAttr = BlockBehaviorDisplaySurfaces
                    .GetDisplayableAttributes(baseSlot, category);
                // The CHOSEN angle, not StoredRotationDeg — that one adds jitter, and this
                // value is about to be stored as the new item's chosen angle, which would
                // then have jitter added again on every read. Each item keeps its own nudge.
                if (baseAttr != null) placeRot = (int)Math.Round(ChosenRotationDeg(loc.Encoded));
            }

            Size3f itemSize = BlockBehaviorDisplaySurfaces.RotatedFootprint(
                dattr.Size, placeRot + SurfaceFacingDeg(surface));

            if (surfaceSize.Width < itemSize.Width || surfaceSize.Height < itemSize.Height || surfaceSize.Length < itemSize.Length)
            {
                (Api as ICoreClientAPI)?.TriggerIngameError(this, "toolarge", Lang.Get("shelfhelp-toolarge-error"));
                return true;
            }

            Vec3f offset = BlockBehaviorDisplaySurfaces.GetCentreOffset(itemSize);
            if (loc.X + offset.X < 0f || loc.Z + offset.Z < 0f
                || loc.X + offset.X > surfaceSize.Width - itemSize.Width
                || loc.Z + offset.Z > surfaceSize.Length - itemSize.Length)
            {
                (Api as ICoreClientAPI)?.TriggerIngameError(this, "outofbounds", Lang.Get("shelfhelp-outofbounds-error"));
                return true;
            }

            string collidingSlotId = GetCollidingSlotId(loc, new Cuboidf(itemSize));
            if (collidingSlotId != null)
            {
                if (dattr.Behavior == EnumDisplayableBehavior.Stacking) return TryStack(byPlayer, collidingSlotId, dattr, placeRot);

                // OVERLAP ALLOWED: the collision is DETECTED exactly as before — the stacking
                // branch above depends on it — it simply stops being a refusal. Turning the
                // detection off instead would end stacking altogether, which is the trap this
                // shape avoids.
                //
                // The one case still refused is a collision with THIS VERY SLOT. That is not
                // two items sharing space, it is the same slot id, and placing into it would
                // either merge silently into the stack already there or fail with no message.
                if (!(UniversalDisplayLibConfig.Current?.AllowItemOverlap ?? false)
                    || collidingSlotId == loc.Encoded)
                {
                    (Api as ICoreClientAPI)?.TriggerIngameError(this, "shelffull", Lang.Get("shelfhelp-shelffull-error"));
                    return true;
                }
            }

            return PlaceItem(byPlayer, held, loc.Encoded, placeRot);
        }

        /// <summary>
        /// Adds the held item on top of the stack starting at <paramref name="collidingSlotId"/>.
        ///
        /// <paramref name="rotDeg"/> is carried through so a stacked item keeps the angle the
        /// player chose, exactly as a ground-level one does. Without it, everything added to
        /// a pile landed unrotated no matter what the preview showed — which reads in play as
        /// "rotation does not work when stacking".
        /// </summary>
        private bool TryStack(IPlayer byPlayer, string collidingSlotId, DisplayableAttributes dattr, int rotDeg)
        {
            DisplaySlotId loc = DisplaySlotId.Decode(collidingSlotId);
            DisplaySurface surface = SurfaceBehavior?.GetSurface(loc?.SurfaceIndex ?? -1);
            if (surface == null) return false;

            // rotDeg arrives already resolved by TryPut: the player's pending angle for a
            // fresh pile, or the base item's angle when stacking onto one. Deciding it there
            // rather than here matters, because TryPut's bounds and collision checks have to
            // measure the same footprint the item will end up with.

            DisplaySlotId cursor = loc;
            while (true)
            {
                ItemSlot slot = inv[cursor.Encoded];
                if (slot == null || slot.Empty) break;
                cursor = cursor.UpCopy();
            }

            // Headroom is judged from where the item would actually REST — the summed heights
            // of the pile — not from its own height times the level. Same rule as before
            // otherwise: the base must still be inside the surface, so the topmost item may
            // overhang it. Tightening that to "the whole item must fit" would retroactively
            // make existing piles illegal.
            if (StackBaseY(cursor, surface, dattr) >= surface.Size.Height) return false;

            return PlaceItem(byPlayer, byPlayer.InventoryManager.ActiveHotbarSlot, cursor.Encoded, rotDeg);
        }

        private bool PlaceItem(IPlayer byPlayer, ItemSlot heldSlot, string targetSlotId, int rotDeg = 0)
        {
            inv.Allocate(targetSlotId);
            ItemSlot target = inv[targetSlotId];
            int moved = heldSlot.TryPutInto(Api.World, target, 1);

            if (moved <= 0) return false;

            // Record the angle it was placed at, so the mesh, the selection box and the
            // collision all use it immediately. Only when the player actually turned it —
            // storing 0 would suppress the placement jitter and make a shelf of jars look
            // machine-stamped.
            //
            // The CHOSEN angle only. Jitter is added by StoredRotationDeg on every read, so
            // it stays governed by the config/block/item switches rather than being frozen in
            // here. A turned item still gets the same natural variation an untouched one does.
            if (rotDeg != 0)
            {
                customRotationDegBySlot ??= new Dictionary<string, float>();
                customRotationDegBySlot[targetSlotId] = rotDeg;
            }

            // The height nudge, same rule and same reason — only stored when the player
            // actually moved it, so an untouched item keeps no entry at all.
            float placeHeight = PlacementHeight.For(Api, byPlayer);
            if (placeHeight != 0f)
            {
                customHeightBySlot ??= new Dictionary<string, float>();
                customHeightBySlot[targetSlotId] = placeHeight;
            }

            // If this is the first chiselled block on a lockable surface, it becomes that
            // surface's permanent design. Here rather than in TryPut/TryStack because both
            // funnel through this method, so "first" is decided in exactly one place.
            ClaimLockIfFirst(targetSlotId, target);

            // One turn, one placement. Stacking inherits from the item below, so a pile is
            // still uniform without pressing the key again.
            PlacementRotation.ResetAfterPlacing(Api, byPlayer);

            // The nudge is NOT inherited when stacking, unlike the angle — see
            // PlacementHeight.ResetAfterPlacing for why.
            PlacementHeight.ResetAfterPlacing(Api, byPlayer);

            OnContentsChanged();

            Api.World.PlaySoundAt(target.Itemstack?.Block?.Sounds?.Place ?? GlobalConstants.DefaultBuildSound, byPlayer.Entity, byPlayer);
            Api.World.Logger.Audit("{0} put 1x{1} into window storage at {2}, slot {3}.",
                byPlayer.PlayerName, target.Itemstack?.Collectible.Code, Pos, targetSlotId);
            return true;
        }

        protected bool TryTake(IPlayer byPlayer, BlockSelection blockSel)
        {
            string slotId = GetSelectedNonEmptySlotId(blockSel);
            if (slotId == null) return false;
            if (IsSlotSurfaceStacked(slotId)) return false;

            // A locked chiselled design is not takeable by hand — see IsSlotLocked. Break the
            // block and it drops with everything else, which is exactly what the report asked
            // for ("only retrievable by breaking the block").
            //
            // TRUE, not false, and for the same reason the claims guard returns true: false
            // falls through to base. The hand is empty on this path so there is nothing to
            // place, but the two refusals should not differ on a detail that subtle.
            if (IsSlotLocked(slotId))
            {
                (Api as ICoreClientAPI)?.TriggerIngameError(this, "displaylocked",
                    Lang.Get("universaldisplaylib:item-locked"));
                return true;
            }

            ItemSlot slot = inv[slotId];
            if (slot == null || slot.Empty) return false;

            ItemStack taken = slot.TakeOut(1);
            if (byPlayer.InventoryManager.TryGiveItemstack(taken))
            {
                Api.World.PlaySoundAt(taken?.Block?.Sounds?.Place ?? GlobalConstants.DefaultBuildSound, byPlayer.Entity, byPlayer);
            }
            else if (taken != null && taken.StackSize > 0)
            {
                Api.World.SpawnItemEntity(taken, Pos);
            }

            // Drop the taken item's angle FIRST. Doing it after the collapse below deleted
            // the angle of whatever had just moved down into this slot instead. The height
            // nudge is keyed the same way and goes with it.
            customRotationDegBySlot?.Remove(slotId);
            customHeightBySlot?.Remove(slotId);

            // Collapse anything stacked above into the gap.
            //
            // The angle has to travel with the item. customRotationDegBySlot is keyed by SLOT,
            // so moving only the Itemstack left the angle behind on the old key and the item
            // fell back to its placement jitter — reported as "take the bottom one and the
            // next one up reverts to its original position".
            DisplaySlotId gap = DisplaySlotId.Decode(slotId);
            DisplaySlotId above = gap.UpCopy();
            while (true)
            {
                ItemSlot aboveSlot = inv[above.Encoded];
                if (aboveSlot == null || aboveSlot.Empty) break;

                inv[gap.Encoded].Itemstack = aboveSlot.Itemstack;
                aboveSlot.Itemstack = null;

                if (customRotationDegBySlot != null)
                {
                    customRotationDegBySlot.Remove(gap.Encoded);
                    if (customRotationDegBySlot.TryGetValue(above.Encoded, out float carried))
                    {
                        customRotationDegBySlot[gap.Encoded] = carried;
                        customRotationDegBySlot.Remove(above.Encoded);
                    }
                }

                // THE NUDGE IS DROPPED, NOT CARRIED — the one place height deliberately
                // parts company with rotation, on the author's call 2026-08-05 after seeing
                // it in game.
                //
                // An angle is absolute: an item turned 90° is still turned 90° wherever it
                // ends up. A nudge is RELATIVE to whatever it was resting on — you push a
                // plant down to settle it into a pot — so once that pot is taken the number
                // is measured against something that is no longer there, and carrying it
                // sinks the plant into the sill by however far it was pushed.
                //
                // Both keys go: the one being vacated, and the one the item is arriving
                // from. It lands at its natural height, which is what it would have had if
                // placed there directly.
                customHeightBySlot?.Remove(gap.Encoded);
                customHeightBySlot?.Remove(above.Encoded);

                // THE LOCK TRAVELS, like the angle. A locked design can sit above a takeable
                // item — take that item and the design slides down into the gap, so a claim
                // left on the old slot id would point at a slot the design has left and the
                // design would come loose.
                if (lockedSlotBySurface != null
                    && gap.SurfaceIndex == above.SurfaceIndex
                    && lockedSlotBySurface.TryGetValue(above.SurfaceIndex, out string claimed)
                    && claimed == above.Encoded)
                {
                    lockedSlotBySurface[above.SurfaceIndex] = gap.Encoded;
                }

                gap = gap.UpCopy();
                above = above.UpCopy();
            }

            OnContentsChanged();
            return true;
        }

        /// <summary>
        /// The random resting angle an item is given on placement, from its
        /// <c>RandYRotAngle</c> (30 by default, so ±15°). Deterministic from the position
        /// and slot, so it never needs storing and survives a reload unchanged.
        /// </summary>
        private float PlacementJitterDeg(string slotId, DisplayableAttributes dattr)
        {
            // Three levels, coarsest first, all meeting at this one choke point:
            //   config PlacementJitter=false  -> the whole mod sits square
            //   block  noPlacementJitter      -> that block sits square (the chiselled window)
            //   item   randYRotAngle: 0       -> that item sits square, set in the patches mod
            if (JitterSuppressed) return 0f;
            if (dattr == null || dattr.RandYRotAngle <= 0) return 0f;

            return GameMath.MurmurHash3Mod(Pos.X, Pos.Y + slotId.GetHashCode(), Pos.Z,
                       dattr.RandYRotAngle + 1) - dattr.RandYRotAngle / 2;
        }

        /// <summary>Same value, resolved from the slot when the caller has no dattr to hand.</summary>
        private float PlacementJitterDeg(string slotId)
        {
            if (!inv.SlotsByslotId.TryGetValue(slotId, out ItemSlot slot) || slot.Empty) return 0f;

            DisplaySlotId loc = DisplaySlotId.Decode(slotId);
            DisplaySurface surface = SurfaceBehavior?.GetSurface(loc?.SurfaceIndex ?? -1);
            if (surface == null) return 0f;

            return PlacementJitterDeg(slotId, BlockBehaviorDisplaySurfaces
                .GetDisplayableAttributes(slot, surface.DisplayCategory ?? "shelf"));
        }

        /// <summary>
        /// The angle an item in this slot actually sits at, in the surface's own frame:
        /// its wrenched angle if it has one, otherwise its placement jitter, plus the
        /// surface's facing. Exactly the terms <see cref="BuildSlotMatrix"/> feeds the mesh,
        /// which is the point — the footprint has to be derived from the same number the
        /// model is, or the two disagree.
        ///
        /// The surface facing is included for correctness rather than for present need: no
        /// shipped surface faces 90° or 270°, so today it never changes a footprint. One that
        /// did would turn every item on it a quarter turn, and the boxes must follow.
        /// </summary>
        /// <summary>
        /// The angle STORED against a slot — its wrenched value if it has one, otherwise its
        /// placement jitter. Deliberately excludes the surface facing, because that is added
        /// separately by everything that renders or measures; this is the number that would
        /// be written into <c>customRotationDegBySlot</c>, so it is what a new item copying
        /// an existing one should take.
        /// </summary>
        /// <summary>
        /// The angle explicitly chosen for a slot — placed-at or wrenched-to — with no jitter.
        /// This is what is stored, and what one item should copy from another.
        /// </summary>
        private float ChosenRotationDeg(string slotId)
        {
            return customRotationDegBySlot != null && customRotationDegBySlot.TryGetValue(slotId, out float custom)
                ? custom : 0f;
        }

        private float StoredRotationDeg(string slotId, DisplayableAttributes dattr)
        {
            // Jitter is ADDED HERE, at read time, never baked into the stored value.
            //
            // It used to be folded in when the item was placed, which froze it: an item put
            // down at an angle kept its random nudge for good, so switching the jitter config
            // off left every rotated and wrenched item exactly as it was while untouched ones
            // went square. Reported as "the ones placed at the angles aren't covered by the
            // config", and it was right.
            //
            // Deriving it on every read means the config, the block flag and the item's own
            // randYRotAngle all apply RETROACTIVELY to everything already placed — turn
            // jitter off and the whole world lines up, turn it back on and it returns.
            //
            // The stored value is therefore the CHOSEN angle alone: 0 for an untouched item,
            // 90 for one placed turned, whatever the wrench has stepped to. That also makes
            // wrench steps exact multiples rather than multiples plus a frozen offset.
            return ChosenRotationDeg(slotId) + PlacementJitterDeg(slotId, dattr);
        }

        private float ItemRotationDeg(string slotId, DisplayableAttributes dattr, DisplaySurface surface)
        {
            return StoredRotationDeg(slotId, dattr) + SurfaceFacingDeg(surface);
        }

        /// <summary>
        /// Whether the item in this slot would still fit if turned to <paramref name="rotDeg"/>.
        /// Checks the same two things placement does — inside the surface, and clear of the
        /// other items — against the ROTATED footprint.
        ///
        /// Its own cuboid is in that list, so a self-hit is ignored; anything else is a real
        /// overlap. Returns true when the item, surface or size cannot be resolved: refusing
        /// to rotate because something failed to resolve would be a worse failure than
        /// allowing it.
        /// </summary>
        private bool RotationFits(string slotId, float rotDeg)
        {
            if (!inv.SlotsByslotId.TryGetValue(slotId, out ItemSlot slot) || slot.Empty) return true;

            DisplaySlotId loc = DisplaySlotId.Decode(slotId);
            DisplaySurface surface = SurfaceBehavior?.GetSurface(loc?.SurfaceIndex ?? -1);
            if (loc == null || surface == null) return true;

            DisplayableAttributes dattr = BlockBehaviorDisplaySurfaces
                .GetDisplayableAttributes(slot, surface.DisplayCategory ?? "shelf");
            if (dattr?.Size == null) return true;

            Size3f size = BlockBehaviorDisplaySurfaces.RotatedFootprint(
                dattr.Size, rotDeg + SurfaceFacingDeg(surface));

            if (surface.Size.Width < size.Width || surface.Size.Length < size.Length) return false;

            Vec3f offset = BlockBehaviorDisplaySurfaces.GetCentreOffset(size);
            if (loc.X + offset.X < 0f || loc.Z + offset.Z < 0f
                || loc.X + offset.X > surface.Size.Width - size.Width
                || loc.Z + offset.Z > surface.Size.Length - size.Length)
            {
                return false;
            }

            // ONE LEVEL TALL, not size.Height. ContentCuboids measures Y in stack levels —
            // loc.Y to loc.Y + 1 — while Size.Height is in voxels. Passing the voxel height
            // made an item at level 1 span levels 1..1+height and "collide" with everything
            // stacked above it, so only the bottom of a stack could ever be turned. Rotation
            // only ever competes for space on its OWN level.
            // Overlap applies to turning too, or an item could be PLACED overlapping its
            // neighbour and then refused permission to turn in the space it already occupies.
            string hit = GetCollidingSlotId(loc, new Cuboidf(0f, 0f, 0f, size.Width, 1f, size.Length));
            return hit == null
                   || hit == slotId
                   || (UniversalDisplayLibConfig.Current?.AllowItemOverlap ?? false);
        }

        /// <summary>
        /// Entry point for the mouse-wheel path, which arrives as a packet rather than
        /// through OnBlockInteractStart. Same code as the wrench so the two cannot behave
        /// differently — including refusing when the turn would not fit.
        /// </summary>
        public bool RotateStoredItem(string slotId, IPlayer byPlayer, bool reverse)
        {
            if (slotId == null || inv[slotId] is not { Empty: false }) return false;

            // A locked design does not turn. "Once placed is placed" has to cover this and
            // the nudge as well as taking, or a chiselled insert can still be spun in its
            // frame by anyone — on a window that is arguably worse than removing it, because
            // the owner may not notice.
            //
            // The refusal is enforced HERE, server-side, because that is where the packet
            // lands and the client cannot be trusted with it. The player is told on the
            // CLIENT, in UniversalDisplayLibMod.TryWheelRotate, which is the only side that
            // can raise an ingame error at all.
            if (IsSlotLocked(slotId)) return false;

            return TryRotateItem(slotId, byPlayer, reverse);
        }

        /// <summary>
        /// Raise or lower an item already on a surface, from the sneak+wheel packet.
        ///
        /// NO FIT CHECK, unlike rotating. A turn changes the space an item needs, so it can
        /// stop fitting; a vertical nudge cannot — the footprint is unchanged and stack
        /// levels are counted, not measured, so nothing it can do makes an occupied slot
        /// collide with another. The configured limit is the only guard it needs.
        /// </summary>
        public bool AdjustStoredItemHeight(string slotId, bool down)
        {
            if (slotId == null) return false;
            if (!inv.SlotsByslotId.TryGetValue(slotId, out ItemSlot slot) || slot == null || slot.Empty)
            {
                return false;
            }

            // The nudge half of the lock, server-side for the reason RotateStoredItem gives.
            if (IsSlotLocked(slotId)) return false;

            customHeightBySlot ??= new Dictionary<string, float>();
            customHeightBySlot.TryGetValue(slotId, out float current);

            float wanted = PlacementHeight.Clamp(
                current + (down ? -PlacementHeight.Step : PlacementHeight.Step));

            // An entry of exactly 0 is the same as no entry, so drop it rather than saving a
            // key that says "unchanged" — nudging an item back to where it started should
            // leave the block as it was before anyone touched it.
            if (wanted == 0f) customHeightBySlot.Remove(slotId);
            else customHeightBySlot[slotId] = wanted;

            // Boxes with the meshes, for the reason spelled out in TryRotateItem: they are
            // derived from item state, so a stale set outlives the change and the item
            // becomes unclickable where it now is.
            InvalidateBoxes();
            MarkMeshesDirty();
            MarkDirty(true);
            return true;
        }

        private bool TryRotateItem(string slotId, IPlayer byPlayer, bool reverse)
        {
            customRotationDegBySlot ??= new Dictionary<string, float>();

            float step = UniversalDisplayLibConfig.Current?.RotationStepDegrees ?? 22.5f;

            // Seed from the CHOSEN angle, which is 0 for an item that has never been turned.
            // The jitter no longer needs seeding in: StoredRotationDeg adds it on every read,
            // so the rendered angle is chosen + jitter and stepping the chosen part by 15
            // moves what you see by exactly 15. That is what the old jitter-seeding was
            // working around, less directly.
            customRotationDegBySlot.TryGetValue(slotId, out float current);

            float wanted = (current + (reverse ? -step : step) + 360f) % 360f;

            // Turning an item can change the space it needs — a quarter turn swaps width and
            // length — so it may no longer fit where it sits. Check before committing, or a
            // rotated item silently overlaps its neighbour or hangs off the sill.
            if (!RotationFits(slotId, wanted))
            {
                (Api as ICoreClientAPI)?.TriggerIngameError(this, "norotateroom",
                    Lang.Get("universaldisplaylib:rotate-noroom"));
                return true;      // handled: refusing is a result, not a fall-through
            }

            customRotationDegBySlot[slotId] = wanted;

            // The selection boxes are derived from item state, so they have to go with the
            // meshes. Without this the client kept a stale set from the moment of rotating
            // until the server's packet arrived and FromTreeAttributes invalidated for it —
            // which is why rotating made boxes disappear and re-opening a pane, which does
            // invalidate, brought them all back.
            InvalidateBoxes();
            MarkMeshesDirty();
            MarkDirty(true);

            // NO RELIGHT HERE — an `ApplyLight()` call was tried on 2026-07-31 and REVERTED.
            //
            // The theory was decent: a candle does not flash when a window is opened or
            // closed, but does when the candle itself is rotated, and the only difference
            // between those two paths is that the toggle relights via SyncStateVariant. So
            // the relight looked like the thing CLEANING UP after the re-tesselation rather
            // than the thing causing the flash.
            //
            // It made no difference at all in game. The likelier explanation for the
            // toggle/rotate difference is far duller: **opening a window moves a large
            // animated pane and plays a sound, which masks a subtle flicker; rotating an item
            // changes almost nothing else on screen, so the flicker is the only thing moving
            // and the eye goes straight to it.**
            //
            // Do not reinstate this. It relights on every wheel notch, which is real work for
            // no observed benefit.
            return true;
        }

        // ── Panes ────────────────────────────────────────────────────────────

        /// <summary>
        /// Sets one pane without touching the rest of the stack. TogglePane drives the
        /// linked segments through this, so the sync cannot recurse back into itself.
        /// </summary>
        public void SetPaneState(int index, bool open)
        {
            if (paneStates == null || index < 0 || index >= paneStates.Length) return;
            if (paneStates[index] == open) return;

            paneStates[index] = open;
            InvalidateBoxes();
            MarkMeshesDirty();
            MarkDirty(true);

            bool roomSafe = UniversalDisplayLibConfig.Current?.RoomSafeOpening ?? false;
            FrameBoxGroup[] groups = SurfaceBehavior?.FrameBoxGroups;
            if (groups != null && index < groups.Length && groups[index].SealsRoom && !roomSafe)
            {
                SyncStateVariant();
            }
        }

        /// <summary>
        /// Client-side application of a linked pane change: sets the state and starts the
        /// animation immediately, rather than waiting for this segment's own block entity
        /// sync. That is what keeps a tall window's segments moving together.
        /// </summary>
        public void ApplyLinkedPane(int index, bool open)
        {
            if (paneStates == null || index < 0 || index >= paneStates.Length) return;

            paneStates[index] = open;
            InvalidateBoxes();
            UpdateAnimationState();
            soundHandler?.PlaySlideSound();
        }

        /// <summary>Server-side: tells every nearby client to move this pane on the whole stack at once.</summary>
        private void BroadcastLinkedPane(int index, bool open)
        {
            if (Api?.Side != EnumAppSide.Server || UniversalDisplayLibMod.ServerChannel == null) return;

            List<BlockPos> chain = DisplayChain.Enumerate(Api.World, Pos);
            var flat = new int[chain.Count * 3];
            for (int i = 0; i < chain.Count; i++)
            {
                flat[i * 3] = chain[i].X;
                flat[i * 3 + 1] = chain[i].Y;
                flat[i * 3 + 2] = chain[i].Z;
            }

            UniversalDisplayLibMod.ServerChannel.BroadcastPacket(new LinkedOpenPacket
            {
                Positions = flat,
                PaneIndex = index,
                IsOpen = open
            });
        }

        public void TogglePane(int index, IPlayer byPlayer = null)
        {
            FrameBoxGroup[] groups = SurfaceBehavior?.FrameBoxGroups;
            if (groups == null || index < 0 || index >= groups.Length) return;

            FrameBoxGroup group = groups[index];
            // A group with only static boxes has nothing to open
            if (group.OpenFrameBox == null && group.ClosedFrameBox == null) return;

            if (index >= paneStates.Length) ClampPaneStatesToBlock();
            if (index >= paneStates.Length) return;

            SetPaneState(index, !paneStates[index]);

            // Tall windows open as one unit
            DisplayChain.SyncPane(Api.World, Pos, index, paneStates[index]);
            BroadcastLinkedPane(index, paneStates[index]);

            // The open/closed variant sync lives in SetPaneState, so every path that
            // changes a pane — including the linked segments above — keeps it in step
            if (!group.SealsRoom)
            {
                Api.World.PlaySoundAt(DisplaySoundHandler.SoundFor(Block, "toggleSolid", "game:sounds/block/door"),
                    Pos, 0, byPlayer, true, 16f);
            }
        }

        private void SyncStateVariant()
        {
            if (Api?.Side != EnumAppSide.Server || Block.Variant["state"] == null) return;

            string targetState = IsAnySealingPaneOpen ? "open" : "closed";
            if (Block.Variant["state"] == targetState) return;

            Block targetBlock = Api.World.GetBlock(Block.CodeWithVariant("state", targetState));
            if (targetBlock == null || targetBlock.Id == Block.Id) return;

            ITreeAttribute snapshot = new TreeAttribute();
            ToTreeAttributes(snapshot);

            // `(synchronize, relight, strict)` — synchronize FALSE, relight TRUE. BOTH of
            // those have been changed and changed back; neither is a free choice.
            //
            // synchronize:true was tried 2026-07-27 and is a straight regression — the same
            // note sits in BlockSwapHelper.
            //
            // **relight:false was tried 2026-07-31 and REVERTED. Do not retry it without
            // reading this.** The reasoning for it was sound and is still true: a relight
            // here cannot change a single light value. `ChunkIlluminator` (engine side,
            // 1.22.5) propagates from `GetLightAbsorptionAt` and never reads `SideSolid` or
            // `SideOpaque` at all — 13 references to absorption, zero to either — and every
            // window blocktype declares `lightAbsorption: 0` with `sideopaque: all false` in
            // BOTH states. What the open/closed variants differ in is `sideSolid`, which
            // drives room sealing and attachment, not light.
            //
            // It was aimed at the open/close light FLASH and did not fix it. The flash turned
            // out not to live here at all: **rotating a stored item does it too**, and
            // `TryRotateItem` touches no light code whatever — the one thing the two share is
            // `MarkDirty(true)`. Worse, the author reported the flash reading *more*
            // noticeable without the relight. That is plausible rather than surprising: with
            // it ON the engine recomputes and pushes correct values straight after the
            // exchange, so a stale client-side value is actively corrected instead of being
            // left to resolve on its own.
            //
            // The observation beats the reasoning, as it has every other time in this project.
            // `(synchronize, relight, strict)` — synchronize FALSE, relight TRUE. Both have
            // been changed and changed back; neither is a free choice.
            //
            // synchronize:true was tried 2026-07-27 and is a straight regression — same note
            // in BlockSwapHelper.
            //
            // **relight:false was tried TWICE on 2026-07-31 and reverted both times. The
            // second attempt was MEASURED. Do not try it a third time:**
            //
            //   | open/close | relight:true | relight:false |
            //   | frames at block-light 0 | 4 and 2 | 4 and 5 |
            //
            // No better opening, two and a half times worse closing. The author had already
            // called that by eye on the first attempt, before any measurement existed.
            //
            // The reasoning FOR removing it was sound and is still true — a relight here
            // cannot change a light VALUE, since ChunkIlluminator propagates from
            // `GetLightAbsorptionAt` and never reads `SideSolid`/`SideOpaque`, and every
            // window blocktype is `lightAbsorption: 0` in both states. It is simply not the
            // whole story: the relight also re-spreads light that `MarkDirty(true)` has
            // already knocked out, so removing it leaves the hole open for longer.
            //
            // "This work cannot change the result" and "removing this work is free" are
            // different claims, and this is what the difference costs.
            IBlockAccessor relightAccessor = Api.World.GetBlockAccessor(false, true, false);
            relightAccessor.ExchangeBlock(targetBlock.Id, Pos);

            if (relightAccessor.GetBlockEntity(Pos) is BEUniversalDisplay newBe && !ReferenceEquals(newBe, this))
            {
                newBe.FromTreeAttributes(snapshot, Api.World);
                newBe.UpdateLightCache();
                newBe.MarkDirty(true);
            }
        }

        // ── Animation ────────────────────────────────────────────────────────

        /// <summary>
        /// Rebuilds the animator when it no longer matches the block or the mesh
        /// angle it was built for. Returns true if it re-initialised.
        ///
        /// Change-detection on FromTreeAttributes is not enough: the client runs
        /// TryPlaceBlock itself and sets MeshAngleRad locally, so by the time the
        /// server's value arrives there is no change left to notice — and the
        /// renderer keeps the rotation of 0 it was constructed with. ExchangeBlock
        /// has the mirror problem, keeping the block entity across a block change.
        /// </summary>
        /// <summary>
        /// Turns <c>animatedElements</c> into the path form SelectiveElements needs.
        ///
        /// The two lists use different matching rules, which is easy to get wrong:
        ///
        ///  * IgnoreElements — a bare name drops the element and its whole subtree,
        ///    because the tesselator `continue`s without recursing.
        ///  * SelectiveElements — a bare name matches only that element and hands its
        ///    children an EMPTY haystack, and an empty (non-null) haystack matches
        ///    nothing. So `["Animation Origin"]` keeps just the origin marker, which
        ///    has no faces, and the animated mesh comes out empty.
        ///
        /// `"Name/*"` is the form that keeps the element and everything under it —
        /// the same convention vanilla's cabinet uses for its doorElements.
        /// Content therefore writes plain names and this adds the suffix.
        /// </summary>
        private static string[] ToSelectiveElementPaths(string[] elementNames)
        {
            if (elementNames == null || elementNames.Length == 0) return null;

            var paths = new string[elementNames.Length];
            for (int i = 0; i < elementNames.Length; i++)
            {
                string name = elementNames[i];
                paths[i] = name.EndsWith("/*", StringComparison.Ordinal) || name == "*"
                    ? name
                    : name + "/*";
            }
            return paths;
        }

        /// <summary>
        /// Everything the animator mesh depends on: which shape it draws, and which
        /// ARL variants textured it. Same for the open and closed block variants, so
        /// toggling a pane does not rebuild it.
        ///
        /// The variants matter because the animation shapes declare a default glass
        /// texture (leaded) that ARL overwrites via BakeVariantTextures — but Variants
        /// are filled from the placed stack in OnBlockPlaced, which runs *after*
        /// Initialize. The first build therefore textures with empty variants and
        /// falls back to that default. Every non-north facing happened to hide this,
        /// because the placement angle changed and forced a rebuild; north is angle 0,
        /// matched what was already built, and kept the leaded glass.
        /// </summary>
        private string ResolveAnimatorShapeKey()
        {
            // Keyed on the block's OWN shape. There used to be an `animationShapePath`
            // branch for content keeping its animations in a separate file; every shape in
            // this mod is single-file — static geometry, psurface markers and animations
            // together, split at tesselation time by `animatedElements` — so it was dead.
            // Removed 2026-07-30.
            string variantKey = GetBehavior<BlockEntityBehaviorShapeTexturesFromAttributes>()?.Variants?.ToString() ?? "";
            return "block:" + Block.Shape?.Base + "|" + variantKey;
        }

        /// <summary>
        /// Whether ARL has its wood/glass variants yet.
        ///
        /// Variants arrive in OnBlockPlaced or FromTreeAttributes, both of which run
        /// *after* Initialize. Building the animator or the block mesh before then bakes
        /// the shape's default textures in, and the corrected rebuild a frame or two
        /// later is the visible pop from generic to matching material.
        ///
        /// The attempt budget stops a block whose variants never arrive — ARL present
        /// but placed without attributes — from staying invisible forever.
        /// </summary>
        private bool ArlVariantsReady
        {
            get
            {
                var arlBe = GetBehavior<BlockEntityBehaviorShapeTexturesFromAttributes>();
                if (arlBe == null) return true;
                if (arlBe.Variants?.Any == true) return true;
                return _variantWaitTicks > MaxVariantWaitTicks;
            }
        }

        private const int MaxVariantWaitTicks = 8;   // ~2s at the 250ms client tick
        private int _variantWaitTicks;

        private bool EnsureAnimatorCurrent()
        {
            if (capi == null || Block == null) return false;
            if (!ArlVariantsReady) return false;

            // Keyed on the animation shape, NOT the block id. Toggling a pane runs
            // SyncStateVariant, which exchanges the block for its open/closed variant —
            // a different block id but the same animation shape. Rebuilding on block id
            // therefore tore the animator down mid-animation and restarted it from the
            // default pose, which is the close-bounce-open-close stutter.
            bool shapeChanged = ResolveAnimatorShapeKey() != _animatorShapeKey;
            bool angleChanged = float.IsNaN(_animatorAngleRad) || Math.Abs(_animatorAngleRad - MeshAngleRad) > 0.001f;
            if (!shapeChanged && !angleChanged) return false;

            InitializeAnimator();

            // The pose right after a build is placement or chunk load, not a player
            // opening something — jump straight to it instead of animating from the
            // default pose. Scoped to the first update after a build so ordinary
            // toggles still ease normally.
            _snapNextAnimationUpdate = true;
            return true;
        }

        private bool _snapNextAnimationUpdate;

        private void InitializeAnimator()
        {
            if (capi == null || Block == null) return;

            // Recorded up front so a shape that fails to load does not retry every tick
            _animatorShapeKey = ResolveAnimatorShapeKey();
            _animatorAngleRad = MeshAngleRad;

            animUtil?.Dispose();
            animUtil = new BlockEntityAnimationUtil(capi, this);

            try
            {
                string[] animatedElements = Block.Attributes?["animatedElements"].AsArray<string>(null);

                // The block's OWN shape, always. The `animationShapePath` branch that used
                // to sit here loaded a separate animation file; every shape in this mod is
                // single-file, so it never ran. Removed 2026-07-30.
                AssetLocation loc = Block.Shape?.Base?.Clone().WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json");
                Shape animShape = loc == null ? null : capi.Assets.TryGet(loc)?.ToObject<Shape>();
                string cacheKey = "universaldisplaylib-" + Block.Code;

                if (animShape == null)
                {
                    Api.Logger.Warning("[UniversalDisplayLib] No animation shape for {0}; panes will not animate.", Block.Code);
                    return;
                }

                ITexPositionSource texSource = BuildBlockTextureSource(animShape, Block.Shape?.Base?.ToString());

                var meta = new TesselationMetaData
                {
                    TexSource = texSource,
                    IgnoreElements = SurfaceBehavior?.SurfaceElementNames,
                    SelectiveElements = ToSelectiveElementPaths(animatedElements)
                };

                MeshData animMesh = animUtil.CreateMesh(Block.Code.ToString(), animShape, out Shape resultingShape, texSource, meta);
                animUtil.InitializeAnimator(cacheKey, animMesh, resultingShape,
                    new Vec3f(0f, MeshAngleRad * GameMath.RAD2DEG, 0f));
            }
            catch (Exception e)
            {
                Api.Logger.Error("[UniversalDisplayLib] Error initializing animation for {0}: {1}", Block?.Code, e.Message);
            }
        }

        /// <summary>
        /// Drives one animation per pane group at the configured speed.
        ///
        /// There is deliberately no "instant" mode. An earlier version forced
        /// AnimationSpeed/EaseIn/EaseOut to 100 when re-initialising, which is what
        /// made every pane snap instead of slide — the animation itself was running
        /// at 100x, not just the correction.
        /// </summary>
        private void UpdateAnimationState()
        {
            if (animUtil == null) return;

            FrameBoxGroup[] groups = SurfaceBehavior?.FrameBoxGroups;
            if (groups == null) return;

            bool snap = _snapNextAnimationUpdate;
            _snapNextAnimationUpdate = false;

            // Ease speeds are deliberately near-instant, and the motion comes from
            // AnimationSpeed alone.
            //
            // These panes are authored as complete transitions — "closed" frame 0 is the
            // open pose — so the keyframes already describe the whole movement. Blending
            // them in gradually on top of BlendMode.Add double-counts: a window held open
            // contributes its -15 while the incoming close animation contributes -15 from
            // its own frame 0, swinging to -30 before settling. That is the bounce.
            // Easing out faster instead leaves a gap where the pose collapses to default,
            // which is the snap. Full weight on both sides avoids both.
            float speed = snap ? 1000f : (UniversalDisplayLibConfig.Current?.AnimationSpeedValue ?? 1f);

            // EaseInSpeed is NOT in per-second terms. Vanilla scales dt by the animation
            // speed BEFORE applying it (RunningAnimation.Progress: `dt *=
            // GetCurrentAnimationSpeed(...)`, then
            // `EasingFactor += (1 - EasingFactor) * |dt| * EaseInSpeed`), so a flat 100
            // eases in four times slower at the config's slowest setting than at normal,
            // and sixteen times slower than at its fastest.
            //
            // THAT IS THE CLOSE STUTTER, measured 2026-08-02. While the incoming animation
            // climbs to full weight the outgoing one is already gone, so for those frames
            // the pane renders the DEFAULT pose — which is the closed pose. Invisible when
            // opening, because default is where an opening window starts; a snap shut and
            // back when closing. It also worsens with framerate: a smaller dt means more
            // frames spent below full weight.
            //
            // Dividing by the speed cancels vanilla's multiplication, so the incoming
            // animation reaches full weight on its FIRST frame at every setting — which is
            // what the fastest setting already did, and the author confirmed there is no
            // stutter there. Nothing needs easing into anyway: each animation's frame 0 is
            // the pose the pane is already in.
            //
            // EASE OUT IS THE SAME BUG AND MUST BE FIXED WITH IT. The shapes declare
            // `onActivityStopped: EaseOut`, so a stopped animation does not vanish — it
            // fades over frames, through the same speed-scaled dt. Its held open pose is
            // then ADDED to the incoming close animation's frame 0, which is ALSO the open
            // pose, so the pane swings to roughly twice the opening for a few frames. That
            // is the -30 double-count the older comments describe, and with the ease-in
            // fixed it was what remained: no longer a snap to closed, but a wobble.
            //
            // THIS IS WHY THREE EARLIER ONE-SIDED ATTEMPTS FAILED, and why an earlier note
            // here saying "ease out stays flat, do not make them symmetric" was wrong.
            // Fixing either side alone trades one artifact for the other: a fast ease-out
            // with a slow ease-in leaves the gap that collapses to the default pose (the
            // snap), and a fast ease-in with a slow ease-out leaves the overlap that
            // double-counts (the wobble). Only both together give a clean handover — the
            // outgoing pose is gone on the same frame the incoming reaches full weight,
            // and the two poses are identical at that instant, so nothing moves.
            // BOTH sides, and they only work as a pair — see below.
            float easeIn = 10000f / speed;
            float easeOut = 10000f / speed;

            for (int i = 0; i < groups.Length && i < paneStates.Length; i++)
            {
                FrameBoxGroup group = groups[i];
                if (string.IsNullOrEmpty(group.AnimOpen) || string.IsNullOrEmpty(group.AnimClose)) continue;

                string target = paneStates[i] ? group.AnimOpen : group.AnimClose;
                string opposite = paneStates[i] ? group.AnimClose : group.AnimOpen;

                if (animUtil.activeAnimationsByAnimCode.ContainsKey(opposite)) animUtil.StopAnimation(opposite);

                if (!animUtil.activeAnimationsByAnimCode.ContainsKey(target))
                {
                    animUtil.StartAnimation(new AnimationMetaData
                    {
                        Animation = target,
                        Code = target,
                        AnimationSpeed = speed,
                        EaseInSpeed = easeIn,
                        EaseOutSpeed = easeOut,
                        Weight = 1f,
                        BlendMode = EnumAnimationBlendMode.Add
                    });
                }
            }
        }

        // ── Meshing ──────────────────────────────────────────────────────────

        public void MarkMeshesDirty()
        {
            meshesGenerated = false;
            if (Api?.Side == EnumAppSide.Client) Api.World.BlockAccessor.MarkBlockDirty(Pos);
        }

        public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
        {
            if (capi == null) return false;

            // Resolve BEFORE generating. GenerateMeshes reads slot.Itemstack.Collectible
            // for every slot; on an unresolved stack that is null, GetDisplayableAttributes
            // returns null, the slot is skipped, and meshesGenerated is still set to true —
            // so the item stays invisible until something else happens to invalidate.
            // That is the "disappears on open/close, sometimes comes back" behaviour.
            inv.ResolveBlocksOrItems();

            // ExchangeBlock (open/closed variant sync, and wrenchSwapTo) swaps the block
            // out from under a surviving block entity without any callback, so compare
            // against what the meshes were actually built for.
            if (_meshBlockId != Block.Id)
            {
                _meshBlockId = Block.Id;
                meshesGenerated = false;
                InvalidateBoxes();
            }

            if (!meshesGenerated) GenerateMeshes();

            // SNAPSHOT, because this runs on the TESSELATION THREAD. Enumerating the live
            // collection threw "Collection was modified" whenever the main thread touched the
            // inventory during a pass, and the block then rendered wrong until something
            // invalidated it again — reported as the shape briefly turning.
            //
            // Every other off-main-thread reader here already snapshots; this was the one that
            // did not. `SlotsSnapshot` also swallows a throw from ToArray itself, which is the
            // same race one level down.
            foreach (var kv in SlotsSnapshot())
            {
                ItemSlot slot = kv.Value;
                if (slot.Empty || slot.Itemstack?.Collectible?.Code == null) continue;

                // Recompute rather than skip when the transform is missing: a slot
                // dropped during generation would otherwise stay invisible until the
                // next invalidation, which is what happened on the first interaction
                // after a world load.
                if (!TfMatrices.TryGetValue(kv.Key, out float[] matrix))
                {
                    matrix = BuildSlotMatrix(kv.Key, slot, out _);
                    if (matrix == null) continue;
                    TfMatrices[kv.Key] = matrix;
                }

                MeshData mesh = GetCachedItemMesh(slot);

                // The cache key includes the stack's contents for anything
                // implementing IContainedMeshSource, so emptying a bowl changes the
                // key. Build on demand rather than silently drawing nothing — that
                // is what left items invisible but still selectable.
                if (mesh == null)
                {
                    DisplayableAttributes dattr = BlockBehaviorDisplaySurfaces
                        .GetDisplayableAttributes(slot, DisplayCategoryFor(kv.Key));
                    if (dattr != null) mesh = GetOrCreateItemMesh(slot, dattr);
                }

                if (mesh != null) mesher.AddMeshData(mesh, matrix);
            }

            // blockMesh excludes the animated elements by construction (see
            // BuildBlockMesh), so it can be added unconditionally. Gating this on
            // "is an animation currently playing" is racy: OnTesselation runs on
            // the tesselation thread and can fire before the animator exists,
            // baking a static copy into the chunk that then never moves — the
            // classic leftover-ghost when static and animated geometry share a
            // shape file.
            if (blockMesh != null)
            {
                float[] blockMatrix = new Matrixf()
                    .Translate(0.5f, 0f, 0.5f)
                    .RotateY(MeshAngleRad)
                    .Translate(-0.5f, 0f, -0.5f)
                    .Values;
                mesher.AddMeshData(blockMesh, blockMatrix);
            }

            return true;
        }

        /// <summary>
        /// Transform for one stored item, or null if the slot cannot be placed yet.
        ///
        /// Split out of GenerateMeshes so OnTesselation can recompute a missing entry
        /// on the spot. Any transient failure during generation — an unresolved stack,
        /// a surface not parsed yet, a throw out of the block mesh build — used to
        /// leave TfMatrices short an entry, and the render loop skipped that item
        /// outright until something else invalidated the meshes. That is the item
        /// vanishing on the first interaction after a world load.
        /// </summary>
        /// <summary>
        /// The mesh and transform the placement ghost should draw, or false if there is
        /// nothing to draw. Thin wrapper on purpose: the ghost must go through the SAME
        /// mesh builder and the SAME matrix as the real placement, or it will show the
        /// player something they are not going to get.
        ///
        /// <paramref name="rotDeg"/> is the player's PENDING angle rather than the stored
        /// one — nothing is stored yet — which is the only reason BuildSlotMatrix takes an
        /// override at all.
        /// </summary>
        /// <summary>
        /// Where an item aimed at an OCCUPIED slot would actually land if stacked, the angle it
        /// would inherit, and whether it would be allowed at all.
        ///
        /// **Mirrors TryPut and TryStack deliberately**, and lives here rather than in the
        /// behaviour because the inventory and the stored angles do. The ghost showing one
        /// thing while the placement does another is the failure this is written to avoid —
        /// a preview that lies is worse than no preview.
        ///
        /// The angle is the BASE item's chosen angle, not the player's pending one: stacking
        /// inherits it, and TryPut resolves it before its own fit checks for that reason.
        /// `ChosenRotationDeg` rather than the stored value, so the base item's random jitter
        /// is not compounded onto the copy above it.
        /// </summary>
        public bool TryResolveStackTarget(DisplaySlotId aimed, DisplayableAttributes heldAttr,
                                          out DisplaySlotId target, out float rotDeg, out bool allowed)
        {
            target = aimed; rotDeg = 0f; allowed = false;

            DisplaySurface surface = SurfaceBehavior?.GetSurface(aimed?.SurfaceIndex ?? -1);
            if (surface == null || heldAttr?.Size == null) return false;

            // TryGetValue, NOT inv[...]. `InventoryInfinite` CREATES a slot on demand, so the
            // indexer mutates `SlotsByslotId` — and this runs every frame from the ghost and
            // the outline, while the TESSELATION THREAD enumerates that same collection in
            // OnTesselation. That threw "Collection was modified" mid-tesselation and left the
            // block rendered wrong for a frame: the "shape briefly turning" glitch.
            //
            // The walk always ends on an EMPTY level, so the creating form was guaranteed to
            // add an entry on every single frame. TryPut uses TryGetValue for the same reason.
            DisplaySlotId cursor = aimed;
            while (true)
            {
                if (!inv.SlotsByslotId.TryGetValue(cursor.Encoded, out ItemSlot slot)
                    || slot == null || slot.Empty)
                {
                    break;
                }
                cursor = cursor.UpCopy();
            }

            target = cursor;
            rotDeg = ChosenRotationDeg(aimed.Encoded);

            // Red rather than absent when it will not go: an item that does not stack, or a
            // pile that has run out of headroom, is exactly what the ghost exists to tell you.
            // Identical test to TryStack's, through the same StackBaseY, so the ghost cannot
            // redden while the placement succeeds or the other way about.
            allowed = heldAttr.Behavior == EnumDisplayableBehavior.Stacking
                      && StackBaseY(cursor, surface, heldAttr) < surface.Size.Height;
            return true;
        }

        public bool TryBuildGhost(string slotId, ItemSlot heldSlot, DisplayableAttributes dattr,
                                  float rotDeg, out MeshData mesh, out float[] matrix)
        {
            mesh = null;

            // The PENDING nudge, for the same reason the angle is an override here: the
            // target slot is empty, so there is nothing stored to read. Resolved inside
            // rather than passed in, so the renderer cannot show a different number from the
            // one the placement will use.
            matrix = BuildSlotMatrix(slotId, heldSlot, out _, rotDeg, null,
                                     PlacementHeight.For(Api, (Api as ICoreClientAPI)?.World.Player));
            if (matrix == null) return false;

            mesh = GetOrCreateItemMesh(heldSlot, dattr);
            return mesh != null;
        }

        private float[] BuildSlotMatrix(string slotId, ItemSlot slot, out DisplayableAttributes dattr,
                                        float? rotationOverrideDeg = null,
                                        KeyValuePair<string, ItemSlot>[] slots = null,
                                        float? heightOverrideVoxels = null)
        {
            dattr = null;

            BlockBehaviorDisplaySurfaces bh = SurfaceBehavior;
            if (bh == null || slot.Empty || slot.Itemstack?.Collectible?.Code == null) return null;

            DisplaySlotId loc = DisplaySlotId.Decode(slotId);
            DisplaySurface surface = bh.GetSurface(loc?.SurfaceIndex ?? -1);
            if (surface == null) return null;

            dattr = BlockBehaviorDisplaySurfaces.GetDisplayableAttributes(slot, surface.DisplayCategory ?? "shelf");
            if (dattr == null) return null;

            Vec3f surfacePos = surface.VoxelPosition;

            Vec3f offset = BlockBehaviorDisplaySurfaces.GetCentreOffset(dattr.Size);

            // The pile below decides the height, not this item's own box — see StackBaseY —
            // plus the player's own nudge, which is what SlotRestY adds.
            // `slots` is threaded in from GenerateMeshes because that runs on the TESSELATION
            // thread, where the live inventory must not be read; the ghost path passes none
            // and takes its own snapshot.
            float stackY = SlotRestY(loc, surface, dattr, slots, heightOverrideVoxels);

            // ONE definition of the angle, shared with the footprint and the particles.
            // All three used to compute it separately, and this one REPLACED the jitter with
            // the chosen angle rather than adding to it — so a rotated item rendered exactly
            // square while its box was measured at chosen + jitter. Model and box disagreeing
            // is the same class of bug as wrenching turning the model but not its footprint.
            //
            // ItemRotationDeg is chosen + jitter + surface facing, the facing added last so
            // wrench steps stay relative to it: changing a surface's facing turns everything
            // already on it and keeps relative angles.
            //
            // The override exists only for the placement ghost, which is previewing an item
            // that has no stored angle yet. It is a parameter rather than a second copy of
            // the arithmetic for the reason spelled out above.
            float rotDeg = rotationOverrideDeg ?? ItemRotationDeg(slotId, dattr, surface);

            // NO HEIGHT JITTER. There used to be a +/-3.3% vertical scale wobble here,
            // removed 2026-07-28: randomness in an item's ANGLE reads as natural, randomness
            // in its HEIGHT does not — two identical ingots are not different heights. It
            // also cost more than it gave, since a stack of items at slightly different
            // heights shows gaps where they fall short and z-fights where they overlap.
            // Only the resting angle is random now.
            return new Matrixf()
                .Translate(0.5f, 0f, 0.5f)
                .RotateYDeg(Block.Shape?.rotateY ?? 0f)
                .RotateY(MeshAngleRad)
                .Translate(
                    (surfacePos.X + loc.X + offset.X) / 16f,
                    (surfacePos.Y + stackY) / 16f,
                    (surfacePos.Z + loc.Z + offset.Z) / 16f)
                .Translate(-0.5f + dattr.Size.Width / 2f / 16f, 0f, -0.5f + dattr.Size.Length / 2f / 16f)
                .RotateY(GameMath.DEG2RAD * rotDeg)
                .Translate(-0.5f, 0f, -0.5f)
                .Values;
        }

        private void GenerateMeshes()
        {
            if (SurfaceBehavior == null) return;

            // Leaving meshesGenerated false means the next frame retries, so the first
            // mesh anyone sees is already textured with the right wood and glass
            if (!ArlVariantsReady) return;

            // A throw here must not cost us the item transforms below, and must not
            // latch meshesGenerated — leaving it false means the next frame retries.
            try
            {
                blockMesh = BuildBlockMesh();
            }
            catch (Exception e)
            {
                Api?.Logger.Warning("[UniversalDisplayLib] Block mesh build failed at {0}, retrying next frame: {1}", Pos, e.Message);
                blockMesh = null;
            }

            TfMatrices.Clear();

            // Snapshot: GenerateMeshes is called FROM OnTesselation, so this is the
            // tesselation thread and the live collection can be mutated under it. It is
            // handed to BuildSlotMatrix as well, which needs the items BELOW each one to
            // work out how high it rests.
            KeyValuePair<string, ItemSlot>[] slots = SlotsSnapshot();

            foreach (var kv in slots)
            {
                float[] matrix = BuildSlotMatrix(kv.Key, kv.Value, out DisplayableAttributes dattr, null, slots);
                if (matrix == null) continue;

                GetOrCreateItemMesh(kv.Value, dattr);
                TfMatrices[kv.Key] = matrix;
            }

            meshesGenerated = true;
        }

        /// <summary>
        /// Static frame mesh, built through ARL so per-variant wood/glass textures
        /// apply. psurface marker elements are stripped; ARL appends
        /// cshape.IgnoreElements in every branch, so they survive to the tesselator.
        ///
        /// Also strips <c>animatedElements</c>, which is what keeps a single-file
        /// shape from rendering its moving parts twice — once here in the chunk
        /// mesh and once through the animator.
        /// </summary>
        private MeshData BuildBlockMesh()
        {
            string[] ignore = (SurfaceBehavior?.SurfaceElementNames ?? Array.Empty<string>())
                .Append(Block.Attributes?["animatedElements"].AsArray<string>(null))
                .Append(Block.Shape?.IgnoreElements);

            var arlBlock = Block.GetBehavior<BlockBehaviorShapeTexturesFromAttributes>();
            var arlBe = GetBehavior<BlockEntityBehaviorShapeTexturesFromAttributes>();

            if (arlBlock != null && arlBe != null)
            {
                CompositeShape cshape = (Block.Attributes?["shape"].AsObject<CompositeShape>() ?? Block.Shape)?.Clone();
                if (cshape == null) return null;

                cshape.IgnoreElements = ignore;
                return arlBlock.GetOrCreateMesh(arlBe.Variants, cshape, Pos, "universaldisplaylib-psurface")?.Clone();
            }

            Shape shape = capi.TesselatorManager.GetCachedShape(Block.Shape.Base);
            if (shape == null) return null;

            capi.Tesselator.TesselateShape(new TesselationMetaData
            {
                TexSource = capi.Tesselator.GetTextureSource(Block),
                IgnoreElements = ignore
            }, shape, out MeshData mesh);

            return mesh;
        }

        private ITexPositionSource BuildBlockTextureSource(Shape shape, string nameForLogging)
        {
            var arlBlock = Block.GetBehavior<BlockBehaviorShapeTexturesFromAttributes>();
            var arlBe = GetBehavior<BlockEntityBehaviorShapeTexturesFromAttributes>();

            if (arlBlock == null || arlBe == null) return capi.Tesselator.GetTextureSource(Block);

            var texSource = new UniversalShapeTextureSource(capi, capi.BlockTextureAtlas, shape, nameForLogging ?? Block.Code.ToString());
            foreach (var kv in Block.Textures) texSource.textures[kv.Key] = kv.Value;

            ShapeOverlayHelper.BakeVariantTextures(capi, texSource, arlBe.Variants, arlBlock.texturesByType);
            return texSource;
        }

        /// <summary>
        /// Keyed by display category as well as the item, because the transform comes
        /// from the category. Without it, the same item shown under two categories
        /// would share one cached mesh and whichever tesselated first would win.
        /// </summary>
        private string GetItemMeshCacheKey(ItemSlot slot)
        {
            string category = (slot as ItemSlotDisplay)?.DisplayCategory ?? "shelf";
            string itemKey = slot.Itemstack.Collectible?.GetCollectibleInterface<IContainedMeshSource>()?.GetMeshCacheKey(slot)
                             ?? slot.Itemstack.Collectible.Code.ToString();
            return category + "|" + itemKey;
        }

        private MeshData GetCachedItemMesh(ItemSlot slot)
        {
            MeshCache.TryGetValue(GetItemMeshCacheKey(slot), out MeshData mesh);
            return mesh;
        }

        /// <summary>Display category of the surface a given slot id sits on.</summary>
        private string DisplayCategoryFor(string slotId)
        {
            DisplaySlotId loc = DisplaySlotId.Decode(slotId);
            return SurfaceBehavior?.GetDisplayCategory(loc?.SurfaceIndex ?? 0) ?? "shelf";
        }

        /// <summary>
        /// Takes a plain ItemSlot rather than ItemSlotDisplay: a slot that arrives
        /// from anywhere other than our own allocator would otherwise be skipped
        /// and render as nothing.
        /// </summary>
        private MeshData GetOrCreateItemMesh(ItemSlot slot, DisplayableAttributes dattr)
        {
            if (slot == null || slot.Empty) return null;

            MeshData cached = GetCachedItemMesh(slot);
            if (cached != null) return cached;

            CollectibleObject collectible = slot.Itemstack.Collectible;

            // A displayable entry may name its own shape to render instead of the item's,
            // which is how clothing shows as a folded pile rather than a worn garment —
            // `displayable.shelf.shape` on upperbody.json points at upperbody-folded. Ignoring
            // it meant the item tesselated from its normal shape and looked wrong.
            //
            // Cached globally on collectible code + shape, not per block entity: the same
            // garment folds identically in every window, and vanilla keys it the same way.
            MeshData mesh = null;
            CompositeShape customShape = dattr?.Shape;
            if (customShape != null)
            {
                string shapeKey = "universaldisplaylib-shape-" + collectible.Code + "-" + customShape;

                // CLONE IT. What comes back is the SHARED cached mesh, and the transform below
                // is applied with MeshData.ModelTransform, which mutates in place — so without
                // this every rebuild transforms an already-transformed mesh and the effect
                // compounds. The block branch further down already clones for the same reason;
                // this path was missing it.
                //
                // Normally invisible because the block entity's own mesh cache means a given
                // item is usually built once. The transform editor is what exposes it: each
                // nudge invalidates and rebuilds, so the item shrinks a little every time
                // until it vanishes — reported 2026-08-02 as "it goes invisible when editing".
                mesh = ObjectCacheUtil.GetOrCreate(capi, shapeKey, () =>
                    capi.TesselatorManager.CreateMesh("display item shape", customShape,
                        (shape, name) => BuildContainedTextureSource(shape, slot, collectible)))
                    ?.Clone();
            }

            // AN EMPTY CUSTOM-SHAPE MESH MUST NOT WIN over the ordinary paths below.
            //
            // CreateMesh can hand back a mesh with no vertices — a shape that resolved but
            // produced nothing — and because that is non-null it used to short-circuit
            // everything after it, so the item rendered as absolutely nothing with no error
            // anywhere. Vanilla mushrooms do exactly this on their `displayable.shelf` shape;
            // reported 2026-08-02 as "mushrooms are invisible", with a completely clean log.
            //
            // Falling through means the worst case is the item's normal appearance rather
            // than no appearance, which is the right way round for a display mod.
            //
            // Logged at Debug so it lands in client-debug.log rather than the main log: it
            // names the shape and the item, which is the whole diagnosis for this class of
            // fault, and it costs nothing on the paths that work.
            if (mesh != null && mesh.VerticesCount == 0)
            {
                capi.Logger.Debug(
                    "[UniversalDisplayLib] custom shape '{0}' for {1} tesselated to an empty mesh; " +
                    "falling back to the item's own.", customShape?.Base, collectible?.Code);
                mesh = null;
            }

            // CLONED, because this one is not ours. Both branches either side of it yield a
            // mesh we own - GetDefaultBlockMesh is cloned, a tesselated item mesh is fresh -
            // and this one was the exception, mutated in place a few lines below.
            //
            // Most IContainedMeshSource implementations build a fresh mesh per call, so it
            // never showed. **BlockShapeFromAttributes does not**: clutter caches its meshes by
            // type in `cMeshes` and GenMesh returns the SHARED instance (public source,
            // anegostudios/vssurvivalmod, Block/BlockShapeFromAttributes.cs). So we were
            // applying the display transform to VANILLA'S OWN cached mesh, once per
            // re-tesselation, compounding it every time - and corrupting that clutter type
            // everywhere in the world, vanilla shelves included.
            //
            // Reported as clutter "shrinking to nothing no matter where the slider goes" while
            // ordinary items tuned fine. A probe proved the data path innocent: the applied
            // scale and the resolved scale matched exactly on every edit, so the fault had to
            // be in what the mesh did with it afterwards.
            MeshData shared = collectible?.GetCollectibleInterface<IContainedMeshSource>()
                ?.GenMesh(slot, capi.BlockTextureAtlas, Pos);
            mesh ??= shared?.Clone();

            if (mesh == null)
            {
                ItemStack stack = slot.Itemstack;
                if (stack.Class == EnumItemClass.Block)
                {
                    mesh = capi.TesselatorManager.GetDefaultBlockMesh(stack.Block).Clone();
                }
                else
                {
                    _nowTesselatingObj = stack.Collectible;
                    _nowTesselatingShape = stack.Item.Shape?.Base != null
                        ? capi.TesselatorManager.GetCachedShape(stack.Item.Shape.Base)
                        : null;

                    capi.Tesselator.TesselateItem(stack.Item, out mesh, this);
                    mesh.RenderPassesAndExtraBits.Fill((short)EnumChunkRenderPass.OpaqueNoCull);
                    _nowTesselatingObj = null;
                    _nowTesselatingShape = null;
                }
            }

            // NOTHING ON A WINDOWSILL SWAYS IN THE WIND.
            //
            // Wind is baked into the vertex flags at tesselation, so a plant keeps swaying
            // once it is on a sill — which looks wrong indoors and is what made displayed
            // flowers wave about. ANDing with ClearWindBitsMask drops the wind mode and wind
            // data bits and leaves everything else in the flags untouched.
            //
            // Applied to every displayed item rather than only to plants. In practice they
            // are the same set today, since nothing else placeable declares a wind mode — but
            // an item is a still object sitting on a shelf whatever it is, so the rule reads
            // better as "displayed things do not move" than as a plant special case.
            //
            // Safe to mutate here: every path above yields a mesh we own — the custom shape
            // branch clones, the contained-mesh branch clones (it did NOT until 2026-08-10, and
            // that was the clutter bug), GetDefaultBlockMesh is cloned, and a tesselated item
            // mesh is fresh.
            if (mesh?.Flags != null)
            {
                for (int i = 0; i < mesh.FlagsCount; i++)
                {
                    mesh.Flags[i] &= VertexFlags.ClearWindBitsMask;
                }
            }

            if (dattr?.Transform != null)
            {
                dattr.Transform.EnsureDefaultValues();
                mesh.ModelTransform(dattr.Transform);
            }
            else if (slot.Itemstack.Class == EnumItemClass.Item &&
                     (slot.Itemstack.Item.Shape == null || slot.Itemstack.Item.Shape.VoxelizeTexture))
            {
                mesh.Rotate(GameMath.PIHALF, 0f, 0f);
                mesh.Scale(0.33f, 0.33f, 0.33f);
                mesh.Translate(0f, -15f / 32f, 0f);
            }

            MeshCache[GetItemMeshCacheKey(slot)] = mesh;
            return mesh;
        }

        /// <summary>
        /// Texture resolver for a displayable's own shape, in the order vanilla uses.
        ///
        /// Lowest to highest priority:
        ///   1. the shape's own declared textures
        ///   2. the itemstack's, so a folded garment picks up THAT garment's cloth rather
        ///      than whatever the shared folded shape happens to declare
        ///   3. a plant's `plantContainable` textures
        ///
        /// Step 3 is not optional for plants and is why this takes the props at all. Vanilla's
        /// potted-plant shape, `block/clay/flowerpot/contents`, declares <c>textures: {}</c>
        /// while its faces use <c>#north</c> — the plant's own colour comes entirely from
        /// `plantContainable.textures`, one entry per flower. Without this the shape would
        /// tesselate with no mapping for that code, which is a logged error and a wrong-looking
        /// plant. Lily of the valley is the one exception: it has a dedicated shape that
        /// declares all seven of its own textures, so it would have worked either way.
        ///
        /// The order matches BlockEntityPlantContainer's own indexer, which checks its props
        /// first and only then falls back to the shape and the contents.
        /// </summary>
        private ITexPositionSource BuildContainedTextureSource(Shape shape, ItemSlot slot,
                                                              CollectibleObject collectible)
        {
            var textures = new Dictionary<string, AssetLocation>(shape.Textures);

            IDictionary<string, CompositeTexture> own = slot.Itemstack.Class == EnumItemClass.Item
                ? slot.Itemstack.Item.Textures
                : slot.Itemstack.Block.Textures;

            if (own != null)
            {
                foreach (KeyValuePair<string, CompositeTexture> kv in own)
                {
                    textures[kv.Key] = kv.Value.Base;
                }
            }

            PlantContainerProps potted = BlockBehaviorDisplaySurfaces.PottedProps(collectible);
            if (potted?.Textures != null)
            {
                foreach (KeyValuePair<string, CompositeTexture> kv in potted.Textures)
                {
                    if (kv.Value?.Base != null) textures[kv.Key] = kv.Value.Base;
                }
            }

            return new ContainedTextureSource(capi, capi.BlockTextureAtlas, textures,
                                              "For displayed item " + collectible.Code);
        }

        // ── ITexPositionSource, for the item tesselation fallback ────────────

        public Size2i AtlasSize => capi.BlockTextureAtlas.Size;

        public TextureAtlasPosition this[string textureCode]
        {
            get
            {
                IDictionary<string, CompositeTexture> textures = _nowTesselatingObj is Item item
                    ? (IDictionary<string, CompositeTexture>)item.Textures
                    : (_nowTesselatingObj as Block)?.Textures;

                AssetLocation path = null;
                if (textures != null)
                {
                    if (textures.TryGetValue(textureCode, out CompositeTexture ct)) path = ct.Baked.BakedName;
                    else if (textures.TryGetValue("all", out CompositeTexture all)) path = all.Baked.BakedName;
                }

                if (path == null) _nowTesselatingShape?.Textures.TryGetValue(textureCode, out path);
                path ??= new AssetLocation(textureCode);

                TextureAtlasPosition texPos = capi.BlockTextureAtlas[path];
                if (texPos == null && !capi.BlockTextureAtlas.GetOrInsertTexture(path, out _, out texPos))
                {
                    return capi.BlockTextureAtlas.UnknownTexturePosition;
                }
                return texPos;
            }
        }

        // ── Persistence ──────────────────────────────────────────────────────

        // LEGACY SLOT MIGRATION REMOVED 2026-07-30, and it is worth knowing why it was
        // never needed and was not merely useless.
        //
        // It converted a pre-psurface inventory — the old block entity used an
        // InventoryGeneric, so slots were saved under integer keys ("0", "1", …) which
        // DisplaySlotId.Decode rejects. That situation cannot arise here: Window Display
        // ships as a SEPARATE mod from Window Storage with its own block codes, nothing is
        // remapped, and no windowdisplay block has ever written an integer slot id. It was
        // already recorded as "not applicable" rather than merely untested.
        //
        // It also ran from FromTreeAttributes, so it re-decoded every slot id of every
        // window on every load and every server sync, to reach a `return` every time.
        //
        // And it was destructive before it validated:
        //
        //     inv.SlotsByslotId.Remove(key);            // removed FIRST
        //     if (stack == null) continue;
        //     if (!int.TryParse(key, out int index)) continue;   // <- stack dropped here
        //
        // Any slot id that was neither a valid DisplaySlotId nor an integer had its slot
        // removed and its itemstack discarded — silent item loss, with no path back. That
        // is a poor trade for a migration that cannot fire, which is what settled removing
        // it rather than fixing the ordering.

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
        {
            base.FromTreeAttributes(tree, worldForResolving);

            bool[] previousPanes = paneStates != null ? (bool[])paneStates.Clone() : null;

            MeshAngleRad = tree.GetFloat("meshAngleRad");

            string states = tree.GetString("paneStates", "");
            if (!string.IsNullOrEmpty(states))
            {
                paneStates = new bool[states.Length];
                for (int i = 0; i < states.Length; i++) paneStates[i] = states[i] == '1';
            }

            // ALWAYS replaced, including with null when the tree carries no rotations.
            //
            // Only assigning when the attribute is present left stale angles behind, because
            // ToTreeAttributes omits "rotation" entirely once the dictionary is empty. Take
            // the last rotated item off a window and the server sends no rotation data at
            // all, so the client kept its old map — and the next item placed in that slot
            // rendered at the previous occupant's angle on the client while the server had
            // it square. The tree is authoritative; treat it as such.
            customRotationDegBySlot = null;
            if (tree.HasAttribute("rotation"))
            {
                customRotationDegBySlot = new Dictionary<string, float>();
                foreach (var kv in tree.GetTreeAttribute("rotation"))
                {
                    if (kv.Value is FloatAttribute f) customRotationDegBySlot[kv.Key] = f.value;
                }
            }

            // ALWAYS replaced, null included, for the reason above — the tree is
            // authoritative, and only assigning when the attribute is present leaves a stale
            // map behind once the last nudged item is taken off.
            customHeightBySlot = null;
            if (tree.HasAttribute("itemheight"))
            {
                customHeightBySlot = new Dictionary<string, float>();
                foreach (var kv in tree.GetTreeAttribute("itemheight"))
                {
                    if (kv.Value is FloatAttribute f) customHeightBySlot[kv.Key] = f.value;
                }
            }

            // ALWAYS replaced, null included, for the same reason as the two above — the tree
            // is authoritative. Keyed by psurface index written as a string, since a tree
            // attribute's keys are strings; an unparseable key is skipped rather than throwing,
            // because a corrupt entry should cost one lock and not the whole block entity.
            lockedSlotBySurface = null;
            if (tree.HasAttribute("lockedslots"))
            {
                lockedSlotBySurface = new Dictionary<int, string>();
                foreach (var kv in tree.GetTreeAttribute("lockedslots"))
                {
                    if (kv.Value is StringAttribute s && int.TryParse(kv.Key, out int surfaceIndex))
                    {
                        lockedSlotBySurface[surfaceIndex] = s.value;
                    }
                }
            }

            InvalidateBoxes();
            MarkMeshesDirty();

            if (worldForResolving.Side == EnumAppSide.Client && Api != null)
            {
                bool reinitialised = EnsureAnimatorCurrent();

                bool panesChanged = previousPanes == null || paneStates == null || previousPanes.Length != paneStates.Length;
                if (!panesChanged && previousPanes != null)
                {
                    for (int i = 0; i < paneStates.Length && i < previousPanes.Length; i++)
                    {
                        if (previousPanes[i] != paneStates[i]) { panesChanged = true; break; }
                    }
                }

                if (panesChanged || reinitialised) UpdateAnimationState();

                if (panesChanged) soundHandler?.PlaySlideSound();
                soundHandler?.FadeSharedSoundsToCurrentTarget();
            }
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetFloat("meshAngleRad", MeshAngleRad);

            if (paneStates != null && paneStates.Length > 0)
            {
                var chars = new char[paneStates.Length];
                for (int i = 0; i < paneStates.Length; i++) chars[i] = paneStates[i] ? '1' : '0';
                tree.SetString("paneStates", new string(chars));
            }

            if (customRotationDegBySlot != null && customRotationDegBySlot.Count > 0)
            {
                var rotTree = new TreeAttribute();
                foreach (var kv in customRotationDegBySlot) rotTree[kv.Key] = new FloatAttribute(kv.Value);
                tree["rotation"] = rotTree;
            }

            if (customHeightBySlot != null && customHeightBySlot.Count > 0)
            {
                var heightTree = new TreeAttribute();
                foreach (var kv in customHeightBySlot) heightTree[kv.Key] = new FloatAttribute(kv.Value);
                tree["itemheight"] = heightTree;
            }

            if (lockedSlotBySurface != null && lockedSlotBySurface.Count > 0)
            {
                var lockTree = new TreeAttribute();
                foreach (var kv in lockedSlotBySurface)
                {
                    lockTree[kv.Key.ToString(CultureInfo.InvariantCulture)] = new StringAttribute(kv.Value);
                }
                tree["lockedslots"] = lockTree;
            }
        }

        // ── Info ─────────────────────────────────────────────────────────────

        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
        {
            bool roomSafe = UniversalDisplayLibConfig.Current?.RoomSafeOpening ?? false;
            if (CanOpen && !roomSafe)
            {
                dsc.AppendLine(IsAnySealingPaneOpen ? Lang.Get("Open") : Lang.Get("Closed"));
            }

            container.ReloadRoom();

            // container.GetPerishRate() only accounts for the room. The open/closed
            // multiplier lives in our OnAcquireTransitionSpeed delegate, so it has to
            // be applied here too or the tooltip never moves when a pane opens.
            float perishRate = container.GetPerishRate() * OnAcquireTransitionSpeed(EnumTransitionType.Perish, null, 1f);
            dsc.AppendLine(Lang.Get("Stored food perish speed: {0}x", Math.Round(perishRate, 2)));

            // The pending placement angle used to be shown here because it was persistent
            // but otherwise invisible — you saw it when you pressed the key and never
            // again, so items quietly went on sideways later.
            //
            // THE GHOST REPLACED THAT REASON. It draws the item at its actual pending angle
            // for as long as you are aiming, which is strictly better than a number: you
            // see the orientation rather than reading it. Dropped from the panel on
            // 2026-07-29, once the preview was confirmed working in game.
            //
            // Still shown when the ghost is switched off, because the angle is invisible
            // again then. Same fallback as the key-press message, and the same reason.
            //
            // (Kept out of the interaction help, then and now: composed help tooltips are
            // CACHED and not rebuilt — the same reason the ctrl/shift label goes stale after
            // a rebind — so a live value there would lie. This panel is rebuilt while aiming
            // at any part of the window, including bare sill.)
            if (UniversalDisplayLibConfig.Current?.PlacementGhost != true)
            {
                int pendingRot = PlacementRotation.For(Api, forPlayer);
                if (pendingRot != 0 && forPlayer?.InventoryManager?.ActiveHotbarSlot?.Empty == false)
                {
                    dsc.AppendLine(Lang.Get("universaldisplaylib:placement-rotation", pendingRot));
                }
            }

            string slotId = GetSelectedNonEmptySlotId(forPlayer?.CurrentBlockSelection);
            if (slotId != null && inv[slotId] is { Empty: false } slot)
            {
                AppendStoredItemInfo(dsc, slot, perishRate);
            }
        }

        /// <summary>
        /// The aimed item's name, plus its spoilage and temperature when it has them.
        ///
        /// Spoilage is formatted by <c>BlockEntityShelf.PerishableInfoCompact</c> — vanilla's
        /// own helper, public and static, and what a shelf uses for the same job. Reusing it
        /// keeps the wording, rounding and "fresh / X days" thresholds identical to every
        /// other container in the game rather than inventing a second dialect. It already
        /// includes the item name, so the plain name is only appended when the item has no
        /// transitionable properties at all.
        ///
        /// <paramref name="perishRate"/> is passed through as the ripen rate so the figure
        /// reflects THIS window — the room, and whether a pane is open — instead of a generic
        /// estimate. That is the same number the "Stored food perish speed" line reports.
        /// </summary>
        private void AppendStoredItemInfo(StringBuilder dsc, ItemSlot slot, float perishRate)
        {
            ItemStack stack = slot.Itemstack;
            if (stack?.Collectible == null) return;

            TransitionableProperties[] props =
                stack.Collectible.GetTransitionableProperties(Api.World, stack, null);

            if (props != null && props.Length > 0)
            {
                // The third argument is a RIPEN rate, not a perish rate — vanilla's own
                // BlockEntityShelf derives it as a clamped 0..1 "how good a spot is this for
                // ripening", and PerishableInfoCompact uses it only in the Ripen branch, as
                // (TransitionHours - TransitionedHours) / HoursPerDay / ripenRate.
                //
                // Passing our perish rate here was wrong: it runs to ~4.8, so anything that
                // RIPENS reported a "days left to ripen" several times too short. The perish
                // side was unaffected — that branch divides by the collectible's own
                // GetTransitionRateMul and never looks at this argument.
                float ripenRate = GameMath.Clamp((1f - container.GetPerishRate() - 0.5f) * 3f, 0f, 1f);
                dsc.Append(BlockEntityShelf.PerishableInfoCompact(Api, slot, ripenRate));
            }
            else
            {
                // Matches vanilla's fallback: a crock or meal names its own contents
                dsc.AppendLine(
                    stack.Collectible.GetCollectibleInterface<IContainedCustomName>()?.GetContainedInfo(slot)
                    ?? stack.GetName());
            }

            // Only when the stack actually carries a temperature — otherwise every stored
            // item would report the ambient value, which is noise on a jar or a candle.
            if (stack.Attributes?.GetTreeAttribute("temperature") != null)
            {
                float temp = stack.Collectible.GetTemperature(Api.World, stack);
                dsc.AppendLine(Lang.Get("Temperature: {0}°C", (int)Math.Round(temp)));
            }
        }
    }
}
