using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace UniversalDisplayLib
{
    internal static class DisplayItemSounds
    {
        // Some placement routes are server-only, so client-predicted dual-call audio
        // excludes the only listener who did not already play it. Broadcast once instead.
        internal static void Play(IWorldAccessor world, SoundAttributes sound, Vintagestory.API.Common.Entities.Entity entity)
        {
            if (world?.Side == EnumAppSide.Server) world.PlaySoundAt(sound, entity, null);
        }

        // Keep SoundAttributes intact so custom volume, pitch and range survive.
        internal static SoundAttributes Resolve(ItemStack stack, bool pickup)
        {
            SoundAttributes? blockSound = stack?.Block?.Sounds?.Place;
            if (blockSound?.Location != null) return blockSound.Value;
            HeldSounds held = stack?.Collectible?.HeldSounds;
            SoundAttributes? itemSound = pickup ? held?.InvPickup : held?.InvPlace;
            return itemSound?.Location != null ? itemSound.Value : GlobalConstants.DefaultBuildSound;
        }
    }
}
