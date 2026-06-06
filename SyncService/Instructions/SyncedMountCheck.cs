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

    [ExportMetadata("Name", "Synced Mount Check")]
    [ExportMetadata("Description", "Place this once around the exposures on every instance that does NOT own the mount. When the mount instance runs a Synced Meridian Flip or Synced Center after Drift it joins the rendezvous and holds until that finishes; it also holds (as a safety net) whenever the mount moves unexpectedly. The in-flight exposure finishes first. Does nothing on the instance connected to the mount.")]
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
            // Register for both rendezvous sources so the mount instance's WaitForSyncStart counts this aux.
            TryRegister(SyncSources.MeridianFlip);
            TryRegister(SyncSources.CenterAfterDrift);
            TryRegister(SyncSources.MountGuard);
        }

        public override void Teardown() {
            TryUnregister(SyncSources.MeridianFlip);
            TryUnregister(SyncSources.CenterAfterDrift);
            TryUnregister(SyncSources.MountGuard);
        }

        private void TryRegister(string source) {
            try { client.RegisterSync(source); } catch (Exception ex) { Logger.Error(ex); }
        }

        private void TryUnregister(string source) {
            try { client.UnregisterSync(source); } catch (Exception ex) { Logger.Error(ex); }
        }

        public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) {
            if (!Enabled) { return false; }
            if (IsThisTheMountInstance()) { return false; }
            return client.IsOperationPendingCached(SyncSources.MeridianFlip)
                || client.IsOperationPendingCached(SyncSources.CenterAfterDrift)
                || client.MountBusyCached;
        }

        public override async Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (IsThisTheMountInstance()) { return; }

            // Flip takes precedence over drift if both are somehow pending.
            if (client.IsOperationPendingCached(SyncSources.MeridianFlip)) {
                var timeout = TimeSpan.FromSeconds(pluginSettings.GetValueInt32(nameof(SyncServicePlugin.RendezvousTimeout), 600));
                Logger.Info("Holding for a synchronized meridian flip on the mount instance");
                progress?.Report(new ApplicationStatus() { Status = "Holding for meridian flip" });
                try {
                    await SyncBarrier.RunAsFollower(client, SyncSources.MeridianFlip, timeout, token);
                } finally {
                    progress?.Report(new ApplicationStatus() { Status = "" });
                }
                return;
            }

            if (client.IsOperationPendingCached(SyncSources.CenterAfterDrift)) {
                var timeout = TimeSpan.FromSeconds(pluginSettings.GetValueInt32(nameof(SyncServicePlugin.RendezvousTimeout), 600));
                Logger.Info("Holding for a synchronized recenter on the mount instance");
                progress?.Report(new ApplicationStatus() { Status = "Holding for recenter" });
                try {
                    await SyncBarrier.RunAsFollower(client, SyncSources.CenterAfterDrift, timeout, token);
                } finally {
                    progress?.Report(new ApplicationStatus() { Status = "" });
                }
                return;
            }

            // Reactive branch: the mount moved (planned Center&Slew or an unexpected move). Hold until idle.
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
