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
        /// What the interaction help calls opening and closing this pane - a lang key or plain text: a drawer can say
        /// "Pull out" / "Push in", a lid "Lift" / "Lower". Unset: "Open" / "Close". The help shows the one that applies
        /// now.
        /// </summary>
        public string OpenLabel { get; set; }
        public string CloseLabel { get; set; }

        // -- More for modders, all optional --

        /// <summary>This pane's own sound when it opens / closes (an asset path; a bare path is the block's own
        /// domain). Unset: the block's <c>displaySounds</c>. Follows each player's open/close volume setting.</summary>
        public string OpenSound { get; set; }
        public string CloseSound { get; set; }

        /// <summary>This pane's animation speed, times the player's own setting - a heavy door slower (0.5), a drawer quicker (2). Default 1.</summary>
        public float AnimSpeed { get; set; } = 1f;

        /// <summary>Seconds after it is opened that the pane shuts by itself (0, the default: never). It waits while
        /// something open is in its way, and an open pane found when the world loads gets the same time.</summary>
        public float AutoCloseSeconds { get; set; }

        /// <summary>The block info's words for its state - a lang key or plain text, such as "Pulled out" / "Pushed in".
        /// Unset: "Open" / "Closed".</summary>
        public string OpenState { get; set; }
        public string ClosedState { get; set; }

        /// <summary>Whether strong wind can blow this pane open and bang it shut (WindWindows). Unset: worked out from the
        /// model - a pane that swings out of the wall can, one that slides cannot.</summary>
        public bool? WindBlown { get; set; }
        /// <summary>
        /// Whether this pane group SEALS THE ROOM when shut, and therefore whether toggling it swaps the block to its
        /// open/closed variant. True by default, because a pane that opens usually does both.
        ///
        /// False is for a pane that animates without being a hole in the wall — an interior door, a display flap, a
        /// shutter over a solid panel. Those also get the `toggleSolid` sound rather than the glassy `toggle` one.
        /// </summary>
        public bool SealsRoom { get; set; } = true;

        /// <summary>
        /// Legacy JSON alias, and it must stay: `isWindow` is in every published blocktype using this library.
        /// Deserialising sets SealsRoom through it, so old and new content both work and neither needs to know about
        /// the other.
        /// </summary>
        public bool IsWindow { get => SealsRoom; set => SealsRoom = value; }

        /// <summary>
        /// The pane's box (outline + collision) stays where it is when SHUT, even while it is open - the pane swings
        /// out with no box, as the awning and hopper do. Still measured from the model, no sizes written. The room
        /// check before opening still uses the real swing (HasRoomToOpen).
        /// </summary>
        public bool BoxStaysShut { get; set; }
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
