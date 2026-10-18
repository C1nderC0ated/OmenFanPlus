using Hp.Omen.OmenCommonLib.PowerControl.Enum;
using Hp.Omen.OmenCommonLib.WMI;

namespace Hp.Omen.OmenCommonLib
{
    /// <summary>
    /// What this laptop's BIOS actually supports, found by asking it with read-only commands.
    /// </summary>
    public sealed class OmenCapabilities
    {
        /// <summary>The hpqBIntM interface answered at least one command (even with an error code).</summary>
        public bool BiosAvailable { get; private init; }

        /// <summary>
        /// Every probe got a definite answer. False while WMI isn't reachable yet (e.g. right after logon);
        /// nothing should be concluded or saved from an incomplete probe.
        /// </summary>
        public bool ProbeComplete { get; private init; }

        /// <summary>0x2D GetFanLevel works, so manual fan levels (0x2E: Off, Max, Custom curve) are available.</summary>
        public bool FanLevelSupported { get; private init; }

        /// <summary>0x26 GetMaxFan works.</summary>
        public bool MaxFanSupported { get; private init; }

        /// <summary>0x11 reports fan speeds in RPM.</summary>
        public bool FanRpmSupported { get; private init; }

        /// <summary>0x28 GetSystemDesignData works (2020 and newer boards).</summary>
        public bool SystemDesignDataSupported { get; private init; }

        public ThermalPolicyVersion ThermalPolicy { get; private init; }

        public string SystemId { get; private init; }

        public int FanLevelReturnCode { get; private init; }
        public int MaxFanReturnCode { get; private init; }
        public int SystemDesignDataReturnCode { get; private init; }
        public int FanRpmReturnCode { get; private init; }

        /// <summary>
        /// Boards with neither 0x28 nor manual fan levels, such as the 2018-2019 OMEN 15-dc models, only know the
        /// original Default (0) / Performance (1) / Cool (2) thermal profiles, so the third mode is Cool rather
        /// than Quiet (3). Boards with manual fan control keep Quiet, as before.
        /// </summary>
        public bool UsesLegacyCoolMode => ProbeComplete && !SystemDesignDataSupported && !FanLevelSupported;

        public bool ManualFanControlSupported => FanLevelSupported;

        /// <summary>The mode sent by the third mode button.</summary>
        public PerformanceMode QuietOrCoolMode => UsesLegacyCoolMode ? PerformanceMode.Cool : PerformanceMode.Quiet;

        public static OmenCapabilities Probe(OmenHsaClient client)
        {
            var fanLevel = client.TryBiosWmiCmd_Get(131080, 0x2D, new byte[4], 128, out _);
            var maxFan = client.TryBiosWmiCmd_Get(131080, 0x26, new byte[4], 4, out _);
            var designData = client.TryBiosWmiCmd_Get(131080, 0x28, new byte[4], 128, out var designDataBytes);
            client.SeedSystemDesignData(designData, designDataBytes);
            var fanRpm = client.TryBiosWmiCmd_Get(131080, 0x11, new byte[] { 0 }, 128, out _);

            var biosAvailable = fanLevel >= 0 || maxFan >= 0 || designData >= 0 || fanRpm >= 0;

            return new OmenCapabilities
            {
                BiosAvailable = biosAvailable,
                // 0x11 only feeds the fan-speed label, so it doesn't hold back re-applying the saved mode.
                ProbeComplete = IsDefinite(fanLevel) && IsDefinite(maxFan) && IsDefinite(designData),
                FanLevelSupported = fanLevel == 0,
                MaxFanSupported = maxFan == 0,
                SystemDesignDataSupported = designData == 0,
                FanRpmSupported = fanRpm == 0,
                FanLevelReturnCode = fanLevel,
                MaxFanReturnCode = maxFan,
                SystemDesignDataReturnCode = designData,
                FanRpmReturnCode = fanRpm,
                ThermalPolicy = biosAvailable ? client.GetThermalPolicyVersion() : ThermalPolicyVersion.V0,
                SystemId = OmenSMBiosHelper.SystemID,
            };
        }

        // -1 = WMI itself failed, 2 = wrong signature: ask again later. Anything else is the BIOS's answer.
        private static bool IsDefinite(int returnCode) => returnCode >= 0 && returnCode != 2;

        public override string ToString() =>
            $"SystemID={SystemId}, BIOS available={BiosAvailable}, probe complete={ProbeComplete}, " +
            $"GetFanLevel(0x2D) rc={FanLevelReturnCode}, GetMaxFan(0x26) rc={MaxFanReturnCode}, " +
            $"SystemDesignData(0x28) rc={SystemDesignDataReturnCode}, FanRpm(0x11) rc={FanRpmReturnCode}, " +
            $"ThermalPolicy={ThermalPolicy}, " +
            $"manual fan control={ManualFanControlSupported}, third mode={QuietOrCoolMode}";
    }
}
