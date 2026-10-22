using System.Collections.Generic;
using System.Linq;
using System;
using System.Collections.Concurrent;
using ProtoBuf;
using UniversalDisplayLib;
using Vintagestory.API.MathTools;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace UniversalDisplayLib.OptionalDisplays
{
    /// <summary>
    /// The place key in its default mode (GroundSettings.PlaceKey "Toggle", Calm 2026-10-16: "not a hold basically a key
    /// to keep it on, so one press on one off"): a rebindable hotkey switches ground placement on and off, so Shift and
    /// Ctrl stay free for raising and turning (and for the game). While on, the ghost shows and right-click puts an item
    /// down this mod's way.
    ///
    /// The SERVER must know too - placing happens there - so each change travels as a packet, and the server keeps the
    /// players who have it on. Off again on every join (the client starts off and says nothing until pressed).
    /// </summary>
    public static class GroundToggle
    {
        public const string HotKeyCode = "displayplacement-place";
        const string Channel = "displayplacement";

        /// <summary>Client: this player's switch.</summary>
        public static bool ClientOn;

        /// <summary>Server: the players (uid) who have it on.</summary>
        static readonly ConcurrentDictionary<string, bool> serverOn = new();

        static IClientNetworkChannel clientChannel;

        [ProtoContract]
        public class PlaceTogglePacket
        {
            [ProtoMember(1)] public bool On;
        }

        /// <summary>"Put the held item down here": the block aimed at (its top), and the point on it.</summary>
        [ProtoContract]
        public class PlaceAtPacket
        {
            [ProtoMember(1)] public int X;
            [ProtoMember(2)] public int Y;
            [ProtoMember(3)] public int Z;
            [ProtoMember(4)] public double HitX;
            [ProtoMember(5)] public double HitY;
            [ProtoMember(6)] public double HitZ;
        }

        /// <summary>Both sides, in Start: the channel and its messages.</summary>
        public static void RegisterChannel(ICoreAPI api)
        {
            api.Network.RegisterChannel(Channel).RegisterMessageType<PlaceTogglePacket>().RegisterMessageType<PlaceAtPacket>();
        }

        public static void StartServer(ICoreServerAPI sapi)
        {
            sapi.Network.GetChannel(Channel).SetMessageHandler<PlaceTogglePacket>((player, p) =>
            {
                if (p.On) serverOn[player.PlayerUID] = true; else serverOn.TryRemove(player.PlayerUID, out _);
            });
            sapi.Network.GetChannel(Channel).SetMessageHandler<PlaceAtPacket>((player, p) => PlaceAt(sapi, player, p));
            sapi.Event.PlayerDisconnect += player => serverOn.TryRemove(player.PlayerUID, out _);
        }

        /// <summary>
        /// Server: the toggle's click. Checked again here - on, in reach, the place key's mode - and then the same placing
        /// as every other route (GroundStorageHook.Place: claims, support, room, then the pile and the item), for anything
        /// the lib can display.
        /// </summary>
        static void PlaceAt(ICoreServerAPI sapi, IServerPlayer player, PlaceAtPacket p)
        {
            IWorldAccessor world = sapi.World;
            if (!GroundSettings.IsEnabled(world) || GroundSettings.Mode(world) != "Toggle" || !IsOn(world, player)) return;
            if (player.Entity == null) return;
            var pos = new BlockPos(p.X, p.Y, p.Z, player.Entity.Pos.Dimension);
            if (player.Entity.Pos.XYZ.DistanceTo(new Vec3d(p.X + 0.5, p.Y + 0.5, p.Z + 0.5)) > 8) return;
            var sel = new BlockSelection { Position = pos, Face = BlockFacing.UP, HitPosition = new Vec3d(p.HitX, p.HitY, p.HitZ) };
            GroundStorageHook.Place(world, player, player.InventoryManager?.ActiveHotbarSlot, sel, anyDisplayable: true);
        }

        public static void StartClient(ICoreClientAPI capi)
        {
            ClientOn = false;
            clientChannel = capi.Network.GetChannel(Channel);
            capi.Input.RegisterHotKey(HotKeyCode, Lang.Get("universaldisplaylib:displayplacement-hotkey-place"), GlKeys.G, HotkeyType.CharacterControls);
            capi.Input.SetHotKeyHandler(HotKeyCode, _ =>
            {
                ClientOn = !ClientOn;
                clientChannel?.SendPacket(new PlaceTogglePacket { On = ClientOn });
                capi.TriggerIngameError(capi, "displayplacement",
                    Lang.Get(ClientOn ? "universaldisplaylib:displayplacement-place-on" : "universaldisplaylib:displayplacement-place-off"));
                return true;
            });

            // THE CLICK, CAUGHT BEFORE THE GAME SEES IT (2026-10-16). Riding the game's own routes failed: block-shaped items
            // go to block placement, a bowl handles its own right-click, and hooking placement crashed the client. While
            // the toggle is on and the aim is a spot a pile would go (the same check the ghost uses), the right-click is
            // this mod's and the server is asked to place. Any other click is left alone - an existing pile, a chiselled
            // block carrying a pile's boxes and everything else answer it themselves.
            capi.Event.MouseDown += e =>
            {
                if (e.Handled || e.Button != EnumMouseButton.Right || !ClientOn) return;
                if (!capi.Input.MouseGrabbed || GroundSettings.Mode(capi.World) != "Toggle" || !GroundSettings.IsEnabled(capi.World)) return;
                IClientPlayer player = capi.World.Player;
                BlockSelection aimed = player?.CurrentBlockSelection;
                ItemSlot held = player?.InventoryManager?.ActiveHotbarSlot;
                if (aimed == null || held?.Itemstack == null) return;
                if (!GroundGhost.Target(capi, held, aimed, true, out _, out _)) return;
                clientChannel?.SendPacket(new PlaceAtPacket
                {
                    X = aimed.Position.X, Y = aimed.Position.Y, Z = aimed.Position.Z,
                    HitX = aimed.HitPosition.X, HitY = aimed.HitPosition.Y, HitZ = aimed.HitPosition.Z
                });
                e.Handled = true;
            };
        }

        /// <summary>Is ground placement on for this player, on this side?</summary>
        public static bool IsOn(IWorldAccessor world, IPlayer player)
        {
            if (world == null || player == null) return false;
            return world.Side == EnumAppSide.Server ? serverOn.ContainsKey(player.PlayerUID) : ClientOn;
        }
    }
}
