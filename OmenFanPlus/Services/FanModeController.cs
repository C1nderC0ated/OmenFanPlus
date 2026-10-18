using Hp.Omen.OmenCommonLib;
using Hp.Omen.OmenCommonLib.PowerControl.Enum;
using Microsoft.Win32;
using OmenFanPlus.Models;
using OmenFanPlus.Properties;

namespace OmenFanPlus.Services
{
    /// <summary>
    /// Sends thermal/fan modes to the BIOS, only offers what the BIOS supports, and re-applies the saved mode
    /// at startup, after sleep and when the power source changes.
    /// </summary>
    internal sealed class FanModeController : IDisposable
    {
        /// <summary>Returned by the Apply/Set methods when the laptop doesn't support the request.</summary>
        public const int NotSupported = OmenHsaClient.UnsupportedReturnCode;

        private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan[] RetryDelays =
            { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30),
              TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(300) };

        // The firmware hands manual fan levels back to automatic after about 120 s, so Max is re-sent before that.
        // Off is deliberately not kept alive: letting the BIOS take the fans back is the safe outcome.
        private static readonly TimeSpan MaxKeepAliveInterval = TimeSpan.FromSeconds(90);

        private readonly OmenHsaClient _client = new();
        private readonly object _applyLock = new();
        private readonly System.Threading.Timer _maxKeepAlive;
        private CancellationTokenSource? _pendingReapply;
        private PowerLineStatus _lastPowerLine = SystemInformation.PowerStatus.PowerLineStatus;
        private int _probeRetry;
        private int _applyRetry;
        private volatile bool _shuttingDown;

        // Set only while the custom curve owns the fans, so a curve update that was already running when the
        // user picked another mode can't overwrite it.
        private bool _customActive;
        private int? _lastCurveRc;

        public FanModeController()
        {
            _maxKeepAlive = new System.Threading.Timer(_ => KeepMaxAlive(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public OmenCapabilities Capabilities { get; private set; } = null!;

        /// <summary>Short text describing the last mode change, e.g. "Performance, applied 14:32".</summary>
        public string Status { get; private set; } = "";

        /// <summary>Whether the last mode change was accepted by the BIOS.</summary>
        public bool LastResultOk { get; private set; } = true;

        /// <summary>Raised (on any thread) when <see cref="Status"/> or <see cref="Capabilities"/> change.</summary>
        public event EventHandler? StatusChanged;

        public void Initialize()
        {
            ProbeAndApply("startup");
            SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
        }

        /// <summary>Asks for a new probe soon, e.g. when the popup opens while the BIOS hasn't answered yet.</summary>
        public void RequestProbe(string reason)
        {
            if (Capabilities?.ProbeComplete != true && _pendingReapply is null or { IsCancellationRequested: true })
                ScheduleReapply(reason, TimeSpan.FromMilliseconds(100), isProbeRetry: true);
        }

        /// <summary>
        /// Asks the BIOS what it supports and re-applies the saved mode. Settings are only migrated and modes
        /// only re-applied once every probe has a definite answer; until then it retries with a back-off that
        /// settles at every 5 minutes.
        /// </summary>
        private void ProbeAndApply(string reason)
        {
            if (_shuttingDown)
                return;

            var complete = false;
            var applyRc = 0;
            if (!Monitor.TryEnter(_applyLock, LockTimeout))
            {
                AppLog.Warn($"Skipped probing the BIOS ({reason}): another BIOS call is still running");
            }
            else
            {
                try
                {
                    if (_shuttingDown)
                        return;

                    Capabilities = OmenCapabilities.Probe(_client);
                    AppLog.Info($"BIOS capabilities: {Capabilities}");

                    complete = Capabilities.ProbeComplete;
                    if (complete)
                    {
                        _probeRetry = 0;
                        MigrateUnsupportedSettings();
                        applyRc = ApplySavedModeLocked(reason);
                    }
                    else
                    {
                        Report("Saved mode", -1);
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Error($"Probing the BIOS failed ({reason})", ex);
                    Capabilities ??= OmenCapabilities.Unavailable();
                }
                finally
                {
                    Monitor.Exit(_applyLock);
                }
            }

            if (!complete)
            {
                // The HP WMI provider can be late right after logon.
                var delay = RetryDelays[Math.Min(_probeRetry++, RetryDelays.Length - 1)];
                ScheduleReapply("BIOS retry", delay, isProbeRetry: true);
            }
            else
            {
                RetryIfTransient(applyRc, reason);
            }
        }

        // A WMI hiccup (rc -1) or a bad-signature answer (rc 2) is worth another try; a BIOS rejection isn't.
        private void RetryIfTransient(int rc, string reason)
        {
            if (rc is >= 0 and not 2 || _applyRetry >= 3)
            {
                if (rc is >= 0 and not 2) _applyRetry = 0;
                return;
            }

            var delay = RetryDelays[_applyRetry++];
            AppLog.Info($"Will try to apply the saved mode again in {delay.TotalSeconds:0} s ({reason})");
            ScheduleReapply($"{reason}, retry {_applyRetry}", delay, isApplyRetry: true);
        }

        /// <summary>Saved modes that this BIOS can't do are mapped to the closest mode it can.</summary>
        private void MigrateUnsupportedSettings()
        {
            var s = Settings.Default;
            var changed = false;

            if (!Capabilities.ManualFanControlSupported && (s.fanMax || s.fanOff || s.fanCustom))
            {
                // On these boards "Max" only ever switched the BIOS to Performance, and Off/Custom never sent a
                // thermal mode at all, so the saved thermal mode is what was really in effect.
                if (s.fanMax)
                    s.PerformanceMode = PerformanceMode.Performance;
                AppLog.Info($"Manual fan modes aren't supported here; saved mode changed to {s.PerformanceMode}");
                s.fanMax = s.fanOff = s.fanCustom = false;
                changed = true;
            }

            var third = Capabilities.QuietOrCoolMode;
            if (s.PerformanceMode is PerformanceMode.Quiet or PerformanceMode.Cool && s.PerformanceMode != third)
            {
                s.PerformanceMode = third;
                changed = true;
            }

            if (changed)
                TrySave();
        }

        public bool ApplyThermalMode(PerformanceMode mode) =>
            Locked(DisplayName(mode), () =>
            {
                StopMaxKeepAlive();
                _customActive = false;
                var releaseRc = ReleaseManualFanControl();
                var rc = _client.SetFanMode(mode);
                // A rejected mode isn't remembered (it would be re-sent at every start); Performance is the
                // exception, because the EC refuses it on battery and it is sent again on AC.
                if (rc is 0 or < 0 || mode == PerformanceMode.Performance)
                    SaveMode(mode);
                return Report(DisplayName(mode), rc, releaseRc);
            });

        public bool ApplyMax() =>
            Locked("Max", () =>
            {
                if (!Capabilities.ManualFanControlSupported)
                    return Report("Max", NotSupported);

                _customActive = false;
                var modeRc = _client.SetFanMode(PerformanceMode.Performance);
                var rc = _client.SetSwFanControlLevel(100, 100);
                SaveMode(max: true);
                // Even a failed first send is retried by the keep-alive.
                _maxKeepAlive.Change(MaxKeepAliveInterval, MaxKeepAliveInterval);
                return Report("Max", rc == 0 ? modeRc : rc);
            });

        public bool ApplyOff() =>
            Locked("Off", () =>
            {
                if (!Capabilities.ManualFanControlSupported)
                    return Report("Off", NotSupported);

                StopMaxKeepAlive();
                _customActive = false;
                // Keep the saved thermal profile (Max may have left Performance in the BIOS).
                _client.SetFanMode(Settings.Default.PerformanceMode);
                if (Capabilities.MaxFanSupported)
                    _client.SetMaxFan(MaxFanMode.Off);
                var rc = _client.SetSwFanControlLevel(FanCurveProfile.StoppedLevel, FanCurveProfile.StoppedLevel);
                SaveMode(off: true);
                return Report("Off", rc);
            });

        /// <summary>Hands the fans to the custom curve; the curve timer then calls <see cref="SetFanLevelsPercent"/>.</summary>
        public bool ApplyCustom() =>
            Locked("Custom curve", () =>
            {
                if (!Capabilities.ManualFanControlSupported)
                    return Report("Custom curve", NotSupported);

                StopMaxKeepAlive();
                _client.SetFanMode(Settings.Default.PerformanceMode);
                if (Capabilities.MaxFanSupported)
                    _client.SetMaxFan(MaxFanMode.Off);
                _customActive = true;
                _lastCurveRc = null;
                SaveMode(custom: true);
                // Full speed until the curve's first reading arrives (within 2 s); this also tells whether
                // the board accepts fan levels at all.
                var rc = _client.SetSwFanControlLevel(Capabilities.FanLevelMax, Capabilities.FanLevelMax);
                return Report("Custom curve", rc);
            });

        /// <summary>
        /// Sets the fans for the custom curve, in curve percent (0 = stopped, 1-100 = slowest to fastest).
        /// Ignored unless Custom is the active mode.
        /// </summary>
        public int SetFanLevelsPercent(int cpuPercent, int gpuPercent)
        {
            if (!Monitor.TryEnter(_applyLock, LockTimeout))
                return -1;
            try
            {
                if (_shuttingDown || !_customActive || !Capabilities.ManualFanControlSupported)
                    return NotSupported;

                var cpu = FanCurveProfile.LevelFromPercent(cpuPercent, Capabilities.FanLevelMin, Capabilities.FanLevelMax);
                var gpu = FanCurveProfile.LevelFromPercent(gpuPercent, Capabilities.FanLevelMin, Capabilities.FanLevelMax);
                var rc = _client.SetSwFanControlLevel(cpu, gpu);

                // Report only when the result flips, so a 2 s timer can't flood the log or the status line.
                if (_lastCurveRc == null || (_lastCurveRc == 0) != (rc == 0))
                    Report("Custom curve", rc);
                _lastCurveRc = rc;
                return rc;
            }
            finally
            {
                Monitor.Exit(_applyLock);
            }
        }

        /// <summary>Fan levels in units of 100 RPM, or an empty list if the BIOS can't report them.</summary>
        public List<int> GetFanLevels() =>
            Capabilities.FanLevelSupported ? _client.GetFanLevel().ConvertAll(b => (int)b) : new List<int>();

        /// <summary>The current fan levels expressed on the curve's percent scale.</summary>
        public List<int> GetFanLevelsPercent() =>
            GetFanLevels().ConvertAll(level =>
                FanCurveProfile.PercentFromLevel(level, Capabilities.FanLevelMin, Capabilities.FanLevelMax));

        /// <summary>
        /// Fan speeds in RPM: from the 0x2D fan levels where the board has them, otherwise from the 0x11 query
        /// (verified on the OMEN 15-dc1xxx). Empty if neither works.
        /// </summary>
        public List<int> GetFanSpeedsRpm()
        {
            if (Capabilities.FanLevelSupported)
                return GetFanLevels().ConvertAll(level => level * 100);

            var speeds = new List<int>();
            if (!FanRpmAvailable())
                return speeds;

            for (var fan = 0; fan < 2; fan++)
            {
                var rpm = _client.GetFanRpm(fan);
                if (rpm >= 0)
                    speeds.Add(rpm);
            }
            return speeds;
        }

        public bool HasFanSpeeds => Capabilities.FanLevelSupported || Capabilities.FanRpmSupported || _fanRpmRecovered;

        private bool _fanRpmRecovered;
        private DateTime _nextFanRpmRetry = DateTime.MinValue;

        // 0x11 isn't part of the startup probe's completeness, so a WMI hiccup during that one call would hide
        // the fan speeds for the whole session. Try it again once a minute until it answers.
        private bool FanRpmAvailable()
        {
            if (Capabilities.FanRpmSupported || _fanRpmRecovered)
                return true;

            var transient = Capabilities.FanRpmReturnCode is < 0 or 2;
            if (!transient || DateTime.UtcNow < _nextFanRpmRetry)
                return false;

            _nextFanRpmRetry = DateTime.UtcNow.AddMinutes(1);
            _fanRpmRecovered = _client.GetFanRpm(0) >= 0;
            if (_fanRpmRecovered)
                AppLog.Info("Fan speed query 0x11 answers now");
            return _fanRpmRecovered;
        }

        public static string DisplayName(PerformanceMode mode) => mode switch
        {
            PerformanceMode.Default => "Default",
            PerformanceMode.Performance => "Performance",
            // HP's firmware calls it Cool; OMEN Command Center showed it as "Comfort" on these laptops.
            PerformanceMode.Cool => "Comfort",
            PerformanceMode.Quiet => "Quiet",
            _ => mode.ToString(),
        };

        public void ApplySavedMode(string reason)
        {
            var rc = NotSupported;
            Locked("Saved mode", () =>
            {
                rc = ApplySavedModeLocked(reason);
                return true;
            });
            RetryIfTransient(rc, reason);
        }

        // Returns the BIOS return code of the mode that was sent.
        private int ApplySavedModeLocked(string reason)
        {
            var s = Settings.Default;
            AppLog.Info($"Re-applying saved mode ({reason})");

            if (Capabilities.ManualFanControlSupported && s.fanMax)
                ApplyMax();
            else if (Capabilities.ManualFanControlSupported && s.fanCustom)
                ApplyCustom();
            else
            {
                // "Off" is never switched on automatically: it needs the warning the button shows.
                ApplyThermalMode(s.PerformanceMode);
            }

            return _lastRc;
        }

        /// <summary>
        /// Called once when the app exits: stops every re-apply and keep-alive first, then gives the fans back to
        /// the BIOS so no manual level stays in force. The saved mode is kept for the next start.
        /// </summary>
        public void Shutdown()
        {
            SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
            _pendingReapply?.Cancel();
            StopMaxKeepAlive();

            if (!Monitor.TryEnter(_applyLock, LockTimeout))
            {
                _shuttingDown = true;
                AppLog.Warn("Couldn't hand the fans back to the BIOS on exit: another BIOS call is still running");
                return;
            }

            try
            {
                _shuttingDown = true;
                var s = Settings.Default;
                var manualActive = _customActive || s.fanOff || s.fanMax;
                _customActive = false;

                if (Capabilities?.ManualFanControlSupported == true && manualActive)
                {
                    var releaseRc = ReleaseManualFanControl();
                    // Same sequence as picking a thermal mode: release the fans, then set the mode.
                    var modeRc = _client.SetFanMode(s.PerformanceMode);
                    AppLog.Info($"Handed the fans back to the BIOS on exit (release rc={releaseRc}, mode rc={modeRc})");
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Handing the fans back to the BIOS on exit failed", ex);
            }
            finally
            {
                Monitor.Exit(_applyLock);
            }
        }

        private void KeepMaxAlive()
        {
            if (!Monitor.TryEnter(_applyLock, LockTimeout))
                return;
            try
            {
                if (_shuttingDown || !Settings.Default.fanMax || Capabilities?.ManualFanControlSupported != true)
                {
                    StopMaxKeepAlive();
                    return;
                }

                var rc = _client.SetSwFanControlLevel(100, 100);
                if ((rc == 0) != LastResultOk)
                    Report("Max", rc);
            }
            catch (Exception ex)
            {
                AppLog.Error("Re-sending Max failed", ex);
            }
            finally
            {
                Monitor.Exit(_applyLock);
            }
        }

        private void StopMaxKeepAlive() => _maxKeepAlive.Change(Timeout.Infinite, Timeout.Infinite);

        /// <summary>
        /// Hands manual fan control back to the BIOS, only with the commands this board has. Max fan is switched
        /// off before the level is released, the order the Linux driver uses.
        /// </summary>
        private int ReleaseManualFanControl()
        {
            var rc = 0;
            if (Capabilities.ManualFanControlSupported || Capabilities.MaxFanSupported)
            {
                var maxRc = _client.SetMaxFan(MaxFanMode.Off);
                // Boards with manual levels but no 0x26 reject 0x27; that isn't a failure to release.
                if (Capabilities.MaxFanSupported)
                    rc = maxRc;
            }

            if (Capabilities.ManualFanControlSupported)
            {
                var levelRc = _client.SetSwFanControlLevel(0, 0);
                if (rc == 0)
                    rc = levelRc;
            }

            return rc;
        }

        // Runs a BIOS operation under the lock, giving up (instead of freezing the UI) if a WMI call hangs.
        // Nothing is sent once the app is shutting down.
        private bool Locked(string what, Func<bool> action)
        {
            if (!Monitor.TryEnter(_applyLock, LockTimeout))
                return Report(what, -1);
            try
            {
                return !_shuttingDown && action();
            }
            catch (Exception ex)
            {
                AppLog.Error($"{what} failed", ex);
                return Report(what, -1);
            }
            finally
            {
                Monitor.Exit(_applyLock);
            }
        }

        // Only writes user.config when something changed (re-applying after sleep shouldn't rewrite it).
        private static void SaveMode(PerformanceMode? mode = null, bool max = false, bool off = false, bool custom = false)
        {
            var s = Settings.Default;
            if (s.fanMax == max && s.fanOff == off && s.fanCustom == custom && (mode == null || s.PerformanceMode == mode))
                return;

            s.fanMax = max;
            s.fanOff = off;
            s.fanCustom = custom;
            if (mode != null)
                s.PerformanceMode = mode.Value;
            TrySave();
        }

        // The BIOS has already been set by the time this runs; a locked or full disk must not take the app down.
        internal static void TrySave()
        {
            try
            {
                Settings.Default.Save();
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Couldn't save the settings: {ex.Message}");
            }
        }

        private int _lastRc;

        private bool Report(string what, int rc, int releaseRc = 0)
        {
            var ok = rc == 0;
            _lastRc = rc;
            LastResultOk = ok && releaseRc == 0;
            Status = rc switch
            {
                0 when releaseRc != 0 => $"{what}, applied, but fans not released (code {releaseRc})",
                0 => $"{what}, applied {DateTime.Now:HH:mm}",
                NotSupported => $"{what} isn't supported on this laptop",
                > 0 => $"{what} rejected by BIOS (code {rc})",
                _ => $"{what} not applied (BIOS not reachable)",
            };
            if (LastResultOk) AppLog.Info(Status);
            else AppLog.Warn(Status);

            StatusChanged?.Invoke(this, EventArgs.Empty);
            return ok;
        }

        private void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            switch (e.Mode)
            {
                case PowerModes.Resume:
                    _applyRetry = 0;
                    ScheduleReapply("resume from sleep", TimeSpan.FromSeconds(5));
                    break;
                case PowerModes.StatusChange:
                    // Also raised for battery level changes; only react when the charger is plugged in or out.
                    // The EC refuses Performance on battery, so it has to be sent again on AC.
                    var line = SystemInformation.PowerStatus.PowerLineStatus;
                    if (line != _lastPowerLine)
                    {
                        _lastPowerLine = line;
                        _applyRetry = 0;
                        ScheduleReapply($"power source changed to {line}", TimeSpan.FromSeconds(3));
                    }
                    break;
            }
        }

        private void ScheduleReapply(string reason, TimeSpan delay, bool isProbeRetry = false, bool isApplyRetry = false)
        {
            if (_shuttingDown)
                return;

            var cts = new CancellationTokenSource();
            Interlocked.Exchange(ref _pendingReapply, cts)?.Cancel();

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay, cts.Token);
                    if (Capabilities?.ProbeComplete == true)
                    {
                        ApplySavedMode(reason);
                    }
                    else
                    {
                        // A wake-up or charger change is a new chance for WMI to be ready: restart the back-off.
                        if (!isProbeRetry && !isApplyRetry)
                            _probeRetry = 0;
                        ProbeAndApply(reason);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    AppLog.Error($"Re-applying mode after {reason} failed", ex);
                }
            });
        }

        public void Dispose()
        {
            SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
            _pendingReapply?.Cancel();
            _maxKeepAlive.Dispose();
        }
    }
}
