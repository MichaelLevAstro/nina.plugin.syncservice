using NINA.Astrometry;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using SyncService.Service;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SyncService.MountActivity {

    /// <summary>
    /// Watches the local telescope and, on the instance that owns the mount, reports a "mount busy" state to the
    /// synchronization server whenever the mount moves (slew, center, meridian flip, manual moves, ...). Other
    /// instances react to that state by pausing imaging. Instances without a connected mount never broadcast, so
    /// the same observer runs everywhere with no configuration - the mount-owning instance is detected at runtime.
    /// </summary>
    public sealed class MountBusyObserver : ITelescopeConsumer {
        private const int RefreshIntervalMs = 2000;

        private readonly ITelescopeMediator telescopeMediator;
        private readonly ISyncServiceClient client;
        private readonly Func<bool> enabled;
        private readonly MountActivityMonitor monitor;
        private readonly Timer refreshTimer;
        private readonly object sync = new object();

        private bool started;
        private bool disposed;
        private bool haveValidCoords;
        private double lastValidRaDeg;
        private double lastValidDecDeg;
        private string lastReason = "Mount is moving";

        public MountBusyObserver(ITelescopeMediator telescopeMediator, ISyncServiceClient client, Func<bool> enabled, Func<int> settleSeconds, Func<double> thresholdArcsec) {
            this.telescopeMediator = telescopeMediator;
            this.client = client;
            this.enabled = enabled ?? (() => true);
            this.monitor = new MountActivityMonitor(this.enabled, settleSeconds, thresholdArcsec);
            this.refreshTimer = new Timer(OnRefreshTick, null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Start() {
            lock (sync) {
                if (started || disposed) { return; }
                started = true;
            }
            telescopeMediator.RegisterConsumer(this);
            Logger.Info("Mount movement observer started");
        }

        public void UpdateDeviceInfo(TelescopeInfo deviceInfo) {
            lock (sync) {
                if (disposed) { return; }

                var connected = deviceInfo?.Connected == true;
                if (!connected) {
                    Apply(monitor.Update(false, false, 0d, 0d, DateTime.UtcNow));
                    return;
                }

                var slewing = deviceInfo.Slewing;
                var coords = deviceInfo.Coordinates;
                var coordsValid = coords != null && !double.IsNaN(coords.RADegrees) && !double.IsNaN(coords.Dec);

                double raDeg, decDeg;
                if (coordsValid) {
                    raDeg = coords.RADegrees;
                    decDeg = coords.Dec;
                    lastValidRaDeg = raDeg;
                    lastValidDecDeg = decDeg;
                    haveValidCoords = true;
                } else if (haveValidCoords) {
                    raDeg = lastValidRaDeg;
                    decDeg = lastValidDecDeg;
                } else if (!slewing) {
                    // No coordinates available yet and not slewing - wait for a real position before baselining.
                    return;
                } else {
                    raDeg = 0d;
                    decDeg = 0d;
                }

                lastReason = slewing ? "Mount is slewing" : "Mount position changed";
                Apply(monitor.Update(true, slewing, raDeg, decDeg, DateTime.UtcNow));
            }
        }

        private void OnRefreshTick(object state) {
            lock (sync) {
                if (disposed) { return; }
                Apply(monitor.Tick(DateTime.UtcNow));
            }
        }

        // Caller must hold sync.
        private void Apply(MountActivityAction action) {
            // The monitor is still advanced (keeps its baseline aligned), but a planned synced mount operation
            // (flip / center & slew / center after drift) drives the hold itself - so the reactive observer
            // stays silent and only reports UNEXPECTED moves.
            if (client.PlannedMountOperationInProgress) {
                if (action == MountActivityAction.ClearBusy) { StopRefreshTimer(); }
                return;
            }
            switch (action) {
                case MountActivityAction.SetBusy:
                    StartRefreshTimer();
                    var setReason = lastReason;
                    FireAndForget(ct => client.SetMountBusy(setReason, ct), "set mount busy");
                    break;
                case MountActivityAction.RefreshBusy:
                    var refreshReason = lastReason;
                    FireAndForget(ct => client.SetMountBusy(refreshReason, ct), "refresh mount busy");
                    break;
                case MountActivityAction.ClearBusy:
                    StopRefreshTimer();
                    FireAndForget(ct => client.ClearMountBusy(ct), "clear mount busy");
                    break;
            }
        }

        private void StartRefreshTimer() {
            try { refreshTimer.Change(RefreshIntervalMs, RefreshIntervalMs); } catch (ObjectDisposedException) { }
        }

        private void StopRefreshTimer() {
            try { refreshTimer.Change(Timeout.Infinite, Timeout.Infinite); } catch (ObjectDisposedException) { }
        }

        private static void FireAndForget(Func<CancellationToken, Task> op, string description) {
            _ = Task.Run(async () => {
                try {
                    await op(CancellationToken.None);
                } catch (Exception ex) {
                    Logger.Error($"Mount sync: failed to {description}", ex);
                }
            });
        }

        public void Dispose() {
            bool wasBusy;
            lock (sync) {
                if (disposed) { return; }
                disposed = true;
                wasBusy = monitor.IsBusy;
                StopRefreshTimer();
            }

            try { telescopeMediator.RemoveConsumer(this); } catch (Exception ex) { Logger.Error("Failed to remove mount observer consumer", ex); }
            try { refreshTimer.Dispose(); } catch (Exception) { }

            if (wasBusy) {
                FireAndForget(ct => client.ClearMountBusy(ct), "clear mount busy on shutdown");
            }
        }
    }
}
