using System.Collections.Generic;
using System;
using System.Linq;
using UniversalDisplayLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace UniversalDisplayLib.OptionalDisplays
{
    /// <summary>
    /// Ground Display: ground storage the Universal Display Lib way. The block itself is plain JSON
    /// (blocktypes/groundpile.json - the lib's own block and block entity classes with one floor surface); this mod's
    /// code is the hook that puts that block down instead of the game's 2x2 pile (GroundStorageHook), the ghost on
    /// bare ground (GroundGhost), shared placement settings and /grounddisplay revert.
    /// </summary>
    public class DisplayPlacementSystem : ModSystem
    {
        /// <summary>The ground pile block.</summary>
        public static readonly AssetLocation PileCode = new AssetLocation("grounddisplay", "groundpile");

        /// <summary>
        /// The display category of the pile's surface (the fifth segment of its psurface marker). windowdisplay, so
        /// items sit as they do on a window and Window Display Patches' fixes apply on the ground too.
        /// </summary>
        public const string Category = "windowdisplay";

        bool active, groundGuests;
        public static bool IsPile(AssetLocation code) => code != null && code.Path == "groundpile"
            && (code.Domain == "grounddisplay" || code.Domain == "universalgroundstorage");

        public override void Start(ICoreAPI api)
        {
            active = DisplayFeatures.Any(api);
            if (!active) return;
            GroundStorageHook.Apply(api);
            groundGuests = DisplayFeatures.Ground(api);
            if (groundGuests) ChiselGuests.Apply(api);
            GroundToggle.RegisterChannel(api);
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            if (!active) return;
            GroundSettings.LoadServer(api);
            GroundToggle.StartServer(api);

            // Before taking the mod out: turn every pile in the loaded world back into items on the ground, so nothing is
            // lost when the block stops existing. Only LOADED chunks - walk round the base first.
            if (DisplayFeatures.Ground(api)) api.ChatCommands.Create("grounddisplay")
                .WithDescription(Lang.Get("universaldisplaylib:cmd-grounddisplay"))
                .RequiresPrivilege(Privilege.controlserver)
                .BeginSubCommand("revert")
                    .WithDescription(Lang.Get("universaldisplaylib:cmd-grounddisplay-revert"))
                    .HandleWith(_ =>
                    {
                        Block pile = api.World.GetBlock(PileCode);
                        if (pile == null) return TextCommandResult.Error(Lang.Get("universaldisplaylib:cmd-grounddisplay-nopile"));
                        var piles = BEUniversalDisplay.LoadedOnServer.Where(be => IsPile(be.Block?.Code)).Select(be => be.Pos.Copy()).ToList();
                        foreach (var pos in piles) api.World.BlockAccessor.BreakBlock(pos, null);
                        return TextCommandResult.Success(Lang.Get("universaldisplaylib:cmd-grounddisplay-reverted", piles.Count));
                    })
                .EndSubCommand();
            if (DisplayFeatures.Chiseled(api)) ChiselShelfSystem.RegisterCommands(api);
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            if (!active) return;
            GroundGhost.Register();
            GroundToggle.StartClient(api);
        }

        public override void Dispose()
        {
            if (!active) return;
            GroundStorageHook.Remove();
            if (groundGuests) ChiselGuests.Remove();
            GroundGhost.Unregister();
        }
    }
}
