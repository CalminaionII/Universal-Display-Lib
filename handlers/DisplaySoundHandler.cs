using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace UniversalDisplayLib
{
    internal class DisplaySoundHandler
    {
        // Typed as BlockEntity, not BEWindowStorageLib: this only ever touches Pos,
        // Block and Api, so both the legacy and the psurface block entities can share
        // one implementation rather than the psurface path growing a copy.
        private readonly BlockEntity _be;
        private readonly ICoreClientAPI _capi;

        private static readonly Dictionary<string, (BlockPos owner, ILoadedSound sound)> SharedSounds = new();

        private const float FadeSpeed = 1.5f;
        private const float SmoothSpeed = 0.08f;

        public DisplaySoundHandler(BlockEntity be, ICoreClientAPI capi)
        {
            _be = be;
            _capi = capi;
        }

        /// <summary>
        /// A sound named by the block's own JSON, or the given fallback.
        ///
        /// **This is what makes the lib usable for something that is not a window.** A cabinet
        /// does not swoosh and a drawer does not sound like rain on glass, so every sound the
        /// lib plays is nameable per block:
        ///
        /// <code>
        /// attributes: {
        ///   "displaySounds": {
        ///     "toggle": "game:sounds/block/door",       // open / close
        ///     "rain":   "game:sounds/environment/rain"  // while rained on
        ///   }
        /// }
        /// </code>
        ///
        /// Absent keys keep the sounds this mod has always used, so existing content is
        /// unaffected by the whole feature.
        ///
        /// **A domain-less path resolves in the DECLARING BLOCK'S domain, not `game:`** — the
        /// gotcha that once made the custom window silent. That is the right default here: a
        /// modder writing `"sounds/block/mine"` means their own asset. Write `game:` explicitly
        /// for a vanilla one.
        /// </summary>
        private AssetLocation SoundFor(string key, string fallback)
            => SoundFor(_be?.Block, key, fallback);

        /// <summary>
        /// The static form, so the block entity and the swap helper resolve sounds through the
        /// SAME code rather than each growing a copy of the lookup — a value derived in more
        /// than one place is this codebase's named recurring bug.
        /// </summary>
        public static AssetLocation SoundFor(Block block, string key, string fallback)
            => DeclaredSound(block, key) ?? new AssetLocation(fallback);

        /// <summary>
        /// The sound a block ASKED for, or null when it named none — no fallback.
        ///
        /// **This is what makes rain opt-in.** Every other sound has a sensible default: a
        /// block that opens should make some noise even if its author never thought about it.
        /// Rain does not work that way — patter on glass is a property of a WINDOW, and a
        /// cabinet or a crate has no business playing it just because nobody said otherwise.
        /// So the rain path asks this, and silence is the answer for anything that has not
        /// declared a rain sound.
        ///
        /// That retired the old <c>disableRainSound</c> attribute: an opt-OUT only made sense
        /// while the sound was on by default. Deleting the `rain` line is now the off switch.
        /// </summary>
        public static AssetLocation DeclaredSound(Block block, string key)
        {
            string declared = block?.Attributes?["displaySounds"]?[key]?.AsString(null);
            if (string.IsNullOrWhiteSpace(declared)) return null;

            return AssetLocation.Create(declared, block.Code?.Domain ?? "game");
        }

        /// <summary>
        /// Plays the open/close sound at the block's position — a swoosh unless the block
        /// names something else.
        /// </summary>
        public void PlaySlideSound()
        {
            if (_capi == null) return;
            _capi.World.PlaySoundAt(
                SoundFor("toggle", "game:sounds/effect/swoosh"),
                _be.Pos.X + 0.5f, _be.Pos.Y + 0.5f, _be.Pos.Z + 0.5f,
                null, randomizePitch: true, range: 16f, volume: 0.8f
            );
        }

        /// <summary>
        /// Called from OnClientTick — manages the shared rain sound.
        /// </summary>
        public void Tick()
        {
            TickSharedRainSound();
        }

        /// <summary>
        /// Fades the shared rain sound toward its current target volume.
        /// Called from the FromTreeAttributes enqueue so volume stays correct
        /// after a network sync.
        /// </summary>
        public void FadeSharedSoundsToCurrentTarget()
        {
            if (SharedSounds.TryGetValue("rain", out var rainEntry)
                && rainEntry.sound != null && !rainEntry.sound.IsDisposed && rainEntry.sound.IsPlaying)
            {
                float target = ComputeTargetRainVolume(rainEntry.owner);
                rainEntry.sound.FadeTo(target, FadeSpeed, (_) => { });
            }
        }

        /// <summary>
        /// Drops the shared rain sound outright. Called from <c>StartClientSide</c>, because
        /// <see cref="SharedSounds"/> is a static and statics outlive a world.
        ///
        /// The entry is normally removed by the owning window's <see cref="Dispose"/> on
        /// unload, so this is usually a no-op — but ONE window owns the sound and every other
        /// window's tick bails out early while that entry looks alive
        /// (<c>existing.owner != _be.Pos</c>, so nobody else may stop it). Carry a stale entry
        /// into a second world and its <c>owner</c> is a position no window there matches, so
        /// the rain sound can neither be stopped nor replaced for the rest of the session.
        ///
        /// Same trap as <c>PlacementRotation.Local</c>, <c>ForceFootprintBox</c> and
        /// <c>LiveClientInstances</c>, all cleared in the same place.
        /// </summary>
        public static void ResetShared()
        {
            foreach (var entry in SharedSounds.Values)
            {
                // The old world's audio system is already gone, so this can throw. Swallowed
                // rather than logged: there is no api to log through at this point and a
                // failure to tidy up a dead sound must not stop the client starting.
                try
                {
                    if (entry.sound != null && !entry.sound.IsDisposed)
                    {
                        entry.sound.Stop();
                        entry.sound.Dispose();
                    }
                }
                catch (Exception) { }
            }

            SharedSounds.Clear();
        }

        /// <summary>
        /// Stops and disposes any rain sound owned by this window.
        /// Called from CleanupClient.
        /// </summary>
        public void Dispose()
        {
            if (SharedSounds.TryGetValue("rain", out var rainEntry) && rainEntry.owner == _be.Pos)
            {
                TryDisposeSound(rainEntry.sound);
                SharedSounds.Remove("rain");
            }
        }

        // ── Private ──────────────────────────────────────────────────────────

        private void TickSharedRainSound()
        {
            // Volume 0 is the off switch — the separate EnableRainSound flag said the
            // same thing twice, so it went with the config cleanup.
            bool shouldPlay = UniversalDisplayLibConfig.Current.RainSoundVolume > 0
                && DeclaredSound(_be?.Block, "rain") != null
                && IsRainedOn(_be.Pos)
                && PlayerNearThisBlock((float)UniversalDisplayLibConfig.Current.RainSoundRange);

            if (SharedSounds.TryGetValue("rain", out var existing))
            {
                bool soundAlive = existing.sound != null && !existing.sound.IsDisposed && existing.sound.IsPlaying;

                if (soundAlive)
                {
                    existing.sound.SetPosition(_capi.World.Player.Entity.Pos.XYZFloat);

                    float target = ComputeTargetRainVolume(existing.owner);
                    existing.sound.SetVolume(GameMath.Lerp(existing.sound.Params.Volume, target, SmoothSpeed));

                    if (!shouldPlay && existing.owner == _be.Pos)
                    {
                        existing.sound.FadeOutAndStop(FadeSpeed);
                        SharedSounds.Remove("rain");
                    }
                    return;
                }

                TryDisposeSound(existing.sound);
                SharedSounds.Remove("rain");
            }

            if (!shouldPlay) return;

            ILoadedSound newSound = _capi.World.LoadSound(new SoundParams()
            {
                Location = DeclaredSound(_be?.Block, "rain"),
                ShouldLoop = true,
                Position = _capi.World.Player.Entity.Pos.XYZFloat,
                DisposeOnFinish = false,
                Volume = ComputeTargetRainVolume(_be.Pos),
                Pitch = 1.0f
            });

            if (newSound != null)
            {
                newSound.Start();
                SharedSounds["rain"] = (_be.Pos, newSound);
            }
        }

        private bool PlayerNearThisBlock(float range)
        {
            if (_capi == null) return false;
            Vec3d playerPos = _capi.World.Player.Entity.Pos.XYZ;
            double dx = playerPos.X - (_be.Pos.X + 0.5);
            double dy = playerPos.Y - (_be.Pos.Y + 0.5);
            double dz = playerPos.Z - (_be.Pos.Z + 0.5);
            return (dx * dx + dy * dy + dz * dz) <= (range * range);
        }

        private bool IsRainedOn(BlockPos pos)
        {
            if (_capi == null) return false;
            ClimateCondition cond = _capi.World.BlockAccessor.GetClimateAt(pos, EnumGetClimateMode.NowValues);
            if (cond == null || cond.Rainfall <= 0.1f || cond.Temperature <= 3f) return false;
            return _capi.World.BlockAccessor.GetRainMapHeightAt(pos) <= pos.Y ||
                   _capi.World.BlockAccessor.GetDistanceToRainFall(pos, 3) <= 2;
        }

        private float ComputeTargetRainVolume(BlockPos ownerPos)
        {
            ClimateCondition cond = _capi.World.BlockAccessor.GetClimateAt(ownerPos, EnumGetClimateMode.NowValues);
            float intensity = Math.Clamp(cond?.Rainfall ?? 0f, 0f, 1.5f);
            return Math.Clamp(UniversalDisplayLibConfig.Current.RainSoundVolumeValue * intensity, 0f, 2f);
        }

        private void TryDisposeSound(ILoadedSound sound)
        {
            if (sound == null) return;
            try
            {
                if (!sound.IsDisposed)
                {
                    sound.Stop();
                    sound.Dispose();
                }
            }
            catch (Exception e)
            {
                _be.Api?.Logger.Warning($"[UniversalDisplayLib] Error disposing sound: {e.Message}");
            }
        }
    }
}