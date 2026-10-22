using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace UniversalDisplayLib;

// A real wall above a display cannot be replaced by an AutoBoxes filler.
// Carry reaching item boxes on that wall instead, using the existing guest IDs.
public sealed class SidingStackSelection : ModSystem
{
    const string PatchId = "universaldisplaylib.siding-stack-selection";
    static Harmony harmony;
    static Type wallType;
    static int users;
    static ConditionalWeakTable<IBlockAccessor, LastWall> cache = new();
    bool active;
    sealed class LastWall
    {
        public readonly object Gate = new();
        public BlockPos Pos;
        public Cuboidf[] Own, Near, Far, Result;
    }

    public override void Start(ICoreAPI api)
    {
        wallType ??= AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("VSSiding.SidingWallBlock", false)).FirstOrDefault(t => t != null);
        if (wallType == null) return;
        active = true;
        if (users++ > 0) return;
        harmony = new Harmony(PatchId);
        try
        {
            Patch(nameof(Block.GetSelectionBoxes), nameof(Selection), false);
            Patch(nameof(Block.OnBlockInteractStart), nameof(Interact), true);
            Patch(nameof(Block.OnBlockInteractStep), nameof(Step), true);
            Patch(nameof(Block.GetPlacedBlockInfo), nameof(Info), true);
            Patch(nameof(Block.OnBlockBroken), nameof(Broken), true);
            Patch(nameof(Block.DoPartialSelection), nameof(Partial), false);
            api.Logger.Notification("[UniversalDisplayLib] Siding upper-stack selection enabled.");
        }
        catch (Exception e)
        {
            harmony.UnpatchAll(PatchId);
            users--; active = false;
            api.Logger.Warning("[UniversalDisplayLib] Siding upper-stack selection unavailable: {0}", e);
        }
    }

    static void Patch(string method, string handler, bool prefix)
    {
        var target = AccessTools.Method(wallType, method);
        target = target.DeclaringType.GetMethod(target.Name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
            null, target.GetParameters().Select(p => p.ParameterType).ToArray(), null);
        var patch = new HarmonyMethod(typeof(SidingStackSelection), handler);
        harmony.Patch(target, prefix: prefix ? patch : null, postfix: prefix ? null : patch);
    }

    public override void AssetsFinalize(ICoreAPI api)
    {
        if (api is not ICoreClientAPI client || wallType == null) return;
        foreach (Block block in api.World.Blocks)
        {
            if (!wallType.IsInstanceOfType(block)
                || block.GetInterface<ICustomSelectionBoxRender>(api.World, null) != null) continue;
            block.BlockBehaviors = (block.BlockBehaviors ?? Array.Empty<BlockBehavior>())
                .Append(new WallOutline(block, client)).ToArray();
        }
    }

    // Keep selection/interaction boxes present even when their visual outline is off.
    // The owner's renderer applies the live box controls and stacking preview policy.
    internal static bool RenderGuest(IWorldAccessor world, BlockSelection selection, RenderBoxDelegate draw)
    {
        var owner = Owner(world, selection);
        if (owner == null) return false;
        var renderer = world.BlockAccessor.GetBlock(owner.Position)
            .GetInterface<ICustomSelectionBoxRender>(world, owner.Position);
        if (renderer == null) return false;
        int dx = owner.Position.X - selection.Position.X;
        int dy = owner.Position.Y - selection.Position.Y;
        int dz = owner.Position.Z - selection.Position.Z;
        renderer.RenderSelectionBoxes(owner, (box, width, colour) => draw(box.OffsetCopy(dx, dy, dz), width, colour));
        return true;
    }

    sealed class WallOutline : BlockBehavior, ICustomSelectionBoxRender
    {
        static readonly Type decorBoxType = AccessTools.TypeByName("Vintagestory.API.Common.DecorSelectionBox");
        readonly ICoreClientAPI client;
        public WallOutline(Block block, ICoreClientAPI client) : base(block) { this.client = client; }
        public void RenderSelectionBoxes(BlockSelection selection, RenderBoxDelegate draw)
        {
            if (RenderGuest(client.World, selection, draw)) return;
            var boxes = block.GetSelectionBoxes(client.World.BlockAccessor, selection.Position);
            if (boxes == null) return;
            bool partial = block.DoPartialSelection(client.World, selection.Position);
            float width = 1.6f * client.Settings.Float["wireframethickness"];
            var colour = block.GetSelectionColor(client, selection.Position);
            for (int i = 0; i < boxes.Length; i++)
                if ((!partial || i == selection.SelectionBoxIndex) && decorBoxType?.IsInstanceOfType(boxes[i]) != true)
                    draw(boxes[i], width, colour);
        }
    }

    static void Selection(Block __instance, IBlockAccessor blockAccessor, BlockPos pos, ref Cuboidf[] __result)
    {
        if (!wallType.IsInstanceOfType(__instance)) return;
        // Same two-block reach used by the display's neighbouring-owner lookup.
        var near = blockAccessor.GetBlockEntity(pos.DownCopy()) as BEUniversalDisplay;
        var far = blockAccessor.GetBlockEntity(pos.DownCopy(2)) as BEUniversalDisplay;
        Cuboidf[] a = near?.AutoBoxesAt(pos) ?? Array.Empty<Cuboidf>();
        Cuboidf[] b = far?.AutoBoxesAt(pos) ?? Array.Empty<Cuboidf>();
        if (a.Length == 0 && b.Length == 0) return;
        var entry = cache.GetValue(blockAccessor, _ => new LastWall());
        lock (entry.Gate)
        {
            Cuboidf[] own = __result ?? Array.Empty<Cuboidf>();
            if (entry.Pos?.Equals(pos) == true && SameBoxes(entry.Own, own)
                && ReferenceEquals(entry.Near, a) && ReferenceEquals(entry.Far, b))
            { __result = entry.Result; return; }
            entry.Pos = pos.Copy(); entry.Own = own; entry.Near = a; entry.Far = b;
            entry.Result = __result = AppendItems(own, a, b);
        }
    }

    static bool SameBoxes(Cuboidf[] a, Cuboidf[] b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i].GetType() != b[i].GetType() || a[i].X1 != b[i].X1 || a[i].Y1 != b[i].Y1 || a[i].Z1 != b[i].Z1
                || a[i].X2 != b[i].X2 || a[i].Y2 != b[i].Y2 || a[i].Z2 != b[i].Z2
                || (a[i] as CuboidfWithId)?.Id != (b[i] as CuboidfWithId)?.Id) return false;
        return true;
    }

    internal static Cuboidf[] AppendItems(Cuboidf[] own, Cuboidf[] near, Cuboidf[] far)
    {
        var result = new List<Cuboidf>(own);
        Add(near, -1); Add(far, -2);
        return result.Count == own.Length ? own : result.ToArray();
        void Add(Cuboidf[] source, int dy)
        {
            foreach (var box in source)
                if (box is CuboidfWithId id && DisplaySlotId.Decode(id.Id) is { IsPlacedItem: true })
                    result.Add(new CuboidfWithId(box) { Id = BEUniversalDisplay.GuestBoxPrefix + "0," + dy + ",0:" + id.Id });
        }
    }

    static BlockSelection Owner(IWorldAccessor world, BlockSelection selection)
    {
        if (selection?.Position == null || !wallType.IsInstanceOfType(world.BlockAccessor.GetBlock(selection.Position))) return null;
        string id = selection.SelectionBoxId;
        if (id == null)
        {
            var boxes = world.BlockAccessor.GetBlock(selection.Position).GetSelectionBoxes(world.BlockAccessor, selection.Position);
            int i = selection.SelectionBoxIndex;
            if (boxes != null && i >= 0 && i < boxes.Length) id = (boxes[i] as CuboidfWithId)?.Id;
        }
        if (!BEUniversalDisplay.GuestSelection(selection, id, out var forwarded)
            || DisplaySlotId.Decode(forwarded.SelectionBoxId) is not { IsPlacedItem: true }
            || world.BlockAccessor.GetBlockEntity(forwarded.Position) is not BEUniversalDisplay) return null;
        return forwarded;
    }

    static bool Interact(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref bool __result)
    {
        var owner = Owner(world, blockSel);
        if (owner == null) return true;
        __result = world.BlockAccessor.GetBlock(owner.Position).OnBlockInteractStart(world, byPlayer, owner);
        return false;
    }

    static bool Step(IWorldAccessor world, BlockSelection blockSel, ref bool __result)
    {
        if (Owner(world, blockSel) == null) return true;
        __result = false; return false; // do not continue the wall's layer-building action
    }

    static bool Info(Block __instance, IWorldAccessor world, BlockPos pos, IPlayer forPlayer, ref string __result)
    {
        if (!wallType.IsInstanceOfType(__instance) || forPlayer?.CurrentBlockSelection?.Position?.Equals(pos) != true) return true;
        var owner = Owner(world, forPlayer.CurrentBlockSelection);
        if (owner == null) return true;
        __result = world.BlockAccessor.GetBlock(owner.Position).GetPlacedBlockInfo(world, owner.Position, forPlayer);
        return false;
    }

    static void Partial(Block __instance, IWorldAccessor world, BlockPos pos, ref bool __result)
    {
        if (!wallType.IsInstanceOfType(__instance)) return;
        var selection = (world.Api as ICoreClientAPI)?.World.Player?.CurrentBlockSelection;
        if (selection?.Position?.Equals(pos) == true && Owner(world, selection) != null) __result = true;
    }

    static bool Broken(IWorldAccessor world, BlockPos pos, IPlayer byPlayer)
    {
        var selection = byPlayer?.CurrentBlockSelection;
        if (selection?.Position?.Equals(pos) != true) return true;
        var owner = Owner(world, selection);
        if (owner == null) return true;
        if (world.Side == EnumAppSide.Server && world.Claims.TestAccess(byPlayer, owner.Position, EnumBlockAccessFlags.BuildOrBreak) == EnumWorldAccessResponse.Granted)
            world.BlockAccessor.BreakBlock(owner.Position, byPlayer);
        return false;
    }

    public override void Dispose()
    {
        if (active && --users == 0)
        {
            harmony?.UnpatchAll(PatchId); harmony = null; wallType = null;
            cache = new ConditionalWeakTable<IBlockAccessor, LastWall>();
        }
        base.Dispose();
    }
}
