using System;
using System.Globalization;
using System.Linq;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Client.NoObf;

[assembly: ModInfo("Window Display", "universaldisplaylib",
    Description = "Adds functional window storage blocks with animations and environmental sounds",
    Authors = new[] { "Calminaion" })]

namespace UniversalDisplayLib
{
    /// <summary>
    /// **EVERY PACKET TYPE NEEDS [ProtoContract] - a lesson that cost a shipped release.**
    /// LinkedOpenPacket (the tall-window pane sync, removed with the chain windows on 2026-10-05)
    /// went out in 1.0.1 without it. On a dedicated server the first BroadcastPacket threw
    /// "Type is not expected, and no contract can be inferred" out of
    /// ProtoBuf.Meta.TypeModel.ThrowUnexpectedType, which the server treats as the player having
    /// thrown an exception and KICKS THEM - opening any window disconnected the player.
    /// Singleplayer never showed it: the integrated server does not put the packet through
    /// protobuf on that path. Anything that only crosses a real network needs testing on a real
    /// server, exactly as the render path needs testing in game.
    /// </summary>
    internal static class PacketContractNote { }


    /// <summary>
    /// Tells the server the angle this player wants their next item placed at.
    ///
    /// The ProtoContract is required, not decoration. protobuf-net cannot infer a contract
    /// for a plain class and throws "Type is not expected, and no contract can be inferred"
    /// the first time it is sent — which took the client down on the first key press.
    /// </summary>
    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class PlacementRotationPacket
    {
        public int Deg;
    }

    /// <summary>
    /// Turn an item that is ALREADY on a surface, from the mouse wheel.
    ///
    /// A packet because this is server state: the wrench path reaches the same code through
    /// OnBlockInteractStart, which the engine runs on both sides for us. A wheel notch is
    /// purely a client input, so it has to be sent.
    ///
    /// ProtoContract for the same reason PlacementRotationPacket needs one — protobuf-net
    /// cannot infer a contract for a plain class and the first send throws "Type is not
    /// expected".
    ///
    /// A comment here once claimed a broadcast packet "survives without one". **That was wrong
    /// and it cost a shipped release** - every packet type needs the attribute; see
    /// PacketContractNote.
    /// </summary>
    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class RotateStoredItemPacket
    {
        public int X, Y, Z;
        public string SlotId;
        public bool Reverse;

        /// <summary>
        /// The step this player wants, in degrees, from THEIR config.
        ///
        /// SENT RATHER THAN LOOKED UP, so turning an item already on a shelf uses the same
        /// step as turning the ghost before placing it. The server used to read its own
        /// RotationStepDegrees here, which meant the two halves of one gesture moved by
        /// different amounts whenever a player's config differed from the server's - and the
        /// ghost, being client-side, always used the player's. Bounded server-side; see
        /// TryRotateItem.
        /// </summary>
        public float StepDeg;
    }

    /// <summary>
    /// Tells the server the height nudge this player wants their next item placed with.
    /// The height twin of <see cref="PlacementRotationPacket"/>, and needed for the same
    /// reason: the placement interaction carries no room for extra data, so the value has
    /// to have been sent already.
    ///
    /// ProtoContract for the reason spelled out on every packet in this file — there is no
    /// exemption, and assuming there was cost a shipped release once.
    /// </summary>
    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class PlacementHeightPacket
    {
        public float Voxels;
    }

    /// <summary>
    /// Nudge an item that is ALREADY on a surface up or down, from the mouse wheel.
    /// The height twin of <see cref="RotateStoredItemPacket"/>.
    /// </summary>
    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class AdjustStoredItemHeightPacket
    {
        public int X, Y, Z;
        public string SlotId;
        public bool Down;

        /// <summary>
        /// The step this player wants, in voxels, from THEIR config. Same reasoning as
        /// RotateStoredItemPacket.StepDeg, and bounded server-side the same way.
        /// </summary>
        public float StepVoxels;
    }

    /// <summary>
    /// Tells a joining client the limits the SERVER will enforce.
    ///
    /// WHY THIS EXISTS. The step is the player's preference, but the LIMIT is the server's
    /// rule - how far an item may be raised at all is shared, because the result is stored
    /// on the block and everyone sees it. Both sides used to clamp with their own copy of
    /// the config, so a client with a larger limit drew a ghost at a height the server then
    /// pulled back, and the item did not land where the ghost said it would.
    ///
    /// **The ProtoContract is required** - see PacketContractNote for what its absence cost.
    /// </summary>
    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class PlacementLimitsPacket
    {
        public float HeightLimitVoxels;

        /// <summary>Whether items may share space. See ServerRules.</summary>
        public bool AllowItemOverlap;

        /// <summary>Whether opening a display block keeps a room sealed. See ServerRules.</summary>
        public bool RoomSafeOpening;

        /// <summary>See ServerRules.PrecisePlacementOn - receiving this rebuilds the grid.</summary>
        public bool PrecisePlacement;
    }

    /// <summary>
    /// The settings the SERVER decides, as told to this client.
    ///
    /// WHY THIS EXISTS, and it is the same lesson three times over. Every one of these is
    /// read as `UniversalDisplayLibConfig.Current` on whichever side happens to be running,
    /// so a "server setting" was really "two copies that can disagree". On 2026-09-10 the
    /// two files on one machine read:
    ///
    ///     AllowItemOverlap   server False   client True
    ///     PrecisePlacement   server False   client True
    ///     HeightLimitVoxels  server 1.0     client 8.0
    ///
    /// The client cheerfully allowed overlapping placements and never showed the "shelf
    /// full" warning, because it was asking itself rather than the server. It had looked
    /// correct until then only because both files still held the default.
    ///
    /// Null means the server has not said - singleplayer before the push, or an older
    /// server - and every property falls back to the local config, which is the behaviour
    /// that existed before any of this.
    ///
    /// NOT merged into `Current`. Writing the server's answers into the local config object
    /// would put them in the player's own file the next time it is saved, so a player who
    /// visited a strict server would carry its rules home.
    /// </summary>
    public static class ServerRules
    {
        public static bool? AllowOverlap;
        public static bool? RoomSafe;
        public static bool? Precise;

        public static bool ItemOverlapAllowed =>
            AllowOverlap ?? (UniversalDisplayLibConfig.Current?.AllowItemOverlap ?? false);

        public static bool RoomSafeOpening =>
            RoomSafe ?? (UniversalDisplayLibConfig.Current?.RoomSafeOpening ?? false);

        /// <summary>
        /// Half-voxel placement spots. UNLIKE the other two this cannot simply be read at
        /// the moment it matters: the grid is built once per block type, so a change has to
        /// REBUILD it - see BlockBehaviorDisplaySurfaces.RebuildAll.
        /// </summary>
        public static bool PrecisePlacementOn =>
            Precise ?? (UniversalDisplayLibConfig.Current?.PrecisePlacement ?? true);

        /// <summary>Forgotten with the other per-world statics; the next server is not this one.</summary>
        public static void Forget()
        {
            AllowOverlap = null;
            RoomSafe = null;
            Precise = null;
        }
    }

    /// <summary>
    /// The height nudge a player wants their next placed item to sit at, in voxels away
    /// from where it would rest on its own.
    ///
    /// **A CORRECTION, NOT A POSITION.** `StackBaseY` already puts an item on top of
    /// whatever is under it, and this is only added to that — so zero means "wherever it
    /// naturally lands", which is what every item placed before this existed has stored.
    /// That is what makes the feature free to ignore.
    ///
    /// Deliberately a near-copy of <see cref="PlacementRotation"/>, including sending the
    /// value rather than deriving it on both sides. Everything that class's comments say
    /// about the client and server drifting apart applies here word for word; nothing here
    /// is different enough to justify a different shape.
    /// </summary>
    public static class PlacementHeight
    {
        /// <summary>
        /// Voxels per wheel notch, clamped rather than trusted — a zero step would make the
        /// gesture do nothing and look broken, which is the same trap the rotation step has.
        /// </summary>
        public static float Step =>
            Math.Clamp(UniversalDisplayLibConfig.Current?.HeightStepVoxels ?? 0.25f, 0.05f, 4f);

        /// <summary>
        /// How far either way a nudge may go.
        ///
        /// **THE SERVER'S NUMBER WINS, AND THE CLIENT IS TOLD IT ON JOIN.** The limit is a
        /// rule rather than a preference: the height ends up stored on the block and
        /// everyone sees it, so one player cannot be allowed a taller one. Both sides used
        /// to clamp with their own copy of the config, which is exactly how the ghost came
        /// to show a height the server then pulled back - reported 2026-09-10.
        ///
        /// Falls back to this side's own config when the server has not said (singleplayer
        /// before the push, or an older server), which is the behaviour that existed before.
        /// </summary>
        public static float Limit =>
            ServerLimit ?? Math.Clamp(UniversalDisplayLibConfig.Current?.HeightLimitVoxels ?? 8f, 1f, 16f);

        /// <summary>
        /// The server's limit, once it has told us. Client-side only; null until then.
        /// Cleared in StartClientSide, because a static outlives a world and the next
        /// server may not be this one.
        /// </summary>
        public static float? ServerLimit;

        /// <summary>Client side: this player's own pending nudge.</summary>
        public static float Local;

        private static readonly System.Collections.Generic.Dictionary<string, float> ByPlayer =
            new System.Collections.Generic.Dictionary<string, float>();

        /// <summary>
        /// Held inside the configured limit, and rounded to a thousandth of a voxel so
        /// repeated stepping cannot accumulate float dust into the saved value.
        /// </summary>
        public static float Clamp(float voxels)
            => (float)Math.Round(Math.Clamp(voxels, -Limit, Limit), 3);

        public static void Remember(string playerUid, float voxels)
        {
            if (playerUid != null) ByPlayer[playerUid] = Clamp(voxels);
        }

        /// <summary>
        /// Back to 0 once an item is placed, exactly as the angle is — one gesture, one
        /// placement, so a nudge cannot silently follow everything placed afterwards.
        ///
        /// UNLIKE the angle, a stacked item does NOT inherit this from the item below. The
        /// angle is inherited to keep a pile uniform; a nudge exists to correct a single
        /// awkward item, so copying it upward would spread the exception rather than the
        /// rule.
        /// </summary>
        public static void ResetAfterPlacing(ICoreAPI api, IPlayer player)
        {
            if (api?.Side == EnumAppSide.Client)
            {
                Local = 0f;
                return;
            }

            if (player == null) return;
            ByPlayer.Remove(player.PlayerUID);

            // Pushed back for the same reason the angle is: the client's PlaceItem can bail
            // out before this point, so leaving each side to reset itself lets them disagree.
            if (player is IServerPlayer sp)
            {
                UniversalDisplayLibMod.ServerChannel?.SendPacket(new PlacementHeightPacket { Voxels = 0f }, sp);
            }
        }

        /// <summary>
        /// The nudge to use for a placement. A player who has never sent one gets 0, which
        /// is exactly the behaviour before this existed.
        /// </summary>
        public static float For(ICoreAPI api, IPlayer player)
        {
            if (api?.Side == EnumAppSide.Client) return Clamp(Local);
            return player != null && ByPlayer.TryGetValue(player.PlayerUID, out float v) ? v : 0f;
        }

        /// <summary>
        /// Drop every remembered nudge. Called from StartServerSide — a static outlives a
        /// world, so without this a player who nudged and then left without placing would
        /// have their first placement in the next world arrive silently offset.
        /// </summary>
        public static void ForgetAll() => ByPlayer.Clear();
    }

    /// <summary>
    /// The angle a player wants their next placed item turned to, in quarter turns.
    ///
    /// It has to TRAVEL with the interaction rather than be worked out twice. The client
    /// previews from its own state while the server places from the BlockSelection it
    /// receives, and anything derived independently on the two sides drifts apart — which is
    /// exactly why sub-voxel placement was reverted, and why the session log's conclusion was
    /// that the value must be sent. So the client owns it, sends it whenever it changes, and
    /// the server remembers the last one per player.
    ///
    /// **Free rotation since 2026-07-29**, sharing `RotationStepDegrees` with the wrench so
    /// one setting governs turning an item before and after it is placed.
    ///
    /// It was quarter turns only, because a footprint is an axis-aligned box and
    /// `RotatedFootprint` swaps width and length only past 45° — so at any other angle the
    /// fit test measures the item square while the model sits turned, and "fits" becomes an
    /// approximation. That restriction existed because nothing SHOWED the discrepancy.
    ///
    /// The ghost removed the reason for it. The player can now see the item overhang, which
    /// is better feedback than a rule preventing them from trying. The wrench has always
    /// worked this way on already-placed items and nobody has minded.
    /// </summary>
    public static class PlacementRotation
    {
        /// <summary>
        /// Degrees per wheel notch, from the config. Clamped rather than trusted: a zero
        /// would make the gesture do nothing at all and look broken.
        /// </summary>
        public static int Step =>
            Math.Clamp(UniversalDisplayLibConfig.Current?.RotationStepDegrees ?? 15, 5, 90);

        /// <summary>Client side: this player's own pending angle.</summary>
        public static int Local;

        private static readonly System.Collections.Generic.Dictionary<string, int> ByPlayer =
            new System.Collections.Generic.Dictionary<string, int>();

        public static int Normalise(int deg) => ((deg % 360) + 360) % 360;

        public static void Remember(string playerUid, int deg)
        {
            if (playerUid != null) ByPlayer[playerUid] = Normalise(deg);
        }

        /// <summary>
        /// Back to 0 once an item has been placed, so a turn applies to ONE placement rather
        /// than silently to everything afterwards — the angle persisting invisibly was the
        /// main confusion in testing.
        ///
        /// Safe only because stacking inherits the angle of the item it is stacking onto: a
        /// pile still comes out uniform without the key being pressed again. Before that
        /// existed, resetting would have meant re-pressing for every item of a stack.
        ///
        /// Both sides run the placement, so each resets its own copy and they stay in step
        /// without another packet.
        /// </summary>
        public static void ResetAfterPlacing(ICoreAPI api, IPlayer player)
        {
            if (api?.Side == EnumAppSide.Client)
            {
                Local = 0;
                return;
            }

            if (player == null) return;
            ByPlayer.Remove(player.PlayerUID);

            // TELL THE CLIENT. Relying on both sides to reset themselves does not work: the
            // client's PlaceItem can bail out before this point (its TryPutInto returns 0 on a
            // predicted placement), so the server cleared its copy while the client kept the
            // old angle — the preview and the info panel then said 90 while the server would
            // have placed at 0. Sending it makes the client's value follow the authority,
            // which is the same reasoning as sending the angle in the first place.
            if (player is IServerPlayer sp)
            {
                UniversalDisplayLibMod.ServerChannel?.SendPacket(new PlacementRotationPacket { Deg = 0 }, sp);
            }
        }

        /// <summary>
        /// The angle to use for a placement. A player who has never sent one gets 0, which is
        /// the previous behaviour — nothing changes until someone actually uses the key.
        /// </summary>
        public static int For(ICoreAPI api, IPlayer player)
        {
            if (api?.Side == EnumAppSide.Client) return Normalise(Local);
            return player != null && ByPlayer.TryGetValue(player.PlayerUID, out int d) ? d : 0;
        }

        /// <summary>
        /// Drop every remembered angle. Called from <c>StartServerSide</c>, because a static
        /// outlives a world: a player who turned an item and then left WITHOUT placing it
        /// leaves an entry behind, and the same UID rejoining a different world would have
        /// its first placement silently arrive turned.
        ///
        /// Same trap as <c>PlacementRotation.Local</c> and <c>ForceFootprintBox</c> on the
        /// client, which are cleared in <c>StartClientSide</c> for exactly this reason.
        /// Server start is the honest point to do it: no player can have a pending angle yet.
        /// </summary>
        public static void ForgetAll() => ByPlayer.Clear();
    }

    public class UniversalDisplayLibMod : ModSystem
    {
        public static IServerNetworkChannel ServerChannel;
        public static IClientNetworkChannel ClientChannel;

        private PlacementGhostRenderer ghostRenderer;
        private GuiDialogItemTuner tuner;
        private ICoreClientAPI capi;

        /// <summary>
        /// Sprint + wheel. "Sprint" and "ctrl" are the same physical key for most players —
        /// ClientSettings clones keyMapping["ctrl"] from keyMapping["sprint"] unless
        /// separateCtrlKeyForMouse is on — so this is the same modifier as ctrl+wrench and
        /// there is nothing new to learn. Reading the KEY rather than the ACTION is the
        /// settled rule: ORing the two made the swap modifier depend on the player's own
        /// keybinds and look intermittent.
        /// </summary>
        private void OnMouseWheel(MouseWheelEventArgs args)
        {
            if (args.delta == 0 || capi == null) return;

            EntityControls controls = capi.World.Player?.Entity?.Controls;
            if (controls == null) return;

            // CTRL FIRST, so holding both modifiers behaves exactly as it did before height
            // existed. ShiftKey/CtrlKey are the modifier KEYS rather than the Sneak/Sprint
            // ACTIONS — the settled rule in this file, because ORing an action with a key
            // made the modifier depend on the player's own keybinds and look intermittent.
            bool handled;
            if (controls.CtrlKey)
            {
                // Same modifier, two jobs, chosen by what is under the crosshair: a pad turns
                // the item you are ABOUT to place, an item already there turns THAT. Reads as
                // one gesture, and neither can be reached while pointing at the other.
                handled = TryWheelRotate(capi, args.delta > 0);
            }
            else if (controls.ShiftKey)
            {
                // Sneak + wheel, the height twin of the above and split the same way.
                handled = TryWheelHeight(capi, args.delta > 0);
            }
            else return;

            if (!handled) return;

            // MUST be handled, or the wheel moves the item AND changes hotbar slot on every
            // notch. Only reached with a modifier down and the aim on one of our boxes, so
            // the wheel is never taken globally.
            args.SetHandled(true);
        }

        /// <summary>
        /// Changing what you are holding drops the pending angle.
        ///
        /// Reported 2026-07-31: turn a shirt to 90°, wheel across to a wrench, and the wrench
        /// is at 90° too — the angle belonged to the *player* rather than to the thing being
        /// placed. It is the same complaint that produced the reset-after-placing rule, which
        /// exists so a turn applies to ONE placement instead of silently to everything
        /// afterwards; picking up something else ends that placement just as surely as making
        /// it does. The angle was simply outliving the only event that was thought to end it.
        ///
        /// **The send is not optional and the guard on it is not either.** The server places
        /// from the last angle it was told, so clearing only the client's copy would leave the
        /// two disagreeing — the exact fault that made `ResetAfterPlacing` push a packet back.
        /// But players scroll the hotbar constantly, so this sends ONLY when there is
        /// something to clear.
        /// </summary>
        private void OnActiveSlotChanged(ActiveSlotChangeEventArgs args)
        {
            if (PlacementRotation.Local != 0)
            {
                PlacementRotation.Local = 0;
                ClientChannel?.SendPacket(new PlacementRotationPacket { Deg = 0 });
            }

            // The nudge belongs to the thing being placed, not to the player, so picking up
            // something else ends it — the same reasoning, and the same send-only-when-there-
            // is-something-to-clear guard, since players scroll the hotbar constantly.
            if (PlacementHeight.Local != 0f)
            {
                PlacementHeight.Local = 0f;
                ClientChannel?.SendPacket(new PlacementHeightPacket { Voxels = 0f });
            }
        }

        /// <summary>
        /// Re-read the config file and make the new values felt. Shared by the IMM and
        /// ConfigKit listeners in Start; see the comment there for why the server defers it.
        /// </summary>
        private static void ApplyConfigReload(ICoreAPI api, string savedBy)
        {
            var c0 = UniversalDisplayLibConfig.Current;
            var was = (c0.HeightLimitVoxels, c0.PrecisePlacement, c0.AllowItemOverlap, c0.RoomSafeOpening);

            if (!UniversalDisplayLibConfig.Reload(api)) return;

            if (api.Side == EnumAppSide.Server)
            {
                var c1 = UniversalDisplayLibConfig.Current;
                var now = (c1.HeightLimitVoxels, c1.PrecisePlacement, c1.AllowItemOverlap, c1.RoomSafeOpening);

                // NOTHING TO SAY IF NO SERVER RULE CHANGED. The managers forward a client's
                // save to the server whenever an ADMIN saves - their own client settings
                // included - so without this the server announced a reload, re-pushed the
                // rules and rebuilt the grid for a file that had not moved.
                if (now == was)
                {
                    api.Logger.VerboseDebug("[UniversalDisplayLib] Server: config re-read, no server rule changed.");
                    return;
                }

                PlacementLimitsPacket rules = ServerRulesPacket();

                foreach (IPlayer p in api.World.AllOnlinePlayers)
                {
                    ServerChannel?.SendPacket(rules, p as IServerPlayer);
                }

                // The server's own grid is built from the same config, so it needs
                // the same rebuild - otherwise the two sides swap places and the
                // SERVER is the one on a stale grid.
                int rebuilt = BlockBehaviorDisplaySurfaces.RebuildAll(api);
                if (rebuilt >= 0)
                {
                    api.Logger.Notification(
                        "[UniversalDisplayLib] Placement grid changed; rebuilt {0} block type(s).", rebuilt);
                }
            }

            var c = UniversalDisplayLibConfig.Current;
            api.Logger.Notification(
                "[UniversalDisplayLib] {0}: settings reloaded{1}. Server rules now: HeightLimitVoxels={2} PrecisePlacement={3} AllowItemOverlap={4} RoomSafeOpening={5}",
                api.Side, savedBy == null ? "" : " (saved by " + savedBy + ")",
                c.HeightLimitVoxels, c.PrecisePlacement, c.AllowItemOverlap, c.RoomSafeOpening);
        }

        public override void Start(ICoreAPI api)
        {
            base.Start(api);

            UniversalDisplayLibConfig.Load(api);

            // The materials come from the Materials Kit (src/Materials, installed by ../Tools/Materials Kit/install-kit.py - do
            // not edit those files, rerun the script). Its item models leave out our display-surface markers too.
            Materials.BlockBehaviorMaterials.ExtraIgnoreElements = block => block.GetBehavior<BlockBehaviorDisplaySurfaces>()?.SurfaceElementNames;

            // PICK UP CONFIG CHANGES MADE IN GAME, without a restart. Integrated Mod Manager
            // raises `imm.<modid>` after it rewrites the file; ConfigKit and ConfigLib raise
            // `configkit:<modid>:config-saved` / `configlib:<modid>:config-saved`. All are
            // vanilla event-bus API and an event nobody raises never fires, so this costs
            // nothing when none of them is installed -
            // the same bargain the rest of the mod makes with other mods.
            //
            // Registered on BOTH sides deliberately. Almost every setting here is read fresh
            // at the moment it is used - PlacementHeight.Step is a property, not a cached
            // field - so a reload takes effect immediately on whichever side reloaded, and
            // the SERVER re-tells every client its height limit so no ghost is left clamping
            // against the old number.
            //
            // THE SERVER RELOAD IS DEFERRED A TICK. ConfigKit and ConfigLib forward the client's
            // config-saved to the server BEFORE the server has written its own file, then
            // raises it again after the write. Reloading on the first copy reads the old
            // values. So every copy RE-ARMS the timer rather than being folded into a pending
            // one: the reload runs 50 ms after the LAST event, and the last event is the one
            // raised after the write. Folding them (the first attempt, 2026-09-12) swallowed
            // the post-write copy whenever the write took longer than the timer, and the
            // reload read the old file.
            //
            // WHO SAVED: a forwarded event carries the saving player's UID as "player" in
            // its data tree (ConfigKit and ConfigLib both set it; IMM does not), so the
            // server's log can name them. Without it the line reads as if the server
            // changed something by itself - which is exactly how it was read, 2026-09-12.
            long pendingReload = 0;
            EventBusListenerDelegate onConfigChanged =
                delegate (string eventName, ref EnumHandling handling, IAttribute data)
                {
                    if (api.Side == EnumAppSide.Client) { ApplyConfigReload(api, null); return; }

                    string uid = (data as ITreeAttribute)?.GetString("player");
                    string savedBy = uid == null ? null : (api.World.PlayerByUid(uid)?.PlayerName ?? uid);

                    if (pendingReload != 0) api.Event.UnregisterCallback(pendingReload);
                    pendingReload = api.Event.RegisterCallback(dt => { pendingReload = 0; ApplyConfigReload(api, savedBy); }, 50);
                };
            api.Event.RegisterEventBusListener(onConfigChanged, 0.5, "imm.universaldisplaylib");
            api.Event.RegisterEventBusListener(onConfigChanged, 0.5, "configkit:universaldisplaylib:config-saved");
            api.Event.RegisterEventBusListener(onConfigChanged, 0.5, "configlib:universaldisplaylib:config-saved");

            api.Logger.Notification("[UniversalDisplayLib] Mod loaded successfully");
            api.Logger.Debug("[UniversalDisplayLib] Config values:");
            api.Logger.Debug("  Animation Speed: {0}", UniversalDisplayLibConfig.Current.AnimationSpeed);

            // The pre-psurface classes are deliberately not registered here. They still
            // exist under src/legacy/ but are excluded from the build: Window Storage Lib
            // registers those same class names, and two mods claiming one name collide
            // if a player has both installed.
            api.RegisterBlockClass("BlockUniversalDisplay", typeof(BlockUniversalDisplay));
            api.RegisterBlockEntityClass("BEUniversalDisplay", typeof(BEUniversalDisplay));

            // `DisplaySurfaces`, not `WindowSurfaces`. This is the single most modder-facing
            // string the lib has — it is what someone else types into their own cabinet's
            // blocktype JSON — and a name has to match what is registered here, so they cannot
            // invent `CabinetSurfaces` for themselves. Under a universal lib it must not say
            // "Window".
            api.RegisterBlockBehaviorClass("DisplaySurfaces", typeof(BlockBehaviorDisplaySurfaces));

            // The AutoBoxes Kit (src/autoboxes/, 2026-10-06 - the base the window boxes are rebuilt on):
            // the invisible filler (assets blocktypes/autoboxesfiller.json) in every space a window reaches.
            api.RegisterBlockClass("UDAutoBoxesFiller", typeof(BlockAutoBoxesFiller));
            api.RegisterBlockEntityClass("UDAutoBoxesFiller", typeof(BEAutoBoxesFiller));

            // The window settings the kit does not have (src/reach/): for now, each pane's 90-degree fallback.
            api.RegisterBlockBehaviorClass("UDAutoBoxes", typeof(Reach.BlockBehaviorUDAutoBoxes));

            // ── LEGACY ALIASES — every published content zip depends on these ──────────
            //
            // A registered name is what a blocktype's JSON asks for by string, so renaming
            // one without keeping the old alive is an instant crash for anything already
            // shipped: "Don't know how to instantiate block of class 'BlockWindowDisplay'".
            // Window Display 1.0.1 is live on ModDB right now asking for exactly these three.
            //
            // Registering a second name against the SAME type costs nothing and means an old
            // content zip and a new one both work against this lib. Do not remove them to
            // tidy up — they are the only reason the rename is safe.
            api.RegisterBlockClass("BlockWindowDisplay", typeof(BlockUniversalDisplay));
            api.RegisterBlockEntityClass("BEWindowDisplay", typeof(BEUniversalDisplay));
            api.RegisterBlockBehaviorClass("WindowSurfaces", typeof(BlockBehaviorDisplaySurfaces));
        }

        /// <summary>
        /// The server's rules as they stand right now. ONE builder, used by both the join
        /// push and the reload push, so the two cannot come to disagree.
        /// </summary>
        private static PlacementLimitsPacket ServerRulesPacket() => new PlacementLimitsPacket
        {
            HeightLimitVoxels = Math.Clamp(UniversalDisplayLibConfig.Current?.HeightLimitVoxels ?? 8f, 1f, 16f),
            AllowItemOverlap = UniversalDisplayLibConfig.Current?.AllowItemOverlap ?? false,
            RoomSafeOpening = UniversalDisplayLibConfig.Current?.RoomSafeOpening ?? false,
            PrecisePlacement = UniversalDisplayLibConfig.Current?.PrecisePlacement ?? true,
        };

        public const string ChannelName = "universaldisplaylib-linkedopen";

        public override void StartServerSide(ICoreServerAPI api)
        {
            // Server-side statics survive a world change just as the client ones do — see
            // ForgetAll for what carries over if this is skipped.
            PlacementRotation.ForgetAll();
            PlacementHeight.ForgetAll();
            MeasuredItemSizes.ForgetAll();
            WornShapeMesh.ForgetAll();

            // TELL EACH PLAYER THE LIMIT AS THEY ARRIVE. The step is theirs; the limit is
            // ours, because the height is stored on the block and everyone sees it. Without
            // this the client clamps its ghost with its own config and draws a height the
            // server then pulls back - the mismatch reported 2026-09-10.
            api.Event.PlayerNowPlaying += byPlayer =>
                ServerChannel?.SendPacket(ServerRulesPacket(), byPlayer as IServerPlayer);

            ServerChannel = api.Network.RegisterChannel(ChannelName)
                .RegisterMessageType<PlacementRotationPacket>()
                .RegisterMessageType<RotateStoredItemPacket>()
                .RegisterMessageType<PlacementHeightPacket>()
                .RegisterMessageType<AdjustStoredItemHeightPacket>()
                .RegisterMessageType<PlacementLimitsPacket>()
                .RegisterMessageType<MeasuredItemSizePacket>()
                .SetMessageHandler<MeasuredItemSizePacket>(MeasuredItemSizes.Receive)
                .SetMessageHandler<PlacementRotationPacket>((player, packet) =>
                    PlacementRotation.Remember(player?.PlayerUID, packet?.Deg ?? 0))
                .SetMessageHandler<PlacementHeightPacket>((player, packet) =>
                    PlacementHeight.Remember(player?.PlayerUID, packet?.Voxels ?? 0f))
                .SetMessageHandler<AdjustStoredItemHeightPacket>((player, packet) =>
                {
                    if (packet?.SlotId == null) return;

                    var pos = new BlockPos(packet.X, packet.Y, packet.Z);

                    // Range check for the same reason the rotate packet has one: this is a
                    // client-supplied position, and without it a malformed or hostile packet
                    // could move an item in a window anywhere in the world.
                    if (player?.Entity == null || player.Entity.Pos.AsBlockPos.DistanceTo(pos) > 12)
                    {
                        return;
                    }

                    if (api.World.BlockAccessor.GetBlockEntity(pos) is BEUniversalDisplay be)
                    {
                        be.AdjustStoredItemHeight(packet.SlotId, packet.Down, packet.StepVoxels);
                    }
                })
                .SetMessageHandler<RotateStoredItemPacket>((player, packet) =>
                {
                    if (packet?.SlotId == null) return;

                    var pos = new BlockPos(packet.X, packet.Y, packet.Z);

                    // Range check, because this is a client-supplied position: without it a
                    // malformed or hostile packet could turn an item in a window anywhere in
                    // the world. 12 blocks is comfortably beyond normal reach.
                    if (player?.Entity == null || player.Entity.Pos.AsBlockPos.DistanceTo(pos) > 12)
                    {
                        return;
                    }

                    if (api.World.BlockAccessor.GetBlockEntity(pos) is BEUniversalDisplay be)
                    {
                        be.RotateStoredItem(packet.SlotId, player, packet.Reverse, packet.StepDeg);
                    }
                });
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            // Client-only: guards an IndexOutOfRange crash inside vanilla's own
            // BlockBehaviorDisplay.placingItemsPreview, which our three-surface blocks
            // make reachable. See VanillaDisplayBoundsPatch for the mechanism.
            VanillaDisplayBoundsPatch.Apply(api);

            // THE R KEY IS GONE, removed 2026-07-31 — sprint + mouse wheel replaces it.
            //
            // It was the original way to turn an item before placing it and the wheel was
            // added beside it, so for a while there were two. Free rotation is what settled
            // it: at the default 15° step a key needs 24 presses to get round, where the
            // wheel is one gesture and turns both ways. Keeping a worse duplicate costs a
            // hotkey slot in a list players scroll through and a second path to keep in step.
            //
            // Nothing else referred to it — the interaction help never advertised it, and
            // the lang entry it used (`placement-rotation`) is still read by the info-panel
            // fallback in BEUniversalDisplay.GetBlockInfo, so it stays.

            // Show the footprint outline on demand, whatever PlacementBox is set to. For
            // lining something up precisely without going to the config and back — the
            // setting is the preference, this is "show me while I do this", so it is not
            // persisted and returns to the configured behaviour next session.
            api.Input.RegisterHotKey("windowdisplaybox", "Window Display: show placement outline",
                GlKeys.B, HotkeyType.CharacterControls);
            api.Input.SetHotKeyHandler("windowdisplaybox", _ =>
            {
                BlockBehaviorDisplaySurfaces.ForceFootprintBox =
                    !BlockBehaviorDisplaySurfaces.ForceFootprintBox;

                api.TriggerIngameError(this, "windowdisplaybox",
                    Lang.Get(BlockBehaviorDisplaySurfaces.ForceFootprintBox
                        ? "universaldisplaylib:placement-box-on"
                        : "universaldisplaylib:placement-box-off"));
                return true;
            });

            // Sprint + mouse wheel — the ONLY way to turn an item, held or placed, since the
            // R key went. Free rotation is why: 24 presses to get round at the default 15°,
            // against one gesture that also goes both ways.
            //
            // A NAMED handler, not a lambda, so Dispose can unsubscribe it. An anonymous
            // subscription with no matching -= accumulates if this mod system is ever set up
            // twice in one process, and the symptom would be the wheel turning an item two
            // or three steps per notch after rejoining a world.
            capi = api;
            api.Event.MouseWheelMove += OnMouseWheel;

            // Both named handlers with a matching -= in Dispose, for the reason above.
            api.Event.AfterActiveSlotChanged += OnActiveSlotChanged;

            // Client-side statics survive leaving a world, so they are cleared here rather
            // than left holding the last world's state: a pending placement angle would
            // otherwise carry over into a new world, and the outline override would still be
            // on with nothing having toggled it.
            PlacementRotation.Local = 0;
            PlacementHeight.Local = 0f;

            // A static outlives a world, and the next server is not necessarily this one.
            // Null means "not told yet", which falls back to our own config.
            PlacementHeight.ServerLimit = null;
            ServerRules.Forget();
            BlockBehaviorDisplaySurfaces.ForceFootprintBox = false;
            BEUniversalDisplay.LiveClientInstances.Clear();
            DisplaySoundHandler.ResetShared();

            // The size editor's state, both halves. Carrying either into a new world would
            // mean items silently sized from the last one, and a mode left on that nothing
            // visibly switched on — the same class of fault as the four statics found by the
            // audit. The overrides are session-only by design; this is where the session ends.
            TuningFiles.SizesDirty = false;
            BlockBehaviorDisplaySurfaces.SizeOverrides.Clear();
            MeasuredItemSizes.ForgetAll();
            WornShapeMesh.ForgetAll();

            TuningFiles.ResetSession();

            // The folder and file exist from startup, not from the first tuned item, so they
            // can be found and opened before there is anything in them.
            TuningFiles.EnsureFile(api);

            // The tuner window. A CHAT COMMAND rather than a hotkey: it is opened
            // occasionally and deliberately, where the K cycle is the quick in-passing nudge,
            // and a command costs no key binding in a list players scroll through.
            tuner = new GuiDialogItemTuner(api);
            api.ChatCommands.Create("udedit")
                .WithDescription("Open the Window Display item tuner (box size and transform)")
                .HandleWith(_ =>
                {
                    // Opens with NOTHING targeted rather than refusing, so an empty window
                    // explains itself. Refusing is how the size file looked broken when it
                    // simply had nothing to say yet.
                    tuner.TryOpen();
                    return TextCommandResult.Success();
                });

            RegisterTransformEditor(api);

            // A see-through copy of the held item inside the outline box. The ONLY per-frame
            // renderer in the mod, so it is behind a config switch and unregisters cleanly.
            if (UniversalDisplayLibConfig.Current?.PlacementGhost == true)
            {
                ghostRenderer = new PlacementGhostRenderer(api);
                api.Event.RegisterRenderer(ghostRenderer, EnumRenderStage.Opaque, "windowdisplayghost");
            }

            ClientChannel = api.Network.RegisterChannel(ChannelName)
                .RegisterMessageType<PlacementRotationPacket>()
                // Registered here as well as on the server: a message type has to be known
                // to BOTH channels. Missing on this side, SendPacket goes nowhere and fails
                // silently — which is exactly what "the wheel does nothing on placed items"
                // looked like.
                .RegisterMessageType<RotateStoredItemPacket>()
                .RegisterMessageType<PlacementHeightPacket>()
                .RegisterMessageType<AdjustStoredItemHeightPacket>()
                .RegisterMessageType<PlacementLimitsPacket>()
                // measured item sizes go to the server (MeasuredItemSizes); registered on both sides like every type
                .RegisterMessageType<MeasuredItemSizePacket>()
                // The server's rule, not ours - see PlacementHeight.Limit.
                .SetMessageHandler<PlacementLimitsPacket>(packet =>
                {
                    if (packet == null) return;

                    PlacementHeight.ServerLimit = Math.Clamp(packet.HeightLimitVoxels, 1f, 16f);
                    ServerRules.AllowOverlap = packet.AllowItemOverlap;
                    ServerRules.RoomSafe = packet.RoomSafeOpening;
                    ServerRules.Precise = packet.PrecisePlacement;

                    // The grid was built at block load, before we knew any of this, so the
                    // value alone changes nothing - it has to be rebuilt. Returns -1 and does
                    // no work when it already matches, which is the usual case.
                    int rebuilt = BlockBehaviorDisplaySurfaces.RebuildAll(api);
                    if (rebuilt >= 0)
                    {
                        api.Logger.Notification(
                            "[UniversalDisplayLib] Server uses {0} placement; rebuilt {1} block type(s).",
                            packet.PrecisePlacement ? "half-voxel" : "whole-voxel", rebuilt);
                    }
                })
                // The server owns the value after a placement and pushes it back
                .SetMessageHandler<PlacementRotationPacket>(packet =>
                    PlacementRotation.Local = PlacementRotation.Normalise(packet?.Deg ?? 0))
                .SetMessageHandler<PlacementHeightPacket>(packet =>
                    PlacementHeight.Local = PlacementHeight.Clamp(packet?.Voxels ?? 0f));
        }

        /// <summary>
        /// Wires up the in-game transform editor: its tab, and the two event bus listeners
        /// that bridge the editor's flat attribute name to the real value at
        /// <c>displayable.&lt;category&gt;.transform</c>.
        ///
        /// **ALL OF IT REGISTERED EXACTLY ONCE, HERE.** `IEventAPI` has no way to remove an
        /// event bus listener — `UnregisterCallback` and `UnregisterGameTickListener` exist,
        /// nothing for the bus — so anything registered per block entity accumulates for the
        /// whole session and holds every block entity it captured. That is what this used to
        /// do: `BEUniversalDisplay.Initialize` registered one per window, on every chunk load,
        /// for ever. Found by audit 2026-07-30 after RAM was seen climbing across chunk
        /// reloads.
        ///
        /// The set-handler walks <see cref="BEUniversalDisplay.LiveClientInstances"/>, which is
        /// maintained by Initialize and Cleanup and therefore reflects what is loaded now.
        /// </summary>
        private static void RegisterTransformEditor(ICoreClientAPI api)
        {
            // The editor's tab strip is fixed width and every mod registering a transform
            // competes for it, so the title is short on purpose — long ones overflow and the
            // tab renders invisible until hovered.
            if (!GuiDialogTransformEditor.extraTransforms.Any(
                    x => x.AttributeName == BlockBehaviorDisplaySurfaces.TransformTarget))
            {
                GuiDialogTransformEditor.extraTransforms.Add(new TransformConfig
                {
                    Title = "Universal Display",
                    AttributeName = BlockBehaviorDisplaySurfaces.TransformTarget
                });
            }

            api.Event.RegisterEventBusListener((string eventName, ref EnumHandling handling, IAttribute data) =>
            {
                if (data is not TreeAttribute tree) return;
                if (tree.GetString("target") != BlockBehaviorDisplaySurfaces.TransformTarget) return;

                BlockBehaviorDisplaySurfaces.HandleGetTransform(api, tree, ref handling);
            }, 0.5, "ongettransform");

            api.Event.RegisterEventBusListener((string eventName, ref EnumHandling handling, IAttribute data) =>
            {
                if (data is not TreeAttribute tree) return;
                if (tree.GetString("target") != BlockBehaviorDisplaySurfaces.TransformTarget) return;

                // Written to the collectible ONCE — it is shared by every stack of that
                // item, so doing it per window was always redundant work.
                //
                // The write itself lives beside HandleGetTransform rather than here, so the
                // two cannot disagree about which entry they are editing. They did: the get
                // side resolved the category from the aimed window while this side hardcoded
                // the default, and every shipped marker declares `-windowdisplay` — so the
                // editor read `displayable.windowdisplay` and wrote `displayable.shelf`.
                BlockBehaviorDisplaySurfaces.HandleSetTransform(api, tree);

                // ToArray so a window unloading mid-iteration cannot invalidate the set.
                foreach (BEUniversalDisplay be in BEUniversalDisplay.LiveClientInstances.ToArray())
                {
                    be.ApplyTransformEdit();
                }

                // Read the applied value straight back out and write it to item-transforms.txt,
                // so the numbers never have to be copied off the dialog by hand.
                TuningFiles.RecordTransformEdit(api);
            }, 0.5, "onsettransform");
        }

        /// <summary>
        /// Steps the pending angle and tells the server. <paramref name="direction"/> is
        /// +1 or -1. Sprint + wheel is the only caller since the R key was removed on
        /// 2026-07-31; it stays a separate method because the send must not be forgotten.
        ///
        /// The server places from what it was last told, so it is told now rather than at
        /// placement time — the placement interaction carries no room for extra data. That
        /// is the same reasoning that made this a packet rather than something each side
        /// works out for itself.
        /// </summary>
        private static void RotatePlacement(ICoreClientAPI api, int direction)
        {
            PlacementRotation.Local = PlacementRotation.Normalise(
                PlacementRotation.Local + direction * PlacementRotation.Step);

            ClientChannel?.SendPacket(new PlacementRotationPacket { Deg = PlacementRotation.Local });

            // Say it in words ONLY when the ghost is switched off, because then nothing else
            // shows the angle and the gesture is completely invisible. This lived on the R
            // key until that was removed; the reasoning for it belongs to whatever does the
            // turning, not to the key that used to, so it came here rather than going with it.
            if (UniversalDisplayLibConfig.Current?.PlacementGhost != true)
            {
                api?.TriggerIngameError(api, "windowdisplayrotate",
                    Lang.Get("universaldisplaylib:placement-rotation", PlacementRotation.Local));
            }
        }

        /// <summary>
        /// Handles one wheel notch, and reports whether it was ours to handle — which is
        /// also the gate on consuming the event. Without that gate the mod would swallow
        /// sprint+wheel everywhere in the world, so this answers NO for anything that is
        /// not one of our surfaces.
        /// </summary>
        private static bool TryWheelRotate(ICoreClientAPI api, bool forward)
        {
            // as the window owning the aimed box sees it - an item stacked above its shelf is aimed at through a filler
            BlockSelection sel = BEUniversalDisplay.WindowSelection(api.World.BlockAccessor, api.World.Player?.CurrentBlockSelection);
            if (sel == null) return false;

            Block block = api.World.BlockAccessor.GetBlock(sel.Position);
            if (block?.GetBehavior<BlockBehaviorDisplaySurfaces>() == null) return false;

            DisplaySlotId loc = DisplaySlotId.Decode(sel.SelectionBoxId);
            if (loc == null) return false;   // frame or pane box — leave the wheel alone

            if (loc.IsPlacedItem)
            {
                // A locked chiselled design does not turn. Said here rather than server-side
                // because only the client can raise an ingame error; the REFUSAL still lives
                // in BEUniversalDisplay.RotateStoredItem, which is what a hand-made packet
                // would meet. This is the message, not the guard.
                //
                // Returns TRUE: the notch was ours and was handled, so the wheel must still
                // be consumed or it falls through to the hotbar and the player's held slot
                // changes every time they try.
                if (api.World.BlockAccessor.GetBlockEntity(sel.Position) is BEUniversalDisplay be
                    && be.IsSlotLocked(loc.Encoded))
                {
                    api.TriggerIngameError(api, "displaylocked",
                        Lang.Get("universaldisplaylib:item-locked"));
                    return true;
                }

                // Turning something already on the sill is server state, so it travels as a
                // packet. Sent without waiting for the wrench's usual interaction path,
                // which a wheel notch never enters.
                ClientChannel?.SendPacket(new RotateStoredItemPacket
                {
                    X = sel.Position.X,
                    Y = sel.Position.Y,
                    Z = sel.Position.Z,
                    SlotId = loc.Encoded,
                    Reverse = !forward,

                    // THIS player's step, so turning something already on the shelf moves it
                    // by the same amount as turning the ghost does.
                    StepDeg = PlacementRotation.Step,
                });
                return true;
            }

            // A pad: turn what is about to be placed, which needs something in hand.
            ItemSlot held = api.World.Player?.Entity?.RightHandItemSlot;
            if (held == null || held.Empty) return false;

            RotatePlacement(api, forward ? 1 : -1);
            return true;
        }

        /// <summary>
        /// Steps the pending height nudge and tells the server, for the same reason
        /// <see cref="RotatePlacement"/> does — the server places from what it was last
        /// told, and the placement interaction carries no room for the value.
        /// </summary>
        private static void AdjustPlacementHeight(ICoreClientAPI api, int direction)
        {
            PlacementHeight.Local = PlacementHeight.Clamp(
                PlacementHeight.Local + direction * PlacementHeight.Step);

            ClientChannel?.SendPacket(new PlacementHeightPacket { Voxels = PlacementHeight.Local });

            // Said in words only when the ghost is off, matching the rotation rule: with the
            // ghost on you can see the item move, and with it off nothing else shows the
            // nudge at all.
            if (UniversalDisplayLibConfig.Current?.PlacementGhost != true)
            {
                api?.TriggerIngameError(api, "windowdisplayheight",
                    Lang.Get("universaldisplaylib:placement-height", PlacementHeight.Local));
            }
        }

        /// <summary>
        /// One wheel notch of height, and whether it was ours — the same gate as
        /// <see cref="TryWheelRotate"/>, so sneak+wheel is never swallowed away from one of
        /// our surfaces.
        /// </summary>
        private static bool TryWheelHeight(ICoreClientAPI api, bool up)
        {
            // as the window owning the aimed box sees it - an item stacked above its shelf is aimed at through a filler
            BlockSelection sel = BEUniversalDisplay.WindowSelection(api.World.BlockAccessor, api.World.Player?.CurrentBlockSelection);
            if (sel == null) return false;

            Block block = api.World.BlockAccessor.GetBlock(sel.Position);
            if (block?.GetBehavior<BlockBehaviorDisplaySurfaces>() == null) return false;

            DisplaySlotId loc = DisplaySlotId.Decode(sel.SelectionBoxId);
            if (loc == null) return false;   // frame or pane box — leave the wheel alone

            if (loc.IsPlacedItem)
            {
                // The nudge half of the lock message — see TryWheelRotate for why it is said
                // on this side and enforced on the other.
                if (api.World.BlockAccessor.GetBlockEntity(sel.Position) is BEUniversalDisplay be
                    && be.IsSlotLocked(loc.Encoded))
                {
                    api.TriggerIngameError(api, "displaylocked",
                        Lang.Get("universaldisplaylib:item-locked"));
                    return true;
                }

                // Moving something already on the sill is server state, so it travels as a
                // packet, exactly as turning one does.
                ClientChannel?.SendPacket(new AdjustStoredItemHeightPacket
                {
                    X = sel.Position.X,
                    Y = sel.Position.Y,
                    Z = sel.Position.Z,
                    SlotId = loc.Encoded,
                    Down = !up,

                    // THIS player's step, for the same reason the rotate packet carries one.
                    StepVoxels = PlacementHeight.Step,
                });
                return true;
            }

            // A pad: nudge what is about to be placed, which needs something in hand.
            ItemSlot held = api.World.Player?.Entity?.RightHandItemSlot;
            if (held == null || held.Empty) return false;

            AdjustPlacementHeight(api, up ? 1 : -1);
            return true;
        }

        public override void Dispose()
        {
            // Unsubscribe what was subscribed. Hotkey handlers replace by name, but an
            // event += and a RegisterRenderer both have to be undone by hand.
            if (capi != null)
            {
                // LAST CHANCE TO SAVE TUNED SIZES. Leaving the world with an axis still
                // selected used to drop them on the floor — see TuningFiles.SizesDirty. The chat line
                // will not be seen from here, but the file and the log entry are the point.
                if (TuningFiles.SizesDirty) TuningFiles.Save(capi, announce: false);

                capi.Event.MouseWheelMove -= OnMouseWheel;
                capi.Event.AfterActiveSlotChanged -= OnActiveSlotChanged;

                // TAKE THE RENDERER OUT OF THE LOOP BEFORE DISPOSING IT, and note this runs
                // while capi is still live, which is why it sits inside this block.
                //
                // There was no UnregisterRenderer call here at all — only a Dispose() under
                // a comment claiming the renderer "unregisters cleanly". It does not;
                // `IClientEventAPI.UnregisterRenderer(IRenderer, EnumRenderStage)` is the
                // matching call. RegisterRenderer with nothing undoing it is the same shape
                // as the MouseWheelMove lambda that was fixed on 2026-07-30.
                //
                // The ORDER is the part that could actually misbehave: Dispose() releases the
                // uploaded MeshRefs while the renderer is still registered, so a frame drawn
                // between the two finds an empty cache and uploads fresh GPU meshes during
                // teardown that nothing will ever free.
                if (ghostRenderer != null)
                {
                    capi.Event.UnregisterRenderer(ghostRenderer, EnumRenderStage.Opaque);
                }

                // Closing it also unregisters its retarget tick listener, which OnGuiClosed
                // owns. Leaving it open across a world change would leave that listener
                // running against a dead world.
                if (tuner?.IsOpened() == true) tuner.TryClose();
                tuner = null;

                capi = null;
            }

            // Explicit: the renderer owns uploaded GPU meshes, and a leaked MeshRef is a
            // crash source rather than merely untidy.
            ghostRenderer?.Dispose();
            ghostRenderer = null;

            VanillaDisplayBoundsPatch.Remove();
            base.Dispose();
        }
    }
}