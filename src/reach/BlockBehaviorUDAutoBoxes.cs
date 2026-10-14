using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace UniversalDisplayLib.Reach
{
    /// <summary>
    /// "UDAutoBoxes" - the window settings the AutoBoxes Kit does not have, read from the block's JSON. Rebuilt on
    /// the kit 2026-10-06; for now only each pane's FALLBACK: the animation it plays instead when its own open
    /// pose has no room (the External Shutter's leaf folds 90 degrees instead of 180 when something is beside it).
    ///
    /// JSON (already in Window Display): "panes": [ { "pane": 0, "fallback": { "animOpen": "rightopen90", "animClose": "rightclosed90" } } ]
    /// Anything else in it (heightByType, ...) is not read yet.
    /// </summary>
    public class BlockBehaviorUDAutoBoxes : BlockBehavior
    {
        readonly Dictionary<int, (string open, string close)> fallbacks = new();

        public BlockBehaviorUDAutoBoxes(Block block) : base(block) { }

        public override void Initialize(JsonObject properties)
        {
            base.Initialize(properties);
            foreach (JsonObject pane in properties?["panes"]?.AsArray() ?? System.Array.Empty<JsonObject>())
            {
                string open = pane["fallback"]["animOpen"].AsString(null);
                if (string.IsNullOrEmpty(open)) continue;
                fallbacks[pane["pane"].AsInt(-1)] = (open, pane["fallback"]["animClose"].AsString(null));
            }
        }

        /// <summary>Pane <paramref name="pane"/>'s fallback open / close animations, or null for none.</summary>
        public (string open, string close)? FallbackFor(int pane) => fallbacks.TryGetValue(pane, out var f) ? f : null;
    }
}
