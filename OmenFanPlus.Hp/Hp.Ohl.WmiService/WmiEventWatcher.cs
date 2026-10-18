using System;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using Hp.Ohl.WmiService.Models;
using Hp.Omen.OmenCommonLib.Utilities;

namespace Hp.Ohl.WmiService
{
    public static class WmiEventWatcher
    {
        public static event HpBiosEventHandler HpBiosEventArrived;
        private static ManagementEventWatcher _hpBiosEventWatcher;
        private static readonly object HPBiosEventWatcherLock = new();
        private static readonly int[] RetryDelaysSeconds = { 5, 15, 30, 60, 120, 300 };
        private static volatile bool _stopping;
        private static int _retryLoopRunning;
        // Bumped whenever a restart is needed, so a retry loop that just succeeded can tell that the new
        // subscription was ended again in the meantime.
        private static int _restartGeneration;

        /// <summary>Starts a fresh watcher. Throws if WMI refuses the subscription.</summary>
        public static void StartHpBiosEventWatcher()
        {
            lock (HPBiosEventWatcherLock)
            {
                _stopping = false;
                StartLocked();
            }
        }

        // Caller holds HPBiosEventWatcherLock. Leaves _stopping alone, so a retry can't undo a stop.
        private static void StartLocked()
        {
            DisposeWatcher();

            var watcher = new ManagementEventWatcher(@"root\wmi", "SELECT * FROM hpqBEvnt");
            watcher.EventArrived += OnHpBiosEventArrived;
            watcher.Stopped += OnHpBiosEventStopped;
            _hpBiosEventWatcher = watcher;
            watcher.Start();
        }

        /// <summary>
        /// Starts the watcher, retrying in the background if the HP WMI provider isn't ready yet (e.g. at logon).
        /// Returns whether the first attempt worked.
        /// </summary>
        public static bool StartWithRetry()
        {
            try
            {
                StartHpBiosEventWatcher();
                return true;
            }
            catch (Exception ex)
            {
                OMENEventSource.Log.Error($"Starting the HP BIOS event watcher failed: {ex.Message}");
                RetryInBackground();
                return false;
            }
        }

        public static void StopHpBiosEventWatcher()
        {
            lock (HPBiosEventWatcherLock)
            {
                _stopping = true;
                DisposeWatcher();
            }
        }

        private static void DisposeWatcher()
        {
            var old = _hpBiosEventWatcher;
            _hpBiosEventWatcher = null;
            if (old == null) return;

            old.EventArrived -= OnHpBiosEventArrived;
            old.Stopped -= OnHpBiosEventStopped;
            try { old.Stop(); } catch (Exception) { }
            old.Dispose();
        }

        private static void RetryInBackground()
        {
            Interlocked.Increment(ref _restartGeneration);
            // Only one retry loop at a time; a running loop notices the new generation.
            if (Interlocked.CompareExchange(ref _retryLoopRunning, 1, 0) != 0)
                return;

            Task.Run(async () =>
            {
                var handled = Volatile.Read(ref _restartGeneration);
                try
                {
                    var attempt = 0;
                    while (attempt < RetryDelaysSeconds.Length)
                    {
                        handled = Volatile.Read(ref _restartGeneration);
                        await Task.Delay(TimeSpan.FromSeconds(RetryDelaysSeconds[attempt]));

                        var started = false;
                        try
                        {
                            lock (HPBiosEventWatcherLock)
                            {
                                if (_stopping) return;
                                StartLocked();
                            }
                            started = true;
                            OMENEventSource.Log.Info("HP BIOS event watcher started");
                        }
                        catch (Exception ex)
                        {
                            OMENEventSource.Log.Error($"Starting the HP BIOS event watcher failed: {ex.Message}");
                        }

                        if (started && Volatile.Read(ref _restartGeneration) == handled)
                            return;
                        // Keep backing off even after a successful start, in case WMI keeps ending the subscription.
                        attempt++;
                    }
                }
                finally
                {
                    Volatile.Write(ref _retryLoopRunning, 0);
                    // A stop that arrived while this loop was finishing would otherwise be lost.
                    if (!_stopping && Volatile.Read(ref _restartGeneration) != handled)
                        RetryInBackground();
                }
            });
        }

        public static void OnHpBiosEventArrived(object sender, EventArrivedEventArgs e)
        {
            var raiseEvent = HpBiosEventArrived;
            if (raiseEvent == null) return;

            // Get WMI event details
            // reference: HPSystemEventUtilityHost.exe:HPSystemEventUtilityHost.HPSystemEventUtilityHost.WmiEventCallback

            var args = new HpBiosEventArgs
            {
                eventId = (uint) e.NewEvent.Properties["EventID"].Value,
                eventData = (uint) e.NewEvent.Properties["EventData"].Value,
                timeCreated = (ulong) e.NewEvent.Properties["TIME_CREATED"].Value,
            };

            try
            {
                ReadEventPayload(args);
            }
            catch (Exception ex)
            {
                // System.Management swallows exceptions thrown from EventArrived, so log them here.
                OMENEventSource.Log.Error(
                    $"Reading payload of HP BIOS event {args.eventId} failed: {ex.Message}");
                args.eventPayload = new NotHandledPayload();
            }

            raiseEvent(sender, args);
        }

        private static void ReadEventPayload(HpBiosEventArgs args)
        {
            HpBiosDataOut dataOut;
            switch (args.eventId)
            {
                case 29:
                    dataOut = HpBiosIntHelper.InvokeBiosCommand(1, 15, 4);
                    args.eventPayload = dataOut.Data switch
                    {
                        _ when dataOut.Data[0] == 1 => new OmenKeyPressedPayload(),
                        _ => new NotHandledPayload { OriginalBytes = dataOut.Data},
                    };
                    break;
                case 4:
                    dataOut = HpBiosIntHelper.InvokeBiosCommand(1, 12, 4);
                    args.eventPayload = dataOut.Data switch
                    {
                        _ when dataOut.Data[0] == 165 && dataOut.Data[1] == 33 => new OmenKeyPressedPayload(),
                        _ when dataOut.Data[0] == 164 && dataOut.Data[1] == 33 => new WinKeyLockPayload
                        {
                            IsWinKeyEnabled = dataOut.Data[2] == 1
                        },
                        _ when dataOut.Data[0] == 169 && dataOut.Data[1] == 33 => new TouchPadTogglePayload
                        {
                            IsTouchPadEnabled = dataOut.Data[2] == 1
                        },
                        // MicMute, PrivacyScreen, PrivacyCamera, AppSwitchKey long pressed, SysInfo Key, etc.
                        _ => new NotHandledPayload {OriginalBytes = dataOut.Data},
                    };
                    break;
                case 3:
                    dataOut = HpBiosIntHelper.InvokeBiosCommand(1, 15, 4);
                    args.eventPayload = dataOut.Data switch
                    {
                        _ when dataOut.Data[0] == 2 => new IncompatiblePowerAdapterPayload(),
                        _ when dataOut.Data[0] == 3 => new LowPowerAdapterPayload(),
                        _ => new PowerAdapterPayload(),
                    };
                    break;
                // etc.
            }
        }

        public static void OnHpBiosEventStopped(object sender, StoppedEventArgs e)
        {
            // WMI can end the subscription by itself (winmgmt restart, queue overflow). Without a restart the
            // OMEN key would stay dead until the app is restarted.
            if (_stopping || !ReferenceEquals(sender, _hpBiosEventWatcher))
                return;

            OMENEventSource.Log.Warn($"HP BIOS event watcher stopped ({e.Status}); restarting");
            RetryInBackground();
        }
    }
}