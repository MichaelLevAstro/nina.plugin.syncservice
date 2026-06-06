using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using NINA.Core.Utility;
using NINA.SyncService.Service.Sync;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SyncService.Service {
    class ClientSource {
        public ClientSource(string id) {
            Id = id;
            DateTime = DateTime.UtcNow;
            Registrations = 1;
        }

        public string Id { get; }
        public DateTime DateTime { get; set; }
        public int Registrations { get; set; }

    }

    class FlagEntry {
        public FlagEntry(DateTime time, string reason, TimeSpan ttl) {
            Time = time;
            Reason = reason;
            Ttl = ttl;
        }

        public DateTime Time { get; set; }
        public string Reason { get; set; }
        public TimeSpan Ttl { get; set; }
    }

    /// <summary>
    /// Protocol:
    /// ClientA, ClientB, ClientC
    /// 
    /// Register when wanting to sync
    /// 
    /// -> AnnounceToSync 
    /// -> WaitForSync 
    /// -> IsLeader? (Response from WaitForSync)
    ///     -> YES
    ///         -> SetDitherInProgress 
    ///         -> SetDitherCompleted 
    ///     -> NO
    ///         -> WaitForDither 
    ///         
    /// 
    /// 
    /// Unregister when finishing the sync block
    /// 
    /// Ping is required to determine keepalives
    /// </summary>
    public class SyncServiceServer : SyncBus.SyncBusBase {
        public static readonly string IdleString = "Idle";
        private static readonly Lazy<SyncServiceServer> lazy = new Lazy<SyncServiceServer>(() => new SyncServiceServer());
        public static SyncServiceServer Instance { get => lazy.Value; }

        private Dictionary<string, string> statusBySource = new Dictionary<string, string>();


        private Dictionary<string, SortedDictionary<string, ClientSource>> registeredClients { get; }
        private Dictionary<string, SortedDictionary<string, bool>> clientsWaitingForSync { get; }
        private Dictionary<string, Dictionary<string, string>> completedSyncLeaderByClient { get; }
        private ConcurrentDictionary<string, bool> syncInProgress;
        private ConcurrentDictionary<string, string> syncLeader;

        // Generic push flags - per (key, client). Independent of the per-source sync barriers.
        // Used for "MountBusy" (planned moves + reactive observer), "<source>.Pending" (rendezvous wake-up
        // for meridian flip / center-after-drift) and "Autofocus.Busy" (reverse hold blocking mount moves).
        // Keyed by the client id that set it so multiple setters compose ("set if any active setter").
        // Entries auto-expire after their per-entry ttl so a crashed setter can never strand waiting instances.
        private Dictionary<string, Dictionary<string, FlagEntry>> flagsByKey = new Dictionary<string, Dictionary<string, FlagEntry>>();

        /// <summary>
        /// Default ttl per flag key, used when a SetFlag request does not specify one. Overridable for tests.
        /// </summary>
        public Dictionary<string, TimeSpan> DefaultFlagTtl { get; } = new Dictionary<string, TimeSpan>() {
            ["MountBusy"] = TimeSpan.FromSeconds(15),
            ["MeridianFlip.Pending"] = TimeSpan.FromSeconds(20),
            ["CenterAfterDrift.Pending"] = TimeSpan.FromSeconds(20),
            ["Autofocus.Busy"] = TimeSpan.FromSeconds(20),
        };

        /// <summary>Back-compat accessor for the mount-busy ttl (now just the "MountBusy" key default).</summary>
        public TimeSpan MountBusyTtl {
            get => DefaultFlagTtl.TryGetValue("MountBusy", out var t) ? t : TimeSpan.FromSeconds(15);
            set => DefaultFlagTtl["MountBusy"] = value;
        }


        private void SetStatus(string source, string message) {
            lock (lockobj) {
                if (statusBySource.ContainsKey(source) && string.IsNullOrWhiteSpace(message)) {
                    statusBySource.Remove(source);
                    return;
                }

                if (statusBySource.ContainsKey(source)) {
                    statusBySource[source] = message;
                } else {
                    statusBySource.Add(source, message);
                }
            }
        }

        public string GetStatus() {
            lock (lockobj) {
                if (statusBySource.Keys.Count > 0) {
                    return string.Join(" | ", statusBySource.Values);
                } else {
                    return IdleString;
                }
            }
        }

        private SyncServiceServer() {
            registeredClients = new Dictionary<string, SortedDictionary<string, ClientSource>>();
            clientsWaitingForSync = new Dictionary<string, SortedDictionary<string, bool>>();
            completedSyncLeaderByClient = new Dictionary<string, Dictionary<string, string>>();
            statusBySource = new Dictionary<string, string>();
            syncInProgress = new ConcurrentDictionary<string, bool>();
            syncLeader = new ConcurrentDictionary<string, string>();
        }

        private object lockobj = new object();


        private void AddClient(string id, string source) {
            lock (lockobj) {
                if (!registeredClients.ContainsKey(source)) {
                    registeredClients[source] = new SortedDictionary<string, ClientSource>();
                }
                if (registeredClients[source].ContainsKey(id)) {
                    registeredClients[source][id].Registrations += 1; 
                    Logger.Info($"Client {id} registered for sync with {source} {registeredClients[source][id].Registrations}x");
                } else {
                    registeredClients[source].Add(id, new ClientSource(id));
                    Logger.Info($"Client {id} registered for sync with {source}");
                }
            }
        }

        private void RemoveClient(string id, string source) {
            lock (lockobj) {
                if (!registeredClients.ContainsKey(source)) {                    
                    registeredClients[source] = new SortedDictionary<string, ClientSource>();
                }
                if (registeredClients[source].ContainsKey(id)) {
                    var client = registeredClients[source][id];
                    client.Registrations -= 1;
                    if(client.Registrations == 0) {
                        registeredClients[source].Remove(id);
                        RemoveClientReleasedFromCompletedSync(id, source);
                        RemoveClientWaitingForSync(id, source);
                        Logger.Info($"Client {id} unregistered sync with {source}");
                    } else {
                        Logger.Info($"Client {id} unregistered once sync with {source} but is still registered {client.Registrations}x");
                    }
                }
            }
        }

        private void UpdateClient(string id) {
            lock (lockobj) {
                foreach (var clientsBySource in registeredClients.Values) {
                    if (clientsBySource.ContainsKey(id)) {
                        clientsBySource[id].DateTime = DateTime.UtcNow;
                    }
                }

            }
        }

        private bool TryGetCompletedSyncLeader(string id, string source, out string leaderId) {
            lock (lockobj) {
                leaderId = string.Empty;
                return completedSyncLeaderByClient.ContainsKey(source)
                    && completedSyncLeaderByClient[source].TryGetValue(id, out leaderId);
            }
        }

        private void RemoveClientReleasedFromCompletedSync(string id, string source) {
            lock (lockobj) {
                if (completedSyncLeaderByClient.ContainsKey(source)) {
                    completedSyncLeaderByClient[source].Remove(id);
                    if (completedSyncLeaderByClient[source].Count == 0) {
                        completedSyncLeaderByClient.Remove(source);
                    }
                }
            }
        }

        private string ElectSyncLeader(string source) {
            lock (lockobj) {
                if (!registeredClients.ContainsKey(source)) {
                    registeredClients[source] = new SortedDictionary<string, ClientSource>();
                }
                if (!clientsWaitingForSync.ContainsKey(source)) {
                    clientsWaitingForSync[source] = new SortedDictionary<string, bool>();
                }
                RemoveInactiveWaitingClients(source);
                return clientsWaitingForSync[source]
                    .Where(x => x.Value)
                    .Select(x => x.Key)
                    .FirstOrDefault(id => IsClientActive(id, source));
            }
        }

        private bool IsClientActive(string id, string source) {
            if (!registeredClients.ContainsKey(source)) {
                registeredClients[source] = new SortedDictionary<string, ClientSource>();
            }

            return registeredClients[source].TryGetValue(id, out var client) && client.DateTime > DateTime.UtcNow.AddSeconds(-30);
        }

        private void RemoveInactiveWaitingClients(string source) {
            if (!clientsWaitingForSync.ContainsKey(source)) {
                clientsWaitingForSync[source] = new SortedDictionary<string, bool>();
            }

            var inactiveClients = clientsWaitingForSync[source]
                .Where(x => !IsClientActive(x.Key, source))
                .Select(x => x.Key)
                .ToList();

            foreach (var clientId in inactiveClients) {
                clientsWaitingForSync[source].Remove(clientId);
            }
        }

        private void RefreshWaitingStatus(string source) {
            lock (lockobj) {
                if (!clientsWaitingForSync.ContainsKey(source)) {
                    clientsWaitingForSync[source] = new SortedDictionary<string, bool>();
                }

                RemoveInactiveWaitingClients(source);

                var clientsForSync = clientsWaitingForSync[source].Count;
                if (clientsForSync == 0) {
                    syncInProgress[source] = false;
                    syncLeader[source] = string.Empty;
                    SetStatus(source, string.Empty);
                    return;
                }

                var totalClients = NumberOfTotalClients(source);
                var statusInfo = $"{clientsForSync}/{totalClients} clients waiting for {source}";
                Logger.Debug(statusInfo);
                SetStatus(source, statusInfo);
                syncInProgress[source] = true;
            }
        }

        private void AddClientWaitingForSync(string id, bool canLead, string source) {
            lock (lockobj) {
                if (!registeredClients.ContainsKey(source)) {
                    registeredClients[source] = new SortedDictionary<string, ClientSource>();
                }
                if (!registeredClients[source].ContainsKey(id)) {
                    // In case a client missed to register or the server restarted in between add the client to the registered clients again
                    registeredClients[source].Add(id, new ClientSource(id));
                }

                if (!clientsWaitingForSync.ContainsKey(source)) {
                    clientsWaitingForSync[source] = new SortedDictionary<string, bool>();
                }
                if (clientsWaitingForSync[source].ContainsKey(id)) {
                    clientsWaitingForSync[source][id] = canLead;
                } else {
                    clientsWaitingForSync[source].Add(id, canLead);
                }
                Logger.Debug($"Add client {id} that canlead={canLead} to waiting for sync from {source}.");
            }
        }

        private void RemoveClientWaitingForSync(string id, string source) {
            lock(lockobj) {
                if (clientsWaitingForSync.ContainsKey(source)) {
                    clientsWaitingForSync[source].Remove(id);
                }
                RefreshWaitingStatus(source);
            }
        }

        private int NumberOfClientsWaitingForSync(string source) {
            lock (lockobj) {
                if (!clientsWaitingForSync.ContainsKey(source)) {
                    clientsWaitingForSync[source] = new SortedDictionary<string, bool>();
                }
                return clientsWaitingForSync[source].Count;
            }
        }

        private int NumberOfTotalClients(string source) {
            lock (lockobj) {
                if (!registeredClients.ContainsKey(source)) {
                    registeredClients[source] = new SortedDictionary<string, ClientSource>();
                }
                return registeredClients[source].Where(x => x.Value.DateTime > DateTime.UtcNow.AddSeconds(-30)).Select(x => x.Key).Count();
            }
        }

        public override async Task<Empty> Register(ClientIdRequest request, ServerCallContext context) {
            
            AddClient(request.Clientid, request.Source);
            return new Empty();
        }

        public override async Task<Empty> Unregister(ClientIdRequest request, ServerCallContext context) {
            RemoveClient(request.Clientid, request.Source);
            return new Empty();
        }

        public override async Task<Empty> AnnounceToSync(AnnounceToSyncRequest request, ServerCallContext context) {
            lock (lockobj) {
                Logger.Info($"Client {request.Clientid} is announcing to sync");

                AddClientWaitingForSync(request.Clientid, request.Canlead, request.Source);
                RefreshWaitingStatus(request.Source);
                return new Empty();
            }
        }

        public override async Task<LeaderReply> WaitForSyncStart(ClientIdRequest request, ServerCallContext context) {
            Logger.Info($"Client {request.Clientid} is waiting to sync");

            try {
                if (TryGetCompletedSyncLeader(request.Clientid, request.Source, out var completedLeaderId)) {
                    return new LeaderReply() { LeaderId = completedLeaderId };
                }

                while (syncInProgress[request.Source] && ClientsAreWaitingForSync(request.Source)) {
                    await Task.Delay(10, context.CancellationToken);
                    if (TryGetCompletedSyncLeader(request.Clientid, request.Source, out completedLeaderId)) {
                        return new LeaderReply() { LeaderId = completedLeaderId };
                    }
                }
            } catch (OperationCanceledException) {
                RemoveClientReleasedFromCompletedSync(request.Clientid, request.Source);
                RemoveClientWaitingForSync(request.Clientid, request.Source);
                throw;
            }

            lock (lockobj) {
                if (TryGetCompletedSyncLeader(request.Clientid, request.Source, out var completedLeaderId)) {
                    return new LeaderReply() { LeaderId = completedLeaderId };
                }

                // If the syncLeader is not empty it was already elected            
                if (!syncLeader.ContainsKey(request.Source) || string.IsNullOrWhiteSpace(syncLeader[request.Source])) {
                    syncLeader[request.Source] = ElectSyncLeader(request.Source);
                    Logger.Debug($"Client {syncLeader[request.Source]} is leading sync");
                    if (string.IsNullOrEmpty(syncLeader[request.Source])) {
                        Logger.Error($"No instance could lead the {request.Source} sync! {Environment.NewLine}Registered Clients: {Environment.NewLine}{string.Join(Environment.NewLine, registeredClients[request.Source])}{Environment.NewLine}Clients Waiting For Sync:{Environment.NewLine}{string.Join(Environment.NewLine, clientsWaitingForSync[request.Source])}");
                        SetStatus(request.Source, $"No instance could lead the {request.Source} sync!");
                        clientsWaitingForSync[request.Source].Clear();
                        syncInProgress[request.Source] = false;
                        syncLeader[request.Source] = string.Empty;
                    }
                }

                return new LeaderReply() { LeaderId = syncLeader[request.Source] };
            }
        }

        private bool ClientsAreWaitingForSync(string source) {
            lock (lockobj) {
                if (!registeredClients.ContainsKey(source)) {
                    registeredClients[source] = new SortedDictionary<string, ClientSource>();
                }
                var reg = registeredClients[source].Where(x => x.Value.DateTime > DateTime.UtcNow.AddSeconds(-30)).Select(x => x.Key).ToList();
                return (reg.Intersect(clientsWaitingForSync[source].Keys).Count() < reg.Count);
            }
        }

        public override async Task<Empty> SetSyncInProgress(ClientIdRequest request, ServerCallContext context) {
            Logger.Info($"Client {request.Clientid} marking sync as in progress");
            SetStatus(request.Source, $"{request.Source} sync in progress");
            lock (lockobj) {
                syncLeader[request.Source] = request.Clientid;
            }
            return new Empty();
        }

        public override async Task<Empty> WaitForSyncCompleted(ClientIdRequest request, ServerCallContext context) {
            Logger.Info($"Client {request.Clientid} is waiting for sync completion {request.Source}");

            try {
                if (TryGetCompletedSyncLeader(request.Clientid, request.Source, out _)) {
                    RemoveClientReleasedFromCompletedSync(request.Clientid, request.Source);
                    RemoveClientWaitingForSync(request.Clientid, request.Source);
                    return new Empty();
                }

                while (syncInProgress[request.Source] && SyncLeaderIsAlive(request.Source)) {
                    await Task.Delay(10, context.CancellationToken);
                    if (TryGetCompletedSyncLeader(request.Clientid, request.Source, out _)) {
                        RemoveClientReleasedFromCompletedSync(request.Clientid, request.Source);
                        RemoveClientWaitingForSync(request.Clientid, request.Source);
                        return new Empty();
                    }
                }
            } catch (OperationCanceledException) {
                RemoveClientReleasedFromCompletedSync(request.Clientid, request.Source);
                RemoveClientWaitingForSync(request.Clientid, request.Source);
                throw;
            }

            RemoveClientWaitingForSync(request.Clientid, request.Source);

            return new Empty();
        }

        public bool SyncLeaderIsAlive(string source) {
            lock (lockobj) {
                if (!registeredClients.ContainsKey(source)) {
                    registeredClients[source] = new SortedDictionary<string, ClientSource>();
                }
                if (registeredClients[source].ContainsKey(syncLeader[source]) && registeredClients[source][syncLeader[source]].DateTime > DateTime.UtcNow.AddSeconds(-30)) {
                    return true;
                } else {
                    // If the syncLeader is empty it was already cleaned
                    if (syncInProgress[source] && syncLeader.ContainsKey(source) && !string.IsNullOrWhiteSpace(syncLeader[source])) {
                        // The sync leader is dead
                        Logger.Warning("The sync leader did not respond in the last 30 seconds.");
                        syncInProgress[source] = false;
                        syncLeader[source] = string.Empty;
                    }
                    return false;
                }

            }
        }

        public override async Task<Empty> SetSyncCompleted(ClientIdRequest request, ServerCallContext context) {
            lock (lockobj) {
                Logger.Info($"Client {request.Clientid} is marking sync to be complete");

                if (!clientsWaitingForSync.ContainsKey(request.Source)) {
                    clientsWaitingForSync[request.Source] = new SortedDictionary<string, bool>();
                }
                if (!completedSyncLeaderByClient.ContainsKey(request.Source)) {
                    completedSyncLeaderByClient[request.Source] = new Dictionary<string, string>();
                }

                foreach (var clientId in clientsWaitingForSync[request.Source].Keys) {
                    if (clientId != request.Clientid) {
                        completedSyncLeaderByClient[request.Source][clientId] = request.Clientid;
                    }
                }
                if (completedSyncLeaderByClient[request.Source].Count == 0) {
                    completedSyncLeaderByClient.Remove(request.Source);
                }

                clientsWaitingForSync[request.Source].Clear();
                syncInProgress[request.Source] = false;
                syncLeader[request.Source] = string.Empty;

                SetStatus(request.Source, string.Empty);

                return new Empty();
            }
        }

        public override async Task<Empty> WithdrawFromSync(ClientIdRequest request, ServerCallContext context) {
            lock(lockobj) { 
                RemoveClientReleasedFromCompletedSync(request.Clientid, request.Source);
                RemoveClientWaitingForSync(request.Clientid, request.Source);
                return new Empty();
            }
        }

        public override async Task<PingReply> Ping(ClientIdRequest request, ServerCallContext context) {
            Logger.Trace($"Client {request.Clientid} is pinging the server");
            UpdateClient(request.Clientid);

            return new PingReply() { Reply = "Pong" };
        }

        public override Task<Empty> SetFlag(FlagRequest request, ServerCallContext context) {
            lock (lockobj) {
                if (!flagsByKey.TryGetValue(request.Key, out var byClient)) {
                    byClient = new Dictionary<string, FlagEntry>();
                    flagsByKey[request.Key] = byClient;
                }
                var ttl = request.Ttlseconds > 0
                    ? TimeSpan.FromSeconds(request.Ttlseconds)
                    : (DefaultFlagTtl.TryGetValue(request.Key, out var d) ? d : TimeSpan.FromSeconds(15));
                if (!byClient.ContainsKey(request.Clientid)) {
                    Logger.Info($"Client {request.Clientid} set flag '{request.Key}' ({request.Reason})");
                }
                byClient[request.Clientid] = new FlagEntry(DateTime.UtcNow, request.Reason, ttl);
                PurgeExpiredFlags(request.Key);
                SetStatus(request.Key, BuildFlagStatus(request.Key));
            }
            return Task.FromResult(new Empty());
        }

        public override Task<Empty> ClearFlag(FlagRequest request, ServerCallContext context) {
            lock (lockobj) {
                if (flagsByKey.TryGetValue(request.Key, out var byClient) && byClient.Remove(request.Clientid)) {
                    Logger.Info($"Client {request.Clientid} cleared flag '{request.Key}'");
                }
                PurgeExpiredFlags(request.Key);
                SetStatus(request.Key, BuildFlagStatus(request.Key));
            }
            return Task.FromResult(new Empty());
        }

        public override Task<FlagsReply> GetFlags(Empty request, ServerCallContext context) {
            lock (lockobj) {
                var reply = new FlagsReply();
                foreach (var key in flagsByKey.Keys.ToList()) {
                    PurgeExpiredFlags(key);
                    if (!flagsByKey.TryGetValue(key, out var byClient) || byClient.Count == 0) { continue; }
                    var latest = byClient.OrderByDescending(x => x.Value.Time).First();
                    reply.Flags.Add(new FlagSnapshot() {
                        Key = key,
                        Set = true,
                        Reason = latest.Value.Reason ?? string.Empty,
                        Ownerid = latest.Key
                    });
                }
                return Task.FromResult(reply);
            }
        }

        private void PurgeExpiredFlags(string key) {
            // Caller must hold lockobj.
            if (!flagsByKey.TryGetValue(key, out var byClient)) { return; }
            var now = DateTime.UtcNow;
            var expired = byClient.Where(x => x.Value.Time < now - x.Value.Ttl).Select(x => x.Key).ToList();
            foreach (var id in expired) {
                byClient.Remove(id);
                Logger.Warning($"Flag '{key}' set by {id} expired without a refresh and was cleared");
            }
            if (byClient.Count == 0) { flagsByKey.Remove(key); }
        }

        private string BuildFlagStatus(string key) {
            // Caller must hold lockobj.
            if (!flagsByKey.TryGetValue(key, out var byClient) || byClient.Count == 0) { return string.Empty; }
            var latest = byClient.OrderByDescending(x => x.Value.Time).First();
            return string.IsNullOrWhiteSpace(latest.Value.Reason) ? key : latest.Value.Reason;
        }
    }
}
