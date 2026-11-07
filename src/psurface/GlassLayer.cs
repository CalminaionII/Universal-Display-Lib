using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace UniversalDisplayLib
{
    /// <summary>
    /// THE GLASS LAYER: RAIN AND FROST DRAWN OVER THE GLASS. A thin see-through layer just outside each window's outer
    /// glass faces, drawn every frame by GlassLayerRenderer - nothing is rebuilt to change it, so it can move and fade
    /// smoothly where an overlay baked into the glass texture could only step.
    ///
    /// RAIN: faint straight streaks running down, DropFrames pictures flipped every DropFrameMs (PaneArt.Rain),
    /// DropLevels of them by how hard it rains at the window (WindWeather.PrecipitationAt); DropPatterns layouts,
    /// picked block by block (RainVariant), so neighbours and a window's own blocks differ. Only while rain falls on
    /// the glass (RainReaches) within DropRange.
    ///
    /// FROST and SNOW: one picture per pane, made for that pane's own size at the rain's density (PaneArt), edges at
    /// the pane's edges; UpdateFrost (FrostGlass) still works out the step the weather wants (FrostTarget, SnowTarget),
    /// and the layer CREEPS to it a little every frame (FrostShown / SnowShown), fading one picture into the next - no
    /// steps, no window rebuilds. Within FrostRange. A shutter (no glass) gets the snow on its boards instead
    /// (BoardSnow).
    ///
    /// FOLLOWS THE PANES: one piece for the still glass and one per moving pane (each animatedElements root), each
    /// drawn with that pane's own animation transform (the animator's Matrices for the root's joint), so rain and frost
    /// swing open, shut and rock with the glass. On the OUTSIDE of the glass only. Each effect has its player's own
    /// switch: FrostOnWindows, SnowOnWindows, RainOnWindows (the renderer leaves a switched-off one out at once).
    ///
    /// Where the glass is: the block's shape tesselated with a marker texture source (GlassCodes -> one made-up atlas
    /// spot, everything else another); the quads in the glass spot facing across the wall kept - the outermost of each
    /// pane - worked out turned with the block (the weather side is a world side) and stored turned back, so the
    /// renderer can put the pane's transform in between. The rain mesh is cut at block lines (the picture once per
    /// block, so blocks run on into each other); the frost mesh is one quad per pane (the picture once per pane, its
    /// edges at the pane's edges).
    /// </summary>
    public partial class BEUniversalDisplay
    {
        internal const int DropFrames = 32;     // (was 16 - drops land, sit and clear at new spots inside the loop)
        internal const int DropFrameMs = 90;
        internal const int DropLevels = 3;
        internal const int DropPatterns = 3;
        const float DropRange = 32f;
        const int LayerCheckMs = 1000;
        const float DropLift = 0.006f;
        const float FrostLift = 0.003f;
        /// <summary>
        /// A shutter board's snow, just off the wood; 0.012 stands visibly off the boards.
        /// </summary>
        const float BoardLift = 0.004f;
        /// <summary>Frost steps a second the layer creeps until the weather has been looked at (then FrostGrowRate / FrostMeltRate).</summary>
        internal const float FrostCreepPerSecond = 0.25f;
        /// <summary>
        /// How many snow steps a second the layer creeps (16 steps = about 45 s at the fastest) - a little quicker than the
        /// heaviest snow adds them (3 a check, ~11 s), so it keeps up and never lags far behind the weather.
        /// </summary>
        internal const float SnowCreepPerSecond = 0.35f;

        /// <summary>Client: the windows with rain or frost on their glass layer now (GlassLayerRenderer draws them).</summary>
        internal static readonly HashSet<BEUniversalDisplay> Layered = new();

        internal sealed class GlassPiece
        {
            /// <summary>The rain, one mesh per rain pattern (null: no block of this piece uses it) - RainVariant.</summary>
            public readonly MeshRef[] Rain = new MeshRef[DropPatterns];
            /// <summary>
            /// A shutter leaf's rain on its BACK faces, the same way: an open leaf folds back against the wall and turns
            /// its back to the weather, and the wood is not see-through, so the rain must be on the side now facing out.
            /// </summary>
            public readonly MeshRef[] RainBack = new MeshRef[DropPatterns];
            /// <summary>One quad per pane, with its picture size in texels (PaneArt.SizeFor).</summary>
            public readonly List<(MeshRef mesh, int w, int h, float bottom, bool upper)> Panes = new();
            /// <summary>
            /// Per pane (same order): the way its quad faces, unturned - drawn only while that faces the weather after the
            /// joint has moved it; null = always drawn. Only a shutter leaf's boards have it (front AND back faces, so an
            /// open leaf, folded back against the wall, shows snow on the side now facing out).
            /// </summary>
            public readonly List<Vec3f> Facing = new();
            public int Joint;
            /// <summary>
            /// DAMP WOOD: the outside wood faces (and the tops on the outer half), one mesh per damp pattern - each block
            /// its own pattern (DampVariant); for a shutter leaf also its back faces. Null: none.
            /// </summary>
            public readonly MeshRef[] DampFront = new MeshRef[PaneArt.DampPatterns], DampBack = new MeshRef[PaneArt.DampPatterns];
            /// <summary>
            /// The way the piece's front (outside) faces and its backs face, unturned - a moving piece's damp wood and
            /// a shutter board's rain are drawn only while that side is not turned away from the weather. Null: always.
            /// </summary>
            public Vec3f FrontFacing, BackFacing;
        }

        /// <summary>The layer: one piece for the still glass (joint -1) and one per moving pane (its root's joint).</summary>
        internal readonly List<GlassPiece> LayerPieces = new();
        /// <summary>The animator the pieces' joints belong to (a new animator -> rebuilt).</summary>
        internal AnimatorBase LayerAnimator;
        /// <summary>Which rain picture this window starts on, so neighbours differ.</summary>
        internal int DropPhase => (int)(PosSeed(23) % DropFrames);
        /// <summary>How hard it rains on this glass now: 0 not at all, 1 to DropLevels.</summary>
        internal int DropLevel;
        /// <summary>
        /// RAIN BUILDS UP AND DRIES OFF: the rain drawn now, 0 to DropLevels, creeping to DropLevel like the frost -
        /// fading one level into the next - instead of jumping between levels and stopping the moment it stops.
        /// </summary>
        internal float RainShown;
        /// <summary>
        /// Rain levels a second the glass gets wetter (light rain after ~12 s, heavy ~37 s) and dries (heavy to dry ~20
        /// s). The drying was 0.03 (~100 s), but the rain pictures are running streaks, so a slow dry looked like rain
        /// still falling after the weather had stopped.
        /// </summary>
        internal const float RainBuildPerSecond = 0.08f, RainDryPerSecond = 0.15f;
        /// <summary>
        /// THE DROPS RUN FASTER IN HEAVIER RAIN: each window's own clock through the drop frames, ticking at
        /// RainSlowest (nearly dry) up to RainFastest (heavy rain) of the normal DropFrameMs pace, by the creeping
        /// RainShown - so the pace changes as smoothly as the amount does (1 = medium rain).
        /// </summary>
        internal float RainClock;
        internal const float RainSlowest = 0.5f, RainFastest = 1.25f;
        /// <summary>
        /// How soaked the wood is: the step the rain wants (0 dry; light rain half of PaneArt.DampSteps, medium three
        /// quarters, heavy all) and the step drawn now, creeping to it like the frost - soaking in over about 90 s,
        /// drying over about 5 minutes.
        /// </summary>
        internal int DampTarget;
        internal float DampShown;
        internal const float DampSoakPerSecond = 0.18f, DampDryPerSecond = 0.053f;
        /// <summary>
        /// Behind a SHUT External Shutter the wood is out of the rain: what damp it had goes in about half a minute, not
        /// the slow drying in the open.
        /// </summary>
        internal bool DampSheltered;
        internal const float DampShelteredDryPerSecond = 0.5f;
        bool _rainSettled;
        /// <summary>The frost step the weather wants (0 clear to PaneArt.FrostSteps, FrostGlass.UpdateFrost), and the step drawn now, creeping to it.</summary>
        internal int FrostTarget;
        internal float FrostShown;
        /// <summary>The same for the snow on the glass (0 to PaneArt.SnowSteps).</summary>
        internal int SnowTarget;
        internal float SnowShown;
        /// <summary>
        /// The temporal storm grime (PaneArt.StormGrime): the forming step it wants (0, or all PaneArt.GrimeSteps in a
        /// storm - its thickness is GrimeThick, by WindWeather.StormGrimeThickness) and the step drawn now. It builds
        /// in about 30 s and clears back the way it came while the storm wanes.
        /// </summary>
        internal int GrimeTarget;
        internal float GrimeShown;
        /// <summary>How thick the grime should be (the storm's strength) and is drawn now (-1 until first set); eased like the frost's.</summary>
        internal float GrimeThickTarget, GrimeThick = -1f;
        bool _grimeSettled;
        /// <summary>
        /// Grime steps a second: building (16 steps in ~30 s - was 0.15, ~1.8 min) and clearing (~55 s - about the
        /// storm's waning).
        /// </summary>
        internal const float GrimeGrowPerSecond = 0.5f, GrimeClearPerSecond = 0.3f;
        internal int FrostPattern => FrostVariant;
        string _layerKey;
        long _layerCheckAt;

        string _layerShape;
        /// <summary>The model the layer was built from: open and shut are separate blocks with the same model.</summary>
        string LayerShapeKey => Block?.Shape?.Base?.ToString() ?? "";
        /// <summary>The weather side the layer was built for (GlassLayerRenderer: which way a shutter board faces now).</summary>
        internal Vec3i LayerSideNormal = new Vec3i();
        float _layerAngle = float.NaN;

        /// <summary>
        /// Renderer: where the piece for <paramref name="joint"/> is now, in the block's own unturned space - the animator's
        /// transform for that joint (null = identity: the still glass, or no such joint). The one shared buffer is handed
        /// back - used at once by the renderer (render thread only), so nothing is made per piece per frame.
        /// </summary>
        internal float[] LayerJointMatrix(int joint)
        {
            if (joint < 0 || LayerAnimator == null || LayerAnimator != animUtil?.animator) return null;
            // Matrices, as the game draws the panes with: the animated pose only while an animation runs, else the
            // default pose. TransformationMatrices keeps the LAST animated pose after an animation stops, so the frost
            // and snow sat where the pane had been
            float[] m = LayerAnimator.Matrices;
            if (m == null || joint * 16 + 16 > m.Length) return null;
            Array.Copy(m, joint * 16, jointBuffer, 0, 16);
            return jointBuffer;
        }

        static readonly float[] jointBuffer = new float[16];

        /// <summary>
        /// Render thread, every frame: a wrench swap (to the edge version and back) changes the block - the pieces are
        /// its old shape, in the old place - and gets a NEW animator, which the pieces stop following at once
        /// (LayerJointMatrix); both would wait for the next once-a-second check. Rebuilt straight away instead.
        /// </summary>
        internal void CatchUpGlassLayer()
        {
            AnimatorBase current = animUtil?.animator as AnimatorBase;
            if (_layerShape == LayerShapeKey && _layerAngle == MeshAngleRad)
            {
                // OPENING / SHUTTING swaps to the open / shut block - the SAME model, so the pieces still fit: only
                // follow the window's animator, no rebuild
                if (LayerAnimator != current) LayerAnimator = current;
                return;
            }
            _layerCheckAt = 0;
            UpdateGlassLayer();
            // not rebuilt (the new block has no glass, say): drop the old pieces rather than try again every frame
            if (_layerShape != LayerShapeKey) StopGlassLayer();
        }

        /// <summary>Client tick, every second: how hard it rains here, whether there is frost to draw; builds the layer once per block, turn, side and animator.</summary>
        void UpdateGlassLayer()
        {
            if (capi == null || IsEmbedded || Block == null || LayerCodes.Count == 0) return;
            long now = capi.World.ElapsedMilliseconds;
            if (now < _layerCheckAt) return;
            _layerCheckAt = now + LayerCheckMs + capi.World.Rand.Next(250);

            double dist2 = capi.World.Player?.Entity?.Pos.SquareDistanceTo(Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5) ?? double.MaxValue;
            DropLevel = 0;
            DampTarget = 0;
            // rain on the glass - and on a shutter's boards (SnowOnly: its layer is its wood)
            if (dist2 <= DropRange * DropRange && RainReaches())
            {
                float rain = WindWeather.PrecipitationAt(capi, Pos);
                DropLevel = rain < 0.3f ? 1 : rain < 0.6f ? 2 : 3;
                if (SnowOnly || WoodCodes.Count > 0) DampTarget = PaneArt.DampSteps * Math.Min(4, DropLevel + 1) / 4;
            }
            // just thawed: the glass stays wet a while - the light rain's drops (FrostGlass.WetUntilMs) - but not
            // behind a shut External Shutter, the same as the rain
            else if (GlassCodes.Count > 0 && dist2 <= DropRange * DropRange && now < WetUntilMs
                     && UniversalDisplayLibConfig.Current?.RainOnWindows != false && GlassOpenToWeather()) DropLevel = 1;
            if (!_rainSettled) { RainShown = DropLevel; DampShown = DampTarget; _rainSettled = true; }
            if (DampTarget == 0 && DampShown > 0.01f)
            {
                BEUniversalDisplay inFront = ShutterInFront();
                DampSheltered = inFront != null && !inFront.paneStates.Any(open => open);
            }
            else DampSheltered = false;
            // temporal storm grime: glass only (not shutter boards), open to the weather, as the rain
            GrimeTarget = 0;
            if (GlassCodes.Count > 0 && !BoardSnow && dist2 <= FrostRange * FrostRange
                && UniversalDisplayLibConfig.Current?.StormGrimeOnWindows != false && GlassOpenToWeather())
            {
                float thickness = WindWeather.StormGrimeThickness(capi);
                if (thickness > 0f) { GrimeTarget = PaneArt.GrimeSteps; GrimeThickTarget = thickness; }   // the whole window; the storm sets how thick
            }
            if (!_grimeSettled) { GrimeShown = GrimeTarget; GrimeThick = GrimeThickTarget; _grimeSettled = true; }   // first look: straight to the storm
            bool frost = dist2 <= FrostRange * FrostRange
                         && (FrostTarget > 0 || FrostShown > 0.01f || SnowTarget > 0 || SnowShown > 0.01f
                             || GrimeTarget > 0 || GrimeShown > 0.01f);
            BlockFacing side = DropLevel > 0 || RainShown > 0.01f || DampShown > 0.01f || frost ? OutsideFacing() : null;
            if (side == null)
            {
                // nothing to show now: stop drawing, but keep the pieces while the player is near: rain and frost come
                // and go
                if (dist2 > FrostRange * FrostRange) StopGlassLayer();
                else Layered.Remove(this);
                return;
            }

            AnimatorBase animator = animUtil?.animator as AnimatorBase;
            string key = LayerShapeKey + "|" + MeshAngleRad + "|" + side.Code;
            if (key != _layerKey)
            {
                DisposeLayerPieces();
                _layerKey = key;
                _layerShape = LayerShapeKey;
                _layerAngle = MeshAngleRad;
                LayerSideNormal = side.Normali;
                LayerAnimator = animator;
                try { BuildLayerPieces(side); }
                catch (Exception e) { capi.Logger.Debug("[UniversalDisplayLib] glass layer failed at {0}: {1}", Pos, e.Message); }
            }
            else if (LayerAnimator != animator) LayerAnimator = animator;   // same model, a rebuilt animator: just follow it
            if (LayerPieces.Count == 0) Layered.Remove(this);
            else Layered.Add(this);
        }

        void BuildLayerPieces(BlockFacing side)
        {
            // the wood is marked too, for the damp; a shutter's layer faces ARE its wood
            var marker = new GlassMarkerSource(capi, LayerCodes, SnowOnly ? null : WoodCodes);
            _dampSeen.Clear();
            string[] surface = SurfaceBehavior?.SurfaceElementNames ?? Array.Empty<string>();
            string[] animated = Block.Attributes?["animatedElements"].AsArray<string>(null) ?? Array.Empty<string>();

            // the still glass, as BuildBlockMesh draws it
            CompositeShape cshape = Block.Attributes?["shape"].AsObject<CompositeShape>() ?? Block.Shape;
            Shape stillShape = cshape?.Base == null ? null : capi.TesselatorManager.GetCachedShape(cshape.Base)
                ?? Shape.TryGet(capi, cshape.Base.Clone().WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json"));
            if (stillShape != null)
            {
                capi.Tesselator.TesselateShape(new TesselationMetaData
                {
                    TexSource = marker,
                    IgnoreElements = surface.Concat(animated).Concat(Block.Shape?.IgnoreElements ?? Array.Empty<string>()).ToArray()
                }, stillShape, out MeshData still);
                //
                AddLayerPiece(still, side, -1);
            }
            // each moving pane in its rest pose, as its own piece, moved by its joint when drawn
            if (animated.Length > 0 && Animates && LayerAnimator != null)
            {
                Shape tree = PaneShapeCopy();
                foreach (string root in animated)
                {
                    string name = root.EndsWith("/*", StringComparison.Ordinal) ? root[..^2] : root;
                    if (name == "*" || name.Length == 0) continue;
                    ShapeElement element = FindElement(tree?.Elements, name, "", out string path);
                    if (element != null) AddMovingPieces(element, path, marker, surface, side);
                }
            }
            MarkUpperPanes();
        }

        /// <summary>
        /// The pieces for a listed moving part. A part that moves itself (has a joint) is one piece. One that does NOT
        /// - the tall casements list their whole top section ("top-Origin") while only the two leaves inside it move -
        /// is looked into: each child that moves becomes its own piece, deeper as needed.
        /// </summary>
        void AddMovingPieces(ShapeElement element, string path, GlassMarkerSource marker, string[] surface, BlockFacing side)
        {
            int joint = LayerJoint(element);
            if (joint > 0)
            {
                // a moving part INSIDE this one (the bi-fold's second leaf, inside the first) has its own joint: it is
                // left out here and made its own piece below, so its glass follows its own swing
                var inner = new List<(ShapeElement element, string path)>();
                FindInnerMovers(element, path, inner);
                string[] ignore = surface.Concat(inner.SelectMany(m => new[] { m.element.Name, m.path })).ToArray();
                // the full path first ("top-Origin/top-Animation Origin/*"); if that picks out no glass, the name alone
                foreach (string selective in path.Contains('/') ? new[] { path + "/*", element.Name + "/*" } : new[] { path + "/*" })
                {
                    Shape animShape = PaneShapeCopy();
                    if (animShape == null) return;
                    capi.Tesselator.TesselateShape(new TesselationMetaData
                    {
                        TexSource = marker,
                        IgnoreElements = ignore,
                        SelectiveElements = new[] { selective }
                    }, animShape, out MeshData pane);
                    if (AddLayerPiece(pane, side, joint)) break;
                }
                foreach (var (mover, moverPath) in inner) AddMovingPieces(mover, moverPath, marker, surface, side);
                return;
            }
            if (element.Children == null) return;
            foreach (ShapeElement child in element.Children)
                if (child != null) AddMovingPieces(child, path + "/" + child.Name, marker, surface, side);
        }

        int LayerJoint(ShapeElement element) =>
            LayerAnimator.GetPosebyName(element.Name, StringComparison.InvariantCultureIgnoreCase)?.ForElement?.JointId ?? 0;

        /// <summary>The nearest elements under <paramref name="element"/> that move by their own joint.</summary>
        void FindInnerMovers(ShapeElement element, string path, List<(ShapeElement, string)> found)
        {
            if (element.Children == null) return;
            foreach (ShapeElement child in element.Children)
            {
                if (child == null) continue;
                string here = path + "/" + child.Name;
                if (LayerJoint(child) > 0) found.Add((child, here));
                else FindInnerMovers(child, here, found);
            }
        }

        /// <summary>The element called <paramref name="name"/> anywhere in the tree, with its path ("parent/child") for SelectiveElements.</summary>
        static ShapeElement FindElement(ShapeElement[] elements, string name, string prefix, out string path)
        {
            path = null;
            if (elements == null) return null;
            foreach (ShapeElement e in elements)
            {
                if (e == null) continue;
                string here = prefix.Length == 0 ? e.Name : prefix + "/" + e.Name;
                if (string.Equals(e.Name, name, StringComparison.InvariantCultureIgnoreCase)) { path = here; return e; }
                ShapeElement found = FindElement(e.Children, name, here, out path);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// LESS SNOW HIGHER UP: a pane whose bottom sits more than a quarter block above the window's lowest panes is
        /// an upper pane - it gets UpperSnowShare of the snow (GlassLayerRenderer).
        /// </summary>
        void MarkUpperPanes()
        {
            if (SnowOnly) return;   // a shutter's boards all catch the same snow
            float lowest = float.MaxValue;
            foreach (GlassPiece piece in LayerPieces)
                foreach (var pane in piece.Panes) lowest = Math.Min(lowest, pane.bottom);
            foreach (GlassPiece piece in LayerPieces)
                for (int i = 0; i < piece.Panes.Count; i++)
                {
                    var pane = piece.Panes[i];
                    piece.Panes[i] = (pane.mesh, pane.w, pane.h, pane.bottom, pane.bottom > lowest + 0.25f);
                }
        }

        /// <summary>
        /// The codes the layer draws over: the glass - or, for a block with no glass that opens (the shutters,
        /// SnowOnly), its wood, so the snow on a shutter is the same snow as on the windows.
        /// </summary>
        HashSet<string> LayerCodes => GlassCodes.Count > 0 ? GlassCodes : SnowOnly ? OverlayCodes : GlassCodes;

        /// <summary>How much of the window's snow an upper pane gets.</summary>
        internal const float UpperSnowShare = 0.5f;

        /// <summary>A shutter: its snow is drawn a little on each board (PaneArt.BoardSnow), not as a window's drift.</summary>
        internal bool BoardSnow => SnowOnly;

        /// <summary>The layer piece for the glass in <paramref name="source"/> (unturned, as the shape draws it), uploaded - false if it had no glass.</summary>
        bool AddLayerPiece(MeshData source, BlockFacing side, int joint)
        {
            if (source?.xyz == null || source.Uv == null) return false;
            float[] turn = new Matrixf().Translate(0.5f, 0f, 0.5f).RotateY(MeshAngleRad).Translate(-0.5f, 0f, -0.5f).Values;
            float[] unturn = new Matrixf().Translate(0.5f, 0f, 0.5f).RotateY(-MeshAngleRad).Translate(-0.5f, 0f, -0.5f).Values;
            Vec3i n = side.Normali;
            bool depthX = n.X != 0;
            // per pane, the outermost glass face: keyed by where it is along the wall and up it
            var outer = new Dictionary<(int, int, int, int), (float depth, float w1, float w2, float y1, float y2, Vec3f[] corners)>();
            // a shutter LEAF's back faces as well - an open leaf folds back against the wall, turning its back to the
            // weather
            bool backs = SnowOnly && joint >= 0;
            var inner = new Dictionary<(int, int, int, int), (float depth, float w1, float w2, float y1, float y2, Vec3f[] corners)>();
            var c = new Vec3f[4];
            for (int q = 0; q + 3 < source.VerticesCount; q += 4)
            {
                float u = 0, v = 0;
                for (int k = 0; k < 4; k++) { u += source.Uv[(q + k) * 2]; v += source.Uv[(q + k) * 2 + 1]; }
                if (!GlassMarkerSource.IsGlass(u / 4, v / 4)) continue;
                for (int k = 0; k < 4; k++)
                {
                    int i = (q + k) * 3;
                    float[] p = Mat4f.MulWithVec4(turn, new[] { source.xyz[i], source.xyz[i + 1], source.xyz[i + 2], 1f });
                    c[k] = new Vec3f(p[0], p[1], p[2]);
                }
                Vec3f normal = (c[1] - c[0]).Cross(c[2] - c[0]);
                float len = normal.Length();
                if (len < 1e-6f || Math.Abs(normal.X * n.X + normal.Z * n.Z) < 0.7f * len) continue;
                float depth = 0, w1 = float.MaxValue, w2 = float.MinValue, y1 = float.MaxValue, y2 = float.MinValue;
                for (int k = 0; k < 4; k++)
                {
                    depth += (depthX ? c[k].X * n.X : c[k].Z * n.Z) / 4;
                    float w = depthX ? c[k].Z : c[k].X;
                    w1 = Math.Min(w1, w); w2 = Math.Max(w2, w);
                    y1 = Math.Min(y1, c[k].Y); y2 = Math.Max(y2, c[k].Y);
                }
                if (w2 - w1 < 1 / 64f || y2 - y1 < 1 / 64f) continue;
                var keyOf = ((int)Math.Round(w1 * 32), (int)Math.Round(w2 * 32), (int)Math.Round(y1 * 32), (int)Math.Round(y2 * 32));
                var corners = new[] { c[0].Clone(), c[1].Clone(), c[2].Clone(), c[3].Clone() };
                if (!outer.TryGetValue(keyOf, out var had) || depth > had.depth) outer[keyOf] = (depth, w1, w2, y1, y2, corners);
                if (backs && (!inner.TryGetValue(keyOf, out var low) || depth < low.depth)) inner[keyOf] = (depth, w1, w2, y1, y2, corners);
            }
            var piece = new GlassPiece { Joint = joint };
            if (joint >= 0) { piece.FrontFacing = Unturned(unturn, n.X, n.Z); piece.BackFacing = Unturned(unturn, -n.X, -n.Z); }
            bool damp = AddDampWood(piece, source, turn, unturn, n, backs);
            if (outer.Count == 0)
            {
                if (!damp) return false;
                LayerPieces.Add(piece);   // wood only (a window's still frame): it goes damp
                return true;
            }
            // A SHUTTER (SnowOnly): every board and rail face keeps its own quad and gets a BOARD snow picture - a
            // little along its top (PaneArt.BoardSnow, GlassLayerRenderer) - not one picture with a drift over the
            // whole leaf

            // RAIN: cut at block lines, the picture once per block - each block its own pattern and flip (RainVariant)
            var rains = new MeshData[DropPatterns];
            foreach (var (depth, w1, w2, y1, y2, corners) in outer.Values)
            {
                if (SnowOnly)
                {
                    // RUN-OFF ON A SHUTTER: on each board's own corners (the boards may be tilted), cut at block lines
                    AddFaceCells(corners, depthX, false, unturn, n.X * DropLift, 0f, n.Z * DropLift, (wc, yc) =>
                    {
                        int variant = RainVariant(wc, yc, joint);
                        return (rains[variant % DropPatterns] ??= new MeshData(64, 96, false, true, true, true), variant >= DropPatterns);
                    });
                }
                else
                {
                    float plane = (depth + DropLift) * (depthX ? n.X : n.Z);
                    for (float wc = (float)Math.Floor(w1); wc < w2; wc++)
                    for (float yc = (float)Math.Floor(y1); yc < y2; yc++)
                    {
                        float a1 = Math.Max(w1, wc), a2 = Math.Min(w2, wc + 1), b1 = Math.Max(y1, yc), b2 = Math.Min(y2, yc + 1);
                        if (a2 - a1 < 1 / 64f || b2 - b1 < 1 / 64f) continue;
                        int variant = RainVariant((int)wc, (int)yc, joint);
                        var rain = rains[variant % DropPatterns] ??= new MeshData(outer.Count * 16, outer.Count * 24, false, true, true, true);
                        float u1 = a1 - wc, u2 = a2 - wc;
                        if (variant >= DropPatterns) { u1 = 1 - u1; u2 = 1 - u2; }   // flipped left to right (never upside down - it runs down)
                        AddLayerQuad(rain, unturn, depthX, plane, a1, a2, b1, b2, u1, u2, 1 - (b1 - yc), 1 - (b2 - yc));
                    }
                }
                // FROST / SNOW: one quad per pane, its own picture made for its size - on the face's OWN corners, so it
                // lies on a tilted shutter board too
                var quad = new MeshData(4, 6, false, true, true, true);
                float lift = SnowOnly ? BoardLift : FrostLift;
                AddCornerQuad(quad, unturn, depthX, corners, n.X * lift, n.Z * lift, w1, w2, y1, y2, SnowOnly ? BoardFlip(w1, y1) : 0);
                var (pw, ph) = PaneArt.SizeFor(w2 - w1, y2 - y1);
                piece.Panes.Add((capi.Render.UploadMesh(quad), pw, ph, y1, false));
                piece.Facing.Add(backs ? Unturned(unturn, n.X, n.Z) : null);
            }
            var rainBacks = new MeshData[DropPatterns];
            if (backs)
            {
                foreach (var (key, (depth, w1, w2, y1, y2, corners)) in inner)
                {
                    if (outer.TryGetValue(key, out var front) && front.depth - depth < 1 / 64f) continue;   // a single face
                    AddFaceCells(corners, depthX, false, unturn, -n.X * DropLift, 0f, -n.Z * DropLift, (wc, yc) =>
                    {
                        int variant = RainVariant(wc, yc + 7, joint);
                        return (rainBacks[variant % DropPatterns] ??= new MeshData(64, 96, false, true, true, true), variant >= DropPatterns);
                    });
                    var quad = new MeshData(4, 6, false, true, true, true);
                    AddCornerQuad(quad, unturn, depthX, corners, -n.X * BoardLift, -n.Z * BoardLift, w1, w2, y1, y2, BoardFlip(w1, y1 + 7));
                    var (pw, ph) = PaneArt.SizeFor(w2 - w1, y2 - y1);
                    piece.Panes.Add((capi.Render.UploadMesh(quad), pw, ph, y1, false));
                    piece.Facing.Add(Unturned(unturn, -n.X, -n.Z));
                }
            }
            for (int r = 0; r < DropPatterns; r++)
            {
                if (rains[r]?.VerticesCount > 0) piece.Rain[r] = capi.Render.UploadMesh(rains[r]);
                if (rainBacks[r]?.VerticesCount > 0) piece.RainBack[r] = capi.Render.UploadMesh(rainBacks[r]);
            }
            LayerPieces.Add(piece);
            return true;
        }

        /// <summary>
        /// DAMP WOOD: the piece's wood faces the rain reaches - those facing out and the tops (sills, rails), on the
        /// outside half - each laid with the damp picture once per block, BoardLift off the wood; a shutter leaf's backs as well
        /// (folded open, they face the weather). A window's wood is its "wood" codes (WoodCodes); a shutter's is its layer
        /// (marked as its "glass"). False if the piece has none.
        /// </summary>
        /// <summary>
        /// The damp faces already laid this build, over ALL the window's pieces (in their rest pose): a face that turns
        /// up in two pieces (a part tesselated with its parent's piece AND as its own) gets one damp film, not two -
        /// two darken that board twice.
        /// </summary>
        readonly HashSet<(int, int, int, int, int, int)> _dampSeen = new();

        bool AddDampWood(GlassPiece piece, MeshData source, float[] turn, float[] unturn, Vec3i n, bool backs)
        {
            bool depthX = n.X != 0;
            float sign = depthX ? n.X : n.Z;
            var front = new MeshData[PaneArt.DampPatterns];
            var back = new MeshData[PaneArt.DampPatterns];
            var c = new Vec3f[4];
            for (int q = 0; q + 3 < source.VerticesCount; q += 4)
            {
                float u = 0, v = 0;
                for (int k = 0; k < 4; k++) { u += source.Uv[(q + k) * 2]; v += source.Uv[(q + k) * 2 + 1]; }
                u /= 4; v /= 4;
                if (!(SnowOnly ? GlassMarkerSource.IsGlass(u, v) : GlassMarkerSource.IsWood(u, v))) continue;
                for (int k = 0; k < 4; k++)
                {
                    int i = (q + k) * 3;
                    float[] p = Mat4f.MulWithVec4(turn, new[] { source.xyz[i], source.xyz[i + 1], source.xyz[i + 2], 1f });
                    c[k] = new Vec3f(p[0], p[1], p[2]);
                }
                Vec3f normal = (c[1] - c[0]).Cross(c[2] - c[0]);
                float len = normal.Length();
                if (len < 1e-6f) continue;
                float outward = (normal.X * n.X + normal.Z * n.Z) / len;
                bool top = normal.Y / len > 0.7f;
                if (!top && outward < 0.7f && !(backs && outward < -0.7f)) continue;
                bool isBack = !top && outward < 0;
                // a window's wood on the inside half - behind the glass, or the indoor sill - stays dry (a shutter's
                // boards are all outside, close to the wall)
                float depth = (depthX ? c[0].X + c[1].X + c[2].X + c[3].X : c[0].Z + c[1].Z + c[2].Z + c[3].Z) / 4;
                if (!SnowOnly && !isBack && (depth - 0.5f) * sign < -0.05f) continue;
                var key = ((int)Math.Round((c[0].X + c[2].X) * 64), (int)Math.Round((c[0].Y + c[2].Y) * 64), (int)Math.Round((c[0].Z + c[2].Z) * 64),
                           (int)Math.Round((c[1].X + c[3].X) * 64), (int)Math.Round((c[1].Y + c[3].Y) * 64), isBack ? 1 : top ? 2 : 0);
                if (!_dampSeen.Add(key)) continue;
                float lift = isBack ? -BoardLift : BoardLift;
                var corners = new[] { c[0].Clone(), c[1].Clone(), c[2].Clone(), c[3].Clone() };
                MeshData[] into = isBack ? back : front;
                int salt = isBack ? 53 : top ? 97 : 29;
                (MeshData, bool) Cell(int a, int b)
                {
                    int variant = RainVariant(a, b + salt, piece.Joint);   // each block its own damp pattern, flipped or not
                    return (into[variant % PaneArt.DampPatterns] ??= new MeshData(64, 96, false, true, true, true), variant >= PaneArt.DampPatterns);
                }
                if (top) AddFaceCells(corners, depthX, true, unturn, 0f, BoardLift, 0f, Cell);
                else AddFaceCells(corners, depthX, false, unturn, n.X * lift, 0f, n.Z * lift, Cell);
            }
            bool any = false;
            for (int i = 0; i < PaneArt.DampPatterns; i++)
            {
                if (front[i]?.VerticesCount > 0) { piece.DampFront[i] = capi.Render.UploadMesh(front[i]); any = true; }
                if (back[i]?.VerticesCount > 0) { piece.DampBack[i] = capi.Render.UploadMesh(back[i]); any = true; }
            }
            return any;
        }

        /// <summary>
        /// A face (its four corners, turned block space) cut at block lines into quads ON ITS OWN CORNERS - so a tilted
        /// board keeps them on its plane - each with the picture once per block: along the wall and up it, or for a
        /// <paramref name="top"/> face along the wall and out. <paramref name="cell"/> gives each block's mesh and
        /// whether its picture is flipped left to right. Moved (liftX, liftY, liftZ) off the face, stored unturned.
        /// </summary>
        static void AddFaceCells(Vec3f[] corners, bool depthX, bool top, float[] unturn, float liftX, float liftY, float liftZ,
                                 System.Func<int, int, (MeshData mesh, bool flip)> cell)
        {
            float A(Vec3f p) => depthX ? p.Z : p.X;
            float B(Vec3f p) => top ? (depthX ? p.X : p.Z) : p.Y;
            float a1 = float.MaxValue, a2 = float.MinValue, b1 = float.MaxValue, b2 = float.MinValue;
            foreach (Vec3f p in corners)
            {
                a1 = Math.Min(a1, A(p)); a2 = Math.Max(a2, A(p));
                b1 = Math.Min(b1, B(p)); b2 = Math.Max(b2, B(p));
            }
            if (a2 - a1 < 1 / 64f || b2 - b1 < 1 / 64f) return;
            // which corner is which: low-low, high-low, high-high, low-high
            var q = new Vec3f[4];
            foreach (Vec3f p in corners)
            {
                bool hiA = A(p) - a1 > (a2 - a1) / 2, hiB = B(p) - b1 > (b2 - b1) / 2;
                q[hiB ? (hiA ? 2 : 3) : (hiA ? 1 : 0)] = p;
            }
            if (q.Any(p => p == null)) return;   // not a rectangle on these axes
            Vec3f At(float a, float b)
            {
                float s = (a - a1) / (a2 - a1), t = (b - b1) / (b2 - b1);
                Vec3f low = q[0] + (q[1] - q[0]) * s, high = q[3] + (q[2] - q[3]) * s;
                return low + (high - low) * t;
            }
            for (float ac = (float)Math.Floor(a1); ac < a2; ac++)
            for (float bc = (float)Math.Floor(b1); bc < b2; bc++)
            {
                float s1 = Math.Max(a1, ac), s2 = Math.Min(a2, ac + 1), t1 = Math.Max(b1, bc), t2 = Math.Min(b2, bc + 1);
                if (s2 - s1 < 1 / 64f || t2 - t1 < 1 / 64f) continue;
                var (mesh, flip) = cell((int)ac, (int)bc);
                float u1 = s1 - ac, u2 = s2 - ac;
                if (flip) { u1 = 1 - u1; u2 = 1 - u2; }
                int start = mesh.VerticesCount;
                foreach (var (a, b, u, v) in new[] { (s1, t1, u1, 1 - (t1 - bc)), (s2, t1, u2, 1 - (t1 - bc)), (s2, t2, u2, 1 - (t2 - bc)), (s1, t2, u1, 1 - (t2 - bc)) })
                {
                    Vec3f p = At(a, b);
                    float[] r = Mat4f.MulWithVec4(unturn, new[] { p.X + liftX, p.Y + liftY, p.Z + liftZ, 1f });
                    mesh.AddVertexWithFlags(r[0], r[1], r[2], u, v, ColorUtil.WhiteArgb, 0);
                }
                mesh.AddIndices(start, start + 1, start + 2, start, start + 2, start + 3);
            }
        }

        /// <summary>
        /// A shutter board's picture flipped one of four ways, picked from where the board is - boards of one size share a
        /// spot picture, and without this every board showed the same spots (bit 1 left-right, bit 2 upside down).
        /// </summary>
        static int BoardFlip(float w1, float y1)
        {
            uint hash = (uint)((int)Math.Round(w1 * 32) * 73856093) ^ (uint)((int)Math.Round(y1 * 32) * 19349663);
            return (int)((hash >> 7) & 3);
        }

        /// <summary>
        /// RAIN VARIES BLOCK BY BLOCK, or a tall window would show the same streaks stacked up, falling in step. Each
        /// block of each piece (the still glass and every moving pane apart) takes one of six looks - a pattern of
        /// DropPatterns, flipped left to right or not - from the window's position, the block's place in the window and
        /// the pane's joint; the renderer runs each pattern at its own point in the drop loop. The same on every
        /// player's game.
        /// </summary>
        int RainVariant(int alongWall, int up, int joint)
        {
            uint h = PosSeed(41) ^ (uint)alongWall * 73856093u ^ (uint)up * 19349663u ^ (uint)(joint + 2) * 83492791u;
            h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
            return (int)(h % (uint)(DropPatterns * 2));
        }

        /// <summary>A direction across the wall (turned block space), turned back into the piece's own space.</summary>
        static Vec3f Unturned(float[] unturn, float x, float z)
        {
            float[] d = Mat4f.MulWithVec4(unturn, new[] { x, 0f, z, 0f });
            return new Vec3f(d[0], d[1], d[2]);
        }

        /// <summary>
        /// A quad on a face's own four corners (turned block space), moved (liftX, liftZ) off it, its picture spread over the
        /// face's extent along the wall (w1..w2) and up it (y1..y2, top of the picture at the top), stored unturned.
        /// </summary>
        static void AddCornerQuad(MeshData layer, float[] unturn, bool depthX, Vec3f[] corners, float liftX, float liftZ,
                                  float w1, float w2, float y1, float y2, int flip = 0)
        {
            int start = layer.VerticesCount;
            foreach (Vec3f corner in corners)
            {
                float w = depthX ? corner.Z : corner.X;
                float u = (w - w1) / Math.Max(1e-6f, w2 - w1);
                float v = 1 - (corner.Y - y1) / Math.Max(1e-6f, y2 - y1);
                if ((flip & 1) != 0) u = 1 - u;
                if ((flip & 2) != 0) v = 1 - v;
                float[] p = Mat4f.MulWithVec4(unturn, new[] { corner.X + liftX, corner.Y, corner.Z + liftZ, 1f });
                layer.AddVertexWithFlags(p[0], p[1], p[2], u, v, ColorUtil.WhiteArgb, 0);
            }
            layer.AddIndices(start, start + 1, start + 2, start, start + 2, start + 3);
        }

        /// <summary>A quad from (w1, y1) to (w2, y2) on the plane, its picture from (u1, v1) to (u2, v2), stored unturned.</summary>
        static void AddLayerQuad(MeshData layer, float[] unturn, bool depthX, float plane,
                                 float w1, float w2, float y1, float y2, float u1, float u2, float v1, float v2)
        {
            int start = layer.VerticesCount;
            AddLayerVertex(layer, unturn, depthX, plane, w1, y1, u1, v1);
            AddLayerVertex(layer, unturn, depthX, plane, w2, y1, u2, v1);
            AddLayerVertex(layer, unturn, depthX, plane, w2, y2, u2, v2);
            AddLayerVertex(layer, unturn, depthX, plane, w1, y2, u1, v2);
            layer.AddIndices(start, start + 1, start + 2, start, start + 2, start + 3);
        }

        static void AddLayerVertex(MeshData layer, float[] unturn, bool depthX, float plane, float w, float y, float u, float v)
        {
            float[] p = Mat4f.MulWithVec4(unturn, depthX ? new[] { plane, y, w, 1f } : new[] { w, y, plane, 1f });
            layer.AddVertexWithFlags(p[0], p[1], p[2], u, v, ColorUtil.WhiteArgb, 0);
        }

        void DisposeLayerPieces()
        {
            foreach (GlassPiece piece in LayerPieces)
            {
                foreach (MeshRef rain in piece.Rain) rain?.Dispose();
                foreach (MeshRef rain in piece.RainBack) rain?.Dispose();
                foreach (MeshRef damp in piece.DampFront) damp?.Dispose();
                foreach (MeshRef damp in piece.DampBack) damp?.Dispose();
                foreach (var pane in piece.Panes) pane.mesh?.Dispose();
            }
            LayerPieces.Clear();
            LayerAnimator = null;
        }

        /// <summary>Takes the layer off (dry and frost-free, too far, unloaded); the meshes are freed.</summary>
        void StopGlassLayer()
        {
            Layered.Remove(this);
            DisposeLayerPieces();
            _layerKey = null;
        }

        /// <summary>
        /// A texture source that puts every glass code at one made-up atlas spot and everything else at another, so the
        /// tesselated mesh says which of its faces are glass (BuildLayerPieces). Never drawn.
        /// </summary>
        /// <summary>
        /// The wall's depth from the glass itself (true: along X): the way the shape's glass faces point, turned with
        /// the block. For a window with no panes to measure - without it the layer could take a side ALONG the wall for
        /// the outside, find no glass facing it and draw nothing. Client only; null if there is no glass. A block with
        /// no glass that still takes the layer (the plank Vanilla Style Pane, "boardSnow") is measured by the wood the
        /// snow goes on (LayerCodes).
        /// </summary>
        bool? GlassDepthIsX()
        {
            if (capi == null || LayerCodes.Count == 0) return null;
            Shape shape = PaneShapeCopy();
            if (shape == null) return null;
            capi.Tesselator.TesselateShape(new TesselationMetaData
            {
                TexSource = new GlassMarkerSource(capi, LayerCodes),
                IgnoreElements = (SurfaceBehavior?.SurfaceElementNames ?? Array.Empty<string>())
                    .Concat(Block.Shape?.IgnoreElements ?? Array.Empty<string>()).ToArray()
            }, shape, out MeshData mesh);
            if (mesh?.xyz == null || mesh.Uv == null) return null;
            float[] turn = new Matrixf().Translate(0.5f, 0f, 0.5f).RotateY(MeshAngleRad).Translate(-0.5f, 0f, -0.5f).Values;
            var c = new Vec3f[3];
            double x = 0, z = 0;
            for (int q = 0; q + 3 < mesh.VerticesCount; q += 4)
            {
                float u = 0, v = 0;
                for (int k = 0; k < 4; k++) { u += mesh.Uv[(q + k) * 2]; v += mesh.Uv[(q + k) * 2 + 1]; }
                if (!GlassMarkerSource.IsGlass(u / 4, v / 4)) continue;
                for (int k = 0; k < 3; k++)
                {
                    int i = (q + k) * 3;
                    float[] p = Mat4f.MulWithVec4(turn, new[] { mesh.xyz[i], mesh.xyz[i + 1], mesh.xyz[i + 2], 1f });
                    c[k] = new Vec3f(p[0], p[1], p[2]);
                }
                Vec3f normal = (c[1] - c[0]).Cross(c[2] - c[0]);   // length = the face's size, so big faces count most
                x += Math.Abs(normal.X);
                z += Math.Abs(normal.Z);
            }
            return x == 0 && z == 0 ? null : x > z;
        }

        sealed class GlassMarkerSource : ITexPositionSource
        {
            static readonly TextureAtlasPosition Glass = new() { x1 = 0.25f, y1 = 0.25f, x2 = 0.5f, y2 = 0.5f };
            static readonly TextureAtlasPosition Other = new() { x1 = 0.625f, y1 = 0.625f, x2 = 0.875f, y2 = 0.875f };
            static readonly TextureAtlasPosition Wood = new() { x1 = 0.0625f, y1 = 0.0625f, x2 = 0.1875f, y2 = 0.1875f };
            readonly ICoreClientAPI capi;
            readonly HashSet<string> glass, wood;

            public GlassMarkerSource(ICoreClientAPI capi, HashSet<string> glass, HashSet<string> wood = null)
            {
                this.capi = capi; this.glass = glass; this.wood = wood;
            }

            public static bool IsGlass(float u, float v) => u >= Glass.x1 && u <= Glass.x2 && v >= Glass.y1 && v <= Glass.y2;
            public static bool IsWood(float u, float v) => u >= Wood.x1 && u <= Wood.x2 && v >= Wood.y1 && v <= Wood.y2;

            public TextureAtlasPosition this[string textureCode] =>
                glass.Contains(textureCode) ? Glass : wood != null && wood.Contains(textureCode) ? Wood : Other;
            public Size2i AtlasSize => capi.BlockTextureAtlas.Size;
        }
    }

    /// <summary>Which made-in-code picture a pane draws (GlassLayerRenderer.PaneTexture).</summary>
    internal enum PaneKind { Frost, Grime, Snow }

    /// <summary>
    /// Draws the glass layers (GlassLayer) after the see-through pass, so they lie over the glass, blended, writing no depth
    /// (whatever is behind still shows), both sides (rain and frost on a window show from indoors too). The standard shader
    /// is set up ONCE a frame for all of them; each window then only gets its own light and model matrix. Frost first, the
    /// rain over it. Any failure turns it off for the session with one warning.
    /// </summary>
    public sealed class GlassLayerRenderer : IRenderer
    {
        public double RenderOrder => 0.5;
        public int RenderRange => 64;

        const int FrostPatterns = 3;
        /// <summary>
        /// EACH PANE ITS OWN PATTERN: the pictures are made per pane SIZE, so a window's same-sized panes (the
        /// casement's two leaves) would all show one picture. Pane n of a window draws pattern (window pattern x 2 + n)
        /// of PanePatterns, so the panes of one window differ (up to six) and neighbouring windows still start on
        /// different ones. Same for every player (the panes come in the same order from the same shape). A shutter's
        /// boards keep the window's pattern - each board is already flipped.
        /// </summary>
        const int PanePatterns = 6;
        // how strongly the rain and the frost show over the glass: 1 = as drawn; snow stays as drawn
        const float RainOpacity = 0.3f;      // ("Lets try the rain at 70%")
        const float FrostOpacity = 1f;       // ("see what it is like with like the least transparentness"; 0.5 looked good)

        readonly ICoreClientAPI capi;
        readonly List<BEUniversalDisplay> frameList = new();
        readonly Matrixf model = new();
        readonly Vec4f tint = new(1f, 1f, 1f, 1f);
        bool gameTexturesRead;
        /// <summary>The rain frames made so far.</summary>
        readonly LoadedTexture[,,] rainFrames = new LoadedTexture[PaneArt.RainLevels, PaneArt.RainPatterns, BEUniversalDisplay.DropFrames];
        /// <summary>The damp wood pictures made so far.</summary>
        readonly LoadedTexture[,] dampFrames = new LoadedTexture[PaneArt.DampSteps, PaneArt.DampPatterns];
        /// <summary>How dark the damp shows over the wood: 1 = as drawn.</summary>
        const float DampOpacity = 1f;
        /// <summary>The frost / grime / snow pictures made so far: (w, h, kind, step, pattern, board, frost thickness, Christmas) -> texture.</summary>
        readonly Dictionary<(int, int, PaneKind, int, int, bool, int, bool), LoadedTexture> paneTextures = new();
        /// <summary>How fast the frost thickens or thins as the temperature changes: thinnest to solid ice in about 50 s.</summary>
        const float FrostThickPerSecond = 0.02f;
        bool failed;

        public GlassLayerRenderer(ICoreClientAPI capi) { this.capi = capi; FarthestFirst = CompareFarthestFirst; }

        Vec3d sortCam;
        readonly Comparison<BEUniversalDisplay> FarthestFirst;   // made once - no new delegate each frame

        int CompareFarthestFirst(BEUniversalDisplay a, BEUniversalDisplay b) => CamDist2(b).CompareTo(CamDist2(a));

        double CamDist2(BEUniversalDisplay be)
        {
            double dx = be.Pos.X + 0.5 - sortCam.X, dy = be.Pos.Y + 0.5 - sortCam.Y, dz = be.Pos.Z + 0.5 - sortCam.Z;
            return dx * dx + dy * dy + dz * dz;
        }

        void LoadTextures()
        {
            gameTexturesRead = true;
            // the frost, ice, rust, grime, mold and snow colours come from the game's own textures
            PaneArt.LoadGameTextures(path => capi.Assets.TryGet(new AssetLocation("game", path))?.Data);
        }

        /// <summary>One rain frame, made and uploaded the first time it is drawn.</summary>
        int RainTexture(int level, int pattern, int frame)
        {
            LoadedTexture texture = rainFrames[level, pattern, frame];
            if (texture == null)
                rainFrames[level, pattern, frame] = texture = Upload(PaneArt.Rain(level, pattern, frame), PaneArt.RainSize, PaneArt.RainSize);
            return texture.TextureId;
        }

        /// <summary>A damp wood picture, made and uploaded the first time it is drawn (step from 1).</summary>
        int DampTexture(int step, int pattern)
        {
            LoadedTexture texture = dampFrames[step - 1, pattern];
            if (texture == null)
                dampFrames[step - 1, pattern] = texture = Upload(PaneArt.Damp(step, pattern), PaneArt.DampSize, PaneArt.DampSize);
            return texture.TextureId;
        }

        /// <summary>
        /// A picture made in code, uploaded the way the game loads a texture FILE (PNG -> BitmapCreateFromPng ->
        /// LoadTexture). The raw LoadTextureFromBgra upload draws every pane solid black.
        /// </summary>
        LoadedTexture Upload(int[] pixels, int w, int h)
        {
            var texture = new LoadedTexture(capi);
            using (BitmapExternal bmp = capi.Render.BitmapCreateFromPng(PaneArt.ToPng(pixels, w, h)))
                capi.Render.LoadTexture(bmp, ref texture, false, 0, false);
            return texture;
        }

        /// <summary>
        /// The frost or snow picture for a w x h pane, made and uploaded the first time it is needed.
        /// </summary>
        int PaneTexture(int w, int h, PaneKind kind, int step, int pattern, bool board = false, int thick = 0)
        {
            bool christmas = kind == PaneKind.Frost && Season.ChristmasNow;   // the season's flakes are their own pictures
            var key = (w, h, kind, step, pattern, board, thick, christmas);
            if (paneTextures.TryGetValue(key, out LoadedTexture known)) return known.TextureId;
            int pw = w, ph = h;   // the Christmas flakes are drawn finer: their picture is bigger, on the same quad
            int[] pixels = christmas ? PaneArt.ChristmasFrost(w, h, step, pattern, thick, out pw, out ph) : kind switch
            {
                PaneKind.Frost => PaneArt.Frost(w, h, step, pattern, thick),
                PaneKind.Grime => PaneArt.StormGrime(w, h, step, pattern, thick),
                _ => board ? PaneArt.BoardSnow(w, h, step, pattern) : PaneArt.Snow(w, h, step, pattern)
            };
            LoadedTexture texture = Upload(pixels, pw, ph);
            paneTextures[key] = texture;
            return texture.TextureId;
        }

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (failed || BEUniversalDisplay.Layered.Count == 0) return;
            IRenderAPI rpi = capi.Render;
            if (!gameTexturesRead) LoadTextures();
            Vec3d cam = capi.World.Player.Entity.CameraPos;

            frameList.Clear();
            frameList.AddRange(BEUniversalDisplay.Layered);
            // FAR TO NEAR: the layers write no depth and the glass is see-through, so one drawn later lies over one
            // drawn before. Drawn furthest first, the nearest last.
            sortCam = cam;
            frameList.Sort(FarthestFirst);
            // frost and snow creep toward what the weather wants - smoothly, a little every frame
            float snowCreep = BEUniversalDisplay.SnowCreepPerSecond * deltaTime;
            foreach (BEUniversalDisplay be in frameList)
            {
                be.CatchUpGlassLayer();
                float diff = be.FrostTarget - be.FrostShown;
                float creep = (diff > 0 ? be.FrostGrowRate : be.FrostMeltRate) * deltaTime;   // grows with the cold, thaws with the warmth
                be.FrostShown = Math.Abs(diff) <= creep ? be.FrostTarget : be.FrostShown + Math.Sign(diff) * creep;
                if (be.FrostThick < 0f) be.FrostThick = be.FrostThickTarget;   // first time: as thick as the cold says
                diff = be.FrostThickTarget - be.FrostThick;
                creep = FrostThickPerSecond * deltaTime;
                be.FrostThick = Math.Abs(diff) <= creep ? be.FrostThickTarget : be.FrostThick + Math.Sign(diff) * creep;
                diff = be.SnowTarget - be.SnowShown;
                be.SnowShown = Math.Abs(diff) <= snowCreep ? be.SnowTarget : be.SnowShown + Math.Sign(diff) * snowCreep;
                diff = be.GrimeTarget - be.GrimeShown;
                creep = (diff > 0 ? BEUniversalDisplay.GrimeGrowPerSecond : BEUniversalDisplay.GrimeClearPerSecond) * deltaTime;
                be.GrimeShown = Math.Abs(diff) <= creep ? be.GrimeTarget : be.GrimeShown + Math.Sign(diff) * creep;
                if (be.GrimeThick < 0f) be.GrimeThick = be.GrimeThickTarget;
                diff = be.GrimeThickTarget - be.GrimeThick;
                creep = FrostThickPerSecond * deltaTime;
                be.GrimeThick = Math.Abs(diff) <= creep ? be.GrimeThickTarget : be.GrimeThick + Math.Sign(diff) * creep;
                // rain builds up as it rains and dries off slowly after
                diff = be.DropLevel - be.RainShown;
                creep = (diff > 0 ? BEUniversalDisplay.RainBuildPerSecond : BEUniversalDisplay.RainDryPerSecond) * deltaTime;
                be.RainShown = Math.Abs(diff) <= creep ? be.DropLevel : be.RainShown + Math.Sign(diff) * creep;
                // the wood soaks up the rain and dries off slowly after
                diff = be.DampTarget - be.DampShown;
                creep = (diff > 0 ? BEUniversalDisplay.DampSoakPerSecond
                         : be.DampSheltered ? BEUniversalDisplay.DampShelteredDryPerSecond : BEUniversalDisplay.DampDryPerSecond) * deltaTime;
                be.DampShown = Math.Abs(diff) <= creep ? be.DampTarget : be.DampShown + Math.Sign(diff) * creep;
                float pace = BEUniversalDisplay.RainSlowest + (BEUniversalDisplay.RainFastest - BEUniversalDisplay.RainSlowest)
                             * Math.Clamp(be.RainShown / BEUniversalDisplay.DropLevels, 0f, 1f);
                be.RainClock = (be.RainClock + deltaTime * 1000f / BEUniversalDisplay.DropFrameMs * pace) % BEUniversalDisplay.DropFrames;
            }

            IStandardShaderProgram prog = null;
            try
            {
                rpi.GlToggleBlend(true);
                rpi.GLDepthMask(false);
                rpi.GlDisableCullFace();
                BEUniversalDisplay first = frameList[0];
                var settings = UniversalDisplayLibConfig.Current;
                bool showFrost = settings?.FrostOnWindows != false, showSnow = settings?.SnowOnWindows != false,
                     showRain = settings?.RainOnWindows != false, showGrime = settings?.StormGrimeOnWindows != false;
                if (!showRain) foreach (BEUniversalDisplay be in frameList) { be.RainShown = 0f; be.DampShown = 0f; }
                prog = rpi.PreparedStandardShader(first.Pos.X, first.Pos.Y, first.Pos.Z);
                prog.NormalShaded = 0;
                prog.ExtraGodray = 0f;
                prog.SsaoAttn = 0f;
                prog.ViewMatrix = rpi.CameraMatrixOriginf;
                prog.ProjectionMatrix = rpi.CurrentProjectionMatrix;
                foreach (BEUniversalDisplay be in frameList)
                {
                    if (be.LayerPieces.Count == 0) continue;
                    prog.RgbaLightIn = capi.World.BlockAccessor.GetLightRGBs(be.Pos.X + be.LightCell.X, be.Pos.Y + be.LightCell.Y, be.Pos.Z + be.LightCell.Z);   // each window its own light (a shut shutter: from beside it)
                    int pattern = Math.Clamp(be.FrostPattern, 0, FrostPatterns - 1);
                    int paneNo = 0;
                    float frostShown = showFrost ? be.FrostShown : 0f, snowShown = showSnow ? be.SnowShown : 0f,
                          grimeShown = showGrime && !be.BoardSnow ? be.GrimeShown : 0f;
                    foreach (BEUniversalDisplay.GlassPiece piece in be.LayerPieces)
                    {
                        // place x turn (as the block) x the pane's own animation transform (none for the still glass)
                        model.Identity()
                            .Translate((float)(be.Pos.X - cam.X), (float)(be.Pos.Y - cam.Y), (float)(be.Pos.Z - cam.Z))
                            .Translate(0.5f, 0f, 0.5f).RotateY(be.MeshAngleRad).Translate(-0.5f, 0f, -0.5f);
                        float[] jm = be.LayerJointMatrix(piece.Joint);
                        if (jm != null) model.Mul(jm);
                        prog.ModelMatrix = model.Values;
                        Vec3i side = be.LayerSideNormal;

                        // DAMP WOOD first, under the snow and the rain
                        if (be.DampShown > 0.01f)
                        {
                            float frontsOut = FacingWeight(piece.FrontFacing, side), backsOut = FacingWeight(piece.BackFacing, side);
                            for (int d = 0; d < PaneArt.DampPatterns; d++)
                            {
                                fade = frontsOut;
                                if (fade > 0.01f && piece.DampFront[d] != null) DrawDamp(prog, rpi, piece.DampFront[d], be.DampShown, d);
                                fade = backsOut;
                                if (fade > 0.01f && piece.DampBack[d] != null) DrawDamp(prog, rpi, piece.DampBack[d], be.DampShown, d);
                            }
                            fade = 1f;
                        }

                        // FROST, then SNOW over it: each pane its own pictures, this step's fading into the next
                        for (int i = 0; i < piece.Panes.Count; i++)
                        {
                            var (mesh, w, h, _, upper) = piece.Panes[i];
                            int panePattern = be.BoardSnow ? pattern : (pattern * 2 + paneNo++) % PanePatterns;
                            fade = FacingWeight(i < piece.Facing.Count ? piece.Facing[i] : null, side);
                            if (fade <= 0.01f) { fade = 1f; continue; }
                            DrawThick(prog, rpi, mesh, w, h, PaneKind.Frost, frostShown, PaneArt.FrostSteps, be.FrostThick, PaneArt.FrostThickLevels, panePattern, FrostOpacity);
                            DrawThick(prog, rpi, mesh, w, h, PaneKind.Grime, grimeShown, PaneArt.GrimeSteps, be.GrimeThick, PaneArt.GrimeThickLevels, panePattern, 1f);
                            DrawCreeping(prog, rpi, mesh, w, h, PaneKind.Snow, upper ? snowShown * BEUniversalDisplay.UpperSnowShare : snowShown, PaneArt.SnowSteps, panePattern, 1f, be.BoardSnow);
                            fade = 1f;
                        }
                        // RAIN
                        // RAIN: each pattern its own point in the drop loop, so blocks sharing a pattern still run apart from the rest
                        // (a shutter board's rain on whichever side is not turned away from the weather - an open leaf
                        // folds its front to the wall and turns its back out)
                        if (be.RainShown > 0.01f && showRain)
                        {
                            float fronts = be.BoardSnow ? FacingWeight(piece.FrontFacing, side) : 1f;
                            float backsOut = FacingWeight(piece.BackFacing, side);
                            for (int r = 0; r < BEUniversalDisplay.DropPatterns; r++)
                            {
                                int frame = ((int)be.RainClock + be.DropPhase + r * 11) % BEUniversalDisplay.DropFrames;
                                fade = fronts;
                                if (fade > 0.01f && piece.Rain[r] != null) DrawRain(prog, rpi, piece.Rain[r], be.RainShown, r, frame);
                                fade = backsOut;
                                if (fade > 0.01f && piece.RainBack[r] != null) DrawRain(prog, rpi, piece.RainBack[r], be.RainShown, r, frame);
                            }
                            fade = 1f;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                failed = true;
                capi.Logger.Warning("[UniversalDisplayLib] Rain / frost on window glass failed and is off for this session: {0}", e);
            }
            finally
            {
                prog?.Stop();
                rpi.GlEnableCullFace();
                rpi.GLDepthMask(true);
                rpi.GlToggleBlend(false);
            }
        }

        /// <summary>
        /// How much a face of the piece now drawn (model) shows, by how far it faces the weather side - its unturned
        /// facing turned by the model matrix (column-major; a direction, so no translation): 1 facing out or side-on,
        /// fading to 0 as it turns away (FacingFadeFrom to FacingFadeTo). A swinging shutter's snow, rain and damp pass
        /// from front to back smoothly - they switched off at one angle before. Null facing: always 1.
        /// </summary>
        float FacingWeight(Vec3f facing, Vec3i side)
        {
            if (facing == null) return 1f;
            float[] m = model.Values;
            float nowX = m[0] * facing.X + m[4] * facing.Y + m[8] * facing.Z;
            float nowZ = m[2] * facing.X + m[6] * facing.Y + m[10] * facing.Z;
            float t = Math.Clamp((nowX * side.X + nowZ * side.Z - FacingFadeTo) / (FacingFadeFrom - FacingFadeTo), 0f, 1f);
            return t * t * (3 - 2 * t);
        }

        const float FacingFadeFrom = 0f, FacingFadeTo = -0.6f;

        /// <summary>The facing fade the next draws take (FacingWeight); 1 outside a faced draw.</summary>
        float fade = 1f;

        /// <summary>The damp at <paramref name="shown"/> steps (0 to PaneArt.DampSteps): step floor(shown) fading into the next.</summary>
        void DrawDamp(IStandardShaderProgram prog, IRenderAPI rpi, MeshRef mesh, float shown, int pattern)
        {
            int lo = (int)Math.Floor(shown);
            float into = shown - lo;
            if (lo >= 1) DrawWith(prog, rpi, mesh, DampTexture(Math.Min(lo, PaneArt.DampSteps), pattern), (lo >= PaneArt.DampSteps ? 1f : 1f - into) * DampOpacity);
            if (lo < PaneArt.DampSteps && into > 0.01f) DrawWith(prog, rpi, mesh, DampTexture(lo + 1, pattern), into * DampOpacity);
        }

        /// <summary>Rain at <paramref name="shown"/> levels (0 to DropLevels): level floor(shown) fading into the next; below 1, level 1 fading in.</summary>
        void DrawRain(IStandardShaderProgram prog, IRenderAPI rpi, MeshRef mesh, float shown, int pattern, int frame)
        {
            int lo = (int)Math.Floor(shown);
            float into = shown - lo;
            if (lo >= 1) DrawWith(prog, rpi, mesh, RainTexture(Math.Min(lo, BEUniversalDisplay.DropLevels) - 1, pattern, frame), (lo >= BEUniversalDisplay.DropLevels ? 1f : 1f - into) * RainOpacity);
            if (lo < BEUniversalDisplay.DropLevels && into > 0.01f) DrawWith(prog, rpi, mesh, RainTexture(lo, pattern, frame), into * RainOpacity);
        }

        /// <summary>A creeping picture at <paramref name="shown"/> steps: step floor(shown) fading into the next.</summary>
        /// <summary>
        /// The frost or the storm grime: its forming step (floor fading into the next, as DrawCreeping) at its thickness - the
        /// two nearest of its thickness pictures, faded between - so both the forming and the thickening are smooth.
        /// </summary>
        void DrawThick(IStandardShaderProgram prog, IRenderAPI rpi, MeshRef mesh, int w, int h, PaneKind kind, float shown, int steps,
                       float thick, int thickLevels, int pattern, float opacity)
        {
            if (shown <= 0.01f) return;
            int lo = (int)Math.Floor(shown);
            float into = shown - lo;
            float levels = Math.Clamp(thick, 0f, 1f) * (thickLevels - 1);
            int tLo = (int)Math.Floor(levels);
            float tInto = levels - tLo;
            for (int s = 0; s < 2; s++)
            {
                int step = lo + s;
                float sw = s == 0 ? 1f - into : into;
                if (step < 1 || step > steps || sw <= 0.01f) continue;
                for (int t = 0; t < 2; t++)
                {
                    int level = tLo + t;
                    float tw = t == 0 ? 1f - tInto : tInto;
                    if (level >= thickLevels || tw <= 0.01f) continue;
                    DrawWith(prog, rpi, mesh, PaneTexture(w, h, kind, step, pattern + 1, false, level), sw * tw * opacity);
                }
            }
        }

        void DrawCreeping(IStandardShaderProgram prog, IRenderAPI rpi, MeshRef mesh, int w, int h, PaneKind kind, float shown, int steps, int pattern, float opacity, bool board = false)
        {
            if (shown <= 0.01f) return;
            int lo = (int)Math.Floor(shown);
            float into = shown - lo;
            if (lo >= 1 && lo <= steps) DrawWith(prog, rpi, mesh, PaneTexture(w, h, kind, lo, pattern + 1, board), (1f - into) * opacity);
            if (lo + 1 <= steps && into > 0.01f) DrawWith(prog, rpi, mesh, PaneTexture(w, h, kind, lo + 1, pattern + 1, board), into * opacity);
        }

        void DrawWith(IStandardShaderProgram prog, IRenderAPI rpi, MeshRef mesh, int texture, float alpha)
        {
            if (texture <= 0) return;   // never a picture that did not load
            tint.A = alpha * fade;
            prog.RgbaTint = tint;
            prog.Tex2D = texture;
            rpi.RenderMesh(mesh);
        }

        public void Dispose()
        {
            foreach (LoadedTexture texture in paneTextures.Values) texture.Dispose();
            paneTextures.Clear();
            foreach (LoadedTexture texture in rainFrames) texture?.Dispose();
            Array.Clear(rainFrames);
            foreach (LoadedTexture texture in dampFrames) texture?.Dispose();
            Array.Clear(dampFrames);
        }
    }
}
