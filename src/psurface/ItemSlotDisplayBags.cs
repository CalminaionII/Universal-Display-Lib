using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace UniversalDisplayLib
{
    /// <summary>
    /// The display slot every Universal Display surface uses: the game's own <see cref="ItemSlotDisplay"/>, which also takes
    /// a bag with things in it (lib 1.3.1, Calm 2026-10-21 - a player asked).
    ///
    /// ItemSlotDisplay is an ItemSlotSurvival, and that refuses any non-empty bag outright ("can hold everything except full
    /// backpacks", vsapi Common/Inventory/ItemSlotSurvival.cs) - the same rule that keeps a full bag out of the hotbar. A
    /// player can still scroll the selection onto a bag slot, so the ghost showed and the placing then failed. For a full
    /// bag these checks are ItemSlot's own instead (storage kind, locks, free space); anything else goes to the game's as
    /// before. The contents live in the bag's own stack attributes, so it comes back off as it went on - as vanilla
    /// ground storage does (its slots are plain ItemSlots with Backpack added).
    /// </summary>
    public class ItemSlotDisplayBags : ItemSlotDisplay
    {
        public ItemSlotDisplayBags(InventoryBase inventory, string category) : base(inventory, category)
        {
            StorageType |= EnumItemStorageFlags.Backpack;
        }

        public override bool CanTakeFrom(ItemSlot sourceSlot, EnumMergePriority priority = EnumMergePriority.AutoMerge)
        {
            if (!IsFullBag(sourceSlot)) return base.CanTakeFrom(sourceSlot, priority);
            if (inventory?.PutLocked == true) return false;
            ItemStack bag = sourceSlot.Itemstack;
            return (bag.Collectible.GetStorageFlags(bag) & StorageType) > 0 && Itemstack == null && GetRemainingSlotSpace(bag) > 0;
        }

        public override bool CanHold(ItemSlot sourceSlot)
        {
            if (!IsFullBag(sourceSlot)) return base.CanHold(sourceSlot);
            if (inventory?.PutLocked == true) return false;
            ItemStack bag = sourceSlot.Itemstack;
            return (bag.Collectible.GetStorageFlags(bag) & StorageType) > 0 && inventory.CanContain(this, sourceSlot);
        }

        static bool IsFullBag(ItemSlot slot) =>
            slot?.Itemstack?.Collectible?.GetCollectibleInterface<IHeldBag>() is IHeldBag bag && !bag.IsEmpty(slot.Itemstack);
    }
}
