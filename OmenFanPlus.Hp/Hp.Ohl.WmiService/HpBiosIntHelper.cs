using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using Hp.Ohl.WmiService.Models;
using Hp.Omen.OmenCommonLib.Utilities;

namespace Hp.Ohl.WmiService
{
    /// <summary>
    /// The BIOS answered a WMI command with a non-zero rwReturnCode
    /// (2 = wrong signature, 3 = command not available, 4 = unknown command type, 5 = invalid parameters/size).
    /// </summary>
    public class HpBiosException : Exception
    {
        public uint ReturnCode { get; }

        public HpBiosException(uint returnCode, string message) : base(message)
        {
            ReturnCode = returnCode;
        }
    }

    public static class HpBiosIntHelper
    {
        private static readonly byte[] Sign = {83, 69, 67, 85};
        private static readonly object CallLock = new();
        private static readonly TimeSpan CallLockTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

        // A call that timed out is still running on its worker thread. No new call is started behind it for
        // OrphanGrace (BIOS writes must stay in order), and never more than MaxOrphans of them are left hanging.
        private static readonly TimeSpan OrphanGrace = TimeSpan.FromSeconds(60);
        private const int MaxOrphans = 3;
        private static readonly List<(Task Call, DateTime TimedOutAt)> Orphans = new();

        private static volatile ManagementObject _classInstance;

        // Looked up on first use (and again after a WMI failure) rather than in a static constructor,
        // so a WMI provider that isn't ready yet at logon doesn't disable BIOS calls for the whole session.
        private static ManagementObject ClassInstance
        {
            get
            {
                var instance = _classInstance;
                if (instance == null)
                {
                    var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\wmi"),
                        new ObjectQuery("SELECT * FROM hpqBIntM"), new EnumerationOptions { Timeout = CallTimeout });
                    instance = searcher.Get().OfType<ManagementObject>().FirstOrDefault()
                               ?? throw new InvalidOperationException("HP BIOS WMI interface (hpqBIntM) not found");
                    _classInstance = instance;
                }

                return instance;
            }
        }

        public static HpBiosDataOut InvokeBiosCommand(uint command, uint commandType, uint size, byte[] biosData = null)
        {
            biosData ??= Array.Empty<byte>();

            // The same ManagementObject is used from the UI thread, timers and the WMI event thread.
            // Don't wait forever behind a call that hangs in WMI.
            if (!Monitor.TryEnter(CallLock, CallLockTimeout))
                throw new TimeoutException("Another HP BIOS call is still running");

            try
            {
                RefuseWhileOrphansRun();

                // None of the WMI calls involved (connecting, looking up hpqBIntM, fetching the parameter classes,
                // InvokeMethod) has a usable time limit of its own, so the whole call runs on a worker thread and
                // the caller stops waiting after CallTimeout. A call that hangs keeps its thread; the next call
                // looks the interface up again.
                var call = Task.Run(() => Invoke(command, commandType, size, biosData));
                if (Task.WaitAny(new Task[] { call }, CallTimeout) < 0)
                {
                    lock (Orphans) Orphans.Add((call, DateTime.UtcNow));
                    throw new TimeoutException(
                        $"HP BIOS command 0x{command:X} type 0x{commandType:X2} did not answer within {CallTimeout.TotalSeconds:0} s");
                }
                return call.GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is not HpBiosException)
            {
                // Anything other than a BIOS answer (WMI error, RPC disconnect after a winmgmt restart, ...):
                // look the interface up again next time.
                _classInstance = null;
                throw;
            }
            finally
            {
                Monitor.Exit(CallLock);
            }
        }

        // Caller holds CallLock.
        private static void RefuseWhileOrphansRun()
        {
            lock (Orphans)
            {
                Orphans.RemoveAll(o => o.Call.IsCompleted);
                if (Orphans.Count == 0)
                    return;
                if (Orphans.Count >= MaxOrphans)
                    throw new TimeoutException($"{Orphans.Count} HP BIOS calls are still hanging in WMI; not starting another");
                if (DateTime.UtcNow - Orphans[^1].TimedOutAt < OrphanGrace)
                    throw new TimeoutException("The previous HP BIOS call is still running in WMI");
            }
        }

        private static HpBiosDataOut Invoke(uint command, uint commandType, uint size, byte[] biosData)
        {
            ManagementObject dataIn = new ManagementClass(@"root\wmi:hpqBDataIn");
            var instance = ClassInstance;
            var inParams = instance.GetMethodParameters(@"hpqBIOSInt128");

            dataIn["Command"] = command;
            dataIn["CommandType"] = commandType;
            dataIn["hpqBData"] = biosData;
            dataIn["Size"] = biosData.Length;
            dataIn["Sign"] = Sign;
            inParams["InData"] = dataIn;

            var outParams = instance.InvokeMethod($"hpqBIOSInt{size}", inParams, null);
            var dataOut = (ManagementBaseObject) outParams?["OutData"];
            if (dataOut == null)
            {
                throw new InvalidOperationException("BIOS not responding to WMI command");
            }

            var returnCode = (uint) dataOut.Properties["rwReturnCode"].Value;
            switch (returnCode)
            {
                case 0x00:
                    break;
                case 0x03:
                    throw new HpBiosException(returnCode, "Command not available");
                case 0x05:
                    throw new HpBiosException(returnCode, "Size is too small");
                default:
                    throw new HpBiosException(returnCode, $"BIOS returned error code {returnCode}");
            }

            if (dataOut.ClassPath.ClassName == "hpqBDataOut0")
            {
                return new HpBiosDataOut(dataOut.ClassPath.ClassName, (bool?) dataOut.Properties["Active"].Value,
                    null, (string) dataOut.Properties["InstanceName"].Value,
                    returnCode, (byte[]) dataOut.Properties["Sign"].Value);
            }

            if (((byte[]) dataOut.Properties["Data"].Value).Length != size)
            {
                OMENEventSource.Log.CommandWarn("InvokeBiosCommand", "BIOS return data length is not expected length");
            }

            return new HpBiosDataOut(dataOut.ClassPath.ClassName, (bool?) dataOut.Properties["Active"].Value,
                (byte[]) dataOut.Properties["Data"].Value, (string) dataOut.Properties["InstanceName"].Value,
                returnCode, (byte[]) dataOut.Properties["Sign"].Value);
        }
    }
}
