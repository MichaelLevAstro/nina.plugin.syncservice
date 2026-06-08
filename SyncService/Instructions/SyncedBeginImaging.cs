using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Utility;
using SyncService.Service;
using System;
using System.ComponentModel.Composition;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SyncService.Instructions {

    [ExportMetadata("Name", "Synced Begin Imaging")]
    [ExportMetadata("Description", "Place this on every instance at the point where it is ready to begin imaging - on the mount instance that is after it has slewed, centered and is guiding on target. The instance connected to the mount releases all the other instances, which wait here until it does, so every instance starts imaging together. The mount instance is detected automatically.")]
    [ExportMetadata("Icon", "SyncWaitSVG")]
    [ExportMetadata("Category", "SyncService")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class SyncedBeginImaging : SequenceItem {
        private readonly IProfileService profileService;
        private readonly ITelescopeMediator telescopeMediator;
        private readonly PluginOptionsAccessor pluginSettings;

        [ImportingConstructor]
        public SyncedBeginImaging(IProfileService profileService, ITelescopeMediator telescopeMediator) : base() {
            this.profileService = profileService;
            this.telescopeMediator = telescopeMediator;
            var assembly = this.GetType().Assembly;
            var id = assembly.GetCustomAttribute<GuidAttribute>().Value;
            this.pluginSettings = new PluginOptionsAccessor(profileService, Guid.Parse(id));
        }

        private SyncedBeginImaging(SyncedBeginImaging cloneMe) : this(cloneMe.profileService, cloneMe.telescopeMediator) {
            CopyMetaData(cloneMe);
        }

        public override object Clone() {
            return new SyncedBeginImaging(this);
        }

        private ISyncServiceClient client => SyncServiceClient.Instance;

        private bool IsThisTheMountInstance() => telescopeMediator.GetInfo()?.Connected == true;

        public override void AfterParentChanged() {
            var root = ItemUtility.GetRootContainer(this.Parent);
            if (root?.Status == NINA.Core.Enum.SequenceEntityStatus.RUNNING) {
                Initialize();
            } else {
                Teardown();
            }
        }

        public override void Initialize() {
            try { client.RegisterSync(SyncSources.StartImaging); } catch (Exception ex) { Logger.Error(ex); }
        }

        public override void Teardown() {
            try { client.UnregisterSync(SyncSources.StartImaging); } catch (Exception ex) { Logger.Error(ex); }
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            var timeout = TimeSpan.FromSeconds(pluginSettings.GetValueInt32(nameof(SyncServicePlugin.StartImagingTimeout), 1800));
            if (IsThisTheMountInstance()) {
                Logger.Info("This instance owns the mount - releasing waiting instances to begin imaging together");
                progress?.Report(new ApplicationStatus() { Status = "Starting imaging on all instances" });
                try {
                    // Leader with no work - announcing + completing is the "go" signal for the waiting instances.
                    await SyncBarrier.RunAsLeader(client, SyncSources.StartImaging, timeout, () => Task.CompletedTask, token);
                } finally {
                    progress?.Report(new ApplicationStatus() { Status = "" });
                }
            } else {
                Logger.Info("Waiting for the mount instance to begin imaging");
                progress?.Report(new ApplicationStatus() { Status = "Waiting for the mount instance to begin imaging" });
                try {
                    await SyncBarrier.RunAsFollower(client, SyncSources.StartImaging, timeout, token);
                } finally {
                    progress?.Report(new ApplicationStatus() { Status = "" });
                }
            }
        }

        public override string ToString() {
            return $"Instruction: {nameof(SyncedBeginImaging)}";
        }
    }
}
