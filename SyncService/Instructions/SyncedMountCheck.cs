using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Utility;
using SyncService.Service;
using System;
using System.ComponentModel.Composition;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SyncService.Instructions {

    [ExportMetadata("Name", "Synced Mount Check (aux)")]
    [ExportMetadata("Description", "Place this once around the exposures on every instance that does NOT own the mount. It holds this instance whenever the mount instance runs ANY mount operation - dither, meridian flip or center after drift - and, as a safety net, whenever the mount moves unexpectedly. The in-flight exposure finishes first. Does nothing on the instance connected to the mount.")]
    [ExportMetadata("Icon", "SyncMountSVG")]
    [ExportMetadata("Category", "SyncService")]
    [Export(typeof(ISequenceTrigger))]
    [JsonObject(MemberSerialization.OptIn)]
    public class SyncedMountCheck : SequenceTrigger {
        private readonly IProfileService profileService;
        private readonly ITelescopeMediator telescopeMediator;
        private readonly PluginOptionsAccessor pluginSettings;

        [ImportingConstructor]
        public SyncedMountCheck(IProfileService profileService, ITelescopeMediator telescopeMediator) : base() {
            this.profileService = profileService;
            this.telescopeMediator = telescopeMediator;

            var assembly = this.GetType().Assembly;
            var id = assembly.GetCustomAttribute<GuidAttribute>().Value;
            this.pluginSettings = new PluginOptionsAccessor(profileService, Guid.Parse(id));
        }

        private SyncedMountCheck(SyncedMountCheck cloneMe) : this(cloneMe.profileService, cloneMe.telescopeMediator) {
            CopyMetaData(cloneMe);
        }

        public override object Clone() {
            return new SyncedMountCheck(this);
        }

        private ISyncServiceClient client => SyncServiceClient.Instance;

        private bool IsThisTheMountInstance() => telescopeMediator.GetInfo()?.Connected == true;

        private bool Enabled => pluginSettings.GetValueBoolean(nameof(SyncServicePlugin.MountSyncEnabled), true);

        public override void AfterParentChanged() {
            var root = ItemUtility.GetRootContainer(this.Parent);
            if (root?.Status == NINA.Core.Enum.SequenceEntityStatus.RUNNING) {
                Initialize();
            } else {
                Teardown();
            }
        }

        public override void Initialize() {
            // Register for the single mount-operation rendezvous so the mount instance's WaitForSyncStart counts this aux.
            TryRegister(SyncSources.MountOp);
        }

        public override void Teardown() {
            TryUnregister(SyncSources.MountOp);
        }

        private void TryRegister(string source) {
            try { client.RegisterSync(source); } catch (Exception ex) { Logger.Error(ex); }
        }

        private void TryUnregister(string source) {
            try { client.UnregisterSync(source); } catch (Exception ex) { Logger.Error(ex); }
        }

        public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) {
            if (!Enabled) { return false; }
            if (!client.ServiceActiveCached) { return false; }
            if (IsThisTheMountInstance()) { return false; }
            return client.IsOperationPendingCached(SyncSources.MountOp)
                || client.MountBusyCached;
        }

        public override async Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (IsThisTheMountInstance()) { return; }

            // One rendezvous for every planned mount operation (dither / flip / center after drift). The kind
            // is carried in the pending reason purely for the status line.
            if (client.IsOperationPendingCached(SyncSources.MountOp)) {
                var timeout = TimeSpan.FromSeconds(pluginSettings.GetValueInt32(nameof(SyncServicePlugin.RendezvousTimeout), 600));
                var reason = client.MountOpReason;
                var status = string.IsNullOrWhiteSpace(reason) ? "Holding for the mount instance" : $"Holding for {reason.ToLowerInvariant()}";
                Logger.Info($"Holding for a synced mount operation on the mount instance ({(string.IsNullOrWhiteSpace(reason) ? "mount operation" : reason)})");
                progress?.Report(new ApplicationStatus() { Status = status });
                try {
                    await SyncBarrier.RunAsFollower(client, SyncSources.MountOp, timeout, token);
                } finally {
                    progress?.Report(new ApplicationStatus() { Status = "" });
                }
                return;
            }

            // Reactive branch: the mount moved (flag-based Slew and Center or an unexpected move). Hold until idle.
            var holdTimeout = TimeSpan.FromSeconds(pluginSettings.GetValueInt32(nameof(SyncServicePlugin.MountWaitTimeout), 600));
            var deadline = DateTime.UtcNow + holdTimeout;
            Logger.Info("Mount is moving - holding imaging until it settles");
            progress?.Report(new ApplicationStatus() { Status = "Mount is moving - holding" });
            try {
                while (client.MountBusyCached) {
                    token.ThrowIfCancellationRequested();
                    if (DateTime.UtcNow > deadline) {
                        Logger.Warning("Synced Mount Check timed out waiting for the mount to settle - resuming");
                        break;
                    }
                    await Task.Delay(200, token);
                }
            } finally {
                progress?.Report(new ApplicationStatus() { Status = "" });
            }
        }

        public override string ToString() {
            return $"Trigger: {nameof(SyncedMountCheck)}";
        }
    }
}
