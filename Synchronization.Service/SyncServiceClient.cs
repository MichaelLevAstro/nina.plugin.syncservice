using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using GrpcDotNetNamedPipes;
using NINA.Core.Utility;
using NINA.Synchronization.Service.Sync;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Synchronization.Service {
    public class SyncServiceClient : SyncService.SyncServiceClient, ISyncServiceClient {

        private static readonly Lazy<SyncServiceClient> lazy = new Lazy<SyncServiceClient>(() => new SyncServiceClient());
        private CancellationTokenSource heartbeatCts;
        public static SyncServiceClient Instance { get => lazy.Value; }

        private Guid id = Guid.NewGuid();
        private static object lockObj = new object();
        private bool heartbeatrunning = false;
        private Dictionary<string, int> registrationsBySource = new Dictionary<string, int>();

        private SyncServiceClient() : base(new NamedPipeChannel(".", "NINA.Synchronization.Service.Sync", new NamedPipeChannelOptions() { ConnectionTimeout = 300000 })) {
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
                        var resp = await SyncServiceClient.Instance.Ping(token);
                    } catch (OperationCanceledException) {
                        Logger.Info($"Stopping heartbeat for {id}");
                    } catch (Exception ex) {
                        Logger.Error("An error occurred while pinging the server", ex);
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
