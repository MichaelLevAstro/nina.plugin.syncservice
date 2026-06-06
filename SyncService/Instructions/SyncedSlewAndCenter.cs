using Newtonsoft.Json;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Core.Utility.WindowService;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.PlateSolving.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Platesolving;
using NINA.Sequencer.Utility;
using SyncService.Service;
using System;
using System.ComponentModel.Composition;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SyncService.Instructions {

    [ExportMetadata("Name", "Synced Slew and Center")]
    [ExportMetadata("Description", "On the instance connected to the mount, waits for any autofocus to finish, then slews to the target and plate-solve centers, while every other instance is paused (held by their Synced Mount Check) until the mount settles. Only place this on the mount instance - it does nothing elsewhere.")]
    [ExportMetadata("Icon", "SyncMountSVG")]
    [ExportMetadata("Category", "SyncService")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class SyncedSlewAndCenter : SequenceItem {
        private readonly IProfileService profileService;
        private readonly ITelescopeMediator telescopeMediator;
        private readonly IImagingMediator imagingMediator;
        private readonly IFilterWheelMediator filterWheelMediator;
        private readonly IGuiderMediator guiderMediator;
        private readonly IDomeMediator domeMediator;
        private readonly IDomeFollower domeFollower;
        private readonly IPlateSolverFactory plateSolverFactory;
        private readonly IWindowServiceFactory windowServiceFactory;
        private readonly PluginOptionsAccessor pluginSettings;
        private Center hosted;

        [ImportingConstructor]
        public SyncedSlewAndCenter(IProfileService profileService, ITelescopeMediator telescopeMediator, IImagingMediator imagingMediator,
                                   IFilterWheelMediator filterWheelMediator, IGuiderMediator guiderMediator, IDomeMediator domeMediator,
                                   IDomeFollower domeFollower, IPlateSolverFactory plateSolverFactory, IWindowServiceFactory windowServiceFactory) : base() {
            this.profileService = profileService;
            this.telescopeMediator = telescopeMediator;
            this.imagingMediator = imagingMediator;
            this.filterWheelMediator = filterWheelMediator;
            this.guiderMediator = guiderMediator;
            this.domeMediator = domeMediator;
            this.domeFollower = domeFollower;
            this.plateSolverFactory = plateSolverFactory;
            this.windowServiceFactory = windowServiceFactory;
            this.hosted = NewHosted();
            this.hosted.Inherited = true;

            var assembly = this.GetType().Assembly;
            var id = assembly.GetCustomAttribute<GuidAttribute>().Value;
            this.pluginSettings = new PluginOptionsAccessor(profileService, Guid.Parse(id));
        }

        private Center NewHosted() =>
            new Center(profileService, telescopeMediator, imagingMediator, filterWheelMediator, guiderMediator,
                       domeMediator, domeFollower, plateSolverFactory, windowServiceFactory);

        public override object Clone() {
            var clone = new SyncedSlewAndCenter(profileService, telescopeMediator, imagingMediator, filterWheelMediator, guiderMediator,
                                                domeMediator, domeFollower, plateSolverFactory, windowServiceFactory);
            clone.CopyMetaData(this);
            clone.hosted = (Center)hosted.Clone();
            return clone;
        }

        [JsonProperty]
        public InputCoordinates Coordinates {
            get => hosted.Coordinates;
            set { hosted.Coordinates = value; RaisePropertyChanged(); }
        }

        [JsonProperty]
        public bool Inherited {
            get => hosted.Inherited;
            set { hosted.Inherited = value; RaisePropertyChanged(); }
        }

        private ISyncServiceClient client => SyncServiceClient.Instance;

        public override void AfterParentChanged() {
            hosted.AttachNewParent(this.Parent);
            hosted.AfterParentChanged();
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (telescopeMediator.GetInfo()?.Connected != true) {
                Logger.Info("SyncedSlewAndCenter: no mount connected on this instance - skipping");
                return;
            }

            var afTimeout = TimeSpan.FromSeconds(pluginSettings.GetValueInt32(nameof(SyncServicePlugin.AutofocusBusyWaitTimeout), 300));
            await SyncBarrier.WaitWhileAutofocusBusy(client, afTimeout, progress, token);

            var reason = string.IsNullOrWhiteSpace(Name) ? "Synced center and slew" : Name;
            client.BeginPlannedMountOperation();
            using var refreshCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task refreshTask = null;
            try {
                await client.SetMountBusy(reason, token);
                refreshTask = SyncBarrier.RefreshLoop(ct => client.SetMountBusy(reason, ct), 5000, refreshCts.Token);
                await hosted.Execute(progress, token);
            } finally {
                refreshCts.Cancel();
                if (refreshTask != null) { try { await refreshTask; } catch (Exception) { } }
                try { await client.ClearMountBusy(CancellationToken.None); } catch (Exception ex) { Logger.Error("Failed to clear mount busy", ex); }
                client.EndPlannedMountOperation();
                progress?.Report(new ApplicationStatus() { Status = "" });
            }
        }

        public override string ToString() {
            return $"Instruction: {nameof(SyncedSlewAndCenter)}";
        }
    }
}
