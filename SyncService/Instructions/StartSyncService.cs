using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Sequencer.SequenceItem;
using SyncService.Service;
using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;

namespace SyncService.Instructions {

    [ExportMetadata("Name", "Start Sync Service")]
    [ExportMetadata("Description", "Starts the synchronization service across ALL instances. Place once (on any instance) at the point where the instances should begin coordinating - until the service is started every Synced instruction passes through and runs on its own without holding the others.")]
    [ExportMetadata("Icon", "SyncWaitSVG")]
    [ExportMetadata("Category", "SyncService")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class StartSyncService : SequenceItem {

        [ImportingConstructor]
        public StartSyncService() : base() { }

        private StartSyncService(StartSyncService cloneMe) : this() {
            CopyMetaData(cloneMe);
        }

        public override object Clone() {
            return new StartSyncService(this);
        }

        private ISyncServiceClient client => SyncServiceClient.Instance;

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            Logger.Info("Starting the synchronization service across all instances");
            await client.SetServiceState(true, token);
        }

        public override string ToString() {
            return $"Instruction: {nameof(StartSyncService)}";
        }
    }
}
