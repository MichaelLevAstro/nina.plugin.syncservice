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

    [ExportMetadata("Name", "Synced Start Imaging")]
    [ExportMetadata("Description", "Place this at the start of the imaging on every instance that does NOT control the mount. It waits here until the mount instance reaches its 'Synced Begin Imaging' (i.e. it has finished slewing, centering and is guiding on target), so all instances start imaging together.")]
    [ExportMetadata("Icon", "SyncWaitSVG")]
    [ExportMetadata("Category", "SyncService")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class SyncedStartImaging : SequenceItem {
        private readonly IProfileService profileService;
        private readonly PluginOptionsAccessor pluginSettings;

        [ImportingConstructor]
        public SyncedStartImaging(IProfileService profileService) : base() {
            this.profileService = profileService;
            var assembly = this.GetType().Assembly;
            var id = assembly.GetCustomAttribute<GuidAttribute>().Value;
            this.pluginSettings = new PluginOptionsAccessor(profileService, Guid.Parse(id));
        }

        private SyncedStartImaging(SyncedStartImaging cloneMe) : this(cloneMe.profileService) {
            CopyMetaData(cloneMe);
        }

        public override object Clone() {
            return new SyncedStartImaging(this);
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
            Logger.Info("Waiting for the mount instance to begin imaging");
            progress?.Report(new ApplicationStatus() { Status = "Waiting for the mount instance to begin imaging" });
            try {
                await SyncBarrier.RunAsFollower(client, SyncSources.StartImaging, timeout, token);
            } finally {
                progress?.Report(new ApplicationStatus() { Status = "" });
            }
        }

        public override string ToString() {
            return $"Instruction: {nameof(SyncedStartImaging)}";
        }
    }
}
