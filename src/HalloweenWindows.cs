using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.Config;

namespace UniversalDisplayLib
{
    /// <summary>
    /// Halloween (Calm, 2026-10-12): 25 October to 1 November by the SERVER's real date (eight days, so the weekend round
    /// Halloween is always covered) - windows near each player open by
    /// themselves with a creak and slam shut again, at random times (Calm: "so it goes off unpredictably").
    /// Hidden on purpose: no setting, nothing announced (Calm: "it should be secret as a good scare"); it stops by itself after
    /// 1 November. Admins can turn it off for a world with "/udhaunt off" (HauntSwitch).
    /// Each turn is EITHER a window (creak open, slam shut) OR an item with nothing stacked on it or under it floating up,
    /// bobbing and dropping back (Calm: "Do the real floating version"; BEUniversalDisplay.HauntFloat + FloatingItemRenderer).
    /// Every window that opens joins in - shutters and display units too - through BEUniversalDisplay.Haunt, which keeps
    /// to the same room rules as a click and never unseals a room.
    /// </summary>
    public class HalloweenWindows : ModSystem
    {
        const int RangeBlocks = 24;
        const int MinGapMs = 30_000, MaxGapMs = 120_000;   // per player, a new random wait every time (Calm: "up the frequency a bit")
        const int MinOpenMs = 1_000, MaxOpenMs = 3_000;    // how long a window hangs open before it slams
        const int MinFloatMs = 3_000, MaxFloatMs = 6_000;  // how long an item floats before it drops
        const int MinCreakGapMs = 20_000, MaxCreakGapMs = 60_000;
        // per player, the big one on its own timer (Calm: "the occasional mix of windows opening and slamming and items
        // floating, like 5 to 10 mins random")
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

        /// <summary>25 October to 1 November (Calm, 2026-10-12: "do 25 to 1st") - 1 November keeps a Halloween weekend that ends on
        /// a Sunday, and the late night after Halloween.</summary>
        public static bool InSeason(DateTime now) => now.Month == 10 && now.Day >= 25 || now.Month == 11 && now.Day == 1;

        /// <summary>
        /// The hidden off switch (Calm: "if anyone asks I can tell them how to turn it off"): "/udhaunt off" writes
        /// ModConfig/universaldisplaylib-halloween.json, "/udhaunt on" turns it back on. A file of its own, made only by the
        /// command, so nothing in the normal settings gives the surprise away.
        /// </summary>
        public class HauntSwitch { public bool Off { get; set; } }
        const string SwitchFile = "universaldisplaylib-halloween.json";
        bool switchedOff;

        void Tick(float dt)
        {
            if (!forced && (switchedOff || !InSeason(DateTime.Now))) { nextAt.Clear(); nextCreakAt.Clear(); nextBigAt.Clear(); return; }
            long now = sapi.World.ElapsedMilliseconds;
            foreach (IPlayer p in sapi.World.AllOnlinePlayers)
            {
                if (p is not IServerPlayer sp || sp.ConnectionState != EnumClientState.Playing || p.Entity == null) continue;

                // the big one: a gust of windows slamming while items float from several windows at once
                if (!nextBigAt.TryGetValue(p.PlayerUID, out long bigAt)) nextBigAt[p.PlayerUID] = now + rnd.Next(MinBigGapMs, MaxBigGapMs + 1);
                else if (now >= bigAt)
                {
                    nextBigAt[p.PlayerUID] = now + rnd.Next(MinBigGapMs, MaxBigGapMs + 1);
                    Big(p);
                }

                // the occasional random creak, on its own timer: a nearby window creaks without moving
                if (!nextCreakAt.TryGetValue(p.PlayerUID, out long creakAt)) nextCreakAt[p.PlayerUID] = now + rnd.Next(MinCreakGapMs, MaxCreakGapMs + 1);
                else if (now >= creakAt)
                {
                    nextCreakAt[p.PlayerUID] = now + rnd.Next(MinCreakGapMs, MaxCreakGapMs + 1);
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
            // two or three windows rattle - rapidly flapping open and shut (Calm: "have the windows rapidly open and close for a bit")
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
        /// A random window near the player that can open (a few tries, as a blocked or busy one says no) - or, about one
        /// turn in four, a GUST (Calm, 2026-10-12): 2 or 3 nearby windows creak open one after another, half a second to a
        /// second apart, and slam in the same order (all hang open equally long).
        /// </summary>
        bool HauntWindow(IPlayer p, int tries, bool? gust = null)
        {
            int openMs = rnd.Next(MinOpenMs, MaxOpenMs + 1);
            if (gust ?? rnd.Next(4) == 0) return Gust(p, openMs);
            return TryNear(p, tries, be => be.Haunt(rnd, openMs));
        }

        /// <param name="big">the big haunt (Calm: "more windows for the big aswell and some last longer than the others"):
        /// 4-7 windows, each hanging open its own 1-5 s, so they slam out of order</param>
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
        /// displays (Calm: "a few from a few windows"), starting 0.3-0.8 s apart like a gust. Each rises to its own height.
        /// </summary>
        /// <param name="severalEach">the big haunt: each display lifts several of its items (Calm: "several on several shelves")</param>
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

        long Gap() => rnd.Next(MinGapMs, MaxGapMs + 1);
    }
}
