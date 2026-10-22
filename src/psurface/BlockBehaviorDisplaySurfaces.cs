using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace UniversalDisplayLib
{
    /// <summary>
    /// Our own take on vanilla's <c>Display</c> block behaviour.
    ///
    /// Parses <c>psurface*</c> elements out of the block shape and owns the grid
    /// of placement pads, exactly like BlockBehaviorDisplay — but keeps the frame
    /// and pane boxes in the same place, and hands all box queries to the block
    /// entity so open/closed state can drive them.
    ///
    /// Implements <see cref="ICustomSelectionBoxRender"/> so the green/red ghost
    /// preview of the held item still appears.
    /// </summary>
    public class BlockBehaviorDisplaySurfaces : StrongBlockBehavior, ICustomSelectionBoxRender
    {
        public DisplaySurface[] Surfaces { get; private set; } = Array.Empty<DisplaySurface>();

        /// <summary>Grid pads, in block-local coordinates, unrotated.</summary>
        CuboidfWithId[] gridBoxes = Array.Empty<CuboidfWithId>();
        Dictionary<int, CuboidfWithId[]> gridsBySurface = new();
        public CuboidfWithId[] GridBoxes
        {
            get => gridBoxes;
            private set
            {
                gridBoxes = value;
                gridsBySurface = value.GroupBy(box => DisplaySlotId.Decode(box.Id)?.SurfaceIndex ?? -1)
                    .ToDictionary(group => group.Key, group => group.ToArray());
            }
        }
        public CuboidfWithId[] GridBoxesForSurface(int index)
            => gridsBySurface.TryGetValue(index, out var boxes) ? boxes : Array.Empty<CuboidfWithId>();

        /// <summary>Element names to strip when tesselating, so the markers stay invisible.</summary>
        public string[] SurfaceElementNames { get; private set; } = Array.Empty<string>();

        public FrameBoxGroup[] FrameBoxGroups { get; private set; } = Array.Empty<FrameBoxGroup>();
        public CollisionBoxGroup[] CollisionBoxGroups { get; private set; } = Array.Empty<CollisionBoxGroup>();

        protected int maxXDivisions = 32;
        protected int maxZDivisions = 32;

        private ICoreClientAPI capi;

        public static readonly Size3f DefaultItemSize = new Size3f(6f, 4f, 6f);

        /// <summary>
        /// Footprint for a plant displayed in its potted form, matched to vanilla's own
        /// flowerpot. Measured rather than copied: <c>empty.json</c> spans Y 0..7 and X 5..11.
        ///
        /// **THE HEIGHT IS NO LONGER WHAT POSITIONS THE PLANT.** It used to be: a stacked
        /// item sat at <c>level x the height of the item being PLACED</c>, so 7 only landed a
        /// plant on the rim of a pot that was also 7. Fixed 2026-08-05 — <c>StackBaseY</c>
        /// now sums the heights of the items actually BELOW, so a plant meets the rim of a pot
        /// of any height and this number no longer has to match anything.
        ///
        /// What 7 still governs is the plant's own footprint, and how far it would lift
        /// anything stacked on top of IT.
        /// </summary>
        public static readonly Size3f PottedItemSize = new Size3f(6f, 7f, 6f);

        /// <summary>
        /// Vanilla's own display category, and the fallback when a psurface marker names
        /// none. Also what an unpatched item's <c>displayable</c> entry is keyed under, so
        /// it doubles as the source to inherit sizing and stacking from.
        /// </summary>
        public const string DefaultDisplayCategory = "shelf";

        public BlockBehaviorDisplaySurfaces(Block block) : base(block)
        {
        }

        public override void Initialize(JsonObject properties)
        {
            base.Initialize(properties);
            maxXDivisions = properties["maxXDivisions"].AsInt(32);
            maxZDivisions = properties["maxZDivisions"].AsInt(32);
        }

        public override void OnLoaded(ICoreAPI api)
        {
            base.OnLoaded(api);
            capi = api as ICoreClientAPI;

            // NO event bus registration here. It used to register OnGetTransform per block
            // TYPE, which is bounded and so far less harmful than the per-block-entity case
            // — but event bus listeners cannot be unregistered at all in this API, so it
            // still accumulated across world loads within one client session. The mod
            // system registers once instead; see UniversalDisplayLibMod.StartClientSide.

            FrameBoxGroups = block.Attributes?["frameBoxGroups"].AsObject<FrameBoxGroup[]>() ?? Array.Empty<FrameBoxGroup>();
            CollisionBoxGroups = block.Attributes?["collisionBoxGroups"].AsObject<CollisionBoxGroup[]>() ?? Array.Empty<CollisionBoxGroup>();

            ParseSurfaces(api);
        }

        /// <summary>
        /// What <see cref="ParseSurfaces"/> last built THIS block type's grid with, so a change can be
        /// detected and a no-op rebuild skipped. Per block type, not static (2026-10-22): in singleplayer the
        /// client and the server each have their own blocks but shared one static, so whichever side rebuilt
        /// first made the other skip its rebuild and keep the old grid.
        /// </summary>
        private bool builtPrecise = true;

        /// <summary>
        /// Bumped every time the grid is rebuilt.
        ///
        /// WHY A COUNTER RATHER THAN GOING AND FINDING EVERY BLOCK. Each block entity caches
        /// its own selection boxes derived from this grid, and rebuilding the grid does not
        /// touch those - which is why changing PrecisePlacement appeared to need a relog even
        /// after the rebuild landed. Walking every loaded chunk to invalidate them would mean
        /// new chunk-iteration machinery and a cost paid up front; comparing an int when a
        /// block's boxes are next ASKED FOR costs nothing until then, and a block nobody
        /// looks at never pays at all.
        /// </summary>
        public static int GridGeneration { get; private set; }

        /// <summary>Main thread: every display block rebuilds its boxes when next asked - a measured item size arrived (MeasuredItemSizes).</summary>
        public static void BumpGeneration() => GridGeneration++;

        /// <summary>
        /// Rebuild every display block's placement grid, for when PrecisePlacement changes.
        ///
        /// WHY A REBUILD AND NOT A RESTART. The grid is built once per block type at load,
        /// so the value arriving from the server later cannot be applied by reading it - the
        /// boxes already exist. Calm's call, 2026-09-10: do the rebuild, and leave the cost
        /// to the server owner, since they are the only one who can change the setting that
        /// triggers it.
        ///
        /// Returns how many block types were rebuilt, or -1 when nothing needed doing.
        /// Skipping the no-op matters: this walks every block in the game and re-reads a
        /// shape for each display block, which is not something to do on a whim.
        /// </summary>
        public static int RebuildAll(ICoreAPI api)
        {
            if (api?.World?.Blocks == null) return -1;
            bool precise = ServerRules.PrecisePlacementOn;

            int rebuilt = 0;
            foreach (Block b in api.World.Blocks)
            {
                var beh = b?.GetBehavior<BlockBehaviorDisplaySurfaces>();
                if (beh == null || beh.builtPrecise == precise) continue;

                beh.ParseSurfaces(api);
                rebuilt++;
            }

            if (rebuilt == 0) return -1;
            GridGeneration++;
            return rebuilt;
        }

        private void ParseSurfaces(ICoreAPI api)
        {
            builtPrecise = ServerRules.PrecisePlacementOn;   // first, so a block with no shape is not retried every rebuild
            AssetLocation shapePath = block.Shape?.Base?.Clone()
                .WithPathPrefixOnce("shapes/")
                .WithPathAppendixOnce(".json");

            if (shapePath == null) return;

            Shape shape = api.Assets.TryGet(shapePath)?.ToObject<Shape>();
            if (shape?.Elements == null)
            {
                api.Logger.Warning("[UniversalDisplayLib] Could not load shape '{0}' for block {1}; no placement surfaces.", shapePath, block.Code);
                return;
            }

            var surfaces = new List<DisplaySurface>();
            var boxes = new List<CuboidfWithId>();

            // Read ONCE, here, because the grid is built once at block load — which is also
            // why changing it needs a restart. Two or one, never more: half a voxel is already
            // one pixel of a 32x32 texture, and doubling again would be sixteen times the
            // boxes for a difference nobody can see.
            // ServerRules, not the local config: on a server this is the server's rule, and
            // asking our own copy is how a client came to draw a 32-grid against a server
            // running a 16 one - see ServerRules.
            bool precise = builtPrecise;

            int steps = precise ? 2 : 1;

            // Only top-level elements, matching vanilla.
            foreach (ShapeElement element in shape.Elements)
            {
                DisplaySurface surface = DisplaySurface.TryParse(element, api.Logger);
                if (surface == null) continue;

                surfaces.Add(surface);
                boxes.AddRange(surface.BuildGridBoxes(maxXDivisions, maxZDivisions, steps));
            }

            Surfaces = surfaces.OrderBy(s => s.Index).ToArray();
            GridBoxes = boxes.ToArray();
            SurfaceElementNames = Surfaces.Select(s => s.ElementName).ToArray();
        }

        /// <summary>
        /// The surface whose NAME carries this index, e.g. 2 for
        /// <c>psurface2-w14-h7-d6-windowdisplay</c>.
        ///
        /// Matched on the parsed index rather than array position on purpose. Slot ids
        /// encode <c>surface.Index</c> (see BuildGridBoxes) and
        /// <c>showSurfacesWhenStacked</c> lists the same numbers, so a positional lookup
        /// silently disagreed with both as soon as the indices in a shape were not
        /// contiguous from 0 — psurface4 with no psurface3 would return the wrong surface
        /// or none, and the pads would stop working with nothing logged.
        /// </summary>
        public DisplaySurface GetSurface(int index)
        {
            for (int i = 0; i < Surfaces.Length; i++)
            {
                if (Surfaces[i].Index == index) return Surfaces[i];
            }
            return null;
        }

        public string GetDisplayCategory(int surfaceIndex)
            => GetSurface(surfaceIndex)?.DisplayCategory ?? "shelf";

        // ── Box routing ─────────────────────────────────────────────────────
        // The block entity owns the complete set (frame + grid + placed items).
        // PreventSubsequent returns its cached array directly. PreventDefault makes
        // BlockGeneric copy it through List/ToArray on every query, also defeating
        // Siding's source-array cache and shifting every placement pad again.

        public override Cuboidf[] GetSelectionBoxes(IBlockAccessor blockAccessor, BlockPos pos, ref EnumHandling handled)
        {
            if (blockAccessor.GetBlockEntity(pos) is BEUniversalDisplay be)
            {
                handled = EnumHandling.PreventSubsequent;
                return be.GetSelectionBoxes();
            }
            return base.GetSelectionBoxes(blockAccessor, pos, ref handled);
        }

        public override Cuboidf[] GetCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos, ref EnumHandling handled)
        {
            if (blockAccessor.GetBlockEntity(pos) is BEUniversalDisplay be)
            {
                handled = EnumHandling.PreventSubsequent;
                return be.GetCollisionBoxes();
            }
            return base.GetCollisionBoxes(blockAccessor, pos, ref handled);
        }

        public override bool DoPartialSelection(IWorldAccessor world, BlockPos pos, ref EnumHandling handled)
        {
            handled = EnumHandling.PreventDefault;
            return true;
        }

        /// <summary>
        /// Rotates the block-breaking decal to match the placed rotation. Without this
        /// the crack overlay is built from the unrotated shape and sits at whatever
        /// angle the block was authored at, not the angle it was placed at.
        /// </summary>
        public override void OnDecalTesselation(IWorldAccessor world, MeshData decalMesh, BlockPos pos, ref EnumHandling handled)
        {
            handled = EnumHandling.PreventDefault;

            if (world.BlockAccessor.GetBlockEntity(pos) is BEUniversalDisplay be && be.MeshAngleRad != 0f)
            {
                decalMesh.Rotate(new Vec3f(0.5f, 0.5f, 0.5f), 0f, be.MeshAngleRad, 0f);
            }
        }

        // ── Particles from stored items ─────────────────────────────────────

        /// <summary>
        /// Opts the block into client particle ticks while something stored on a surface
        /// can emit — a candle or an oil lamp on the sill.
        ///
        /// <c>handling</c> is left PassThrough when the answer is no, so the block's own
        /// ParticleProperties branch still gets its say. Only a yes claims the decision:
        /// vanilla treats any non-PassThrough behaviour as having answered for the block.
        ///
        /// The client re-evaluates this on a **block change**, not a block-entity one.
        /// That costs nothing here because anything with a flame also emits light, and
        /// BEUniversalDisplay.ApplyLight already exchanges the block on placement — so the
        /// ticker registers the moment a candle goes down. An item that emitted particles
        /// but no light would wait for the periodic rescan instead.
        ///
        /// **Not main-thread.** The rescan calls this from `OnSeperateThreadGameTick`, on
        /// the "blockticking" thread — see the note on HasParticleEmittingItem.
        /// </summary>
        public override bool ShouldReceiveClientParticleTicks(IWorldAccessor world, IPlayer byPlayer, BlockPos pos, ref EnumHandling handling)
        {
            try
            {
                if (world.BlockAccessor.GetBlockEntity(pos) is not BEUniversalDisplay be) return false;
                if (!be.HasParticleEmittingItem()) return false;
            }
            catch (Exception e)
            {
                WarnOnceOffThread(world, e);
                return false;
            }

            handling = EnumHandling.PreventDefault;
            return true;
        }

        /// <summary>
        /// Runs on the async particle thread, so the block entity is fetched through
        /// <c>manager.BlockAccess</c> rather than the world accessor — vanilla's own
        /// BlockOilLamp reaches for Api.World here, which is not thread-safe.
        /// </summary>
        public override void OnAsyncClientParticleTick(IAsyncParticleManager manager, BlockPos pos, float windAffectednessAtPos, float secondsTicking)
        {
            try
            {
                if (manager.BlockAccess.GetBlockEntity(pos) is BEUniversalDisplay be)
                {
                    be.SpawnStoredItemParticles(manager, pos, windAffectednessAtPos);
                }
            }
            catch (Exception e)
            {
                WarnOnceOffThread(null, e);
            }
        }

        private static bool _warnedOffThread;

        /// <summary>
        /// Both particle hooks run on client threads where nothing catches for us — an
        /// escaping exception ends the thread and drops the player to the main menu, as an
        /// NPE in ResolveBlocksOrItems did. Particles are decoration, so they are never
        /// worth that. Logged once rather than per tick, since these fire continuously.
        /// </summary>
        private void WarnOnceOffThread(IWorldAccessor world, Exception e)
        {
            if (_warnedOffThread) return;
            _warnedOffThread = true;

            ILogger logger = (world ?? capi?.World)?.Logger;
            logger?.Warning("[UniversalDisplayLib] Stored-item particles failed and were skipped: {0}", e);
        }

        // ── Item sizing ─────────────────────────────────────────────────────

        /// <summary>
        /// Resolves the placement footprint / transform for a held or stored item.
        /// Same resolution order as vanilla so items authored for cabinets and
        /// shelves work unchanged.
        /// </summary>
        /// <summary>
        /// Resolves an item's display properties and GUARANTEES a usable Size.
        ///
        /// **A null Size crashes the client from the render thread**, in
        /// <see cref="GetCentreOffset"/> via RegenSelectionBoxes, and it is reachable from
        /// ordinary JSON: any <c>displayable.&lt;category&gt;</c> entry that omits <c>size</c>
        /// deserialises with Size null. The in-game transform editor produces exactly that —
        /// it writes a <c>transform</c> into an entry that did not exist, so tuning an item
        /// with no display data of its own leaves a transform and nothing else behind.
        ///
        /// Found 2026-08-02 by doing precisely that to a flower. Filling the default in here
        /// rather than guarding each caller means no future reader can hit it either.
        /// </summary>
        public static DisplayableAttributes GetDisplayableAttributes(ItemSlot slot, string displayType)
        {
            DisplayableAttributes dattr = ResolveDisplayableAttributes(slot, displayType);
            if (dattr == null) return null;

            // A PLANT'S OWN DEFAULTS FILL ANY GAP BEFORE THE GENERIC ONES DO.
            //
            // This matters because of what the transform editor leaves behind. Editing a
            // flower creates a `displayable.windowdisplay` carrying a transform and NOTHING
            // else, and that entry then wins the resolution chain — so without this the plant
            // silently loses both the things that made it work:
            //
            //   * its SIZE drops to the generic 6x4x6, so the height that puts it on a pot's
            //     rim goes from 7 to 4 and it lands three voxels low
            //   * its SHAPE goes, so it reverts from the potted form to a plain block mesh
            //     mid-edit — the editor destroying the very thing being tuned
            //
            // Filling both back in means an author can nudge the position and watch the real
            // plant move, which is the whole point of having the editor work on it.
            if (dattr.Size == null || dattr.Shape == null)
            {
                PlantContainerProps potted = PottedProps(slot?.Itemstack?.Collectible);
                if (potted != null)
                {
                    dattr.Size ??= PottedItemSize;
                    dattr.Shape ??= potted.Shape?.Clone();   // the props are shared now (PottedProps)
                }
            }

            dattr.Size ??= DefaultItemSize;

            // AUTHORING OVERRIDE — the in-game size editor, client and session only.
            //
            // Applied LAST so it beats every branch above, and kept OUTSIDE the collectible's
            // attribute tree deliberately. Writing a `size` into `displayable.<category>`
            // would leave an entry carrying only that, and resolution is WHOLESALE — so the
            // item would lose its transform, behaviour and shape the instant it was tuned.
            // That is exactly the trap the transform editor springs on plants, worked around
            // a few lines above; there is no reason to build a second one.
            //
            // NOT PERSISTED AND THERE IS NO REVERT. The number is meant to be read off the
            // printout and typed into a patch file, which is the only place it becomes real.
            // Reloading the world drops it. Cleared in StartClientSide, because a client
            // static outlives the world it describes.
            // MEASURED FOOTPRINT (2026-10-07): the size the item is DRAWN at on this category, once a client has measured it -
            // fitting and stacking then follow the model and a patch need not write a size. Before the .udedit override below,
            // so tuning still wins. See MeasuredItemSizes for what is left on the written size.
            if (MeasuredItemSizes.TryGet(slot, displayType, out Size3f measured))
            {
                dattr.Size = measured;
            }

            if (SizeOverrides.Count > 0 && slot?.Itemstack?.Collectible != null
                && SizeOverrides.TryGetValue((slot.Itemstack.Collectible, TypedKeyOf(slot)), out Size3f tuned))
            {
                dattr.Size = tuned;
            }

            // THE PLAYER'S SIZE (lib 1.4.0, the Resize key): last, so every fit and stacking check sees the space the item
            // really takes. Placed: the size it carries; in hand: its holder's pending size. See ItemScale.
            dattr.Size = ItemScale.Scaled(dattr.Size, ItemScale.For(slot));

            return dattr;
        }

        /// <summary>
        /// The stack's <c>type</c> for an item whose display data is keyed by type rather than
        /// by block code — vanilla clutter and anything else carrying
        /// <c>TypedDisplayableProps</c>. Null for everything else, which is nearly everything.
        ///
        /// **Why this exists.** Clutter declares its sizes in that behaviour's properties, a map
        /// keyed by type (<c>pottery/art1</c>) and then by category, and the type lives in the
        /// ITEMSTACK'S ATTRIBUTES. So every piece of clutter in the game is the same
        /// <see cref="CollectibleObject"/> and the same block <c>clutter</c> — meaning anything
        /// keyed on the collectible, as the size editor and its output file both were, flattens
        /// a hundred different objects onto one set of numbers. Tune one pot and they all move.
        ///
        /// Public source: <c>anegostudios/vssurvivalmod</c>,
        /// <c>CollectibleBehavior/BehaviorDisplayableProps.cs</c> — the lookup is
        /// <c>Attributes.GetString("type")</c> matched against the map's keys with
        /// <c>WildcardUtil.Match</c>, then a bare <c>TryGetValue(displayType)</c> with no
        /// fallback. That missing fallback is why our shelf re-ask exists at all.
        /// </summary>
        public static string TypedKeyOf(ItemSlot slot)
        {
            ItemStack stack = slot?.Itemstack;
            if (stack?.Collectible?.GetCollectibleInterface<IDisplayableProps>() == null) return null;

            string type = stack.Attributes?.GetString("type", null);
            return string.IsNullOrEmpty(type) ? null : type;
        }

        /// <summary>
        /// The typed-props behaviour on a collectible, or null. Separate from
        /// <see cref="TypedKeyOf"/> because the writer needs the map itself, not just the key.
        /// </summary>
        public static CollectibleBehaviorTypedDisplayableProps TypedPropsBehaviour(CollectibleObject c)
            => c?.GetCollectibleBehavior<CollectibleBehaviorTypedDisplayableProps>(withInheritance: true);

        /// <summary>
        /// Per-collectible size overrides set by the in-game size editor. Empty in normal
        /// play, so the lookup above costs one <c>Count</c> check and nothing else.
        ///
        /// Keyed on the COLLECTIBLE **and its typed key** — see <see cref="TypedKeyOf"/>. For
        /// nearly everything the second half is null and this behaves as it always did: one
        /// number shared by every stack of that item on every window, which is what makes it
        /// useful for writing a patch. Tuning one flower on one sill tunes them all.
        ///
        /// The typed half exists for clutter, where one collectible covers ~100 objects whose
        /// sizes differ. Without it, tuning a pot resized every pot, plate and bottle in the
        /// game at once.
        /// </summary>
        public static readonly Dictionary<(CollectibleObject, string), Size3f> SizeOverrides = new();

        /// <summary>
        /// A working copy of props that belong to somebody else, so the callers downstream can
        /// fill defaults and apply overrides without writing into another mod's — or vanilla's
        /// — live data. Only the fields this library reads are carried across; anything else
        /// would be copied for no one.
        /// </summary>
        private static DisplayableAttributes CopyOf(DisplayableAttributes src)
        {
            if (src == null) return null;

            return new DisplayableAttributes
            {
                Size = src.Size,
                Transform = src.Transform,
                Shape = src.Shape,
                Behavior = src.Behavior,
                RandYRotAngle = src.RandYRotAngle,
            };
        }

        /// <summary>
        /// READ ONCE PER ITEM AND CATEGORY (code check 2026-10-22). An item with no IDisplayableProps answers from its own
        /// attributes alone, which used to be deserialised on every call - per stored item on every box rebuild, and several
        /// times a frame while placing. Each caller still gets its own copy (FreshCopyOf), as when every call parsed.
        /// Items with the interface (clutter's typed props) can answer per stack and are still asked every time.
        /// Cleared on world start and by the transform editor's writes (ForgetDisplayData). Each entry also remembers the
        /// attributes object it was read from: an item whose attributes are REPLACED later (another mod, or the regression
        /// suite) is read again rather than answered from the old ones.
        /// </summary>
        static readonly ConcurrentDictionary<(CollectibleObject, string), (JsonObject From, DisplayableAttributes Entry)> attributeEntries = new();
        static readonly ConcurrentDictionary<CollectibleObject, (JsonObject From, PlantContainerProps Props)> pottedProps = new();
        static readonly ConcurrentDictionary<(CollectibleObject, string), (JsonObject From, bool BySize)> outlineRules = new();

        public static void ForgetDisplayData()
        {
            attributeEntries.Clear();
            pottedProps.Clear();
            outlineRules.Clear();
        }

        /// <summary>
        /// A copy for one caller: its own object (downstream sets the size on it) and its own shape. The TRANSFORM IS
        /// SHARED - nothing changes one after this point: its defaults were filled in once when the entry was cached
        /// (the only write anything made, EnsureDefaultValues, is then a no-op), and the tuner clones before editing.
        /// </summary>
        private static DisplayableAttributes FreshCopyOf(DisplayableAttributes src)
        {
            DisplayableAttributes copy = CopyOf(src);
            if (copy?.Shape != null) copy.Shape = copy.Shape.Clone();
            return copy;
        }

        private static DisplayableAttributes ResolveDisplayableAttributes(ItemSlot slot, string displayType)
        {
            CollectibleObject collectible = slot?.Itemstack?.Collectible;
            if (collectible == null) return null;

            IDisplayableProps props = collectible.GetCollectibleInterface<IDisplayableProps>();
            if (props != null) return ResolveDisplayableAttributes(slot, collectible, displayType, props);

            JsonObject from = collectible.Attributes;
            if (!attributeEntries.TryGetValue((collectible, displayType), out var known) || !ReferenceEquals(known.From, from))
            {
                DisplayableAttributes entry = ResolveDisplayableAttributes(null, collectible, displayType, null);
                entry?.Transform?.EnsureDefaultValues();   // filled once, here, so every caller can share it (FreshCopyOf)
                known = (from, entry);
                attributeEntries[(collectible, displayType)] = known;
            }
            return FreshCopyOf(known.Entry);
        }

        /// <summary><paramref name="props"/> null: the item has no IDisplayableProps, and <paramref name="slot"/> is not read.</summary>
        private static DisplayableAttributes ResolveDisplayableAttributes(ItemSlot slot, CollectibleObject collectible,
                                                                          string displayType, IDisplayableProps props)
        {

            // COPIED, NOT RETURNED AS-IS. What comes back here belongs to whoever implements
            // the interface — for clutter it is the live entry inside the behaviour's own
            // PropsByCode map — and everything downstream MUTATES what it is given:
            // `Size ??= DefaultItemSize`, the plant backfill, and the size override all write
            // straight onto it. Without the copy, resolving one clutter item stamped our
            // defaults permanently into vanilla's map for that type, for the rest of the
            // session and for every block in the world reading it.
            //
            // Exactly the fault the cached mesh had a few hours earlier, in a different place:
            // anything obtained from an interface someone else implements is not ours to write
            // on unless we copied it first.
            DisplayableAttributes fromInterface = CopyOf(props?.GetDisplayableProps(slot, displayType));

            if (fromInterface != null)
            {
                // A TYPE-KEYED ENTRY INHERITS THAT TYPE'S OWN SHELF SIZE when it does not state
                // one. Clutter keeps its real size under `shelf`, so a patch adding a
                // `windowdisplay` entry for a transform alone used to answer first, carry no
                // size, and drop the item to the generic default — it visibly shrank. Vanilla's
                // number now stands unless the patch deliberately replaces it.
                //
                // Size only. `null` unambiguously means "absent", while an omitted `behavior`
                // is indistinguishable from one explicitly set to Default — so restating
                // `behavior` stays the author's job, as the cookbook says.
                if (fromInterface.Size == null && displayType != DefaultDisplayCategory)
                {
                    DisplayableAttributes shelf = props.GetDisplayableProps(slot, DefaultDisplayCategory);
                    fromInterface.Size = shelf?.Size;
                }

                return fromInterface;
            }

            DisplayableAttributes fromAttributes = collectible.Attributes?["displayable"][displayType].AsObject<DisplayableAttributes>();
            if (fromAttributes != null)
            {
                // AN ENTRY WITH NO SHAPE KEEPS THE SHELF'S (2026-10-10). A patch giving this category only a transform
                // replaced the game's shelf entry wholesale, so a garment lost its folded model and was drawn as worn.
                // Shape only, as the typed-props path above keeps only the size: nothing else is ambiguous.
                if (fromAttributes.Shape == null && displayType != DefaultDisplayCategory)
                    fromAttributes.Shape = collectible.Attributes?["displayable"][DefaultDisplayCategory]["shape"]
                        .AsObject<CompositeShape>();
                return fromAttributes;
            }

            // Same inheritance for the interface path. Clutter declares its sizes through a
            // TypedDisplayableProps behaviour rather than plain attributes, and that class
            // does a bare TryGetValue(displayType) with no fallback — so asking for
            // "windowdisplay" returns null and every piece of clutter was rejected outright.
            // Its map also lives in behaviour properties, so the attributes fallback below
            // cannot see it either. Asking the interface again for "shelf" covers all of it,
            // and any other mod using the same behaviour, without patching ~100 entries.
            if (displayType != DefaultDisplayCategory)
            {
                DisplayableAttributes shelfFromInterface = CopyOf(props?.GetDisplayableProps(slot, DefaultDisplayCategory));
                if (shelfFromInterface != null) return shelfFromInterface;
            }

            // Inherit vanilla's own shelf entry when this item has no windowdisplay one.
            //
            // Worth the extra step because that object carries far more than a transform:
            // Size, Behavior, Category and RandYRotAngle. The flag branches below can only
            // synthesise a Size and a Transform, leaving Behavior at Default — so a bowl,
            // which vanilla declares as `behavior: "Stacking"` with `size: {5,2,5}`, lost
            // both its stacking and its real footprint and fell back to a generic 6x4x6.
            //
            // Anything authored for a vanilla shelf therefore works here untouched, and a
            // patched `displayable.windowdisplay` still wins outright above. Note it wins
            // WHOLESALE, not merged: an override that wants stacking has to say so itself.
            if (displayType != DefaultDisplayCategory)
            {
                DisplayableAttributes fromShelf = collectible.Attributes?["displayable"][DefaultDisplayCategory]
                    .AsObject<DisplayableAttributes>();
                if (fromShelf != null) return fromShelf;
            }

            if (collectible.Attributes?.IsTrue("shelvable") == true)
            {
                return new DisplayableAttributes
                {
                    Size = DefaultItemSize,
                    Transform = collectible.Attributes["onshelfTransform"].AsObject<ModelTransform>()
                                ?? collectible.Attributes["onDisplayTransform"].AsObject<ModelTransform>()
                };
            }

            // Opt-in flag, so content can allow items vanilla would reject.
            //
            // `universaldisplayable` is the name going forward. `windowdisplayable` is still
            // read because it is PUBLISHED API — an item patch anywhere may already set it,
            // and a flag that silently stops working is the worst kind of break. Nothing in
            // this project's own content or patches ever used it, which is what makes
            // introducing the new name free.
            if (collectible.Attributes?.IsTrue("universaldisplayable") == true
                || collectible.Attributes?.IsTrue("windowdisplayable") == true)
            {
                return new DisplayableAttributes
                {
                    Size = DefaultItemSize,
                    Transform = collectible.Attributes[TransformTarget].AsObject<ModelTransform>()
                                ?? collectible.Attributes[LegacyTransformTarget].AsObject<ModelTransform>()
                };
            }

            // LAST RESORT: a plant that vanilla knows how to put in a flowerpot.
            //
            // Flowers carry NO display data at all — no `displayable`, no `shelvable` — so
            // without this they cannot be placed on a surface and the click falls through to
            // ordinary block placement, which answers "requires fertile ground". What they DO
            // carry is `plantContainable`, which is how they get into vanilla's own pots, and
            // it holds exactly what is needed: the potted shape and the per-plant textures.
            //
            // Reading it rather than patching each flower is the point. Every pottable plant
            // declares it, vanilla and modded alike, because it is the only way into a
            // flowerpot — so this covers plants this project has never seen, for free.
            //
            // DELIBERATELY THE LAST BRANCH. Anything with real display data of its own keeps
            // it, so nothing that works today can change behaviour. Only items that would
            // otherwise be rejected outright reach here.
            PlantContainerProps potted = PottedProps(collectible);
            if (potted != null)
            {
                return new DisplayableAttributes
                {
                    // Matched to vanilla's flowerpot, which declares 6 x 7 x 6.
                    //
                    // Height used to decide where the plant itself sat, because the lift was
                    // level x the plant's OWN height — which is why 7 had to match the pot.
                    // It no longer does: `BEUniversalDisplay.StackBaseY` lifts by the heights of
                    // the items actually underneath, so a plant lands on the rim of a pot of
                    // any height. What this 7 still governs is the plant's own footprint and
                    // how far it would lift anything stacked on top of IT.
                    Size = PottedItemSize,

                    // Without this a plant cannot go ON a pot at all: stacking is gated on
                    // the item being placed, not on what is underneath.
                    Behavior = EnumDisplayableBehavior.Stacking,

                    Shape = potted.Shape,

                    // The plant shape is modelled from the block FLOOR — measured, both
                    // `contents.json` and `contents-lilyofthevalley.json` span Y 0..16 —
                    // because its lowest voxels are meant to be buried in the pot's soil.
                    // A 7-tall pot lifts the plant by 7 and puts the part that should be
                    // hidden above the rim. That is the "it floats" report.
                    //
                    // Cancelling that lift (7 voxels = 7/16 of a block) leaves the plant drawn
                    // where it would sit inside a real pot.
                    //
                    // TUNED FOR A 7-TALL POT, which is vanilla's. Now that the lift follows the
                    // item below, a pot of another height moves the plant by the difference —
                    // deeper in a tall planter, higher out of a short pot. Only reachable by a
                    // plant with no `plantContainable` transform of its own; if it looks wrong
                    // on some modded pot, that is what the editor is for.
                    //
                    // A STARTING POINT, NOT A FINAL NUMBER. It is arithmetic, and the one
                    // lesson this project keeps relearning is that display offsets get tuned
                    // by eye. The transform editor's Window tab will now move a plant
                    // properly, so nudge it and put the value found here.
                    //
                    // A plant declaring its own transform keeps it — that is the author
                    // having already said where it goes.
                    Transform = potted.Transform ?? new ModelTransform
                    {
                        Translation = new Vec3f(0f, -7f / 16f, 0f),
                        Rotation = new Vec3f(0f, 0f, 0f),
                        Origin = new Vec3f(0.5f, 0f, 0.5f),
                        ScaleXYZ = new Vec3f(1f, 1f, 1f)
                    },

                    // A pot already has its own jitter. Letting the plant turn independently
                    // makes the two visibly disagree.
                    RandYRotAngle = 0
                };
            }

            return null;
        }

        /// <summary>
        /// Vanilla's own "this plant goes in a flowerpot" description, or null.
        ///
        /// The key is <c>plantContainable.smallContainer</c> — small being the flowerpot,
        /// large being the planter. Read through <c>AsObject</c> with the collectible's own
        /// domain so a modded plant resolves its shape and textures against its own assets
        /// rather than ours, which is what vanilla's BlockEntityPlantContainer does.
        /// </summary>
        public static PlantContainerProps PottedProps(CollectibleObject collectible)
        {
            if (collectible == null) return null;
            // read once per item and attributes object (ForgetDisplayData) - asked on every resolve of a plant with no size
            // or shape of its own
            JsonObject from = collectible.Attributes;
            if (pottedProps.TryGetValue(collectible, out var known) && ReferenceEquals(known.From, from)) return known.Props;
            JsonObject node = from?["plantContainable"]["smallContainer"];
            PlantContainerProps props = node?.Exists == true ? node.AsObject<PlantContainerProps>(null, collectible.Code.Domain) : null;
            pottedProps[collectible] = (from, props);
            return props;
        }

        /// <summary>
        /// The flat attribute name the in-game transform editor edits under. Vanilla's
        /// equivalent is "onshelfTransform".
        /// </summary>
        public const string TransformTarget = "onUniversalDisplayTransform";

        /// <summary>
        /// The pre-rename attribute name, still READ so that patches written against it keep
        /// working. It is published API — anything anywhere may already set it — and a
        /// transform that silently stops being applied is the worst kind of break, because the
        /// item still places and merely looks wrong.
        ///
        /// Only read, never written: the editor writes the current name.
        /// </summary>
        public const string LegacyTransformTarget = "onWindowDisplayTransform";

        /// <summary>
        /// Feeds the transform editor the currently resolved transform.
        ///
        /// The editor works in terms of a flat attribute name, but the real value lives
        /// at displayable.&lt;category&gt;.transform. This pair of listeners bridges the two,
        /// the same way vanilla bridges "onshelfTransform" to displayable.shelf.transform
        /// — which is why cabinets have no editor entry of their own to copy.
        /// </summary>
        /// <summary>
        /// Static so the mod system can register ONE listener rather than one per block
        /// type. It needs no particular block: the value comes from the HELD item, and the
        /// category is the same across every surface this mod ships.
        /// </summary>
        /// <summary>
        /// Which <c>displayable.&lt;category&gt;</c> entry the editor is working on: the one
        /// belonging to the window being aimed at, or the default when aiming at nothing.
        ///
        /// **Both editor handlers MUST use this.** They used to resolve it separately — the
        /// get side from the aimed block, the set side hardcoded to
        /// <see cref="DefaultDisplayCategory"/> — so the editor READ from
        /// `displayable.windowdisplay` (what every shipped marker declares) and WROTE to
        /// `displayable.shelf`. Editing an item therefore did nothing at all, or, on an item
        /// that happens to have a vanilla shelf entry, quietly edited THAT instead.
        ///
        /// One derivation, one place. This codebase's recurring bug is a value computed in
        /// more than one spot, and this is the fourth time it has been paid for.
        /// </summary>
        public static string ResolveEditorCategory(ICoreClientAPI capi)
        {
            BlockSelection sel = capi == null ? null : BEUniversalDisplay.WindowSelection(capi.World.BlockAccessor, capi.World.Player?.CurrentBlockSelection);
            if (sel == null) return DefaultDisplayCategory;

            if (capi.World.BlockAccessor.GetBlock(sel.Position)?.GetBehavior<BlockBehaviorDisplaySurfaces>()
                is not BlockBehaviorDisplaySurfaces bh)
            {
                return DefaultDisplayCategory;
            }

            // FROM THE SURFACE BEING AIMED AT, not from Surfaces[0].
            //
            // The category is what separates one mod's display data from another's — a block
            // declaring `psurface0-w16-h4-d16-box` reads `displayable.box`, and that is the
            // whole mechanism by which someone else's mod keeps its transforms clear of this
            // one's. Taking it from the first surface silently worked only while every surface
            // on a block shared a category, which is true of every window here and need not be
            // true of anyone else's block.
            //
            // The failure it caused was the bad kind: the editor READ and WROTE the same wrong
            // key, so it looked consistent and self-confirming while editing an entry the
            // surface never consults.
            DisplaySlotId loc = DisplaySlotId.Decode(sel.SelectionBoxId);
            return bh.GetDisplayCategory(loc?.SurfaceIndex ?? 0);
        }

        /// <summary>
        /// Writes the edited transform back to <c>displayable.&lt;category&gt;.transform</c>
        /// on the held item, creating whatever part of that structure does not exist yet.
        ///
        /// **Creating it matters — that is the authoring case.** The old code only wrote when
        /// the entry was already there, so tuning an item that has no patch yet, which is
        /// exactly what the editor is for, silently did nothing.
        ///
        /// Every level is created explicitly rather than assumed. Vanilla's own firepit
        /// handler is the cautionary tale here: it creates the `attributes` object and then
        /// does `Token["inFirePitProps"]["transform"] = ...` on a key that does not exist,
        /// which is a NullReferenceException out of the GUI thread for any item that is not
        /// already firepit-renderable.
        /// </summary>
        /// <summary>
        /// What the transform editor should be editing: **the stored item under the
        /// crosshair**, falling back to the held one.
        ///
        /// **VANILLA'S DIALOG ONLY KNOWS ABOUT THE HELD ITEM, AND THAT IS THE COMPLAINT** —
        /// *"you have to have a copy of the item in your hand which is a pain"*. It is also
        /// not a real constraint: the dialog reaches the value through OUR two event-bus
        /// handlers, so what it reads and writes is entirely our choice. Pointing at the item
        /// on the sill is both fewer steps and less error-prone than finding a spare copy.
        ///
        /// Opening the dialog RELEASES THE MOUSE, which freezes `CurrentBlockSelection` on
        /// whatever was last aimed at — so this locks onto the item you were pointing at when
        /// you opened it and holds there while you drag sliders. That is the behaviour wanted,
        /// arrived at for free.
        ///
        /// **The trade-off, stated plainly:** the dialog's own preview still renders the item
        /// in your hand, so if those differ the preview shows the wrong model. The windows in
        /// the world show the truth and update live, which is what is actually being judged.
        ///
        /// Same rule as the size editor, deliberately — one mental model: the tool works on
        /// what you point at, and on what you hold only when you point at nothing.
        /// </summary>
        public static ItemSlot ResolveEditorTarget(ICoreClientAPI capi)
        {
            BlockSelection sel = capi == null ? null : BEUniversalDisplay.WindowSelection(capi.World.BlockAccessor, capi.World.Player?.CurrentBlockSelection);
            if (sel != null
                && BEUniversalDisplay.At(capi.World.BlockAccessor, sel.Position) is { } window
                && DisplaySlotId.Decode(sel.SelectionBoxId) is { IsPlacedItem: true } loc
                && window.SlotById(loc.Encoded) is { } placed)
            {
                return placed;
            }

            return capi?.World.Player?.InventoryManager?.ActiveHotbarSlot;
        }

        // VANILLA DISPLAY BLOCKS ARE DELIBERATELY NOT TARGETED. Removed 2026-08-02 after a run.
        //
        // Targeting a cabinet worked — `BEBehaviorDisplay.InventoryInv` is public and our slot
        // ids decode with `BlockBehaviorDisplay.decodeSlotid`, no translation needed. What does
        // not work is SHOWING the result: vanilla regenerates its item meshes only when its own
        // `meshesGenerated` flag is cleared, and the method that does it is `protected`. The
        // one reachable lever, clearing the shared `ObjectCacheUtil` mesh cache, empties meshes
        // for every display block of that class and refills none of them — the item VANISHES.
        //
        // So the honest state was: edits written correctly, nothing visibly moving until the
        // item was taken and replaced. A tool that silently does the right thing while looking
        // broken is worse than one that does not offer the option, so it does not.
        //
        // If this is ever wanted, the way in is to SUBCLASS `BEBehaviorDisplay` and expose
        // `MarkMeshesDirty` publicly on your own block — that leaves vanilla's blocks alone.

        // WHAT NOT TO DO HERE, measured 2026-08-02: DO NOT CLEAR VANILLA'S MESH CACHE.
        //
        // The obvious-looking invalidation is
        //
        //     ObjectCacheUtil.GetOrCreate(capi, "meshesDisplay-" + beh.ClassCode, ...).Clear();
        //
        // and it makes the displayed item VANISH. Two reasons, both true of vanilla's
        // `BEBehaviorDisplay` (public source, vssurvivalmod):
        //
        //   * that dictionary is SHARED by every display block of that class in the world, so
        //     one edit throws away meshes belonging to every other cabinet too, and
        //   * emptying it does not make anything rebuild. Vanilla regenerates only when its own
        //     `meshesGenerated` flag is cleared, and the method that does it —
        //     `MarkMeshesDirty` — is `protected`. So the cache empties, nothing refills it, and
        //     an empty mesh is non-null and wins every fallback after it. That failure mode is
        //     already in this project's notes from the mushrooms.
        //
        // The consequence, stated plainly: on a VANILLA display block a transform edit is
        // written correctly but does not show until the item is taken and replaced, or the
        // chunk reloads. Ours refresh live because `ApplyTransformEdit` can reach our own
        // invalidation.
        //
        // The fix for a mod that wants live preview on its own board is to SUBCLASS
        // `BEBehaviorDisplay` and expose the protected `MarkMeshesDirty` as a public method —
        // about fifteen lines, no dependency on this lib, and it leaves vanilla's own blocks
        // untouched.

        public static void HandleSetTransform(ICoreClientAPI capi, TreeAttribute tree)
        {
            CollectibleObject collectible = ResolveEditorTarget(capi)?.Itemstack?.Collectible;
            if (collectible == null) return;

            WriteTransform(capi, collectible, ModelTransform.CreateFromTreeAttribute(tree));
        }

        /// <summary>
        /// Applies a live transform to a TYPE-KEYED item — clutter and anything else using
        /// <c>TypedDisplayableProps</c> — by writing into that behaviour's own map rather than
        /// into <c>collectible.Attributes.displayable</c>.
        ///
        /// **Both halves of this matter, and the ordinary path gets both wrong for clutter.**
        ///
        ///  * WRONG SCOPE. `collectible.Attributes` is shared by every piece of clutter in the
        ///    game, so editing one pot moved all ~100 objects.
        ///  * WRONG SIZE, and this is the one that shows. Writing to `displayable.<category>`
        ///    creates an entry carrying ONLY a transform, and resolution is wholesale — so the
        ///    size stopped coming from the behaviour, found nothing in the new entry, and fell
        ///    through to <see cref="DefaultItemSize"/>. Reported as the item "just getting
        ///    smaller instead of adjusting properly", which is precisely 6x4x6 arriving.
        ///
        /// Writing into `PropsByCode[type][category]` avoids both: it is the same structure
        /// vanilla reads, scoped to the one type, and the existing size is carried across so
        /// nothing is lost by tuning a transform.
        ///
        /// The map is runtime state on a collectible, so it lasts the session and is rebuilt on
        /// the next world load — the same lifetime the size overrides have, and the same reason
        /// neither is a substitute for writing the patch out.
        /// </summary>
        public static bool WriteTypedTransform(CollectibleObject collectible, string type,
                                               string category, ModelTransform tf)
        {
            CollectibleBehaviorTypedDisplayableProps bh = TypedPropsBehaviour(collectible);
            if (bh?.PropsByCode == null || string.IsNullOrEmpty(type) || tf == null) return false;

            // The key may be a WILDCARD - "pottery/*" legitimately covers the aimed type - so
            // the entry to edit is the one that MATCHES, not one named after the stack. Editing
            // a copy filed under the literal type would shadow the author's own wildcard and
            // leave the file describing something the game never read.
            string key = null;
            foreach (var kv in bh.PropsByCode)
            {
                if (WildcardUtil.Match(kv.Key, type)) { key = kv.Key; break; }
            }
            if (key == null)
            {
                key = type;
                bh.PropsByCode[key] = new Dictionary<string, DisplayableAttributes>();
            }

            Dictionary<string, DisplayableAttributes> byCategory = bh.PropsByCode[key];
            if (!byCategory.TryGetValue(category, out DisplayableAttributes entry) || entry == null)
            {
                // Seed from whatever the item currently resolves to, so a transform edit does
                // not silently discard the size, behaviour and category it already had.
                byCategory.TryGetValue(DefaultDisplayCategory, out DisplayableAttributes fallback);
                entry = new DisplayableAttributes
                {
                    Size = fallback?.Size,
                    Behavior = fallback?.Behavior ?? EnumDisplayableBehavior.Default,
                    RandYRotAngle = fallback?.RandYRotAngle ?? 0,
                    Shape = fallback?.Shape,
                };
                byCategory[category] = entry;
            }

            entry.Transform = tf;
            return true;
        }

        /// <summary>
        /// Writes a transform onto a collectible's <c>displayable.&lt;category&gt;</c> entry,
        /// creating whatever part of that structure is missing.
        ///
        /// Split out of <see cref="HandleSetTransform"/> so the tuner dialog writes by exactly
        /// the same route as vanilla's editor. Two ways of storing the same value would be the
        /// "derived in more than one place" bug again, and this one would show up as an item
        /// that looks right until something else touches it.
        /// </summary>
        /// <param name="category">The display category to write under; null = the one under the crosshair. The tuner
        /// passes the category of the item it is pinned to, which the crosshair may have left (2026-10-22).</param>
        public static void WriteTransform(ICoreClientAPI capi, CollectibleObject collectible, ModelTransform tf, string category = null)
        {
            if (collectible == null || tf == null) return;

            if (collectible.Attributes?.Token is not JObject root)
            {
                root = new JObject();
                collectible.Attributes = new JsonObject(root);
            }

            if (root["displayable"] is not JObject displayable)
            {
                displayable = new JObject();
                root["displayable"] = displayable;
            }

            category ??= ResolveEditorCategory(capi);
            if (displayable[category] is not JObject entry)
            {
                entry = new JObject();
                displayable[category] = entry;
            }

            entry["transform"] = JToken.FromObject(tf);
            ForgetDisplayData();   // the item's attributes just changed under the cache
        }

        public static void HandleGetTransform(ICoreClientAPI capi, TreeAttribute tree, ref EnumHandling handling)
        {
            // Reads from the SAME target the set side writes to — the aimed item first. The
            // two drifting apart is the exact bug that shipped in 1.0.2: the get side resolved
            // the category from the aimed window while the set side hardcoded the default.
            ItemSlot target = ResolveEditorTarget(capi);
            if (target == null || target.Empty) return;

            DisplayableAttributes dattr = GetDisplayableAttributes(target, ResolveEditorCategory(capi));
            if (dattr?.Transform == null) return;

            handling = EnumHandling.PreventDefault;
            tree.SetBool("preventDefault", true);
            dattr.Transform.ToTreeAttribute(tree);
        }

        // EditorCategory (first surface wins) REMOVED 2026-08-02 — it was the bug above.
        // Use GetDisplayCategory(surfaceIndex) with the index decoded from the aimed slot id.

        public static Size3f GetItemSize(ItemSlot slot, string displayType)
            => GetDisplayableAttributes(slot, displayType)?.Size;

        /// <summary>
        /// Items are positioned by their centre, so the mesh origin sits half a
        /// footprint back from the clicked pad.
        ///
        /// </summary>
        public static Vec3f GetCentreOffset(Size3f size) => new Vec3f(-size.Width / 2f, 0f, -size.Length / 2f);

        /// <summary>

        /// <summary>

        /// <summary>
        /// An item's width and length as they actually sit once its own rotation is applied.
        ///
        /// The declared <c>Size</c> describes the item unrotated. Everything reasoning about
        /// the space an item occupies — its selection box, what it collides with, whether it
        /// fits — has to use the turned footprint, or a rotated item looks one way and
        /// behaves another. That was the case for a long time: rotation was applied in
        /// <c>BuildSlotMatrix</c> to the MESH only, so wrenching a letter or a hammer turned
        /// the model while its box stayed in the original orientation. Invisible on a square
        /// item, plainly wrong on a long one.
        ///
        /// Snapped to the nearest quarter turn deliberately. A footprint is an axis-aligned
        /// box, so an item at 45° would need the bounding box of a rotated rectangle — which
        /// is LARGER than either orientation, meaning things would fit worse the more you
        /// turned them. That is the opposite of what rotating is for. Under 45° of turn keeps
        /// the original footprint; past it, width and length swap.
        /// </summary>
        public static Size3f RotatedFootprint(Size3f size, float rotDeg)
        {
            if (size == null) return null;

            float a = ((rotDeg % 180f) + 180f) % 180f;      // 0..180; a half turn is symmetric
            return a > 45f && a < 135f
                ? new Size3f(size.Length, size.Height, size.Width)
                : size;
        }

        // ── Placement preview ───────────────────────────────────────────────

        public void RenderSelectionBoxes(BlockSelection blockSel, RenderBoxDelegate renderBoxHandler)
            => RenderSelectionBoxesOn(null, blockSel, renderBoxHandler);

        public void RenderSelectionBoxesOn(BEUniversalDisplay display, BlockSelection blockSel, RenderBoxDelegate renderBoxHandler)
        {
            RenderBoxDelegate drawAtHost = renderBoxHandler;
            // VS SIDING (2026-10-17): a block in a siding wall's space is moved off the panel - the game's boxes come
            // back moved, our own placing outline is built where it would be without siding. So everything below works
            // unmoved (the aimed box taken back) and every box drawn is moved on the way out (SidingShift).
            Vec3f siding = blockSel?.Position == null ? null : SidingShift.At(blockSel.Position, block);
            if (siding != null)
            {
                RenderBoxDelegate drawUnmoved = renderBoxHandler;
                renderBoxHandler = (box, width, colour) => drawUnmoved(box.OffsetCopy(siding.X, 0f, siding.Z), width, colour);
            }
            float lineWidth = 1.6f * capi.Settings.Float["wireframethickness"];

            if ((display ?? BEUniversalDisplay.At(capi.World.BlockAccessor, blockSel.Position)) is not { } be)
            {
                renderBoxHandler(Cuboidf.Default(), lineWidth, block.GetSelectionColor(capi, blockSel.Position));
                return;
            }

            if (be.SuppressPlacement(capi.World.Player?.InventoryManager?.ActiveHotbarSlot)) return;
            Cuboidf hovered;
            string boxId = blockSel.SelectionBoxId;
            var explicitTarget = DisplaySlotId.Decode(boxId);
            if (be.IsEmbedded && explicitTarget is { IsPlacedItem: false })
            {
                // Embedded displays expose only item boxes to the host. The real
                // voxel floor is aimed at; its preview pad is resolved directly.
                var padSurface = GetSurface(explicitTarget.SurfaceIndex);
                if (padSurface == null) return;
                var origin = StackPreviewOrigin(padSurface, explicitTarget);
                hovered = new Cuboidf(origin.X, origin.Y, origin.Z, origin.X, origin.Y, origin.Z)
                    .RotatedCopyRad(0f, be.MeshAngleRad, 0f, new Vec3d(.5, 0, .5));
            }
            else
            {
                Cuboidf[] selectionBoxes = be.IsEmbedded ? be.GetSelectionBoxes()
                    : block.GetSelectionBoxes(capi.World.BlockAccessor, blockSel.Position);
                // Forwarded selections carry the owner ID, not an owner-local index.
                int hoveredIndex = blockSel.SelectionBoxIndex;
                if (boxId != null)
                    hoveredIndex = Array.FindIndex(selectionBoxes, b => (b as CuboidfWithId)?.Id == boxId);
                if (hoveredIndex < 0 || hoveredIndex >= selectionBoxes.Length) return;
                hovered = selectionBoxes[hoveredIndex];
                boxId ??= (hovered as CuboidfWithId)?.Id;
            }
            if (BEUniversalDisplay.GuestSelection(blockSel, boxId, out var ownerSelection)
                && RenderOwnerSelection(capi.World, blockSel, ownerSelection, drawAtHost)) return;
            if (siding != null) hovered = hovered.OffsetCopy(-siding.X, 0f, -siding.Z);
            if (BEUniversalDisplay.IsHiddenPartBox((hovered as CuboidfWithId)?.Id ?? boxId)) return;
            DisplaySlotId loc = DisplaySlotId.Decode(boxId);

            ItemSlot heldSlot = capi.World.Player.Entity.RightHandItemSlot;

            // Not aiming at a placement pad, or nothing placeable in hand — plain outline
            if (loc == null || loc.IsPlacedItem || heldSlot == null || heldSlot.Empty)
            {
                // FRAME AND PANE BOXES ALWAYS DRAW, and this line was MISSING until
                // 2026-08-08. `loc == null` is a frame or pane box, and the two tests below
                // both require IsPlacedItem — so aiming at a frame fell through the whole
                // branch to the `return` without ever calling renderBoxHandler, and the box
                // that tells you where to click to open a window was simply never outlined.
                //
                // The comment here already said these must never be hidden; only the code
                // disagreed. Present in the 2026-08-06 backup too, so it shipped in 1.1.0.
                // Noticed on the corner window, whose frame is the first one you aim at with
                // nothing else nearby to hit instead.
                //
                // Implementing ICustomSelectionBoxRender REPLACES the default rendering, so
                // drawing nothing here really does mean nothing on screen.
                if (loc == null)
                {
                    // ...except an item's box from a window below, carried here as a guest (a stack reaching up into this
                    // block, 2026-10-10): that follows "Show Placed Item Box" like the window's own items
                    bool guestItem = boxId != null && boxId.StartsWith(BEUniversalDisplay.GuestBoxPrefix, StringComparison.Ordinal)
                        && DisplaySlotId.Decode(boxId.Substring(boxId.IndexOf(':', BEUniversalDisplay.GuestBoxPrefix.Length) + 1)) is { IsPlacedItem: true };
                    if (!guestItem || ShouldDrawPlacedItemBox())
                        renderBoxHandler(hovered, lineWidth, block.GetSelectionColor(capi, blockSel.Position));
                }

                // The outline around an item already on the sill can be switched off.
                if (loc is { IsPlacedItem: true } && ShouldDrawPlacedItemBox())
                {
                    renderBoxHandler(hovered, lineWidth, block.GetSelectionColor(capi, blockSel.Position));
                }

                // AIMING AT AN ITEM WITH SOMETHING IN HAND MEANS STACKING, so outline where
                // the copy would land as well. A pad placement gets a footprint box and a
                // ghost; without this a stacked one got a ghost floating with nothing under
                // it, and the box is the half that answers "will the next one fit".
                if (loc is { IsPlacedItem: true } && heldSlot is { Empty: false })
                {
                    DrawStackFootprint(be, loc, heldSlot, lineWidth, renderBoxHandler, blockSel);
                }

                return;
            }

            DisplaySurface surface = GetSurface(loc.SurfaceIndex);
            if (PlacementGhostRenderer.AwaitingPlacement(be.Pos, loc.Encoded, Environment.TickCount64)) return;
            if (surface == null)
            {
                renderBoxHandler(hovered, lineWidth, block.GetSelectionColor(capi, blockSel.Position));
                return;
            }

            Size3f itemSize = GetItemSize(heldSlot, surface.DisplayCategory ?? "shelf");
            if (itemSize == null)
            {
                renderBoxHandler(hovered, lineWidth, block.GetSelectionColor(capi, blockSel.Position));
                return;
            }

            // Preview the TURNED footprint. This is the whole value of rotating before
            // placing: you can see a long item swing across the sill and go from red to
            // black before you commit, rather than placing and discovering it did not fit.
            itemSize = RotatedFootprint(itemSize, PreviewRotationDeg(be, surface));

            Vec3f offset = GetCentreOffset(itemSize);
            bool fits = FitsAt(be, surface, loc, itemSize);
            DisplayableAttributes ghostAttr = GetDisplayableAttributes(heldSlot, surface.DisplayCategory ?? "shelf");
            fits = fits && ghostAttr != null && be.EmbeddedPlacementFits(loc, ghostAttr, PreviewRotationDeg(be, surface));

            // Undo the block rotation, build the preview in local space, rotate back
            Cuboidf local = hovered.RotatedCopyRad(0f, -be.MeshAngleRad, 0f, new Vec3d(0.5, 0.0, 0.5));

            Cuboidf preview = new Cuboidf(0f, 0.0625f, 0f,
                                          itemSize.Width / 16f,
                                          itemSize.Height / 16f + 0.0625f,
                                          itemSize.Length / 16f)
                .Translate(local.Start)
                // The pending height nudge, so the box on an empty pad moves with the ghost.
                // A level-0 placement has no pile under it, so this is the whole offset.
                .Translate(offset.X / 16f,
                           be.SlotRestY(loc, surface, ghostAttr, null, PlacementHeight.For(capi, capi.World.Player)) / 16f,
                           offset.Z / 16f)
                .RotatedCopyRad(0f, be.MeshAngleRad, 0f, new Vec3d(0.5, 0.0, 0.5));

            if (!ShouldDrawFootprintBox()) return;

            // The outline is the GHOST's - measured from the mesh it draws (2026-10-06); whether it fits (the colour)
            // still comes from the written footprint, as the placement does.
            if (!OutlineFromSize(heldSlot, surface.DisplayCategory))
                preview = (ghostAttr == null ? null : be.GhostBox(loc.Encoded, heldSlot, ghostAttr, PreviewRotationDeg(be, surface))) ?? preview;

            renderBoxHandler(preview, lineWidth,
                fits ? new Vec4f(0f, 0f, 0f, 0.5f) : new Vec4f(0.5f, 0f, 0f, 0.5f));
        }

        /// <summary>
        /// The footprint outline for an item about to be STACKED, drawn at the level it would
        /// land on rather than at the item being aimed at.
        ///
        /// Built from the level-0 pad of the same column and lifted by
        /// <c>BEUniversalDisplay.StackBaseY</c>, which is the same call the mesh matrix uses — the
        /// box and the ghost have to agree, and deriving both from the pad is what keeps them
        /// agreeing when an item's declared height is not its visual height.
        ///
        /// The angle is the one the placement will inherit, not the player's pending one, for
        /// the reason spelled out in TryResolveStackTarget.
        /// </summary>
        private void DrawStackFootprint(BEUniversalDisplay be, DisplaySlotId aimed, ItemSlot heldSlot,
                                        float lineWidth, RenderBoxDelegate renderBoxHandler, BlockSelection selection)
        {
            if (!ShouldDrawFootprintBox()) return;

            DisplaySurface surface = GetSurface(aimed.SurfaceIndex);
            if (surface == null) return;

            string category = surface.DisplayCategory ?? DefaultDisplayCategory;
            DisplayableAttributes dattr = GetDisplayableAttributes(heldSlot, category);
            if (dattr?.Size == null) return;

            if (!be.TryResolveStackTargetOn(aimed, dattr, out DisplaySlotId target, out float rotDeg, out bool allowed, selection))
            {
                return;
            }

            // + SurfaceFacingDeg, exactly as the pad path's PreviewRotationDeg and TryPut
            if (PlacementGhostRenderer.AwaitingPlacement(be.Pos, target.Encoded, Environment.TickCount64)) return;
            // both do. Leaving it off was the bug that put the box out of line with the item:
            // the ghost looked right because BuildSlotMatrix adds the facing itself, so the
            // outline was the only thing measuring an unturned footprint.
            Size3f itemSize = RotatedFootprint(dattr.Size, rotDeg + be.SurfaceFacingDeg(surface));
            Vec3f offset = GetCentreOffset(itemSize);

            // Stored columns survive grid-resolution changes. Build directly from the
            // slot coordinates, as the ghost does; a matching current pad is unnecessary.
            Vec3f local = StackPreviewOrigin(surface, target);

            // NO 1-voxel lift here, unlike the pad path. That path starts the box at 0.0625 so
            // it clears the surface it is drawn on, which is invisible on an empty sill — but
            // a stacked box has the item BELOW it to be judged against, and the same voxel
            // read as the outline floating above where the item lands.
            //
            // The item's own base is (surfacePos.Y + StackBaseY), and `local.Start` is the
            // surface, so starting at 0 and lifting by StackBaseY puts the box exactly where
            // the mesh goes. The SAME call, not a second copy of the sum — this line used to
            // read `target.Y * itemSize.Height`, which measured the held item against itself
            // and so drew the box where the pile below did not actually end.
            Cuboidf preview = new Cuboidf(0f, 0f, 0f,
                                          itemSize.Width / 16f,
                                          itemSize.Height / 16f,
                                          itemSize.Length / 16f)
                .Translate(local)
                // SlotRestY with the PENDING nudge, matching TryBuildGhost exactly — the
                // target slot is empty, so there is nothing stored to read.
                .Translate(offset.X / 16f,
                           be.SlotRestY(target, surface, dattr, null,
                                        PlacementHeight.For(capi, capi.World.Player)) / 16f,
                           offset.Z / 16f)
                .RotatedCopyRad(0f, be.MeshAngleRad, 0f, new Vec3d(0.5, 0.0, 0.5));

            // the ghost's own box at the level it would land on (same target and angle the ghost uses)
            if (!OutlineFromSize(heldSlot, category))
                preview = be.GhostBox(target.Encoded, heldSlot, dattr, rotDeg) ?? preview;

            renderBoxHandler(preview, lineWidth,
                allowed ? new Vec4f(0f, 0f, 0f, 0.5f) : new Vec4f(0.5f, 0f, 0f, 0.5f));
        }

        /// <summary>
        /// Whether to draw the footprint outline, per the `PlacementBox` setting:
        /// 0 never, 1 only when the item will not fit, 2 always.
        ///
        /// The ghost made the box arguably redundant — it shows the item where it will land
        /// and reddens the same way — but the two do not say quite the same thing. The ghost
        /// shows the ITEM; the box shows the FOOTPRINT, and those genuinely differ: seashells
        /// declare a deliberately tight box so more fit along a sill, and the ruler is 12.8
        /// wide but paper thin. Judging where the next item can go is the box's job.
        ///
        /// Hence a setting rather than a decision made for everyone, and hence mode 1 —
        /// which shows the box exactly when it is telling you something, and gets out of the
        /// way otherwise.
        ///
        /// **Never hidden while the ghost is off.** Turning both off would leave a bare pad
        /// grid with no indication of what is about to happen anywhere, which is not a state
        /// worth letting anyone configure themselves into by accident.
        /// </summary>
        /// <summary>
        /// Live override from the toggle key, for lining something up precisely without
        /// going to the config and back. Client-side and deliberately not persisted: it is
        /// a "show me while I do this" switch, not a preference — the preference is
        /// `PlacementBox`, and this returns to it next session.
        /// </summary>
        public static bool ForceFootprintBox;

        internal static Vec3f StackPreviewOrigin(DisplaySurface surface, DisplaySlotId target)
            => new Vec3f((surface.VoxelPosition.X + target.X) / 16f,
                         surface.VoxelPosition.Y / 16f,
                         (surface.VoxelPosition.Z + target.Z) / 16f);

        // A guest item still belongs to the lower shelf. Delegate the whole rendering
        // decision (including its pending flower/stack preview), not just its own box.
        internal static bool RenderOwnerSelection(IWorldAccessor world, BlockSelection host,
                                                  BlockSelection owner, RenderBoxDelegate draw)
        {
            if (owner?.Position == null || host?.Position == null || owner.Position.Equals(host.Position)) return false;
            var renderer = world.BlockAccessor.GetBlock(owner.Position)
                .GetInterface<ICustomSelectionBoxRender>(world, owner.Position);
            if (renderer == null) return false;
            int dx = owner.Position.X - host.Position.X;
            int dy = owner.Position.Y - host.Position.Y;
            int dz = owner.Position.Z - host.Position.Z;
            renderer.RenderSelectionBoxes(owner, (box, width, colour) => draw(box.OffsetCopy(dx, dy, dz), width, colour));
            return true;
        }

        /// <summary>
        /// Whether to outline an item already on the sill. Same toggle key overrides it, and
        /// the same safety rule applies: never hidden while the ghost is off, since then
        /// nothing at all would indicate what you are pointing at.
        /// </summary>
        internal static bool ShouldDrawPlacedItemBox()
        {
            if (ForceFootprintBox) return true;

            UniversalDisplayLibConfig cfg = UniversalDisplayLibConfig.Current;
            if (cfg?.PlacementGhost != true) return true;

            return cfg.ShowPlacedItemBox;
        }

        private static bool ShouldDrawFootprintBox()
        {
            if (ForceFootprintBox) return true;

            UniversalDisplayLibConfig cfg = UniversalDisplayLibConfig.Current;
            if (cfg?.PlacementGhost != true) return true;

            return cfg.ShowPlacementBox;
        }

        /// <summary>
        /// The angle the item in hand would be placed at on this surface: the player's
        /// pending quarter turn plus the surface's own facing.
        ///
        /// Shared by the outline box and the ghost mesh deliberately. A value worked out
        /// in two places is this codebase's recurring bug — the mesh, the footprint and
        /// the particles each derived an item's angle separately and produced three
        /// distinct faults before they were unified on ItemRotationDeg.
        /// </summary>
        /// <summary>
        /// <c>"outline": "size"</c> in an item's displayable entry (any category, or the shelf one it inherits; per variant
        /// with <c>outlineByType</c>): outline it by its written size instead of measuring its drawn mesh. For crossed-plane
        /// models - flowers, grass, ferns - whose mesh is a whole block of mostly see-through sheet (Calm, 2026-10-06).
        /// </summary>
        public static bool OutlineFromSize(ItemSlot slot, string category)
        {
            CollectibleObject collectible = slot?.Itemstack?.Collectible;
            if (collectible == null) return false;
            // read once per item, category and attributes object (outlineRules) - asked on every settings lookup
            category ??= DefaultDisplayCategory;
            JsonObject from = collectible.Attributes;
            if (outlineRules.TryGetValue((collectible, category), out var known) && ReferenceEquals(known.From, from)) return known.BySize;
            JsonObject displayable = from?["displayable"];
            bool bySize = false;
            if (displayable != null && displayable.Exists)
            {
                string value = displayable[category]["outline"].AsString(null)
                    ?? displayable[DefaultDisplayCategory]["outline"].AsString(null);
                bySize = string.Equals(value, "size", StringComparison.OrdinalIgnoreCase);
            }
            outlineRules[(collectible, category)] = (from, bySize);
            return bySize;
        }

        public float PreviewRotationDeg(BEUniversalDisplay be, DisplaySurface surface)
            => PlacementRotation.ForSurface(capi, capi.World.Player, be, surface) + be.SurfaceFacingDeg(surface);

        /// <summary>
        /// Whether an item of this ALREADY-ROTATED footprint can be placed centred on
        /// <paramref name="loc"/>. Three independent gates, in the order they bite:
        /// the item must fit wholly inside the surface from that centre, it must not
        /// exceed the surface in any dimension at all, and it must not overlap something
        /// already there.
        ///
        /// Extracted so the outline box and the ghost mesh cannot drift apart about what
        /// "fits" means — they colour themselves from the same answer.
        /// </summary>
        public static bool FitsAt(BEUniversalDisplay be, DisplaySurface surface,
                                  DisplaySlotId loc, Size3f rotatedSize)
        {
            if (be == null || surface == null || loc == null || rotatedSize == null) return false;
            rotatedSize = BEUniversalDisplay.FitSize(rotatedSize); // half a voxel of give on every side (lib 1.3.1)

            Vec3f offset = GetCentreOffset(rotatedSize);

            if (loc.X + offset.X < 0f || loc.Z + offset.Z < 0f
                || loc.X + offset.X > surface.Size.Width - rotatedSize.Width
                || loc.Z + offset.Z > surface.Size.Length - rotatedSize.Length) return false;

            if (surface.Size.Width < rotatedSize.Width
                || (!be.UnboundedStacking && surface.Size.Height < rotatedSize.Height)
                || surface.Size.Length < rotatedSize.Length) return false;

            string hit = be.GetCollidingSlotId(loc, new Cuboidf(rotatedSize));
            if (hit == null) return true;

            // With overlap allowed the ghost must stop reddening for a placement that is now
            // going to succeed — a preview that lies is worse than no preview, and this and
            // TryPut have to agree about the SAME two conditions. Its own slot still counts as
            // occupied, for the reason spelled out there.
            return ServerRules.ItemOverlapAllowed
                   && hit != loc.Encoded;
        }

        /// <summary>
        /// Everything the placement ghost needs, resolved from what the player is aiming
        /// at — or false when there is nothing to preview.
        ///
        /// Deliberately mirrors the early-outs of <see cref="RenderSelectionBoxes"/> so the
        /// ghost appears exactly when the footprint box does and never on its own.
        /// </summary>
        public bool TryGetGhost(BlockSelection blockSel, ItemSlot heldSlot,
                                out BEUniversalDisplay be, out DisplaySlotId loc,
                                out DisplayableAttributes dattr, out float rotDeg, out bool fits)
        {
            be = null; loc = null; dattr = null; rotDeg = 0f; fits = false;

            if (blockSel == null || heldSlot == null || heldSlot.Empty) return false;

            be = BEUniversalDisplay.At(capi.World.BlockAccessor, blockSel.Position);
            if (be == null) return false;
            // Native voxel indices are unrelated to the embedded display's pad list.
            // Bare carved floors go through the ground source with an explicit target.
            if (be.IsEmbedded && DisplaySlotId.Decode(blockSel.SelectionBoxId) == null) return false;
            return TryGetGhostOn(be, blockSel, heldSlot, out loc, out dattr, out rotDeg, out fits);
        }

        /// <summary>
        /// The ghost against a given block entity - the one in the world, or a phantom (BEUniversalDisplay.Phantom) for
        /// a block that does not exist yet. <paramref name="blockSel"/> names the aimed pad or item by SelectionBoxId.
        /// </summary>
        public bool TryGetGhostOn(BEUniversalDisplay be, BlockSelection blockSel, ItemSlot heldSlot,
                                  out DisplaySlotId loc, out DisplayableAttributes dattr, out float rotDeg, out bool fits)
        {
            loc = null; dattr = null; rotDeg = 0f; fits = false;
            if (be == null || blockSel == null || heldSlot == null || heldSlot.Empty) return false;
            if (be.SuppressPlacement(heldSlot)) return false;

            // Ground previews create a pad selection with an explicit ID. Real shelf
            // selections may carry only an index; resolve those through the same owner
            // lookup used for clicking so flowers get the same preview on both paths.
            loc = DisplaySlotId.Decode(be.AimedBoxId(blockSel));
            if (loc == null) return false;

            DisplaySurface surface = GetSurface(loc.SurfaceIndex);
            if (surface == null) return false;

            dattr = GetDisplayableAttributes(heldSlot, surface.DisplayCategory ?? "shelf");
            if (dattr?.Size == null) return false;

            // AIMING AT AN ITEM MEANS STACKING ONTO IT, so preview where the copy would
            // actually land rather than giving up. This used to `return false` on a placed
            // item, so the one case where a player most wants to know what they are about to
            // get — another one on top, at the pile's angle — was the one case with no ghost.
            //
            // The block entity resolves it, because the walk up the pile and the inherited
            // angle both need the inventory. `loc` becomes the TARGET slot, which is what the
            // renderer builds its matrix from.
            if (loc.IsPlacedItem)
            {
                if (!be.TryResolveStackTargetOn(loc, dattr, out DisplaySlotId target, out rotDeg, out bool allowed, blockSel))
                {
                    return false;
                }

                loc = target;
                fits = allowed && be.EmbeddedPlacementFits(target, dattr, rotDeg);
                // The ghost's angle REPLACES ItemRotationDeg in BuildSlotMatrix, and that one includes the surface's own
                // facing - as PreviewRotationDeg does on the pad path. The stack resolver gives the angle without it, so
                // on a turned surface (a corner shelf's side) the stacked ghost faced the wrong way (Calm, 2026-10-19).
                // Surfaces facing 0 (ordinary shelves, ground, carved floors) never showed it.
                rotDeg += be.SurfaceFacingDeg(surface);
                return true;
            }

            rotDeg = PreviewRotationDeg(be, surface);
            fits = FitsAt(be, surface, loc, RotatedFootprint(dattr.Size, rotDeg))
                && be.EmbeddedPlacementFits(loc, dattr, rotDeg);
            return true;
        }

        // ── Swap aim point ──────────────────────────────────────────────────

        /// <summary>
        /// Whether the box under the cursor is somewhere a ctrl+wrench block swap may be
        /// taken from.
        ///
        /// Normally that is the frame or a pane, which is what a player sees and aims at.
        /// A block may legitimately declare NO frame boxes at all, though — the custom
        /// chiselled window does, so that the only selection box in the block is the
        /// chiselled block the player put there — and then the swap had no aim point
        /// anywhere and was simply unreachable, silently.
        ///
        /// So with no frame boxes declared, any box except a placed item stands in for the
        /// frame. Grid pads do nothing on a ctrl+wrench otherwise, so nothing is taken away.
        ///
        /// The placed-item exclusion is kept, but its ORIGINAL reason is gone: it used to
        /// stop the swap colliding with ctrl+wrench reverse-rotation, which was removed on
        /// 2026-07-30 when rotation moved to the mouse wheel. It stays because swapping the
        /// whole block by clicking an item sitting on it would be a surprising thing to do,
        /// not because anything else now competes for that click.
        ///
        /// Deliberately NOT the same question as <c>onFrame</c> at the call site, which
        /// also gates pane toggling and the fall-through to item placement — widening that
        /// would make a pad click return early and stop items being placed at all.
        /// </summary>
        public static bool IsSwapAimPoint(BlockBehaviorDisplaySurfaces bh, string boxId)
        {
            if (BEUniversalDisplay.IsHiddenPartBox(boxId)) return false;
            if (boxId == null
                || boxId == BEUniversalDisplay.WindowBoxId
                || boxId.StartsWith(BEUniversalDisplay.PartBoxPrefix, StringComparison.Ordinal)
                || boxId.StartsWith("pane", StringComparison.Ordinal)
                || boxId.StartsWith("frame", StringComparison.Ordinal))
            {
                return true;
            }

            if (bh == null || bh.HasAnyFrameBox) return false;

            return !boxId.StartsWith(DisplaySlotId.PlacedPrefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// Whether any frame box is actually produced, rather than whether the attribute
        /// exists — a group declaring an empty <c>staticFrameBoxes</c> and no open/closed
        /// pane box contributes nothing to aim at, same as no group at all.
        /// </summary>
        public bool HasAnyFrameBox
        {
            get
            {
                for (int i = 0; i < FrameBoxGroups.Length; i++)
                {
                    FrameBoxGroup g = FrameBoxGroups[i];
                    if (g.ClosedFrameBox != null || g.OpenFrameBox != null) return true;
                    if (g.StaticFrameBoxes != null && g.StaticFrameBoxes.Length > 0) return true;
                }
                return false;
            }
        }

        // ── Interaction help ────────────────────────────────────────────────

        /// <summary>Whether any pane group can actually open (a box that moves or an open animation).</summary>
        private bool HasOpenablePane => FrameBoxGroups.Any(g => g.OpenFrameBox != null || g.ClosedFrameBox != null || !string.IsNullOrEmpty(g.AnimOpen));

        /// <summary>
        /// Whether clicking this box would move a pane. A fixed part kept in a group of its own with no
        /// pane ("frame1-0" - the tall sash's upper sash, the tall awning's bottom glass) opens nothing,
        /// so it gets no Open/Close prompt (2026-10-05). Unparseable ids keep the prompt, as before.
        /// </summary>
        private bool OpensSomething(string boxId)
        {
            // a fixed part's box (measured, 2026-10-06) opens nothing
            if (boxId != null && boxId.StartsWith(BEUniversalDisplay.PartBoxPrefix, StringComparison.Ordinal)) return false;
            if (boxId == null || !boxId.StartsWith("frame", StringComparison.Ordinal)) return true;
            int dash = boxId.IndexOf('-');
            string digits = dash > 5 ? boxId.Substring(5, dash - 5) : boxId.Substring(5);
            if (!int.TryParse(digits, out int group) || group < 0) return true;
            if (group >= FrameBoxGroups.Length) return false;
            FrameBoxGroup g = FrameBoxGroups[group];
            return g.OpenFrameBox != null || g.ClosedFrameBox != null || !string.IsNullOrEmpty(g.AnimOpen);
        }

        /// <summary>
        /// The help's word for a click on this frame box: the pane's own <c>openLabel</c> / <c>closeLabel</c> (a lang key or
        /// plain text - Lang.Get hands plain text back as it is) or "Open" / "Close", whichever the click will do now.
        /// The whole-window box works every pane together - it opens if any is shut.
        /// </summary>
        string ToggleLabel(IWorldAccessor world, BlockSelection selection, string boxId)
        {
            FrameBoxGroup[] groups = FrameBoxGroups ?? Array.Empty<FrameBoxGroup>();
            int pane = 0;
            if (boxId != null && boxId.StartsWith("pane", StringComparison.Ordinal)) int.TryParse(boxId.Substring(4), out pane);
            else if (boxId != null && boxId.StartsWith("frame", StringComparison.Ordinal))
            {
                int dash = boxId.IndexOf('-');
                int.TryParse(dash > 5 ? boxId.Substring(5, dash - 5) : boxId.Substring(5), out pane);
            }
            bool[] states = (world?.BlockAccessor.GetBlockEntity(selection.Position) as BEUniversalDisplay)?.paneStates ?? Array.Empty<bool>();
            bool open = boxId == BEUniversalDisplay.WindowBoxId
                ? states.Length > 0 && states.All(s => s)
                : pane >= 0 && pane < states.Length && states[pane];
            FrameBoxGroup g = pane >= 0 && pane < groups.Length ? groups[pane] : null;
            string own = open ? g?.CloseLabel : g?.OpenLabel;
            return !string.IsNullOrWhiteSpace(own) ? own : open ? "universaldisplaylib:blockhelp-close" : "universaldisplaylib:blockhelp-open";
        }

        public override WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer, ref EnumHandling handling)
        {
            if (BEUniversalDisplay.IsHiddenPartBox(selection?.SelectionBoxId))
            {
                handling = EnumHandling.PreventSubsequent;
                return Array.Empty<WorldInteraction>();
            }
            // aimed at another window's leaf open into this block: that window's help
            if (BEUniversalDisplay.GuestSelection(selection, null, out BlockSelection forwarded))
            {
                handling = EnumHandling.PreventSubsequent;
                return world.BlockAccessor.GetBlock(forwarded.Position).GetPlacedBlockInteractionHelp(world, forwarded, forPlayer);
            }

            WorldInteraction[] help = base.GetPlacedBlockInteractionHelp(world, selection, forPlayer, ref handling);

            string boxId = selection.SelectionBoxId;
            bool onFrame = boxId == null
                           || boxId == BEUniversalDisplay.WindowBoxId
                           || boxId.StartsWith(BEUniversalDisplay.PartBoxPrefix, StringComparison.Ordinal)
                           || boxId.StartsWith("pane", StringComparison.Ordinal)
                           || boxId.StartsWith("frame", StringComparison.Ordinal);
            if (!onFrame && !IsSwapAimPoint(this, boxId)) return help;

            // canOpen ABSENT counts as true, as BEUniversalDisplay.CanOpen reads it - such a block does open, so the help
            // should say so (2026-10-22) - but only when it has a pane that opens, so a plain shelf never offers "Open"
            JsonObject canOpen = block.Attributes?["canOpen"];
            bool opens = canOpen?.Exists == true ? canOpen.AsBool(false) : HasOpenablePane;
            if (onFrame && opens && OpensSomething(boxId))
            {
                help = help.Append(new WorldInteraction
                {
                    ActionLangCode = ToggleLabel(world, selection, boxId),
                    MouseButton = EnumMouseButton.Right
                });
            }

            if (BlockSwapHelper.HasSwapTargets(block))
            {
                if (BlockBehaviorWrenchOrientable.wrenchItems == null)
                {
                    BlockBehaviorWrenchOrientable.loadWrenchItems(world);
                }

                help = help.Append(new WorldInteraction
                {
                    ActionLangCode = "universaldisplaylib:blockhelp-swap",
                    // Must match the modifier BEUniversalDisplay actually reads for the swap
                    // (controls.CtrlKey). These drifted apart once already — the help said
                    // ctrl while the code took sprint-OR-ctrl, so which key worked depended
                    // on the player's bindings and looked intermittent. Change both together.
                    HotKeyCode = "ctrl",
                    MouseButton = EnumMouseButton.Right,
                    Itemstacks = BlockBehaviorWrenchOrientable.wrenchItems
                });
            }

            return help;
        }
    }
}
