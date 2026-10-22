using System;
using System.Collections.Generic;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace UniversalDisplayLib
{
    /// <summary>
    /// SIZE OF A DISPLAYED ITEM (lib 1.4.0, Calm 2026-10-21: "if we can do scale as well as the rotation and the height";
    /// tried in Experiments/Universal Display - Item Scale, brought in without the tilt: "size will be handy for things").
    ///
    /// The size lives ON THE ITEM, as the stack attribute <see cref="AttributeKey"/>, not in a per-slot table like the
    /// turn and the height: every fit and stacking check gets an item's size through
    /// BlockBehaviorDisplaySurfaces.GetDisplayableAttributes, which sees the slot and its item but not the slot's id, so
    /// the one place that applies the scale there covers fitting, stacking, free-stack rests and embedded displays alike.
    /// The model is scaled in BEUniversalDisplay.BuildSlotMatrix (round the item's bottom centre), never in its mesh or
    /// transform, so the measured item sizes and the mesh cache keep describing the item at its own size.
    ///
    /// An item that is not on a display yet (in hand) has no attribute; it takes the size its player has wound up for the
    /// next placement (<see cref="PlacementScale"/>), found from the player inventory the slot belongs to. Taking an item
    /// off (or breaking the block) removes the attribute, so it stacks in an inventory as before.
    /// </summary>
    public static class ItemScale
    {
        public const string AttributeKey = "udScale";

        /// <summary>The size stored on a placed item, 1 when none.</summary>
        public static float Of(ItemStack stack)
        {
            float s = stack?.Attributes?.GetFloat(AttributeKey, 1f) ?? 1f;
            return s > 0f ? s : 1f;
        }

        /// <summary>The size an item in this slot is shown at: its own if placed, else its holder's pending size.</summary>
        public static float For(ItemSlot slot)
        {
            ItemStack stack = slot?.Itemstack;
            if (stack == null) return 1f;
            if (stack.Attributes?.HasAttribute(AttributeKey) == true) return Of(stack);
            if (slot.Inventory is InventoryBasePlayer owned && owned.Api != null)
                return PlacementScale.For(owned.Api, owned.Player);
            return 1f;
        }

        public static void Set(ItemStack stack, float scale)
        {
            if (stack?.Attributes == null) return;
            if (Math.Abs(scale - 1f) < 0.0001f) stack.Attributes.RemoveAttribute(AttributeKey);
            else stack.Attributes.SetFloat(AttributeKey, scale);
        }

        public static void Strip(ItemStack stack) => stack?.Attributes?.RemoveAttribute(AttributeKey);

        /// <summary>A size scaled by <paramref name="scale"/>, or the same one when 1.</summary>
        public static Size3f Scaled(Size3f size, float scale)
            => size == null || Math.Abs(scale - 1f) < 0.0001f ? size
                : new Size3f(size.Width * scale, size.Height * scale, size.Length * scale);
    }

    /// <summary>
    /// The size a player wants their next placed item at - the scale twin of <see cref="PlacementHeight"/>, and the same
    /// shape for the same reasons: the client owns it and sends it whenever it changes, the server remembers the last one
    /// per player, both go back to 1 once an item is placed or the held item changes.
    /// </summary>
    public static class PlacementScale
    {
        /// <summary>Per wheel notch - the player's SizeStepPercent (5% by default), clamped as the rotation step is.</summary>
        public static float Step => Math.Clamp(UniversalDisplayLibConfig.Current?.SizeStepPercent ?? 5, 1, 50) / 100f;

        /// <summary>
        /// How small / big an item may be made. THE SERVER'S NUMBERS WIN, told on join (PlacementLimitsPacket) - the same
        /// reason as PlacementHeight.Limit: the size is stored on the block, so the ghost must not promise a size the
        /// server then pulls back. This side's own config until the server has said.
        /// </summary>
        public static float Min => (ServerMinPercent ?? Math.Clamp(UniversalDisplayLibConfig.Current?.SizeMinPercent ?? 25, 10, 100)) / 100f;
        public static float Max => (ServerMaxPercent ?? Math.Clamp(UniversalDisplayLibConfig.Current?.SizeMaxPercent ?? 200, 100, 400)) / 100f;

        /// <summary>The server's limits once it has told us. Client side only; cleared in StartClientSide.</summary>
        public static int? ServerMinPercent, ServerMaxPercent;

        /// <summary>Client side: this player's own pending size.</summary>
        public static float Local = 1f;

        private static readonly Dictionary<string, float> ByPlayer = new();

        /// <summary>Held inside the limits and rounded to a thousandth, so stepping cannot gather float dust.</summary>
        public static float Clamp(float scale) => (float)Math.Round(Math.Clamp(scale, Min, Max), 3);

        public static void Remember(string playerUid, float scale)
        {
            if (playerUid == null) return;
            float s = Clamp(scale);
            if (Math.Abs(s - 1f) < 0.0001f) ByPlayer.Remove(playerUid);
            else ByPlayer[playerUid] = s;
        }

        /// <summary>The size for a placement: the client's own, or what the server was last told by that player.</summary>
        public static float For(ICoreAPI api, IPlayer player)
        {
            if (api?.Side == EnumAppSide.Client)
            {
                var local = (api as ICoreClientAPI)?.World?.Player;
                return player == null || local == null || player.PlayerUID == local.PlayerUID ? Clamp(Local) : 1f;
            }
            return player != null && ByPlayer.TryGetValue(player.PlayerUID, out float v) ? v : 1f;
        }

        /// <summary>Back to 1 once an item is placed - one wheel-up, one placement, as with the turn and the height.</summary>
        public static void ResetAfterPlacing(ICoreAPI api, IPlayer player)
        {
            if (api?.Side == EnumAppSide.Client)
            {
                Local = 1f;
                return;
            }
            if (player == null) return;
            ByPlayer.Remove(player.PlayerUID);
            if (player is IServerPlayer sp)
                UniversalDisplayLibMod.ServerChannel?.SendPacket(new PlacementScalePacket { Scale = 1f }, sp);
        }

        public static void ForgetAll() => ByPlayer.Clear();
    }

    /// <summary>The size this player wants their next placed item at (the scale twin of PlacementHeightPacket).</summary>
    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class PlacementScalePacket
    {
        public float Scale;
    }

    /// <summary>Size an item ALREADY on a surface up or down a step, from the mouse wheel (twin of AdjustStoredItemHeightPacket).</summary>
    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class AdjustStoredItemScalePacket
    {
        public int X, Y, Z;
        public string SlotId;
        public bool Down;
    }
}
