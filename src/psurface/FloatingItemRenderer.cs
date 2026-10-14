using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace UniversalDisplayLib
{
    /// <summary>
    /// Halloween (Calm, 2026-10-12: "make items on the shelves float, as long as they're not stacked" - "the real floating
    /// version"): draws ONE stored item on its own for a few seconds while the block's mesh leaves it out - it rises about
    /// five voxels, bobs and sways gently, then drops back onto its spot. Started by BEUniversalDisplay.StartFloat when the
    /// server says so (HauntFloat); the server plays the landing thud. Drawn the same way as the placing ghost
    /// (PlacementGhostRenderer.Draw), but solid and lit.
    /// </summary>
    internal sealed class FloatingItemRenderer : IRenderer
    {
        const float RiseSeconds = 0.9f, DropSeconds = 0.22f, Bob = 0.6f / 16f, BobSeconds = 1.6f, SwayDeg = 7f;
        /// <summary>How high this item rises, in blocks - each float its own (Calm: "I like the idea of different heights").</summary>
        readonly float Height;

        readonly ICoreClientAPI capi;
        readonly BEUniversalDisplay be;
        readonly string slotId;
        readonly ItemStack stack;
        readonly MultiTextureMeshRef meshRef;
        readonly float[] slotMatrix;
        readonly float centreX, centreZ;
        readonly float total;
        float elapsed;
        bool done;

        public double RenderOrder => 0.5;
        public int RenderRange => 48;

        FloatingItemRenderer(ICoreClientAPI capi, BEUniversalDisplay be, string slotId, ItemStack stack, MultiTextureMeshRef meshRef,
                             float[] slotMatrix, Cuboidf drawn, int ms, float heightVoxels)
        {
            Height = heightVoxels / 16f;
            this.capi = capi; this.be = be; this.slotId = slotId; this.stack = stack; this.meshRef = meshRef; this.slotMatrix = slotMatrix;
            centreX = drawn == null ? 0.5f : (drawn.X1 + drawn.X2) / 2f;
            centreZ = drawn == null ? 0.5f : (drawn.Z1 + drawn.Z2) / 2f;
            total = Math.Max(RiseSeconds + DropSeconds + 0.5f, ms / 1000f);
        }

        /// <summary>Upload the item's mesh and start drawing it; false (nothing changes) if the mesh cannot be uploaded.</summary>
        public static bool TryStart(ICoreClientAPI capi, BEUniversalDisplay be, string slotId, ItemStack stack, MeshData mesh, float[] slotMatrix, int ms, float heightVoxels)
        {
            MultiTextureMeshRef meshRef;
            try
            {
                MeshData upload = mesh;
                if ((mesh.TextureIds?.Length ?? 0) != 1 && mesh.TextureIndices == null)
                {
                    // the same single-texture collapse the ghost uses for meshes that cannot be split
                    upload = mesh.Clone();
                    upload.TextureIds = new[] { mesh.TextureIds?.Length > 0 ? mesh.TextureIds[0] : capi.BlockTextureAtlas.AtlasTextures[0].TextureId };
                }
                meshRef = capi.Render.UploadMultiTextureMesh(upload);
            }
            catch (Exception) { return false; }
            if (meshRef == null) return false;

            var r = new FloatingItemRenderer(capi, be, slotId, stack, meshRef, slotMatrix, BEUniversalDisplay.MeshBounds(mesh, slotMatrix), ms, heightVoxels);
            capi.Event.RegisterRenderer(r, EnumRenderStage.Opaque, "universaldisplaylib-float");
            return true;
        }

        public void OnRenderFrame(float dt, EnumRenderStage stage)
        {
            if (done) return;
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

            Draw(Math.Max(0f, lift), sway);
        }

        void Draw(float lift, float sway)
        {
            IRenderAPI rpi = capi.Render;
            BlockPos pos = be.Pos;
            Vec3d cam = capi.World.Player.Entity.CameraPos;
            IStandardShaderProgram prog = null;
            try
            {
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
                rpi.RenderMultiTextureMesh(meshRef, "tex");
            }
            finally
            {
                prog?.Stop();   // the standard shader must never be left bound (see PlacementGhostRenderer.Draw)
            }
        }

        void Finish()
        {
            if (done) return;
            done = true;
            capi.Event.UnregisterRenderer(this, EnumRenderStage.Opaque);
            meshRef?.Dispose();
            be.EndFloat(slotId);
        }

        public void Dispose()
        {
            if (!done) { done = true; meshRef?.Dispose(); }
        }
    }
}
