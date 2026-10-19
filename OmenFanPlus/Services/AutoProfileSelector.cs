using Hp.Omen.OmenCommonLib.PowerControl.Enum;

namespace OmenFanPlus.Services
{
    /// <summary>
    /// Picks a thermal profile from the temperature, for laptops whose BIOS has no fan-speed control but does
    /// switch profiles: the calm profile (Comfort, or Quiet on Victus boards) when cool, Default in between,
    /// Performance when hot. Pure logic: nothing here touches the hardware.
    /// </summary>
    internal sealed class AutoProfileSelector
    {
        public const int MinThreshold = 30;
        public const int MaxThreshold = 100;
        public const int MinHysteresis = 1;
        public const int MaxHysteresis = 15;

        /// <summary>A profile change is held for at least this long, so sensor noise can't flip it back at once.</summary>
        public static readonly TimeSpan MinimumDwell = TimeSpan.FromSeconds(15);

        /// <summary>Below this temperature the calm profile is used.</summary>
        public int ComfortBelow { get; }

        /// <summary>At or above this temperature Performance is used.</summary>
        public int PerformanceAbove { get; }

        /// <summary>How far the temperature has to fall below a threshold before stepping back down.</summary>
        public int Hysteresis { get; }

        /// <summary>The calm profile of this board: Cool (shown as Comfort) on OMEN, Quiet on Victus.</summary>
        public PerformanceMode CalmProfile { get; }

        /// <summary>The profile chosen last; null before the first evaluation.</summary>
        public PerformanceMode? Current { get; private set; }

        public DateTime LastChangeUtc { get; private set; } = DateTime.MinValue;

        public AutoProfileSelector(int comfortBelow, int performanceAbove, int hysteresis, PerformanceMode calmProfile)
        {
            if (!TryValidate(comfortBelow, performanceAbove, hysteresis, out var error))
                throw new ArgumentException(error);
            ComfortBelow = comfortBelow;
            PerformanceAbove = performanceAbove;
            Hysteresis = hysteresis;
            CalmProfile = calmProfile;
        }

        public static bool TryValidate(int comfortBelow, int performanceAbove, int hysteresis, out string? error)
        {
            error = null;
            if (comfortBelow is < MinThreshold or > MaxThreshold || performanceAbove is < MinThreshold or > MaxThreshold)
                error = $"Thresholds must be between {MinThreshold} and {MaxThreshold} °C.";
            else if (hysteresis is < MinHysteresis or > MaxHysteresis)
                error = $"Hysteresis must be between {MinHysteresis} and {MaxHysteresis} °C.";
            else if (performanceAbove - comfortBelow < hysteresis)
                error = "The Performance threshold must be at least the hysteresis above the Comfort threshold.";
            return error == null;
        }

        /// <summary>The profile the thresholds alone would pick, with no hysteresis or dwell (for previews).</summary>
        public PerformanceMode Band(float temperature, bool onBattery)
        {
            if (temperature < ComfortBelow)
                return CalmProfile;
            if (temperature >= PerformanceAbove && !onBattery)
                return PerformanceMode.Performance;
            return PerformanceMode.Default;
        }

        /// <summary>
        /// Updates <see cref="Current"/> for a new reading and returns it. Steps up as soon as a threshold is
        /// reached, steps down only once the temperature is a hysteresis below it, and never changes twice
        /// within <see cref="MinimumDwell"/>. On battery Performance is never chosen (the firmware refuses it).
        /// </summary>
        public PerformanceMode Next(float temperature, bool onBattery, DateTime nowUtc)
        {
            if (Current == null)
            {
                Current = Band(temperature, onBattery);
                LastChangeUtc = nowUtc;
                return Current.Value;
            }

            var wanted = Current.Value;
            switch (Current.Value)
            {
                case PerformanceMode.Performance:
                    if (onBattery)
                        wanted = temperature < ComfortBelow ? CalmProfile : PerformanceMode.Default;
                    else if (temperature <= PerformanceAbove - Hysteresis)
                        wanted = temperature < ComfortBelow ? CalmProfile : PerformanceMode.Default;
                    break;
                case PerformanceMode.Default:
                    if (temperature >= PerformanceAbove && !onBattery)
                        wanted = PerformanceMode.Performance;
                    else if (temperature <= ComfortBelow - Hysteresis)
                        wanted = CalmProfile;
                    break;
                default: // the calm profile
                    if (temperature >= PerformanceAbove && !onBattery)
                        wanted = PerformanceMode.Performance;
                    else if (temperature >= ComfortBelow)
                        wanted = PerformanceMode.Default;
                    break;
            }

            // Leaving Performance because the charger was pulled is not noise, so it isn't held back.
            var leavingPerformanceOnBattery = Current == PerformanceMode.Performance && onBattery;
            if (wanted != Current && (leavingPerformanceOnBattery || nowUtc - LastChangeUtc >= MinimumDwell))
            {
                Current = wanted;
                LastChangeUtc = nowUtc;
            }

            return Current.Value;
        }

        /// <summary>Forgets the current profile, so the next reading picks the band directly.</summary>
        public void Reset()
        {
            Current = null;
            LastChangeUtc = DateTime.MinValue;
        }
    }
}
