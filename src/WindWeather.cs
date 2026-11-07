using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace UniversalDisplayLib
{
    /// <summary>
    /// WINDOWS IN THE WIND. In a strong breeze or a storm, windows with open sky beside them WOBBLE - rock a little
    /// open and back, on each player's screen only (WindWobble.cs) - and CREAK now and then (this system, on the
    /// server).
    ///
    /// How often they creak is the WindWindowFrequency setting (0 = no creaks, 5 = normal, 10 = twice as often) - the
    /// same setting sets the pace of the Halloween windows (HalloweenWindows.Paced). The glass rattle is
    /// WindGlassRattle (on / off, at the normal pace); the rocking is each player's WindRocking.
    /// </summary>
    public class WindWeather : ModSystem
    {
        /// <summary>
        /// BY THE WIND PATTERN, NOT THE LIVE SPEED. The game's patterns (game/config/windpatterns): still 0, light
        /// breeze 0.15, medium 0.3, strong breeze 0.6 (+-0.15), storm 1 - and the live speed has large gust noise on
        /// top, so a medium breeze can gust past any speed line. A region whose pattern is at least this strong moves
        /// windows (a modded pattern too).
        /// </summary>
        const float StrongPattern = 0.5f, StrongBreeze = 0.6f, Storm = 1.0f;
        const int RangeBlocks = 24;
        /// <summary>Seconds between creaks near one player at the normal pace.</summary>
        const float CreakAtStrong = 20f, CreakAtStorm = 6f;
        /// <summary>Seconds between glass rattles near one player at the normal pace.</summary>
        const float RattleAtStrong = 30f, RattleAtStorm = 8f;

        ICoreServerAPI sapi;
        /// <summary>Each side's weather system, found once at start (singleplayer runs both in one process).</summary>
        static WeatherSystemBase serverWeather, clientWeather;
        /// <summary>The game's temporal storm system on the client (it is sent each storm's strength and end).</summary>
        static SystemTemporalStability clientTemporal;
        /// <summary>The storm's last part, in days, when the game says it "seems to be waning" - the grime clears from there.</summary>
        const double StormWaningDays = 0.02;
        readonly Random rnd = new();
        readonly Dictionary<string, long> nextCreakAt = new();
        readonly Dictionary<string, long> nextRattleAt = new();
        WobbleRenderer wobbleRenderer;
        GlassLayerRenderer layerRenderer;
        ICoreClientAPI capi;

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            serverWeather = api.ModLoader.GetModSystem<WeatherSystemServer>();
            api.Event.RegisterGameTickListener(Tick, 2000, 2000);
            api.Event.PlayerDisconnect += p => { nextCreakAt.Remove(p.PlayerUID); nextRattleAt.Remove(p.PlayerUID); };
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            clientWeather = api.ModLoader.GetModSystem<WeatherSystemClient>();
            clientTemporal = api.ModLoader.GetModSystem<SystemTemporalStability>();
            BEUniversalDisplay.Wobbling.Clear();
            BEUniversalDisplay.ForgetAllGlass();
            wobbleRenderer = new WobbleRenderer();
            api.Event.RegisterRenderer(wobbleRenderer, EnumRenderStage.Before, "universaldisplaylib-wobble");
            BEUniversalDisplay.Layered.Clear();
            layerRenderer = new GlassLayerRenderer(api);
            api.Event.RegisterRenderer(layerRenderer, EnumRenderStage.AfterOIT, "universaldisplaylib-glasslayer");
        }

        public override void Dispose()
        {
            if (sapi != null) serverWeather = null;
            if (capi != null) { clientWeather = null; clientTemporal = null; }
            if (capi != null && wobbleRenderer != null) capi.Event.UnregisterRenderer(wobbleRenderer, EnumRenderStage.Before);
            if (capi != null && layerRenderer != null) capi.Event.UnregisterRenderer(layerRenderer, EnumRenderStage.AfterOIT);
            BEUniversalDisplay.Layered.Clear();
            BEUniversalDisplay.Wobbling.Clear();
        }

        /// <summary>The pace from the setting: 1 normal, 0 never, 2 at the most.</summary>
        public static float Pace => Math.Clamp(UniversalDisplayLibConfig.Current?.WindWindowFrequency ?? 5, 0, 10) / 5f;


        void Tick(float dt)
        {
            float pace = Pace;
            bool rattles = UniversalDisplayLibConfig.Current?.WindGlassRattle != false;
            if (pace <= 0f) nextCreakAt.Clear();
            if (!rattles) nextRattleAt.Clear();
            if (pace <= 0f && !rattles) return;
            long now = sapi.World.ElapsedMilliseconds;
            foreach (IPlayer p in sapi.World.AllOnlinePlayers)
            {
                if (p is not IServerPlayer sp || sp.ConnectionState != EnumClientState.Playing || p.Entity == null) continue;
                float strength = StrengthAt(sapi, p.Entity.Pos.AsBlockPos);
                if (strength < 0f) { nextCreakAt.Remove(p.PlayerUID); nextRattleAt.Remove(p.PlayerUID); continue; }

                // glass rattling in its frame, on its own timer and at the normal pace (its own on / off setting)
                if (!rattles) { }
                else if (!nextRattleAt.TryGetValue(p.PlayerUID, out long rattleAt)) nextRattleAt[p.PlayerUID] = now + Paced(strength, 1f, RattleAtStrong, RattleAtStorm);
                else if (now >= rattleAt)
                {
                    nextRattleAt[p.PlayerUID] = now + Paced(strength, 1f, RattleAtStrong, RattleAtStorm);
                    Rattle(p);
                }

                // a window creaking in the wind - latched ones too, the wood still moves
                if (pace <= 0f) continue;
                if (!nextCreakAt.TryGetValue(p.PlayerUID, out long creakAt)) nextCreakAt[p.PlayerUID] = now + Paced(strength, pace);
                else if (now >= creakAt)
                {
                    nextCreakAt[p.PlayerUID] = now + Paced(strength, pace);
                    Creak(p);
                }
            }
        }

        /// <summary>
        /// Either side: how strong the wind pattern is over this spot - 0 a strong breeze up to 1 a storm, -1 when it is
        /// weaker than a strong breeze (nothing moves). Its region's pattern from the game's weather simulation.
        /// </summary>
        public static float StrengthAt(ICoreAPI api, BlockPos pos)
        {
            // looked up once at start: searching the mod systems is costly
            WeatherSystemBase weather = api.Side == EnumAppSide.Server ? serverWeather : clientWeather;
            if (weather == null) return -1f;
            int size = api.World.BlockAccessor.RegionSize;
            float avg = weather.getOrCreateWeatherSimForRegion(pos.X / size, pos.Z / size)?.CurWindPattern?.config?.Strength?.avg ?? 0f;
            if (avg < StrongPattern) return -1f;
            return Math.Clamp((avg - StrongBreeze) / (Storm - StrongBreeze), 0f, 1f);
        }

        /// <summary>Either side: how hard it is raining or snowing at this spot now, 0 to 1 (the game's weather; 0 if unknown).</summary>
        public static float PrecipitationAt(ICoreAPI api, BlockPos pos)
        {
            WeatherSystemBase weather = api.Side == EnumAppSide.Server ? serverWeather : clientWeather;
            return weather?.GetPrecipitation(pos.X + 0.5, pos.Y + 0.5, pos.Z + 0.5) ?? 0f;
        }

        /// <summary>
        /// Client: how thick the temporal storm grime is now, 0 none to 1 the thickest. By the storm's strength - the
        /// game sets it per storm, light 0.53-0.63, medium 0.67-0.77, heavy 0.9-1.0 (vssurvivalmod
        /// TemporalStability.cs) - so a light storm leaves a thin film, a heavy one near-solid grime; it covers the
        /// whole window either way. 0 when there is no storm, and from the WANING (the storm's last StormWaningDays) so
        /// the grime clears back as the storm ends.
        /// </summary>
        public static float StormGrimeThickness(ICoreAPI api)
        {
            TemporalStormRunTimeData storm = clientTemporal?.StormData;
            if (storm == null || !storm.nowStormActive) return 0f;
            if (storm.stormActiveTotalDays - api.World.Calendar.TotalDays < StormWaningDays) return 0f;
            return Math.Clamp((storm.stormGlitchStrength - 0.45f) / 0.55f, 0.15f, 1f);
        }

        /// <summary>The wait to the next creak: 20 s (a strong breeze) down to 6 s (a storm), divided by the pace, give or take a third.</summary>
        long Paced(float s, float pace) => Paced(s, pace, CreakAtStrong, CreakAtStorm);

        long Paced(float s, float pace, float atStrong, float atStorm)
        {
            float seconds = atStrong + (atStorm - atStrong) * s;
            return (long)(seconds * 1000f / pace * (0.67f + (float)rnd.NextDouble() * 0.66f));
        }

        /// <summary>A window with glass and open sky beside it, near the player, rattles.</summary>
        void Rattle(IPlayer p)
        {
            foreach (BEUniversalDisplay be in BEUniversalDisplay.ServerWindowsNear(p.Entity.Pos.AsBlockPos, RangeBlocks)
                         .Where(be => be.ExposedToWeather(out _) && StrengthAt(sapi, be.Pos) >= 0f).OrderBy(_ => rnd.Next()))
                if (be.WindRattle(rnd)) return;
        }

        /// <summary>A window with open sky beside it, near the player, creaks without moving.</summary>
        void Creak(IPlayer p)
        {
            foreach (BEUniversalDisplay be in BEUniversalDisplay.ServerWindowsNear(p.Entity.Pos.AsBlockPos, RangeBlocks)
                         .Where(be => be.ExposedToWeather(out _) && StrengthAt(sapi, be.Pos) >= 0f).OrderBy(_ => rnd.Next()))
                if (be.WindCreak(rnd)) return;
        }
    }
}
