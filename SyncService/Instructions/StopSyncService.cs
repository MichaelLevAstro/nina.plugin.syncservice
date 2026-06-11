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

    [ExportMetadata("Name", "Stop Sync Service")]
    [ExportMetadata("Description", "Stops the synchronization service across ALL instances. Place once (on any instance) where coordination should end - afterwards every Synced instruction passes through and runs on its own, and the status spinner and mount watching go idle until the service is started again.")]
    [ExportMetadata("Icon", "SyncWaitSVG")]
    [ExportMetadata("Category", "SyncService")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class StopSyncService : SequenceItem {

        [ImportingConstructor]
        public StopSyncService() : base() { }

        private StopSyncService(StopSyncService cloneMe) : this() {
            CopyMetaData(cloneMe);
        }

        public override object Clone() {
            return new StopSyncService(this);
        }

        private ISyncServiceClient client => SyncServiceClient.Instance;

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            Logger.Info("Stopping the synchronization service across all instances");
            await client.SetServiceState(false, token);
        }

        public override string ToString() {
            return $"Instruction: {nameof(StopSyncService)}";
        }
    }
}
