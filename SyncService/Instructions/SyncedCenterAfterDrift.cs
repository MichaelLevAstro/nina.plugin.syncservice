using Newtonsoft.Json;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Trigger.Platesolving;
using NINA.Sequencer.Utility;
using NINA.WPF.Base.Interfaces.Mediator;
using SyncService.Service;
using System;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SyncService.Instructions {

    [ExportMetadata("Name", "Synced Center after Drift (main)")]
    [ExportMetadata("Description", "Place on the mount instance. Measures pointing drift via plate-solving and, when it exceeds the threshold, waits for any autofocus to finish, pauses every other instance until they have all arrived at their Synced Mount Check, recenters, then releases them. Does nothing on instances without a mount - put a Synced Mount Check on those.")]
    [ExportMetadata("Icon", "SyncMountSVG")]
    [ExportMetadata("Category", "SyncService")]
    [Export(typeof(ISequenceTrigger))]
    [JsonObject(MemberSerialization.OptIn)]
    public class SyncedCenterAfterDrift : SequenceTrigger {
        private readonly IProfileService profileService;
        private readonly ITelescopeMediator telescopeMediator;
        private readonly IFilterWheelMediator filterWheelMediator;
        private readonly IGuiderMediator guiderMediator;
        private readonly IImagingMediator imagingMediator;
        private readonly ICameraMediator cameraMediator;
        private readonly IDomeMediator domeMediator;
        private readonly IDomeFollower domeFollower;
        private readonly IImageSaveMediator imageSaveMediator;
        private readonly IApplicationStatusMediator applicationStatusMediator;
        private readonly PluginOptionsAccessor pluginSettings;
        private CenterAfterDriftTrigger hosted;

        [ImportingConstructor]
        public SyncedCenterAfterDrift(IProfileService profileService, ITelescopeMediator telescopeMediator, IFilterWheelMediator filterWheelMediator,
                                      IGuiderMediator guiderMediator, IImagingMediator imagingMediator, ICameraMediator cameraMediator,
                                      IDomeMediator domeMediator, IDomeFollower domeFollower, IImageSaveMediator imageSaveMediator,
                                      IApplicationStatusMediator applicationStatusMediator) : base() {
            this.profileService = profileService;
            this.telescopeMediator = telescopeMediator;
            this.filterWheelMediator = filterWheelMediator;
            this.guiderMediator = guiderMediator;
            this.imagingMediator = imagingMediator;
            this.cameraMediator = cameraMediator;
            this.domeMediator = domeMediator;
            this.domeFollower = domeFollower;
            this.imageSaveMediator = imageSaveMediator;
            this.applicationStatusMediator = applicationStatusMediator;
            this.hosted = NewHosted();
            this.hosted.Inherited = true;
            this.hosted.PropertyChanged += OnHostedPropertyChanged;

            var assembly = this.GetType().Assembly;
            var id = assembly.GetCustomAttribute<GuidAttribute>().Value;
            this.pluginSettings = new PluginOptionsAccessor(profileService, Guid.Parse(id));
        }

        private CenterAfterDriftTrigger NewHosted() =>
            new CenterAfterDriftTrigger(profileService, telescopeMediator, filterWheelMediator, guiderMediator, imagingMediator,
                                        cameraMediator, domeMediator, domeFollower, imageSaveMediator, applicationStatusMediator);

        public override object Clone() {
            var clone = new SyncedCenterAfterDrift(profileService, telescopeMediator, filterWheelMediator, guiderMediator, imagingMediator,
                                                   cameraMediator, domeMediator, domeFollower, imageSaveMediator, applicationStatusMediator);
            clone.CopyMetaData(this);
            clone.DistanceArcMinutes = DistanceArcMinutes;
            clone.AfterExposures = AfterExposures;
            return clone;
        }

        /// <summary>Live measured drift since the last check (mirrors the hosted Center after Drift trigger).</summary>
        public double LastDistanceArcMinutes => hosted.LastDistanceArcMinutes;
        public int ProgressExposures => hosted.ProgressExposures;

        private void OnHostedPropertyChanged(object sender, PropertyChangedEventArgs e) {
            // Re-raise the hosted trigger's change notifications so the UI shows the live drift readout.
            RaisePropertyChanged(e.PropertyName);
        }

        [JsonProperty]
        public double DistanceArcMinutes {
            get => hosted.DistanceArcMinutes;
            set { hosted.DistanceArcMinutes = value; RaisePropertyChanged(); }
        }

        [JsonProperty]
        public int AfterExposures {
            get => hosted.AfterExposures;
            set { hosted.AfterExposures = value; RaisePropertyChanged(); }
        }

        private ISyncServiceClient client => SyncServiceClient.Instance;

        private bool IsThisTheMountInstance() => telescopeMediator.GetInfo()?.Connected == true;

        public override void AfterParentChanged() {
            hosted.AttachNewParent(this.Parent);
            hosted.AfterParentChanged();
            var root = ItemUtility.GetRootContainer(this.Parent);
            if (root?.Status == NINA.Core.Enum.SequenceEntityStatus.RUNNING) {
                Initialize();
            } else {
                Teardown();
            }
        }

        public override void Initialize() {
            try { client.RegisterSync(SyncSources.MountOp); } catch (Exception ex) { Logger.Error(ex); }
            try { hosted.Initialize(); } catch (Exception ex) { Logger.Error(ex); }
        }

        public override void Teardown() {
            try { hosted.Teardown(); } catch (Exception ex) { Logger.Error(ex); }
            try { client.UnregisterSync(SyncSources.MountOp); } catch (Exception ex) { Logger.Error(ex); }
        }

        public override void SequenceBlockInitialize() => hosted.SequenceBlockInitialize();
        public override void SequenceBlockTeardown() => hosted.SequenceBlockTeardown();

        public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) {
            if (!IsThisTheMountInstance()) { return false; }
            if (client.IsOperationPendingCached(SyncSources.MountOp)) { return false; }
            return hosted.ShouldTrigger(previousItem, nextItem);
        }

        public override async Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (!IsThisTheMountInstance()) { return; }

            var rendezvous = TimeSpan.FromSeconds(pluginSettings.GetValueInt32(nameof(SyncServicePlugin.RendezvousTimeout), 600));
            var afTimeout = TimeSpan.FromSeconds(pluginSettings.GetValueInt32(nameof(SyncServicePlugin.AutofocusBusyWaitTimeout), 300));

            // Wait for any autofocus to finish before moving the mount (the reverse hold).
            await SyncBarrier.RunMountOperation(client, "Recenter after drift", preemptAutofocus: false, rendezvous, afTimeout, async () => {
                progress?.Report(new ApplicationStatus() { Status = "Recentering after drift" });
                await hosted.Execute(context, progress, token);
            }, progress, token);
        }

        public override string ToString() {
            return $"Trigger: {nameof(SyncedCenterAfterDrift)}, DistanceArcMinutes: {DistanceArcMinutes}";
        }
    }
}
