using Hp.Omen.OmenCommonLib.PowerControl.Enum;

namespace OmenFanPlus.Services
{
    /// <summary>
    /// The modes a user can pick for a power source or cycle through with the OMEN key: the three thermal
    /// profiles plus Auto. Max, Off and the custom curve are deliberate manual states and aren't part of this.
    /// </summary>
    public enum ModeChoice
    {
        /// <summary>The calm profile: Cool (shown as Comfort) on OMEN boards, Quiet on Victus.</summary>
        Comfort,
        Default,
        Performance,
        Auto,
    }

    internal static class ModeChoices
    {
        public static readonly ModeChoice[] All = { ModeChoice.Comfort, ModeChoice.Default, ModeChoice.Performance, ModeChoice.Auto };

        /// <summary>The settings text for a choice ("Comfort", "Default", "Performance", "Auto").</summary>
        public static string Key(ModeChoice choice) => choice.ToString();

        /// <summary>Reads a settings text; "Cool" and "Quiet" count as Comfort. Null for anything else.</summary>
        public static ModeChoice? Parse(string? text)
        {
            var key = text?.Trim() ?? "";
            if (key.Equals("Cool", StringComparison.OrdinalIgnoreCase) || key.Equals("Quiet", StringComparison.OrdinalIgnoreCase))
                return ModeChoice.Comfort;
            return Enum.TryParse<ModeChoice>(key, ignoreCase: true, out var choice) && Enum.IsDefined(choice) ? choice : null;
        }

        /// <summary>The choice the saved mode amounts to; null while Max, Off or the custom curve is active.</summary>
        public static ModeChoice? FromSaved(bool fanAuto, PerformanceMode mode, bool manualActive)
        {
            if (manualActive)
                return null;
            if (fanAuto)
                return ModeChoice.Auto;
            return mode switch
            {
                PerformanceMode.Performance => ModeChoice.Performance,
                PerformanceMode.Default => ModeChoice.Default,
                PerformanceMode.Cool or PerformanceMode.Quiet => ModeChoice.Comfort,
                _ => null,
            };
        }

        /// <summary>The thermal profile behind a choice (not for Auto, which is a mode of its own).</summary>
        public static PerformanceMode ToPerformanceMode(ModeChoice choice, PerformanceMode calmMode) => choice switch
        {
            ModeChoice.Comfort => calmMode,
            ModeChoice.Default => PerformanceMode.Default,
            ModeChoice.Performance => PerformanceMode.Performance,
            _ => throw new ArgumentOutOfRangeException(nameof(choice), choice, "Auto has no single thermal profile"),
        };

        public static string DisplayName(ModeChoice choice, PerformanceMode calmMode) => choice switch
        {
            ModeChoice.Comfort => FanModeController.DisplayName(calmMode),
            ModeChoice.Auto => "Auto",
            _ => FanModeController.DisplayName(ToPerformanceMode(choice, calmMode)),
        };

        /// <summary>
        /// What the OMEN key switches to next: Comfort, Default, Performance, Auto, and round again. From a manual
        /// state (null) it starts at Comfort. On battery Performance is skipped, since the firmware refuses it
        /// there (Auto never picks it on battery either).
        /// </summary>
        public static ModeChoice Next(ModeChoice? current, bool onBattery)
        {
            var next = current switch
            {
                null => ModeChoice.Comfort,
                ModeChoice.Comfort => ModeChoice.Default,
                ModeChoice.Default => ModeChoice.Performance,
                ModeChoice.Performance => ModeChoice.Auto,
                _ => ModeChoice.Comfort,
            };
            if (next == ModeChoice.Performance && onBattery)
                next = ModeChoice.Auto;
            return next;
        }
    }
}
