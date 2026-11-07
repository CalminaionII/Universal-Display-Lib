using System;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace UniversalDisplayLib
{
    /// <summary>
    /// BORROWED LIGHT FOR A DISPLAY THAT BLOCKS LIGHT. A shut shutter absorbs light, so the light IN its own space is
    /// 0 once no sky shines straight down on it (under a roof). The game's AnimatableRenderer lights the moving parts
    /// with exactly that light (GetLightRGBs at its own position), so a shut shutter's leaves drew pitch black; the
    /// still parts are lit from the faces' neighbours and looked right. While the block absorbs light, the moving parts
    /// and the glass layer take the light of the brightest space beside it (or above) instead.
    ///
    /// The renderer reads its light at its `pos`, and places the model there too. So its pos is given its own Vec3d,
    /// moved to the lit space, and its CustomTransform moves the model back by the same amount - drawn where it always
    /// was, lit from next door. With nothing to borrow, pos is the window's own again and CustomTransform null (the
    /// renderer's own placement).
    /// </summary>
    public partial class BEUniversalDisplay
    {
        static readonly FieldInfo rendererPosField =
            typeof(AnimatableRenderer).GetField("pos", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>The space whose light this display is drawn with, from its own (0, 0, 0 = its own).</summary>
        internal Vec3i LightCell = new Vec3i();

        AnimatableRenderer _litRenderer;
        Vec3d _litBase, _litPos;
        Vec3i _litFor;
        float _litRotY = float.NaN;

        static readonly BlockFacing[] lightSides = { BlockFacing.NORTH, BlockFacing.EAST, BlockFacing.SOUTH, BlockFacing.WEST, BlockFacing.UP };

        /// <summary>Client tick: which space to borrow the light from - none unless the block absorbs light.</summary>
        void UpdateBorrowedLight()
        {
            if (capi == null || Block == null || Pos == null) return;
            if (Block.LightAbsorption <= 0) { LightCell.Set(0, 0, 0); return; }
            IBlockAccessor ba = capi.World.BlockAccessor;
            float best = -1f;
            Vec3i pick = new Vec3i();
            foreach (BlockFacing side in lightSides)
            {
                Vec4f light = ba.GetLightRGBs(Pos.X + side.Normali.X, Pos.Y + side.Normali.Y, Pos.Z + side.Normali.Z);
                float bright = Math.Max(Math.Max(light.R, light.G), Math.Max(light.B, light.A));
                if (bright > best) { best = bright; pick = side.Normali; }
            }
            LightCell.Set(pick.X, pick.Y, pick.Z);
        }

        /// <summary>Every frame, before the moving parts are drawn (PaneAnimTicker): the renderer set to the borrowed light.</summary>
        void ApplyBorrowedLight()
        {
            AnimatableRenderer renderer = animUtil?.renderer;
            if (renderer == null || rendererPosField == null) return;
            if (renderer != _litRenderer)
            {
                // a new renderer: its own position kept as the base, a Vec3d of ours given to it to move
                if (rendererPosField.GetValue(renderer) is not Vec3d own) return;
                _litRenderer = renderer;
                _litBase = own.Clone();
                _litPos = own.Clone();
                rendererPosField.SetValue(renderer, _litPos);
                _litFor = null;
            }
            float rotY = renderer.rotationDeg?.Y ?? 0f;
            if (_litFor != null && _litFor.Equals(LightCell) && rotY == _litRotY) return;
            _litFor = LightCell.Clone();
            _litRotY = rotY;
            _litPos.Set(_litBase.X + LightCell.X, _litBase.Y + LightCell.Y, _litBase.Z + LightCell.Z);
            if (LightCell.X == 0 && LightCell.Y == 0 && LightCell.Z == 0) { renderer.CustomTransform = null; return; }
            // back by the borrowed offset, then the renderer's own placement (turned about the block's middle)
            float[] m = Mat4f.Create();
            Mat4f.Translate(m, m, -LightCell.X, -LightCell.Y, -LightCell.Z);
            Mat4f.Translate(m, m, 0.5f, 0f, 0.5f);
            Mat4f.Scale(m, m, renderer.ScaleX, renderer.ScaleY, renderer.ScaleZ);
            Mat4f.RotateY(m, m, rotY * GameMath.DEG2RAD);
            Mat4f.Translate(m, m, -0.5f, 0f, -0.5f);
            renderer.CustomTransform = m;
        }
    }
}
