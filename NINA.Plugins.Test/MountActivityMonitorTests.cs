using NUnit.Framework;
using SyncService.Service;
using System;

namespace NINA.Plugins.Test {
    [TestFixture]
    public class MountActivityMonitorTests {
        private static readonly DateTime T0 = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private const double Ra = 10d;
        private const double Dec = 20d;

        private static MountActivityMonitor NewMonitor(int settle = 5, double threshold = 10d) {
            return new MountActivityMonitor(() => true, () => settle, () => threshold);
        }

        [Test]
        public void FirstConnectedSample_IsBaseline_NoTrigger() {
            var m = NewMonitor();
            Assert.That(m.Update(true, false, Ra, Dec, T0), Is.EqualTo(MountActivityAction.None));
            Assert.That(m.IsBusy, Is.False);
        }

        [Test]
        public void SlewingRisingEdge_SetsBusy() {
            var m = NewMonitor();
            m.Update(true, false, Ra, Dec, T0);
            Assert.That(m.Update(true, true, Ra, Dec, T0.AddSeconds(2)), Is.EqualTo(MountActivityAction.SetBusy));
            Assert.That(m.IsBusy, Is.True);
        }

        [Test]
        public void CoordinateJumpBeyondThreshold_SetsBusy_WithoutSlewing() {
            var m = NewMonitor();
            m.Update(true, false, Ra, Dec, T0);
            // 0.02 deg = 72 arcsec > 10 arcsec threshold
            Assert.That(m.Update(true, false, Ra, Dec + 0.02, T0.AddSeconds(2)), Is.EqualTo(MountActivityAction.SetBusy));
        }

        [Test]
        public void SubThresholdJitter_DoesNotTrigger() {
            var m = NewMonitor();
            m.Update(true, false, Ra, Dec, T0);
            // 0.001 deg = 3.6 arcsec < 10 arcsec threshold (tracking jitter / solve noise)
            Assert.That(m.Update(true, false, Ra, Dec + 0.001, T0.AddSeconds(2)), Is.EqualTo(MountActivityAction.None));
            Assert.That(m.IsBusy, Is.False);
        }

        [Test]
        public void StaysBusyWhileSlewing_ThenClearsAfterSettle() {
            var m = NewMonitor(settle: 5);
            m.Update(true, false, Ra, Dec, T0);
            Assert.That(m.Update(true, true, Ra, Dec, T0.AddSeconds(2)), Is.EqualTo(MountActivityAction.SetBusy));
            Assert.That(m.Update(true, true, Ra, Dec, T0.AddSeconds(4)), Is.EqualTo(MountActivityAction.RefreshBusy));
            // Slew stopped at T0+5; still within settle window
            Assert.That(m.Update(true, false, Ra, Dec, T0.AddSeconds(5)), Is.EqualTo(MountActivityAction.RefreshBusy));
            Assert.That(m.IsBusy, Is.True);
            // Last motion was at T0+4; settle of 5s elapses at T0+9
            Assert.That(m.Update(true, false, Ra, Dec, T0.AddSeconds(9)), Is.EqualTo(MountActivityAction.ClearBusy));
            Assert.That(m.IsBusy, Is.False);
        }

        [Test]
        public void CoordinateJumpDuringSettleWindow_RestartsTheTimer() {
            var m = NewMonitor(settle: 5);
            m.Update(true, false, Ra, Dec, T0);
            m.Update(true, true, Ra, Dec, T0.AddSeconds(1)); // busy, last motion T0+1
            m.Update(true, false, Ra, Dec, T0.AddSeconds(2)); // settling
            // A fresh coordinate jump at T0+4 restarts the settle timer
            Assert.That(m.Update(true, false, Ra, Dec + 0.02, T0.AddSeconds(4)), Is.EqualTo(MountActivityAction.RefreshBusy));
            // T0+8 is only 4s after the last motion (T0+4) -> still held
            Assert.That(m.Update(true, false, Ra, Dec + 0.02, T0.AddSeconds(8)), Is.EqualTo(MountActivityAction.RefreshBusy));
            Assert.That(m.IsBusy, Is.True);
            // T0+10 is >=5s after last motion -> clear
            Assert.That(m.Update(true, false, Ra, Dec + 0.02, T0.AddSeconds(10)), Is.EqualTo(MountActivityAction.ClearBusy));
        }

        [Test]
        public void DisconnectWhileBusy_ClearsBusy() {
            var m = NewMonitor();
            m.Update(true, false, Ra, Dec, T0);
            m.Update(true, true, Ra, Dec, T0.AddSeconds(2));
            Assert.That(m.IsBusy, Is.True);
            Assert.That(m.Update(false, false, 0d, 0d, T0.AddSeconds(3)), Is.EqualTo(MountActivityAction.ClearBusy));
            Assert.That(m.IsBusy, Is.False);
        }

        [Test]
        public void Tick_ClearsAfterSettle_WithoutNewSamples() {
            var m = NewMonitor(settle: 5);
            m.Update(true, true, Ra, Dec, T0); // SetBusy, last motion T0
            m.Update(true, false, Ra, Dec, T0.AddSeconds(1)); // slew stopped, last motion still T0
            Assert.That(m.Tick(T0.AddSeconds(3)), Is.EqualTo(MountActivityAction.RefreshBusy));
            Assert.That(m.Tick(T0.AddSeconds(6)), Is.EqualTo(MountActivityAction.ClearBusy));
            Assert.That(m.IsBusy, Is.False);
        }

        [Test]
        public void Disabled_NeverBusy() {
            var m = new MountActivityMonitor(() => false, () => 5, () => 10d);
            Assert.That(m.Update(true, true, Ra, Dec, T0), Is.EqualTo(MountActivityAction.None));
            Assert.That(m.IsBusy, Is.False);
        }

        [Test]
        public void SeparationArcsec_IsApproximatelyCorrect() {
            // 0.01 deg along declination == 36 arcsec
            Assert.That(MountActivityMonitor.SeparationArcsec(Ra, Dec, Ra, Dec + 0.01), Is.EqualTo(36d).Within(0.5));
        }
    }
}
