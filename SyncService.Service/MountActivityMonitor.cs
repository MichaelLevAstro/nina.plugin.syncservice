using System;

namespace SyncService.Service {

    public enum MountActivityAction {
        /// <summary>Nothing to do.</summary>
        None,
        /// <summary>The mount just started moving - report it as busy.</summary>
        SetBusy,
        /// <summary>The mount is still moving (or settling) - keep the busy state alive.</summary>
        RefreshBusy,
        /// <summary>The mount has stopped and settled - clear the busy state.</summary>
        ClearBusy
    }

    /// <summary>
    /// Pure, side-effect-free state machine that decides when a mount is "busy" (moving) based on a stream
    /// of telescope samples. It is deliberately free of any N.I.N.A. or gRPC dependency so it can be unit tested.
    ///
    /// A mount is considered busy when it is slewing or when its reported coordinates change by more than a
    /// threshold between samples. Once movement stops the busy state is held for a configurable settle window so
    /// that the micro-slews of a plate-solve center do not prematurely release waiting instances.
    ///
    /// Note: while a mount tracks a fixed target its reported RA/Dec stay constant, so tracking does not trip
    /// the coordinate-change detection.
    /// </summary>
    public sealed class MountActivityMonitor {
        private readonly Func<bool> enabled;
        private readonly Func<int> settleSeconds;
        private readonly Func<double> thresholdArcsec;

        private bool busy;
        private bool haveLast;
        private bool lastConnected;
        private bool lastSlewing;
        private double lastRaDeg;
        private double lastDecDeg;
        private DateTime lastMotionUtc;

        public MountActivityMonitor(Func<bool> enabled, Func<int> settleSeconds, Func<double> thresholdArcsec) {
            this.enabled = enabled ?? (() => true);
            this.settleSeconds = settleSeconds ?? (() => 5);
            this.thresholdArcsec = thresholdArcsec ?? (() => 10d);
        }

        public bool IsBusy => busy;

        /// <summary>
        /// Feed a new telescope sample.
        /// </summary>
        public MountActivityAction Update(bool connected, bool slewing, double raDegrees, double decDegrees, DateTime nowUtc) {
            if (!enabled() || !connected) {
                haveLast = false;
                lastConnected = connected;
                if (busy) {
                    busy = false;
                    return MountActivityAction.ClearBusy;
                }
                return MountActivityAction.None;
            }

            if (!haveLast) {
                // First sample after (re)connecting establishes the baseline and must not be read as a move.
                haveLast = true;
                lastConnected = true;
                lastRaDeg = raDegrees;
                lastDecDeg = decDegrees;
                lastSlewing = slewing;
                lastMotionUtc = nowUtc;
                if (slewing) {
                    busy = true;
                    return MountActivityAction.SetBusy;
                }
                return MountActivityAction.None;
            }

            var movedNow = slewing || SeparationArcsec(raDegrees, decDegrees, lastRaDeg, lastDecDeg) > thresholdArcsec();
            if (movedNow) {
                lastMotionUtc = nowUtc;
            }

            lastRaDeg = raDegrees;
            lastDecDeg = decDegrees;
            lastSlewing = slewing;
            lastConnected = true;

            if (!busy) {
                if (movedNow) {
                    busy = true;
                    return MountActivityAction.SetBusy;
                }
                return MountActivityAction.None;
            }

            if (movedNow) {
                return MountActivityAction.RefreshBusy;
            }

            if ((nowUtc - lastMotionUtc).TotalSeconds >= settleSeconds()) {
                busy = false;
                return MountActivityAction.ClearBusy;
            }
            return MountActivityAction.RefreshBusy;
        }

        /// <summary>
        /// Re-evaluate based on elapsed time only, without a new sample. Used by a timer so the busy state is
        /// refreshed and the settle window is honored even if the telescope stops broadcasting updates.
        /// </summary>
        public MountActivityAction Tick(DateTime nowUtc) {
            if (!busy) {
                return MountActivityAction.None;
            }
            if (!lastSlewing && (nowUtc - lastMotionUtc).TotalSeconds >= settleSeconds()) {
                busy = false;
                return MountActivityAction.ClearBusy;
            }
            return MountActivityAction.RefreshBusy;
        }

        /// <summary>
        /// Great-circle angular separation between two equatorial coordinates, in arcseconds.
        /// </summary>
        public static double SeparationArcsec(double ra1Deg, double dec1Deg, double ra2Deg, double dec2Deg) {
            const double d2r = Math.PI / 180d;
            var dec1 = dec1Deg * d2r;
            var dec2 = dec2Deg * d2r;
            var dRa = (ra1Deg - ra2Deg) * d2r;
            var cosd = Math.Sin(dec1) * Math.Sin(dec2) + Math.Cos(dec1) * Math.Cos(dec2) * Math.Cos(dRa);
            if (cosd > 1d) { cosd = 1d; }
            if (cosd < -1d) { cosd = -1d; }
            return Math.Acos(cosd) / d2r * 3600d;
        }
    }
}
