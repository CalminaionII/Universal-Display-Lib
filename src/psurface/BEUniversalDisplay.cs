using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Text;
using UniversalDisplayLib.Materials;
using Vintagestory.Client.NoObf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
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
    ///  * the block mesh is built with the block's materials (UDMaterials - the lib's own, since
    ///    1.2.0; Attribute Rendering Library before)
    ///
    /// The psurface *format* is kept identical to vanilla — shape element names and
    /// string slot ids — so shapes authored for cabinets work unchanged.
    /// </summary>
    public partial class BEUniversalDisplay : BlockEntityContainer, ITexPositionSource, IAutoBoxesOwner, IAutoBoxesPartOfOwner
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

        /// <summary>The placed items' footprint boxes from the last box rebuild (both sides alike) - itemCollision's boxes.</summary>
        private Cuboidf[] _itemFootprints;

        /// <summary>The block's JSON asks for its placed items to be solid (the ground pile).</summary>
        bool ItemCollision => Block?.Attributes?["itemCollision"].AsBool(false) == true;

        /// <summary>
        /// Set when a placed item's outline had to use its written size because its mesh was not built yet (the
        /// boxes are made the moment an item goes in, the mesh a moment later when it is drawn). The next
        /// tesselation, which builds the mesh, drops the outlines so they are measured again - so an item put in, or
        /// swapped for another, gets ITS drawn size (Calm, 2026-10-06: "track the size change when replaced").
        /// </summary>
        private volatile bool _itemBoxNeedsMesh;
        private Cuboidf[] _collisionBoxes;

        /// <summary>
        /// Which grid generation the cached boxes were built against. When the placement
        /// grid is rebuilt - PrecisePlacement changing - this no longer matches and the
        /// boxes are regenerated on next use. See BlockBehaviorDisplaySurfaces.GridGeneration.
        /// </summary>
        private int _boxesGeneration = -1;

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

        // [ThreadStatic]: item meshes are built on the tesselation thread AND the main thread (the ghost), and the
        // texture lookup below reads these while TesselateItem runs on the same thread that set them (2026-10-22).
        [ThreadStatic] private static CollectibleObject _nowTesselatingObj;
        [ThreadStatic] private static Shape _nowTesselatingShape;

        public byte[] CachedLightHsv => IsEmbedded ? embeddedEffects.Light : _cachedLightHsv;

        public BlockBehaviorDisplaySurfaces SurfaceBehavior => Block?.GetBehavior<BlockBehaviorDisplaySurfaces>();

        public string AttributeTransformCode => BlockBehaviorDisplaySurfaces.TransformTarget;

        /// <summary>
        /// Concurrent (2026-10-22): written from the tesselation thread (GenerateMeshes) and the main thread (the ghost,
        /// floating items, the editor's Clear) at the same time - a plain Dictionary can corrupt itself under that.
        /// </summary>
        protected ConcurrentDictionary<string, MeshData> MeshCache =>
            ObjectCacheUtil.GetOrCreate(Api, "universaldisplaylib-itemmeshes", () => new ConcurrentDictionary<string, MeshData>());

        /// <summary>MeshCache keys whose mesh is the item's ordinary model, drawn while its own mesh source had nothing yet.</summary>
        ConcurrentDictionary<string, byte> StandInMeshes =>
            ObjectCacheUtil.GetOrCreate(Api, "universaldisplaylib-itemmeshes-standin", () => new ConcurrentDictionary<string, byte>());

        /// <summary>Whether this slot's cached mesh is a stand-in, so its measured size is only kept until the real one.</summary>
        bool IsStandInMesh(ItemSlot slot) => StandInMeshes.ContainsKey(GetItemMeshCacheKey(slot));

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

        /// <summary>
        /// A block entity that is NOT in the world, for previewing: the placing ghost on bare ground (Universal Ground
        /// Storage) needs a pile to measure and draw against before one exists. Client only; no ticks, no fillers,
        /// nothing saved. Reads the block below once (followSupport) so the ghost sits where the item will.
        /// </summary>
        public static BEUniversalDisplay Phantom(ICoreClientAPI capi, Block block, BlockPos pos)
        {
            var be = new BEUniversalDisplay { Api = capi, capi = capi, Pos = pos.Copy(), Block = block };
            if (be.FollowSupport) be.RefreshSupport(false);
            return be;
        }

        public BEUniversalDisplay()
        {
            inv = new InventoryInfinite((slotId, invBase) =>
            {
                DisplaySlotId loc = DisplaySlotId.Decode(slotId);
                string category = SurfaceBehavior?.GetDisplayCategory(loc?.SurfaceIndex ?? 0) ?? "shelf";
                // takes bags with things in them too (ItemSlotDisplayBags)
                return new ItemSlotDisplayBags(invBase, category);
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

            if (IsEmbedded)
            {
                StartEmbeddedEffects();
                if (Api.Side == EnumAppSide.Server) ScheduleItemFillerCheck();
                if (capi != null)
                {
                    LiveClientInstances.Add(this);
                    _clientTickListener = RegisterGameTickListener(_ => RefreshEmbeddedMeshes(), 500);
                    RefreshEmbeddedMeshes();
                }
                return;
            }

            // NOTE: deliberately not subscribing to inv.SlotModified.
            // InventoryInfinite overrides DidModifyItemSlot to an empty method, so
            // SlotModified never fires for this inventory type. Content changes are
            // signalled explicitly through OnContentsChanged() instead.

            UpdateLightCache();
            // A chunk/first-placement packet can initialize with items already loaded.
            // Wait until this BE is attached to the accessor before registering its light.
            if (capi != null) Api.Event.EnqueueMainThreadTask(ApplyLight, "udl-initial-stored-light");

            // Server only: the claim is authoritative state and reaches the client through the
            // tree like everything else. Doing it on both sides would have them adopt
            // independently, which is fine while they agree and a silent divergence when they
            // do not.
            if (api.Side == EnumAppSide.Server) AdoptPreExistingLocks();

            AutoBoxesMeasureTurned();

            // followSupport (the ground pile): read what it stands on now, and again every second - chiselling the block
            // below changes no block id, so no neighbour event says so (Taxidermy's mounts found the same).
            if (FollowSupport)
            {
                RefreshSupport(false);
                RegisterGameTickListener(_ => RefreshSupport(true), 1000);
            }

            // An old chain tall window (Window Display 1.0.5) converts as it loads; every window re-syncs its fillers once.
            if (api.Side == EnumAppSide.Server) Reach.LegacyTallWindows.Schedule(this);
            if (api.Side == EnumAppSide.Server) LiveServerInstances[this] = 0;
            // a pane left open with an autoCloseSeconds (saved open, or the world closed before it shut) gets its time again
            if (api.Side == EnumAppSide.Server)
                RegisterDelayedCallback(_ => { for (int i = 0; i < paneStates.Length; i++) if (paneStates[i]) ScheduleAutoClose(i); }, 1000);

            if (capi != null)
            {
                // the animator is made by InitializeAnimator, and only for a block with something to open (Animates)
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
            // the ghost keeps its own uploaded copy of each item's mesh - drop those too, or it shows the old size
            PlacementGhostRenderer.MarkStale();
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
            if (!MaterialsReady) _variantWaitTicks++;

            // A wrenchSwapTo target can have a different number of pane groups
            if (_paneStateBlockId != Block.Id)
            {
                _paneStateBlockId = Block.Id;
                ClampPaneStatesToBlock();
                AutoBoxesMeasureTurned();
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
            // base runs the UDMaterials behaviour, which is where the materials get filled from
            // the stack. Everything texture-dependent has to happen after this line, not in
            // Initialize — that runs first, with no variants, and is why a freshly
            // placed window used to show default textures before correcting itself.
            base.OnBlockPlaced(byItemStack);

            ClampPaneStatesToBlock();

            if (Api?.Side == EnumAppSide.Server) SyncFillers();

            if (Api?.Side == EnumAppSide.Server) MarkDirty(true);
            else RefreshAnimator();
        }

        public override void OnBlockRemoved()
        {
            if (IsEmbedded) { _removed = true; if (Api?.Side == EnumAppSide.Server) AutoBoxes.ReleaseAll(Api.World, Pos, autoHeld); Cleanup(); base.OnBlockRemoved(); return; }
            LiveServerInstances.TryRemove(this, out _);
            if (Api?.Side == EnumAppSide.Server) AutoBoxes.ReleaseAll(Api.World, Pos, autoHeld);
            FreeGuests();
            _removed = true;
            TellHosts(autoLayout);
            RemoveLight();
            Cleanup();
            base.OnBlockRemoved();
        }

        public override void OnBlockUnloaded()
        {
            if (IsEmbedded) { _removed = true; Cleanup(); base.OnBlockUnloaded(); return; }
            LiveServerInstances.TryRemove(this, out _);
            _removed = true;
            TellHosts(autoLayout);
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

            // the items drop at their own size (see ItemScale)
            foreach (ItemSlot stored in inv) ItemScale.Strip(stored?.Itemstack);

            base.OnBlockBroken(byPlayer);
        }

        private void Cleanup()
        {
            if (IsEmbedded) StopEmbeddedEffects();
            PublishStorageReach(_itemCells.Keys, Array.Empty<(int, int, int)>());
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

        /// <summary>
        /// Rebuilds the animator right now, on the caller's thread.
        ///
        /// Called client-side from TryPlaceBlock the moment MeshAngleRad is set. The
        /// block entity is created during base.TryPlaceBlock, so Initialize builds
        /// the renderer while the angle is still 0; without this the correction only
        /// lands on the next client tick and you see the window snap into place.
        /// </summary>
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
                ServerRules.RoomSafeOpening
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
            if (IsEmbedded) RefreshEmbeddedEffects();
            InvalidateBoxes();
            MarkMeshesDirty();

            // Both sides, matching vanilla BlockEntityGroundStorage, which calls
            // LightUpdate straight out of its interaction handler with no side check.
            // Light is computed client-side for rendering as well as server-side; if
            // only the server relights, the client shows nothing until the chunk is
            // reloaded — which is exactly the "only updates on reload" behaviour.
            if (Api != null)
            {
                RefreshStoredLight();
            }

            MarkDirty(true);
        }

        // ── Light from stored items ──────────────────────────────────────────

        // Inventory packets also need this: a newly created client host starts empty,
        // and does not run the server's TryPutAt/OnContentsChanged interaction.
        private void RefreshStoredLight()
        {
            if (IsEmbedded || Api == null) return;
            byte[] oldLight = _cachedLightHsv;
            UpdateLightCache();
            bool changed = (oldLight == null) != (_cachedLightHsv == null)
                || (oldLight != null && _cachedLightHsv != null && !oldLight.SequenceEqual(_cachedLightHsv));
            if (!changed) return;
            if (oldLight != null && _cachedLightHsv == null)
                Api.World.BlockAccessor.RemoveBlockLight(oldLight, Pos);
            ApplyLight();
        }

        private void UpdateLightCache()
        {
            if (IsEmbedded) return;
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
            if (IsEmbedded) return;
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
            if (IsEmbedded) return embeddedEffects.Emitters.Length > 0;
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

        /// <summary>World start: the lookups below hold the last world's Block objects.</summary>
        public static void ForgetBlockLookups() => _blockBehindItem.Clear();

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
            if (IsEmbedded) { SpawnEmbeddedParticles(manager, pos, windAffectednessAtPos); return; }
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
            float scale = ItemScale.For(slot);   // a resized candle keeps its flame on the wick
            if (offset != null && scale != 1f) offset = new Vec3f(offset.X * scale, offset.Y * scale, offset.Z * scale);

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
            if (loc != null && FreeData(loc.Encoded) is { } free) return free.RestY;
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
            if (loc != null && FreeData(loc.Encoded) is { } free)
                return free.RestY + (nudgeOverrideVoxels ?? ChosenHeightVoxels(loc.Encoded));
            return StackBaseY(loc, surface, selfAttr, slots)
                 + (nudgeOverrideVoxels ?? ChosenHeightVoxels(loc?.Encoded))
                 + SupportUnder(loc, surface, selfAttr, slots)
                 + (loc?.SurfaceIndex == 0 ? EmbeddedFloorVoxels : 0f);
        }

        // ── followSupport: rest on what is underneath (the ground pile) ──────────────────────────────────

        /// <summary>The block's JSON asks its items to rest on the block below as it really is (slab, chiselled top).</summary>
        internal bool FollowSupport => Block?.Attributes?["followSupport"].AsBool(false) == true;

        /// <summary>The block below's collision boxes, in that block's units - read on the main thread, read anywhere.</summary>
        volatile Cuboidf[] _support;

        /// <summary>
        /// Reads the block below. A change (chiselled, slab swapped) redraws and rebuilds the boxes; on the server, nothing
        /// left to stand on breaks the block like a removed support would (needsSupport).
        /// </summary>
        void RefreshSupport(bool react)
        {
            if (Api == null) return;
            BlockPos below = Pos.DownCopy();
            Cuboidf[] boxes = Api.World.BlockAccessor.GetBlock(below)?.GetCollisionBoxes(Api.World.BlockAccessor, below)
                              ?? Array.Empty<Cuboidf>();
            if (!UpdateSupport(boxes)) return;
            if (!react) return;

            if (Api.Side == EnumAppSide.Server && !boxes.Any(b => b != null)
                && Block?.Attributes?["needsSupport"].AsBool(false) == true)
            {
                Api.World.BlockAccessor.BreakBlock(Pos, null);
                return;
            }
            InvalidateBoxes();
            MarkMeshesDirty();
            if (capi != null) capi.World.BlockAccessor.MarkBlockDirty(Pos);
        }

        // Compare model-derived coordinates without formatting every box every second.
        // Copy only on a change: game/mod collision arrays may be edited in place.
        bool UpdateSupport(Cuboidf[] boxes)
        {
            var previous = _support;
            int count = 0;
            bool same = previous != null;
            foreach (var box in boxes)
            {
                if (box == null) continue;
                if (same && (count >= previous.Length || !SameSupportBox(previous[count], box))) same = false;
                count++;
            }
            if (same && count == previous.Length) return false;
            var support = new Cuboidf[count];
            int index = 0;
            foreach (var box in boxes) if (box != null) support[index++] = box.Clone();
            PublishSupport(support);
            return true;
        }

        static bool SameSupportBox(Cuboidf a, Cuboidf b)
            => a.X1 == b.X1 && a.Y1 == b.Y1 && a.Z1 == b.Z1
                && a.X2 == b.X2 && a.Y2 == b.Y2 && a.Z2 == b.Z2;

        // Keep volatile publication outside the method ARL scans for block Attributes.
        // ARL 3.1's stack emulator cannot interpret the volatile IL prefix. The
        // publication still uses the same volatile field; readers keep their barrier.
        void PublishSupport(Cuboidf[] support) => _support = support;

        /// <summary>
        /// How far an item rests above (+) or below (-) this block's floor, in voxels: the top of the highest support box
        /// under its footprint (the item sits on the highest point it covers), else the highest anywhere. 0 unless followSupport.
        /// A stacked item takes the bottom item's footprint, so a pile moves as one.
        /// </summary>
        float SupportUnder(DisplaySlotId loc, DisplaySurface surface, DisplayableAttributes selfAttr,
                           KeyValuePair<string, ItemSlot>[] slots)
        {
            if (loc == null || surface == null || !FollowSupport) return 0f;
            DisplayableAttributes baseAttr = selfAttr;
            string baseId = loc.WithY(0).Encoded;
            if (loc.Y > 0)
                baseAttr = FindSlotAttributes(slots ?? SlotsSnapshot(), baseId,
                               surface.DisplayCategory ?? BlockBehaviorDisplaySurfaces.DefaultDisplayCategory) ?? selfAttr;
            Size3f size = baseAttr?.Size != null
                ? BlockBehaviorDisplaySurfaces.RotatedFootprint(baseAttr.Size, ItemRotationDeg(baseId, baseAttr, surface))
                : new Size3f(1f, 1f, 1f);
            return SupportVoxels(surface.VoxelPosition.X + loc.X, surface.VoxelPosition.Z + loc.Z, size.Width / 2f, size.Length / 2f);
        }

        /// <summary>The support height (voxels, -16..16) under a rectangle centred on (cx, cz) in this block's voxels.</summary>
        public float SupportVoxels(float cx, float cz, float halfW, float halfL)
        {
            Cuboidf[] boxes = _support;
            if (boxes == null || boxes.Length == 0 || !FollowSupport) return 0f;
            float x1 = (cx - halfW) / 16f, x2 = (cx + halfW) / 16f, z1 = (cz - halfL) / 16f, z2 = (cz + halfL) / 16f;
            float top = float.MinValue, any = float.MinValue;
            foreach (Cuboidf b in boxes)
            {
                any = Math.Max(any, b.Y2);
                if (b.X2 > x1 && b.X1 < x2 && b.Z2 > z1 && b.Z1 < z2) top = Math.Max(top, b.Y2);
            }
            if (top == float.MinValue) top = any;
            return GameMath.Clamp((top - 1f) * 16f, -16f, 16f);
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

        /// <summary>
        /// True when another window sits directly below this one - a window's own block, or the top
        /// cell of a tall window (a helper since lib 1.1.4; the old chain's top segment was a window
        /// block entity and counted, so the helper has to as well).
        /// </summary>
        public bool IsStacked
        {
            get
            {
                if (Pos == null || Api == null) return false;
                BlockEntity below = Api.World.BlockAccessor.GetBlockEntity(Pos.DownCopy());
                return below is BEUniversalDisplay
                    || (below is BEAutoBoxesFiller filler && filler.Owner != null && Api.World.BlockAccessor.GetBlockEntity(filler.Owner) is BEUniversalDisplay);
            }
        }

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
            _collisionWithGuests = null;
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
            // CHISEL THROUGH A GROUND PILE (chisel-edit trial, Calm 2026-10-19: "while a chisel is held, let the chisel
            // reach the block under a Ground Display pile, like stacked chiselled blocks"): on the client, with chisel +
            // hammer in hand, a followSupport pile offers nothing to aim at, so the aim reaches the chiselled block below.
            // The chisel guard (ChiselShelfSystem.Chisel) still protects the voxels the pile's items stand on.
            if (FollowSupport && Api is ICoreClientAPI chiselClient
                && ChisellingToolsHeld(chiselClient.World.Player?.InventoryManager?.ActiveHotbarSlot, chiselClient.World.Player))
                return Array.Empty<Cuboidf>();

            // A rebuilt grid makes every cached box stale, wherever it is.
            if (_boxesGeneration != BlockBehaviorDisplaySurfaces.GridGeneration) InvalidateBoxes();

            // A host can still have cached boxes after its guest's inventory changed.
            // Refresh dirty guests before using those copies, on the selection thread,
            // rather than waiting for the player to aim at the owning block again.
            RefreshDirtyGuestBoxes();

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

        void RefreshDirtyGuestBoxes()
        {
            BEUniversalDisplay[] known = guests;
            if (known == null || regenDepth != 0 || !CanRebuildBoxesHere()) return;
            // Shared blocks may refer back to each other. Only the outer selection query
            // refreshes dependencies; the existing rebuild guard handles nested guests.
            regenDepth++;
            try
            {
                foreach (BEUniversalDisplay guest in known)
                {
                    if (guest == this || guest._removed || !guest.CanRebuildBoxesHere()) continue;
                    if (guest._padOnlyBoxes == null || guest._allBoxes == null
                        || guest._boxesGeneration != BlockBehaviorDisplaySurfaces.GridGeneration)
                        guest.GetSelectionBoxes();
                }
            }
            finally { regenDepth--; }
        }

        private void RegenSelectionBoxes()
        {
            regenDepth++;
            try { RegenSelectionBoxesNow(); }
            finally { regenDepth--; }
        }

        private void RegenSelectionBoxesNow()
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

            var upperPads = new List<CuboidfWithId>();
            if (bh != null)
            {
                // Placement pads FIRST: a measured box level with a pad (a Static part under a surface) must not
                // win the tie and swallow the click - the pads are what placing aims at.
                // Placement pads, flattened to a 1-voxel lip like vanilla.
                // A stacked surface contributes none, so it cannot be aimed at — the
                // window above covers it.
                foreach (CuboidfWithId pad in IsEmbedded ? Array.Empty<CuboidfWithId>() : bh.GridBoxes)
                {
                    // (2026-10-17) a pad set below lands in upperPads when its surface is in ANOTHER space (a 2-high shelf's
                    // upper shelves) - see SetPadCells
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
                    if (rotated.Y2 >= 1f - 0.001f || rotated.X1 >= 1f || rotated.Z1 >= 1f || rotated.X2 <= 0f || rotated.Z2 <= 0f) upperPads.Add(rotated);
                    if (FollowSupport)
                        rotated.Y1 += SupportVoxels((rotated.X1 + rotated.X2) * 8f, (rotated.Z1 + rotated.Z2) * 8f, 0.25f, 0.25f) / 16f;
                    rotated.Y2 = rotated.Y1 + 0.0625f;
                    boxes.Add(rotated);
                }

                // The window itself, measured from its model (AutoBoxes Kit + Reach.PaneBoxes): each leaf and each
                // Static group, whole from every block it reaches.
                foreach (Cuboidf box in AutoBoxesAt(Pos)) boxes.Add(box);

                // Another window's leaf open into this block (a classic window into an open External Shutter's block):
                // outlined here and clickable, the click going on to that window. Before the placed items, so the
                // server's list (no placing preview) and the client's agree on every index up to here.
                foreach (BEUniversalDisplay guest in Guests())
                {
                    if (guest._removed) continue;
                    string prefix = GuestBoxPrefix + (guest.Pos.X - Pos.X) + "," + (guest.Pos.Y - Pos.Y) + "," + (guest.Pos.Z - Pos.Z) + ":";
                    foreach (Cuboidf box in guest.AutoBoxesAt(Pos)) boxes.Add(new CuboidfWithId(box) { Id = prefix + (box as CuboidfWithId)?.Id });
                }
            }

            _boxesGeneration = BlockBehaviorDisplaySurfaces.GridGeneration;
            _padOnlyBoxes = boxes.ToArray();

            // Outlines of already-placed items, on top of the pads
            var withItems = new List<Cuboidf>(boxes);
            // per item: the written-footprint box (the SAME on server and client - which other spaces it reaches, so a filler's
            // box list, and the box index the game sends, agree) and the outline drawn there (the client's drawn box)
            var itemReach = new List<(Cuboidf footprint, Cuboidf outline)>();
            var footprints = new List<Cuboidf>();
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

                    // CLIENT: the outline is measured from the item as it is DRAWN - its own mesh through the same matrix
                    // the tesselator uses (turn, height, nudge, transform; 2026-10-06). The server has no meshes and keeps
                    // the written footprint: same boxes, same order, same ids, only the client's outline is shaped.
                    // "outline": "size" in the item's entry (flowers and other crossed-plane models): the written box
                    bool bySize = BlockBehaviorDisplaySurfaces.OutlineFromSize(slot, surface.DisplayCategory);
                    Cuboidf drawn = capi == null || bySize ? null : DrawnItemBox(kv.Key, slot, slots);
                    CuboidfWithId footprint = Rotated(itemBox, DisplaySlotId.PlacedPrefix + kv.Key);
                    footprints.Add(footprint);
                    if (drawn != null)
                    {
                        var outline = new CuboidfWithId(drawn) { Id = DisplaySlotId.PlacedPrefix + kv.Key };
                        withItems.Add(outline);
                        itemReach.Add((footprint, outline));
                    }
                    else
                    {
                        withItems.Add(footprint);
                        itemReach.Add((footprint, footprint));
                        if (capi != null && !bySize) _itemBoxNeedsMesh = true;
                    }
                }
            }

            _allBoxes = withItems.ToArray();
            SetPadCells(upperPads);
            _itemFootprints = footprints.ToArray();
            if (ItemCollision) { _collisionBoxes = null; _collisionWithGuests = null; }
            SetItemCells(itemReach);
        }

        /// <summary>
        /// True while the player is lining up a placeable item, in which case only
        /// the pads are exposed so placed items do not block aiming.
        /// </summary>
        [ThreadStatic] static bool inPlacingPreviewCheck;

        private bool IsPlacingPreview()
        {
            if (IsEmbedded) return false; // real voxels are the pads; keep stored-item targets visible
            if (capi == null) return false;

            // The explicit outline control must expose placed-item boxes as well as
            // drawing them. Otherwise a non-stacking held item hides the very flowerpot
            // (or its contents) whose box the player asked to see.
            if (BlockBehaviorDisplaySurfaces.ForceFootprintBox) return false;

            ItemSlot held = capi.World.Player?.InventoryManager?.ActiveHotbarSlot;
            BlockSelection sel = capi.World.Player?.CurrentBlockSelection;
            if (held == null || held.Empty || sel?.SelectionBoxId == null) return false;

            // A liquid container is also a displayable item, but its right-click can
            // pour/fill the precise contained item. Never hide that target in favour
            // of the floor pad while deciding whether the held container can be placed.
            if (held.Itemstack?.Collectible is ILiquidInterface) return false;

            // Slot ids are local to their owning display. A shelf above this stand can
            // use the same ids for different items; its placement must not hide ours.
            // GUARDED (2026-10-12): resolving a SHARED filler asks each owner for its boxes, whose
            // GetSelectionBoxes asks this again - without the guard that never ends (two External
            // Shutters open into one gap, anything in hand = stack overflow). Inside, "not placing".
            if (inPlacingPreviewCheck) return false;
            inPlacingPreviewCheck = true;
            try { sel = WindowSelection(capi.World.BlockAccessor, sel); }
            finally { inPlacingPreviewCheck = false; }
            if (sel?.Position == null || !sel.Position.Equals(Pos)) return false;

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
            // FREE STACKING (1.3.1): ANY displayable item can go on top of another, so the item boxes are exposed for
            // any held item, not only "Stacking" ones (Calm, 2026-10-19: "free form stacking on anything").
            if ((FreeStackingEnabled || dattr.Behavior == EnumDisplayableBehavior.Stacking)
                && GetCollidingSlotId(loc, new Cuboidf(dattr.Size)) != null)
            {
                return false;
            }

            return true;
        }

        /// <summary>This block's own collision, plus the pieces of other windows' leaves open into it (Guests).</summary>
        public Cuboidf[] GetCollisionBoxes()
        {
            Cuboidf[] cached = _collisionWithGuests;
            if (cached != null) return cached;
            Cuboidf[] own = OwnCollisionBoxes();
            var guestPieces = new List<Cuboidf>();
            foreach (BEUniversalDisplay guest in Guests()) if (!guest._removed) guestPieces.AddRange(guest.AutoCollisionAt(Pos));
            cached = guestPieces.Count == 0 ? own : own.Concat(guestPieces).ToArray();
            _collisionWithGuests = cached;
            return cached;
        }

        /// <summary>What this block takes up for another window's room check: its own collision and every guest's piece but that window's.</summary>
        public Cuboidf[] CollisionExcept(BlockPos asker)
        {
            IEnumerable<Cuboidf> all = OwnCollisionBoxes();
            foreach (BEUniversalDisplay guest in Guests())
                if (!guest._removed && !guest.Pos.Equals(asker)) all = all.Concat(guest.AutoCollisionAt(Pos));
            return all.ToArray();
        }

        private Cuboidf[] _collisionWithGuests;

        private Cuboidf[] OwnCollisionBoxes()
        {
            // The window's own piece of its measured box (AutoBoxes Kit); fillers hold the rest
            Cuboidf[] auto = AutoCollisionAt(Pos);
            if (autoLayout.Collision.TryGetValue((0,0,0),out var nativeLocal) && nativeLocal.Length > 0) return auto;
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
            result.AddRange(ItemCollisionAt(Pos));
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
            // An item selection identifies a column exactly. A wider held item can also
            // overlap a neighbouring column; dictionary order must not redirect stacking
            // there while the preview and contained interaction target the aimed bowl.
            if (at.IsPlacedItem && inv.SlotsByslotId.TryGetValue(at.Encoded, out ItemSlot aimed)
                && aimed is { Empty: false }) return at.Encoded;
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
                // TryGetValue, not inv[...]: the indexer CREATES a slot for an id that is not there
                if (inv.SlotsByslotId.TryGetValue(direct, out ItemSlot directSlot) && directSlot is { Empty: false }) return direct;
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
            // Some forwarding/game interaction paths supply an index without its ID.
            // Resolve it once before deciding between contained use, taking and stacking.
            if (blockSel != null && blockSel.SelectionBoxId == null)
            {
                blockSel = blockSel.Clone();
                blockSel.SelectionBoxId = AimedBoxId(blockSel);
            }
            // These parts stop the ray but have no action. Consume the click before it can
            // fall through to pane toggles, display placement, swapping or held-item use.
            if (IsHiddenPartBox(AimedBoxId(blockSel))) return true;

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

            // Another window's leaf open into this block (Guests): the click is that window's - open, shut, swap, all as on it
            if (GuestSelection(blockSel, AimedBoxId(blockSel), out BlockSelection forwarded))
                return world.BlockAccessor.GetBlock(forwarded.Position).OnBlockInteractStart(world, byPlayer, forwarded);

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
                           || boxId == WindowBoxId
                           || boxId.StartsWith(PartBoxPrefix, StringComparison.Ordinal)
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

                // a fixed part (frame edge, fixed glass): only the wrench (above) and breaking do anything
                if (boxId != null && boxId.StartsWith(PartBoxPrefix, StringComparison.Ordinal)) return true;

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

                if (world.Side == EnumAppSide.Server)
                {
                    if (boxId == WindowBoxId) ToggleWindow(byPlayer);
                    else TogglePane(paneIndex, byPlayer);
                }
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
            if (SuppressPlacement(held, byPlayer)) return false;
            string category = surface.DisplayCategory ?? "shelf";
            DisplayableAttributes dattr = BlockBehaviorDisplaySurfaces.GetDisplayableAttributes(held, category);
            if (dattr == null) return false;
            if (FreeStackingEnabled && loc.IsPlacedItem)
                return TryPutFreeStack(byPlayer, blockSel, loc, dattr);

            Size3i surfaceSize = surface.Size;

            // The player may have turned the item before placing it, which swaps width and
            // length on a quarter turn — that is the whole point, letting a long item be
            // fitted across a sill it will not fit along. Every check below therefore works
            // on the TURNED footprint, and the same angle is stored on the slot afterwards
            // so the model, the box and the collision all agree from the first frame.
            int placeRot = PlacementRotation.ForSurface(Api, byPlayer, this, surface);

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
                if (baseAttr != null && !PlacementRotation.HasPending(Api, byPlayer)) placeRot = (int)Math.Round(ChosenRotationDeg(loc.Encoded));
            }

            Size3f itemSize = BlockBehaviorDisplaySurfaces.RotatedFootprint(
                dattr.Size, placeRot + SurfaceFacingDeg(surface));

            Size3f fitSize = FitSize(itemSize); // the checks below give half a voxel on every side (lib 1.3.1)
            if (surfaceSize.Width < fitSize.Width || (!UnboundedStacking && surfaceSize.Height < fitSize.Height) || surfaceSize.Length < fitSize.Length)
            {
                (Api as ICoreClientAPI)?.TriggerIngameError(this, "toolarge", Lang.Get("shelfhelp-toolarge-error"));
                return true;
            }

            Vec3f offset = BlockBehaviorDisplaySurfaces.GetCentreOffset(fitSize);
            if (loc.X + offset.X < 0f || loc.Z + offset.Z < 0f
                || loc.X + offset.X > surfaceSize.Width - fitSize.Width
                || loc.Z + offset.Z > surfaceSize.Length - fitSize.Length)
            {
                (Api as ICoreClientAPI)?.TriggerIngameError(this, "outofbounds", Lang.Get("shelfhelp-outofbounds-error"));
                return true;
            }

            string collidingSlotId = GetCollidingSlotId(loc, new Cuboidf(fitSize));
            if (collidingSlotId != null)
            {
                // With free stacking a stack is made by aiming AT the item below (TryPutFreeStack); an aim at the surface
                // that would overlap an item is refused like any other overlap, as the ghost already shows it in red.
                if (!FreeStackingEnabled && dattr.Behavior == EnumDisplayableBehavior.Stacking) return TryStack(byPlayer, collidingSlotId, dattr, placeRot);

                // OVERLAP ALLOWED: the collision is DETECTED exactly as before — the stacking
                // branch above depends on it — it simply stops being a refusal. Turning the
                // detection off instead would end stacking altogether, which is the trap this
                // shape avoids.
                //
                // The one case still refused is a collision with THIS VERY SLOT. That is not
                // two items sharing space, it is the same slot id, and placing into it would
                // either merge silently into the stack already there or fail with no message.
                // ServerRules, not the local config: on a server this rule is the
                // server's, and asking our own copy is what stopped the "shelf full"
                // refusal firing at all - see ServerRules.
                if (!ServerRules.ItemOverlapAllowed
                    || collidingSlotId == loc.Encoded)
                {
                    (Api as ICoreClientAPI)?.TriggerIngameError(this, "shelffull", Lang.Get("shelfhelp-shelffull-error"));
                    return true;
                }
            }

            if (!EmbeddedPlacementFits(loc, dattr, placeRot, byPlayer)) return true;
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
            if (!UnboundedStacking && StackBaseY(cursor, surface, dattr) >= surface.Size.Height) return false;

            if (!StackTargetFits(cursor, surface, dattr, rotDeg, byPlayer)) return true;

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

            // The size it was placed at, kept on the item itself - see ItemScale.
            ItemScale.Set(target.Itemstack, PlacementScale.For(Api, byPlayer));

            // If this is the first chiselled block on a lockable surface, it becomes that
            // surface's permanent design. Here rather than in TryPut/TryStack because both
            // funnel through this method, so "first" is decided in exactly one place.
            ClaimLockIfFirst(targetSlotId, target);

            // One turn, one placement. Stacking inherits from the item below, so a pile is
            // still uniform without pressing the key again.
            PlacementRotation.ResetAfterPlacing(Api, byPlayer, Pos, targetSlotId);

            // The nudge is NOT inherited when stacking, unlike the angle — see
            // PlacementHeight.ResetAfterPlacing for why.
            PlacementHeight.ResetAfterPlacing(Api, byPlayer);
            PlacementScale.ResetAfterPlacing(Api, byPlayer);

            OnContentsChanged();

            DisplayItemSounds.Play(Api.World, DisplayItemSounds.Resolve(target.Itemstack, false), byPlayer.Entity);
            Api.World.Logger.Audit("{0} put 1x{1} into window storage at {2}, slot {3}.",
                byPlayer.PlayerName, target.Itemstack?.Collectible.Code, Pos, targetSlotId);
            return true;
        }

        protected bool TryTake(IPlayer byPlayer, BlockSelection blockSel)
        {
            string slotId = GetSelectedNonEmptySlotId(blockSel);
            if (slotId == null) return false;
            if (RefuseFreeSupportEdit(slotId)) return true;
            bool freeTaken = freeStacks.ContainsKey(slotId);
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
            ItemScale.Strip(taken);   // its display size stays behind, so it stacks in an inventory as before
            var pickupSound = DisplayItemSounds.Resolve(taken, true);
            if (!byPlayer.InventoryManager.TryGiveItemstack(taken) && taken != null && taken.StackSize > 0)
            {
                Api.World.SpawnItemEntity(taken, Pos);
            }
            if (taken != null) DisplayItemSounds.Play(Api.World, pickupSound, byPlayer.Entity);

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
            if (freeTaken) ForgetFreeStack(slotId);
            if (!freeTaken)
            {
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
            }

            OnContentsChanged();
            RemoveIfEmpty();
            return true;
        }

        /// <summary>
        /// Server: a block whose JSON sets <c>removeWhenEmpty</c> goes once its last item is taken - the ground pile
        /// (Universal Ground Storage) leaves no empty block behind, as the game's own ground storage does not.
        /// </summary>
        private void RemoveIfEmpty()
        {
            if (IsEmbedded) return;
            if (Api?.Side != EnumAppSide.Server || !inv.Empty) return;
            if (Block?.Attributes?["removeWhenEmpty"].AsBool(false) != true) return;
            Api.World.BlockAccessor.SetBlock(0, Pos);
        }

        /// <summary>
        /// Places the player's held item at a point on this block's surfaces, exactly as a click on the placement pad
        /// under that point would - claims, fit, stacking and the chosen angle included. For another mod's code that
        /// has just put the block down and wants the item that started it placed where the player aimed
        /// (Universal Ground Storage). <paramref name="hitInBlock"/> is in block units, 0-1 on each axis.
        /// </summary>
        public bool TryPutAt(IPlayer byPlayer, Vec3d hitInBlock)
        {
            if (byPlayer == null) return false;
            BlockSelection sel = PadSelectionAt(hitInBlock);
            return sel != null && OnBlockInteractStart(Api.World, byPlayer, sel);
        }

        public bool TryPutAt(IPlayer byPlayer, Vec3d hitInBlock, int surfaceIndex)
        {
            if (byPlayer == null) return false;
            var selection = PadSelectionAt(hitInBlock, surfaceIndex);
            return selection != null && OnBlockInteractStart(Api.World, byPlayer, selection);
        }

        /// <summary>A selection naming the placement pad under a point of this block (0-1 units, x and z), or null.</summary>
        public BlockSelection PadSelectionAt(Vec3d hitInBlock)
            => PadSelectionAt(hitInBlock, null);

        public BlockSelection PadSelectionAt(Vec3d hitInBlock, int? surfaceIndex)
        {
            BlockBehaviorDisplaySurfaces bh = SurfaceBehavior;
            if (bh == null || hitInBlock == null) return null;
            foreach (CuboidfWithId r in RotatedPads(bh, surfaceIndex ?? -1))
            {
                if (hitInBlock.X < r.X1 || hitInBlock.X >= r.X2 || hitInBlock.Z < r.Z1 || hitInBlock.Z >= r.Z2) continue;
                return new BlockSelection
                {
                    Position = Pos.Copy(),
                    HitPosition = hitInBlock.Clone(),
                    Face = BlockFacing.UP,
                    SelectionBoxId = r.Id
                };
            }
            return null;
        }

        /// <summary>
        /// A surface's pads (-1: every surface) turned with this block - KEPT, not rebuilt per call (Calm 2026-10-22: the
        /// ghost lagged holding a hammer or knife over a chiselled block or bare ground). PadSelectionAt is asked twice a
        /// frame there (the ghost and the outline), and turning every pad of a carved floor's 1,024 on each call made
        /// hundreds of thousands of boxes a second for the garbage collector. Rebuilt when the turn, the grids
        /// (GridGeneration) or the block's behaviour change. Read on the main thread; a race only builds a list twice.
        /// </summary>
        private CuboidfWithId[] RotatedPads(BlockBehaviorDisplaySurfaces bh, int surfaceIndex)
        {
            RotatedPadCache cache = _rotatedPads;
            if (cache == null || cache.Angle != MeshAngleRad || cache.Generation != BlockBehaviorDisplaySurfaces.GridGeneration
                || !ReferenceEquals(cache.Behavior, bh))
                _rotatedPads = cache = new RotatedPadCache(MeshAngleRad, BlockBehaviorDisplaySurfaces.GridGeneration, bh);
            return cache.BySurface.GetOrAdd(surfaceIndex, index =>
                (index >= 0 ? bh.GridBoxesForSurface(index) : bh.GridBoxes).Select(pad => Rotated(pad, pad.Id)).ToArray());
        }

        private sealed record RotatedPadCache(float Angle, int Generation, BlockBehaviorDisplaySurfaces Behavior)
        {
            public readonly ConcurrentDictionary<int, CuboidfWithId[]> BySurface = new();
        }

        private volatile RotatedPadCache _rotatedPads;

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
            if (FreeData(slotId) != null) return 0f;
            if (JitterSuppressed) return 0f;
            if (dattr == null || dattr.RandYRotAngle <= 0) return 0f;

            return GameMath.MurmurHash3Mod(Pos.X, Pos.Y + StableHash(slotId), Pos.Z,
                       dattr.RandYRotAngle + 1) - dattr.RandYRotAngle / 2;
        }

        /// <summary>
        /// FNV-1a of a slot id - the same number in every game process. string.GetHashCode is NOT: .NET seeds it
        /// differently each launch, so the jitter angle it fed changed on every restart and could differ between a
        /// server and its clients (fixed 2026-10-22). Same reasoning as WornShapeMesh.Fnv and StartFloat.
        /// </summary>
        private static int StableHash(string s)
        {
            uint h = 2166136261;
            foreach (char c in s ?? "") { h ^= c; h *= 16777619; }
            return (int)h;
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
            if (freeStacks.ContainsKey(slotId))
            {
                var freeLoc=DisplaySlotId.Decode(slotId);
                var plane=SurfaceBehavior?.GetSurface(freeLoc.SurfaceIndex);
                if (plane == null || !inv.SlotsByslotId.TryGetValue(slotId, out ItemSlot freeSlot)) return false;
                var props=BlockBehaviorDisplaySurfaces.GetDisplayableAttributes(freeSlot,plane.DisplayCategory);
                return FreePlacementFits(freeLoc,props,rotDeg,ChosenHeightVoxels(slotId),null);
            }
            if (IsEmbedded) return EmbeddedEditFits(slotId, rotDeg, null, null);
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
                   || ServerRules.ItemOverlapAllowed;
        }

        /// <summary>
        /// Entry point for the mouse-wheel path, which arrives as a packet rather than
        /// through OnBlockInteractStart. Same code as the wrench so the two cannot behave
        /// differently — including refusing when the turn would not fit.
        /// </summary>
        /// <param name="stepDeg">
        /// The step the player asked for, passed straight through to TryRotateItem, which
        /// bounds it. 0 means "not supplied" and falls back to this side's config.
        /// </param>
        public bool RotateStoredItem(string slotId, IPlayer byPlayer, bool reverse, float stepDeg = 0f)
        {
            if (IsEmbedded && byPlayer != null && !Api.World.Claims.TryAccess(byPlayer, Pos, EnumBlockAccessFlags.Use)) return false;
            // TryGetValue, not inv[...]: the id comes from a client packet, and the indexer would CREATE a slot for it
            if (slotId == null || !inv.SlotsByslotId.TryGetValue(slotId, out ItemSlot stored) || stored is not { Empty: false }) return false;

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

            return TryRotateItem(slotId, byPlayer, reverse, stepDeg);
        }

        /// <summary>
        /// Raise or lower an item already on a surface, from the sneak+wheel packet.
        ///
        /// Normal shelves retain their free height nudge within the configured limit.
        /// Embedded storage additionally checks the real floor, walls, roof and items.
        /// </summary>
        /// <param name="stepVoxels">
        /// The step the PLAYER asked for, from their own config, arriving in the packet.
        /// Bounded here to the same range the config itself allows - the step is a
        /// preference, but it is still a number off the network and 0 or 1e9 would be as
        /// broken as it sounds. 0 or less means the sender said nothing, so fall back to
        /// this side's config, which is what an older client will do.
        /// </param>
        public bool AdjustStoredItemHeight(string slotId, bool down, float stepVoxels = 0f)
            => AdjustStoredItemHeight(slotId, down, stepVoxels, null);

        public bool AdjustStoredItemHeight(string slotId, bool down, float stepVoxels, IPlayer player)
        {
            if (RefuseFreeSupportEdit(slotId)) return false;
            if (slotId == null) return false;
            if (!inv.SlotsByslotId.TryGetValue(slotId, out ItemSlot slot) || slot == null || slot.Empty)
            {
                return false;
            }

            // The nudge half of the lock, server-side for the reason RotateStoredItem gives.
            if (IsSlotLocked(slotId)) return false;

            customHeightBySlot ??= new Dictionary<string, float>();
            customHeightBySlot.TryGetValue(slotId, out float current);

            float step = stepVoxels > 0f ? Math.Clamp(stepVoxels, 0.05f, 4f) : PlacementHeight.Step;

            float wanted = PlacementHeight.Clamp(current + (down ? -step : step));
            if (freeStacks.ContainsKey(slotId))
            {
                var freeLoc=DisplaySlotId.Decode(slotId);
                var plane=SurfaceBehavior?.GetSurface(freeLoc?.SurfaceIndex ?? -1);
                if (plane == null) return false;
                var props=BlockBehaviorDisplaySurfaces.GetDisplayableAttributes(slot,plane.DisplayCategory);
                if (!FreePlacementFits(freeLoc,props,ChosenRotationDeg(slotId),wanted,player)) return true;
            }
            if (IsEmbedded && !freeStacks.ContainsKey(slotId) && !EmbeddedEditFits(slotId, null, wanted, player)) return true;

            // An entry of exactly 0 is the same as no entry, so drop it rather than saving a
            // key that says "unchanged" — nudging an item back to where it started should
            // leave the block as it was before anyone touched it.
            if (wanted == 0f) customHeightBySlot.Remove(slotId);
            else customHeightBySlot[slotId] = wanted;
            if (IsEmbedded) RefreshEmbeddedEffects();

            // Boxes with the meshes, for the reason spelled out in TryRotateItem: they are
            // derived from item state, so a stale set outlives the change and the item
            // becomes unclickable where it now is.
            InvalidateBoxes();
            MarkMeshesDirty();
            MarkDirty(true);
            return true;
        }

        /// <summary>
        /// Size an item already on a surface up or down one step, from the Resize key + wheel packet (lib 1.4.0).
        /// Refused, like raising, while something rests on it; refused when the new size does not fit where it sits -
        /// the size is tried on the item and the same check a turn uses decides, then put back if it fails.
        /// </summary>
        public bool AdjustStoredItemScale(string slotId, bool down, IPlayer player)
        {
            if (RefuseFreeSupportEdit(slotId)) return false;
            if (slotId == null || !inv.SlotsByslotId.TryGetValue(slotId, out ItemSlot slot) || slot == null || slot.Empty) return false;
            if (IsSlotLocked(slotId)) return false;
            if (IsEmbedded && player != null && !Api.World.Claims.TryAccess(player, Pos, EnumBlockAccessFlags.Use)) return false;

            float current = ItemScale.Of(slot.Itemstack);
            float wanted = PlacementScale.Clamp(current + (down ? -PlacementScale.Step : PlacementScale.Step));
            if (wanted == current) return true;

            ItemScale.Set(slot.Itemstack, wanted);
            bool fits;
            if (freeStacks.ContainsKey(slotId))
            {
                var freeLoc = DisplaySlotId.Decode(slotId);
                var plane = SurfaceBehavior?.GetSurface(freeLoc?.SurfaceIndex ?? -1);
                var props = plane == null ? null : BlockBehaviorDisplaySurfaces.GetDisplayableAttributes(slot, plane.DisplayCategory);
                fits = props != null && FreePlacementFits(freeLoc, props, ChosenRotationDeg(slotId), ChosenHeightVoxels(slotId), player);
            }
            else fits = IsEmbedded ? EmbeddedEditFits(slotId, null, null, player) : RotationFits(slotId, ChosenRotationDeg(slotId));

            if (!fits)
            {
                ItemScale.Set(slot.Itemstack, current);
                return true;   // handled: refusing is a result
            }

            if (IsEmbedded) RefreshEmbeddedEffects();
            InvalidateBoxes();
            MarkMeshesDirty();
            MarkDirty(true);
            return true;
        }

        /// <param name="stepDeg">
        /// The step the PLAYER asked for, bounded here for the reason AdjustStoredItemHeight
        /// gives. 0 or less falls back to this side's config.
        /// </param>
        private bool TryRotateItem(string slotId, IPlayer byPlayer, bool reverse, float stepDeg = 0f)
        {
            // Turning an item with others on top is allowed (Calm, 2026-10-19: "turning is fine").
            customRotationDegBySlot ??= new Dictionary<string, float>();

            float step = stepDeg > 0f
                ? Math.Clamp(stepDeg, 5f, 90f)
                : (UniversalDisplayLibConfig.Current?.RotationStepDegrees ?? 22.5f);

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
            if (!(freeStacks.ContainsKey(slotId) ? RotationFits(slotId,wanted)
                : IsEmbedded ? EmbeddedEditFits(slotId, wanted, null, byPlayer) : RotationFits(slotId, wanted)))
            {
                (Api as ICoreClientAPI)?.TriggerIngameError(this, "norotateroom",
                    Lang.Get("universaldisplaylib:rotate-noroom"));
                return true;      // handled: refusing is a result, not a fall-through
            }

            customRotationDegBySlot[slotId] = wanted;
            if (IsEmbedded) RefreshEmbeddedEffects();

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
        /// Sets one pane. A tall window is ONE block entity since lib 1.1.4 (the single-controller
        /// system in src/reach/), so there are no linked segments to keep in step any more - the
        /// LinkedOpenPacket that used to push a pane change to every segment's client ahead of the
        /// block entity sync is gone with the chains (2026-10-05).
        /// </summary>
        public void SetPaneState(int index, bool open)
        {
            if (paneStates == null || index < 0 || index >= paneStates.Length) return;
            if (paneStates[index] == open) return;

            paneStates[index] = open;
            // an open leaf has its own box, and one that swings into the blocks beside gets fillers there
            AutoBoxesAngleChanged();
            MarkMeshesDirty();
            MarkDirty(true);
            RaisePaneChanged(this, index, open);
            if (open) ScheduleAutoClose(index);

            bool roomSafe = ServerRules.RoomSafeOpening;
            FrameBoxGroup[] groups = SurfaceBehavior?.FrameBoxGroups;
            if (groups != null && index < groups.Length && groups[index].SealsRoom && !roomSafe)
            {
                SyncStateVariant();
            }
        }

        /// <summary>
        /// Server: a click on the window's one measured box. Every pane moves together: the shut ones open if
        /// any pane is shut, otherwise all close (step 1 of the AutoBoxes rebuild - one box, not one per leaf).
        /// </summary>
        public void ToggleWindow(IPlayer byPlayer = null)
        {
            FrameBoxGroup[] groups = SurfaceBehavior?.FrameBoxGroups ?? Array.Empty<FrameBoxGroup>();
            if (paneStates.Length < groups.Length) ClampPaneStatesToBlock();
            int[] panes = Enumerable.Range(0, Math.Min(groups.Length, paneStates.Length))
                .Where(i => !string.IsNullOrEmpty(groups[i].AnimOpen)).ToArray();
            bool anyShut = panes.Any(i => !paneStates[i]);
            foreach (int i in panes)
            {
                if (paneStates[i] != anyShut) TogglePane(i, byPlayer);
            }
        }

        public void TogglePane(int index, IPlayer byPlayer = null)
        {
            FrameBoxGroup[] groups = SurfaceBehavior?.FrameBoxGroups;
            if (groups == null || index < 0 || index >= groups.Length) return;

            FrameBoxGroup group = groups[index];
            // A group with only static boxes has nothing to open - one naming an animation does,
            // boxes or not (a measured window's JSON has none)
            if (group.OpenFrameBox == null && group.ClosedFrameBox == null && string.IsNullOrEmpty(group.AnimOpen)) return;

            if (index >= paneStates.Length) ClampPaneStatesToBlock();
            if (index >= paneStates.Length) return;

            // Opening: the full swing if every block it reaches has room, else the pane's fallback (90 degrees),
            // else not at all. Server only, like this method.
            if (!paneStates[index] && Api?.Side == EnumAppSide.Server)
            {
                var fallback = Block?.GetBehavior<Reach.BlockBehaviorUDAutoBoxes>()?.FallbackFor(index);
                if (HasRoomToOpen(index, group.AnimOpen, byPlayer)) SetFallback(index, false);
                else if (fallback != null && HasRoomToOpen(index, fallback.Value.open, byPlayer)) SetFallback(index, true);
                else
                {
                    (byPlayer as IServerPlayer)?.SendIngameError("reach-blocked", Lang.Get("universaldisplaylib:reach-blocked"));
                    return;
                }
            }
            // Closing: not through another window's open leaf (an External Shutter over an open classic window)
            else if (paneStates[index] && Api?.Side == EnumAppSide.Server && OpenNeighbourInTheWay(index))
            {
                (byPlayer as IServerPlayer)?.SendIngameError("reach-closeblocked", Lang.Get("universaldisplaylib:reach-closeblocked"));
                return;
            }

            // One block entity for the whole window, tall or not: the block entity sync carries
            // the pane state and how far it opened together, so the client animates once.
            SetPaneState(index, !paneStates[index]);

            // The open/closed variant sync lives in SetPaneState, so every path that
            // changes a pane keeps it in step. The sound is each client's (DisplaySoundHandler.PlaySlideSound), so
            // every player's own volume setting applies.
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

            // a shut External Shutter is dark - its fillers in its own upper blocks follow (PartOfOwnerLight)
            ((relightAccessor.GetBlockEntity(Pos) as BEUniversalDisplay) ?? this).MatchFillerLight();
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
        /// materials (UDMaterials, formerly ARL's variants) textured it. Same for the open and closed block variants, so
        /// toggling a pane does not rebuild it.
        ///
        /// The variants matter because the animation shapes declare a default glass
        /// texture (leaded) that the materials overwrite (MaterialTextureSource) — but the materials
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
            string variantKey = GetBehavior<BEBehaviorMaterials>()?.Key ?? "";
            return "block:" + Block.Shape?.Base + "|" + variantKey;
        }

        /// <summary>
        /// Whether this block's materials (wood, glass...) are known yet.
        ///
        /// They arrive in OnBlockPlaced or FromTreeAttributes, both of which run
        /// *after* Initialize. Building the animator or the block mesh before then bakes
        /// the shape's default textures in, and the corrected rebuild a frame or two
        /// later is the visible pop from generic to matching material.
        ///
        /// The attempt budget stops a block placed without materials (a stack with no
        /// `types`) from staying invisible forever - it draws in the shape's own textures.
        /// </summary>
        private bool MaterialsReady
        {
            get
            {
                var materials = GetBehavior<BEBehaviorMaterials>();
                if (materials == null) return true;
                if (materials.Types.Count > 0) return true;
                return _variantWaitTicks > MaxVariantWaitTicks;
            }
        }

        private const int MaxVariantWaitTicks = 8;   // ~2s at the 250ms client tick
        private int _variantWaitTicks;

        private bool EnsureAnimatorCurrent()
        {
            if (capi == null || Block == null) return false;

            // NOTHING TO OPEN, NO ANIMATOR (code check 2026-10-22): shelves, piles and tables used to get one too - a
            // second copy of the model on the GPU and 4-5 renderers called every frame, never drawing anything.
            if (!Animates)
            {
                if (animUtil != null) { animUtil.Dispose(); animUtil = null; _animatorShapeKey = null; _animatorAngleRad = float.NaN; }
                return false;
            }

            if (!MaterialsReady) return false;

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

        /// <summary>Whether this block has a pane with open and close animations - the only thing the animator is for.
        /// Worked out once per block id: a wrench swap can change it.</summary>
        private bool Animates
        {
            get
            {
                if (_animatesBlockId != Block.Id)
                {
                    FrameBoxGroup[] groups = SurfaceBehavior?.FrameBoxGroups;
                    _animates = groups != null && groups.Any(g => !string.IsNullOrEmpty(g.AnimOpen) && !string.IsNullOrEmpty(g.AnimClose));
                    _animatesBlockId = Block.Id;
                }
                return _animates;
            }
        }

        private int _animatesBlockId = -1;
        private bool _animates;

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

                // opened to its fallback (90 degrees, no room for the full swing): that pair plays instead
                string animOpen = OpenAnimFor(i), animClose = CloseAnimFor(i);

                string target = paneStates[i] ? animOpen : animClose;
                string opposite = paneStates[i] ? animClose : animOpen;

                if (animUtil.activeAnimationsByAnimCode.ContainsKey(opposite)) animUtil.StopAnimation(opposite);
                foreach (string other in AllAnimsFor(i))
                {
                    if (other != target && other != opposite && animUtil.activeAnimationsByAnimCode.ContainsKey(other)) animUtil.StopAnimation(other);
                }

                if (!animUtil.activeAnimationsByAnimCode.ContainsKey(target))
                {
                    // Halloween: a haunted pane SLAMS shut - its close plays SlamSpeedFactor times faster
                    // (a rattling pane flaps both ways at that speed)
                    float paneSpeed = !snap && (_rattle || _slamFast && !paneStates[i]) ? speed * SlamSpeedFactor : speed;
                    // and a haunted pane CREEPS open (Calm: "the windows opening slowly then slamming")
                    if (!snap && !_rattle && _hauntOpenSeen == i && paneStates[i]) paneSpeed = speed * HauntOpenSpeedFactor;
                    // the pane's own speed (FrameBoxGroup.AnimSpeed) on top - a heavy door slower, a drawer quicker
                    if (!snap && group.AnimSpeed > 0f && group.AnimSpeed != 1f) paneSpeed *= group.AnimSpeed;
                    animUtil.StartAnimation(new AnimationMetaData
                    {
                        Animation = target,
                        Code = target,
                        AnimationSpeed = paneSpeed,
                        EaseInSpeed = 10000f / paneSpeed,
                        EaseOutSpeed = 10000f / paneSpeed,
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
                if (_floatingSlots.ContainsKey(kv.Key)) continue;   // Halloween: drawn on its own while it floats (FloatingItemRenderer)

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
                    if (mesh != null) MeasuredNew(slot, DisplayCategoryFor(kv.Key), mesh);
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
            // the item meshes exist now: outlines that fell back to the written size are measured again
            if (_itemBoxNeedsMesh)
            {
                _itemBoxNeedsMesh = false;
                _padOnlyBoxes = null;
                _allBoxes = null;
            }

            if (blockMesh != null && !IsEmbedded)
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
            => TryResolveStackTargetOn(aimed, heldAttr, out target, out rotDeg, out allowed, null, null);

        public bool TryResolveStackTargetOn(DisplaySlotId aimed, DisplayableAttributes heldAttr,
                                          out DisplaySlotId target, out float rotDeg, out bool allowed,
                                          BlockSelection selection = null, IPlayer player = null)
        {
            if (FreeStackingEnabled && selection != null)
                return TryResolveFreeStack(aimed, heldAttr, selection, player ?? (Api as ICoreClientAPI)?.World.Player,
                    out target, out rotDeg, out allowed);
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
            rotDeg = PlacementRotation.HasPending(Api, null) ? PlacementRotation.For(Api, null) : ChosenRotationDeg(aimed.Encoded);

            // Red rather than absent when it will not go: an item that does not stack, or a
            // pile that has run out of headroom, is exactly what the ghost exists to tell you.
            // Identical test to TryStack's, through the same StackBaseY, so the ghost cannot
            // redden while the placement succeeds or the other way about.
            allowed = heldAttr.Behavior == EnumDisplayableBehavior.Stacking
                      && (UnboundedStacking || StackBaseY(cursor, surface, heldAttr) < surface.Size.Height)
                      && StackTargetFits(cursor, surface, heldAttr, rotDeg);
            return true;
        }

        /// <summary>Client: a stored item's box as drawn - its cached mesh through its slot matrix - or null if not built yet.</summary>
        private Cuboidf DrawnItemBox(string slotId, ItemSlot slot, KeyValuePair<string, ItemSlot>[] slots)
        {
            MeshData mesh = GetCachedItemMesh(slot);
            if (mesh == null) return null;
            float[] matrix = BuildSlotMatrix(slotId, slot, out _, null, slots);
            return matrix == null ? null : MeshBounds(mesh, matrix);
        }

        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MeshData, float[]> meshExtents = new();

        /// <summary>Record a mesh's extent instead of measuring its vertices - a worn item drawn on an invisible seraph (ClothingExtent).</summary>
        internal static void SetMeshExtent(MeshData mesh, float[] extent) => meshExtents.AddOrUpdate(mesh, extent);

        /// <summary>
        /// The box round a mesh placed by <paramref name="matrix"/> (column-major, as the tesselator takes it): the mesh's own
        /// extent (measured once per mesh) with its eight corners put through the matrix. Null for an empty mesh.
        /// </summary>
        public static Cuboidf MeshBounds(MeshData mesh, float[] matrix)
        {
            if (mesh?.xyz == null || mesh.VerticesCount == 0 || matrix == null) return null;
            float[] e = meshExtents.GetValue(mesh, m =>
            {
                float[] b = { float.MaxValue, float.MaxValue, float.MaxValue, float.MinValue, float.MinValue, float.MinValue };
                for (int i = 0; i < m.VerticesCount; i++)
                    for (int a = 0; a < 3; a++)
                    {
                        float v = m.xyz[i * 3 + a];
                        if (v < b[a]) b[a] = v;
                        if (v > b[a + 3]) b[a + 3] = v;
                    }
                return b;
            });
            float[] lo = { float.MaxValue, float.MaxValue, float.MaxValue }, hi = { float.MinValue, float.MinValue, float.MinValue };
            foreach (float x in new[] { e[0], e[3] }) foreach (float y in new[] { e[1], e[4] }) foreach (float z in new[] { e[2], e[5] })
                for (int a = 0; a < 3; a++)
                {
                    float v = matrix[a] * x + matrix[4 + a] * y + matrix[8 + a] * z + matrix[12 + a];
                    if (v < lo[a]) lo[a] = v;
                    if (v > hi[a]) hi[a] = v;
                }
            return new Cuboidf(lo[0], lo[1], lo[2], hi[0], hi[1], hi[2]);
        }

        /// <summary>Client: the box round the placement ghost - the same mesh and matrix the ghost is drawn with.</summary>
        public Cuboidf GhostBox(string slotId, ItemSlot heldSlot, DisplayableAttributes dattr, float rotDeg)
        {
            // "outline": "size": null, so the caller draws the written footprint
            if (BlockBehaviorDisplaySurfaces.OutlineFromSize(heldSlot, DisplayCategoryFor(slotId))) return null;
            return TryBuildGhost(slotId, heldSlot, dattr, rotDeg, out MeshData mesh, out float[] matrix) ? MeshBounds(mesh, matrix) : null;
        }

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
            if (mesh != null) MeasuredItemSizes.Measure(Api as ICoreClientAPI, heldSlot, DisplayCategoryFor(slotId), mesh, IsStandInMesh(heldSlot));
            return mesh != null;
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
            // The item's size (the Resize key, lib 1.4.0) round its own bottom centre, after its turn - dattr.Size is
            // already the sized one, so the centring above matches.
            float scale = ItemScale.For(slot);
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
                .Scale(scale, scale, scale)
                .Translate(-0.5f, 0f, -0.5f)
                .Values;
        }

        private void GenerateMeshes()
        {
            if (SurfaceBehavior == null) return;

            // Leaving meshesGenerated false means the next frame retries, so the first
            // mesh anyone sees is already textured with the right wood and glass
            if (!MaterialsReady) return;

            // Built on the tesselation thread: a material texture not in the atlas yet is put
            // there on the main thread, which then redraws this block - so wait for it rather
            // than bake in a stand-in.
            var materialsBlock = Block.GetBehavior<BlockBehaviorMaterials>();
            var materialsBe = GetBehavior<BEBehaviorMaterials>();
            if (materialsBlock != null && materialsBe != null && !materialsBlock.TexturesReady(capi, materialsBe.Types, Pos)) return;

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

                MeshData built = GetOrCreateItemMesh(kv.Value, dattr);
                if (built != null) MeasuredNew(kv.Value, DisplayCategoryFor(kv.Key), built);
                TfMatrices[kv.Key] = matrix;
            }

            meshesGenerated = true;
        }

        /// <summary>
        /// Static frame mesh, built with the block's materials (UDMaterials) so its wood /
        /// glass textures apply. psurface marker elements are stripped, and the shape's own
        /// IgnoreElements with them.
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

            var materialsBlock = Block.GetBehavior<BlockBehaviorMaterials>();
            var materialsBe = GetBehavior<BEBehaviorMaterials>();
            CompositeShape cshape = (Block.Attributes?["shape"].AsObject<CompositeShape>() ?? Block.Shape);
            if (cshape?.Base == null) return null;

            if (materialsBlock != null && materialsBe != null)
            {
                // One mesh per shape + materials, shared by every window like it (ARL cached the same way).
                var cache = ObjectCacheUtil.GetOrCreate(capi, "universaldisplaylib-materialblockmeshes", () => new System.Collections.Concurrent.ConcurrentDictionary<string, MeshData>());
                string key = Block.Id + "|" + cshape.Base + "|" + materialsBe.Key + "|" + string.Join(",", ignore);
                if (cache.TryGetValue(key, out var cached)) return cached.Clone();

                Shape materialShape = capi.TesselatorManager.GetCachedShape(cshape.Base)
                    ?? Shape.TryGet(capi, cshape.Base.Clone().WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json"));
                if (materialShape == null) return null;
                var source = new MaterialTextureSource(capi, Block, materialsBlock, materialsBe.Types, materialShape, Pos);
                capi.Tesselator.TesselateShape(new TesselationMetaData
                {
                    TexSource = source,
                    IgnoreElements = ignore
                }, materialShape, out MeshData materialMesh);
                if (source.Waiting) return null;   // a texture is still being put in the atlas - this block is redrawn then
                cache[key] = materialMesh;
                return materialMesh.Clone();
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
            var materialsBlock = Block.GetBehavior<BlockBehaviorMaterials>();
            var materialsBe = GetBehavior<BEBehaviorMaterials>();

            if (materialsBlock == null || materialsBe == null) return capi.Tesselator.GetTextureSource(Block);

            // The animator is built on the main thread, so any texture not in the atlas yet goes straight in.
            return new MaterialTextureSource(capi, Block, materialsBlock, materialsBe.Types, shape, Pos);
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
            // an item drawn from its worn shape: which look, and whether it is built yet (WornShapeMesh)
            string worn = WornShapeMesh.MeshKeyPart(slot.Itemstack);
            return category + "|" + itemKey + (worn != null ? "|" + worn : "");
        }

        private MeshData GetCachedItemMesh(ItemSlot slot)
        {
            MeshCache.TryGetValue(GetItemMeshCacheKey(slot), out MeshData mesh);
            return mesh;
        }

        /// <summary>Display category of the surface a given slot id sits on.</summary>
        /// <summary>
        /// Client, any thread: measure a stored item's display mesh (MeasuredItemSizes). A NEW size changes how high things
        /// stack and what fits, so this window redraws once on the main thread with it.
        /// </summary>
        private void MeasuredNew(ItemSlot slot, string category, MeshData mesh)
        {
            if (Api is not ICoreClientAPI capi || !MeasuredItemSizes.Measure(capi, slot, category, mesh, IsStandInMesh(slot))) return;
            capi.Event.EnqueueMainThreadTask(() =>
            {
                InvalidateBoxes();
                MarkMeshesDirty();
                Api?.World.BlockAccessor.MarkBlockDirty(Pos);
            }, "universaldisplaylib-measuredredraw");
        }

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

            // THE KEY IS FIXED BEFORE ANYTHING IS BUILT (2026-10-10). Worked out afterwards, a mesh source that finished in
            // between (a figurine, a worn-shape backpack - their keys drop "|wait" once built) had its stand-in stored under
            // the FINISHED key, and kept for the session. Now a stand-in is stored under the "|wait" key it was asked for.
            string cacheKey = GetItemMeshCacheKey(slot);
            if (MeshCache.TryGetValue(cacheKey, out MeshData cached) && cached != null) return cached;

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

            // Drawn from a display shape (a folded garment, from a patch or the game's own shelf entry): never on the seraph,
            // so the correction below is not even tried (it also decides by the model itself - ClothingExtent).
            bool fromDisplayShape = mesh != null;

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
            IContainedMeshSource source = collectible?.GetCollectibleInterface<IContainedMeshSource>();
            MeshData shared = source?.GenMesh(slot, capi.BlockTextureAtlas, Pos);
            // It has a mesh source that gave nothing (yet): the ordinary model below stands in, and so does its size
            bool standIn = mesh == null && source != null && shared == null;
            mesh ??= shared?.Clone();

            // No display shape and no mesh source, but a worn shape for this very stack (Modular Backpacks' pouches):
            // drawn from that. Until it is built the plain model below stands in, like a late mesh source.
            bool worn = false;
            if (mesh == null && source == null && customShape == null && WornShapeMesh.Applies(collectible))
            {
                mesh = WornShapeMesh.TryGet(capi, slot.Itemstack, Pos, out bool pending);
                worn = mesh != null;
                if (pending) standIn = true;
            }

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

                    // THE BLANKET FILL IS DELIBERATE - tried the alternative 2026-09-25 and backed
                    // it out. Keeping a shape's own renderPass made vanilla's Artistic bottle show
                    // its glass, but ~200 item shapes mark passes, and the crystal skull in the
                    // Transparent pass showed all its insides through the crystal. Forced opaque
                    // is what every item has always looked like on a sill; the bottle's solid
                    // glass is a documented known issue instead.
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

            // a worn item's own extent, not the invisible seraph it is built on (ClothingExtent, 2026-10-07) - ONLY for a
            // garment really drawn on the seraph (boots, clothing with no folded model; ClothingExtent checks the model).
            // A folded shirt took the size of the shirt as worn and floated where a torso would be (Calm, 2026-10-10). Not
            // for a worn-shape mesh either: the wearer's body is already out of it, its own bounds are right.
            if (mesh != null && !worn && !fromDisplayShape) ClothingExtent.Register(capi, slot, dattr?.Transform, mesh);

            MeshCache[cacheKey] = mesh;
            if (standIn) StandInMeshes[cacheKey] = 0;
            else StandInMeshes.TryRemove(cacheKey, out _);
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
            ReadFreeStacks(tree);
            EmbeddedFloorVoxels = tree.GetFloat("embeddedFloor");
            base.FromTreeAttributes(tree, worldForResolving);

            bool[] previousPanes = paneStates != null ? (bool[])paneStates.Clone() : null;

            MeshAngleRad = tree.GetFloat("meshAngleRad");
            bool wasSlamFast = _slamFast;
            _slamFast = tree.GetBool("hauntSlam");
            _rattle = tree.GetBool("hauntRattle");
            string floatSlot = tree.GetString("hauntFloat", null);
            int floatSeq = tree.GetInt("hauntFloatSeq", 0);
            int floatMs = tree.GetInt("hauntFloatMs", 0);
            if (capi != null && floatSlot != null && floatSeq != _floatSeqSeen && floatMs > 0)
            {
                // several items: each lifts (and lands) FloatStaggerMs after the one before
                string[] floatSlots = floatSlot.Split('|');
                for (int f = 0; f < floatSlots.Length; f++)
                {
                    string one = floatSlots[f];
                    int seq = floatSeq;
                    capi.Event.RegisterCallback(_ => StartFloat(one, floatMs, seq), f * FloatStaggerMs);
                }
            }
            _floatSeqSeen = floatSeq;
            int hauntOpen = tree.GetInt("hauntOpen", -1);
            // at once (Calm: "the squeak is too delayed"); only where to cut it off waits for the rest of this update
            if (capi != null && hauntOpen >= 0 && _hauntOpenSeen < 0) PlayHauntCreak(hauntOpen);
            _hauntOpenSeen = hauntOpen;

            string states = tree.GetString("paneStates", "");
            if (!string.IsNullOrEmpty(states))
            {
                paneStates = new bool[states.Length];
                for (int i = 0; i < states.Length; i++) paneStates[i] = states[i] == '1';
            }

            autoHeld = AutoBoxes.Read(tree.GetString("autoboxesHeld"));
            paneFallback = tree.GetString("paneFallback", "").Select(c => c == '1').ToArray();
            if (Api != null) AutoBoxesMeasureTurned();

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

            if (IsEmbedded) { QueueEmbeddedMeshes(); QueueEmbeddedEffects(); }
            if (!IsEmbedded && worldForResolving.Side == EnumAppSide.Client && Api != null)
            {
                // Publish after the authoritative inventory has been read, then queue
                // relight with the light-aware getter already ready for the worker.
                RefreshStoredLight();
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

                // Halloween: the snap - a swoosh as a haunted pane starts to slam, cut off as it lands (PlaySnapSwoosh)
                if (_slamFast && !wasSlamFast && panesChanged && capi != null && previousPanes != null)
                    for (int i = 0; i < paneStates.Length && i < previousPanes.Length; i++)
                        if (previousPanes[i] && !paneStates[i]) { PlaySnapSwoosh(i); break; }
                // Halloween: the creak stops the moment its pane shuts
                if (_creak != null && (_creakPane < 0 || _creakPane >= paneStates.Length || !paneStates[_creakPane])) StopHauntCreak();
                // Halloween: a haunted open (creak) or slam has its own sounds - no ordinary open/close sound under them
                if (panesChanged && hauntOpen < 0 && !_slamFast && !_rattle) PlayOpenCloseSound(previousPanes);
                // for code mods (PaneChanged): every pane that moved, as each client sees it
                if (panesChanged && previousPanes != null)
                    for (int i = 0; i < paneStates.Length && i < previousPanes.Length; i++)
                        if (previousPanes[i] != paneStates[i]) RaisePaneChanged(this, i, paneStates[i]);
                soundHandler?.FadeSharedSoundsToCurrentTarget();
            }
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            WriteFreeStacks(tree);
            if (IsEmbedded) tree.SetFloat("embeddedFloor", EmbeddedFloorVoxels);
            tree.SetFloat("meshAngleRad", MeshAngleRad);

            if (paneStates != null && paneStates.Length > 0)
            {
                var chars = new char[paneStates.Length];
                for (int i = 0; i < paneStates.Length; i++) chars[i] = paneStates[i] ? '1' : '0';
                tree.SetString("paneStates", new string(chars));
            }

            tree.SetString("autoboxesHeld", AutoBoxes.Write(autoHeld));
            if (paneFallback.Any(f => f)) tree.SetString("paneFallback", new string(paneFallback.Select(f => f ? '1' : '0').ToArray()));

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
            if (_slamFast) tree.SetBool("hauntSlam", true);
            if (_rattle) tree.SetBool("hauntRattle", true);
            if (_hauntOpenPane >= 0) tree.SetInt("hauntOpen", _hauntOpenPane);
            if (_floatSlotServer != null)
            {
                tree.SetString("hauntFloat", _floatSlotServer);
                tree.SetInt("hauntFloatSeq", _floatSeq);
                tree.SetInt("hauntFloatMs", _floatMsServer);
            }
        }

        // ── Info ─────────────────────────────────────────────────────────────

        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
        {
            bool roomSafe = ServerRules.RoomSafeOpening;
            if (CanOpen && !roomSafe)
            {
                dsc.AppendLine(StateLine());
            }

            container.ReloadRoom();

            // container.GetPerishRate() only accounts for the room. The open/closed
            // multiplier lives in our OnAcquireTransitionSpeed delegate, so it has to
            // be applied here too or the tooltip never moves when a pane opens.
            float perishRate = container.GetPerishRate() * OnAcquireTransitionSpeed(EnumTransitionType.Perish, null, 1f);
            // a block with no surfaces can store nothing, so the line would only mislead (Calm, 2026-10-13)
            if ((SurfaceBehavior?.Surfaces.Length ?? 0) > 0)
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

            // Block info is forwarded to the owner, but the player's selection still points at the host shelf / filler
            // (an item on the top shelf with another shelf above said nothing - Calm 2026-10-16; fix by Codex). Resolve it
            // the way a click is, before reading our inventory.
            BlockSelection aimed = WindowSelection(Api.World.BlockAccessor, forPlayer?.CurrentBlockSelection);
            string slotId = aimed?.Position?.Equals(Pos) == true ? GetSelectedNonEmptySlotId(aimed) : null;
            if (slotId != null && inv[slotId] is { Empty: false } slot)
            {
                AppendStoredItemInfo(dsc, slot, perishRate);

                // its display size, when it has been resized (Calm 2026-10-21: "so we know the size of it")
                float scale = ItemScale.Of(slot.Itemstack);
                if (Math.Abs(scale - 1f) > 0.0001f)
                    dsc.AppendLine(Lang.Get("universaldisplaylib:item-scale", (int)Math.Round(scale * 100f)));
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

        // ── AutoBoxes Kit (src/autoboxes/, guide 2B + 2C) ────────────────────
        // The window's boxes are measured from its model, turned by MeshAngleRad, and reach every block the
        // model does: fillers in the empty ones. The display-surface markers are left out of the measuring,
        // as they are left out of the mesh.
        // Every part its own box (Calm, 2026-10-06), all from the model - Reach.PaneBoxes:
        //   each Static group - id "part{n}": outlined and solid, wrench + break, a click opens nothing;
        //   other fixed parts - id "solidpart{n}": collision and selection, no outline or right-click action;
        //   each leaf, shut or where its open animation leaves it - id "pane{i}": a click opens / closes that leaf.
        // A leaf that swings into the blocks beside (the External Shutter) gets fillers there.

        /// <summary>The id of the window's measured body box - a click on it works every pane (ToggleWindow).</summary>
        public const string WindowBoxId = "window";

        /// <summary>Id prefix of a fixed part's box ("part0", "part1"...) - aimable and solid, opens nothing.</summary>
        public const string PartBoxPrefix = "part";

        /// <summary>Unoutlined selection blockers for model parts outside Static and animated groups.</summary>
        public const string HiddenPartBoxPrefix = "solidpart";

        public static bool IsHiddenPartBox(string id)
        {
            // A neighbouring display can carry this box with a guest-owner prefix.
            while (id != null && id.StartsWith(GuestBoxPrefix, StringComparison.Ordinal))
            {
                int colon = id.IndexOf(':', GuestBoxPrefix.Length);
                if (colon < 0) return false;
                id = id.Substring(colon + 1);
            }
            return id != null && id.StartsWith(HiddenPartBoxPrefix, StringComparison.Ordinal);
        }

        /// <summary>Everything the boxes are, made in one go and swapped in whole (read from the render and physics threads).</summary>
        sealed class AutoLayout
        {
            public static readonly AutoLayout Empty = new();
            public Dictionary<(int, int, int), Cuboidf[]> Selection = new();
            public Dictionary<(int, int, int), Cuboidf[]> Collision = new();
        }

        volatile AutoLayout autoLayout = AutoLayout.Empty;
        List<(int x, int y, int z)> autoHeld = new();

        void AutoBoxesMeasureTurned()
        {
            if (IsEmbedded) return;
            if (Api == null || Block == null) return;
            Block measured = MeasuredBlock();
            var boxes = new List<(Cuboidf box, string id)>();

            FrameBoxGroup[] groups = SurfaceBehavior?.FrameBoxGroups ?? Array.Empty<FrameBoxGroup>();
            bool[] open = paneStates ?? Array.Empty<bool>();
            Reach.PaneBoxes panes = Reach.PaneBoxes.For(Api, measured?.Shape, Enumerable.Range(0, groups.Length).Select(OpenAnimFor).ToArray());

            if (panes == null)
            {
                // the model could not be split: the kit's one box round it all
                Cuboidf whole = AutoBoxesMeasure.FromModel(Api, measured, false);
                if (whole != null) boxes.Add((whole, PartBoxPrefix + "0"));
            }
            else
            {
                // every fixed part its own box (outlined, solid, wrench and break - a click opens nothing)
                for (int n = 0; n < panes.Fixed.Length; n++) boxes.Add((panes.Fixed[n], PartBoxPrefix + n));
                // every leaf one box, shut or where its open animation leaves it (a click works that leaf);
                // a pane marked boxStaysShut keeps its shut box while open
                for (int i = 0; i < groups.Length; i++)
                {
                    bool isOpen = i < open.Length && open[i] && !groups[i].BoxStaysShut;
                    Cuboidf leaf = isOpen ? panes.Open[i] ?? panes.Shut[i] : panes.Shut[i];
                    if (leaf != null) boxes.Add((leaf, "pane" + i));
                }
            }

            var layout = new AutoLayout();
            var selection = new Dictionary<(int, int, int), List<Cuboidf>>();
            var collision = new Dictionary<(int, int, int), List<Cuboidf>>();
            foreach (var (box, id) in boxes)
            {
                Cuboidf turned = Turned(box);
                foreach (var (cell, piece) in AutoBoxes.Cut(turned))
                {
                    if (!selection.TryGetValue(cell, out var sel)) selection[cell] = sel = new List<Cuboidf>();
                    if (!collision.TryGetValue(cell, out var col)) collision[cell] = col = new List<Cuboidf>();
                    sel.Add(new CuboidfWithId(AutoBoxes.Whole(turned, cell)) { Id = id });
                    col.Add(piece);
                }
            }
            // Parts outside leaves and Static groups also stop aiming through the model.
            // Their selection boxes are deliberately unoutlined and consume clicks without an action.
            int solidIndex = 0;
            foreach (Cuboidf box in panes?.Solid ?? Array.Empty<Cuboidf>())
            {
                Cuboidf turned = Turned(box);
                string id = HiddenPartBoxPrefix + solidIndex++;
                foreach (var (cell, piece) in AutoBoxes.Cut(turned))
                {
                    if (!selection.TryGetValue(cell, out var sel)) selection[cell] = sel = new List<Cuboidf>();
                    sel.Add(new CuboidfWithId(AutoBoxes.Whole(turned, cell)) { Id = id });
                    if (!collision.TryGetValue(cell, out var col)) collision[cell] = col = new List<Cuboidf>();
                    col.Add(piece);
                }
            }
            foreach (var kv in selection) layout.Selection[kv.Key] = kv.Value.ToArray();
            foreach (var kv in collision) layout.Collision[kv.Key] = kv.Value.ToArray();
            AutoLayout before = autoLayout;
            autoLayout = layout;
            InvalidateBoxes();
            TellHosts(before, layout);
        }

        /// <summary>A box turned with the window, corners in order (the kit's cutting expects x1 &lt; x2).</summary>
        Cuboidf Turned(Cuboidf box) => TurnedBy(box, MeshAngleRad);

        static Cuboidf TurnedBy(Cuboidf box, float angle)
        {
            Cuboidf t = angle == 0 ? box : box.RotatedCopyRad(0f, angle, 0f, new Vec3d(0.5, 0.0, 0.5));
            return new Cuboidf(Math.Min(t.X1, t.X2), Math.Min(t.Y1, t.Y2), Math.Min(t.Z1, t.Z2), Math.Max(t.X1, t.X2), Math.Max(t.Y1, t.Y2), Math.Max(t.Z1, t.Z2));
        }

        /// <summary>The block as the kit measures it: its own shape, minus the psurface markers.</summary>
        Block MeasuredBlock() => MeasuredBlockOf(Block);

        static Block MeasuredBlockOf(Block block)
        {
            string[] markers = block?.GetBehavior<BlockBehaviorDisplaySurfaces>()?.SurfaceElementNames ?? Array.Empty<string>();
            if (block?.Shape == null || markers.Length == 0) return block;
            CompositeShape shape = block.Shape.Clone();
            shape.IgnoreElements = (shape.IgnoreElements ?? Array.Empty<string>()).Concat(markers).Distinct().ToArray();
            return new Block { Code = block.Code, Shape = shape };
        }

        /// <summary>
        /// Every block a window of <paramref name="block"/>, turned by <paramref name="angle"/>, reaches when SHUT (offsets from
        /// its own; its own included) - what placing it needs free (the height detection: a tall window needs the space above).
        /// </summary>
        public static HashSet<(int x, int y, int z)> ShutCells(ICoreAPI api, Block block, float angle)
            => ShutCells(api, block, angle, true);

        /// <summary>
        /// <paramref name="withSolidOnly"/> false leaves out the parts in no leaf and no Static group (collision only - the
        /// sliding shutters' rails running along the wall): the spaces only those reach are not the window's body.
        /// </summary>
        static HashSet<(int x, int y, int z)> ShutCells(ICoreAPI api, Block block, float angle, bool withSolidOnly)
        {
            var cells = new HashSet<(int x, int y, int z)> { (0, 0, 0) };
            Block measured = MeasuredBlockOf(block);
            FrameBoxGroup[] groups = block?.GetBehavior<BlockBehaviorDisplaySurfaces>()?.FrameBoxGroups ?? Array.Empty<FrameBoxGroup>();
            Reach.PaneBoxes panes = Reach.PaneBoxes.For(api, measured?.Shape, groups.Select(g => g.AnimOpen ?? "").ToArray());
            var boxes = panes == null
                ? new[] { AutoBoxesMeasure.FromModel(api, measured, false) }
                : panes.Fixed.Concat(withSolidOnly ? panes.Solid : Array.Empty<Cuboidf>()).Concat(panes.Shut);
            foreach (Cuboidf box in boxes)
            {
                if (box == null) continue;
                foreach (var cell in AutoBoxes.Cut(TurnedBy(box, angle)).Keys) cells.Add(cell);
            }
            return cells;
        }

        /// <summary>
        /// The window's OWN spaces (a tall window's upper blocks): the blocks it fills when shut - not the spaces a leaf only
        /// swings into. Its fillers there seal rooms and hold torches as the window does (IAutoBoxesPartOfOwner, 2026-10-07).
        /// </summary>
        public bool IsPartOfOwner(BlockPos cell)
        {
            if (Api == null || Block == null) return false;
            var cache = ownCells;
            if (cache.blockId != Block.Id || cache.angle != MeshAngleRad || cache.cells == null)
                ownCells = cache = (Block.Id, MeshAngleRad, ShutCells(Api, Block, MeshAngleRad));
            return cache.cells.Contains((cell.X - Pos.X, cell.Y - Pos.Y, cell.Z - Pos.Z));
        }
        private (int blockId, float angle, HashSet<(int x, int y, int z)> cells) ownCells;

        /// <summary>The window's own spaces other than its block (a tall window's upper blocks) - they hold a filler even when empty.</summary>
        IEnumerable<(int, int, int)> OwnSpaces()
        {
            if (Api == null || Block == null) return Array.Empty<(int, int, int)>();
            IsPartOfOwner(Pos);   // fills the cache
            return ownCells.cells.Where(c => c != (0, 0, 0)).Select(c => (c.x, c.y, c.z));
        }

        /// <summary>
        /// Where a shut light-blocking window darkens its fillers: its own spaces that its leaves or Static parts fill, not
        /// those only trim reaches (Calm, 2026-10-20: the sliding shutters' rails shaded the outside wall beside them).
        /// </summary>
        public bool TakesLightAt(BlockPos cell)
        {
            if (Api == null || Block == null) return false;
            var cache = lightCells;
            if (cache.blockId != Block.Id || cache.angle != MeshAngleRad || cache.cells == null)
                lightCells = cache = (Block.Id, MeshAngleRad, ShutCells(Api, Block, MeshAngleRad, false));
            return cache.cells.Contains((cell.X - Pos.X, cell.Y - Pos.Y, cell.Z - Pos.Z));
        }
        private (int blockId, float angle, HashSet<(int x, int y, int z)> cells) lightCells;

        /// <summary>
        /// Server: the fillers in the window's own spaces take its light - dark while its block absorbs light (a shut External
        /// Shutter), clear otherwise. Done a moment after the change, once Sync's fillers are in place (tried 2026-10-20 for a
        /// shut 3 high that stayed lit behind; that was cured by keeping the own-space fillers - see SyncFillers - and this
        /// was kept as tested with it).
        /// </summary>
        public void MatchFillerLight()
        {
            if (Api?.Side != EnumAppSide.Server) return;
            ICoreAPI api = Api;
            BlockPos pos = Pos;
            api.Event.RegisterCallback(_ =>
            {
                if (api.World.BlockAccessor.GetBlockEntity(pos) is BEUniversalDisplay be && be.Api != null)
                    PartOfOwnerLight.Match(api.World, pos, be, be.autoHeld);
            }, 50);
        }

        /// <summary>Every other block this window's boxes reach right now (outline or solid) - where its fillers go.</summary>
        public IEnumerable<BlockPos> CoveredCells()
        {
            AutoLayout layout = autoLayout;
            foreach (var cell in layout.Selection.Keys.Union(layout.Collision.Keys))
            {
                if (cell != (0, 0, 0)) yield return Pos.AddCopy(cell.Item1, cell.Item2, cell.Item3);
            }
        }

        /// <summary>Measure again (turned, a pane opened or shut, another block after a swap) and, on the server, move the fillers to match.</summary>
        public void AutoBoxesAngleChanged()
        {
            AutoBoxesMeasureTurned();
            if (Api?.Side == EnumAppSide.Server) SyncFillers();
        }

        readonly ConcurrentDictionary<(int, int, int), DisplayBoxCache> fillerSelectionCache = new();
        readonly ConcurrentDictionary<(int, int, int), DisplayBoxCache> fillerCollisionCache = new();

        public Cuboidf[] AutoBoxesAt(BlockPos cell)
        {
            var key = (cell.X - Pos.X, cell.Y - Pos.Y, cell.Z - Pos.Z);
            Cuboidf[] own = autoLayout.Selection.TryGetValue(key, out var boxes) ? boxes : [];
            if (key == (0, 0, 0)) return own;
            Cuboidf[] items = ItemBoxesIn(key);
            // the placement pads of a surface in that space (2026-10-17) - after the layout, before the items, the same
            // order on both sides so a click's index means the same box
            // PADS FIRST (2026-10-17, Calm: the top shelf "shows right on the edge but no area on the shelf") - the layout's
            // whole frame box tied with the pad at the shelf top and won, as in the own block before pads went first
            Cuboidf[] pads = _padCells.TryGetValue(key, out var foundPads) ? foundPads : Array.Empty<Cuboidf>();
            Vec3f shift = Api == null ? null : SidingShift.At(Pos, Block);
            return fillerSelectionCache.GetOrAdd(key, _ => new DisplayBoxCache())
                .Get(own, pads, items, shift?.X ?? 0f, shift?.Z ?? 0f);
        }

        // ── Items reaching past the block (2026-10-10, review point 2: "aiming at higher stacked crocks selects the ground
        // behind them") ──────────────────────────────────────────────────────────────────────────────────────────────────────
        // The game tests only the boxes of the block space the aim is passing through (vsapi AABBIntersectionTest walks the
        // spaces), so an item box reaching above its window was clickable only through the window's own space. Each placed
        // item's box is now also handed, whole and with its own id, to every other space it reaches - through AutoBoxesAt, so
        // the window's fillers there (or the UD block standing there, as a guest) outline it and send a click on to the window
        // with that id. Fillers go only into empty spaces: a real block above is never replaced. Outline only, never solid.

        /// <summary>Placed items' boxes in the other spaces they reach (offset -> whole boxes, ids kept). Swapped in whole.</summary>
        volatile Dictionary<(int, int, int), Cuboidf[]> _itemCells = new();
        volatile Dictionary<(int, int, int), Cuboidf[]> _itemCollisionCells = new();
        /// <summary>The spaces the fillers were last synced for (server).</summary>
        HashSet<(int, int, int)> _itemCellsSynced = new();
        [ThreadStatic] static int regenDepth;
        bool _fillerCheckQueued;

        /// <summary>Server: every loaded window - told when an item's measured size arrives (its boxes may now reach further).</summary>
        static readonly ConcurrentDictionary<BEUniversalDisplay, byte> LiveServerInstances = new();

        /// <summary>Server: every display block entity in loaded chunks (Universal Ground Storage's /ugs revert walks its piles).</summary>
        public static IEnumerable<BEUniversalDisplay> LoadedOnServer => LiveServerInstances.Keys;

        /// <summary>Server start: a static outlives a world, so the last world's windows must not be walked in this one.</summary>
        public static void ForgetServerInstances() => LiveServerInstances.Clear();

        Cuboidf[] ItemBoxesIn((int, int, int) key)
        {
            // current boxes - unless asked from inside this window's own rebuild (a guest asking back), or from where a
            // rebuild is not safe: then the last ones. REBUILT ONLY ON THE MAIN THREAD, ONCE THE INVENTORY IS SET UP
            // (2026-10-10, a client crash on load: the game asked a filler's sounds on the 'blockticking' thread, this
            // rebuilt the boxes there, and inv.ResolveBlocksOrItems dereferenced the not-yet-initialised inventory's Api -
            // the same trap the particle hooks avoid). Aiming happens on the main thread, so the boxes are current there.
            if (regenDepth == 0 && CanRebuildBoxesHere()) GetSelectionBoxes();
            // Hosts cache these boxes, including their order for server interaction.
            // Do not bake the current player's temporary placement preview into that
            // cache: adding an item on a host shelf could otherwise hide this stand's
            // entire stack until a later rebuild. Shared-space targets stay exposed.
            return _itemCells.TryGetValue(key, out var boxes) ? boxes : [];
        }

        static Cuboidf Ordered(Cuboidf b) =>
            new Cuboidf(Math.Min(b.X1, b.X2), Math.Min(b.Y1, b.Y2), Math.Min(b.Z1, b.Z2), Math.Max(b.X1, b.X2), Math.Max(b.Y1, b.Y2), Math.Max(b.Z1, b.Z2));

        /// <summary>Whether this thread may rebuild the boxes: the inventory is initialised, and on the client it is the main thread.</summary>
        bool CanRebuildBoxesHere() =>
            Api != null && inv?.Api != null
            && (capi == null || Environment.CurrentManagedThreadId == RuntimeEnv.MainThreadId);

        /// <summary>
        /// PADS IN THE SPACES ABOVE (2026-10-17, Calm: a 2-high shelf "combining the 2 shelves in a recipe" - "would make
        /// them look better than stacking"). A surface the model puts in another space (an upper shelf of a 2-high stand)
        /// had its placement pads only in this block's own box list, and the game tests a space's boxes only when the aim
        /// passes through that space - so from the front the upper shelf could not be placed on. Each such pad is now also
        /// handed, whole and with its own id, to the space it is in, exactly as items reaching up are: the filler there
        /// outlines it and sends the click down here with the pad's id, so placing, the ghost and the wheel all work.
        /// </summary>
        volatile Dictionary<(int, int, int), Cuboidf[]> _padCells = new();

        void SetPadCells(List<CuboidfWithId> pads)
        {
            var cells = new Dictionary<(int, int, int), List<Cuboidf>>();
            foreach (CuboidfWithId pad in pads)
                // a shelf right on the join (pad top = the space's floor) belongs to the space above too: cut with its top
                // nudged up so that space gets it
                foreach (var cell in AutoBoxes.Cut(new Cuboidf(Ordered(pad).X1, Ordered(pad).Y1, Ordered(pad).Z1, Ordered(pad).X2, Ordered(pad).Y2 + 0.01f, Ordered(pad).Z2)).Keys)
                {
                    if (cell == (0, 0, 0)) continue;
                    // ...but only where the model itself goes on up (a tall stand). The top shelf of every 1-high piece
                    // sits on the join too, and handed up it put a filler above every stand (Calm 2026-10-17: a second
                    // box on the top shelf in a siding wall, and lag)
                    if (Ordered(pad).Y1 < 1f && !autoLayout.Selection.ContainsKey(cell) && !autoLayout.Collision.ContainsKey(cell)) continue;
                    if (!cells.TryGetValue(cell, out var list)) cells[cell] = list = new List<Cuboidf>();
                    list.Add(new CuboidfWithId(AutoBoxes.Whole(Ordered(pad), cell)) { Id = pad.Id });
                }
            bool changed = !_padCells.Keys.ToHashSet().SetEquals(cells.Keys);
            _padCells = cells.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
            if (changed && Api?.Side == EnumAppSide.Server) ScheduleItemFillerCheck();
        }

        void SetItemCells(List<(Cuboidf footprint, Cuboidf outline)> items)
        {
            var cells = new Dictionary<(int, int, int), List<Cuboidf>>();
            var collisions = new Dictionary<(int, int, int), List<Cuboidf>>();
            foreach (var (footprint, outline) in items)
            {
                string id = (outline as CuboidfWithId)?.Id ?? (footprint as CuboidfWithId)?.Id;
                Cuboidf shown = Ordered(outline);
                foreach (var (cell, piece) in AutoBoxes.Cut(Ordered(footprint)))
                {
                    if (!collisions.TryGetValue(cell, out var physical)) collisions[cell] = physical = new List<Cuboidf>();
                    physical.Add(piece);
                    if (cell == (0, 0, 0)) continue;
                    if (!cells.TryGetValue(cell, out var list)) cells[cell] = list = new List<Cuboidf>();
                    list.Add(new CuboidfWithId(AutoBoxes.Whole(shown, cell)) { Id = id });
                }
            }
            var physicalBefore = _itemCollisionCells;
            var physicalAfter = new Dictionary<(int,int,int),Cuboidf[]>();
            foreach (var entry in collisions)
            {
                var next = entry.Value.ToArray();
                if (physicalBefore.TryGetValue(entry.Key, out var old) && SameItemCellBoxes(old,next)) next = old;
                physicalAfter[entry.Key] = next;
            }
            _itemCollisionCells = physicalAfter;
            var before = _itemCells;
            var after = cells.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
            _itemCells = after;
            PublishStorageReach(before.Keys, after.Keys);
            bool cellsChanged = !before.Keys.ToHashSet().SetEquals(after.Keys);
            // Hosts cache copies of these boxes. A stack can grow, shrink or move while
            // remaining in the same cell, so compare the boxes as well as cell membership.
            // Unchanged boxes must not invalidate hosts: neighbouring displays rebuild
            // each other's guest lists and would otherwise keep invalidating one another.
            IBlockAccessor ba = Api?.World?.BlockAccessor;
            if (ba != null && Pos != null)
                foreach (var (x, y, z) in before.Keys.Union(after.Keys).Union(physicalBefore.Keys).Union(physicalAfter.Keys))
                {
                    before.TryGetValue((x, y, z), out var oldBoxes);
                    after.TryGetValue((x, y, z), out var newBoxes);
                    physicalBefore.TryGetValue((x,y,z),out var oldPhysical);
                    physicalAfter.TryGetValue((x,y,z),out var newPhysical);
                    if (SameItemCellBoxes(oldBoxes, newBoxes) && SameItemCellBoxes(oldPhysical,newPhysical)) continue;
                    if (ba.GetBlockEntity(Pos.AddCopy(x, y, z)) is BEUniversalDisplay host && host != this) host.GuestsChanged();
                }
            if (cellsChanged && Api?.Side == EnumAppSide.Server) ScheduleItemFillerCheck();
        }

        static bool SameItemCellBoxes(Cuboidf[] before, Cuboidf[] after)
        {
            if (ReferenceEquals(before, after)) return true;
            if (before == null || after == null || before.Length != after.Length) return false;
            for (int i = 0; i < before.Length; i++)
            {
                Cuboidf a = before[i], b = after[i];
                if ((a as CuboidfWithId)?.Id != (b as CuboidfWithId)?.Id
                    || a.X1 != b.X1 || a.Y1 != b.Y1 || a.Z1 != b.Z1
                    || a.X2 != b.X2 || a.Y2 != b.Y2 || a.Z2 != b.Z2) return false;
            }
            return true;
        }

        /// <summary>Server: the fillers for the window's own boxes AND the spaces its items reach.</summary>
        void SyncFillers()
        {
            if (Api?.Side != EnumAppSide.Server) return;
            GetSelectionBoxes();
            var itemCells = _itemCells.Keys.ToHashSet();
            // + the window's own spaces, open or shut (Calm, 2026-10-21): a 3 high shutter folded open has nothing in its middle
            // block, and a filler placed there again on shutting did not take the light out of the room behind - only facing
            // east, where rounding kept one. Kept always, a shut is only ever a clear-to-dark swap on a filler already there.
            var wanted = autoLayout.Selection.Keys.Union(autoLayout.Collision.Keys).Union(itemCells).Union(_padCells.Keys)
                .Union(OwnSpaces()).ToList();
            autoHeld = AutoBoxes.Sync(Api.World, Pos, wanted, autoHeld);
            _itemCellsSynced = itemCells.Union(_padCells.Keys).ToHashSet();
            MatchFillerLight();
        }

        /// <summary>Server, debounced: after a change (anything that marks the window dirty), re-sync the fillers if the items now reach other spaces.</summary>
        void ScheduleItemFillerCheck()
        {
            if (Api?.Side != EnumAppSide.Server || _fillerCheckQueued || _removed) return;
            _fillerCheckQueued = true;
            RegisterDelayedCallback(_ =>
            {
                _fillerCheckQueued = false;
                if (_removed) return;
                GetSelectionBoxes();
                if (!_itemCells.Keys.Union(_padCells.Keys).ToHashSet().SetEquals(_itemCellsSynced)) SyncFillers();
            }, 50);
        }

        public override void MarkDirty(bool redrawOnClient = false, IPlayer skipPlayer = null)
        {
            if (IsEmbedded)
            {
                if (redrawOnClient) { meshesGenerated = false; InvalidateBoxes(); }
                EmbeddedHost.MarkDirty(redrawOnClient, skipPlayer);
                ScheduleItemFillerCheck();
                QueueEmbeddedMeshes();
                return;
            }
            base.MarkDirty(redrawOnClient, skipPlayer);
            if (Api?.Side == EnumAppSide.Server) ScheduleItemFillerCheck();
        }

        /// <summary>Server: a measured item size arrived - windows holding items may now reach other spaces.</summary>
        public static void ItemSizesChanged()
        {
            var api = LiveServerInstances.Keys.FirstOrDefault()?.Api;
            api?.Event.EnqueueMainThreadTask(() =>
            {
                foreach (var be in LiveServerInstances.Keys)
                    if (!be._removed && !be.inv.Empty) { be.InvalidateBoxes(); be.ScheduleItemFillerCheck(); }
            }, "universaldisplaylib-itemsizes");
        }

        // Physical footprint pieces, never client mesh outlines or placement pads.
        public Cuboidf[] ItemCollisionAt(BlockPos cell)
        {
            if (!ItemCollision || cell == null || Pos == null) return Array.Empty<Cuboidf>();
            if (regenDepth == 0 && CanRebuildBoxesHere()) GetSelectionBoxes();
            var key = (cell.X - Pos.X, cell.Y - Pos.Y, cell.Z - Pos.Z);
            return _itemCollisionCells.TryGetValue(key, out var pieces) ? pieces : Array.Empty<Cuboidf>();
        }

        public Cuboidf[] AutoCollisionAt(BlockPos cell)
        {
            var key = (cell.X - Pos.X, cell.Y - Pos.Y, cell.Z - Pos.Z);
            Cuboidf[] items = ItemCollisionAt(cell);
            Cuboidf[] boxes = autoLayout.Collision.TryGetValue(key, out var found) ? found : [];
            Vec3f shift = key == (0,0,0) || Api == null ? null : SidingShift.At(Pos, Block);
            if (items.Length == 0 && key == (0,0,0)) return boxes;
            return fillerCollisionCache.GetOrAdd(key, _ => new DisplayBoxCache())
                .Get(boxes, Array.Empty<Cuboidf>(), items, shift?.X ?? 0f, shift?.Z ?? 0f);
        }

        // ── Guests (Calm, 2026-10-07: "I need all the parts to be clickable the shutter and the window and to have normal
        // window functions") ─────────────────────────────────────────────────
        // A leaf that opens into a block another Universal Display block stands in (a classic window into an OPEN External
        // Shutter's block - the room check lets it, the shutter's leaves being folded away) can have no filler there, so that
        // part of it was drawn but neither solid nor clickable. The block it is in now carries it: the piece is solid there,
        // the leaf is outlined there, and a click, a break, the help, the name and the sounds go to the window it belongs to.
        // Nothing is saved: a block looks round for windows reaching into it when first asked, and a window tells the blocks
        // it reaches whenever its boxes change (opened, shut, turned, swapped, loaded, removed).
        // A tall window's upper blocks are not this: there the space holds the other window's filler, which is SHARED.

        /// <summary>Id prefix of a guest's box in its host's list: "guest:dx,dy,dz:id" - offset to the window, and the box's own id there.</summary>
        public const string GuestBoxPrefix = "guest:";

        volatile BEUniversalDisplay[] guests;

        /// <summary>The windows whose boxes reach into this block (beside it, or a tall one below or above).</summary>
        BEUniversalDisplay[] Guests()
        {
            if (IsEmbedded) return Array.Empty<BEUniversalDisplay>();
            BEUniversalDisplay[] found = guests;
            if (found != null) return found;
            IBlockAccessor ba = Api?.World?.BlockAccessor;
            if (ba == null || Pos == null) return Array.Empty<BEUniversalDisplay>();
            var list = new List<BEUniversalDisplay>();
            foreach (var other in StorageGuestsAt(ba, Pos))
                if (other != this && !other._removed) list.Add(other);
            for (int dy = -3; dy <= 3; dy++)
            for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (dx == 0 && dy == 0 && dz == 0) continue;
                if (At(ba, Pos.AddCopy(dx, dy, dz)) is not { } other || other._removed) continue;
                // ITS PLACED ITEMS' BOXES UP TO DATE FIRST (2026-10-11, Calm: a stand's top-shelf items reaching into a shelf
                // standing above could not be taken). A leaf's boxes exist from load, but the item boxes a window hands to the
                // spaces above it are only worked out when that window rebuilds its own boxes - so this block could look
                // before they existed, find nothing and keep that answer (and on the server the box numbers then disagree).
                // One level deep only: the window's rebuild looks round in turn, and must not start this one again.
                if (regenDepth < 2 && other.CanRebuildBoxesHere()) other.GetSelectionBoxes();
                if (!list.Contains(other) && (other.AutoBoxesAt(Pos).Length > 0 || other.AutoCollisionAt(Pos).Length > 0)) list.Add(other);
            }
            found = list.ToArray();
            guests = found;
            return found;
        }

        /// <summary>A window reaching into this block changed its boxes: look again.</summary>
        public void GuestsChanged()
        {
            guests = null;
            InvalidateBoxes();
        }

        /// <summary>Tell every Universal Display block this window reaches (before and after a change) to look again.</summary>
        void TellHosts(params AutoLayout[] layouts)
        {
            IBlockAccessor ba = Api?.World?.BlockAccessor;
            if (ba == null || Pos == null) return;
            var cells = new HashSet<(int, int, int)>();
            foreach (AutoLayout layout in layouts)
            {
                if (layout == null) continue;
                cells.UnionWith(layout.Selection.Keys);
                cells.UnionWith(layout.Collision.Keys);
            }
            foreach (var (x, y, z) in cells)
            {
                if (x == 0 && y == 0 && z == 0) continue;
                if (ba.GetBlockEntity(Pos.AddCopy(x, y, z)) is BEUniversalDisplay host && host != this) host.GuestsChanged();
            }
        }

        /// <summary>
        /// A selection aimed at a guest's box in this block, as the guest's own block would have seen it: its position, the
        /// hit point re-measured from there, the box's own id. <paramref name="boxId"/>: the aimed box's id, if the
        /// selection does not carry it.
        /// </summary>
        public static bool GuestSelection(BlockSelection sel, string boxId, out BlockSelection forwarded)
        {
            forwarded = null;
            string id = boxId ?? sel?.SelectionBoxId;
            if (sel?.Position == null || id == null || !id.StartsWith(GuestBoxPrefix, StringComparison.Ordinal)) return false;
            int colon = id.IndexOf(':', GuestBoxPrefix.Length);
            if (colon < 0) return false;
            string[] d = id.Substring(GuestBoxPrefix.Length, colon - GuestBoxPrefix.Length).Split(',');
            if (d.Length != 3
                || !int.TryParse(d[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int dx)
                || !int.TryParse(d[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int dy)
                || !int.TryParse(d[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int dz)) return false;
            forwarded = sel.Clone();
            forwarded.Position = sel.Position.AddCopy(dx, dy, dz);
            if (sel.HitPosition != null) forwarded.HitPosition = sel.HitPosition.AddCopy(-dx, -dy, -dz);
            forwarded.SelectionBoxId = id.Substring(colon + 1);
            forwarded.SelectionBoxIndex = 0;
            return true;
        }

        /// <summary>
        /// The player's selection as the window that owns the aimed box sees it - through a filler (an item reaching above
        /// its window, a tall window's upper block) and through a guest box on another UD block. Unchanged otherwise.
        /// For everything that reads the selection itself: the mouse wheel, the placing ghost, the editors.
        /// </summary>
        public static BlockSelection WindowSelection(IBlockAccessor ba, BlockSelection sel)
        {
            sel = BlockAutoBoxesFiller.ToOwner(ba, sel);
            // A selection from a block that is not ours carries only an index: read the id from that block's boxes, so a
            // guest box another mod put on a foreign block (Universal Ground Storage on a chiselled block) forwards too.
            string id = sel?.SelectionBoxId;
            if (id == null && sel?.Position != null && sel.SelectionBoxIndex >= 0)
            {
                Cuboidf[] boxes = ba.GetBlock(sel.Position)?.GetSelectionBoxes(ba, sel.Position);
                if (boxes != null && sel.SelectionBoxIndex < boxes.Length) id = (boxes[sel.SelectionBoxIndex] as CuboidfWithId)?.Id;
            }
            if (GuestSelection(sel, id, out BlockSelection guest))
            {
                sel = guest;
                id = guest.SelectionBoxId;
            }
            if (id?.StartsWith(EmbeddedBoxPrefix, StringComparison.Ordinal) == true)
            {
                var embedded = sel.Clone();
                embedded.SelectionBoxId = id.Substring(EmbeddedBoxPrefix.Length);
                embedded.SelectionBoxIndex = 0;
                return embedded;
            }
            return GuestSelection(sel, id, out BlockSelection forwarded) ? forwarded : sel;
        }

        /// <summary>The id of the box a selection of this block aims at - from the selection, or by its index here.</summary>
        public string AimedBoxId(BlockSelection sel)
        {
            if (sel == null) return null;
            if (sel.SelectionBoxId != null) return sel.SelectionBoxId;
            Cuboidf[] boxes = GetSelectionBoxes();
            return sel.SelectionBoxIndex >= 0 && sel.SelectionBoxIndex < boxes.Length ? (boxes[sel.SelectionBoxIndex] as CuboidfWithId)?.Id : null;
        }

        /// <summary>Server, this block going: the windows open into it get fillers there now (next tick, once it is gone).</summary>
        void FreeGuests()
        {
            if (Api?.Side != EnumAppSide.Server) return;
            foreach (BEUniversalDisplay guest in Guests())
            {
                BEUniversalDisplay g = guest;
                if (!g._removed) g.RegisterDelayedCallback(_ => { if (!g._removed) g.AutoBoxesAngleChanged(); }, 50);
            }
        }

        // ── Fallback (UDAutoBoxes "fallback", 2026-10-06) ─────────────────────
        // A pane whose full swing has no room opens to its fallback instead (the External Shutter: 90 degrees
        // instead of 180 when something - a wall, another shutter, another shutter's folded leaf - is beside it).

        /// <summary>Per pane: opened to its fallback. Saved and synced as "paneFallback"; kept after closing so the matching close animation plays.</summary>
        bool[] paneFallback = Array.Empty<bool>();

        bool UsesFallback(int pane) => pane < paneFallback.Length && paneFallback[pane]
            && Block?.GetBehavior<Reach.BlockBehaviorUDAutoBoxes>()?.FallbackFor(pane) != null;

        /// <summary>Where this window's OPEN panes really are in <paramref name="cell"/> (that block's own frame), part by part.</summary>
        IEnumerable<Cuboidf> OpenPartsIn(BlockPos cell)
        {
            FrameBoxGroup[] groups = SurfaceBehavior?.FrameBoxGroups ?? Array.Empty<FrameBoxGroup>();
            bool[] open = paneStates ?? Array.Empty<bool>();
            if (Api == null || !open.Contains(true)) yield break;
            string[] anims = Enumerable.Range(0, groups.Length).Select(OpenAnimFor).ToArray();
            Reach.PaneBoxes measured = Reach.PaneBoxes.For(Api, MeasuredBlock()?.Shape, anims);
            if (measured == null) yield break;
            var offset = (cell.X - Pos.X, cell.Y - Pos.Y, cell.Z - Pos.Z);
            for (int i = 0; i < open.Length && i < measured.OpenParts.Length; i++)
            {
                if (!open[i]) continue;
                foreach (Cuboidf part in measured.OpenParts[i])
                    if (AutoBoxes.Cut(Turned(part)).TryGetValue(offset, out var piece)) yield return piece;
            }
        }

        string OpenAnimFor(int pane)
        {
            FrameBoxGroup[] groups = SurfaceBehavior?.FrameBoxGroups ?? Array.Empty<FrameBoxGroup>();
            if (pane < 0 || pane >= groups.Length) return "";
            return UsesFallback(pane) ? Block.GetBehavior<Reach.BlockBehaviorUDAutoBoxes>().FallbackFor(pane).Value.open : groups[pane].AnimOpen ?? "";
        }

        string CloseAnimFor(int pane)
        {
            FrameBoxGroup[] groups = SurfaceBehavior?.FrameBoxGroups ?? Array.Empty<FrameBoxGroup>();
            if (pane < 0 || pane >= groups.Length) return "";
            return UsesFallback(pane) ? Block.GetBehavior<Reach.BlockBehaviorUDAutoBoxes>().FallbackFor(pane).Value.close ?? groups[pane].AnimClose : groups[pane].AnimClose;
        }

        /// <summary>Both pairs a pane can play, so the one not in use can be stopped.</summary>
        IEnumerable<string> AllAnimsFor(int pane)
        {
            FrameBoxGroup[] groups = SurfaceBehavior?.FrameBoxGroups ?? Array.Empty<FrameBoxGroup>();
            if (pane < 0 || pane >= groups.Length) yield break;
            yield return groups[pane].AnimOpen;
            yield return groups[pane].AnimClose;
            var fallback = Block?.GetBehavior<Reach.BlockBehaviorUDAutoBoxes>()?.FallbackFor(pane);
            if (fallback == null) yield break;
            yield return fallback.Value.open;
            if (fallback.Value.close != null) yield return fallback.Value.close;
        }

        // ── Halloween (2026-10-12, Calm: "all the windows trigger an event on halloween, sounds change and the windows randomly
        // open and then slam shut") ── driven by HalloweenWindows. ROOM-SAFE: the open/closed variant is not swapped while
        // haunted, so rooms stay sealed and a shut External Shutter stays dark. A player can still shut it first.
        bool _haunted;
        /// <summary>Synced: the pane shutting now was haunted - its close plays fast.</summary>
        bool _slamFast;
        const float SlamSpeedFactor = 5f;
        /// <summary>A haunted pane opens at this fraction of its normal speed - slow, with the creak all the way.</summary>
        const float HauntOpenSpeedFactor = 0.35f;
        // ── Halloween, floating items (Calm: "make items on the shelves float, as long as they're not stacked") ──
        /// <summary>Server: the items floating now, slot ids joined by '|' (synced with a sequence number and the length), null when none.</summary>
        string _floatSlotServer;
        /// <summary>Several items float one after another, this far apart - lifting and landing.</summary>
        const int FloatStaggerMs = 250;
        int _floatSeq, _floatMsServer;
        /// <summary>Client: the last float sequence seen, and the slot left out of the block mesh while it floats.</summary>
        int _floatSeqSeen;
        /// <summary>Client: the slots left out of the block mesh while they float (read on the tesselation thread).</summary>
        readonly ConcurrentDictionary<string, byte> _floatingSlots = new();

        /// <summary>
        /// Server: float one item that has nothing stacked on it and nothing under it, for <paramref name="floatMs"/>, then
        /// land it with a thud. The clients draw the rise, the bobbing and the drop (FloatingItemRenderer). False if none.
        /// </summary>
        public bool HauntFloat(Random rnd, int floatMs, bool several = false)
        {
            if (_removed || _floatSlotServer != null || Api?.Side != EnumAppSide.Server) return false;
            var filled = SlotsSnapshot().Where(kv => !kv.Value.Empty).Select(kv => kv.Key).ToList();
            var loners = filled.Where(id =>
            {
                DisplaySlotId loc = DisplaySlotId.Decode(id);
                if (loc == null || loc.Y > 0f) return false;   // on top of something
                return !filled.Any(other => other != id && DisplaySlotId.Decode(other) is { } o
                    && o.SurfaceIndex == loc.SurfaceIndex && o.X == loc.X && o.Z == loc.Z);   // something on top of it
            }).ToList();
            if (loners.Count == 0) return false;

            // one, or (Calm: "can we have several things float at once?") up to four of them, in a random order
            var chosen = loners.OrderBy(_ => rnd.Next()).Take(several ? Math.Min(loners.Count, 2 + rnd.Next(3)) : 1).ToList();
            _floatSlotServer = string.Join("|", chosen);
            _floatSeq++;
            _floatMsServer = floatMs;
            MarkDirty(true);
            for (int i = 0; i < chosen.Count; i++)
            {
                bool last = i == chosen.Count - 1;
                RegisterDelayedCallback(_ =>
                {
                    if (last) { _floatSlotServer = null; MarkDirty(true); }
                    Api.World.PlaySoundAt(new AssetLocation("game:sounds/thud"), Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5, null, true, 16f, 0.8f);
                }, floatMs + i * FloatStaggerMs);
            }
            return true;
        }

        /// <summary>Client: lift the item out of the block mesh and float it on its own for <paramref name="ms"/>.</summary>
        void StartFloat(string slotId, int ms, int seq)
        {
            if (capi == null || _removed || _floatingSlots.ContainsKey(slotId) || inv?.Api == null) return;
            // RESOLVE FIRST (2026-10-12, a client crash): the stack has just arrived with the sync and does not know its
            // item yet - the trap OnTesselation guards against. Main thread here, so resolving is safe.
            inv.ResolveBlocksOrItems();
            if (!inv.SlotsByslotId.TryGetValue(slotId, out ItemSlot slot) || slot == null || slot.Empty
                || slot.Itemstack.Collectible?.Code == null) return;
            DisplayableAttributes dattr = BlockBehaviorDisplaySurfaces.GetDisplayableAttributes(slot, DisplayCategoryFor(slotId));
            MeshData mesh = GetCachedItemMesh(slot) ?? (dattr == null ? null : GetOrCreateItemMesh(slot, dattr));
            float[] matrix = BuildSlotMatrix(slotId, slot, out _);
            if (mesh == null || matrix == null) return;
            // its own height, 3 to 9 voxels - worked out from the block, the slot and this float's number, so every player
            // sees the same one without it being sent
            // (FNV-1a by hand: .NET's HashCode is seeded differently in every game process, so players would disagree)
            uint fnv = 2166136261;
            foreach (char c in Pos.X + "," + Pos.Y + "," + Pos.Z + "|" + slotId + "|" + seq) { fnv ^= c; fnv *= 16777619; }
            int h = (int)(fnv % 61u);
            if (!FloatingItemRenderer.TryStart(capi, this, slotId, slot.Itemstack, mesh, matrix, ms, 3f + h / 10f)) return;
            _floatingSlots[slotId] = 0;
            capi.World.BlockAccessor.MarkBlockDirty(Pos);
        }

        /// <summary>Client: the float is over (or the item was taken) - back into the block mesh.</summary>
        internal void EndFloat(string slotId)
        {
            if (!_floatingSlots.TryRemove(slotId, out _)) return;
            if (!_removed && capi != null) capi.World.BlockAccessor.MarkBlockDirty(Pos);
        }

        /// <summary>Client: the stack in a slot now, for the floating renderer to notice it was taken.</summary>
        internal ItemStack StackIn(string slotId) =>
            inv.SlotsByslotId.TryGetValue(slotId, out ItemSlot slot) ? slot?.Itemstack : null;

        internal bool IsGone => _removed;

        /// <summary>Synced: the pane being haunted open (-1 none) - each client plays the creak and stops it once open.</summary>
        int _hauntOpenPane = -1;
        /// <summary>Client: the last hauntOpen seen, so a later sync of the same opening does not creak again.</summary>
        int _hauntOpenSeen = -1;

        /// <summary>Server: the loaded windows within <paramref name="range"/> blocks of <paramref name="pos"/>.</summary>
        public static List<BEUniversalDisplay> ServerWindowsNear(BlockPos pos, int range)
        {
            var list = new List<BEUniversalDisplay>();
            foreach (BEUniversalDisplay be in LiveServerInstances.Keys)
            {
                if (be._removed || be.Pos == null || be.Pos.dimension != pos.dimension) continue;
                int dx = be.Pos.X - pos.X, dy = be.Pos.Y - pos.Y, dz = be.Pos.Z - pos.Z;
                if (dx * dx + dy * dy + dz * dz <= range * range) list.Add(be);
            }
            return list;
        }

        /// <summary>
        /// Server: open one shut pane by itself with a creak and slam it shut <paramref name="openMs"/> later. The same room
        /// rules as a click (full swing, else the 90 fallback, else not at all). False if this window cannot.
        /// </summary>
        /// <summary>Synced: a pane rattling (Calm: "have the windows rapidly open and close for a bit") - both ways fast, no ordinary sound.</summary>
        bool _rattle;

        /// <summary>
        /// Server: flap one shut pane open and shut <paramref name="flaps"/> times, fast, a knock each time it shuts and the
        /// full slam on the last. The same room rules as a click; it stops (left open) if something opens in front of it.
        /// </summary>
        public bool HauntRattle(Random rnd, int flaps)
        {
            if (_haunted || _removed || Api?.Side != EnumAppSide.Server || !CanOpen) return false;
            FrameBoxGroup[] groups = SurfaceBehavior?.FrameBoxGroups;
            if (groups == null) return false;
            if (paneStates.Length < groups.Length) ClampPaneStatesToBlock();
            int[] shut = Enumerable.Range(0, Math.Min(groups.Length, paneStates.Length))
                .Where(i => !string.IsNullOrEmpty(groups[i].AnimOpen) && !paneStates[i]).ToArray();
            if (shut.Length == 0) return false;
            int pane = shut[rnd.Next(shut.Length)];
            var fallback = Block?.GetBehavior<Reach.BlockBehaviorUDAutoBoxes>()?.FallbackFor(pane);
            if (HasRoomToOpen(pane, groups[pane].AnimOpen, null)) SetFallback(pane, false);
            else if (fallback != null && HasRoomToOpen(pane, fallback.Value.open, null)) SetFallback(pane, true);
            else return false;

            _haunted = true;
            _rattle = true;
            RattleStep(rnd, pane, 0, flaps * 2);
            return true;
        }

        void RattleStep(Random rnd, int pane, int step, int steps)
        {
            if (_removed) return;
            bool opening = step % 2 == 0;
            if (pane >= paneStates.Length || paneStates[pane] != !opening   // a player moved it meanwhile
                || !opening && OpenNeighbourInTheWay(pane))                  // something opened in front of it
            {
                _haunted = false;
                RegisterDelayedCallback(_ => _rattle = false, 600);
                return;
            }
            SetHauntedPane(pane, opening);
            double x = Pos.X + 0.5, y = Pos.Y + 0.5, z = Pos.Z + 0.5;
            bool last = step == steps - 1;
            if (!opening && !last) Api.World.PlaySoundAt(new AssetLocation(SlamSound), x, y, z, null, 1.1f + (float)rnd.NextDouble() * 0.2f, 24f, 0.7f);
            if (last)
            {
                PlaySlam(Api.World, x, y, z, SlamSound, SlamSnap, SlamBodyPitch, 4, 2);
                _haunted = false;
                // cleared once the last shut has gone out, unsent: the next sync carries it off
                RegisterDelayedCallback(_ => _rattle = false, 600);
                if (Block?.Variant["state"] == "open" && !IsAnySealingPaneOpen) SyncStateVariant();
                return;
            }
            RegisterDelayedCallback(_ => RattleStep(rnd, pane, step + 1, steps), rnd.Next(180, 321));
        }

        public bool Haunt(Random rnd, int openMs)
        {
            if (_haunted || _removed || Api?.Side != EnumAppSide.Server || !CanOpen) return false;
            FrameBoxGroup[] groups = SurfaceBehavior?.FrameBoxGroups;
            if (groups == null) return false;
            if (paneStates.Length < groups.Length) ClampPaneStatesToBlock();
            int[] shut = Enumerable.Range(0, Math.Min(groups.Length, paneStates.Length))
                .Where(i => !string.IsNullOrEmpty(groups[i].AnimOpen) && !paneStates[i]).ToArray();
            if (shut.Length == 0) return false;
            int pane = shut[rnd.Next(shut.Length)];
            var fallback = Block?.GetBehavior<Reach.BlockBehaviorUDAutoBoxes>()?.FallbackFor(pane);
            if (HasRoomToOpen(pane, groups[pane].AnimOpen, null)) SetFallback(pane, false);
            else if (fallback != null && HasRoomToOpen(pane, fallback.Value.open, null)) SetFallback(pane, true);
            else return false;

            _haunted = true;
            _hauntOpenPane = pane;   // the creak is played by each client, cut off when the pane is open (PlayHauntCreak)
            SetHauntedPane(pane, true);
            // it creeps open first (HauntOpenSpeedFactor), THEN hangs for openMs - so it is always fully open before it slams
            int creepMs = (int)(AnimSeconds(OpenAnimFor(pane), HauntOpenSpeedFactor) * 1000f);
            RegisterDelayedCallback(_ => HauntSlam(pane, 0), creepMs + openMs);
            return true;
        }

        /// <summary>
        /// The slam - see PlaySlam: the thud's body (Calm: "Lets try the thud") with a short snap on top ("if we can make it
        /// snap then that might be good") - the barrel lid's knock, picked by ear from SnapCandidates. The candidates are what "/udhaunt slam 1-6" and "/udhaunt snap 1-5" play.
        /// </summary>
        // Calm, after trying them: "just the barrel close but louder and sharper" - the barrel lid's knock twice a little
        // high (the copies add up: louder; higher: sharper), and once higher again for the front edge
        public const string SlamSound = "game:sounds/block/barrelclose";
        public const float SlamBodyPitch = 1.1f;
        /// <summary>The chosen snap (a property, not a field: a field set from SnapCandidates above its declaration would run first and crash).</summary>
        public static (string sound, float pitch) SlamSnap => ("game:sounds/block/barrelclose", 1.3f);   // was snap 3 under the thud
        public static readonly (string sound, float pitch)[] SnapCandidates =
        {
            ("game:sounds/effect/latch", 1.1f), ("game:sounds/block/wood-tool", 1.3f), ("game:sounds/block/barrelclose", 1.0f),
            ("game:sounds/block/stickhit", 1.0f), ("game:sounds/block/chestclose", 1.1f)
        };
        public static readonly string[] SlamCandidates =
        {
            "game:sounds/block/wood-tool", "game:sounds/block/chop3", "game:sounds/block/door",
            "game:sounds/effect/crusher-impact1", "game:sounds/block/largedoor-close", "game:sounds/thud"
        };

        /// <summary>
        /// Server: the slam, "deeper, louder and sharper" (Calm): the body twice at 70% pitch (deeper, and the two copies add
        /// up), and the snap on top at its own pitch for the sharp front.
        /// </summary>
        /// <remarks>
        /// LOUDER = MORE COPIES (Calm: "need a louder slam ... make people jump"): one sound cannot go past full volume, so
        /// the copies are stacked at the same instant - each doubling is about 6 dB. <paramref name="bodyCopies"/> /
        /// <paramref name="snapCopies"/>: 4 and 2 for the real slam; the /udhaunt slam test keeps 2 and 1. Heard further
        /// (48 blocks) so it stays loud a few blocks off.
        /// </remarks>
        public static void PlaySlam(IWorldAccessor world, double x, double y, double z, string body, (string sound, float pitch) snap,
                                    float bodyPitch = 0.7f, int bodyCopies = 2, int snapCopies = 1)
        {
            var loc = new AssetLocation(body);
            var snapLoc = new AssetLocation(snap.sound);
            for (int i = 0; i < bodyCopies; i++) world.PlaySoundAt(loc, x, y, z, null, bodyPitch, 48f, 1f);
            for (int i = 0; i < snapCopies; i++) world.PlaySoundAt(snapLoc, x, y, z, null, snap.pitch, 48f, 1f);
        }

        void HauntSlam(int pane, int tries)
        {
            if (_removed) return;
            if (pane >= paneStates.Length || !paneStates[pane]) { _haunted = false; _hauntOpenPane = -1; return; }   // a player shut it first
            if (OpenNeighbourInTheWay(pane))
            {
                // something opened in front of it meanwhile: wait for it, or give up and leave it open
                if (tries < 5) RegisterDelayedCallback(_ => HauntSlam(pane, tries + 1), 1000);
                else { _haunted = false; _hauntOpenPane = -1; }
                return;
            }
            // synced with this shut so every client plays the close fast; cleared a moment later, unsent - the next sync
            // carries it off, and a player's own close is never fast
            _slamFast = true;
            _hauntOpenPane = -1;
            SetHauntedPane(pane, false);
            _haunted = false;
            RegisterDelayedCallback(_ => _slamFast = false, 1500);
            // the snap's swoosh is each client's (PlaySnapSwoosh), so it can be cut off as the pane lands
            double x = Pos.X + 0.5, y = Pos.Y + 0.5, z = Pos.Z + 0.5;
            // the slam, landing as the fast close ends: the wood chop (Calm's pick, "the slam sound would be the chop3") - "something
            // louder than the chop": two copies at once (already at full volume) over the large chest lid's wooden bang
            int landMs = (int)Math.Clamp(AnimSeconds(CloseAnimFor(pane), SlamSpeedFactor) * 1000f, 0f, 1000f);
            // "deeper, louder and sharper", with the wood tool hit (Calm: "Lets try wood-tool"): twice at 70% pitch (deeper,
            // and the two copies add up), once pitched UP for the crack at the front (the chest lid's bang under it went -
            // Calm: "remove the squeak when closing")
            RegisterDelayedCallback(_ => PlaySlam(Api.World, x, y, z, SlamSound, SlamSnap, SlamBodyPitch, 4, 2), landMs);
            // a player shut another pane while this one was haunted open: the variant followed this one - put it right
            if (Block?.Variant["state"] == "open" && !IsAnySealingPaneOpen) SyncStateVariant();
        }

        /// <summary>Client: every pane that just moved is one that does not seal a room (a shutter, a display door).</summary>
        bool ChangedPanesAreSolid(bool[] before)
        {
            FrameBoxGroup[] groups = SurfaceBehavior?.FrameBoxGroups;
            if (groups == null || paneStates == null) return false;
            bool any = false;
            for (int i = 0; i < paneStates.Length && i < groups.Length; i++)
            {
                bool was = before != null && i < before.Length && before[i];
                if (was == paneStates[i]) continue;
                if (groups[i].SealsRoom) return false;
                any = true;
            }
            return any;
        }

        /// <summary>Client: the creak of the pane haunted open now, and which pane - cut off when that pane shuts.</summary>
        ILoadedSound _creak;
        int _creakPane = -1;
        const float SqueakLeadInSeconds = 0.2f;
        const int SqueakGraceMs = 60;   // "till it fully opens" - stops right as it is open

        /// <summary>
        /// Client: a long creak while a haunted pane opens (Calm, after trying the squeak), cut off the moment it is open - or when it shuts
        /// (Calm, 2026-10-12: "the squeak noise when opening is better"; the long creaks are the separate random creaks now,
        /// HalloweenWindows).
        /// </summary>
        void PlayHauntCreak(int pane)
        {
            if (capi == null || Pos == null) return;
            // the long creaks (2.6-4.8 s, longer than any opening) - Calm: "we should have the long creak when it opens"
            string[] creaks = { "woodcreak_long1", "woodcreak_long2", "woodcreak_long3" };
            ILoadedSound sound = capi.World.LoadSound(new SoundParams
            {
                Location = new AssetLocation("game:sounds/block/creak/" + creaks[capi.World.Rand.Next(creaks.Length)]),
                Position = new Vec3f(Pos.X + 0.5f, Pos.Y + 0.5f, Pos.Z + 0.5f),
                DisposeOnFinish = true,
                Range = 24f,
                Volume = 1f,
                Pitch = 0.95f + (float)capi.World.Rand.NextDouble() * 0.1f
            });
            if (sound == null) return;
            StopHauntCreak();
            sound.Start();
            // skip the quiet lead-in so the squeak lands as the pane starts to move
            try { sound.PlaybackPosition = SqueakLeadInSeconds; } catch { /* not every sound can seek - it just plays from the start */ }
            _creak = sound;
            _creakPane = pane;
            // cut it off a moment after the pane is open - worked out next frame, once this update (the fallback flag) is read
            capi.Event.RegisterCallback(_ =>
            {
                float speed = (UniversalDisplayLibConfig.Current?.AnimationSpeedValue ?? 1f) * HauntOpenSpeedFactor;   // it creeps open
                int openMs = (int)(AnimSeconds(OpenAnimFor(pane), speed) * 1000f) + SqueakGraceMs;
                capi.Event.RegisterCallback(__ => { if (_creak == sound) StopHauntCreak(); }, Math.Max(250, openMs));
            }, 0);
        }

        /// <summary>
        /// Client: the snap (Calm: "need some noise for when the window snaps shut") - a swoosh as the haunted pane starts to
        /// slam, cut off the moment it lands so the slam is heard clean (Calm: "the close noise is too long and plays over the
        /// wood tool noise" - the swoosh runs 0.9 s, the fast close a fraction of that).
        /// </summary>
        void PlaySnapSwoosh(int pane)
        {
            if (capi == null || Pos == null) return;
            ILoadedSound sound = capi.World.LoadSound(new SoundParams
            {
                Location = new AssetLocation("game:sounds/effect/swoosh"),
                Position = new Vec3f(Pos.X + 0.5f, Pos.Y + 0.5f, Pos.Z + 0.5f),
                DisposeOnFinish = true,
                Range = 24f,
                Volume = 1f,
                Pitch = 1.3f
            });
            if (sound == null) return;
            sound.Start();
            float speed = (UniversalDisplayLibConfig.Current?.AnimationSpeedValue ?? 1f) * SlamSpeedFactor;
            int landMs = (int)Math.Clamp(AnimSeconds(CloseAnimFor(pane), speed) * 1000f, 60f, 1000f);
            // released by hand once faded: a sound stopped early is not one that "finished", so DisposeOnFinish may never
            // free it - one left per slam would pile up over a session (Calm: "will it overload the sound like my old mod?")
            capi.Event.RegisterCallback(_ =>
            {
                if (sound.IsDisposed) return;
                sound.FadeOutAndStop(0.03f);
                capi.Event.RegisterCallback(__ => { if (!sound.IsDisposed) sound.Dispose(); }, 150);
            }, landMs);
        }

        /// <summary>Client: cut the haunt creak short (the pane shut).</summary>
        void StopHauntCreak()
        {
            ILoadedSound sound = _creak;
            _creak = null;
            _creakPane = -1;
            if (sound != null && !sound.IsDisposed) { sound.FadeOutAndStop(0.08f); capi?.Event.RegisterCallback(_ => { if (!sound.IsDisposed) sound.Dispose(); }, 200); }
        }

        /// <summary>How long an animation of this block's shape runs at <paramref name="speed"/> (the game plays them at 30 frames a second).</summary>
        float AnimSeconds(string code, float speed)
        {
            if (string.IsNullOrEmpty(code) || speed <= 0 || Api == null) return 0.5f;
            AssetLocation path = Block?.Shape?.Base?.Clone().WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json");
            if (path == null) return 0.5f;
            // the frame count, read from the shape file once per animation rather than parsing the file every call
            int frames = AnimFrames.GetOrAdd(path + "|" + code, _ =>
                Api.Assets.TryGet(path)?.ToObject<Shape>()?.Animations?.FirstOrDefault(a => a.Code == code)?.QuantityFrames ?? -1);
            return frames < 0 ? 0.5f : frames / 30f / speed;
        }

        static readonly ConcurrentDictionary<string, int> AnimFrames = new();

        /// <summary>
        /// Server: a long creak from this window without it moving (Calm: "add in the occasional random creak"). False if it
        /// cannot open at all - a creak from a solid wall would give the game away.
        /// </summary>
        public bool HauntCreak(Random rnd)
        {
            if (_removed || Api?.Side != EnumAppSide.Server || !CanOpen) return false;
            // mostly the long creaks, now and then a short one (Calm: "add in an occasional short creak")
            string[] creaks = rnd.Next(10) < 4
                ? new[] { "woodcreak_1", "woodcreak_2", "woodcreak_3", "woodcreak_4" }
                : new[] { "woodcreak_long1", "woodcreak_long2", "woodcreak_long3" };
            Api.World.PlaySoundAt(new AssetLocation("game:sounds/block/creak/" + creaks[rnd.Next(creaks.Length)]),
                Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5, null, 0.85f + (float)rnd.NextDouble() * 0.25f, 20f, 0.8f);
            return true;
        }

        /// <summary>SetPaneState without the open/closed variant swap.</summary>
        void SetHauntedPane(int index, bool open)
        {
            paneStates[index] = open;
            AutoBoxesAngleChanged();
            MarkMeshesDirty();
            MarkDirty(true);
            RaisePaneChanged(this, index, open);
        }

        // -- More for modders (2026-10-13, Calm: "do all of them") --

        /// <summary>
        /// For code mods: a pane opened (true) or shut (false) - (block entity, pane index, open). Raised on the SERVER when
        /// the pane changes and on each CLIENT when the change arrives, so a listener on either side hears it. A listener
        /// that throws is logged and skipped, never allowed to break the window.
        /// </summary>
        public static event System.Action<BEUniversalDisplay, int, bool> PaneChanged;

        static void RaisePaneChanged(BEUniversalDisplay be, int pane, bool open)
        {
            var handlers = PaneChanged;
            if (handlers == null) return;
            foreach (System.Action<BEUniversalDisplay, int, bool> h in handlers.GetInvocationList())
            {
                try { h(be, pane, open); }
                catch (Exception e) { be.Api?.Logger.Error("[UniversalDisplayLib] a PaneChanged listener failed: {0}", e); }
            }
        }

        /// <summary>Client: the open/close sound for what just moved - a pane's own openSound / closeSound, else the block's.</summary>
        void PlayOpenCloseSound(bool[] before)
        {
            FrameBoxGroup[] groups = SurfaceBehavior?.FrameBoxGroups ?? Array.Empty<FrameBoxGroup>();
            for (int i = 0; i < paneStates.Length && i < groups.Length; i++)
            {
                bool was = before != null && i < before.Length && before[i];
                if (was == paneStates[i]) continue;
                string own = paneStates[i] ? groups[i].OpenSound : groups[i].CloseSound;
                if (!string.IsNullOrWhiteSpace(own)) { soundHandler?.PlayPaneSound(own); return; }
                break;
            }
            soundHandler?.PlaySlideSound(ChangedPanesAreSolid(before));
        }

        /// <summary>The block info's state line: the panes' own openState / closedState when any pane names them, else Open / Closed.</summary>
        string StateLine()
        {
            FrameBoxGroup named = (SurfaceBehavior?.FrameBoxGroups ?? Array.Empty<FrameBoxGroup>())
                .FirstOrDefault(g => !string.IsNullOrWhiteSpace(g.OpenState) || !string.IsNullOrWhiteSpace(g.ClosedState));
            if (named == null) return IsAnySealingPaneOpen ? Lang.Get("Open") : Lang.Get("Closed");
            bool open = paneStates != null && paneStates.Any(s => s);
            string own = open ? named.OpenState : named.ClosedState;
            return Lang.Get(!string.IsNullOrWhiteSpace(own) ? own : open ? "Open" : "Closed");
        }

        /// <summary>Server: each opening counts up, so a close timed for an earlier opening does nothing.</summary>
        int[] _openSeq = Array.Empty<int>();

        /// <summary>Server: shut this pane by itself after its autoCloseSeconds (FrameBoxGroup.AutoCloseSeconds), if it has one.</summary>
        void ScheduleAutoClose(int pane)
        {
            if (Api?.Side != EnumAppSide.Server) return;
            FrameBoxGroup[] groups = SurfaceBehavior?.FrameBoxGroups;
            if (groups == null || pane < 0 || pane >= groups.Length || groups[pane].AutoCloseSeconds <= 0f) return;
            if (_openSeq.Length <= pane) Array.Resize(ref _openSeq, pane + 1);
            int seq = ++_openSeq[pane];
            RegisterDelayedCallback(_ => AutoClose(pane, seq, 0), Math.Max(100, (int)(groups[pane].AutoCloseSeconds * 1000f)));
        }

        void AutoClose(int pane, int seq, int tries)
        {
            if (_removed || pane >= paneStates.Length || !paneStates[pane] || pane >= _openSeq.Length || _openSeq[pane] != seq) return;
            // something open in front of it (another window's leaf): wait for it, up to half a minute
            if (OpenNeighbourInTheWay(pane))
            {
                if (tries < 30) RegisterDelayedCallback(_ => AutoClose(pane, seq, tries + 1), 1000);
                return;
            }
            TogglePane(pane, null);
        }

        void SetFallback(int pane, bool on)
        {
            if (paneFallback.Length <= pane) Array.Resize(ref paneFallback, pane + 1);
            paneFallback[pane] = on;
        }

        /// <summary>
        /// Server: would pane <paramref name="pane"/>, opened with <paramref name="animOpen"/>, have room in every block it
        /// reaches? Each must be empty (air or something replaceable, no fluid) or already hold this window's own filler,
        /// and the player must be allowed to build there.
        /// </summary>
        bool HasRoomToOpen(int pane, string animOpen, IPlayer byPlayer)
        {
            FrameBoxGroup[] groups = SurfaceBehavior?.FrameBoxGroups ?? Array.Empty<FrameBoxGroup>();
            string[] anims = Enumerable.Range(0, groups.Length).Select(OpenAnimFor).ToArray();
            if (pane < 0 || pane >= anims.Length) return true;
            anims[pane] = animOpen ?? "";
            Reach.PaneBoxes measured = Reach.PaneBoxes.For(Api, MeasuredBlock()?.Shape, anims);
            Cuboidf open = measured?.Open[pane];
            if (open == null) return true;
            // the whole swing, not just where it ends (a classic leaf's tip sweeps across the opening on its way out)
            Cuboidf[] path = pane < measured.Sweep.Length && measured.Sweep[pane].Length > 0 ? measured.Sweep[pane] : new[] { open };

            IBlockAccessor ba = Api.World.BlockAccessor;
            foreach (Cuboidf part in path)
            foreach (var (cell, piece) in AutoBoxes.Cut(Turned(part)))
            {
                if (cell == (0, 0, 0)) continue;
                BlockPos at = Pos.AddCopy(cell.Item1, cell.Item2, cell.Item3);
                if (byPlayer != null && Api.World.Claims.TestAccess(byPlayer, at, EnumBlockAccessFlags.BuildOrBreak) != EnumWorldAccessResponse.Granted) return false;
                if (ba.GetBlockEntity(at) is BEAutoBoxesFiller filler)
                {
                    if (filler.Owner?.Equals(Pos) == true || filler.HasSharer(Pos)) continue;
                    // a tall window's own upper block, or a space another window's leaf swung into (two shutters folding into
                    // one gap - shared, as before the rebuild): room if the pane misses everything already there
                    bool someone = false;
                    foreach (BlockPos who in filler.AllOwners())
                    {
                        if (ba.GetBlockEntity(who) is not BEUniversalDisplay there) return false;   // a box we cannot read: taken
                        someone = true;
                        foreach (Cuboidf taken in there.AutoCollisionAt(at).Concat(there.OpenPartsIn(at))) if (Overlaps(piece, taken)) return false;
                    }
                    if (!someone) return false;
                    continue;
                }
                if (ba.GetBlock(at, BlockLayersAccess.Fluid).Id != 0) return false;
                Block solid = ba.GetBlock(at, BlockLayersAccess.Solid);
                if (solid.Id == 0 || solid.Replaceable >= 6000) continue;
                // Something is there: room if the pane misses what it takes up (Calm, 2026-10-07: a classic window opens
                // into the block of an OPEN External Shutter - its leaves folded away, only its thin frame left)
                foreach (Cuboidf taken in OccupiedIn(ba, at, solid, Pos)) if (Overlaps(piece, taken)) return false;
            }
            return true;
        }

        /// <summary>
        /// How far two parts may meet before one is in the other's way: more than this on every axis (1/64 block, a quarter
        /// voxel). An open External Shutter's frame and a classic window's leaf touch by ~0.01 voxel (the 1-high WIDE leaf,
        /// sitting 0.1 voxel off in its model, by 0.13 mid-swing) and pass; a shutter folding into the shutter beside it
        /// overlaps its frame by half a voxel and still falls back to 90 degrees. Checked on every classic window against
        /// the 1/2/3-high shutters in each leaf state, 2026-10-07.
        /// </summary>
        const float RoomOverlap = 1 / 64f;

        static bool Overlaps(Cuboidf a, Cuboidf b) =>
            Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1) > RoomOverlap
            && Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1) > RoomOverlap
            && Math.Min(a.Z2, b.Z2) - Math.Max(a.Z1, b.Z1) > RoomOverlap;

        /// <summary>
        /// What a block in a space takes up there: a window its collision (it follows its panes, and holds other windows'
        /// leaves open into it - not the asking window's own), anything else its outline.
        /// </summary>
        static Cuboidf[] OccupiedIn(IBlockAccessor ba, BlockPos at, Block block, BlockPos asker) =>
            (ba.GetBlockEntity(at) is BEUniversalDisplay there ? there.CollisionExcept(asker) : block.GetSelectionBoxes(ba, at)) ?? Array.Empty<Cuboidf>();

        /// <summary>
        /// Server, before a pane SHUTS: is another window's open part where this pane shuts? (the 2026-10-03 rule - a
        /// shutter cannot close while the window behind it is open into its block - rebuilt on the measured boxes.)
        /// A leaf that opened into an occupied block has no filler there, but its owner still knows its piece of that block.
        /// </summary>
        bool OpenNeighbourInTheWay(int pane)
        {
            FrameBoxGroup[] groups = SurfaceBehavior?.FrameBoxGroups ?? Array.Empty<FrameBoxGroup>();
            string[] anims = Enumerable.Range(0, groups.Length).Select(OpenAnimFor).ToArray();
            Reach.PaneBoxes measured = pane >= 0 && pane < anims.Length ? Reach.PaneBoxes.For(Api, MeasuredBlock()?.Shape, anims) : null;
            Cuboidf shut = measured?.Shut[pane];
            if (shut == null) return false;
            // where it shuts AND the way back there (the same swing as opening, run backwards)
            Cuboidf[] path = pane < measured.Sweep.Length ? measured.Sweep[pane].Append(shut).ToArray() : new[] { shut };

            IBlockAccessor ba = Api.World.BlockAccessor;
            foreach (Cuboidf swung in path)
            foreach (var (cell, piece) in AutoBoxes.Cut(Turned(swung)))
            {
                BlockPos at = Pos.AddCopy(cell.Item1, cell.Item2, cell.Item3);
                var asked = new HashSet<BlockPos>();
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    BlockPos n = at.AddCopy(dx, dy, dz);
                    // a window beside, or the upper block of a tall one (its filler leads to the window below)
                    BlockEntity be = ba.GetBlockEntity(n);
                    var candidates = be is BEAutoBoxesFiller f ? f.AllOwners().Select(ba.GetBlockEntity) : new[] { be };
                    foreach (BlockEntity cand in candidates)
                    {
                        if (cand is not BEUniversalDisplay other || other.Pos.Equals(Pos) || !asked.Add(other.Pos)) continue;
                        // its boxes there, AND where its open panes really are - a pane whose box stays shut (the edge awning)
                        // has no box in this block but its glass is here all the same (Calm, 2026-10-07)
                        foreach (Cuboidf part in other.AutoCollisionAt(at).Concat(other.OpenPartsIn(at))) if (Overlaps(piece, part)) return true;
                    }
                }
            }
            return false;
        }
    }
}

