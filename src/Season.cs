using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace UniversalDisplayLib
{
    /// <summary>
    /// The seasonal events, all in this file: when each one runs, then what it does. A new event is a date test here
    /// and its code below. The everyday weather on windows (frost, snow, grime, rain) is not seasonal and stays in
    /// PaneArt and GlassLayer. What a single window does when it is haunted is in BEUniversalDisplay, because the wind
    /// and the levitating display share that code.
    /// </summary>
    public static class Season
    {
        /// <summary>
        /// 25 October to 1 November by the server's date: 1 November keeps a Halloween weekend that ends on a Sunday,
        /// and the late night after Halloween.
        /// </summary>
        public static bool IsHalloween(DateTime now) => now.Month == 10 && now.Day >= 25 || now.Month == 11 && now.Day == 1;

        /// <summary>1 December to 6 January by the player's own date.</summary>
        public static bool IsChristmas(DateTime now) => now.Month == 12 || now.Month == 1 && now.Day <= 6;

        /// <summary>Whether the frost draws the Christmas flakes right now, on this player's game.</summary>
        public static bool ChristmasNow => IsChristmas(DateTime.Now);
    }

    /// <summary>
    /// Halloween: 25 October to 1 November by the SERVER's real date (eight days, so the weekend round Halloween is
    /// always covered) - windows near each player open by themselves with a creak and slam shut again, at random times.
    /// Hidden on purpose: no setting, nothing announced; it stops by itself after 1 November. Admins can turn it off
    /// for a world with "/udhaunt off" (HauntSwitch). Each turn is EITHER a window (creak open, slam shut) OR an item
    /// with nothing stacked on it or under it floating up, bobbing and dropping back (BEUniversalDisplay.HauntFloat +
    /// FloatingItemRenderer). Every window that opens joins in - shutters and display units too - through
    /// BEUniversalDisplay.Haunt, which keeps to the same room rules as a click and never unseals a room.
    /// </summary>
    public class HalloweenWindows : ModSystem
    {
        const int RangeBlocks = 24;
        const int MinGapMs = 30_000, MaxGapMs = 120_000;   // per player, a new random wait every time
        const int MinOpenMs = 1_000, MaxOpenMs = 3_000;    // how long a window hangs open before it slams
        const int MinFloatMs = 3_000, MaxFloatMs = 6_000;  // how long an item floats before it drops
        const int MinCreakGapMs = 20_000, MaxCreakGapMs = 60_000;
        // per player, the big one on its own timer
        const int MinBigGapMs = 300_000, MaxBigGapMs = 600_000;   // per player, the random creaks on their own timer

        ICoreServerAPI sapi;
        readonly Random rnd = new();
        readonly Dictionary<string, long> nextAt = new();
        readonly Dictionary<string, long> nextCreakAt = new();
        readonly Dictionary<string, long> nextBigAt = new();

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            try { switchedOff = api.LoadModConfig<HauntSwitch>(SwitchFile)?.Off == true; } catch { switchedOff = false; }
            api.Event.RegisterGameTickListener(Tick, 5000, 5000);
            api.Event.PlayerDisconnect += p => { nextAt.Remove(p.PlayerUID); nextCreakAt.Remove(p.PlayerUID); nextBigAt.Remove(p.PlayerUID); };

            // TESTING ONLY, admins: "/udhaunt" haunts a window near you now; "/udhaunt season on|off" forces the week on or
            // back to the calendar until the server restarts. Not mentioned anywhere players read.
            api.ChatCommands.Create("udhaunt")
                .WithDescription("Universal Display: test the Halloween windows")
                .RequiresPrivilege(Privilege.controlserver)
                .RequiresPlayer()
                .WithArgs(api.ChatCommands.Parsers.OptionalWord("season"), api.ChatCommands.Parsers.OptionalWord("onoff"))
                .HandleWith(args =>
                {
                    if ((args[0] as string) == "off" || (args[0] as string) == "on")
                    {
                        switchedOff = (args[0] as string) == "off";
                        api.StoreModConfig(new HauntSwitch { Off = switchedOff }, SwitchFile);
                        return TextCommandResult.Success(switchedOff ? "The Halloween haunting is off on this world." : "The Halloween haunting is on again.");
                    }
                    if ((args[0] as string) == "slam")
                    {
                        // "/udhaunt slam 1-6": one of the candidate slams, a block in front of you, to choose it by ear
                        string[] c = BEUniversalDisplay.SlamCandidates;
                        if (!int.TryParse(args[1] as string, out int n) || n < 1 || n > c.Length)
                            return TextCommandResult.Success("Slams: " + string.Join(", ", System.Linq.Enumerable.Select(c, (s, i) => (i + 1) + " " + s.Substring(s.LastIndexOf('/') + 1))));
                        var e = args.Caller.Player.Entity;
                        var at = e.Pos.XYZ.Add(e.Pos.GetViewVector().ToVec3d().Normalize().Mul(1.5));
                        BEUniversalDisplay.PlaySlam(sapi.World, at.X, e.Pos.Y + 1, at.Z, c[n - 1], BEUniversalDisplay.SlamSnap);
                        return TextCommandResult.Success("Slam " + n + ": " + c[n - 1]);
                    }
                    if ((args[0] as string) == "snap")
                    {
                        // "/udhaunt snap 1-5": the slam's body with one of the candidate snaps on top
                        var sc = BEUniversalDisplay.SnapCandidates;
                        if (!int.TryParse(args[1] as string, out int n) || n < 1 || n > sc.Length)
                            return TextCommandResult.Success("Snaps: " + string.Join(", ", System.Linq.Enumerable.Select(sc, (s, i) => (i + 1) + " " + s.sound.Substring(s.sound.LastIndexOf('/') + 1))));
                        var e = args.Caller.Player.Entity;
                        var at = e.Pos.XYZ.Add(e.Pos.GetViewVector().ToVec3d().Normalize().Mul(1.5));
                        BEUniversalDisplay.PlaySlam(sapi.World, at.X, e.Pos.Y + 1, at.Z, BEUniversalDisplay.SlamSound, sc[n - 1]);
                        return TextCommandResult.Success("Snap " + n + ": " + sc[n - 1].sound);
                    }
                    if ((args[0] as string) == "season")
                    {
                        forced = (args[1] as string) == "on";
                        return TextCommandResult.Success(forced ? "Halloween windows forced on" : "Halloween windows follow the calendar");
                    }
                    string what = args[0] as string;   // "window", "gust", "float", "floatmany", "creak", "big", "rattle", or nothing = any
                    bool done = what == "creak" ? TryNear(args.Caller.Player, 99, be => be.HauntCreak(rnd))
                              : what == "big" ? Big(args.Caller.Player)
                              : what == "rattle" ? Rattle(args.Caller.Player)
                              : what == "window" ? HauntWindow(args.Caller.Player, 99, false)
                              : what == "gust" ? HauntWindow(args.Caller.Player, 99, true)
                              : what == "float" ? HauntItem(args.Caller.Player, 99, false)
                              : what == "floatmany" ? HauntItem(args.Caller.Player, 99, true)
                              : HauntSomething(args.Caller.Player, 99);
                    return TextCommandResult.Success(done ? "Boo." : "Nothing near you can be haunted.");
                });
        }

        bool forced;

        /// <summary>
        /// The hidden off switch: "/udhaunt off" writes ModConfig/universaldisplaylib-halloween.json, "/udhaunt on"
        /// turns it back on. A file of its own, made only by the command, so nothing in the normal settings gives the
        /// surprise away.
        /// </summary>
        public class HauntSwitch { public bool Off { get; set; } }
        const string SwitchFile = "universaldisplaylib-halloween.json";
        bool switchedOff;

        void Tick(float dt)
        {
            // the pace is the wind's frequency setting - 0 stops this too
            if (!forced && (switchedOff || !Season.IsHalloween(DateTime.Now) || WindWeather.Pace <= 0f)) { nextAt.Clear(); nextCreakAt.Clear(); nextBigAt.Clear(); return; }
            long now = sapi.World.ElapsedMilliseconds;
            foreach (IPlayer p in sapi.World.AllOnlinePlayers)
            {
                if (p is not IServerPlayer sp || sp.ConnectionState != EnumClientState.Playing || p.Entity == null) continue;

                // the big one: a gust of windows slamming while items float from several windows at once
                if (!nextBigAt.TryGetValue(p.PlayerUID, out long bigAt)) nextBigAt[p.PlayerUID] = now + Paced(MinBigGapMs, MaxBigGapMs);
                else if (now >= bigAt)
                {
                    nextBigAt[p.PlayerUID] = now + Paced(MinBigGapMs, MaxBigGapMs);
                    Big(p);
                }

                // the occasional random creak, on its own timer: a nearby window creaks without moving
                if (!nextCreakAt.TryGetValue(p.PlayerUID, out long creakAt)) nextCreakAt[p.PlayerUID] = now + Paced(MinCreakGapMs, MaxCreakGapMs);
                else if (now >= creakAt)
                {
                    nextCreakAt[p.PlayerUID] = now + Paced(MinCreakGapMs, MaxCreakGapMs);
                    TryNear(p, 6, be => be.HauntCreak(rnd));
                }

                if (!nextAt.TryGetValue(p.PlayerUID, out long at)) { nextAt[p.PlayerUID] = now + Gap(); continue; }
                if (now < at) continue;
                nextAt[p.PlayerUID] = now + Gap();

                HauntSomething(p, 6);
            }
        }

        /// <summary>The big one: a gust of 2-3 windows, and items floating from 2-4 windows starting a second later.</summary>
        bool Big(IPlayer p)
        {
            bool windows = Gust(p, 0, big: true);
            // two or three windows rattle - rapidly flapping open and shut
            sapi.Event.RegisterCallback(_ => { for (int r = 2 + rnd.Next(2); r > 0; r--) Rattle(p); }, 500);
            sapi.Event.RegisterCallback(_ => HauntItem(p, 12, true, true), 1000);   // several items on each of 3-5 displays
            return windows;
        }

        /// <summary>A nearby window flaps open and shut 3 to 5 times.</summary>
        bool Rattle(IPlayer p) => TryNear(p, 6, be => be.HauntRattle(rnd, 3 + rnd.Next(3)));

        /// <summary>A window slams or an item floats - either, at random; the other if nothing near can do the first.</summary>
        bool HauntSomething(IPlayer p, int tries) =>
            rnd.Next(2) == 0 ? HauntWindow(p, tries) || HauntItem(p, tries) : HauntItem(p, tries) || HauntWindow(p, tries);

        /// <summary>
        /// A random window near the player that can open (a few tries, as a blocked or busy one says no) - or, about
        /// one turn in four, a GUST: 2 or 3 nearby windows creak open one after another, half a second to a second
        /// apart, and slam in the same order (all hang open equally long).
        /// </summary>
        bool HauntWindow(IPlayer p, int tries, bool? gust = null)
        {
            int openMs = rnd.Next(MinOpenMs, MaxOpenMs + 1);
            if (gust ?? rnd.Next(4) == 0) return Gust(p, openMs);
            return TryNear(p, tries, be => be.Haunt(rnd, openMs));
        }

        /// <param name="big">the big haunt: 4-7 windows, each hanging open its own 1-5 s, so they slam out of
        /// order</param>
        bool Gust(IPlayer p, int openMs, bool big = false)
        {
            if (p?.Entity == null) return false;
            List<BEUniversalDisplay> near = BEUniversalDisplay.ServerWindowsNear(p.Entity.Pos.AsBlockPos, RangeBlocks);
            int want = big ? 4 + rnd.Next(4) : 2 + rnd.Next(2), started = 0, delay = 0;
            while (near.Count > 0 && started < want)
            {
                int i = rnd.Next(near.Count);
                BEUniversalDisplay be = near[i];
                near.RemoveAt(i);
                int ms = big ? rnd.Next(1_000, 5_001) : openMs;
                if (started == 0)
                {
                    if (be.Haunt(rnd, ms)) started++;   // the first one must go, or there is no gust
                    continue;
                }
                delay += big ? rnd.Next(250, 701) : rnd.Next(500, 1001);
                sapi.Event.RegisterCallback(_ => be.Haunt(rnd, ms), delay);   // a later one that cannot just stays still
                started++;
            }
            return started > 0;
        }

        /// <summary>
        /// A random unstacked item on a display near the player floats - or, half the time, one each from 2 to 4 nearby
        /// displays, starting 0.3-0.8 s apart like a gust. Each rises to its own height.
        /// </summary>
        /// <param name="severalEach">the big haunt: each display lifts several of its items</param>
        bool HauntItem(IPlayer p, int tries, bool? several = null, bool severalEach = false)
        {
            int floatMs = rnd.Next(MinFloatMs, MaxFloatMs + 1);
            if (!(several ?? rnd.Next(2) == 0)) return TryNear(p, tries, be => be.HauntFloat(rnd, floatMs));
            if (p?.Entity == null) return false;
            List<BEUniversalDisplay> near = BEUniversalDisplay.ServerWindowsNear(p.Entity.Pos.AsBlockPos, RangeBlocks);
            int want = severalEach ? 3 + rnd.Next(3) : 2 + rnd.Next(3), started = 0, delay = 0;
            while (near.Count > 0 && started < want)
            {
                int i = rnd.Next(near.Count);
                BEUniversalDisplay be = near[i];
                near.RemoveAt(i);
                if (started == 0)
                {
                    if (be.HauntFloat(rnd, floatMs, severalEach)) started++;   // the first must go, or nothing happened
                    continue;
                }
                delay += rnd.Next(300, 801);
                sapi.Event.RegisterCallback(_ => be.HauntFloat(rnd, floatMs, severalEach), delay);   // one with nothing free just stays still
                started++;
            }
            return started > 0;
        }

        bool TryNear(IPlayer p, int tries, System.Func<BEUniversalDisplay, bool> haunt)
        {
            if (p?.Entity == null) return false;
            List<BEUniversalDisplay> near = BEUniversalDisplay.ServerWindowsNear(p.Entity.Pos.AsBlockPos, RangeBlocks);
            for (int t = 0; t < tries && near.Count > 0; t++)
            {
                int i = rnd.Next(near.Count);
                if (haunt(near[i])) return true;
                near.RemoveAt(i);
            }
            return false;
        }

        long Gap() => Paced(MinGapMs, MaxGapMs);

        /// <summary>A random wait between the two, at the frequency setting's pace (5 = as written, 10 = half as long).</summary>
        long Paced(int minMs, int maxMs) => (long)(rnd.Next(minMs, maxMs + 1) / Math.Max(0.1f, WindWeather.Pace));
    }

    public static partial class PaneArt
    {
        // ---- Christmas frost ----
        // SMALL CRISP SNOWFLAKES. From 1 December to 6 January by the player's own date (Season.IsChristmas) the light
        // frost is little snowflakes PIXELLED BY HAND at 32 a block and placed texel for texel (nothing drawn finer and
        // averaged, which is what blurred the big ones): a 7x7 snowflake and a 5x5 cross (ChristmasFlakes), each a
        // little stronger or fainter, packed tight at random. They form as the frost forms, more the colder. ON THEIR
        // OWN - no milky frost while the event is on - and drawn as CLEAR ICE, each flake its own slight colour, a
        // faint patchy film of ice between them (ChristmasFrost).
        const double CrystalShareLight = 0.45;               // share of the flakes that form in the lightest frost (-1 C)
        const double CrystalShareAllAt = 0.43;               // cold (0-1) by which every flake forms: about -11 C
        const double CrystalGrowSpan = 0.15;                 // of the forming, from a flake's first texel to its last
        const int FlakeOverhang = 2;                         // how far a flake may hang over the pane's edge, in texels
        // CLEAR ICE: a faint blue body you see through, a bright lit edge, a darker blue far edge
        static readonly (int r, int g, int b) IceTint = (196, 226, 250), IceLit = (250, 254, 255), IceShade = (96, 140, 196);
        const double IceBodyAlpha = 0.2, IceLitAlpha = 0.62, IceShadeAlpha = 0.38;
        const double IceTintCyanR = -26, IceTintCyanG = 6, IceTintVioletR = 16, IceTintVioletG = -14;   // each flake's colour shift, at most
        const double IceBrightLow = 0.9, IceBrightHigh = 1.05;   // and its brightness
        const double IceFilmBase = 0.2, IceFilmCold = 0.2;   // the ice between them, more the colder
        const double IceColdBase = 0.9, IceColdMore = 0.3;   // a little stronger the colder

        /// <summary>
        /// '@' the bright middle, '#' an arm, '+' a faint barb or tip.
        /// </summary>
        static readonly string[][] ChristmasFlakes =
        {
            new[] { "...#...",
                    ".+.#.+.",
                    "..+#+..",
                    "###@###",
                    "..+#+..",
                    ".+.#.+.",
                    "...#..." },
            new[] { "..#..",
                    ".+#+.",
                    "##@##",
                    ".+#+.",
                    "..#.." },
        };

        static double FlakeLevel(char c) => c switch { '@' => 0.72, '#' => 0.6, _ => 0.34 };

        /// <summary>
        /// The Christmas frost for a w x h pane, BGRA, the same size (<paramref name="pw"/> x <paramref name="ph"/> = w
        /// x h): the flakes ON THEIR OWN - no milky frost while the event is on - as CLEAR ICE: the body almost clear
        /// glass with a faint blue tint, the shape shown by its edges - bright on the side facing the light from the
        /// top left, a darker blue on the far side; a little stronger the colder.
        /// </summary>
        public static int[] ChristmasFrost(int w, int h, int step, int pattern, int thick, out int pw, out int ph)
        {
            pw = w; ph = h;
            double front = step / (double)FrostSteps * 1.02;
            double cold = Math.Clamp(thick / (double)(FrostThickLevels - 1), 0, 1);
            var (flakes, tints, brights) = ChristmasFlakesAt(w, h, pattern, front, cold);
            bool Open(int x, int y) => x >= 0 && x < w && y >= 0 && y < h && flakes[y * w + x] < 0.05;   // the frame is not clear glass: no lit edge along it
            double strength = IceColdBase + IceColdMore * cold;
            // the faint ice on the glass between the flakes
            float[,] film = IceFilmFor(w, h, pattern);
            double filmAlpha = (IceFilmBase + IceFilmCold * cold) * Math.Min(1, front);
            GameTile ice = iceTiles[pattern % iceTiles.Length];
            var pixels = new int[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var ic = ice.At(x, y);
                double a = filmAlpha * (0.3 + 1.4 * film[x, y]), r = ic.r, g = ic.g, b = ic.b;
                double fa = flakes[y * w + x];
                if (fa > 0.02)
                {
                    // an edge texel has clear glass beside it: toward the top left = lit, toward the bottom right = shaded
                    bool litSide = Open(x - 1, y) || Open(x, y - 1), farSide = Open(x + 1, y) || Open(x, y + 1);
                    (int r, int g, int b) col;
                    double ca;
                    if (litSide && !farSide) { col = IceLit; ca = IceLitAlpha; }
                    else if (farSide && !litSide) { col = IceShade; ca = IceShadeAlpha; }
                    else if (litSide) { col = ((IceLit.r + IceShade.r) / 2, (IceLit.g + IceShade.g) / 2, (IceLit.b + IceShade.b) / 2); ca = (IceLitAlpha + IceShadeAlpha) / 2; }   // a one-texel-thin arm
                    else { col = IceTint; ca = IceBodyAlpha; }
                    ca = Math.Min(1, ca * Math.Min(1, fa / 0.6) * strength);
                    // each flake its own colour, a little
                    double t = tints[y * w + x], br = brights[y * w + x];
                    double fr = Math.Clamp((col.r + (t < 0 ? IceTintCyanR * -t : IceTintVioletR * t)) * br, 0, 255);
                    double fg = Math.Clamp((col.g + (t < 0 ? IceTintCyanG * -t : IceTintVioletG * t)) * br, 0, 255);
                    double fb = Math.Clamp(col.b * br, 0, 255);
                    double below = a * (1 - ca), all = ca + below;
                    r = (fr * ca + r * below) / all; g = (fg * ca + g * below) / all; b = (fb * ca + b * below) / all;
                    a = all;
                }
                if (a < 0.02) continue;
                pixels[y * w + x] = ColorUtil.ToRgba((int)(Math.Min(1, a) * 255), (int)r, (int)g, (int)b);
            }
            return pixels;
        }

        static readonly Dictionary<(int, int, int), float[,]> iceFilms = new();

        /// <summary>The ice film's patchiness over a w x h pane, 0-1, seeded per size and pattern.</summary>
        static float[,] IceFilmFor(int w, int h, int pattern)
        {
            lock (iceFilms)
            {
                if (iceFilms.TryGetValue((w, h, pattern), out var known)) return known;
            }
            float[,] film = ValueNoise(w, h, 5, new Random(4112026 + pattern * 7 + w * 131 + h * 7919));
            lock (iceFilms) iceFilms[(w, h, pattern)] = film;
            return film;
        }

        static readonly Dictionary<(int, int, int), (int x, int y, int kind, double strength, double tint, double bright, double rank)[]> christmasLayouts = new();

        /// <summary>
        /// Where a pane's flakes sit (top-left corner, which drawing, its strength, and rank 0-1 = the order they form
        /// in), PACKED AS TIGHT AS THEY GO at random: every position of either drawing, in a random order, each put
        /// down if it fits - so when done not one more fits. They never overlap or touch side on; corner to corner is
        /// allowed, so arms nest into each other's gaps; they may hang over the pane's edge. Seeded per size and
        /// pattern, so every player sees the same, each pane its own.
        /// </summary>
        static (int x, int y, int kind, double strength, double tint, double bright, double rank)[] ChristmasLayoutFor(int w, int h, int pattern)
        {
            lock (christmasLayouts)
            {
                if (christmasLayouts.TryGetValue((w, h, pattern), out var known)) return known;
            }
            var rnd = new Random(2512026 + pattern * 7 + w * 131 + h * 7919);
            var tries = new List<(double order, int x, int y, int kind)>();
            for (int kind = 0; kind < ChristmasFlakes.Length; kind++)
            {
                int n = ChristmasFlakes[kind].Length;
                for (int y = -FlakeOverhang; y <= h - n + FlakeOverhang; y++)
                for (int x = -FlakeOverhang; x <= w - n + FlakeOverhang; x++)
                    tries.Add((rnd.NextDouble(), x, y, kind));
            }
            tries.Sort((a, b) => a.order.CompareTo(b.order));
            var taken = new bool[w, h];
            bool Taken(int x, int y) => x >= 0 && x < w && y >= 0 && y < h && taken[x, y];
            var kept = new List<(int x, int y, int kind)>();
            foreach (var (_, x, y, kind) in tries)
            {
                string[] rows = ChristmasFlakes[kind];
                bool clash = false;
                for (int ry = 0; ry < rows.Length && !clash; ry++)
                for (int rx = 0; rx < rows[ry].Length && !clash; rx++)
                {
                    if (rows[ry][rx] == '.') continue;
                    int tx = x + rx, ty = y + ry;
                    clash = Taken(tx, ty) || Taken(tx - 1, ty) || Taken(tx + 1, ty) || Taken(tx, ty - 1) || Taken(tx, ty + 1);
                }
                if (clash) continue;
                for (int ry = 0; ry < rows.Length; ry++)
                for (int rx = 0; rx < rows[ry].Length; rx++)
                {
                    int tx = x + rx, ty = y + ry;
                    if (rows[ry][rx] != '.' && tx >= 0 && tx < w && ty >= 0 && ty < h) taken[tx, ty] = true;
                }
                kept.Add((x, y, kind));
            }
            var layout = new (int x, int y, int kind, double strength, double tint, double bright, double rank)[kept.Count];
            for (int i = 0; i < kept.Count; i++)
                layout[i] = (kept[i].x, kept[i].y, kept[i].kind, 0.8 + rnd.NextDouble() * 0.3, rnd.NextDouble() * 2 - 1, IceBrightLow + rnd.NextDouble() * (IceBrightHigh - IceBrightLow), (i + 0.5) / kept.Count);
            lock (christmasLayouts) christmasLayouts[(w, h, pattern)] = layout;
            return layout;
        }

        /// <summary>The flakes at this much forming (front) and cold, per texel of a w x h pane: alpha (0 = none there), and the colour shift and brightness of the flake it belongs to.</summary>
        static (double[] alpha, double[] tint, double[] bright) ChristmasFlakesAt(int w, int h, int pattern, double front, double cold)
        {
            var map = new double[w * h];
            var tints = new double[w * h];
            var brights = new double[w * h];
            double share = CrystalShareLight + (1 - CrystalShareLight) * Math.Min(1.0, cold / CrystalShareAllAt);
            foreach (var (x, y, kind, strength, tint, bright, rank) in ChristmasLayoutFor(w, h, pattern))
            {
                if (rank > share) continue;                                   // too warm for this one
                // its turn comes at rank / share of the forming, so the last has grown in full as the forming ends
                double grow = (front - rank / share * (1 - CrystalGrowSpan)) / CrystalGrowSpan;
                if (grow <= 0) continue;
                string[] rows = ChristmasFlakes[kind];
                double c = (rows.Length - 1) / 2.0;
                for (int ry = 0; ry < rows.Length; ry++)
                for (int rx = 0; rx < rows[ry].Length; rx++)
                {
                    char ch = rows[ry][rx];
                    if (ch == '.') continue;
                    double d = Math.Sqrt((rx - c) * (rx - c) + (ry - c) * (ry - c)) / (c * 1.42 + 0.5);   // grows out from its middle
                    if (d > grow * 1.05) continue;
                    int tx = x + rx, ty = y + ry;
                    if (tx < 0 || tx >= w || ty < 0 || ty >= h) continue;
                    int n = ty * w + tx;
                    map[n] = Math.Max(map[n], FlakeLevel(ch) * strength);
                    tints[n] = tint; brights[n] = bright;
                }
            }
            return (map, tints, brights);
        }
    }
}
