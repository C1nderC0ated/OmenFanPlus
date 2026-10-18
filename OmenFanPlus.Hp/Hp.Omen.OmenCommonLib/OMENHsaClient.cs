using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Hp.Ohl.WmiService;
using Hp.Omen.AppShim;
using Hp.Omen.OmenCommonLib.PowerControl.Enum;
using Hp.Omen.OmenCommonLib.Utilities;
using Hp.Omen.OmenCommonLib.WMI;

namespace Hp.Omen.OmenCommonLib
{
    public class OmenHsaClient
    {
        /// <summary>Returned instead of a BIOS code when this code refuses to send a command.</summary>
        public const int UnsupportedReturnCode = -2;

        private static readonly object SystemDesignDataLock = new();
        private static byte[] _systemDesignData;
        private static bool _systemDesignDataUnsupported;

        public byte[] SystemDesignData
        {
            get
            {
                lock (SystemDesignDataLock)
                {
                    if (_systemDesignData == null && !_systemDesignDataUnsupported)
                    {
                        _systemDesignData = (byte[])CurrentApp.Properties["SystemDesignData"];
                        if (_systemDesignData == null)
                        {
                            var rc = TryBiosWmiCmd_Get(131080, 40, new byte[4], 128, out var data);
                            SeedSystemDesignData(rc, data);
                        }
                    }

                    return _systemDesignData;
                }
            }
        }

        /// <summary>
        /// Stores an answer to 0x28 GetSystemDesignData. Older boards (e.g. the 2019 OMEN 15-dc1xxx, 8574) say
        /// "command not available"; that is remembered so the BIOS isn't asked on every mode change. A WMI failure
        /// (-1) or a bad-signature answer (2) is not remembered, so the next call asks again.
        /// </summary>
        public void SeedSystemDesignData(int returnCode, byte[] data)
        {
            lock (SystemDesignDataLock)
            {
                if (returnCode == 0 && data != null)
                {
                    _systemDesignData = data;
                    CurrentApp.Properties["SystemDesignData"] = data;
                }
                else if (returnCode > 0 && returnCode != 2)
                {
                    _systemDesignDataUnsupported = true;
                }
            }
        }

        private void OutputLogError(string message)
        {
            OMENEventSource.Log.Error($"{this} : {message}");
        }

        public byte[] BiosWmiCmd_Get(uint commandType)
        {
            return BiosWmiCmd_Get(131081, commandType, new byte[1], 128);
        }

        public byte[] BiosWmiCmd_Get(uint command, uint commandType, byte[] inputData,
            uint returnDataSize)
        {
            try
            {
                var result = HpBiosIntHelper.InvokeBiosCommand(command, commandType, returnDataSize, inputData);
                return result.Data;
            }
            catch (Exception ex)
            {
                OutputLogError(ex.Message);
            }

            return null;
        }

        /// <summary>
        /// Like <see cref="BiosWmiCmd_Get(uint, uint, byte[], uint)"/>, but reports why a command failed.
        /// Returns 0 on success, the BIOS rwReturnCode if the BIOS rejected the command, or -1 if WMI itself failed.
        /// </summary>
        /// <param name="quiet">A "not available" answer (rc 3/4) is expected (capability probe): log it as info.</param>
        public int TryBiosWmiCmd_Get(uint command, uint commandType, byte[] inputData, uint returnDataSize, out byte[] data,
            bool quiet = false)
        {
            data = null;
            try
            {
                data = HpBiosIntHelper.InvokeBiosCommand(command, commandType, returnDataSize, inputData).Data;
                return 0;
            }
            catch (HpBiosException ex)
            {
                var text = $"command 0x{command:X} type 0x{commandType:X2}: {ex.Message}";
                if (quiet && ex.ReturnCode is 3 or 4)
                    OMENEventSource.Log.Info(text);
                else
                    OutputLogError(text);
                return (int)ex.ReturnCode;
            }
            catch (Exception ex)
            {
                OutputLogError($"command 0x{command:X} type 0x{commandType:X2}: {ex.Message}");
                return -1;
            }
        }

        public int BiosWmiCmd_Set(uint commandType, byte[] inputData)
        {
            return BiosWmiCmd_Set(131081, commandType, inputData);
        }

        /// <summary>
        /// Returns 0 on success, the BIOS rwReturnCode if the BIOS rejected the command, or -1 if WMI itself failed.
        /// </summary>
        public int BiosWmiCmd_Set(uint command, uint commandType, byte[] inputData)
        {
            try
            {
                HpBiosIntHelper.InvokeBiosCommand(command, commandType, 4, inputData);
                return 0;
            }
            catch (HpBiosException ex)
            {
                OutputLogError($"command 0x{command:X} type 0x{commandType:X2}: {ex.Message}");
                return (int)ex.ReturnCode;
            }
            catch (Exception ex)
            {
                OutputLogError($"command 0x{command:X} type 0x{commandType:X2}: {ex.Message}");
            }

            return -1;
        }

        public byte[] BiosWmiCmd_GetColor()
        {
            return BiosWmiCmd_Get(2);
        }

        public int BiosWmiCmd_SetColor(byte[] inputData)
        {
            return BiosWmiCmd_Set(3, inputData);
        }

        public byte[] BiosWmiCmd_GetBrightness()
        {
            return BiosWmiCmd_Get(4);
        }

        public int BiosWmiCmd_SetBrightness(byte[] inputData)
        {
            return BiosWmiCmd_Set(5, inputData);
        }

        public int GetKeyboardBrightness()
        {
            var array = BiosWmiCmd_GetBrightness();
            if (array != null && array.Length >= 1)
            {
                return array[0] & 128;
            }

            throw new Exception("BIOS returned malformed message");
        }

        public int SetKeyboardBrightness(byte brightness)
        {
            var d = new byte[4];
            d[0] = brightness;
            return BiosWmiCmd_SetBrightness(d);
        }

        public ThermalPolicyVersion GetThermalPolicyVersion()
        {
            ThermalPolicyVersion thermalPolicyVersion = ThermalPolicyVersion.V0;
            byte[] systemDesignData = this.SystemDesignData;
            if (systemDesignData != null && systemDesignData.Length != 0)
                thermalPolicyVersion = (ThermalPolicyVersion)systemDesignData[3];
            if (((IEnumerable<string>)new string[6]
            {
                "8607",
                "8746",
                "8747",
                "8749",
                "874A",
                "8748"
            }).Contains<string>(OmenSMBiosHelper.SystemID))
                thermalPolicyVersion = ThermalPolicyVersion.V0;
            OMENEventSource.Log.Verbose("GetThermalPolicyVersion(), version = " + thermalPolicyVersion.ToString());
            return thermalPolicyVersion;
        }

        /// <summary>
        /// Raw answer of the read-only 0x2F fan-table query (fan levels per temperature step), or null.
        /// Only logged for now; the layout hasn't been verified on a board that answers it.
        /// </summary>
        public byte[] GetFanTableRaw()
        {
            var rc = TryBiosWmiCmd_Get(131080, 0x2F, new byte[4], 128, out var data, quiet: true);
            return rc == 0 ? data : null;
        }

        /// <summary>
        /// Speed of one fan (index 0 or 1) in RPM via the read-only 0x11 query, or -1 if the BIOS can't report it.
        /// This is the query the Linux hp-wmi driver uses for fan speed; it works on boards without 0x2D
        /// (e.g. the OMEN 15-dc1xxx, 8574). The speed is big-endian in bytes 2-3 of the answer.
        /// </summary>
        public int GetFanRpm(int fanIndex)
        {
            var rc = TryBiosWmiCmd_Get(131080, 0x11, new[] { (byte)fanIndex }, 128, out var data);
            if (rc != 0 || data == null || data.Length < 4)
                return -1;
            return (data[2] << 8) | data[3];
        }

        /// <summary>
        /// Current level of each fan (CPU, GPU) in units of 100 RPM. Empty if the BIOS doesn't support 0x2D.
        /// </summary>
        public List<byte> GetFanLevel()
        {
            var data = new List<byte>();
            var result = BiosWmiCmd_Get(131080, 45, new byte[4], 128);
            if (result != null && result.Length >= 2)
            {
                // Read by position: a stopped fan reports 0, which must not hide the fans after it.
                data.Add(result[0]);
                data.Add(result[1]);
            }

            return data;
        }

        public int SetSwFanControlLevel(int cpuFanLevel, int gpuFanLevel)
        {
            int num = BiosWmiCmd_Set(131080, 46, new byte[2]
            {
                Convert.ToByte(cpuFanLevel),
                Convert.ToByte(gpuFanLevel)
            });

            return num;
        }

        public int SetFanMode(PerformanceMode mode, bool isMapped = true)
        {
            byte[] array = new byte[2]
            {
                255,
                (byte)mode
            };
            OMENEventSource.Log.Verbose("SetFanModeAsync(), mode = " + mode);
            if (isMapped)
            {
                var version = GetThermalPolicyVersion();
                switch (version)
                {
                    case ThermalPolicyVersion.V1:
                        switch (mode)
                        {
                            case PerformanceMode.Default:
                            case PerformanceMode.Eco:
                                mode = PerformanceMode.L2;
                                break;
                            case PerformanceMode.Performance:
                                mode = PerformanceMode.L7;
                                break;
                            case PerformanceMode.Cool:
                                mode = PerformanceMode.L4;
                                break;
                        }

                        break;
                    case ThermalPolicyVersion.V0:
                        if (mode == PerformanceMode.Eco)
                        {
                            mode = PerformanceMode.Default;
                        }

                        break;
                    default:
                        // The design data names a thermal policy this code has no table for; sending a raw value
                        // could pick an undefined profile. Linux hp-wmi refuses such boards too.
                        OutputLogError($"Thermal policy version {(int)version} is unknown; not sending mode {mode}");
                        return UnsupportedReturnCode;
                }

                array[1] = (byte)mode;
            }

            int num = BiosWmiCmd_Set(131080, 26, array);

            return num;
        }

        public MaxFanMode GetMaxFan()
        {
            byte[] array = new byte[4];
            MaxFanMode mode = MaxFanMode.Off;
            byte[] array2 = BiosWmiCmd_Get(131080, 38, array, /*array.Length,*/ 4);
            if (array2 != null && array2.Length != 0)
            {
                mode = (MaxFanMode)array2[0];
            }

            return mode;
        }

        public int SetMaxFan(MaxFanMode mode)
        {
            return BiosWmiCmd_Set(131080, 39, new byte[1] { (byte)mode });
        }
    }
}