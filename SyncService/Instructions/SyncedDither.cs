using Newtonsoft.Json;
using NINA.Core.Locale;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Guider;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Utility;
using NINA.Sequencer.Validations;
using NINA.WPF.Base.Interfaces.ViewModel;
using SyncService.Service;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SyncService.Instructions {

    [ExportMetadata("Name", "Synced Dither (main)")]
    [ExportMetadata("Description", "Place on the mount/guider instance. A dither moves the shared mount, so it is a mount operation: after the configured number of exposures it waits for any autofocus to finish, pauses every other instance until they have all arrived at their Synced Mount Check, performs one real dither, then releases them. Other instances do NOT need this - their Synced Mount Check holds them during the dither.")]
    [ExportMetadata("Icon", "SyncDitherSVG")]
    [ExportMetadata("Category", "SyncService")]
    [Export(typeof(ISequenceTrigger))]
    [JsonObject(MemberSerialization.OptIn)]
    public class SyncedDither : SequenceTrigger, IValidatable {
        private IGuiderMediator guiderMediator;
        private IImageHistoryVM history;
        private IProfileService profileService;

        [ImportingConstructor]
        public SyncedDither(IGuiderMediator guiderMediator, IImageHistoryVM history, IProfileService profileService) : base() {
            this.guiderMediator = guiderMediator;
            this.history = history;
            this.profileService = profileService;
            AfterExposures = 1;
            TriggerRunner.Add(new Dither(guiderMediator, profileService));

            var assembly = this.GetType().Assembly;
            var id = assembly.GetCustomAttribute<GuidAttribute>().Value;
            this.pluginSettings = new PluginOptionsAccessor(profileService, Guid.Parse(id));
        }

        private SyncedDither(SyncedDither cloneMe) : this(cloneMe.guiderMediator, cloneMe.history, cloneMe.profileService) {
            CopyMetaData(cloneMe);
        }

        public override object Clone() {
            return new SyncedDither(this) {
                AfterExposures = AfterExposures,
                TriggerRunner = (SequentialContainer)TriggerRunner.Clone()
            };
        }

        private IList<string> issues = new List<string>();

        public IList<string> Issues {
            get => issues;
            set {
                issues = ImmutableList.CreateRange(value);
                RaisePropertyChanged();
            }
        }

        public override string ToString() {
            return $"Trigger: {nameof(SyncedDither)}, AfterExposures: {AfterExposures}";
        }

        public bool Validate() {
            var i = new List<string>();

            Issues = i;
            return i.Count == 0;
        }

        private int lastTriggerId = 0;
        private int afterExposures;

        [JsonProperty]
        public int AfterExposures {
            get => afterExposures;
            set {
                afterExposures = Math.Max(1, value);
                RaisePropertyChanged();
            }
        }

        public override void AfterParentChanged() {
            var root = ItemUtility.GetRootContainer(this.Parent);
            if (root?.Status == NINA.Core.Enum.SequenceEntityStatus.RUNNING) {
                Initialize();
            } else {
                Teardown();
            }
        }

        private ISyncServiceClient client {
            get => SyncServiceClient.Instance;
        }

        public override void Initialize() {
            try {
                client.RegisterSync(SyncSources.MountOp);
            } catch (Exception ex) {
                Logger.Error(ex);
            }
        }

        public override void Teardown() {
            try {
                client.UnregisterSync(SyncSources.MountOp);
            } catch (Exception ex) {
                Logger.Error(ex);
            }
        }

        private PluginOptionsAccessor pluginSettings;

        public int ProgressExposures {
            get => AfterExposures > 0 ? history.ImageHistory.Count % AfterExposures : 0;
        }

        public override async Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) {
            // Only the guiding (mount) instance performs the dither; every other instance holds via its
            // Synced Mount Check. A dither moves the shared mount, so it is just another planned mount operation.
            if (guiderMediator.GetInfo()?.Connected != true) { return; }
            if (AfterExposures <= 0) { return; }

            lastTriggerId = history.ImageHistory.Count;

            var rendezvous = TimeSpan.FromSeconds(pluginSettings.GetValueInt32(nameof(SyncServicePlugin.RendezvousTimeout), 600));
            var afTimeout = TimeSpan.FromSeconds(pluginSettings.GetValueInt32(nameof(SyncServicePlugin.AutofocusBusyWaitTimeout), 300));

            await SyncBarrier.RunMountOperation(client, "Dither", preemptAutofocus: false, rendezvous, afTimeout, async () => {
                progress?.Report(new ApplicationStatus() { Status = "Dithering" });
                await TriggerRunner.Run(progress, token);
            }, progress, token);
        }

        public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) {
            if (previousItem == null && nextItem == null) { return false; }
            if (AfterExposures <= 0) { return false; }
            // Only the guiding (mount) instance dithers; the others hold via their Synced Mount Check.
            if (guiderMediator.GetInfo()?.Connected != true) { return false; }
            // Don't stack a dither on top of another in-flight mount operation (flip / recenter).
            if (client.IsOperationPendingCached(SyncSources.MountOp)) { return false; }
            RaisePropertyChanged(nameof(ProgressExposures));
            if (lastTriggerId > history.ImageHistory.Count) {
                // The image history was most likely cleared
                lastTriggerId = 0;
            }
            var shouldTrigger = lastTriggerId < history.ImageHistory.Count && history.ImageHistory.Count > 0 && ProgressExposures == 0;

            if (shouldTrigger) {
                if (ItemUtility.IsTooCloseToMeridianFlip(Parent, TriggerRunner.GetItemsSnapshot().First().GetEstimatedDuration())) {
                    Logger.Warning("Dither should be triggered, however the meridian flip is too close to be executed");
                    shouldTrigger = false;
                }
            }

            return shouldTrigger;
        }
    }
}
