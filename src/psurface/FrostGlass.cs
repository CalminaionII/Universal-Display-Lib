using System;
using System.Collections.Generic;
using System.Linq;
using UniversalDisplayLib.Materials;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace UniversalDisplayLib
{
    /// <summary>
    /// FROST AND SNOW ON THE GLASS - THE WEATHER SIDE. Client only, looks only. Every ~10 s (within FrostRange)
    /// UpdateFrost works out what the weather wants on a window with open sky beside it (ExposedToWeather) or an
    /// External Shutter in front: FrostTarget from the outside temperature, SnowTarget building while it snows below 0
    /// C. The GLASS LAYER (GlassLayer.cs) draws them, creeping to the targets frame by frame - nothing here redraws the
    /// window. The pattern (one of three) comes from the window's position - neighbours differ, every player sees the
    /// same. Only the codes the block's "glass" kind fills and its shape really draws get it; a block with none never
    /// frosts. FrostOnWindows and SnowOnWindows (each player's own) turn them off.
    /// </summary>
    public partial class BEUniversalDisplay
    {
        /// <summary>
        /// SNOW ON THE GLASS: while it snows below 0 C on a window the weather reaches (open sky beside it, or behind
        /// an OPEN External Shutter), snow gathers along the bottom and into the corners; it stays while it is cold and
        /// melts above SnowMeltAbove. CREEPING SNOW: the snow picture has PaneArt.SnowSteps steps; each check while it
        /// snows adds a step in light snow, two in medium, three in heavy (full in about three minutes / a minute and a
        /// half / a minute), and the layer creeps after it. Melting takes SnowMeltSteps a check.
        /// </summary>
        const int SnowSteps = PaneArt.SnowSteps;
        const int SnowMeltSteps = 3;
        const float SnowMeltAbove = 2f;
        const string GlassKind = "glass";
        /// <summary>
        /// FROST FOLLOWS THE OUTSIDE TEMPERATURE: below FrostStartsBelow it always forms in from the frame to the WHOLE
        /// pane (FrostTarget = all the steps), and the cold sets how THICK it is (FrostThickTarget, 0 at
        /// FrostStartsBelow to 1 at FullFrostAt)
        /// </summary>
        const float FrostStartsBelow = -1f;
        const float FullFrostAt = -25f;
        /// <summary>
        /// How fast the layer creeps (frost steps a second): colder grows faster; above freezing it thaws, faster the warmer
        /// (GlassLayerRenderer); a little below freezing, frost the weather no longer wants only slowly goes.
        /// </summary>
        internal float FrostGrowRate = FrostCreepPerSecond, FrostMeltRate = FrostCreepPerSecond;
        /// <summary>How thick the frost should be (0 a thin film to 1 solid ice), by the cold; the glass layer eases to it (FrostThick).</summary>
        internal float FrostThickTarget;
        /// <summary>How thick it is drawn now, easing to FrostThickTarget (GlassLayerRenderer); below 0 until first set - then it starts there.</summary>
        internal float FrostThick = -1f;
        /// <summary>A shut External Shutter in front: the frost behind it this much as thick.</summary>
        const float BehindShutterThick = 0.6f;
        /// <summary>WET GLASS AFTER A THAW: the light rain's drops run on the glass until then.</summary>
        internal long WetUntilMs;
        const int WetAfterThawMs = 90_000;
        const int FrostVariants = 3;
        const int FrostCheckMs = 10_000;

        /// <summary>The frost step the weather wants now, 0 clear to PaneArt.FrostSteps (before a shut shutter takes some off).</summary>
        int _frostLevel;
        long _frostCheckAt;
        HashSet<string> _glassCodes;
        int _glassCodesFor = -1;

        /// <summary>This window's frost pattern, 0-2, from its position (the same on every player's game) - worked out once.</summary>
        int FrostVariant => _frostVariant >= 0 ? _frostVariant : (_frostVariant = (int)(PosSeed(11) % FrostVariants));
        int _frostVariant = -1;

        /// <summary>
        /// Frost and snow change only within this many blocks of the player: a window further away keeps what it has
        /// and catches up when the player comes near.
        /// </summary>
        const float FrostRange = 64f;

        /// <summary>
        /// The shape texture codes this block's glass fills - only those its shape really draws (empty: no glass, never
        /// frosts). The External Shutters have a "glass" kind in their materials but no glass in their shape.
        /// </summary>
        HashSet<string> GlassCodes { get { ResolveOverlayCodes(); return _glassCodes; } }

        /// <summary>
        /// SNOW ON THE SHUTTERS: a block with no glass in its shape that OPENS (the shutters), or that asks for it
        /// ("boardSnow"), takes the snow - only the snow - on its wood instead: patches on each board
        /// (PaneArt.BoardSnow), building and melting as on glass.
        /// </summary>
        bool SnowOnly { get { ResolveOverlayCodes(); return _snowOnly; } }

        /// <summary>The codes the frost / snow go on: the glass, or for a shutter the wood (SnowOnly); empty: none.</summary>
        HashSet<string> OverlayCodes { get { ResolveOverlayCodes(); return _overlayCodes; } }

        /// <summary>The codes of the block's wood that its shape draws (not the glass): what goes damp in the rain (GlassLayer).</summary>
        HashSet<string> WoodCodes { get { ResolveOverlayCodes(); return _woodCodes; } }

        HashSet<string> _overlayCodes, _woodCodes;
        bool _snowOnly;

        void ResolveOverlayCodes()
        {
            if (_glassCodesFor == Block.Id && _glassCodes != null) return;
            _glassCodesFor = Block.Id;
            var slots = Block.GetBehavior<BlockBehaviorMaterials>()?.Slots;
            HashSet<string> drawn = ShapeTextureCodes(Api, Block.Shape?.Base);
            _glassCodes = slots != null && slots.TryGetValue(GlassKind, out var glass) ? glass.Keys.Where(drawn.Contains).ToHashSet() : new HashSet<string>();
            // a wooden block that opens (the shutters) - or one whose blocktype asks for it
            _snowOnly = _glassCodes.Count == 0 && (CanOpen || Block.Attributes?["boardSnow"].AsBool(false) == true)
                        && slots != null && slots.ContainsKey("wood");
            _overlayCodes = _glassCodes.Count > 0 ? _glassCodes
                : _snowOnly ? slots["wood"].Keys.Where(drawn.Contains).ToHashSet() : new HashSet<string>();
            if (_overlayCodes.Count == 0) _snowOnly = false;
            _woodCodes = slots != null && slots.TryGetValue("wood", out var wood)
                ? wood.Keys.Where(c => drawn.Contains(c) && !_glassCodes.Contains(c)).ToHashSet() : new HashSet<string>();
        }

        /// <summary>Every texture code a shape's enabled faces use ('#' taken off), read once per shape.</summary>
        static HashSet<string> ShapeTextureCodes(ICoreAPI api, AssetLocation shapeBase)
        {
            if (api == null || shapeBase == null) return new HashSet<string>();
            string key = shapeBase.ToString();
            lock (shapeCodes)
            {
                if (shapeCodes.TryGetValue(key, out var known)) return known;
            }
            var codes = new HashSet<string>();
            Shape shape = api.Assets.TryGet(shapeBase.Clone().WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json"))?.ToObject<Shape>();
            void Walk(ShapeElement[] elements)
            {
                if (elements == null) return;
                foreach (ShapeElement e in elements)
                {
                    // FacesResolved, not the obsolete Faces - on 1.22.7 a shape read from its file has Faces null and
                    // its six faces in FacesResolved (texture codes without the '#', a disabled face null); reading
                    // Faces finds no glass anywhere
                    if (e.FacesResolved != null)
                        foreach (var face in e.FacesResolved)
                            if (face != null && face.Enabled && !string.IsNullOrEmpty(face.Texture)) codes.Add(face.Texture.TrimStart('#'));
                    Walk(e.Children);
                }
            }
            Walk(shape?.Elements);
            lock (shapeCodes) shapeCodes[key] = codes;
            return codes;
        }

        static readonly Dictionary<string, HashSet<string>> shapeCodes = new();

        void UpdateFrost()
        {
            if (capi == null || IsEmbedded || Block == null || OverlayCodes.Count == 0) return;
            long now = capi.World.ElapsedMilliseconds;
            if (now < _frostCheckAt) return;
            _frostCheckAt = now + FrostCheckMs + capi.World.Rand.Next(2000);   // spread out, not every window on one frame
            if (capi.World.Player?.Entity?.Pos.SquareDistanceTo(Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5) > FrostRange * FrostRange) return;

            // A SHUTTER IN FRONT: a window behind an External Shutter frosts too - its shutter faces the sky - but
            // thinner while the shutter is shut
            BEUniversalDisplay shutter = ShutterInFront();
            bool shutterShut = shutter != null && !shutter.paneStates.Any(open => open);
            int want = 0;
            int snow = SnowTarget;   // the snow is on the glass layer (GlassLayer) - glass, and the shutters' wood
            BlockPos outsidePos = null;
            bool exposed = shutter != null ? (shutter.ExposedToWeather(out outsidePos) || true) : ExposedToWeather(out outsidePos);
            int settleSnow = 0;
            // snow, like rain, does not get through leaves overhead (the frost does)
            bool snowReaches = shutter != null || ExposedToWeather(out _, leavesCover: true);
            bool frostOn = UniversalDisplayLibConfig.Current?.FrostOnWindows != false;
            bool snowOn = UniversalDisplayLibConfig.Current?.SnowOnWindows != false;
            if ((frostOn || snowOn) && exposed)
            {
                float temp = capi.World.BlockAccessor.GetClimateAt(Pos, EnumGetClimateMode.NowValues)?.Temperature ?? 10f;
                // first look after loading, with nothing remembered: as much snow as lies on the ground outside
                if (snowOn && snowReaches && !_glassSettled && temp < 0f && !shutterShut) settleSnow = SnowOnGroundNear(outsidePos);
                // below freezing the frost forms over the whole pane (glass only); the cold sets how thick it is
                if (frostOn && !SnowOnly && temp < FrostStartsBelow)
                {
                    want = PaneArt.FrostSteps;
                    FrostThickTarget = Math.Clamp((FrostStartsBelow - temp) / (FrostStartsBelow - FullFrostAt), 0f, 1f)
                                       * (shutterShut ? BehindShutterThick : 1f);
                }
                FrostGrowRate = 0.2f + 0.04f * Math.Max(0f, -temp);              // -20 C: clear to full in about 16 s
                FrostMeltRate = temp > 0f ? 0.3f + 0.15f * temp : 0.08f;         // +5 C: full to clear in about 15 s
                // thawing: the glass is wet for a while (the drops run down it), as long as frost is still going
                if (temp > 0f && FrostShown > 0.5f) WetUntilMs = now + WetAfterThawMs;
                // snow gathers while it snows on glass the weather reaches (not behind a shut shutter), melts when warm
                if (!snowOn) snow = 0;
                else if (temp > SnowMeltAbove) snow = Math.Max(0, snow - SnowMeltSteps);
                else if (temp < 0f && !shutterShut && snowReaches)
                {
                    float fall = WindWeather.PrecipitationAt(capi, Pos);
                    if (fall > 0.05f) snow = Math.Min(SnowSteps, snow + (fall < 0.3f ? 1 : fall < 0.6f ? 2 : 3));
                }
            }
            else
            {
                snow = Math.Max(0, snow - SnowMeltSteps);   // setting off, or out of the weather: it goes a little at a time
                FrostMeltRate = 1f;
            }
            _frostLevel = want;
            int target = want;
            // only the step it should reach - the glass layer creeps to it, frame by frame, without rebuilding the window
            FrostTarget = target;
            // FIRST LOOK AFTER LOADING with nothing remembered (a game restart): straight to what the weather says - no
            // creeping in from clear glass every time the world is joined
            if (!_glassSettled)
            {
                FrostShown = target;
                FrostThick = FrostThickTarget;
                snow = Math.Max(snow, settleSnow);
            }
            SnowTarget = snow;
            if (!_glassSettled) SnowShown = snow;
            // switched off: gone at once, not melted away
            if (!frostOn) FrostShown = 0f;
            if (!snowOn) SnowShown = 0f;
            _glassSettled = true;
        }

        /// <summary>
        /// KEPT THROUGH RELOADS. Frost, snow and rain are drawn on each player's own game and never saved by the
        /// server, so: a window going out of view (chunk unloaded) leaves its glass in GlassMemory, and the same window
        /// coming back takes it up again at once; the memory lasts the session (cleared on joining a world -
        /// WindWeather). After a game restart there is none, and the first look goes straight to what the weather says
        /// instead of creeping in from clear.
        /// </summary>
        sealed class GlassMemory
        {
            public int BlockId, FrostLevel, FrostStep, SnowStep;
            public float FrostThick;
        }

        static readonly Dictionary<BlockPos, GlassMemory> glassMemory = new();
        bool _glassSettled;

        /// <summary>Joining a world: nothing remembered from the last one - glass, and the shapes read (another world may have other mods).</summary>
        internal static void ForgetAllGlass()
        {
            glassMemory.Clear();
            weatherHandOffs.Clear();
            paneShapes.Clear();
            lock (shapeCodes) shapeCodes.Clear();
        }

        void RememberGlass()
        {
            if (capi == null || IsEmbedded || Block == null || Pos == null) return;
            if (_frostLevel == 0 && FrostTarget == 0 && SnowTarget == 0) { glassMemory.Remove(Pos); return; }
            glassMemory[Pos.Copy()] = new GlassMemory
            {
                BlockId = Block.Id, FrostLevel = _frostLevel, FrostStep = FrostTarget, SnowStep = SnowTarget, FrostThick = FrostThick
            };
        }

        /// <summary>
        /// WEATHER KEPT THROUGH A BLOCK SWAP. Opening or shutting can give the window a NEW block entity on this game (the
        /// open and shut blocks are different blocks); a new one starts dry and jumps to what the weather says now - after
        /// the rain had stopped, the rain on the glass and the damp wood went at once instead of drying off. The removed
        /// one leaves what it showed here, and a new one at the same place within WeatherHandOffMs takes it over.
        /// </summary>
        sealed class WeatherHandOff
        {
            public long At;
            public float Rain, Damp, RainClock, Frost, FrostThick, Snow;
            public int FrostTarget, SnowTarget, FrostLevel;
        }

        static readonly Dictionary<BlockPos, WeatherHandOff> weatherHandOffs = new();
        const long WeatherHandOffMs = 3000;

        void HandOffWeather()
        {
            if (capi == null || IsEmbedded || Pos == null) return;
            weatherHandOffs[Pos.Copy()] = new WeatherHandOff
            {
                At = capi.World.ElapsedMilliseconds, Rain = RainShown, Damp = DampShown, RainClock = RainClock,
                Frost = FrostShown, FrostThick = FrostThick, Snow = SnowShown,
                FrostTarget = FrostTarget, SnowTarget = SnowTarget, FrostLevel = _frostLevel
            };
        }

        void TakeWeatherHandOff()
        {
            if (capi == null || IsEmbedded || Pos == null || !weatherHandOffs.Remove(Pos, out WeatherHandOff h)) return;
            if (capi.World.ElapsedMilliseconds - h.At > WeatherHandOffMs) return;
            RainShown = h.Rain; DampShown = h.Damp; RainClock = h.RainClock;
            FrostShown = h.Frost; FrostTarget = h.FrostTarget; _frostLevel = h.FrostLevel;
            FrostThick = h.FrostThick; FrostThickTarget = Math.Max(0f, h.FrostThick);
            SnowShown = h.Snow; SnowTarget = h.SnowTarget;
            _rainSettled = true;   // carry on from here: no jump to the weather's first look
            _glassSettled = true;
        }

        void ForgetGlass()
        {
            if (capi != null && Pos != null) glassMemory.Remove(Pos);
        }

        /// <summary>Client start: this window's glass as it was when it went out of view, if it did this session.</summary>
        void RestoreGlass()
        {
            if (capi == null || IsEmbedded || Block == null || !glassMemory.TryGetValue(Pos, out GlassMemory m) || m.BlockId != Block.Id) return;
            _frostLevel = m.FrostLevel;
            FrostTarget = m.FrostStep;
            FrostShown = m.FrostStep;   // the frost layer as it was
            FrostThickTarget = FrostThick = Math.Max(0f, m.FrostThick);
            SnowTarget = (int)(SnowShown = m.SnowStep);   // the snow layer as it was
            _glassSettled = true;
        }

        /// <summary>The game's own snow lying in the open column at <paramref name="at"/> (the ground outside), as snow steps (3 layers or more = full).</summary>
        int SnowOnGroundNear(BlockPos at)
        {
            if (at == null) return 0;
            IBlockAccessor ba = capi.World.BlockAccessor;
            int y = ba.GetRainMapHeightAt(at);
            float level = 0;
            for (int dy = 0; dy <= 1; dy++)
            {
                var p = new BlockPos(at.X, y + dy, at.Z, at.dimension);
                level = Math.Max(level, ba.GetBlock(p).GetSnowLevel(p));
            }
            return level <= 0 ? 0 : Math.Min(SnowSteps, (int)Math.Ceiling(level * SnowSteps / 3f));
        }

        /// <summary>
        /// The weather side of this window: along the wall's depth, the space with open sky - or the External Shutter
        /// in front. The glass layer goes on this side only. Null: not found.
        /// </summary>
        BlockFacing OutsideFacing()
        {
            IBlockAccessor ba = capi.World.BlockAccessor;
            bool? depthIsX = DepthIsX();
            BEUniversalDisplay shutter = ShutterInFront();
            // OPEN ON BOTH SIDES (a window standing on its own, a wall with no roof): the side the window was placed
            // facing away from. Taking the first open side in the fixed north-east-south-west order put the weather on
            // the inside of every window facing south or west.
            BlockFacing placedOut = PlacedOutside(), found = null;
            foreach (BlockFacing face in BlockFacing.HORIZONTALS)
            {
                if (depthIsX != null && (face.Normali.X != 0) != depthIsX.Value) continue;
                BlockPos at = Pos.AddCopy(face);
                if (shutter != null)
                {
                    if (ba.GetBlockEntity(at) is BEUniversalDisplay or BEAutoBoxesFiller) return face;
                    continue;
                }
                Block there = ba.GetBlock(at, BlockLayersAccess.Solid);
                if (there.Id != 0 && there.Replaceable < 6000 && ba.GetBlockEntity(at) is not BEAutoBoxesFiller) continue;
                if (!OpenToSky(ba, at)) continue;
                if (face == placedOut) return face;
                found ??= face;
            }
            return found;
        }

        /// <summary>
        /// The side facing away from the player who placed the window: the model's front (+Z) is turned toward them on
        /// placing (BlockUniversalDisplay.SnapPlacementAngle), so the outside is its -Z, turned.
        /// </summary>
        BlockFacing PlacedOutside()
        {
            float x = -(float)Math.Sin(MeshAngleRad), z = -(float)Math.Cos(MeshAngleRad);
            return Math.Abs(x) > Math.Abs(z) ? (x > 0 ? BlockFacing.EAST : BlockFacing.WEST) : (z > 0 ? BlockFacing.SOUTH : BlockFacing.NORTH);
        }

        /// <summary>The block's shape for the moving panes (GlassLayer), read once per shape and handed out as a copy.</summary>
        Shape PaneShapeCopy()
        {
            AssetLocation loc = Block.Shape?.Base?.Clone().WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json");
            if (loc == null) return null;
            string key = loc.ToString();
            if (!paneShapes.TryGetValue(key, out Shape shape))
            {
                shape = capi.Assets.TryGet(loc)?.ToObject<Shape>();
                if (shape == null) return null;
                paneShapes[key] = shape;
            }
            return shape.Clone();
        }

        static readonly Dictionary<string, Shape> paneShapes = new();

        /// <summary>
        /// Another display block right in front of or behind this window (along the wall's depth, not beside it along the
        /// wall) that opens and has open sky beside it - an External Shutter. Seen through a filler too (a tall shutter's
        /// upper blocks), but never this window's own.
        /// </summary>
        BEUniversalDisplay ShutterInFront()
        {
            IBlockAccessor ba = capi.World.BlockAccessor;
            bool? depthIsX = DepthIsX();
            foreach (BlockFacing face in BlockFacing.HORIZONTALS)
            {
                if (depthIsX != null && (face.Normali.X != 0) != depthIsX.Value) continue;
                BlockPos at = Pos.AddCopy(face);
                BlockEntity be = ba.GetBlockEntity(at);
                IEnumerable<BlockEntity> candidates = be is BEAutoBoxesFiller filler ? filler.AllOwners().Select(ba.GetBlockEntity) : new[] { be };
                foreach (BlockEntity candidate in candidates)
                    if (candidate is BEUniversalDisplay other && other != this && !other.Pos.Equals(Pos)
                        && other.CanOpen && other.paneStates != null && other.ExposedToWeather(out _)) return other;
            }
            return null;
        }

        bool? _depthIsX;
        int _depthFor = -1;
        float _depthAngle = float.NaN;

        /// <summary>
        /// Which way the wall's depth runs here (true: along X), from the first pane's shut box turned with the block - or,
        /// for a window with no panes (the plain Vanilla Style Pane display), from the way its glass faces; null if unknown.
        /// </summary>
        bool? DepthIsX()
        {
            if (_depthFor == Block.Id && _depthAngle == MeshAngleRad) return _depthIsX;
            _depthFor = Block.Id;
            _depthAngle = MeshAngleRad;
            _depthIsX = null;
            FrameBoxGroup[] groups = SurfaceBehavior?.FrameBoxGroups ?? Array.Empty<FrameBoxGroup>();
            string[] anims = Enumerable.Range(0, groups.Length).Select(i => groups[i].AnimOpen ?? "").ToArray();
            Reach.PaneBoxes measured = groups.Length > 0 ? Reach.PaneBoxes.For(Api, MeasuredBlock()?.Shape, anims) : null;
            Cuboidf shut = measured?.Shut?.FirstOrDefault(b => b != null);
            if (shut != null)
            {
                Cuboidf turned = Turned(shut);
                _depthIsX = turned.X2 - turned.X1 < turned.Z2 - turned.Z1;
            }
            else _depthIsX = GlassDepthIsX();
            return _depthIsX;
        }
    }
}
