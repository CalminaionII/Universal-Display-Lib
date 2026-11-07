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

/// <summary>
/// The levitating display: any block with <c>"udlLevitatingDisplay": true</c> in its attributes makes the ONE item
/// directly on top of it hover, turning and bobbing.
///
/// Stood on any display surface, the base is an ordinary displayed item and the item stacked on it floats. That stack
/// uses column stacking - same X and Z, the next level up -
///   not free stacking, so the item is always centred over the base wherever it is clicked. A carved floor inside a
///   chiselled block (Chiseled Block Display) counts as a display surface here too;
/// - placed as a block (its own tiny display, <see cref="IsFloatBlock"/>), the one item on its surface floats, centred.
///
/// Nothing stacks on a floating item, and a base only goes on the surface itself, never on top of other items. Both are
/// refused the way any stack that does not fit is, the ghost red. The hover height is part of where the item RESTS
/// (StackBaseY), so the mesh, its click box, its particles and the headroom check all follow it; the motion on top is
/// drawn by <see cref="FloatingItemRenderer"/> up close and left out further away (UpdateHovers).
/// </summary>
public partial class BEUniversalDisplay
{
    /// <summary>The block attribute that makes a block a levitating display.</summary>
    public const string LevitatingDisplayAttribute = "udlLevitatingDisplay";

    /// <summary>
    /// A floating item's pose and motion, in voxels, degrees and seconds: the base's block attributes, or what a player
    /// set in the menu (<see cref="FloatTuning"/>). TurnSeconds is signed - negative turns the other way, 0 not at all.
    /// </summary>
    internal readonly struct FloatMotion
    {
        /// <summary>TiltDeg leans the item side to side, TiltForwardDeg back and forward.</summary>
        public readonly float RiseVoxels, BobVoxels, BobSeconds, TurnSeconds, EaseSeconds, TiltDeg, RotDeg, TiltForwardDeg;
        /// <summary>
        /// Moves the item in its OWN frame, after its turn, so a model drawn off its centre can be centred over the
        /// base and stays centred whatever its rotation.
        /// </summary>
        public readonly float OffsetXVoxels, OffsetZVoxels;

        public FloatMotion(float rise, float bob, float bobSeconds, float turnSeconds, float easeSeconds, float tiltDeg, float rotDeg,
                           float tiltForwardDeg, float offsetX = 0f, float offsetZ = 0f)
        {
            RiseVoxels = rise; BobVoxels = bob; BobSeconds = bobSeconds; TurnSeconds = turnSeconds; EaseSeconds = easeSeconds;
            TiltDeg = tiltDeg; RotDeg = rotDeg; TiltForwardDeg = tiltForwardDeg; OffsetXVoxels = offsetX; OffsetZVoxels = offsetZ;
        }
    }

    /// <summary>The menu's settings by floating spot. Replaced whole, never changed in place: read on the tesselation thread.</summary>
    volatile Dictionary<string, FloatTuning> floatTunings = new();

    /// <summary>Does this block or item make whatever is on it float?</summary>
    internal static bool IsLevitatingDisplay(CollectibleObject collectible)
        => collectible?.Attributes?[LevitatingDisplayAttribute].AsBool(false) == true;

    /// <summary>This block itself is a levitating display (the standalone block): the item on its surface floats.</summary>
    internal bool IsFloatBlock => !IsEmbedded && IsLevitatingDisplay(Block);

    /// <summary>The motion a base asks for: rise 2.5 voxels, bob half a voxel over 2 s, one turn in 9 s, ease up in 1 s.</summary>
    internal static FloatMotion MotionOf(CollectibleObject LevitatingDisplay)
    {
        JsonObject attrs = LevitatingDisplay?.Attributes;
        return new FloatMotion(
            Math.Clamp(Number(attrs, "levitateRiseVoxels", 2.5f), 0f, 16f),
            Math.Clamp(Number(attrs, "levitateBobVoxels", 0.5f), 0f, 8f),
            Math.Max(0.1f, Number(attrs, "levitateBobSeconds", 2f)),
            Number(attrs, "levitateTurnSeconds", 9f),   // signed, as the menu's: negative turns the other way, 0 not at all
            Math.Max(0f, Number(attrs, "levitateEaseSeconds", 1f)),
            Math.Clamp(Number(attrs, "levitateTiltDeg", 0f), -90f, 90f),
            0f,
            Math.Clamp(Number(attrs, "levitateTiltForwardDeg", 0f), -90f, 90f));
    }

    /// <summary>The motion at one floating spot: the base's own, with the menu's settings for that spot on top.</summary>
    internal FloatMotion MotionAt(string spotId, CollectibleObject LevitatingDisplay)
    {
        FloatMotion motion = MotionOf(LevitatingDisplay);
        if (spotId == null || !floatTunings.TryGetValue(spotId, out FloatTuning tuning)) return motion;
        return new FloatMotion(tuning.Rise, tuning.Bob, tuning.BobSeconds, tuning.Spin == 0f ? 0f : 60f / tuning.Spin,
            motion.EaseSeconds, tuning.Tilt, tuning.Rot, tuning.TiltForward, tuning.OffsetX, tuning.OffsetZ);
    }

    /// <summary>The menu's view of a spot: its stored settings, or the base's own as a starting point.</summary>
    internal FloatTuning TuningAt(string spotId)
        => spotId != null && floatTunings.TryGetValue(spotId, out FloatTuning tuning) ? tuning : DefaultTuningAt(spotId);

    /// <summary>The base's own settings (its block's JSON) for a spot, as the menu shows them - what Reset goes back to.</summary>
    internal FloatTuning DefaultTuningAt(string spotId)
    {
        FloatMotion motion = MotionOf(FloatSourceOf(DisplaySlotId.Decode(spotId)));
        return new FloatTuning(motion.RiseVoxels, motion.TiltDeg, 0f,
            motion.TurnSeconds == 0f ? 0f : 60f / motion.TurnSeconds, motion.BobVoxels, motion.BobSeconds, motion.TiltForwardDeg,
            motion.OffsetXVoxels, motion.OffsetZVoxels);
    }

    static float Number(JsonObject attrs, string key, float fallback)
        => attrs != null && attrs[key].Exists ? attrs[key].AsFloat(fallback) : fallback;

    /// <summary>The stack in one slot of a snapshot, or null.</summary>
    static ItemStack StackAt(KeyValuePair<string, ItemSlot>[] slots, string slotId)
    {
        foreach (var kv in slots)
            if (kv.Key == slotId) return kv.Value is { Empty: false } ? kv.Value.Itemstack : null;
        return null;
    }

    /// <summary>
    /// The levitating display an item floats on - this block (standalone) or the base under it - or null if it does not float.
    /// <paramref name="slots"/> is taken only when it is needed, so this is cheap for the items that cannot float and
    /// safe on the tesselation thread (a snapshot, never the live inventory).
    /// </summary>
    internal CollectibleObject FloatSourceOf(DisplaySlotId loc, KeyValuePair<string, ItemSlot>[] slots = null)
    {
        if (loc == null) return null;
        if (loc.Y <= 0f) return IsFloatBlock ? Block : null;
        // column stacking: straight above the base, one level up, never a free-stacked item
        if (loc.Y != 1f || FreeData(loc.Encoded) != null) return null;
        ItemStack below = StackAt(slots ?? SlotsSnapshot(), loc.WithY(0f).Encoded);
        return IsLevitatingDisplay(below?.Collectible) ? below.Collectible : null;
    }

    /// <summary>How far above its natural resting height an item hovers, in voxels (0 when it does not float).</summary>
    float FloatRiseVoxels(DisplaySlotId loc, KeyValuePair<string, ItemSlot>[] slots)
    {
        CollectibleObject source = FloatSourceOf(loc, slots);
        return source == null ? 0f : MotionAt(loc.Encoded, source).RiseVoxels;
    }

    /// <summary>
    /// A floating item's two tilts and extra turn from the menu, in degrees, and its side-to-side / forward-back move in
    /// voxels (all 0 when it does not float).
    /// </summary>
    (float Tilt, float TiltForward, float Rot, float OffsetX, float OffsetZ) FloatPose(DisplaySlotId loc, KeyValuePair<string, ItemSlot>[] slots)
    {
        CollectibleObject source = FloatSourceOf(loc, slots);
        if (source == null) return (0f, 0f, 0f, 0f, 0f);
        FloatMotion motion = MotionAt(loc.Encoded, source);
        return (motion.TiltDeg, motion.TiltForwardDeg, motion.RotDeg, motion.OffsetXVoxels, motion.OffsetZVoxels);
    }

    // ── The menu (sneak + right-click with an empty hand) ────────────────

    /// <summary>
    /// The floating spot a sneak + right-click opens the menu for, or null: anywhere on the standalone block; on a
    /// display, the floating item or the base under it - so a base can be set up before its item is put on.
    /// </summary>
    internal string FloatMenuSpot(BlockSelection selection)
    {
        if (IsFloatBlock)
        {
            DisplaySurface surface = SurfaceBehavior?.GetSurface(0);
            return surface == null ? null : FloatBlockSpot(0, surface).Encoded;
        }

        DisplaySlotId loc = DisplaySlotId.Decode(AimedBoxId(selection));
        if (loc == null || !loc.IsPlacedItem) return null;
        KeyValuePair<string, ItemSlot>[] slots = SlotsSnapshot();
        if (FloatSourceOf(loc, slots) != null) return loc.Encoded;
        bool onBase = loc.Y <= 0f && FreeData(loc.Encoded) == null && IsLevitatingDisplay(StackAt(slots, loc.Encoded)?.Collectible);
        return onBase ? loc.WithY(1f).Encoded : null;
    }

    /// <summary>Server: store (or with <paramref name="tuning"/> null, forget) one spot's settings, for everyone.</summary>
    public void SetFloatTuning(string spotId, FloatTuning tuning)
    {
        if (Api?.Side != EnumAppSide.Server || spotId == null) return;
        // The id comes from a client: only a real spot, spelled the way the lib spells it, so one spot is one entry.
        DisplaySlotId loc = DisplaySlotId.Decode(spotId);
        if (loc == null || loc.Encoded != spotId || FloatSourceOf(loc) == null) return;
        if (IsFloatBlock && (SurfaceBehavior?.GetSurface(0) is not { } surface || FloatBlockSpot(0, surface).Encoded != spotId)) return;
        var next = new Dictionary<string, FloatTuning>(floatTunings);
        if (tuning == null) next.Remove(spotId);
        else next[spotId] = tuning.Clamped();
        floatTunings = next;
        InvalidateBoxes();   // the click box follows the height and the tilt
        MarkMeshesDirty();
        MarkDirty(true);
    }

    /// <summary>Server: settings for a spot whose base has gone go with it (OnContentsChanged).</summary>
    void PruneFloatTunings()
    {
        var map = floatTunings;
        if (map.Count == 0 || Api?.Side != EnumAppSide.Server) return;
        KeyValuePair<string, ItemSlot>[] slots = SlotsSnapshot();
        var keep = map.Where(kv => FloatSourceOf(DisplaySlotId.Decode(kv.Key), slots) != null)
                      .ToDictionary(kv => kv.Key, kv => kv.Value);
        if (keep.Count != map.Count) floatTunings = keep;
    }

    void WriteFloatTunings(ITreeAttribute tree)
    {
        var map = floatTunings;
        if (map.Count == 0) { tree.RemoveAttribute("floatTuning"); return; }
        var saved = new TreeAttribute();
        foreach (var kv in map)
        {
            var entry = new TreeAttribute();
            entry.SetFloat("rise", kv.Value.Rise); entry.SetFloat("tilt", kv.Value.Tilt); entry.SetFloat("rot", kv.Value.Rot);
            entry.SetFloat("spin", kv.Value.Spin); entry.SetFloat("bob", kv.Value.Bob); entry.SetFloat("bobSeconds", kv.Value.BobSeconds);
            entry.SetFloat("tiltForward", kv.Value.TiltForward);
            entry.SetFloat("offsetX", kv.Value.OffsetX); entry.SetFloat("offsetZ", kv.Value.OffsetZ);
            saved[kv.Key] = entry;
        }
        tree["floatTuning"] = saved;
    }

    /// <summary>ALWAYS replaced, empty included - the tree is authoritative, as for the stored angles and heights.</summary>
    void ReadFloatTunings(ITreeAttribute tree)
    {
        var next = new Dictionary<string, FloatTuning>();
        if (tree.GetTreeAttribute("floatTuning") is { } saved)
            foreach (var row in saved)
                if (row.Value is ITreeAttribute e && DisplaySlotId.Decode(row.Key) != null)
                    next[row.Key] = new FloatTuning(e.GetFloat("rise"), e.GetFloat("tilt"), e.GetFloat("rot"),
                        e.GetFloat("spin"), e.GetFloat("bob"), e.GetFloat("bobSeconds", 2f), e.GetFloat("tiltForward"),
                        e.GetFloat("offsetX"), e.GetFloat("offsetZ")).Clamped();
        floatTunings = next;
    }

    // ── Placing ──────────────────────────────────────────────────────────

    /// <summary>
    /// The floating rules for an aim at a placed item, or false when they do not apply (ordinary stacking decides).
    /// True with <paramref name="allowed"/> false is a refusal: onto a floating item, or a base onto anything but the
    /// surface. An item onto a base goes straight above it (<paramref name="target"/>), facing the player.
    /// Shared by TryPut and the ghost (TryResolveStackTargetOn) so the preview cannot disagree with the click.
    /// </summary>
    internal bool TryResolveFloatStack(DisplaySlotId aimed, DisplayableAttributes heldAttr, IPlayer player,
                                       out DisplaySlotId target, out float rotDeg, out bool allowed)
    {
        target = aimed; rotDeg = 0f; allowed = false;
        if (aimed == null) return false;
        DisplaySurface surface = SurfaceBehavior?.GetSurface(aimed.SurfaceIndex);
        if (surface == null || heldAttr?.Size == null) return false;

        KeyValuePair<string, ItemSlot>[] slots = SlotsSnapshot();
        ItemStack aimedStack = StackAt(slots, aimed.Encoded);
        if (aimedStack == null) return false;

        player ??= (Api as ICoreClientAPI)?.World.Player;
        bool heldIsBase = IsLevitatingDisplay(player?.InventoryManager?.ActiveHotbarSlot?.Itemstack?.Collectible);
        bool aimedFloats = FloatSourceOf(aimed, slots) != null;
        bool aimedIsBase = !aimedFloats && aimed.Y <= 0f && FreeData(aimed.Encoded) == null
                           && IsLevitatingDisplay(aimedStack.Collectible);
        if (!aimedFloats && !aimedIsBase && !heldIsBase) return false;

        rotDeg = PlacementRotation.ForSurface(Api, player, this, surface);
        target = aimed.WithY(aimed.Y + 1f);
        // nothing on a floating item, and a base only on the surface itself (the ghost shows red)
        if (!aimedIsBase || heldIsBase) return true;
        // one item per base
        if (StackAt(slots, target.Encoded) != null) return true;

        allowed = (UnboundedStacking || StackBaseY(target, surface, heldAttr, slots) < surface.Size.Height)
                  && StackTargetFits(target, surface, heldAttr, rotDeg, player);
        return true;
    }

    /// <summary>TryPut's floating branch: place onto a base, or refuse as any stack that does not fit is refused.</summary>
    bool TryPutFloatStack(IPlayer byPlayer, ItemSlot held, DisplaySlotId target, float rotDeg, bool allowed)
    {
        if (!allowed)
        {
            (Api as ICoreClientAPI)?.TriggerIngameError(this, "freestackfit", Lang.Get("universaldisplaylib:stack-needs-space"));
            return true;
        }
        return PlaceItem(byPlayer, held, target.Encoded, (int)Math.Round(rotDeg));
    }

    /// <summary>The standalone block's one spot: the middle of its surface, on the surface itself.</summary>
    static DisplaySlotId FloatBlockSpot(int surfaceIndex, DisplaySurface surface) => new DisplaySlotId
    {
        SurfaceIndex = surfaceIndex,
        X = surface.Size.Width / 2f,
        Z = surface.Size.Length / 2f,
        Y = 0f
    };

    bool HasItemOn(int surfaceIndex, KeyValuePair<string, ItemSlot>[] slots)
        => slots.Any(kv => kv.Value is { Empty: false } && DisplaySlotId.Decode(kv.Key)?.SurfaceIndex == surfaceIndex);

    /// <summary>
    /// The standalone block's spot for an aim at its surface, or false when this is not a floating-base block. Wherever
    /// the surface is clicked the item goes in the middle; <paramref name="free"/> is false once something is there.
    /// </summary>
    internal bool TryFloatBlockSpot(DisplaySlotId aimed, DisplayableAttributes heldAttr, out DisplaySlotId spot, out bool free)
    {
        spot = null; free = false;
        if (!IsFloatBlock || aimed == null) return false;
        DisplaySurface surface = SurfaceBehavior?.GetSurface(aimed.SurfaceIndex);
        if (surface == null) return false;
        KeyValuePair<string, ItemSlot>[] slots = SlotsSnapshot();
        spot = FloatBlockSpot(aimed.SurfaceIndex, surface);
        free = !aimed.IsPlacedItem && !HasItemOn(aimed.SurfaceIndex, slots)
               && (UnboundedStacking || heldAttr?.Size == null || StackBaseY(spot, surface, heldAttr, slots) < surface.Size.Height);
        return true;
    }

    /// <summary>TryPut on the standalone block: the one item, centred, if it fits on the surface.</summary>
    bool TryPutOnFloatBlock(IPlayer byPlayer, ItemSlot held, DisplaySurface surface, DisplayableAttributes dattr, DisplaySlotId aimed)
    {
        if (!TryFloatBlockSpot(aimed, dattr, out DisplaySlotId spot, out bool free)) return false;
        if (!free)
        {
            (Api as ICoreClientAPI)?.TriggerIngameError(this, "freestackfit", Lang.Get("universaldisplaylib:stack-needs-space"));
            return true;
        }

        int rotDeg = PlacementRotation.ForSurface(Api, byPlayer, this, surface);
        Size3f fitSize = FitSize(BlockBehaviorDisplaySurfaces.RotatedFootprint(dattr.Size, rotDeg + SurfaceFacingDeg(surface)));
        if (surface.Size.Width < fitSize.Width || surface.Size.Length < fitSize.Length)
        {
            (Api as ICoreClientAPI)?.TriggerIngameError(this, "toolarge", Lang.Get("shelfhelp-toolarge-error"));
            return true;
        }

        if (!EmbeddedPlacementFits(spot, dattr, rotDeg, byPlayer)) return true;
        return PlaceItem(byPlayer, held, spot.Encoded, rotDeg);
    }

    /// <summary>
    /// The standalone block's interaction help: put an item on, or take it off. Its words come from the block's own
    /// <c>levitatePlaceHelp</c> / <c>levitateTakeHelp</c> lang keys, the game's ground storage ones when it names none.
    /// </summary>
    internal WorldInteraction FloatBlockHelp()
    {
        JsonObject attrs = Block?.Attributes;
        if (SlotsSnapshot().Any(kv => kv.Value is { Empty: false }))
            return new WorldInteraction
            {
                ActionLangCode = attrs?["levitateTakeHelp"].AsString("blockhelp-groundstorage-remove") ?? "blockhelp-groundstorage-remove",
                MouseButton = EnumMouseButton.Right,
                RequireFreeHand = true
            };
        return new WorldInteraction
        {
            ActionLangCode = attrs?["levitatePlaceHelp"].AsString("blockhelp-groundstorage-add") ?? "blockhelp-groundstorage-add",
            MouseButton = EnumMouseButton.Right
        };
    }

    // ── The motion (client) ──────────────────────────────────────────────

    /// <summary>Client: the floating items drawn moving right now, by slot. Concurrent: the particle thread reads it (FollowHover).</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, FloatingItemRenderer> _hovers = new();
    /// <summary>Client: the stack last seen in each floating slot, so only a newly put item eases up.</summary>
    readonly Dictionary<string, string> _hoverKnown = new();
    /// <summary>Client: items found before this (the world loading) are already hovering; later ones ease up.</summary>
    long _hoverEaseFrom = long.MaxValue;

    /// <summary>
    /// Blocks: drawn moving within HoverNearBlocks, still again beyond HoverFarBlocks. The gap keeps it from flickering
    /// between the two when the player stands right at the edge.
    /// </summary>
    const double HoverNearBlocks = 18, HoverFarBlocks = 22;

    /// <summary>
    /// Client, main thread: start, keep or stop the moving copy of every floating item. A moving item is left out of
    /// the block's mesh (_floatingSlots, as the Halloween float does); a still one is in it, at its hover height, and
    /// costs nothing per frame. Run from the client tick and as soon as the contents change, so a new item eases up
    /// from the base instead of appearing at its hover height first.
    /// </summary>
    void UpdateHovers()
    {
        if (capi == null || _removed || inv == null || SurfaceBehavior == null) return;
        if (_hoverEaseFrom == long.MaxValue) _hoverEaseFrom = Environment.TickCount64 + 1500;

        KeyValuePair<string, ItemSlot>[] slots = SlotsSnapshot();
        // the cheap answer for every display with no base on it
        if (_hovers.Count == 0 && _hoverKnown.Count == 0 && !IsFloatBlock
            && !slots.Any(kv => kv.Value is { Empty: false } && IsLevitatingDisplay(kv.Value.Itemstack.Collectible))) return;

        bool animate = UniversalDisplayLibConfig.Current?.LevitatingAnimation != false;
        Vec3d eye = capi.World.Player?.Entity?.CameraPos;
        double distance = eye == null ? double.MaxValue : Math.Sqrt(
            (eye.X - Pos.X - 0.5) * (eye.X - Pos.X - 0.5) + (eye.Y - Pos.Y - 0.5) * (eye.Y - Pos.Y - 0.5)
            + (eye.Z - Pos.Z - 0.5) * (eye.Z - Pos.Z - 0.5));
        bool easeNew = Environment.TickCount64 >= _hoverEaseFrom;

        var seen = new HashSet<string>();
        foreach (var kv in slots)
        {
            ItemSlot slot = kv.Value;
            if (slot is not { Empty: false } || slot.Itemstack.Collectible?.Code == null) continue;
            CollectibleObject source = FloatSourceOf(DisplaySlotId.Decode(kv.Key), slots);
            if (source == null) continue;
            seen.Add(kv.Key);

            string key = slot.Itemstack.Class + ":" + slot.Itemstack.Id;
            bool isNew = !_hoverKnown.TryGetValue(kv.Key, out string was) || was != key;
            _hoverKnown[kv.Key] = key;

            float[] matrix = BuildSlotMatrix(kv.Key, slot, out DisplayableAttributes dattr);
            bool running = _hovers.TryGetValue(kv.Key, out FloatingItemRenderer hover) && !hover.Finished;
            bool wanted = animate && matrix != null && distance <= (running ? HoverFarBlocks : HoverNearBlocks);

            FloatMotion motion = MotionAt(kv.Key, source);
            if (running && wanted && !isNew)
            {
                // turned, nudged, resized or set up in the menu since it started: carry on from where it is
                hover.Retarget(matrix, motion);
                continue;
            }
            if (running || hover != null) StopHover(kv.Key);
            if (!wanted) continue;

            MeshData mesh = GetCachedItemMesh(slot) ?? (dattr == null ? null : GetOrCreateItemMesh(slot, dattr));
            if (mesh == null) continue;
            FloatingItemRenderer started = FloatingItemRenderer.TryStartHover(capi, this, kv.Key, slot.Itemstack, mesh, matrix,
                motion, isNew && easeNew);
            if (started == null) continue;
            _hovers[kv.Key] = started;
            _floatingSlots[kv.Key] = 0;
            NoteLeftOutChanged();
            // a resting item is still in the block's mesh until the redraw; a newly placed one never was, and a display
            // inside a chiselled block is drawn by its host, so neither would ever see the pass this waits for
            if (!(isNew && easeNew) && !IsEmbedded) started.HoldUntilRedrawn(_leftOutVersion);
            RedrawWithoutMovingItems();
        }

        foreach (string gone in _hovers.Keys.Where(id => !seen.Contains(id)).ToList()) StopHover(gone);
        foreach (string gone in _hoverKnown.Keys.Where(id => !seen.Contains(id)).ToList()) _hoverKnown.Remove(gone);
    }

    /// <summary>Any thread: a stored item's particle origin, moved with its copy while that is drawn bobbing and turning.</summary>
    internal Vec3f FollowHover(string slotId, Vec3f still)
        => still != null && slotId != null && _hovers.TryGetValue(slotId, out FloatingItemRenderer hover) ? hover.Follow(still) : still;

    /// <summary>Client: how far an item's moving copy is lifted off its still height now, in blocks (0 when it is still).</summary>
    internal float HoverLift(string slotId)
        => slotId != null && _hovers.TryGetValue(slotId, out FloatingItemRenderer hover) ? hover.LiftNow : 0f;

    /// <summary>Client: stop drawing one item moving - back into the block's mesh, still, at its hover height.</summary>
    void StopHover(string slotId)
    {
        if (!_hovers.TryGetValue(slotId, out FloatingItemRenderer hover)) return;
        _hovers.TryRemove(slotId, out _);
        hover.Stop();   // unregisters, and EndFloat puts it back into the mesh
        if (IsEmbedded) RedrawWithoutMovingItems();
    }

    /// <summary>
    /// Client: draw the block again, leaving out the items drawn moving (_floatingSlots). Inside a chiselled block the
    /// host block draws a copy of this display's mesh, so that copy is rebuilt - redrawing the host alone would show
    /// the old one, the moving item twice.
    /// </summary>
    void RedrawWithoutMovingItems()
    {
        if (IsEmbedded) QueueEmbeddedMeshes();
        else capi.World.BlockAccessor.MarkBlockDirty(Pos);
    }

    /// <summary>Client: how often to ask again for a redraw that has not started yet, in milliseconds.</summary>
    const int LeftOutRedrawRetryMs = 1000;

    /// <summary>Client: counts each change to which items the block's mesh leaves out (_floatingSlots). Written on the main thread only.</summary>
    volatile int _leftOutVersion;
    /// <summary>Client: _leftOutVersion as the latest tesselation pass began. Written on the tesselation thread.</summary>
    volatile int _leftOutDrawn;
    long _leftOutRetryAt;

    /// <summary>Client, main thread: call AFTER changing _floatingSlots, so a pass that sees the new version also sees the new set.</summary>
    void NoteLeftOutChanged()
    {
        _leftOutVersion++;
        _leftOutRetryAt = Environment.TickCount64 + LeftOutRedrawRetryMs;
    }

    /// <summary>Client: whether a tesselation pass has begun since this version of the left-out items.</summary>
    internal bool RedrawnSince(int version) => _leftOutDrawn >= version;

    /// <summary>
    /// Client, main thread: ask again until the block has really been redrawn since the last change. While the world is
    /// loading a single redraw request can come too early to count, and the item then shows twice: moving, and still in the
    /// old mesh, until something else redraws the block. Costs one comparison when nothing is waiting.
    /// </summary>
    void RecheckLeftOutRedraw()
    {
        if (_leftOutDrawn == _leftOutVersion || capi == null || _removed) return;
        long now = Environment.TickCount64;
        if (now < _leftOutRetryAt) return;
        _leftOutRetryAt = now + LeftOutRedrawRetryMs;
        capi.World.BlockAccessor.MarkBlockDirty(Pos);
    }
}

/// <summary>
/// One floating spot's settings from the menu (sneak + right-click, GuiDialogFloatTuner): height, tilt and turn in
/// voxels and degrees (Tilt side to side, TiltForward back and forward), the spin in turns a minute (negative the other
/// way, 0 still), the bob in voxels and seconds, and the side-to-side (OffsetX) / forward-back (OffsetZ) move in voxels.
/// Kept by the SPOT, not the item, so a base keeps its settings when its item is swapped; dropped with the base.
/// </summary>
public sealed record FloatTuning(float Rise, float Tilt, float Rot, float Spin, float Bob, float BobSeconds, float TiltForward,
                                 float OffsetX = 0f, float OffsetZ = 0f)
{
    /// <summary>Kept to sane ranges here, where the server stores them - the packet comes from a client.</summary>
    public FloatTuning Clamped() => new FloatTuning(
        Math.Clamp(Finite(Rise), 0f, 12f), Math.Clamp(Finite(Tilt), -90f, 90f), GameMath.Mod(Finite(Rot), 360f),
        Math.Clamp(Finite(Spin), -60f, 60f), Math.Clamp(Finite(Bob), 0f, 8f), Math.Clamp(Finite(BobSeconds), 0.2f, 30f),
        Math.Clamp(Finite(TiltForward), -90f, 90f), Math.Clamp(Finite(OffsetX), -8f, 8f), Math.Clamp(Finite(OffsetZ), -8f, 8f));

    static float Finite(float v) => float.IsFinite(v) ? v : 0f;
}
