using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Core.Utility;
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
    [ExportMetadata("Description", "Place this on the mount instance at the point where it is ready to image (after slewing, centering and once it is guiding on target). It releases every instance that is waiting at a 'Synced Start Imaging' so they all start imaging together. It also waits briefly for those instances to be ready.")]
    [ExportMetadata("Icon", "SyncWaitSVG")]
    [ExportMetadata("Category", "SyncService")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class SyncedBeginImaging : SequenceItem {
        private readonly IProfileService profileService;
        private readonly PluginOptionsAccessor pluginSettings;

        [ImportingConstructor]
        public SyncedBeginImaging(IProfileService profileService) : base() {
            this.profileService = profileService;
            var assembly = this.GetType().Assembly;
            var id = assembly.GetCustomAttribute<GuidAttribute>().Value;
            this.pluginSettings = new PluginOptionsAccessor(profileService, Guid.Parse(id));
        }

        private SyncedBeginImaging(SyncedBeginImaging cloneMe) : this(cloneMe.profileService) {
            CopyMetaData(cloneMe);
        }

        public override object Clone() {
            return new SyncedBeginImaging(this);
        }

        private ISyncServiceClient client => SyncServiceClient.Instance;

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
            Logger.Info("Releasing waiting instances to begin imaging together");
            progress?.Report(new ApplicationStatus() { Status = "Starting imaging on all instances" });
            try {
                // Leader with no work - announcing + completing is the "go" signal for the waiting instances.
                await SyncBarrier.RunAsLeader(client, SyncSources.StartImaging, timeout, () => Task.CompletedTask, token);
            } finally {
                progress?.Report(new ApplicationStatus() { Status = "" });
            }
        }

        public override string ToString() {
            return $"Instruction: {nameof(SyncedBeginImaging)}";
        }
    }
}
