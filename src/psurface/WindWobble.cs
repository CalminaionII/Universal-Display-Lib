using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace UniversalDisplayLib
{
    /// <summary>
    /// WINDOWS WOBBLE IN THE WIND. In a strong breeze or a storm (WindWeather.StrengthAt, by the wind pattern) an
    /// unlatched window with open sky beside it rocks: a SHUT pane a little open and back - a strong breeze up to about
    /// a sixth of its swing, slowly; a storm up to about a third, quicker and gustier - and an OPEN pane a little back
    /// from fully open and out again. EVERY swinging pane of a window rocks, each in its own rhythm, external shutters'
    /// leaves too (WindPane). WIND DIRECTION: a shut window on the side the wind blows AWAY from is pulled outward and
    /// rocks the most, one facing INTO the wind is pressed against its frame (down to 40%); an open one is pushed back
    /// most when the wind blows straight at it.
    ///
    /// CLIENT ONLY, LOOKS ONLY. Each player's game works it out from the weather and the world clock - the same on
    /// every game - nothing is sent, the block is not swapped, the window stays as it is for rooms, boxes and
    /// everyone's clicks.
    ///
    /// HOW: a pane's animations are whole transitions - its "open" runs from the shut pose (frame 0) to open, its
    /// "close" from open to shut and is held at its end while shut. A wobbling pane is on its OPEN animation held all
    /// but still (speed 0.001) with the frame set by hand every rendered frame (WobbleRenderer) - so it can sit
    /// anywhere between shut and open. UpdateAnimationState leaves a wobbling pane alone (WobbleHolds). Settling: a
    /// shut pane eases shut and its close animation comes back already at its end; an open one eases fully open. A pane
    /// opened or shut meanwhile has its held animation given its ordinary speed back and the ordinary change goes on
    /// from there (ReleaseWobble). SETTLE FIRST : no wobble starts within SettleMs of a pane moving, nor while its own
    /// swing is still running.
    /// </summary>
    public partial class BEUniversalDisplay
    {
        /// <summary>One wobbling pane.</summary>
        sealed class PaneWobble
        {
            public int Pane;
            /// <summary>false: a SHUT pane rocking open from its frame; true: an OPEN pane rocking a little back.</summary>
            public bool Open;
            /// <summary>How far it is from where it rests (shut, or fully open), 0 to 1 of its swing.</summary>
            public float At;
            public bool Settling, Tapped = true, Creaked = true;
            /// <summary>The open animation held still - given its ordinary speed back when the wobble ends.</summary>
            public AnimationMetaData Meta;
            /// <summary>Its own sway: three phases and a tempo, from the window's position and the pane, the same on every game.</summary>
            public double P1, P2, P3, Tempo;
        }

        readonly Dictionary<int, PaneWobble> _wobbles = new();
        readonly List<PaneWobble> _wobbleFrameList = new();

        /// <summary>Client: the windows with a pane wobbling now, moved each frame by WobbleRenderer.</summary>
        internal static readonly HashSet<BEUniversalDisplay> Wobbling = new();

        const float WobbleRange = 32f;
        /// <summary>After a pane opens or shuts the wind leaves the window this long (and until its swing has finished).</summary>
        const long SettleMs = 1500;
        /// <summary>Client: when a pane last changed, from the sync.</summary>
        long _paneMovedAtMs = long.MinValue / 2;

        float _wobbleStrength;
        /// <summary>0.4 (the wind presses it shut) to 1 (the wind blows away from it, pulling it open).</summary>
        float _wobbleLee = 1f;
        long _wobbleCheckAt;

        /// <summary>
        /// Whether the wind gets at this window, and the open-sky space it comes from. BEHIND AN EXTERNAL SHUTTER: only
        /// while the shutter is open - the wind then comes round it, from the shutter's own open side; a shut shutter
        /// keeps the window still.
        /// </summary>
        bool WindReaches(out BlockPos outside, out BEUniversalDisplay shutter)
        {
            shutter = ShutterInFront();
            if (shutter != null)
            {
                outside = null;
                return shutter.paneStates.Any(open => open) && shutter.ExposedToWeather(out outside);
            }
            return ExposedToWeather(out outside);
        }

        /// <summary>
        /// ONE SHUTTER LEAF OPEN, THE OTHER SHUT: the window's panes that have a SHUT leaf of the shutter in front of
        /// them. Matched by where they are along the wall - each pane's shut box and each shut leaf's, turned with its
        /// block, in world blocks along the wall's width (the axis that is not its depth); overlapping by more than a
        /// voxel = behind it. Those panes settle and do not wobble.
        /// </summary>
        HashSet<int> PanesBehindShutLeaves(BEUniversalDisplay shutter)
        {
            if (shutter == null || shutter.paneStates == null) return null;
            bool? depthIsX = DepthIsX();
            if (depthIsX == null) return null;
            bool widthIsX = !depthIsX.Value;
            Cuboidf[] mine = ShutBoxes(), theirs = shutter.ShutBoxes();
            if (mine == null || theirs == null) return null;
            var blocked = new HashSet<int>();
            for (int i = 0; i < mine.Length; i++)
            {
                if (mine[i] == null) continue;
                (double lo, double hi) = WidthSpan(mine[i], Pos, widthIsX);
                for (int j = 0; j < theirs.Length && j < shutter.paneStates.Length; j++)
                {
                    if (theirs[j] == null || shutter.paneStates[j]) continue;   // only a SHUT leaf is in the way
                    (double tlo, double thi) = WidthSpan(theirs[j], shutter.Pos, widthIsX);
                    if (Math.Min(hi, thi) - Math.Max(lo, tlo) > 1 / 16.0) { blocked.Add(i); break; }
                }
            }
            return blocked;
        }

        /// <summary>Each pane's shut box turned with this block (in its own block, 0-1), or null.</summary>
        Cuboidf[] ShutBoxes()
        {
            FrameBoxGroup[] groups = SurfaceBehavior?.FrameBoxGroups ?? Array.Empty<FrameBoxGroup>();
            if (groups.Length == 0) return null;
            string[] anims = Enumerable.Range(0, groups.Length).Select(OpenAnimFor).ToArray();
            Reach.PaneBoxes measured = Reach.PaneBoxes.For(Api, MeasuredBlock()?.Shape, anims);
            if (measured?.Shut == null) return null;
            return measured.Shut.Select(b => b == null ? null : Turned(b)).ToArray();
        }

        /// <summary>A box's extent along the wall's width, in world blocks.</summary>
        static (double lo, double hi) WidthSpan(Cuboidf box, BlockPos at, bool widthIsX) =>
            widthIsX ? (at.X + box.X1, at.X + box.X2) : (at.Z + box.Z1, at.Z + box.Z2);

        /// <summary>Client tick: start, keep or stop the wobble from the weather (cheap - the motion is WobbleFrame's).</summary>
        void UpdateWobble()
        {
            if (capi == null || animUtil?.animator == null || Block == null || IsEmbedded) { EndAllWobbles(); return; }
            // opened or shut by someone - normally caught at once in UpdateAnimationState; this is the safety net
            if (_wobbles.Count > 0)
            {
                bool released = false;
                foreach (PaneWobble w in _wobbles.Values.ToArray())
                    if (w.Pane >= paneStates.Length || paneStates[w.Pane] != w.Open) { ReleaseWobble(w.Pane); released = true; }
                if (released) UpdateAnimationState();
            }

            // THE WEATHER IS LOOKED AT ONCE A SECOND, not every tick: the cheap tests first (latched, the setting, can
            // it open, how far), the block and climate lookups after
            long nowMs = capi.World.ElapsedMilliseconds;
            if (nowMs < _wobbleCheckAt) return;
            _wobbleCheckAt = nowMs + 1000 + capi.World.Rand.Next(250);

            float strength = -1f;
            BlockPos outside = null;
            BEUniversalDisplay shutter = null;
            bool can = !_latched && UniversalDisplayLibConfig.Current?.WindRocking != false && CanOpen
                       && capi.World.Player?.Entity?.Pos.SquareDistanceTo(Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5) <= WobbleRange * WobbleRange
                       && WindReaches(out outside, out shutter)
                       && (strength = WindWeather.StrengthAt(capi, Pos)) >= 0f;
            if (can) { _wobbleLee = LeeFactor(outside); _wobbleStrength = strength; }
            HashSet<int> blocked = can ? PanesBehindShutLeaves(shutter) : null;
            foreach (PaneWobble w in _wobbles.Values) w.Settling = !can || blocked?.Contains(w.Pane) == true;
            if (!can) return;
            if (capi.World.ElapsedMilliseconds - _paneMovedAtMs < SettleMs) return;   // just opened or shut: let it settle

            FrameBoxGroup[] groups = SurfaceBehavior?.FrameBoxGroups;
            if (groups == null) return;
            for (int i = 0; i < Math.Min(groups.Length, paneStates.Length); i++)
            {
                if (_wobbles.ContainsKey(i) || !WindPane(i) || string.IsNullOrEmpty(groups[i].AnimClose) || blocked?.Contains(i) == true) continue;
                PaneWobble w = paneStates[i] ? StartOpenWobble(i) : StartShutWobble(i);
                if (w == null) continue;
                // each pane its own sway, from the position and the pane - the same on every player's game
                w.P1 = PosSeed(1 + i * 10) / (double)uint.MaxValue * Math.PI * 2;
                w.P2 = PosSeed(2 + i * 10) / (double)uint.MaxValue * Math.PI * 2;
                w.P3 = PosSeed(3 + i * 10) / (double)uint.MaxValue * Math.PI * 2;
                w.Tempo = 0.85 + PosSeed(4 + i * 10) / (double)uint.MaxValue * 0.3;   // +-15%
                _wobbles[i] = w;
                Wobbling.Add(this);
            }
        }

        /// <summary>
        /// How much the wind pulls this window open: the wind's direction beside it against the way its open side faces.
        /// Blowing straight away from the window (out of the open side) 1, straight at it 0.4, across it 0.7; no direction
        /// to speak of, 1.
        /// </summary>
        float LeeFactor(BlockPos outside)
        {
            Vec3d wind = capi.World.BlockAccessor.GetWindSpeedAt(outside);
            double len = Math.Sqrt(wind.X * wind.X + wind.Z * wind.Z);
            if (len < 0.01) return 1f;
            double nx = outside.X - Pos.X, nz = outside.Z - Pos.Z;   // the way the open side faces
            double along = (wind.X * nx + wind.Z * nz) / len;          // 1 blowing away from the window, -1 into it
            return 0.4f + 0.6f * (float)((along + 1.0) / 2.0);
        }

        /// <summary>A shut pane: its close animation (finished) swapped for its open one, held still at the shut pose.</summary>
        PaneWobble StartShutWobble(int pane)
        {
            string open = OpenAnimFor(pane), close = CloseAnimFor(pane);
            if (string.IsNullOrEmpty(open)) return null;
            if (!string.IsNullOrEmpty(close) && FrameFraction(close) < 0.999f) return null;   // still swinging shut
            if (!string.IsNullOrEmpty(close) && animUtil.activeAnimationsByAnimCode.ContainsKey(close)) animUtil.StopAnimation(close);
            var meta = new AnimationMetaData
            {
                Animation = open,
                Code = open,
                AnimationSpeed = 0.001f,   // all but still: the frame is set by hand (WobbleFrame)
                EaseInSpeed = 1e7f,         // full weight on the first frame - the frame is the shut pose then
                EaseOutSpeed = 1e7f,
                Weight = 1f,
                BlendMode = EnumAnimationBlendMode.Add
            };
            animUtil.StartAnimation(meta);
            return new PaneWobble { Pane = pane, Open = false, Meta = meta };
        }

        /// <summary>An open pane: its open animation (finished, holding its last frame) held still and rocked back from it.</summary>
        PaneWobble StartOpenWobble(int pane)
        {
            string open = OpenAnimFor(pane);
            if (string.IsNullOrEmpty(open) || !animUtil.activeAnimationsByAnimCode.TryGetValue(open, out AnimationMetaData meta) || meta == null) return null;
            if (FrameFraction(open) < 0.999f) return null;   // still swinging open
            meta.AnimationSpeed = 0.001f;
            return new PaneWobble { Pane = pane, Open = true, Meta = meta };
        }

        /// <summary>
        /// UpdateAnimationState: true while pane <paramref name="pane"/> wobbles (its frame is set by hand). A pane opened or
        /// shut meanwhile is released here, at once, and false lets the ordinary change go on.
        /// </summary>
        bool WobbleHolds(int pane)
        {
            if (!_wobbles.TryGetValue(pane, out PaneWobble w)) return false;
            if (pane < paneStates.Length && paneStates[pane] == w.Open) return true;
            ReleaseWobble(pane);
            return false;
        }

        /// <summary>
        /// The pane was opened or shut while it wobbled : stopping and restarting the held animation does not take, so
        /// the same animation gets its ordinary speed back - a shut wobble runs on to fully open, an open one is
        /// replaced by the close as usual.
        /// </summary>
        void ReleaseWobble(int pane)
        {
            if (!_wobbles.Remove(pane, out PaneWobble w)) return;
            if (_wobbles.Count == 0) Wobbling.Remove(this);
            RestoreSpeed(w);
        }

        void RestoreSpeed(PaneWobble w)
        {
            if (w.Meta == null) return;
            FrameBoxGroup[] groups = SurfaceBehavior?.FrameBoxGroups;
            float speed = UniversalDisplayLibConfig.Current?.AnimationSpeedValue ?? 1f;
            if (groups != null && w.Pane < groups.Length && groups[w.Pane].AnimSpeed > 0f) speed *= groups[w.Pane].AnimSpeed;
            w.Meta.AnimationSpeed = speed;
            w.Meta.EaseInSpeed = 10000f / speed;
            w.Meta.EaseOutSpeed = 10000f / speed;
            w.Meta = null;
        }

        /// <summary>
        /// Stop one pane where it rests. Shut: the still open animation goes and the close animation comes back already at
        /// its end - the shut pose - so nothing visibly swings. Open: the open animation is put at its end and given its speed back.
        /// </summary>
        void EndWobble(int pane)
        {
            if (!_wobbles.Remove(pane, out PaneWobble w)) return;
            if (_wobbles.Count == 0) Wobbling.Remove(this);
            if (animUtil?.animator == null) return;
            string open = OpenAnimFor(pane), close = CloseAnimFor(pane);
            if (w.Open)
            {
                SetFrame(open, 1f);
                RestoreSpeed(w);
                return;
            }
            if (!string.IsNullOrEmpty(open) && animUtil.activeAnimationsByAnimCode.ContainsKey(open)) animUtil.StopAnimation(open);
            if (!string.IsNullOrEmpty(close))
            {
                animUtil.StartAnimation(new AnimationMetaData
                {
                    Animation = close,
                    Code = close,
                    AnimationSpeed = 1000f,   // straight to its end, the shut pose - as a snap on load
                    EaseInSpeed = 10f,
                    EaseOutSpeed = 10f,
                    Weight = 1f,
                    BlendMode = EnumAnimationBlendMode.Add
                });
                SetFrame(close, 1f);
            }
            UpdateAnimationState();
        }

        void EndAllWobbles()
        {
            foreach (int pane in _wobbles.Keys.ToArray()) EndWobble(pane);
        }

        /// <summary>A new animator (frost, a swap) or the block going: the held animations are gone with the old one.</summary>
        void ForgetWobbles()
        {
            _wobbles.Clear();
            Wobbling.Remove(this);
        }

        /// <summary>
        /// Client, every rendered frame, each wobbling pane. THE SAME ON EVERY PLAYER'S GAME: where the pane should be
        /// comes from the WORLD CLOCK (kept in step by the server) and its own phases - three slow waves added up, so
        /// it rests some of the time and is pushed by the wind the rest, faster in a storm. SHUT: pushed open by up to
        /// a sixth (a strong breeze) to a third (a storm) of its swing, times the wind direction, with a soft knock as
        /// it meets the frame. OPEN: pushed back by up to a twentieth to an eighth, most with the wind at its face. A
        /// small creak now and then as it moves off. It follows closely but smoothly, so nothing ever jumps.
        /// </summary>
        internal void WobbleFrame(float dt)
        {
            if (_removed || capi == null || _wobbles.Count == 0) return;
            float s = _wobbleStrength;
            double now = WorldSeconds();
            // a copy kept between frames (a pane may end its wobble inside the loop) - no new list every frame
            _wobbleFrameList.Clear();
            _wobbleFrameList.AddRange(_wobbles.Values);
            foreach (PaneWobble w in _wobbleFrameList)
            {
                float target = 0f;
                if (!w.Settling)
                {
                    double t = now * w.Tempo * (0.35 + 0.65 * s) * Math.PI * 2;
                    double wave = 0.45 * Math.Sin(t * 0.61 + w.P1) + 0.35 * Math.Sin(t * 1.13 + w.P2) + 0.2 * Math.Sin(t * 2.37 + w.P3);
                    float push = (float)Math.Clamp((wave + 0.15) / 1.15, 0, 1);   // resting below the middle, pushed above it
                    target = w.Open
                        ? (FoldsFlat(w.Pane) ? 0.06f + 0.16f * s : 0.05f + 0.07f * s) * (1.4f - _wobbleLee) * push   // open: the wind at its face pushes it back most; a shutter leaf further
                        : (0.16f + 0.18f * s) * _wobbleLee * push;
                }
                w.At += (target - w.At) * Math.Min(1f, (w.Settling ? 3f : 10f) * dt);

                // a small creak as it moves off from where it rests
                if (w.At < 0.01f) w.Creaked = false;
                else if (!w.Creaked && w.At > 0.03f)
                {
                    w.Creaked = true;
                    if (capi.World.Rand.NextDouble() < 0.5 && MayPlayWindSound())
                    {
                        string[] creaks = { "woodcreak_1", "woodcreak_2", "woodcreak_3", "woodcreak_4" };
                        capi.World.PlaySoundAt(new AssetLocation("game:sounds/block/creak/" + creaks[capi.World.Rand.Next(creaks.Length)]),
                            Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5, null, 1.05f + (float)capi.World.Rand.NextDouble() * 0.2f, 10f, 0.2f);
                    }
                }
                // a soft knock as a shut one meets its frame
                if (!w.Open)
                {
                    if (w.At > 0.05f) w.Tapped = false;
                    else if (!w.Tapped && w.At < 0.012f)
                    {
                        w.Tapped = true;
                        if (MayPlayWindSound())
                            capi.World.PlaySoundAt(new AssetLocation(SlamSound), Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5, null,
                                1.25f + (float)capi.World.Rand.NextDouble() * 0.2f, 12f, 0.18f);
                    }
                }
                // SHUTTERS BANG IN A STORM: an open leaf lying flat against the wall, swung out by a storm gust, slaps
                // back against the wall - a proper bang, heard further
                else if (s >= 0.5f && FoldsFlat(w.Pane))
                {
                    if (w.At > 0.08f) w.Tapped = false;
                    else if (!w.Tapped && w.At < 0.012f)
                    {
                        w.Tapped = true;
                        if (MayPlayWindSound())
                            capi.World.PlaySoundAt(new AssetLocation(SlamSound), Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5, null,
                                0.85f + (float)capi.World.Rand.NextDouble() * 0.1f, 24f, 0.45f);
                    }
                }

                if (w.Settling && w.At < 0.004f) { EndWobble(w.Pane); continue; }
                SetMovingFrame(OpenAnimFor(w.Pane), w.Open ? 1f - w.At : w.At);
            }
        }

        /// <summary>
        /// NO PILE-UP OF SOUND: the wind's sounds are the game's fire-and-forget ones (PlaySoundAt - the game frees
        /// each when it ends; nothing is held like the rain loop's ILoadedSound), and on top of that a player's game
        /// plays at most MaxWindSoundsPerSecond of them - a storm over a street of windows cannot stack up dozens at
        /// once.
        /// </summary>
        static bool MayPlayWindSound()
        {
            long second = Environment.TickCount64 / 1000;
            if (second != windSoundSecond) { windSoundSecond = second; windSoundsThisSecond = 0; }
            if (windSoundsThisSecond >= MaxWindSoundsPerSecond) return false;
            windSoundsThisSecond++;
            return true;
        }

        static long windSoundSecond;
        static int windSoundsThisSecond;
        const int MaxWindSoundsPerSecond = 6;

        /// <summary>How far through a running animation is, 0 to 1 (1 when it is not running).</summary>
        float FrameFraction(string code)
        {
            RunningAnimation[] running = animUtil?.animator?.Animations;
            if (running == null) return 1f;
            foreach (RunningAnimation ra in running)
            {
                if (ra?.Animation?.Code != code) continue;
                return ra.CurrentFrame / Math.Max(1, ra.Animation.QuantityFrames - 1);
            }
            return 1f;
        }

        /// <summary>
        /// Real seconds by the WORLD clock - the game's calendar, which the server keeps every player in step with (game
        /// hours turned back into real seconds by the speed of time, so the sway is not 30 times too fast).
        /// </summary>
        double WorldSeconds()
        {
            IGameCalendar cal = capi.World.Calendar;
            double gameSecondsPerReal = Math.Max(0.001, cal.SpeedOfTime * cal.CalendarSpeedMul);
            return cal.TotalHours * 3600.0 / gameSecondsPerReal;
        }

        /// <summary>A number from this window's position (FNV-1a by hand - .NET's own hash differs between game processes).</summary>
        uint PosSeed(int salt)
        {
            uint h = 2166136261;
            foreach (char c in Pos.X + "," + Pos.Y + "," + Pos.Z + "|" + salt) { h ^= c; h *= 16777619; }
            return h;
        }

        /// <summary>
        /// Put a running animation at <paramref name="fraction"/> of the part of it where something MOVES - an
        /// animation that holds still at its start or end (the awning's top pane does nothing for its first 5 of 30
        /// frames) would otherwise not wobble at all near that end.
        /// </summary>
        void SetMovingFrame(string code, float fraction)
        {
            RunningAnimation[] running = animUtil?.animator?.Animations;
            if (running == null || string.IsNullOrEmpty(code)) return;
            foreach (RunningAnimation ra in running)
            {
                if (ra?.Animation?.Code != code) continue;
                (float start, float end) = MovingRange(ra.Animation);
                ra.CurrentFrame = start + GameMath.Clamp(fraction, 0f, 1f) * (end - start);
                return;
            }
        }

        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Animation, Tuple<float, float>> movingRanges = new();

        /// <summary>
        /// The frames between which an animation actually moves: from the last keyframe that still holds the first one's
        /// pose to the first keyframe that already holds the last one's. Worked out once per animation.
        /// </summary>
        static (float start, float end) MovingRange(Animation anim)
        {
            Tuple<float, float> range = movingRanges.GetValue(anim, a =>
            {
                float last = Math.Max(1, a.QuantityFrames - 1);
                AnimationKeyFrame[] keys = a.KeyFrames?.OrderBy(k => k.Frame).ToArray();
                if (keys == null || keys.Length < 2) return Tuple.Create(0f, last);
                string first = PoseOf(keys[0]), final = PoseOf(keys[^1]);
                float start = 0f, end = last;
                for (int k = 1; k < keys.Length && PoseOf(keys[k]) == first; k++) start = keys[k].Frame;
                for (int k = keys.Length - 2; k >= 0 && PoseOf(keys[k]) == final; k--) end = keys[k].Frame;
                if (end <= start) return Tuple.Create(0f, last);
                return Tuple.Create(start, end);
            });
            return (range.Item1, range.Item2);
        }

        /// <summary>A keyframe's pose as text, for comparing two: every element's offset, turn and stretch (unset = 0).</summary>
        static string PoseOf(AnimationKeyFrame key)
        {
            if (key?.Elements == null) return "";
            var sb = new System.Text.StringBuilder();
            foreach (var pair in key.Elements.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                AnimationKeyFrameElement e = pair.Value;
                if (e == null) continue;
                sb.Append(pair.Key).Append(':')
                  .Append(Math.Round(e.OffsetX ?? 0, 3)).Append(',').Append(Math.Round(e.OffsetY ?? 0, 3)).Append(',').Append(Math.Round(e.OffsetZ ?? 0, 3)).Append(',')
                  .Append(Math.Round(e.RotationX ?? 0, 3)).Append(',').Append(Math.Round(e.RotationY ?? 0, 3)).Append(',').Append(Math.Round(e.RotationZ ?? 0, 3)).Append(',')
                  .Append(Math.Round(e.StretchX ?? 1, 3)).Append(',').Append(Math.Round(e.StretchY ?? 1, 3)).Append(',').Append(Math.Round(e.StretchZ ?? 1, 3)).Append(';');
            }
            return sb.ToString();
        }

        /// <summary>Put a running animation at <paramref name="fraction"/> of its length (0 first frame, 1 last).</summary>
        void SetFrame(string code, float fraction)
        {
            RunningAnimation[] running = animUtil?.animator?.Animations;
            if (running == null || string.IsNullOrEmpty(code)) return;
            foreach (RunningAnimation ra in running)
            {
                if (ra?.Animation?.Code != code) continue;
                int frames = Math.Max(1, ra.Animation.QuantityFrames - 1);
                ra.CurrentFrame = GameMath.Clamp(fraction, 0f, 1f) * frames;
                return;
            }
        }
    }

    /// <summary>Client: moves every wobbling window once a frame (BEUniversalDisplay.WobbleFrame), before they are drawn.</summary>
    public sealed class WobbleRenderer : IRenderer
    {
        public double RenderOrder => 0.0;
        public int RenderRange => 64;

        readonly List<BEUniversalDisplay> frameList = new();

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (BEUniversalDisplay.Wobbling.Count == 0) return;
            // a copy kept between frames (a window may stop wobbling inside the loop) - no new array every frame
            frameList.Clear();
            frameList.AddRange(BEUniversalDisplay.Wobbling);
            foreach (BEUniversalDisplay be in frameList) be.WobbleFrame(deltaTime);
        }

        public void Dispose() { }
    }
}
