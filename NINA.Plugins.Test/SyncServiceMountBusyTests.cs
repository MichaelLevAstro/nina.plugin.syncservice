using Google.Protobuf.WellKnownTypes;
using NINA.SyncService.Service.Sync;
using NUnit.Framework;
using SyncService.Service;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace NINA.Plugins.Test {
    [TestFixture]
    [NonParallelizable]
    public class SyncServiceFlagTests {
        private static readonly TestServerCallContext Context = TestServerCallContext.Create();

        private static FlagRequest Req(string key, string id, string reason = "", int ttl = 0) {
            return new FlagRequest { Key = key, Clientid = id, Reason = reason, Ttlseconds = ttl };
        }

        private static async Task<FlagSnapshot> Snapshot(SyncServiceServer server, string key) {
            var reply = await server.GetFlags(new Empty(), Context);
            return reply.Flags.FirstOrDefault(f => f.Key == key);
        }

        private static async Task<bool> IsSet(SyncServiceServer server, string key) {
            var snap = await Snapshot(server, key);
            return snap != null && snap.Set;
        }

        private static string UniqueKey() => "Test." + Guid.NewGuid().ToString("N");

        [Test]
        public async Task SetFlag_ThenGet_ReportsSetWithOwnerAndReason() {
            var server = SyncServiceServer.Instance;
            var key = UniqueKey();
            var client = "c-" + Guid.NewGuid().ToString("N");
            try {
                await server.SetFlag(Req(key, client, "Mount is slewing", ttl: 60), Context);

                var snap = await Snapshot(server, key);
                Assert.That(snap, Is.Not.Null);
                Assert.That(snap.Set, Is.True);
                Assert.That(snap.Ownerid, Is.EqualTo(client));
                Assert.That(snap.Reason, Is.EqualTo("Mount is slewing"));
            } finally {
                await server.ClearFlag(Req(key, client), Context);
            }
        }

        [Test]
        public async Task ClearFlag_ReportsCleared() {
            var server = SyncServiceServer.Instance;
            var key = UniqueKey();
            var client = "c-" + Guid.NewGuid().ToString("N");

            await server.SetFlag(Req(key, client, "slew", ttl: 60), Context);
            await server.ClearFlag(Req(key, client), Context);

            Assert.That(await IsSet(server, key), Is.False);
        }

        [Test]
        public async Task Flag_AutoExpiresAfterTtl_WhenNotRefreshed() {
            var server = SyncServiceServer.Instance;
            var key = UniqueKey();
            var client = "c-" + Guid.NewGuid().ToString("N");
            try {
                server.DefaultFlagTtl[key] = TimeSpan.FromMilliseconds(100);
                await server.SetFlag(Req(key, client, "slew"), Context);

                Assert.That(await IsSet(server, key), Is.True);

                await Task.Delay(200);

                // A crashed setter that never refreshes must not strand waiting instances.
                Assert.That(await IsSet(server, key), Is.False);
            } finally {
                server.DefaultFlagTtl.Remove(key);
                await server.ClearFlag(Req(key, client), Context);
            }
        }

        [Test]
        public async Task Flag_WithTwoSetters_StaysSetUntilBothClear() {
            var server = SyncServiceServer.Instance;
            var key = UniqueKey();
            var a = "a-" + Guid.NewGuid().ToString("N");
            var b = "b-" + Guid.NewGuid().ToString("N");
            try {
                await server.SetFlag(Req(key, a, "a", ttl: 60), Context);
                await server.SetFlag(Req(key, b, "b", ttl: 60), Context);

                await server.ClearFlag(Req(key, a), Context);
                Assert.That(await IsSet(server, key), Is.True, "Should stay set while the second setter is active");

                await server.ClearFlag(Req(key, b), Context);
                Assert.That(await IsSet(server, key), Is.False);
            } finally {
                await server.ClearFlag(Req(key, a), Context);
                await server.ClearFlag(Req(key, b), Context);
            }
        }
    }
}
