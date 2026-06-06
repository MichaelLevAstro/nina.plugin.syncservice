using NINA.SyncService.Service.Sync;
using NUnit.Framework;
using SyncService.Service;
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

        [Test]
        public async Task WaitForSyncStart_WhenLeaderCompletesBeforeFollowerObservesStart_FollowerReturnsCompletedLeader() {
            var server = SyncServiceServer.Instance;
            var source = UniqueSource();
            var leaderClient = "00000000-0000-0000-0000-000000000101";
            var followerClient = "ffffffff-ffff-ffff-ffff-ffffffffff01";

            await server.Register(Client(leaderClient, source), Context);
            await server.Register(Client(followerClient, source), Context);
            await server.AnnounceToSync(Announcement(followerClient, source, canLead: true), Context);
            await server.AnnounceToSync(Announcement(leaderClient, source, canLead: true), Context);

            var leader = await server.WaitForSyncStart(Client(leaderClient, source), Context);
            Assert.That(leader.LeaderId, Is.EqualTo(leaderClient));

            await server.SetSyncInProgress(Client(leaderClient, source), Context);
            await server.SetSyncCompleted(Client(leaderClient, source), Context);

            var follower = await AssertCompletes(
                server.WaitForSyncStart(Client(followerClient, source), Context),
                "Follower did not observe the completed sync from WaitForSyncStart.");

            Assert.That(follower.LeaderId, Is.EqualTo(leaderClient));

            await AssertCompletes(
                server.WaitForSyncCompleted(Client(followerClient, source), Context),
                "Follower did not return from WaitForSyncCompleted after observing start.");
        }

        [Test]
        public async Task WaitForSyncStart_WhenNextSyncIsAnnouncedBeforeFollowerObservesCompletion_FollowerStillReturnsCompletedLeader() {
            var server = SyncServiceServer.Instance;
            var source = UniqueSource();
            var leaderClient = "00000000-0000-0000-0000-000000000102";
            var followerClient = "ffffffff-ffff-ffff-ffff-ffffffffff02";

            await server.Register(Client(leaderClient, source), Context);
            await server.Register(Client(followerClient, source), Context);
            await server.AnnounceToSync(Announcement(followerClient, source, canLead: true), Context);
            await server.AnnounceToSync(Announcement(leaderClient, source, canLead: true), Context);

            var leader = await server.WaitForSyncStart(Client(leaderClient, source), Context);
            Assert.That(leader.LeaderId, Is.EqualTo(leaderClient));

            await server.SetSyncInProgress(Client(leaderClient, source), Context);
            await server.SetSyncCompleted(Client(leaderClient, source), Context);

            await server.AnnounceToSync(Announcement(leaderClient, source, canLead: true), Context);

            var follower = await AssertCompletes(
                server.WaitForSyncStart(Client(followerClient, source), Context),
                "Follower was blocked by the next sync after the previous sync completed.");

            Assert.That(follower.LeaderId, Is.EqualTo(leaderClient));

            await AssertCompletes(
                server.WaitForSyncCompleted(Client(followerClient, source), Context),
                "Follower did not finish the completed sync after the next sync was announced.");
        }

        [Test]
        public async Task WaitForSyncStart_FastCompletionRace_DoesNotLeaveFollowerBlockedUnderRepeatedRuns() {
            var server = SyncServiceServer.Instance;

            for (var i = 0; i < 100; i++) {
                var source = $"{UniqueSource()}-{i}";
                var leaderClient = $"00000000-0000-0000-0000-{i + 1:000000000000}";
                var followerClient = $"ffffffff-ffff-ffff-ffff-{i + 1:000000000000}";

                await server.Register(Client(leaderClient, source), Context);
                await server.Register(Client(followerClient, source), Context);
                await server.AnnounceToSync(Announcement(followerClient, source, canLead: true), Context);

                using var cts = new CancellationTokenSource();
                cts.CancelAfter(1500);
                var followerStartTask = server.WaitForSyncStart(Client(followerClient, source), TestServerCallContext.Create(cts.Token));

                if (i % 3 == 0) {
                    await Task.Delay(1);
                }

                await server.AnnounceToSync(Announcement(leaderClient, source, canLead: true), Context);

                var leader = await server.WaitForSyncStart(Client(leaderClient, source), Context);
                Assert.That(leader.LeaderId, Is.EqualTo(leaderClient), $"Leader election failed on iteration {i}.");

                await server.SetSyncInProgress(Client(leaderClient, source), Context);
                await server.SetSyncCompleted(Client(leaderClient, source), Context);

                if (i % 2 == 0) {
                    await server.AnnounceToSync(Announcement(leaderClient, source, canLead: true), Context);
                }

                var follower = await AssertCompletes(
                    followerStartTask,
                    $"Follower stayed blocked after fast completion on iteration {i}.");

                Assert.That(follower.LeaderId, Is.EqualTo(leaderClient), $"Follower saw the wrong leader on iteration {i}.");

                await AssertCompletes(
                    server.WaitForSyncCompleted(Client(followerClient, source), Context),
                    $"Follower did not finish completion wait on iteration {i}.");

                await server.Unregister(Client(leaderClient, source), Context);
                await server.Unregister(Client(followerClient, source), Context);
            }
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

        private static async Task<T> AssertCompletes<T>(Task<T> task, string message) {
            var completed = await Task.WhenAny(task, Task.Delay(1000));
            Assert.That(completed, Is.EqualTo(task), message);
            return await task;
        }

        private static async Task AssertCompletes(Task task, string message) {
            var completed = await Task.WhenAny(task, Task.Delay(1000));
            Assert.That(completed, Is.EqualTo(task), message);
            await task;
        }
    }
}
