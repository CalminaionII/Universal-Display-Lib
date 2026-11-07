using System;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace UniversalDisplayLib
{
    /// <summary>
    /// WIND AND THE LATCH. WindWeather (the mod system) picks the moments; this is what one window does with them.
    ///
    /// * Only panes that SWING OUT move in the wind (WindPane) - measured from the model: a pane whose open box reaches out of
    ///   the wall's depth swings, one that stays in it (sash, sliding shutters) slides and is left alone.
    /// * Only windows with open sky beside them (ExposedToWeather).
    /// * In a strong breeze or storm the pane WOBBLES a little open and back on each player's screen (WindWobble.cs); the
    ///   window stays shut for rooms and boxes.
    /// * The LATCH: sneak + right-click the shut frame with an empty hand. A latched window does not open at all - not for
    ///   players, wind or the haunting - until the player who latched it unlatches it; an admin or a creative-mode player can too, so a window is never stuck for good.
    /// </summary>
    public partial class BEUniversalDisplay
    {
        bool _latched;
        /// <summary>Who latched it: the only player (besides an admin or creative) who can unlatch it.</summary>
        string _latchedBy, _latchedByName;
        public bool Latched => _latched;

        /// <summary>Panes the wind can move, worked out once per block from its model.</summary>
        bool[] _windPanes;
        /// <summary>Of those, the ones that open flat against the wall (External Shutter leaves) - they bang in a storm.</summary>
        bool[] _foldsFlat;

        /// <summary>Whether pane <paramref name="pane"/> opens folding flat against the wall (WindPane works it out).</summary>
        public bool FoldsFlat(int pane) => WindPane(pane) && _foldsFlat != null && pane < _foldsFlat.Length && _foldsFlat[pane];
        int _windPanesFor = -1;

        public const string LatchSound = "game:sounds/effect/latch";

        /// <summary>A window with a pane that opens can be latched.</summary>
        public bool CanLatch =>
            CanOpen && (SurfaceBehavior?.FrameBoxGroups ?? Array.Empty<FrameBoxGroup>()).Any(g => g.SealsRoom && !string.IsNullOrEmpty(g.AnimOpen));

        /// <summary>
        /// INNER DOORS ARE NOT LATCHED: the latch locks the panes that SEAL THE ROOM - the window itself. A pane with
        /// sealsRoom false (a display unit's inner door, a shelf flap) opens and shuts as ever. True when a click on
        /// <paramref name="boxId"/> is locked: a sealing pane, or the whole window / a fixed part of a window that has
        /// no other pane to open.
        /// </summary>
        public bool LatchLocks(string boxId)
        {
            if (!_latched) return false;
            FrameBoxGroup[] groups = SurfaceBehavior?.FrameBoxGroups ?? Array.Empty<FrameBoxGroup>();
            int pane = PaneOfBox(boxId);
            if (pane >= 0) return pane >= groups.Length || groups[pane].SealsRoom;
            return !groups.Any(g => !g.SealsRoom && !string.IsNullOrEmpty(g.AnimOpen));
        }

        /// <summary>Whether the latch locks pane <paramref name="pane"/>: a pane that seals the room, or a number that is no pane at all.</summary>
        public bool PaneLatched(int pane)
        {
            if (!_latched) return false;
            FrameBoxGroup[] groups = SurfaceBehavior?.FrameBoxGroups ?? Array.Empty<FrameBoxGroup>();
            return pane < 0 || pane >= groups.Length || groups[pane].SealsRoom;
        }

        /// <summary>
        /// Server: may this player undo the latch (or restyle the window under it)? Whoever latched it, a creative-mode player,
        /// an admin - and inside a land claim anyone who may build there, who could break the window anyway, so a latch cannot
        /// lock a claim's owner out of their own window. On unclaimed land it stays with whoever latched it.
        /// </summary>
        public bool MayUnlatch(IPlayer byPlayer)
        {
            if (!_latched) return true;
            if (byPlayer == null) return false;
            if (byPlayer.PlayerUID == _latchedBy || byPlayer.WorldData?.CurrentGameMode == EnumGameMode.Creative
                || byPlayer.HasPrivilege(Privilege.controlserver)) return true;
            var claims = Api?.World?.Claims;
            return claims != null && claims.Get(Pos)?.Length > 0
                   && claims.TestAccess(byPlayer, Pos, EnumBlockAccessFlags.BuildOrBreak) == EnumWorldAccessResponse.Granted;
        }

        /// <summary>The pane a "pane{n}" or "frame{n}-..." box belongs to, else -1 (the same reading as the click).</summary>
        static int PaneOfBox(string boxId)
        {
            if (boxId == null) return -1;
            if (boxId.StartsWith("pane", StringComparison.Ordinal) && int.TryParse(boxId.Substring(4), out int p)) return p;
            if (boxId.StartsWith("frame", StringComparison.Ordinal))
            {
                int dash = boxId.IndexOf('-');
                if (int.TryParse(dash > 5 ? boxId.Substring(5, dash - 5) : boxId.Substring(5), out int f)) return f;
            }
            return -1;
        }

        /// <summary>
        /// Server: latch a shut window, or unlatch it (<see cref="MayUnlatch"/>). The latch's click either way; a refusal says why.
        /// </summary>
        public void ToggleLatch(IPlayer byPlayer)
        {
            if (Api?.Side != EnumAppSide.Server || !CanLatch || byPlayer == null) return;
            var sp = byPlayer as IServerPlayer;
            if (_latched)
            {
                if (!MayUnlatch(byPlayer))
                {
                    sp?.SendIngameError("latched-notyours", Lang.Get("universaldisplaylib:latched-notyours", _latchedByName ?? "?"));
                    return;
                }
                _latched = false;
                _latchedBy = _latchedByName = null;
            }
            else
            {
                // only the panes it locks need to be shut - an inner door may stand open
                FrameBoxGroup[] sealing = SurfaceBehavior?.FrameBoxGroups ?? Array.Empty<FrameBoxGroup>();
                if (paneStates != null && Enumerable.Range(0, Math.Min(paneStates.Length, sealing.Length)).Any(i => paneStates[i] && sealing[i].SealsRoom))
                {
                    sp?.SendIngameError("latch-shutfirst", Lang.Get("universaldisplaylib:latch-shutfirst"));
                    return;
                }
                _latched = true;
                _latchedBy = byPlayer.PlayerUID;
                _latchedByName = byPlayer.PlayerName;
            }
            Api.World.PlaySoundAt(new AssetLocation(LatchSound), Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5, null, _latched ? 1.0f : 1.15f, 12f, 0.8f);
            MarkDirty(true);
        }

        void WriteLatch(ITreeAttribute tree)
        {
            if (!_latched) return;
            tree.SetBool("latched", true);
            if (_latchedBy != null) tree.SetString("latchedBy", _latchedBy);
            if (_latchedByName != null) tree.SetString("latchedByName", _latchedByName);
        }

        void ReadLatch(ITreeAttribute tree)
        {
            _latched = tree.GetBool("latched");
            _latchedBy = _latched ? tree.GetString("latchedBy") : null;
            _latchedByName = _latched ? tree.GetString("latchedByName") : null;
        }

        /// <summary>
        /// Client: THE THUD WHEN A WINDOW IS SHUT - the haunting's slam, softened: one knock of the barrel lid as a
        /// pane lands shut, timed to its close animation at the player's speed, at the open/close volume setting. EVERY
        /// window, sash and sliding too. Not for the haunting's or the wind's own moves (they are not ordinary closes).
        /// </summary>
        void PlayCloseThud(bool[] before)
        {
            if (capi == null || Pos == null || paneStates == null || UniversalDisplayLibConfig.Current?.CloseThud == false) return;
            float volume = 0.5f * (UniversalDisplayLibConfig.Current?.OpenCloseSoundVolumeValue ?? 1f);
            if (volume <= 0f) return;
            FrameBoxGroup[] groups = SurfaceBehavior?.FrameBoxGroups ?? Array.Empty<FrameBoxGroup>();
            for (int i = 0; i < paneStates.Length && i < groups.Length; i++)
            {
                bool was = before != null && i < before.Length && before[i];
                if (!was || paneStates[i]) continue;
                float speed = UniversalDisplayLibConfig.Current?.AnimationSpeedValue ?? 1f;
                if (groups[i].AnimSpeed > 0f) speed *= groups[i].AnimSpeed;
                int landMs = (int)Math.Clamp(AnimSeconds(CloseAnimFor(i), speed) * 1000f, 0f, 3000f);
                float pitch = 1.1f + (float)capi.World.Rand.NextDouble() * 0.1f;
                capi.Event.RegisterCallback(_ =>
                {
                    if (_removed || capi == null) return;
                    capi.World.PlaySoundAt(new AssetLocation(SlamSound), Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5, null, pitch, 16f, volume);
                }, landMs);
                return;   // one thud for the window, however many panes shut together
            }
        }

        /// <summary>
        /// Server: the glass RATTLING in its frame in the wind - a few quick, quiet clinks of glass, high and light,
        /// 50-110 ms apart. Only a block with glass (its materials' "glass" kind); latched ones too.
        /// </summary>
        public bool WindRattle(Random rnd)
        {
            if (_removed || Api?.Side != EnumAppSide.Server || GlassCodes.Count == 0) return false;
            double x = Pos.X + 0.5, y = Pos.Y + 0.5, z = Pos.Z + 0.5;
            // A BETTER RATTLE: the loose frame - the reed trap's shake, about 0.85 s of rattling, quiet and a little
            // high - with two or three short ceramic clinks on top for the glass
            Api.World.PlaySoundAt(new AssetLocation("game:sounds/block/reedtrapshake" + (1 + rnd.Next(3))), x, y, z, null,
                1.15f + (float)rnd.NextDouble() * 0.2f, 12f, 0.25f);
            int clinks = 2 + rnd.Next(2), delay = 60 + rnd.Next(80);
            for (int c = 0; c < clinks; c++)
            {
                float pitch = 1.7f + (float)rnd.NextDouble() * 0.4f, volume = 0.08f + (float)rnd.NextDouble() * 0.06f;
                RegisterDelayedCallback(_ =>
                {
                    if (!_removed) Api.World.PlaySoundAt(new AssetLocation("game:sounds/block/ceramichit"), x, y, z, null, pitch, 10f, volume);
                }, delay);
                delay += 120 + rnd.Next(160);
            }
            return true;
        }

        /// <summary>
        /// Server: a window creaking in the wind (WindWeather) - SHORT creaks only, quieter than the haunting's.
        /// Latched ones too.
        /// </summary>
        public bool WindCreak(Random rnd)
        {
            if (_removed || Api?.Side != EnumAppSide.Server || !CanOpen) return false;
            string[] creaks = { "woodcreak_1", "woodcreak_2", "woodcreak_3", "woodcreak_4" };
            Api.World.PlaySoundAt(new AssetLocation("game:sounds/block/creak/" + creaks[rnd.Next(creaks.Length)]),
                Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5, null, 0.9f + (float)rnd.NextDouble() * 0.2f, 16f, 0.4f + (float)rnd.NextDouble() * 0.1f);
            return true;
        }

        /// <summary>
        /// Whether pane <paramref name="pane"/> swings out of the wall. The shut pane's thinnest side across (X or Z,
        /// in the model) is the wall's depth; a pane that reaches more than 2 voxels further along it ANYWHERE ON ITS
        /// WAY OPEN swings - the whole sweep, not just where it ends, so an External Shutter folding flat against the
        /// wall counts. A sash or slider never leaves its depth. A pane with "windBlown" in its frameBoxGroup is what
        /// that says, whatever its model does.
        /// </summary>
        public bool WindPane(int pane)
        {
            FrameBoxGroup[] groups = SurfaceBehavior?.FrameBoxGroups ?? Array.Empty<FrameBoxGroup>();
            if (pane < 0 || pane >= groups.Length || string.IsNullOrEmpty(groups[pane].AnimOpen)) return false;
            if (groups[pane].WindBlown is bool written) return written;
            // an inner door (sealsRoom false - a display unit's) is out of the wind
            if (!groups[pane].SealsRoom) return false;
            if (_windPanes == null || _windPanesFor != Block.Id)
            {
                string[] anims = Enumerable.Range(0, groups.Length).Select(i => groups[i].AnimOpen ?? "").ToArray();
                Reach.PaneBoxes measured = Reach.PaneBoxes.For(Api, MeasuredBlock()?.Shape, anims);
                var swings = new bool[groups.Length];
                var folds = new bool[groups.Length];
                for (int i = 0; i < groups.Length && measured != null; i++)
                {
                    Cuboidf shut = i < measured.Shut.Length ? measured.Shut[i] : null;
                    Cuboidf open = i < measured.Open.Length ? measured.Open[i] : null;
                    if (shut == null || open == null) continue;
                    bool depthIsX = shut.X2 - shut.X1 < shut.Z2 - shut.Z1;
                    float shutDepth = depthIsX ? shut.X2 - shut.X1 : shut.Z2 - shut.Z1;
                    float most = 0f;
                    foreach (Cuboidf b in new[] { open }.Concat(i < measured.Sweep.Length ? measured.Sweep[i] : Array.Empty<Cuboidf>()))
                    {
                        float lo = Math.Min(depthIsX ? b.X1 : b.Z1, depthIsX ? shut.X1 : shut.Z1);
                        float hi = Math.Max(depthIsX ? b.X2 : b.Z2, depthIsX ? shut.X2 : shut.Z2);
                        most = Math.Max(most, hi - lo);
                    }
                    swings[i] = most - shutDepth > 2f / 16f;
                    // where it ENDS no deeper than shut, though it swings out on the way: it folds flat against the wall
                    float openDepth = Math.Max(depthIsX ? open.X2 : open.Z2, depthIsX ? shut.X2 : shut.Z2)
                                      - Math.Min(depthIsX ? open.X1 : open.Z1, depthIsX ? shut.X1 : shut.Z1);
                    folds[i] = swings[i] && openDepth - shutDepth <= 2f / 16f;
                }
                _windPanes = swings;
                _foldsFlat = folds;
                _windPanesFor = Block.Id;
            }
            return pane < _windPanes.Length && _windPanes[pane];
        }

        /// <summary>
        /// Server: open sky right beside the window - a space next to it on any side that is empty and that rain
        /// reaches. Indoor windows (between two rooms) and ones under a deep overhang never feel the wind.
        /// </summary>
        /// <param name="leavesCover">Leaves overhead count as cover - for rain and snow only, as the game has
        /// it.</param>
        public bool ExposedToWeather(out BlockPos outside, bool leavesCover = false)
        {
            outside = null;
            if (Api == null || Pos == null) return false;
            IBlockAccessor ba = Api.World.BlockAccessor;
            foreach (BlockFacing face in BlockFacing.HORIZONTALS)
            {
                BlockPos at = Pos.AddCopy(face);
                Block there = ba.GetBlock(at, BlockLayersAccess.Solid);
                if (there.Id != 0 && there.Replaceable < 6000 && ba.GetBlockEntity(at) is not BEAutoBoxesFiller) continue;
                if (!OpenToSky(ba, at, leavesCover)) continue;
                outside = at;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Nothing but sky - or tree leaves - above <paramref name="at"/>. The game's rain map stops at leaves, so a
        /// window with a tree over the space outside it counted as indoors: no frost, snow, rain, grime or wind. A
        /// roof, an overhang or anything but leaves still covers it.
        /// </summary>
        internal static bool OpenToSky(IBlockAccessor ba, BlockPos at, bool leavesCover = false)
        {
            int top = ba.GetRainMapHeightAt(at);
            if (top <= at.Y) return true;
            if (leavesCover) return false;   // rain and snow: the game's own rain map, leaves and all
            var up = at.Copy();
            for (int y = at.Y + 1; y <= top && y <= at.Y + MaxCanopyScan; y++)
            {
                up.Y = y;
                Block b = ba.GetBlock(up, BlockLayersAccess.Solid);
                if (b.Id != 0 && b.BlockMaterial != EnumBlockMaterial.Leaves && b.Replaceable < 6000) return false;
            }
            return top <= at.Y + MaxCanopyScan;   // a canopy higher than this: counted as cover
        }

        /// <summary>How far up a column is looked through for leaves (blocks).</summary>
        const int MaxCanopyScan = 48;
    }
}
