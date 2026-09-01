using Vintagestory.API.MathTools;

namespace UniversalDisplayLib
{
    // Shared by the psurface path. Lifted out of the legacy block entity so that
    // file can be excluded from the build without taking these with it.
    public class FrameBoxGroup
    {
        public string AnimOpen { get; set; }
        public string AnimClose { get; set; }
        /// <summary>
        /// Whether this pane group SEALS THE ROOM when shut, and therefore whether toggling it
        /// swaps the block to its open/closed variant. True by default, because a pane that
        /// opens usually does both.
        ///
        /// False is for a pane that animates without being a hole in the wall — an interior
        /// door, a display flap, a shutter over a solid panel. Those also get the
        /// `toggleSolid` sound rather than the glassy `toggle` one.
        ///
        /// **Renamed from `isWindow` on 2026-08-06** with the mod. The old name said what the
        /// flag was FOR on a window rather than what it DOES, which stopped being useful the
        /// moment the library was for any block.
        /// </summary>
        public bool SealsRoom { get; set; } = true;

        /// <summary>
        /// Legacy JSON alias, and it must stay: `isWindow` is in every published blocktype
        /// using this library, including Window Display 1.0.1. Deserialising sets SealsRoom
        /// through it, so old and new content both work and neither needs to know about the
        /// other.
        /// </summary>
        public bool IsWindow { get => SealsRoom; set => SealsRoom = value; }
        public Cuboidf ClosedFrameBox { get; set; }
        public Cuboidf OpenFrameBox { get; set; }
        public Cuboidf[] StaticFrameBoxes { get; set; }
    }

    public class CollisionBoxGroup
    {
        public Cuboidf ClosedCollisionBox { get; set; }
        public Cuboidf OpenCollisionBox { get; set; }
        public Cuboidf[] StaticCollisionBoxes { get; set; }
    }
}
