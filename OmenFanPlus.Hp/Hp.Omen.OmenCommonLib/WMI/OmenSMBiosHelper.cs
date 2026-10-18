using System;
using System.Management;
using System.Runtime.CompilerServices;
using Hp.Omen.OmenCommonLib.Utilities;

namespace Hp.Omen.OmenCommonLib.WMI
{
    public static class OmenSMBiosHelper
    {
        private static readonly object SystemIdLock = new object();
        private static string systemId;

        public static ManagementObjectSearcher CreateDefaultManagementObjectSearcher(string query)
            => new ManagementObjectSearcher(new ManagementScope(@"root\CIMV2"), new ObjectQuery(query), new EnumerationOptions
            {
                ReturnImmediately = true,
                Rewindable = false,
                Timeout = TimeSpan.FromSeconds(10),
            });

        public static void LogError(Exception exception, [CallerMemberName] string callerName = "")
        {
            OMENEventSource.Log.Error(
                $"OmenSMBiosHelper{(string.IsNullOrEmpty(callerName) ? string.Empty : "." + callerName)} Exception: {exception}");
        }

        /// <summary>
        /// The board ID HP uses to tell models apart (Win32_BaseBoard.Product, e.g. "8574"). Empty string until
        /// WMI answers; a failed query is not remembered, so the next call asks again.
        /// </summary>
        public static string SystemID
        {
            get
            {
                if (systemId != null)
                    return systemId;

                lock (SystemIdLock)
                {
                    if (systemId != null)
                        return systemId;

                    try
                    {
                        using var mngObjSearcher =
                            CreateDefaultManagementObjectSearcher("SELECT Product FROM Win32_BaseBoard");
                        using var mngObjCollection = mngObjSearcher.Get();
                        foreach (var mngBaseObject in mngObjCollection)
                        {
                            var product = mngBaseObject["Product"]?.ToString();
                            if (!string.IsNullOrWhiteSpace(product))
                                systemId = product.Trim();
                        }
                    }
                    catch (Exception ex)
                    {
                        LogError(ex);
                    }

                    return systemId ?? string.Empty;
                }
            }
        }
    }
}
