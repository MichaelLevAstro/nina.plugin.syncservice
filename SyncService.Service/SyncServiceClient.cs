using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using GrpcDotNetNamedPipes;
using NINA.Core.Utility;
using NINA.SyncService.Service.Sync;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SyncService.Service {
    public class SyncServiceClient : SyncBus.SyncBusClient, ISyncServiceClient {

        private static readonly Lazy<SyncServiceClient> lazy = new Lazy<SyncServiceClient>(() => new SyncServiceClient());
        private CancellationTokenSource heartbeatCts;
        public static SyncServiceClient Instance { get => lazy.Value; }

        private Guid id = Guid.NewGuid();
        private static object lockObj = new object();
        private bool heartbeatrunning = false;
        private Dictionary<string, int> registrationsBySource = new Dictionary<string, int>();

        // Heartbeat-refreshed caches, read synchronously by trigger ShouldTrigger.
        private volatile bool mountBusyCached = false;
        public bool MountBusyCached => mountBusyCached;
        public string MountBusyReason { get; private set; } = string.Empty;

        private volatile bool autofocusBusyCached = false;
        public bool AutofocusBusyCached => autofocusBusyCached;

        private readonly ConcurrentDictionary<string, bool> pendingBySource = new ConcurrentDictionary<string, bool>();
        public bool IsOperationPendingCached(string source) => pendingBySource.TryGetValue(source, out var v) && v;

        /// <summary>The meridian-flip pending flag doubles as the "preempt autofocus" signal.</summary>
        public bool FlipPreemptCached => IsOperationPendingCached(SyncSources.MeridianFlip);

        // Process-local gate: while a planned mount operation runs on this instance, the reactive observer
        // suppresses its own mount-busy reporting so it only reacts to UNEXPECTED moves.
        private int plannedMountOpDepth = 0;
        public bool PlannedMountOperationInProgress => Volatile.Read(ref plannedMountOpDepth) > 0;
        public void BeginPlannedMountOperation() => Interlocked.Increment(ref plannedMountOpDepth);
        public void EndPlannedMountOperation() {
            if (Interlocked.Decrement(ref plannedMountOpDepth) < 0) {
                Interlocked.Exchange(ref plannedMountOpDepth, 0);
            }
        }

        private SyncServiceClient() : base(new NamedPipeChannel(".", "NINA.SyncService.Service.Sync", new NamedPipeChannelOptions() { ConnectionTimeout = 300000 })) {
        }

        /// <summary>
        /// Register the client against the sync service
        /// </summary>
        public void RegisterSync(string source) {
            base.Register(new ClientIdRequest() { Clientid = id.ToString(), Source = source }, new CallOptions(deadline: DateTime.UtcNow.AddSeconds(5)));
            AddRegistration(source);
        }

        public override Empty Register(ClientIdRequest request, CallOptions options) {
            var result = base.Register(request, options);
            AddRegistration(request.Source);
            return result;
        }

        /// <summary>
        /// Remove the client from the sync service
        /// </summary>
        public void UnregisterSync(string source) {
            base.Unregister(new ClientIdRequest() { Clientid = id.ToString(), Source = source }, new CallOptions(deadline: DateTime.UtcNow.AddSeconds(5)));
            RemoveRegistration(source);
        }

        public override Empty Unregister(ClientIdRequest request, CallOptions options) {
            var result = base.Unregister(request, options);
            RemoveRegistration(request.Source);
            return result;
        }

        /// <summary>
        /// Register the client against the sync service
        /// </summary>
        public async Task Register(string source) {
            await base.RegisterAsync(new ClientIdRequest() { Clientid = id.ToString(), Source = source }, null, deadline: DateTime.UtcNow.AddSeconds(5));
            AddRegistration(source);
        }

        /// <summary>
        /// Remove the client from the sync service
        /// </summary>
        public async Task Unregister(string source) {
            await base.UnregisterAsync(new ClientIdRequest() { Clientid = id.ToString(), Source = source }, null, deadline: DateTime.UtcNow.AddSeconds(5));
            RemoveRegistration(source);
        }

        /// <summary>
        /// Wait until the server sends that all clients are synced up
        /// </summary>
        /// <returns></returns>
        public async Task<bool> WaitForSyncStart(string source, CancellationToken ct, TimeSpan timeout) {
            var result = await base.WaitForSyncStartAsync(new ClientIdRequest() { Clientid = id.ToString(), Source = source }, null, deadline: DateTime.UtcNow.AddSeconds(timeout.TotalSeconds), cancellationToken: ct);
            if (string.IsNullOrEmpty(result.LeaderId)) { throw new Exception($"No instance could lead the synchronized {source}!"); }
            return result.LeaderId == id.ToString();
        }

        /// <summary>
        /// Wait until the server sends that the sync has been completed by the leader
        /// </summary>
        /// <returns></returns>
        public async Task WaitForSyncComplete(string source, CancellationToken ct, TimeSpan timeout) {
            await base.WaitForSyncCompletedAsync(new ClientIdRequest() { Clientid = id.ToString(), Source = source }, null, deadline: DateTime.UtcNow.AddSeconds(timeout.TotalSeconds), cancellationToken: ct);
        }

        /// <summary>
        /// Send to the server that the leader has started the sync
        /// </summary>
        /// <returns></returns>
        public async Task SetSyncInProgress(string source, CancellationToken ct) {
            await base.SetSyncInProgressAsync(new ClientIdRequest() { Clientid = id.ToString(), Source = source }, null, deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);
        }

        /// <summary>
        /// Send to the server that the leader has finished the sync
        /// </summary>
        /// <returns></returns>
        public async Task SetSyncComplete(string source, CancellationToken ct) {
            await base.SetSyncCompletedAsync(new ClientIdRequest() { Clientid = id.ToString(), Source = source }, null, deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);
        }

        public async Task AnnounceToSync(string source, bool canLead, CancellationToken ct) {
            await base.AnnounceToSyncAsync(new AnnounceToSyncRequest() { Clientid = id.ToString(), Source = source, Canlead = canLead }, null, deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);
        }

        public async Task<string> Ping(CancellationToken ct) {
            var resp = await base.PingAsync(new ClientIdRequest() { Clientid = id.ToString() }, null, deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);
            return resp.Reply;
        }

        public async Task WithdrawFromSync(string source, CancellationToken ct) {
            await base.WithdrawFromSyncAsync(new ClientIdRequest() { Clientid = id.ToString(), Source = source }, null, deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);
        }

        // ---- generic keyed flags ----
        public async Task SetFlag(string key, string reason, TimeSpan ttl, CancellationToken ct) {
            await base.SetFlagAsync(new FlagRequest() {
                Key = key,
                Clientid = id.ToString(),
                Reason = reason ?? string.Empty,
                Ttlseconds = (int)Math.Max(0, ttl.TotalSeconds)
            }, null, deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);
        }

        public async Task ClearFlag(string key, CancellationToken ct) {
            await base.ClearFlagAsync(new FlagRequest() { Key = key, Clientid = id.ToString() }, null, deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);
        }

        public async Task<FlagsReply> GetFlags(CancellationToken ct) {
            return await base.GetFlagsAsync(new Empty(), null, deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);
        }

        // ---- mount busy (back-compat wrappers over the generic flag) ----
        public Task SetMountBusy(string reason, CancellationToken ct) => SetFlag(SyncSources.MountBusy, reason, TimeSpan.Zero, ct);
        public Task ClearMountBusy(CancellationToken ct) => ClearFlag(SyncSources.MountBusy, ct);

        // ---- operation pending (rendezvous wake-up) ----
        public Task SetOperationPending(string source, string reason, CancellationToken ct) => SetFlag(SyncSources.PendingKey(source), reason, TimeSpan.Zero, ct);
        public Task ClearOperationPending(string source, CancellationToken ct) => ClearFlag(SyncSources.PendingKey(source), ct);

        // ---- autofocus busy (reverse hold) ----
        public Task SetAutofocusBusy(string reason, CancellationToken ct) => SetFlag(SyncSources.AutofocusBusy, reason, TimeSpan.Zero, ct);
        public Task ClearAutofocusBusy(CancellationToken ct) => ClearFlag(SyncSources.AutofocusBusy, ct);

        private void UpdateFlagCaches(FlagsReply flags) {
            var mount = false;
            var af = false;
            var mountReason = string.Empty;
            var seenPending = new HashSet<string>();
            foreach (var f in flags.Flags) {
                if (!f.Set) { continue; }
                if (f.Key == SyncSources.MountBusy) {
                    mount = true;
                    mountReason = f.Reason;
                } else if (f.Key == SyncSources.AutofocusBusy) {
                    af = true;
                } else if (f.Key.EndsWith(SyncSources.PendingSuffix)) {
                    var src = f.Key.Substring(0, f.Key.Length - SyncSources.PendingSuffix.Length);
                    pendingBySource[src] = true;
                    seenPending.Add(src);
                }
            }
            foreach (var src in pendingBySource.Keys.ToList()) {
                if (!seenPending.Contains(src)) { pendingBySource[src] = false; }
            }
            mountBusyCached = mount;
            MountBusyReason = mountReason ?? string.Empty;
            autofocusBusyCached = af;
        }

        private void ClearAllFlagCaches() {
            mountBusyCached = false;
            autofocusBusyCached = false;
            MountBusyReason = string.Empty;
            foreach (var src in pendingBySource.Keys.ToList()) { pendingBySource[src] = false; }
        }

        private void AddRegistration(string source) {
            var shouldStartHeartbeat = false;

            lock (lockObj) {
                if (registrationsBySource.ContainsKey(source)) {
                    registrationsBySource[source] += 1;
                } else {
                    registrationsBySource[source] = 1;
                }

                shouldStartHeartbeat = !heartbeatrunning;
            }

            if (shouldStartHeartbeat) {
                _ = StartHeartbeat();
            }
        }

        private void RemoveRegistration(string source) {
            var shouldStopHeartbeat = false;

            lock (lockObj) {
                if (!registrationsBySource.ContainsKey(source)) {
                    return;
                }

                registrationsBySource[source] -= 1;
                if (registrationsBySource[source] <= 0) {
                    registrationsBySource.Remove(source);
                }

                shouldStopHeartbeat = registrationsBySource.Values.Sum() == 0;
            }

            if (shouldStopHeartbeat) {
                StopHeartbeat();
            }
        }

        private Task StartHeartbeat() {
            CancellationToken token;

            lock (lockObj) {
                if (heartbeatrunning) {
                    return Task.CompletedTask;
                }

                if (registrationsBySource.Values.Sum() == 0) {
                    return Task.CompletedTask;
                }

                heartbeatrunning = true;
                heartbeatCts = new CancellationTokenSource();
                token = heartbeatCts.Token;
            }

            Logger.Info($"Starting heartbeat for {id}");

            return Task.Run(async () => {
                while (!token.IsCancellationRequested) {
                    try {
                        await Task.Delay(1000, token);
                        await SyncServiceClient.Instance.Ping(token);
                        var flags = await SyncServiceClient.Instance.GetFlags(token);
                        UpdateFlagCaches(flags);
                    } catch (OperationCanceledException) {
                        Logger.Info($"Stopping heartbeat for {id}");
                    } catch (Exception ex) {
                        Logger.Error("An error occurred while pinging the server", ex);
                        // Fail open: if the server is unreachable, never hold an instance hostage to a stale flag.
                        ClearAllFlagCaches();
                    }
                }
            });
        }

        private void StopHeartbeat() {
            lock (lockObj) {
                if (!heartbeatrunning) {
                    return;
                }

                if (registrationsBySource.Values.Sum() > 0) {
                    return;
                }

                try {
                    heartbeatCts?.Cancel();
                } catch (Exception) {
                }

                heartbeatrunning = false;
                heartbeatCts = null;
            }
        }
    }
}
