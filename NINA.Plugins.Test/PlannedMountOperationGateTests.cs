using NUnit.Framework;
using SyncService.Service;

namespace NINA.Plugins.Test {
    [TestFixture]
    [NonParallelizable]
    public class PlannedMountOperationGateTests {
        [Test]
        public void Gate_TracksNestedBeginEnd_AndClampsAtZero() {
            var client = SyncServiceClient.Instance;

            Assert.That(client.PlannedMountOperationInProgress, Is.False);

            client.BeginPlannedMountOperation();
            client.BeginPlannedMountOperation();
            Assert.That(client.PlannedMountOperationInProgress, Is.True);

            client.EndPlannedMountOperation();
            Assert.That(client.PlannedMountOperationInProgress, Is.True, "Still in progress while a nested operation is active");

            client.EndPlannedMountOperation();
            Assert.That(client.PlannedMountOperationInProgress, Is.False);

            // An extra End must not drive the counter negative.
            client.EndPlannedMountOperation();
            Assert.That(client.PlannedMountOperationInProgress, Is.False);

            client.BeginPlannedMountOperation();
            Assert.That(client.PlannedMountOperationInProgress, Is.True);
            client.EndPlannedMountOperation();
            Assert.That(client.PlannedMountOperationInProgress, Is.False);
        }
    }
}
