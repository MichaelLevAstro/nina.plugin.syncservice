using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SyncService.Service {
    public interface ISyncServiceClient {
        void RegisterSync(string source);
        void UnregisterSync(string source);
        Task Register(string source);
        Task Unregister(string source);
        Task<bool> WaitForSyncStart(string source, CancellationToken ct, TimeSpan timeout);
        Task WaitForSyncComplete(string source, CancellationToken ct, TimeSpan timeout);
        Task SetSyncInProgress(string source, CancellationToken ct);
        Task SetSyncComplete(string source, CancellationToken ct);
        Task AnnounceToSync(string source, bool canLead, CancellationToken ct);
        Task WithdrawFromSync(string source, CancellationToken ct);
        Task<string> Ping(CancellationToken ct);

        // Fleet-wide service on/off. ServiceActiveCached is kept fresh by the plugin's state watcher; the sync
        // primitives read it so that while stopped every instruction passes through without coordinating.
        bool ServiceActiveCached { get; }
        Task SetServiceState(bool active, CancellationToken ct);
        Task<bool> RefreshServiceState(CancellationToken ct);

        // Generic keyed flags (heartbeat-cached for synchronous reads in ShouldTrigger).
        Task SetFlag(string key, string reason, TimeSpan ttl, CancellationToken ct);
        Task ClearFlag(string key, CancellationToken ct);

        // Mount busy (planned Center&Slew + reactive observer). Aux react via the Mount Check.
        bool MountBusyCached { get; }
        Task SetMountBusy(string reason, CancellationToken ct);
        Task ClearMountBusy(CancellationToken ct);

        // Operation pending (rendezvous wake-up). Every planned mount operation uses the single
        // SyncSources.MountOp source; the reason carries the human-readable kind for the status line.
        bool IsOperationPendingCached(string source);
        string MountOpReason { get; }
        Task SetOperationPending(string source, string reason, CancellationToken ct);
        Task ClearOperationPending(string source, CancellationToken ct);

        // Autofocus busy (reverse hold - blocks the main's mount moves while any instance focuses).
        bool AutofocusBusyCached { get; }
        Task SetAutofocusBusy(string reason, CancellationToken ct);
        Task ClearAutofocusBusy(CancellationToken ct);

        // Autofocus preempt - set ONLY by the time-critical meridian flip to cancel in-flight autofocus.
        bool AutofocusPreemptCached { get; }
        Task SetAutofocusPreempt(string reason, CancellationToken ct);
        Task ClearAutofocusPreempt(CancellationToken ct);

        // Process-local gate so the reactive observer only reacts to UNEXPECTED moves.
        bool PlannedMountOperationInProgress { get; }
        void BeginPlannedMountOperation();
        void EndPlannedMountOperation();
    }
}
