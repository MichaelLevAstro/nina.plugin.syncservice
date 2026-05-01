using NINA.Synchronization.Service.Sync;
using NUnit.Framework;
using Synchronization.Service;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.Test {
    [TestFixture]
    [NonParallelizable]
    public class SyncServiceServerTests {
        private static readonly TestServerCallContext Context = TestServerCallContext.Create();

        [Test]
        public async Task WaitForSyncStart_DoesNotElectUnregisteredWaitingClient() {
            var server = SyncServiceServer.Instance;
            var source = UniqueSource();
            var staleClient = "00000000-0000-0000-0000-000000000001";
            var activeClient = "ffffffff-ffff-ffff-ffff-ffffffffffff";

            await server.Register(Client(staleClient, source), Context);
            await server.AnnounceToSync(Announcement(staleClient, source, canLead: true), Context);
            await server.Unregister(Client(staleClient, source), Context);

            await server.Register(Client(activeClient, source), Context);
            await server.AnnounceToSync(Announcement(activeClient, source, canLead: true), Context);

            var leader = await server.WaitForSyncStart(Client(activeClient, source), Context);

            Assert.That(leader.LeaderId, Is.EqualTo(activeClient));
        }

        [Test]
        public async Task WaitForSyncStart_WhenCallIsCanceled_StopsWaitingAndWithdrawsClient() {
            var server = SyncServiceServer.Instance;
            var source = UniqueSource();
            var canceledClient = "00000000-0000-0000-0000-000000000002";
            var activeClient = "ffffffff-ffff-ffff-ffff-fffffffffffe";

            await server.Register(Client(canceledClient, source), Context);
            await server.Register(Client(activeClient, source), Context);
            await server.AnnounceToSync(Announcement(canceledClient, source, canLead: true), Context);

            using var cts = new CancellationTokenSource();
            cts.CancelAfter(50);

            var waitTask = server.WaitForSyncStart(Client(canceledClient, source), TestServerCallContext.Create(cts.Token));
            var completed = await Task.WhenAny(waitTask, Task.Delay(500));

            if (completed != waitTask) {
                await server.AnnounceToSync(Announcement(activeClient, source, canLead: true), Context);
                await Task.WhenAny(waitTask, Task.Delay(500));
                Assert.Fail("WaitForSyncStart did not observe the gRPC cancellation token.");
            }

            Assert.ThrowsAsync(Is.InstanceOf<OperationCanceledException>(), async () => await waitTask);

            await server.Unregister(Client(canceledClient, source), Context);
            await server.AnnounceToSync(Announcement(activeClient, source, canLead: true), Context);

            var leader = await server.WaitForSyncStart(Client(activeClient, source), Context);

            Assert.That(leader.LeaderId, Is.EqualTo(activeClient));
        }

        [Test]
        public async Task WaitForSyncStart_WhenCallIsCanceled_ClearsWaitingStatusWhenNoClientsRemainWaiting() {
            var server = SyncServiceServer.Instance;
            var source = UniqueSource();
            var canceledClient = "00000000-0000-0000-0000-000000000004";
            var otherClient = "ffffffff-ffff-ffff-ffff-fffffffffffc";

            await server.Register(Client(canceledClient, source), Context);
            await server.Register(Client(otherClient, source), Context);
            await server.AnnounceToSync(Announcement(canceledClient, source, canLead: true), Context);

            using var cts = new CancellationTokenSource();
            cts.CancelAfter(50);

            var waitTask = server.WaitForSyncStart(Client(canceledClient, source), TestServerCallContext.Create(cts.Token));

            Assert.ThrowsAsync(Is.InstanceOf<OperationCanceledException>(), async () => await waitTask);
            Assert.That(server.GetStatus(), Does.Not.Contain(source));
        }

        [Test]
        public async Task WaitForSyncStart_AfterNoLeaderFailure_RequiresClientsToAnnounceAgain() {
            var server = SyncServiceServer.Instance;
            var source = UniqueSource();
            var clientWithoutGuider = "00000000-0000-0000-0000-000000000003";
            var leaderCapableClient = "ffffffff-ffff-ffff-ffff-fffffffffffd";

            await server.Register(Client(clientWithoutGuider, source), Context);
            await server.AnnounceToSync(Announcement(clientWithoutGuider, source, canLead: false), Context);

            var noLeader = await server.WaitForSyncStart(Client(clientWithoutGuider, source), Context);

            Assert.That(noLeader.LeaderId, Is.Empty);

            await server.Register(Client(leaderCapableClient, source), Context);
            await server.AnnounceToSync(Announcement(leaderCapableClient, source, canLead: true), Context);

            using var cts = new CancellationTokenSource();
            cts.CancelAfter(50);

            var waitTask = server.WaitForSyncStart(Client(leaderCapableClient, source), TestServerCallContext.Create(cts.Token));

            Assert.ThrowsAsync(Is.InstanceOf<OperationCanceledException>(), async () => await waitTask);
        }

        private static string UniqueSource() {
            return $"{TestContext.CurrentContext.Test.Name}-{Guid.NewGuid():N}";
        }

        private static ClientIdRequest Client(string id, string source) {
            return new ClientIdRequest {
                Clientid = id,
                Source = source
            };
        }

        private static AnnounceToSyncRequest Announcement(string id, string source, bool canLead) {
            return new AnnounceToSyncRequest {
                Clientid = id,
                Source = source,
                Canlead = canLead
            };
        }
    }
}
