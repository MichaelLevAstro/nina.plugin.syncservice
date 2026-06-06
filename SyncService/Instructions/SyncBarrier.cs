using Grpc.Core;
using NINA.Core.Model;
using NINA.Core.Utility;
using SyncService.Service;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SyncService.Instructions {
    /// <summary>
    /// Shared leader/follower rendezvous helpers built on the per-source barrier. The MAIN (mount owner) is the
    /// leader (it announced canLead=true); the aux are followers (canLead=false). The leader always marks the
    /// sync complete - even on cancellation - so waiting followers are released and never strand.
    /// </summary>
    internal static class SyncBarrier {
        /// <summary>
        /// MAIN side: announce, wait until every aux has arrived, run <paramref name="work"/> while they wait,
        /// then release them. The caller raises/clears the operation-pending flag around this.
        /// </summary>
        public static async Task RunAsLeader(ISyncServiceClient client, string source, TimeSpan timeout, Func<Task> work, CancellationToken token) {
            var announced = false;
            var completed = false;
            try {
                announced = true;
                await client.AnnounceToSync(source, true, token);
                await client.WaitForSyncStart(source, token, timeout);
                await client.SetSyncInProgress(source, token);
                await work();
                await client.SetSyncComplete(source, token);
                completed = true;
            } catch (RpcException e) when (e.StatusCode == StatusCode.Cancelled) {
                if (announced && !completed) { await SafeComplete(client, source); completed = true; }
                throw new OperationCanceledException($"The synchronized {source} was cancelled", e, token);
            } catch (OperationCanceledException) {
                if (announced && !completed) { await SafeComplete(client, source); completed = true; }
                throw;
            } catch (Exception) {
                // The leader's work failed - release any waiting followers before propagating.
                if (announced && !completed) { await SafeComplete(client, source); completed = true; }
                throw;
            } finally {
                if (announced && !completed) { await SafeWithdraw(client, source); }
            }
        }

        /// <summary>
        /// AUX side: announce arrival, then block until the leader marks the operation complete. Fails open -
        /// a leaderless or timed-out rendezvous lets this instance proceed rather than crash or strand.
        /// </summary>
        public static async Task RunAsFollower(ISyncServiceClient client, string source, TimeSpan timeout, CancellationToken token) {
            var announced = false;
            var completed = false;
            try {
                announced = true;
                await client.AnnounceToSync(source, false, token);
                await client.WaitForSyncStart(source, token, timeout);
                await client.WaitForSyncComplete(source, token, timeout);
                completed = true;
            } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                throw;
            } catch (RpcException e) when (e.StatusCode == StatusCode.Cancelled && token.IsCancellationRequested) {
                throw new OperationCanceledException($"The synchronized {source} was cancelled", e, token);
            } catch (Exception ex) {
                Logger.Warning($"Synchronized {source} did not complete ({ex.Message}) - proceeding");
            } finally {
                if (announced && !completed) { await SafeWithdraw(client, source); }
            }
        }

        /// <summary>
        /// Periodically re-invoke <paramref name="action"/> (e.g. re-set a flag) until cancelled, so a long
        /// operation keeps the server-side flag ttl alive. The caller sets the flag once before starting this.
        /// </summary>
        public static async Task RefreshLoop(Func<CancellationToken, Task> action, int intervalMs, CancellationToken ct) {
            while (!ct.IsCancellationRequested) {
                try {
                    await Task.Delay(intervalMs, ct);
                    await action(ct);
                } catch (OperationCanceledException) {
                    break;
                } catch (Exception ex) {
                    Logger.Error("Flag refresh failed", ex);
                }
            }
        }

        /// <summary>
        /// Block (bounded) while any instance is autofocusing, so the main does not slew/center during an AF run.
        /// A meridian flip does NOT use this - it preempts autofocus instead.
        /// </summary>
        public static async Task WaitWhileAutofocusBusy(ISyncServiceClient client, TimeSpan timeout, IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (!client.AutofocusBusyCached) { return; }
            var deadline = DateTime.UtcNow + timeout;
            progress?.Report(new ApplicationStatus() { Status = "Waiting for autofocus to finish" });
            while (client.AutofocusBusyCached) {
                token.ThrowIfCancellationRequested();
                if (DateTime.UtcNow > deadline) {
                    Logger.Warning("Timed out waiting for autofocus to finish - proceeding with the mount move");
                    break;
                }
                await Task.Delay(200, token);
            }
        }

        private static async Task SafeComplete(ISyncServiceClient client, string source) {
            try { await client.SetSyncComplete(source, CancellationToken.None); } catch (Exception ex) { Logger.Error("Failed to mark sync complete", ex); }
        }

        private static async Task SafeWithdraw(ISyncServiceClient client, string source) {
            try { await client.WithdrawFromSync(source, CancellationToken.None); } catch (Exception ex) { Logger.Error("Failed to withdraw from sync", ex); }
        }
    }
}
