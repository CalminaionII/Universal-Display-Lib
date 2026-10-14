using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace UniversalDisplayLib
{
    /// <summary>
    /// The authoring tools' file output: one paste-ready <c>displayable</c> entry per item,
    /// carrying both the box size and the transform.
    ///
    /// **ONE FILE, NOT TWO.** Sizes and transforms were written separately at first, which was
    /// wrong for the obvious reason: they are two halves of the SAME patch entry, and an
    /// author tuning an item produces both within a minute of each other. Splitting them meant
    /// pasting from two places and hand-merging the braces. The file now emits exactly what
    /// goes into the patch.
    ///
    /// **The CATEGORY travels with each item.** A block declaring
    /// <c>psurface0-w16-h4-d16-box</c> reads <c>displayable.box</c>, and that is the whole
    /// mechanism keeping one mod's display data clear of another's. It cannot be a heading at
    /// the top of the file — a single session can tune a window item and a box item one after
    /// the other, and a heading could only ever be right for one of them.
    ///
    /// **Moved out of UniversalDisplayLibMod before there was a second caller** — the K cycle was
    /// the only one until the tuner dialog arrived, and "a value derived in more than one
    /// place" is this codebase's named recurring bug.
    ///
    /// Client side only. Everything here is notes for an author: **nothing is ever read back by
    /// the mod**, so deleting or editing these files cannot break anything.
    /// </summary>
    public static class TuningFiles
    {
        /// <summary>One item's tuned state. Either half may be absent — an author who only
        /// resized something must not have a transform invented for them.</summary>
        private class Entry
        {
            public Size3f Size;
            public ModelTransform Transform;

            public string Category = BlockBehaviorDisplaySurfaces.DefaultDisplayCategory;

            /// <summary>The stack type for a TYPE-KEYED item (clutter), null for everything
            /// else. Decides which of the two output forms this entry is written in.</summary>
            public string Type;
        }

        /// <summary>
        /// Keyed by ITEM **AND CATEGORY**, not by item alone.
        ///
        /// The same item legitimately wants different numbers on different surfaces — a hammer
        /// laid flat on a window sill and stood up in someone's box are two different entries
        /// in two different patch keys, and both are worth keeping. Keying on the collectible
        /// alone meant tuning it on the second surface silently REPLACED the first, so an
        /// author lost the earlier pass by doing exactly what the tool invites.
        /// </summary>
        private static readonly Dictionary<(CollectibleObject, string, string), Entry> entries = new();

        /// <summary>Set when something has been tuned and not yet written.</summary>
        public static bool SizesDirty;

        private static string filePrefix = "";

        /// <summary>
        /// Where the file lives. `GamePaths.ModConfig` is per-INSTALLATION, not per-machine —
        /// it follows the data path the running client was launched with, the same as
        /// `StoreModConfig`, so tuning in the test install writes into the test install.
        ///
        /// A subfolder because ModConfig is a shared flat directory — nineteen mods' files in
        /// the tester — and this is authoring output rather than a setting players edit.
        /// </summary>
        public static string LogDir => System.IO.Path.Combine(GamePaths.ModConfig, "universaldisplaylib");

        /// <summary>
        /// Renamed from `window-display-entries.txt` with the mod. Nothing reads this file
        /// back — it is authoring OUTPUT — so there is nothing to migrate, unlike the config.
        /// An old one left in the folder is simply an old file.
        /// </summary>
        public static string LogPath => System.IO.Path.Combine(LogDir, "display-entries.txt");

        /// <summary>CRLF, so the file opens cleanly in Notepad on Windows.</summary>
        private const string NL = "\r\n";

        /// <summary>
        /// FIVE decimals, not two. Two was enough for a size in voxels and quietly destroyed
        /// TRANSFORMS: a 0.005 nudge came out as 0.01 and a 0.001 one as 0, so the micro
        /// adjustments an author had just made by hand were rounded away on the way to the
        /// file. Trailing zeros are still dropped, so 5.64 stays 5.64.
        ///
        /// NEGATIVE ZERO is normalised away too — a tiny negative value formats as "-0",
        /// which is valid JSON but reads like a mistake in a patch someone else opens.
        /// </summary>
        public static string Fmt(float v)
        {
            string s = v.ToString("0.#####", CultureInfo.InvariantCulture);
            return s == "-0" ? "0" : s;
        }

        /// <summary>
        /// Clears everything belonging to one world. Called from StartClientSide, because a
        /// client static outlives the world it describes — the lesson from the four the code
        /// audit found.
        /// </summary>
        public static void ResetSession()
        {
            SizesDirty = false;
            entries.Clear();
            filePrefix = "";
        }

        private static Entry EntryFor(CollectibleObject c, string category, string type)
        {
            if (string.IsNullOrEmpty(category)) category = BlockBehaviorDisplaySurfaces.DefaultDisplayCategory;
            if (string.IsNullOrEmpty(type)) type = null;

            // TYPE IS PART OF THE KEY for the same reason category is: one collectible covers
            // every piece of clutter, so keying on (item, category) alone meant tuning a second
            // pot overwrote the first and the file described only whichever was touched last.
            var key = (c, type, category);
            if (!entries.TryGetValue(key, out Entry e))
            {
                entries[key] = e = new Entry { Category = category, Type = type };
            }
            return e;
        }

        public static void RecordSize(ICoreClientAPI api, CollectibleObject c, Size3f size, string category,
                                      string type = null, bool announce = false)
        {
            if (c == null || size == null) return;

            Entry e = EntryFor(c, category, type);
            e.Size = size;

            SizesDirty = true;
            Save(api, announce);
        }

        public static void RecordTransform(ICoreClientAPI api, CollectibleObject c, ModelTransform tf,
                                           string category, string type = null)
        {
            if (c == null || tf == null) return;

            Entry e = EntryFor(c, category, type);
            e.Transform = tf;

            SizesDirty = true;
            Save(api, announce: false);
        }

        /// <summary>
        /// Records what vanilla's transform editor just applied, resolving the target and
        /// category from the crosshair. Driven by the `onsettransform` event the mod already
        /// listens to for the live preview, so the value costs nothing extra to obtain.
        /// </summary>
        public static void RecordTransformEdit(ICoreClientAPI api)
        {
            ItemSlot target = BlockBehaviorDisplaySurfaces.ResolveEditorTarget(api);
            CollectibleObject collectible = target?.Itemstack?.Collectible;
            if (collectible == null) return;

            string category = BlockBehaviorDisplaySurfaces.ResolveEditorCategory(api);
            ModelTransform tf = BlockBehaviorDisplaySurfaces
                .GetDisplayableAttributes(target, category)?.Transform;
            if (tf == null) return;

            RecordTransform(api, collectible, tf, category, BlockBehaviorDisplaySurfaces.TypedKeyOf(target));
        }

        /// <summary>
        /// Creates the folder and file at startup so they can be found before anything has been
        /// tuned — a tool whose output location only exists once it has produced output cannot
        /// be checked. Never truncates: a previous session's numbers are still someone's work.
        ///
        /// Also captures the file as it stands, which every save writes back ahead of this
        /// session's block. **Without that, each session erased the last** — saving rewrites
        /// rather than appends, so that earlier attempts do not bury the answer, and rewriting
        /// from the header alone dropped everything from previous sittings. Read ONCE here, not
        /// per save, or this session's own block folds back in on every change.
        /// </summary>
        public static void EnsureFile(ICoreClientAPI api)
        {
            filePrefix = "";

            try
            {
                System.IO.Directory.CreateDirectory(LogDir);
                if (!System.IO.File.Exists(LogPath))
                {
                    System.IO.File.WriteAllText(LogPath,
                        "# Universal Display Lib - display entries from the in-game editor.\r\n"
                        + "# Type .udedit and point at an item. Paste a block into that item's\r\n"
                        + "# patch, under the category its surface declares.\r\n"
                        + "# NOTHING HERE IS READ BY THE MOD - it is notes. Delete freely.\r\n"
                        + "# Kept for ever and never tidied automatically; prune it by hand.\r\n\r\n",
                        Encoding.UTF8);
                }

                filePrefix = System.IO.File.ReadAllText(LogPath);
                if (filePrefix.Length > 0 && !filePrefix.EndsWith("\n")) filePrefix += "\r\n";
            }
            catch (Exception e)
            {
                api.Logger.Error("[UniversalDisplayLib] Could not create {0}: {1}", LogPath, e.Message);
            }
        }

        /// <summary>
        /// Writes every tuned item as a complete <c>displayable</c> entry, on EVERY change.
        ///
        /// **Live rather than on close.** The close-only version lost a whole pass on its first
        /// outing: quitting with the editor still open discarded everything, silently, which is
        /// the natural way to use the tool. A change is a human action, so rewriting a few
        /// hundred bytes costs nothing.
        ///
        /// **The CURRENT state is rewritten, not appended** — appending per change buries the
        /// answer under thirty near-identical attempts. History across sessions survives because
        /// <see cref="filePrefix"/> is written back first and startup never truncates.
        ///
        /// **A file, not chat:** the game renders chat as VTML, so the braces and quotes of a
        /// JSON block are parsed as markup. The first version logged *"Found closing tag char
        /// &gt; but no tag was opened"* four times per line and showed nothing useful.
        /// </summary>
        public static void Save(ICoreClientAPI api, bool announce)
        {
            if (entries.Count == 0) return;

            var sb = new StringBuilder();
            sb.Append("# ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append(NL);

            // GROUPED BY ITEM FAMILY, because that is the shape a patch takes: ONE
            // `sizeByType` holding every variant, ONE `transformByType` holding every variant.
            // A block per item meant tuning five boots produced five fragments to merge by
            // hand - the exact work this file exists to remove.
            foreach (var family in GroupByFamily())
            {
                var members = family.Value;
                bool byType = VariantKey(members[0].Item1) != null;
                bool anySize = members.Exists(m => m.Item2.Size != null);
                bool anyTf = members.Exists(m => m.Item2.Transform != null);
                if (!anySize && !anyTf) continue;

                sb.Append(family.Key.Item1);
                if (byType && members.Count > 1) sb.Append("   (").Append(members.Count).Append(" variants)");
                sb.Append(NL);
                sb.Append("\t\t\t\"").Append(family.Key.Item2).Append("\": {").Append(NL);

                if (anySize)
                {
                    sb.Append("\t\t\t\t\"").Append(byType ? "sizeByType" : "size").Append("\": {").Append(NL);
                    WriteSection(sb, members, byType, e => e.Size == null ? null : SizeBody(e.Size));
                    sb.Append("\t\t\t\t}").Append(anyTf ? "," : "").Append(NL);
                }

                if (anyTf)
                {
                    sb.Append("\t\t\t\t\"").Append(byType ? "transformByType" : "transform").Append("\": {").Append(NL);
                    WriteSection(sb, members, byType, e => e.Transform == null ? null : TransformBody(e.Transform));
                    sb.Append("\t\t\t\t}").Append(NL);
                }

                sb.Append("\t\t\t}").Append(NL);
            }
            sb.Append(NL);

            WriteTypedEntries(sb);

            // A failed write must not escape a hotkey or wheel handler. Losing the numbers is a
            // nuisance; taking the client down while an author is tuning is not.
            try
            {
                System.IO.Directory.CreateDirectory(LogDir);
                System.IO.File.WriteAllText(LogPath, filePrefix + sb, Encoding.UTF8);
                SizesDirty = false;

                if (announce)
                {
                    api.Logger.Notification("[UniversalDisplayLib] display entries written to {0}", LogPath);
                    api.ShowChatMessage(Lang.Get("universaldisplaylib:size-edit-written", LogPath));
                }
            }
            catch (Exception e)
            {
                api.Logger.Error("[UniversalDisplayLib] Could not write {0}: {1}", LogPath, e.Message);
                if (announce) api.ShowChatMessage(Lang.Get("universaldisplaylib:size-edit-writefailed"));
            }
        }

        /// <summary>
        /// <c>scale</c> when the three axes agree, <c>scaleXYZ</c> when they do not. Both are
        /// valid in item JSON and the uniform form is what nearly every patch wants; emitting
        /// scaleXYZ regardless would make every pasted block noisier than what it describes.
        /// A negative X is what "flip on X" means — the same mirroring vanilla's own transform
        /// editor applies — so the non-uniform form covers flipping for free.
        /// </summary>


        /// <summary>
        /// The second output form, for items whose display data is keyed by STACK TYPE rather
        /// than by block code - vanilla clutter, and anything else carrying
        /// <c>TypedDisplayableProps</c>.
        ///
        /// **It has to be a different FORM, not another key in the same one.** An ordinary item
        /// is patched at <c>/attributes/displayable/[category]</c> with its variants separated
        /// by <c>sizeByType</c>. Clutter has no variants at all - its type lives in the
        /// itemstack - so that shape cannot express it, and a size written there applies to all
        /// ~100 objects at once. The data lives in the BEHAVIOUR'S properties, so that is where
        /// the patch has to go.
        ///
        /// The pointer path is spelled out because it is the part nobody guesses: a <c>/</c>
        /// inside a key is <c>~1</c> in a JSON pointer, and <c>/behaviors/N</c> is a positional
        /// index a game update can move.
        ///
        /// Nothing is emitted unless something type-keyed was actually tuned, so the file looks
        /// exactly as it always did for anyone who never touches clutter.
        /// </summary>
        private static void WriteTypedEntries(StringBuilder sb)
        {
            var typed = new List<KeyValuePair<(CollectibleObject, string, string), Entry>>();
            foreach (var kv in entries)
            {
                if (kv.Value.Type != null && (kv.Value.Size != null || kv.Value.Transform != null))
                {
                    typed.Add(kv);
                }
            }
            if (typed.Count == 0) return;

            sb.Append(NL);
            sb.Append("# TYPE-KEYED ITEMS (clutter and the like)").Append(NL);
            sb.Append("# NOT patched at /attributes/displayable - these sizes live in the").Append(NL);
            sb.Append("# TypedDisplayableProps behaviour, keyed by the stack's own type.").Append(NL);
            sb.Append("# Keys wildcard-match, so \"pottery/*\" is legal and one entry can cover a family.").Append(NL);
            sb.Append("#   \"op\": \"addmerge\",").Append(NL);
            sb.Append("#   \"path\": \"/behaviors/<n>/properties/displayable/<type, with / written as ~1>\",").Append(NL);
            sb.Append("#   \"file\": \"<domain>:blocktypes/<block>\"").Append(NL);
            sb.Append("# <n> is the index of TypedDisplayableProps in that block's behaviors array.").Append(NL);

            foreach (var kv in typed)
            {
                Entry e = kv.Value;
                bool anyTf = e.Transform != null;

                sb.Append(NL).Append(kv.Key.Item1.Code).Append("   type: ").Append(e.Type).Append(NL);
                sb.Append("\t\t\t\"").Append(e.Type).Append("\": {").Append(NL);
                sb.Append("\t\t\t\t\"").Append(e.Category).Append("\": {").Append(NL);

                if (e.Size != null)
                {
                    sb.Append("\t\t\t\t\t\"size\": {").Append(NL);
                    sb.Append(SizeBody(e.Size));
                    sb.Append("\t\t\t\t\t}").Append(anyTf ? "," : "").Append(NL);
                }

                if (anyTf)
                {
                    sb.Append("\t\t\t\t\t\"transform\": {").Append(NL);
                    sb.Append(TransformBody(e.Transform));
                    sb.Append("\t\t\t\t\t}").Append(NL);
                }

                sb.Append("\t\t\t\t}").Append(NL);
                sb.Append("\t\t\t}").Append(NL);
            }
        }

        /// <summary>
        /// Tuned items collected by (family, category), each family's members in the order they
        /// were tuned. The family is the code with its variant components stripped, so
        /// `hammer-copper` and `hammer-iron` land together under `game:hammer`.
        /// </summary>
        private static List<KeyValuePair<(string, string), List<(CollectibleObject, Entry)>>> GroupByFamily()
        {
            var order = new List<(string, string)>();
            var map = new Dictionary<(string, string), List<(CollectibleObject, Entry)>>();

            foreach (var kv in entries)
            {
                if (kv.Value.Type != null) continue;   // typed items use their own writer
                var key = (FamilyCode(kv.Key.Item1), kv.Value.Category);
                if (!map.TryGetValue(key, out var list))
                {
                    map[key] = list = new List<(CollectibleObject, Entry)>();
                    order.Add(key);
                }
                list.Add((kv.Key.Item1, kv.Value));
            }

            var result = new List<KeyValuePair<(string, string), List<(CollectibleObject, Entry)>>>();
            foreach (var key in order)
            {
                result.Add(new KeyValuePair<(string, string), List<(CollectibleObject, Entry)>>(key, map[key]));
            }
            return result;
        }

        /// <summary>The code with its variant components removed - `game:hammer` for
        /// `game:hammer-copper`. An item with no variants keeps its whole code.</summary>
        private static string FamilyCode(CollectibleObject c)
        {
            string path = c.Code.Path;
            int n = c.VariantStrict?.Count ?? 0;

            if (n > 0)
            {
                string[] parts = path.Split('-');
                if (parts.Length > n) path = string.Join("-", parts, 0, parts.Length - n);
            }

            return c.Code.Domain + ":" + path;
        }

        /// <summary>
        /// One ByType section: every tuned variant, **then a `*` catch-all LAST**.
        ///
        /// The order is not cosmetic. `RegistryObjectType.solveByType` (public source,
        /// `anegostudios/vsessentialsmod`) takes the FIRST matching key and stops -
        /// *"Replaces for first matched key only"* - so a `*` written above the specific
        /// entries would swallow every one of them.
        ///
        /// **The `*` is always emitted**, seeded from the first variant tuned. Without it any
        /// variant the author did not tune matches nothing, gets no size and no transform, and
        /// renders wrong - which reads as a broken patch rather than an incomplete one. It is a
        /// starting point to edit, not a guess to trust.
        /// </summary>
        private static void WriteSection(StringBuilder sb, List<(CollectibleObject, Entry)> members,
                                         bool byType, System.Func<Entry, string> body)
        {
            if (!byType)
            {
                sb.Append(body(members[0].Item2));
                return;
            }

            string fallback = null;
            var written = new List<string>();

            foreach (var (c, e) in members)
            {
                string text = body(e);
                if (text == null) continue;

                string variant = VariantKey(c);
                if (variant == null || written.Contains(variant)) continue;
                written.Add(variant);
                fallback ??= text;

                sb.Append("\t\t\t\t\t\"*-").Append(variant).Append("\": {").Append(NL)
                  .Append(text).Append("\t\t\t\t\t},").Append(NL);
            }

            if (fallback != null)
            {
                sb.Append("\t\t\t\t\t\"*\": {").Append(NL).Append(fallback)
                  .Append("\t\t\t\t\t}").Append(NL);
            }
        }

        private static string SizeBody(Size3f s) => string.Format(CultureInfo.InvariantCulture,
            "\t\t\t\t\t\t\"width\": {0}," + NL + "\t\t\t\t\t\t\"height\": {1}," + NL
            + "\t\t\t\t\t\t\"length\": {2}" + NL,
            Fmt(s.Width), Fmt(s.Height), Fmt(s.Length));

        private static string TransformBody(ModelTransform t) => string.Format(CultureInfo.InvariantCulture,
            "\t\t\t\t\t\t\"translation\": {{ \"x\": {0}, \"y\": {1}, \"z\": {2} }}," + NL
            + "\t\t\t\t\t\t\"rotation\": {{ \"x\": {3}, \"y\": {4}, \"z\": {5} }}," + NL
            + "\t\t\t\t\t\t\"origin\": {{ \"x\": {6}, \"y\": {7}, \"z\": {8} }}," + NL
            + "\t\t\t\t\t\t{9}" + NL,
            Fmt(t.Translation.X), Fmt(t.Translation.Y), Fmt(t.Translation.Z),
            Fmt(t.Rotation.X), Fmt(t.Rotation.Y), Fmt(t.Rotation.Z),
            Fmt(t.Origin.X), Fmt(t.Origin.Y), Fmt(t.Origin.Z),
            ScaleJson(t));

        /// <summary>
        /// The item's LAST variant value — `copper` for `hammer-copper` — or null when it has
        /// no variants at all.
        ///
        /// `VariantStrict` is an ordered dictionary of variant group to value, filled from the
        /// item's own `variantgroups`, so the last entry is the one that distinguishes this
        /// item from its siblings. That is the segment an author reaches for: `*-cookpot`,
        /// `*-squire`.
        /// </summary>
        /// <summary>
        /// EVERY variant value, in declaration order, joined the way the item code joins them —
        /// so a trophy of `{animal: bear, grade: standard}` gives `bear-standard` and the key
        /// written is `*-bear-standard`.
        ///
        /// **It used to return only the LAST value**, which was correct for the single-group
        /// items this was written against and quietly wrong for anything with two. A trophy
        /// tuned as a bear wrote `*-standard`, which matches every animal at standard grade —
        /// so the numbers measured for one item silently applied to fifteen others, and tuning
        /// a second animal was dropped as a duplicate key. Reported 2026-08-07 by an author who
        /// noticed only the grade coming out.
        ///
        /// Order matters and is not assumed lightly: `VariantStrict` enumerates in the order
        /// the variantgroups are declared, which is the order the code segments appear in, so
        /// joining them reproduces the code exactly.
        /// </summary>
        private static string VariantKey(CollectibleObject c)
        {
            var v = c?.VariantStrict;
            if (v == null || v.Count == 0) return null;

            var parts = new List<string>(v.Count);
            foreach (var kv in v)
            {
                if (!string.IsNullOrEmpty(kv.Value)) parts.Add(kv.Value);
            }

            return parts.Count == 0 ? null : string.Join("-", parts);
        }

        private static string ScaleJson(ModelTransform t)
        {
            var s = t.ScaleXYZ;
            bool uniform = Math.Abs(s.X - s.Y) < 0.0001f && Math.Abs(s.Y - s.Z) < 0.0001f;

            return uniform
                ? string.Format(CultureInfo.InvariantCulture, "\"scale\": {0}", Fmt(s.X))
                : string.Format(CultureInfo.InvariantCulture,
                    "\"scaleXYZ\": {{ \"x\": {0}, \"y\": {1}, \"z\": {2} }}", Fmt(s.X), Fmt(s.Y), Fmt(s.Z));
        }
    }
}
