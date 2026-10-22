using System.Collections.Generic;
using System.Linq;
using System;
using Vintagestory.API.Common;

namespace UniversalDisplayLib.OptionalDisplays
{
    /// <summary>
    /// ModConfig/displayplacement.json - the SERVER's file decides. It is copied into the world config, which the
    /// game sends to every client as it joins, so a client always previews with the server's key (no packet needed).
    /// </summary>
    public class GroundSettings
    {
        /// <summary>false: every ground pile is the game's own again (piles already made stay until taken or reverted).</summary>
        public bool Enabled = true;

        /// <summary>
        /// How an item is put down this mod's way: "Toggle" (default - the place key, G unless rebound in Controls,
        /// switches it on and off; Shift and Ctrl stay free for raising / turning and for the game), "Shift" (held, the
        /// game's own ground-storage key - replaces the vanilla pile for the simple layouts) or "Ctrl" (held; Shift stays
        /// the game's 2x2 pile).
        /// </summary>
        public string PlaceKey = "Toggle";

        const string File = "displayplacement.json";
        const string EnabledKey = "displayplacement-enabled";
        const string PlaceKeyKey = "displayplacement-placekey";

        /// <summary>Server: read (or write) the file and publish it in the world config once the save is loaded.</summary>
        public static void LoadServer(Vintagestory.API.Server.ICoreServerAPI api)
        {
            GroundSettings s = Read(api);
            api.Event.SaveGameLoaded += () => Publish(api, s);
        }

        static void Publish(ICoreAPI api, GroundSettings s)
        {
            api.World.Config.SetBool(EnabledKey, s.Enabled);
            api.World.Config.SetString(PlaceKeyKey, s.PlaceKey);
            api.Logger.Notification("[Display Placement] {0}, place key {1}", s.Enabled ? "on" : "OFF", s.PlaceKey);
        }

        static GroundSettings Read(ICoreAPI api)
        {
            GroundSettings s = null;
            try { s = api.LoadModConfig<GroundSettings>(File); }
            catch (System.Exception e) { api.Logger.Warning("[Display Placement] {0} could not be read, defaults used: {1}", File, e.Message); }
            if (s == null)
            {
                try { s = api.LoadModConfig<GroundSettings>("universalgroundstorage.json"); }
                catch (System.Exception) { }
                s ??= new GroundSettings();
                api.StoreModConfig(s, File);
            }
            string k = s.PlaceKey?.Trim().ToLowerInvariant();
            s.PlaceKey = k == "ctrl" ? "Ctrl" : k == "shift" ? "Shift" : "Toggle";
            return s;
        }

        /// <summary>Either side: what the server set (defaults until the world config has arrived).</summary>
        public static bool IsEnabled(IWorldAccessor world) => world?.Config?.GetBool(EnabledKey, true) ?? true;

        /// <summary>The server's mode: Toggle, Shift or Ctrl.</summary>
        public static string Mode(IWorldAccessor world) => world?.Config?.GetString(PlaceKeyKey, "Toggle") ?? "Toggle";

        /// <summary>
        /// Would right-click put an item down this mod's way now? Toggle: the player's switch is on. Shift / Ctrl: that
        /// modifier KEY is held (ShiftKey / CtrlKey, not the Sneak / Sprint actions - as vanilla's ground storage reads).
        /// </summary>
        public static bool PlaceActive(IWorldAccessor world, IPlayer player, EntityControls controls)
        {
            switch (Mode(world))
            {
                case "Shift": return controls?.ShiftKey == true;
                case "Ctrl": return controls?.CtrlKey == true;
                default: return GroundToggle.IsOn(world, player);
            }
        }
    }
}
