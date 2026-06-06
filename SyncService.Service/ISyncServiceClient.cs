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

        // Generic keyed flags (heartbeat-cached for synchronous reads in ShouldTrigger).
        Task SetFlag(string key, string reason, TimeSpan ttl, CancellationToken ct);
        Task ClearFlag(string key, CancellationToken ct);

        // Mount busy (planned Center&Slew + reactive observer). Aux react via the Mount Check.
        bool MountBusyCached { get; }
        Task SetMountBusy(string reason, CancellationToken ct);
        Task ClearMountBusy(CancellationToken ct);

        // Operation pending (rendezvous wake-up for meridian flip / center-after-drift).
        bool IsOperationPendingCached(string source);
        Task SetOperationPending(string source, string reason, CancellationToken ct);
        Task ClearOperationPending(string source, CancellationToken ct);

        // The meridian-flip pending flag doubles as the "preempt autofocus" signal.
        bool FlipPreemptCached { get; }

        // Autofocus busy (reverse hold - blocks the main's mount moves).
        bool AutofocusBusyCached { get; }
        Task SetAutofocusBusy(string reason, CancellationToken ct);
        Task ClearAutofocusBusy(CancellationToken ct);

        // Process-local gate so the reactive observer only reacts to UNEXPECTED moves.
        bool PlannedMountOperationInProgress { get; }
        void BeginPlannedMountOperation();
        void EndPlannedMountOperation();
    }
}
