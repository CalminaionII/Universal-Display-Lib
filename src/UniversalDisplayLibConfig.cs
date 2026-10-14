using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace UniversalDisplayLib
{
    public class UniversalDisplayLibConfig
    {
        // ====================== Rain ======================
        [Category("Rain")]
        [Display(Name = "Rain Comment", Description = "Rain heard through the block, for blocks that declare a rain sound. 0 = off, 1 = very quiet, 5 = normal, 10 = loud")]
        public string RainComment { get; set; } = "Rain heard through the block, for blocks that declare a rain sound. 0 = off, 1 = very quiet, 5 = normal, 10 = loud";


        [Category("Rain")]
        [Display(Name = "Volume", Description = "Rain heard through the block, for blocks that declare a rain sound. 0 = off, 1 = very quiet, 5 = normal, 10 = loud")]
        [Range(0, 10)]
        public int RainSoundVolume { get; set; } = 5;

        [Category("Rain")]
        [Display(Name = "Rain Sound Range Comment", Description = "How many blocks away you can hear rain on a display block (1 to 64)")]
        public string RainSoundRangeComment { get; set; } = "How many blocks away you can hear rain on a display block (1 to 64)";

        [Category("Rain")]
        [Display(Name = "Range", Description = "How many blocks away you can hear rain on a display block (1 to 64)")]
        [Range(1, 64)]
        public int RainSoundRange { get; set; } = 12;

        // ====================== Open / close sound (2026-10-12: a player asked for a way to turn it off) ======================
        [Category("Sounds")]
        [Display(Name = "Open Close Sound Comment", Description = "The sound a display block makes when you open or close it. 0 = off, 1 = very quiet, 5 = normal, 10 = loud")]
        public string OpenCloseSoundComment { get; set; } = "The sound a display block makes when you open or close it. 0 = off, 1 = very quiet, 5 = normal, 10 = loud";

        [Category("Sounds")]
        [Display(Name = "Open / close volume", Description = "The sound a display block makes when you open or close it. 0 = off, 1 = very quiet, 5 = normal, 10 = loud")]
        [Range(0, 10)]
        public int OpenCloseSoundVolume { get; set; } = 5;

        // ====================== Animation ======================
        [Category("Animation")]
        [Display(Name = "Animation Comment", Description = "1 = very slow, 3 = normal, 5 = very fast")]
        public string AnimationComment { get; set; } = "1 = very slow, 3 = normal, 5 = very fast";

        [Category("Animation")]
        [Display(Name = "Speed", Description = "1 = very slow, 3 = normal, 5 = very fast")]
        [Range(1, 5)]
        public int AnimationSpeed { get; set; } = 3;

    
        // ====================== Placement rotation ======================
        [Category("Placement Rotation")]
        [Display(Name = "Rotation Comment", Description = "Rotation Degrees (5 to 90)")]
        public string RotationComment { get; set; } = "Rotation Degrees (5 to 90)";

        [Category("Placement Rotation")]
        [Display(Name = "Degrees", Description = "Rotation Degrees (5 to 90)")]
        [Range(5, 90)]
        public int RotationStepDegrees { get; set; } = 15;

        [Category("Placement Rotation")]
        [Display(Name = "Placement Jitter Comment", Description = "Slight random resting ANGLE on placed items. false = everything sits perfectly square")]
        public string PlacementJitterComment { get; set; } = "Slight random resting ANGLE on placed items. false = everything sits perfectly square";

        [Category("Placement Rotation")]
        [Display(Name = "Placement Jitter", Description = "Slight random resting ANGLE on placed items. false = everything sits perfectly square")]
        public bool PlacementJitter { get; set; } = true;

        // ====================== Placement height ======================
        [Category("Placement Height")]
        [Display(Name = "Height Comment", Description = "Sneak + mouse wheel raises or lowers an item. Step in voxels (0.05 to 4), and how far it may move (1 to 16)")]
        public string HeightComment { get; set; } = "Sneak + mouse wheel raises or lowers an item. Step in voxels (0.05 to 4), and how far it may move (1 to 16)";

        /// <summary>
        /// Voxels per wheel notch. A quarter voxel by default: items land on the right
        /// level by themselves now, so this exists for the last touch of tidying rather
        /// than for moving something any real distance.
        /// </summary>
        [Category("Placement Height")]
        [Display(Name = "Height Step Voxels", Description = "Voxels per wheel notch when adjusting an item's height (0.05 to 4)")]
        [Range(0.05, 4)]
        public float HeightStepVoxels { get; set; } = 0.25f;

        /// <summary>
        /// How far from its natural resting height an item may be nudged, either way.
        /// A limit rather than free rein: without one an item can be pushed down through
        /// the sill or floated off the top of the window, and neither is recoverable
        /// except by taking it back out.
        /// </summary>
        [Category("Placement Height")]
        [Display(Name = "Height Limit Voxels", Description = "How far up or down an item may be nudged from where it would sit (1 to 16)")]
        [Range(1, 16)]
        public float HeightLimitVoxels { get; set; } = 8f;

        // ====================== Placement rules ======================
        [Category("Placement Rules")]
        [Display(Name = "Precise Placement Comment", Description = "Half-voxel placement spots - 32 across a block instead of 16, which is one pixel of a 32x32 texture. Needs a restart to take effect")]
        public string PrecisePlacementComment { get; set; } = "Half-voxel placement spots - 32 across a block instead of 16, which is one pixel of a 32x32 texture. Needs a restart to take effect";

        /// <summary>
        /// Half-voxel placement spots instead of whole-voxel ones — 32 across a block rather
        /// than 16, which is one pixel of a 32x32 texture and therefore the finest distinction
        /// a player can actually see. There is deliberately no finer setting: doubling again
        /// would be sixteen times the boxes for a difference nobody can perceive.
        ///
        /// **A RENAME, NOT A RETYPE.** This replaces an int `PlacementStepsPerVoxel` that
        /// briefly existed. Changing that property's TYPE in place would throw while
        /// deserialising the old number into a bool, and `Load()` catches that by falling back
        /// to defaults — silently wiping every other setting the player had. `PlacementBox`
        /// taught this once already; the fix both times is a new name, so the stale key is
        /// simply ignored.
        ///
        /// **Costs boxes quadratically** — 4x, so a display unit goes from 308 pads to about
        /// 1,230. Load-tested 2026-08-06 at ~50 windows with items: no measurable memory
        /// growth and no creep across chunk reloads. Rebuilds cost ~0.5 ms and fire on
        /// invalidation, not per frame.
        ///
        /// **Read once, when the grid is built at block load**, so a change needs a restart.
        ///
        /// SAFE TO TURN OFF. The pad grid and an item's own box come from different places —
        /// pads from BuildGridBoxes, a placed item's box from its stored slot id — so items
        /// already sitting on half-voxel spots keep their boxes and stay clickable with this
        /// off. The id is a plain string carrying a float, so nothing needs migrating either
        /// way.
        /// </summary>
        [Category("Placement Rules")]
        [Display(Name = "Precise Placement", Description = "Half-voxel placement spots - 32 across a block instead of 16. Needs a restart")]
        public bool PrecisePlacement { get; set; } = true;

        [Category("Placement Rules")]
        [Display(Name = "Allow Item Overlap Comment", Description = "Let items overlap each other on a surface so they can be packed tight. Stacking is unaffected and items still have to stay on the surface. Overlapping items are fiddlier to pick back up")]
        public string AllowItemOverlapComment { get; set; } = "Let items overlap each other on a surface so they can be packed tight. Stacking is unaffected and items still have to stay on the surface. Overlapping items are fiddlier to pick back up";

        /// <summary>
        /// Stops item-to-item collision REFUSING a placement. It does not stop it being
        /// detected — that same test is what decides an item should stack, so switching the
        /// detection off would quietly end stacking altogether.
        ///
        /// The reasoning is the one that made rotation free: the rule existed because nothing
        /// SHOWED the player the problem, and the ghost now does. Bounds are untouched, so
        /// items still cannot leave the surface.
        ///
        /// **Server-side in effect**, like every other placement rule here — `TryPut` runs on
        /// both sides, so this has to match or the two disagree about what is legal.
        /// </summary>
        [Category("Placement Rules")]
        [Display(Name = "Allow Item Overlap", Description = "Let items overlap each other on a surface so they can be packed tight. Stacking is unaffected and items still have to stay on the surface. Overlapping items are fiddlier to pick back up")]
        public bool AllowItemOverlap { get; set; } = false;

        // ====================== Placement preview ======================
        [Category("Placement Preview")]
        [Display(Name = "Placement Ghost Comment", Description = "Show a see-through copy of the held item where it would land. The outline box is shown either way")]
        public string PlacementGhostComment { get; set; } = "Show a see-through copy of the held item where it would land. The outline box is shown either way";

        /// <summary>
        /// The kill switch for <c>PlacementGhostRenderer</c>. This is the only thing in the
        /// mod that draws per frame, so it is the only thing that can cost frames or take
        /// the client down — turning it off leaves the wireframe box exactly as it was and
        /// removes the renderer from the render loop entirely, with no rebuild.
        /// </summary>
        [Category("Placement Preview")]
        [Display(Name = "Placement Ghost", Description = "Show a see-through copy of the held item where it would land. The outline box is shown either way")]
        public bool PlacementGhost { get; set; } = true;

        [Category("Placement Preview")]
        [Display(Name = "Placement Ghost Opacity Comment", Description = "How solid the ghost item looks, 1 (barely visible) to 10 (solid)")]
        public string PlacementGhostOpacityComment { get; set; } = "How solid the ghost item looks, 1 (barely visible) to 10 (solid)";

        [Category("Placement Preview")]
        [Display(Name = "Placement Ghost Opacity", Description = "How solid the ghost item looks, 1 (barely visible) to 10 (solid)")]
        [Range(1, 10)]
        public int PlacementGhostOpacity { get; set; } = 5;

        [Category("Placement Preview")]
        [Display(Name = "Show Placement Box Comment", Description = "Outline showing where the held item will land. false hides it and leaves just the ghost")]
        public string ShowPlacementBoxComment { get; set; } = "Outline showing where the held item will land. false hides it and leaves just the ghost";

        /// <summary>
        /// The box and the ghost overlap in what they tell you, but not completely: the
        /// ghost shows the ITEM, the box shows the FOOTPRINT, and those genuinely differ —
        /// seashells declare a deliberately tight box so more fit along a sill, and the
        /// ruler is 12.8 wide but paper thin. Turning the box off is a real choice rather
        /// than pure tidying, so it is a setting rather than a decision made for everyone.
        ///
        /// RENAMED from an int `PlacementBox` (0/1/2) on 2026-07-30. Renaming rather than
        /// changing the type on purpose: an existing file holds `"PlacementBox": 2`, and
        /// deserialising 2 into a bool throws — which Load() catches by falling back to
        /// defaults, silently resetting every other setting the player had. A new NAME is
        /// simply an added key and a removed one, which SchemaDiffers reconciles by design.
        /// </summary>
        [Category("Placement Preview")]
        [Display(Name = "Show Placement Box", Description = "Outline showing where the held item will land. false hides it and leaves just the ghost")]
        // OFF by default from 1.0.3. The ghost shows the item where it will land and reddens
        // the same way, so the outline is a second opinion most players do not need — and the
        // author's own verdict on hiding them was that it "makes it feel like there is true
        // free placement". Still one switch away for anyone judging a tight fit.
        public bool ShowPlacementBox { get; set; } = false;

        [Category("Placement Preview")]
        [Display(Name = "Show Placed Item Box Comment", Description = "Outline around an item already on the sill when you aim at it. false hides it")]
        public string ShowPlacedItemBoxComment { get; set; } = "Outline around an item already on the sill when you aim at it. false hides it";

        /// <summary>
        /// Separate from <see cref="ShowPlacementBox"/> because they answer different questions:
        /// that one is "where will this go", this one is "what am I pointing at".
        ///
        /// Worth hiding for a reason the placement box does not have: a selection box is a
        /// `Cuboidf`, which is axis-aligned by definition, so it CANNOT follow a freely
        /// rotated item. At any angle off a quarter turn the outline visibly disagrees with
        /// the item inside it, and that mismatch is the whole argument for turning it off.
        ///
        /// The cost is losing the only indication of which item you are about to take or
        /// turn, which is why it defaults to true and has a toggle key.
        /// </summary>
        [Category("Placement Preview")]
        [Display(Name = "Show Placed Item Box", Description = "Outline around an item already on the sill when you aim at it. false hides it")]
        public bool ShowPlacedItemBox { get; set; } = false;


        [Category("General")]
        [Display(Name = "Room Safe Opening Comment", Description = "Close windows before changing, if true will disable open/closed states")]
        public string RoomSafeOpeningComment { get; set; } = "Close windows before changing, if true will disable open/closed states";

        [Category("General")]
        [Display(Name = "Room Safe Opening", Description = "Close windows before changing, if true will disable open/closed states")]
        public bool RoomSafeOpening { get; set; } = false;

        // ====================== Computed values (hidden) ======================
        [Newtonsoft.Json.JsonIgnore]
        [Browsable(false)]
        public float RainSoundVolumeValue => Math.Clamp(RainSoundVolume, 0, 10) * 0.1f;

        /// <summary>The open/close sound's volume as a factor: 5 (the default) = as it always was, 0 = silent.</summary>
        [Newtonsoft.Json.JsonIgnore]
        [Browsable(false)]
        public float OpenCloseSoundVolumeValue => Math.Clamp(OpenCloseSoundVolume, 0, 10) * 0.2f;

        [Newtonsoft.Json.JsonIgnore]
        [Browsable(false)]
        public float PlacementGhostAlpha => Math.Clamp(PlacementGhostOpacity, 1, 10) * 0.1f;

        [Newtonsoft.Json.JsonIgnore]
        [Browsable(false)]
        public float AnimationSpeedValue => AnimationSpeed switch
        {
            1 => 0.25f,
            2 => 0.5f,
            3 => 1.0f,
            4 => 2.0f,
            5 => 4.0f,
            _ => 1.0f
        };


        public static UniversalDisplayLibConfig Current { get; private set; }

        /// <summary>The pre-rename filename. Left as a literal — it is history, not a setting.</summary>
        private const string LegacyConfigFilename = "windowdisplaylibconfig.json";

        /// <summary>
        /// Moves a pre-rename config across, then removes it, so a player upgrading keeps
        /// every setting they had and is not left with a stale file nothing reads.
        ///
        /// **Only when the new file does not exist.** Otherwise a player who had already
        /// configured the renamed mod would have it overwritten by an old file the next time
        /// they launched.
        ///
        /// Failure here must never stop the mod loading — the worst case is a player retyping
        /// a few settings, which is not worth a crash on startup, so everything is caught and
        /// logged. Deletion is attempted separately for the same reason: a config that
        /// migrated but could not be deleted is fine, and throwing there would undo nothing.
        /// </summary>
        private static void MigrateLegacyConfig(ICoreAPI api, string configFilename)
        {
            try
            {
                string newPath = System.IO.Path.Combine(GamePaths.ModConfig, configFilename);
                string oldPath = System.IO.Path.Combine(GamePaths.ModConfig, LegacyConfigFilename);

                if (System.IO.File.Exists(newPath) || !System.IO.File.Exists(oldPath)) return;

                var legacy = api.LoadModConfig<UniversalDisplayLibConfig>(LegacyConfigFilename);
                if (legacy == null) return;

                api.StoreModConfig(legacy, configFilename);
                api.Logger.Notification(
                    "[UniversalDisplayLib] Carried your settings over from {0} to {1} after the mod rename.",
                    LegacyConfigFilename, configFilename);

                try
                {
                    System.IO.File.Delete(oldPath);
                }
                catch (Exception e)
                {
                    api.Logger.Notification(
                        "[UniversalDisplayLib] Settings were carried over, but {0} could not be removed ({1}). It is no longer read and is safe to delete.",
                        LegacyConfigFilename, e.Message);
                }
            }
            catch (Exception e)
            {
                api.Logger.Warning(
                    "[UniversalDisplayLib] Could not carry settings over from {0}, starting from defaults instead: {1}",
                    LegacyConfigFilename, e.Message);
            }
        }

        /// <summary>
        /// Hold every numeric inside the range the sliders and the code both assume.
        ///
        /// EXTRACTED so Reload can use it too. A second copy of this list would drift, and
        /// the config UI's slider bounds are generated from these same numbers - see
        /// assets/universaldisplaylib/config/configlib-patches.json.
        /// </summary>
        private static void ApplyBounds()
        {
            if (Current == null) return;

            Current.RainSoundVolume = Math.Clamp(Current.RainSoundVolume, 0, 10);
            Current.RainSoundRange = Math.Clamp(Current.RainSoundRange, 1, 64);
            Current.OpenCloseSoundVolume = Math.Clamp(Current.OpenCloseSoundVolume, 0, 10);
            Current.AnimationSpeed = Math.Clamp(Current.AnimationSpeed, 1, 5);
            Current.RotationStepDegrees = Math.Clamp(Current.RotationStepDegrees, 5, 90);
            Current.HeightStepVoxels = Math.Clamp(Current.HeightStepVoxels, 0.05f, 4f);
            Current.HeightLimitVoxels = Math.Clamp(Current.HeightLimitVoxels, 1f, 16f);
            Current.PlacementGhostOpacity = Math.Clamp(Current.PlacementGhostOpacity, 1, 10);
        }

        /// <summary>
        /// Re-read the file after something else has written it - Integrated Mod Manager,
        /// ConfigKit, ConfigLib, or a text editor.
        ///
        /// READS ONLY. Load rewrites the file when its shape has changed, which is right at
        /// startup and wrong here: whatever just wrote it is the authority, and writing back
        /// over it would race them.
        ///
        /// Returns false and changes NOTHING if the file cannot be read, so a bad edit
        /// leaves the settings that are already working rather than replacing them with
        /// defaults.
        /// </summary>
        public static bool Reload(ICoreAPI api)
        {
            try
            {
                var fresh = api.LoadModConfig<UniversalDisplayLibConfig>("universaldisplaylibconfig.json");
                if (fresh == null) return false;

                Current = fresh;
                ApplyBounds();
                return true;
            }
            catch (Exception e)
            {
                api.Logger.Error("[UniversalDisplayLib] Could not re-read the config, keeping the settings already loaded: {0}", e.Message);
                return false;
            }
        }

        public static void Load(ICoreAPI api)
        {
            try
            {
                string configFilename = "universaldisplaylibconfig.json";

                // ── CARRY THE OLD FILE OVER, ONCE ────────────────────────────────────
                //
                // The mod was `windowdisplaylib` until the rename, and the config filename
                // follows the modid — so without this every existing player silently gets
                // defaults back: rain volume reset, ghost switched on again, jitter back.
                // Nothing errors, which is exactly why it would go unnoticed.
                //
                // The old file is READ as this class, so every matching property comes across
                // and anything since removed is dropped. It runs only when the new file does
                // not exist yet, so it cannot overwrite settings a player has already changed
                // under the new name.
                MigrateLegacyConfig(api, configFilename);

                Current = api.LoadModConfig<UniversalDisplayLibConfig>(configFilename);

                if (Current == null)
                {
                    api.Logger.Notification("[UniversalDisplayLib] No config file found, creating default config");
                    Current = new UniversalDisplayLibConfig();
                    Save(api);
                }
                else
                {
                    api.Logger.Notification("[UniversalDisplayLib] Config loaded successfully");

                    // Rewrite the file whenever its shape no longer matches the class,
                    // so options added or removed in an update are reconciled automatically
                    try
                    {
                        // Vintage Story saves mod configs in the ModConfig data folder
                        string configPath = System.IO.Path.Combine(GamePaths.ModConfig, configFilename);
                        if (System.IO.File.Exists(configPath))
                        {
                            string rawJson = System.IO.File.ReadAllText(configPath);
                            if (SchemaDiffers(rawJson, out string added, out string removed))
                            {
                                api.Logger.Notification(
                                    "[UniversalDisplayLib] Config schema changed (added: {0}; removed: {1}), rewriting file...",
                                    string.IsNullOrEmpty(added) ? "none" : added,
                                    string.IsNullOrEmpty(removed) ? "none" : removed);
                                Save(api);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        api.Logger.Warning("[UniversalDisplayLib] Could not verify config file structure on disk: {0}", ex.Message);
                    }

                    ApplyBounds();
                }
            }
            catch (Exception e)
            {
                api.Logger.Error("[UniversalDisplayLib] Failed to load config, using defaults. Error: {0}", e.Message);
                Current = new UniversalDisplayLibConfig();
            }
        }

        /// <summary>
        /// Compares the property names on disk against the ones this class actually
        /// serialises. Any difference in either direction means the file is stale.
        ///
        /// Deliberately not a string search for a known option name: that only catches
        /// additions, has to be updated by hand every time an option is added, and
        /// silently leaves removed options sitting in the file forever.
        ///
        /// Values are untouched — Current was already deserialised from this file, so
        /// re-saving keeps every setting the user had and only reconciles the shape.
        /// </summary>
        private static bool SchemaDiffers(string rawJson, out string added, out string removed)
        {
            added = removed = null;

            var onDisk = Newtonsoft.Json.Linq.JObject.Parse(rawJson)
                .Properties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

            var inClass = typeof(UniversalDisplayLibConfig)
                .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite
                            && p.GetCustomAttribute<Newtonsoft.Json.JsonIgnoreAttribute>() == null)
                .Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

            if (onDisk.SetEquals(inClass)) return false;

            added = string.Join(", ", inClass.Except(onDisk));
            removed = string.Join(", ", onDisk.Except(inClass));
            return true;
        }

        public static void Save(ICoreAPI api)
        {
            if (Current == null)
            {
                api.Logger.Warning("[UniversalDisplayLib] Cannot save config - Current is null");
                return;
            }

            try
            {
                api.StoreModConfig(Current, "universaldisplaylibconfig.json");
                api.Logger.Notification("[UniversalDisplayLib] Config saved successfully");
            }
            catch (Exception e)
            {
                api.Logger.Error("[UniversalDisplayLib] Failed to save config. Error: {0}", e.Message);
            }
        }
    }
}