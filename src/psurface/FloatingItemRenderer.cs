using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace UniversalDisplayLib
{
    /// <summary>
    /// Halloween: draws ONE stored item on its own for a few seconds while the block's mesh leaves it out - it rises
    /// about five voxels, bobs and sways gently, then drops back onto its spot. Started by
    /// BEUniversalDisplay.StartFloat when the server says so (HauntFloat); the server plays the landing thud. Drawn the
    /// same way as the placing ghost (PlacementGhostRenderer.Draw), but solid and lit.
    ///
    /// HOVER MODE (the levitating display, BEUniversalDisplay.UpdateHovers): the same drawing, but it never lands. The
    /// item is already at its hover height in its slot matrix (the rise is part of where it rests), so this only adds
    /// the motion on top - easing up from the base when it is put on, a gentle bob and a slow steady turn - until it is
    /// stopped.
    /// </summary>
    internal sealed class FloatingItemRenderer : IRenderer
    {
        const float RiseSeconds = 0.9f, DropSeconds = 0.22f, Bob = 0.6f / 16f, BobSeconds = 1.6f, SwayDeg = 7f;
        /// <summary>How high this item rises, in blocks - each float its own.</summary>
        readonly float Height;

        readonly ICoreClientAPI capi;
        readonly BEUniversalDisplay be;
        readonly string slotId;
        readonly ItemStack stack;
        /// <summary>
        /// The item's SOLID faces (drawn in the Opaque stage, writing depth) and its SEE-THROUGH ones (Transparent /
        /// BlendNoCull faces, drawn AfterOIT with no depth write; null when it has none). Otherwise a levitating glass
        /// tank in a window hides the window glass and the frost behind it: the whole item would write depth in the
        /// Opaque stage, so the glass and the weather layers drawn later would fail the depth test where it covered
        /// them.
        /// </summary>
        readonly MultiTextureMeshRef meshRef, clearRef;
        /// <summary>The lift and turn worked out in the Opaque stage this frame - the see-through faces drawn AfterOIT use them.</summary>
        float drawnLift, drawnSway;
        float[] slotMatrix;
        // volatile: also read by the particle thread (Follow)
        volatile float centreX, centreZ;
        readonly float total;
        float elapsed;
        bool done;

        // hover mode (the levitating display) - see the class summary
        readonly bool hover;
        readonly float easeSeconds;
        float rise, bob, bobSeconds, turnSeconds;
        readonly MeshData sourceMesh;
        /// <summary>0 to 1 round the bob and the turn, kept small so a float that hovers for hours stays smooth.</summary>
        float bobPhase, turnPhase;
        /// <summary>
        /// Hover mode, an item that was already resting: it stays exactly on its still pose for MotionSettleSeconds, so it
        /// lines up with the still copy if that is slow to leave the block's mesh, then eases into its motion over
        /// MotionRampSeconds. The phases start at 0, which is that still pose, so nothing jumps.
        /// </summary>
        const float MotionSettleSeconds = 2f, MotionRampSeconds = 2f;
        /// <summary>Hover mode: the block redraw this copy waits for before it shows (BEUniversalDisplay.RedrawnSince); -1 once it shows.</summary>
        int holdForVersion = -1;
        /// <summary>Hover mode: the lift (blocks) and turn (radians) drawn this frame - for the particles and the outline.</summary>
        volatile float liftNow, turnNow;

        /// <summary>Over: stopped, or it noticed its item taken or its block gone.</summary>
        public bool Finished => done;

        // after the glass layer (GlassLayerRenderer, 0.5) in the AfterOIT stage, so the see-through faces lie over the window
        // glass and the frost behind them
        public double RenderOrder => 0.6;
        public int RenderRange => 48;

        FloatingItemRenderer(ICoreClientAPI capi, BEUniversalDisplay be, string slotId, ItemStack stack, (MultiTextureMeshRef solid, MultiTextureMeshRef clear) refs,
                             float[] slotMatrix, Cuboidf drawn, int ms, float heightVoxels)
        {
            Height = heightVoxels / 16f;
            this.capi = capi; this.be = be; this.slotId = slotId; this.stack = stack; meshRef = refs.solid; clearRef = refs.clear; this.slotMatrix = slotMatrix;
            centreX = drawn == null ? 0.5f : (drawn.X1 + drawn.X2) / 2f;
            centreZ = drawn == null ? 0.5f : (drawn.Z1 + drawn.Z2) / 2f;
            total = Math.Max(RiseSeconds + DropSeconds + 0.5f, ms / 1000f);
        }

        FloatingItemRenderer(ICoreClientAPI capi, BEUniversalDisplay be, string slotId, ItemStack stack, (MultiTextureMeshRef solid, MultiTextureMeshRef clear) refs,
                             MeshData mesh, float[] slotMatrix, BEUniversalDisplay.FloatMotion motion, bool easeUp)
        {
            hover = true;
            this.capi = capi; this.be = be; this.slotId = slotId; this.stack = stack; meshRef = refs.solid; clearRef = refs.clear;
            sourceMesh = mesh;
            easeSeconds = easeUp ? motion.EaseSeconds : 0f;
            Retarget(slotMatrix, motion);
        }

        /// <summary>Upload the item's mesh and start drawing it; false (nothing changes) if the mesh cannot be uploaded.</summary>
        public static bool TryStart(ICoreClientAPI capi, BEUniversalDisplay be, string slotId, ItemStack stack, MeshData mesh, float[] slotMatrix, int ms, float heightVoxels)
        {
            var refs = UploadSplit(capi, mesh);
            if (refs.solid == null && refs.clear == null) return false;
            var r = new FloatingItemRenderer(capi, be, slotId, stack, refs, slotMatrix, BEUniversalDisplay.MeshBounds(mesh, slotMatrix), ms, heightVoxels);
            r.Register("universaldisplaylib-float");
            return true;
        }

        /// <summary>
        /// Hover mode: start drawing a floating item moving, or null (nothing changes) if its mesh cannot be uploaded.
        /// <paramref name="easeUp"/>: it was just put on, so it rises from the base rather than starting at its height.
        /// </summary>
        public static FloatingItemRenderer TryStartHover(ICoreClientAPI capi, BEUniversalDisplay be, string slotId, ItemStack stack,
                                                         MeshData mesh, float[] slotMatrix, BEUniversalDisplay.FloatMotion motion, bool easeUp)
        {
            var refs = UploadSplit(capi, mesh);
            if (refs.solid == null && refs.clear == null) return null;
            var r = new FloatingItemRenderer(capi, be, slotId, stack, refs, mesh, slotMatrix, motion, easeUp);
            r.Register("universaldisplaylib-hover");
            return r;
        }

        /// <summary>The Opaque stage always (it moves the item and draws its solid faces); AfterOIT too when it has see-through faces.</summary>
        void Register(string name)
        {
            capi.Event.RegisterRenderer(this, EnumRenderStage.Opaque, name);
            if (clearRef != null) capi.Event.RegisterRenderer(this, EnumRenderStage.AfterOIT, name + "-clear");
        }

        static bool SeeThrough(short passAndBits)
        {
            int pass = passAndBits & 0x3ff;   // lower 10 bits: the render pass (vsapi MeshData.RenderPassesAndExtraBits)
            return pass == (int)EnumChunkRenderPass.Transparent || pass == (int)EnumChunkRenderPass.BlendNoCull;
        }

        /// <summary>
        /// The mesh as its solid and its see-through faces, each uploaded (a part with no faces: null). A mesh with no
        /// see-through faces (every ITEM - displays draw items OpaqueNoCull) or no per-face textures stays whole, as before.
        /// </summary>
        static (MultiTextureMeshRef solid, MultiTextureMeshRef clear) UploadSplit(ICoreClientAPI capi, MeshData mesh)
        {
            short[] passes = mesh?.RenderPassesAndExtraBits;
            int faces = mesh == null ? 0 : mesh.VerticesCount / Math.Max(1, mesh.VerticesPerFace);
            bool any = false;
            if (passes != null && mesh.TextureIndices != null && passes.Length >= faces)
                for (int i = 0; i < faces && !any; i++) any = SeeThrough(passes[i]);
            if (!any) return (Upload(capi, mesh), null);
            try
            {
                MeshData solid = mesh.EmptyClone(), clear = mesh.EmptyClone();
                solid.AddMeshData(mesh, i => !SeeThrough(passes[i]));
                clear.AddMeshData(mesh, i => SeeThrough(passes[i]));
                return (solid.VerticesCount > 0 ? Upload(capi, solid) : null, Upload(capi, clear));
            }
            catch (Exception) { return (Upload(capi, mesh), null); }
        }

        static MultiTextureMeshRef Upload(ICoreClientAPI capi, MeshData mesh)
        {
            try
            {
                MeshData upload = mesh;
                if ((mesh.TextureIds?.Length ?? 0) != 1 && mesh.TextureIndices == null)
                {
                    // the same single-texture collapse the ghost uses for meshes that cannot be split
                    upload = mesh.Clone();
                    upload.TextureIds = new[] { mesh.TextureIds?.Length > 0 ? mesh.TextureIds[0] : capi.BlockTextureAtlas.AtlasTextures[0].TextureId };
                }
                return capi.Render.UploadMultiTextureMesh(upload);
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Hover mode: the item's spot or its settings changed (turned, nudged, resized, the menu) - keep moving, from
        /// where it is, so nothing jumps.
        /// </summary>
        public void Retarget(float[] matrix, BEUniversalDisplay.FloatMotion motion)
        {
            rise = motion.RiseVoxels / 16f;
            bob = motion.BobVoxels / 2f / 16f;   // the whole bob is BobVoxels: half of it up, half down
            bobSeconds = Math.Max(0.1f, motion.BobSeconds);
            turnSeconds = motion.TurnSeconds;
            if (matrix == null || sourceMesh == null) return;
            slotMatrix = matrix;
            Cuboidf drawn = BEUniversalDisplay.MeshBounds(sourceMesh, matrix);
            centreX = drawn == null ? 0.5f : (drawn.X1 + drawn.X2) / 2f;
            centreZ = drawn == null ? 0.5f : (drawn.Z1 + drawn.Z2) / 2f;
        }

        /// <summary>
        /// Hover mode, any thread: where a point of the still item (block units, as the slot matrix puts it) is on the
        /// moving copy now - the same lift and turn Draw uses. For its particles.
        /// </summary>
        internal Vec3f Follow(Vec3f still)
        {
            if (!hover || done || still == null) return still;
            float cx = centreX, cz = centreZ;
            Vec4f v = new Matrixf().Translate(cx, liftNow, cz).RotateY(turnNow).Translate(-cx, 0f, -cz)
                .TransformVector(new Vec4f(still.X, still.Y, still.Z, 1f));
            return new Vec3f(v.X, v.Y, v.Z);
        }

        /// <summary>Hover mode: how far the moving copy is lifted off its still height now, in blocks (its outline follows it).</summary>
        internal float LiftNow => hover && !done ? liftNow : 0f;

        /// <summary>Hover mode: show nothing until the block has started redrawing without this item, so it never shows twice while the world loads.</summary>
        internal void HoldUntilRedrawn(int version) => holdForVersion = version;

        /// <summary>Hover mode: stop now (too far away, the setting turned off, the item no longer floating).</summary>
        public void Stop() => Finish();

        public void OnRenderFrame(float dt, EnumRenderStage stage)
        {
            if (done) return;
            if (stage == EnumRenderStage.AfterOIT)
            {
                if (clearRef != null && holdForVersion < 0) Draw(clearRef, drawnLift, drawnSway, false);   // where the Opaque stage left it this frame
                return;
            }
            if (hover) { RenderHover(dt); return; }
            elapsed += dt;
            // over, the block gone, or the item taken / swapped while it floated
            // (by class + id: a sync rebuilds the stack objects, so the same item comes back as a new one - and not yet
            // resolved, so its Collectible may be null for a moment; the id is there from the start)
            ItemStack now = be.StackIn(slotId);
            if (elapsed >= total || be.IsGone || now == null || now.Class != stack.Class || now.Id != stack.Id)
            {
                Finish();
                return;
            }

            // rise (ease out), hover with a bob and a sway, drop (falls, speeding up)
            float lift, hoverT = elapsed - RiseSeconds;
            float dropStart = total - DropSeconds;
            if (elapsed < RiseSeconds) { float t = elapsed / RiseSeconds; lift = Height * (1f - (1f - t) * (1f - t)); }
            else if (elapsed < dropStart) lift = Height + Bob * (float)Math.Sin(hoverT / BobSeconds * Math.PI * 2);
            else
            {
                float fromLift = Height + Bob * (float)Math.Sin((dropStart - RiseSeconds) / BobSeconds * Math.PI * 2);
                float t = (elapsed - dropStart) / DropSeconds;
                lift = fromLift * (1f - t * t);
            }
            float swayIn = Math.Clamp(elapsed / RiseSeconds, 0f, 1f) * Math.Clamp((total - elapsed) / DropSeconds, 0f, 1f);
            float sway = SwayDeg * GameMath.DEG2RAD * swayIn * (float)Math.Sin(elapsed / (BobSeconds * 1.7f) * Math.PI * 2);

            Move(Math.Max(0f, lift), sway);
        }

        void RenderHover(float dt)
        {
            ItemStack now = be.StackIn(slotId);
            if (be.IsGone || now == null || now.Class != stack.Class || now.Id != stack.Id)
            {
                Finish();
                return;
            }

            if (holdForVersion >= 0)
            {
                if (!be.RedrawnSince(holdForVersion)) return;
                holdForVersion = -1;
            }

            elapsed = Math.Min(elapsed + dt, 3600f);
            // an item easing up from the base is moving already; a resting one settles first, then picks up speed
            float motionIn = easeSeconds > 0f ? 1f : GameMath.Clamp((elapsed - MotionSettleSeconds) / MotionRampSeconds, 0f, 1f);
            motionIn = motionIn * motionIn * (3f - 2f * motionIn);
            bobPhase = (bobPhase + dt * motionIn / bobSeconds) % 1f;
            // signed: a negative time turns the other way; kept in 0..1 either way
            if (turnSeconds != 0f) turnPhase = GameMath.Mod(turnPhase + dt * motionIn / turnSeconds, 1f);

            // eases up from the top of the base (ease out), the bob fading in with it so nothing jumps
            float t = easeSeconds > 0f ? Math.Clamp(elapsed / easeSeconds, 0f, 1f) : 1f;
            float eased = 1f - (1f - t) * (1f - t);
            float lift = -rise * (1f - eased) + eased * bob * (float)Math.Sin(bobPhase * Math.PI * 2);
            liftNow = lift;
            turnNow = turnPhase * GameMath.TWOPI;
            Move(lift, turnNow);
        }

        /// <summary>This frame's lift and turn: kept for the see-through faces, and the solid faces drawn now.</summary>
        void Move(float lift, float sway)
        {
            drawnLift = lift; drawnSway = sway;
            if (meshRef != null) Draw(meshRef, lift, sway, true);
        }

        void Draw(MultiTextureMeshRef mesh, float lift, float sway, bool writeDepth)
        {
            IRenderAPI rpi = capi.Render;
            BlockPos pos = be.Pos;
            Vec3d cam = capi.World.Player.Entity.CameraPos;
            IStandardShaderProgram prog = null;
            try
            {
                // GLASS SHOWS THROUGH: blended, and both sides of a face drawn as the chunk's OpaqueNoCull pass does -
                // the ghost does the same (PlacementGhostRenderer.Draw). Both put back below.
                rpi.GlToggleBlend(true);
                rpi.GlDisableCullFace();
                if (!writeDepth) rpi.GLDepthMask(false);   // the see-through faces: what lies behind still draws
                prog = rpi.PreparedStandardShader(pos.X, pos.Y, pos.Z);
                prog.ModelMatrix = new Matrixf()
                    .Translate((float)(pos.X - cam.X), (float)(pos.Y - cam.Y), (float)(pos.Z - cam.Z))
                    .Translate(centreX, lift, centreZ)
                    .RotateY(sway)
                    .Translate(-centreX, 0f, -centreZ)
                    .Mul(slotMatrix)
                    .Values;
                prog.ViewMatrix = rpi.CameraMatrixOriginf;
                prog.ProjectionMatrix = rpi.CurrentProjectionMatrix;
                rpi.RenderMultiTextureMesh(mesh, "tex");
            }
            finally
            {
                prog?.Stop();   // the standard shader must never be left bound (see PlacementGhostRenderer.Draw)
                if (!writeDepth) rpi.GLDepthMask(true);
                rpi.GlEnableCullFace();
                rpi.GlToggleBlend(false);
            }
        }

        void Finish()
        {
            if (done) return;
            done = true;
            capi.Event.UnregisterRenderer(this, EnumRenderStage.Opaque);
            if (clearRef != null) capi.Event.UnregisterRenderer(this, EnumRenderStage.AfterOIT);
            meshRef?.Dispose();
            clearRef?.Dispose();
            be.EndFloat(slotId);
        }

        public void Dispose()
        {
            if (!done) { done = true; meshRef?.Dispose(); clearRef?.Dispose(); }
        }
    }
}
