using System.Linq;
using Vintagestory.API.Common;

namespace UniversalDisplayLib
{
    /// <summary>
    /// RAIN ON THE GLASS. The drops themselves are RainDrops in the glass layer. What is left here is the one test: is
    /// rain falling on this glass now.
    /// </summary>
    public partial class BEUniversalDisplay
    {
        /// <summary>
        /// Whether rain (not snow) is falling on this glass now: the setting on, above 0 C, rain where the window is, and its
        /// glass open to the weather (open sky beside it, or an OPEN External Shutter in front).
        /// </summary>
        bool RainReaches()
        {
            if (UniversalDisplayLibConfig.Current?.RainOnWindows == false) return false;
            float temp = capi.World.BlockAccessor.GetClimateAt(Pos, EnumGetClimateMode.NowValues)?.Temperature ?? 10f;
            if (temp <= 0f || WindWeather.PrecipitationAt(capi, Pos) <= 0.05f) return false;
            return GlassOpenToWeather(leavesCover: true);   // rain does not get through leaves
        }

        /// <summary>The glass faces the weather: open sky beside it, or an External Shutter in front that is OPEN.</summary>
        bool GlassOpenToWeather(bool leavesCover = false)
        {
            BEUniversalDisplay shutter = ShutterInFront();
            if (shutter != null) return shutter.paneStates.Any(open => open);
            return ExposedToWeather(out _, leavesCover);
        }
    }
}
