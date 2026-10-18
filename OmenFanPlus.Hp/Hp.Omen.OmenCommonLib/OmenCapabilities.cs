using System;
using System.Collections.Generic;
using System.Linq;
using Hp.Omen.OmenCommonLib.PowerControl.Enum;
using Hp.Omen.OmenCommonLib.Utilities;
using Hp.Omen.OmenCommonLib.WMI;

namespace Hp.Omen.OmenCommonLib
{
    /// <summary>
    /// What this laptop's BIOS actually supports, found by asking it with read-only commands.
    /// </summary>
    public sealed class OmenCapabilities
    {
        /// <summary>
        /// Boards HP lists as "VICTUS Laptop" in OMEN Gaming Hub's device list (plus 88F8/8A25, the two the
        /// Linux hp-wmi driver sends Quiet to). These take Quiet (3) as their third thermal mode; OMEN boards
        /// take Cool (2 on V0, mapped to 0x50 on V1).
        /// </summary>
        private static readonly HashSet<string> VictusBoards = new(StringComparer.OrdinalIgnoreCase)
        {
            "88EB", "88EC", "88ED", "88EE", "88F8", "88F9", "88FA", "88FF", "8900", "8934",
            "8A21", "8A22", "8A25", "8A26", "8A27", "8A3C", "8A3D", "8A3E", "8A3F", "8A4F", "8A50",
            "8BB1", "8BB2", "8BB3", "8BB4", "8BBD", "8BBE", "8BC1", "8BC2", "8BC8", "8BCA", "8BCB", "8BD4", "8BD5",
            "8C2D", "8C2E", "8C2F", "8C30", "8C3F", "8C40", "8C4D", "8C4E", "8C99", "8C9A", "8C9C", "8C9D",
        };

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
        /// Lowest and highest fan level (units of 100 RPM) the custom curve maps onto. OmenMon observed 0x15-0x39
        /// on boards with manual control, so 20 and 55 are used until a board's 0x2F fan table has been decoded.
        /// </summary>
        public int FanLevelMin { get; private init; } = 20;
        public int FanLevelMax { get; private init; } = 55;

        public bool ManualFanControlSupported => FanLevelSupported;

        /// <summary>HP's Victus boards use Quiet (3) as the third thermal mode; OMEN boards use Cool (2).</summary>
        public bool IsVictusBoard => VictusBoards.Contains(SystemId ?? "");

        /// <summary>The mode sent by the third mode button.</summary>
        public PerformanceMode QuietOrCoolMode => IsVictusBoard ? PerformanceMode.Quiet : PerformanceMode.Cool;

        public static OmenCapabilities Probe(OmenHsaClient client)
        {
            var fanLevel = client.TryBiosWmiCmd_Get(131080, 0x2D, new byte[4], 128, out _, quiet: true);
            var maxFan = client.TryBiosWmiCmd_Get(131080, 0x26, new byte[4], 4, out _, quiet: true);
            var designData = client.TryBiosWmiCmd_Get(131080, 0x28, new byte[4], 128, out var designDataBytes, quiet: true);
            client.SeedSystemDesignData(designData, designDataBytes);
            var fanRpm = client.TryBiosWmiCmd_Get(131080, 0x11, new byte[] { 0 }, 128, out _, quiet: true);

            var biosAvailable = fanLevel >= 0 || maxFan >= 0 || designData >= 0 || fanRpm >= 0;

            if (fanLevel == 0)
            {
                // Logged so the layout can be decoded from a real board one day; not interpreted yet.
                var table = client.GetFanTableRaw();
                if (table != null)
                    OMENEventSource.Log.Info("Fan table (0x2F) raw: " + BitConverter.ToString(table.Take(48).ToArray()));
            }

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

        /// <summary>What the app assumes while the BIOS hasn't answered at all (probe failed before it could run).</summary>
        public static OmenCapabilities Unavailable() => new()
        {
            FanLevelReturnCode = -1, MaxFanReturnCode = -1, SystemDesignDataReturnCode = -1, FanRpmReturnCode = -1,
            SystemId = OmenSMBiosHelper.SystemID,
        };

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
