using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Trigger.Autofocus;
using NINA.Sequencer.Utility;
using NINA.WPF.Base.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;
using SyncService.Service;
using System;
using System.ComponentModel.Composition;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SyncService.Instructions {

    /// <summary>
    /// Base for the Synced Autofocus triggers. Each instance runs its OWN autofocus (delegated to the matching
    /// N.I.N.A. autofocus trigger). While focusing it raises the autofocus-busy flag so the mount instance will
    /// not slew/center. A due meridian flip preempts it (cancels the autofocus); because a cancelled autofocus
    /// never satisfies the underlying trigger condition, it re-fires - and runs to completion - after the flip.
    /// </summary>
    public abstract class SyncedAutofocusBase : SequenceTrigger {
        protected readonly IProfileService profileService;
        protected readonly IImageHistoryVM history;
        protected readonly ICameraMediator cameraMediator;
        protected readonly IFilterWheelMediator filterWheelMediator;
        protected readonly IFocuserMediator focuserMediator;
        protected readonly IAutoFocusVMFactory autoFocusVMFactory;
        protected readonly PluginOptionsAccessor pluginSettings;
        protected SequenceTrigger hosted;

        protected SyncedAutofocusBase(IProfileService profileService, IImageHistoryVM history, ICameraMediator cameraMediator,
                                      IFilterWheelMediator filterWheelMediator, IFocuserMediator focuserMediator, IAutoFocusVMFactory autoFocusVMFactory) : base() {
            this.profileService = profileService;
            this.history = history;
            this.cameraMediator = cameraMediator;
            this.filterWheelMediator = filterWheelMediator;
            this.focuserMediator = focuserMediator;
            this.autoFocusVMFactory = autoFocusVMFactory;
            this.hosted = CreateHosted();

            var assembly = this.GetType().Assembly;
            var id = assembly.GetCustomAttribute<GuidAttribute>().Value;
            this.pluginSettings = new PluginOptionsAccessor(profileService, Guid.Parse(id));
        }

        /// <summary>Create the N.I.N.A. autofocus trigger this variation delegates to.</summary>
        protected abstract SequenceTrigger CreateHosted();

        private ISyncServiceClient client => SyncServiceClient.Instance;

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
            // Keep-alive registration so this instance heartbeats (and FlipPreemptCached stays fresh) even if it
            // has no Synced Mount Check. SyncSources.Autofocus is never used in a rendezvous.
            try { client.RegisterSync(SyncSources.Autofocus); } catch (Exception ex) { Logger.Error(ex); }
            try { hosted.Initialize(); } catch (Exception ex) { Logger.Error(ex); }
        }

        public override void Teardown() {
            try { hosted.Teardown(); } catch (Exception ex) { Logger.Error(ex); }
            try { client.UnregisterSync(SyncSources.Autofocus); } catch (Exception ex) { Logger.Error(ex); }
        }

        public override void SequenceBlockInitialize() => hosted.SequenceBlockInitialize();
        public override void SequenceBlockTeardown() => hosted.SequenceBlockTeardown();

        public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) {
            // Don't start an autofocus if a meridian flip is imminent - it would just be preempted.
            if (client.IsOperationPendingCached(SyncSources.MeridianFlip)) { return false; }
            return hosted.ShouldTrigger(previousItem, nextItem);
        }

        public override async Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) {
            using var preemptCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var refreshCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task watchTask = null;
            Task refreshTask = null;
            try {
                await client.SetAutofocusBusy("Autofocus", token);
                watchTask = WatchForFlipPreempt(preemptCts);
                refreshTask = SyncBarrier.RefreshLoop(ct => client.SetAutofocusBusy("Autofocus", ct), 5000, refreshCts.Token);

                await hosted.Execute(context, progress, preemptCts.Token);
            } catch (OperationCanceledException) when (preemptCts.IsCancellationRequested && !token.IsCancellationRequested) {
                // Preempted by a meridian flip (not a user stop). Swallow without marking success so it re-runs
                // after the flip releases this instance.
                Logger.Info("Autofocus preempted by a meridian flip - it will re-run after the flip completes");
            } finally {
                preemptCts.Cancel();
                refreshCts.Cancel();
                if (watchTask != null) { try { await watchTask; } catch (Exception) { } }
                if (refreshTask != null) { try { await refreshTask; } catch (Exception) { } }
                try { await client.ClearAutofocusBusy(CancellationToken.None); } catch (Exception ex) { Logger.Error(ex); }
                progress?.Report(new ApplicationStatus() { Status = "" });
            }
        }

        private async Task WatchForFlipPreempt(CancellationTokenSource preemptCts) {
            try {
                while (!preemptCts.IsCancellationRequested) {
                    if (client.FlipPreemptCached) {
                        preemptCts.Cancel();
                        break;
                    }
                    await Task.Delay(200, preemptCts.Token);
                }
            } catch (OperationCanceledException) {
            }
        }
    }

    [ExportMetadata("Name", "Synced Autofocus after Exposures")]
    [ExportMetadata("Description", "Runs this instance's autofocus every N exposures. Holds the mount instance from slewing while focusing; a due meridian flip preempts and the autofocus re-runs after the flip.")]
    [ExportMetadata("Icon", "SyncAutofocusSVG")]
    [ExportMetadata("Category", "SyncService")]
    [Export(typeof(ISequenceTrigger))]
    [JsonObject(MemberSerialization.OptIn)]
    public class SyncedAutofocusAfterExposures : SyncedAutofocusBase {
        [ImportingConstructor]
        public SyncedAutofocusAfterExposures(IProfileService profileService, IImageHistoryVM history, ICameraMediator cameraMediator, IFilterWheelMediator filterWheelMediator, IFocuserMediator focuserMediator, IAutoFocusVMFactory autoFocusVMFactory)
            : base(profileService, history, cameraMediator, filterWheelMediator, focuserMediator, autoFocusVMFactory) { }

        protected override SequenceTrigger CreateHosted() => new AutofocusAfterExposures(profileService, history, cameraMediator, filterWheelMediator, focuserMediator, autoFocusVMFactory);
        private AutofocusAfterExposures H => (AutofocusAfterExposures)hosted;

        [JsonProperty]
        public int AfterExposures { get => H.AfterExposures; set { H.AfterExposures = value; RaisePropertyChanged(); } }
        public int ProgressExposures => H.ProgressExposures;

        public override object Clone() {
            var clone = new SyncedAutofocusAfterExposures(profileService, history, cameraMediator, filterWheelMediator, focuserMediator, autoFocusVMFactory);
            clone.CopyMetaData(this);
            clone.hosted = (SequenceTrigger)hosted.Clone();
            return clone;
        }

        public override string ToString() => $"Trigger: {nameof(SyncedAutofocusAfterExposures)}, AfterExposures: {AfterExposures}";
    }

    [ExportMetadata("Name", "Synced Autofocus after Time")]
    [ExportMetadata("Description", "Runs this instance's autofocus after an elapsed time. Holds the mount instance from slewing while focusing; a due meridian flip preempts and the autofocus re-runs after the flip.")]
    [ExportMetadata("Icon", "SyncAutofocusSVG")]
    [ExportMetadata("Category", "SyncService")]
    [Export(typeof(ISequenceTrigger))]
    [JsonObject(MemberSerialization.OptIn)]
    public class SyncedAutofocusAfterTime : SyncedAutofocusBase {
        [ImportingConstructor]
        public SyncedAutofocusAfterTime(IProfileService profileService, IImageHistoryVM history, ICameraMediator cameraMediator, IFilterWheelMediator filterWheelMediator, IFocuserMediator focuserMediator, IAutoFocusVMFactory autoFocusVMFactory)
            : base(profileService, history, cameraMediator, filterWheelMediator, focuserMediator, autoFocusVMFactory) { }

        protected override SequenceTrigger CreateHosted() => new AutofocusAfterTimeTrigger(profileService, history, cameraMediator, filterWheelMediator, focuserMediator, autoFocusVMFactory);
        private AutofocusAfterTimeTrigger H => (AutofocusAfterTimeTrigger)hosted;

        [JsonProperty]
        public double Amount { get => H.Amount; set { H.Amount = value; RaisePropertyChanged(); } }

        public override object Clone() {
            var clone = new SyncedAutofocusAfterTime(profileService, history, cameraMediator, filterWheelMediator, focuserMediator, autoFocusVMFactory);
            clone.CopyMetaData(this);
            clone.hosted = (SequenceTrigger)hosted.Clone();
            return clone;
        }

        public override string ToString() => $"Trigger: {nameof(SyncedAutofocusAfterTime)}, Minutes: {Amount}";
    }

    [ExportMetadata("Name", "Synced Autofocus on HFR Increase")]
    [ExportMetadata("Description", "Runs this instance's autofocus when HFR rises by a configured percentage. Holds the mount instance from slewing while focusing; a due meridian flip preempts and the autofocus re-runs after the flip.")]
    [ExportMetadata("Icon", "SyncAutofocusSVG")]
    [ExportMetadata("Category", "SyncService")]
    [Export(typeof(ISequenceTrigger))]
    [JsonObject(MemberSerialization.OptIn)]
    public class SyncedAutofocusAfterHFRIncrease : SyncedAutofocusBase {
        [ImportingConstructor]
        public SyncedAutofocusAfterHFRIncrease(IProfileService profileService, IImageHistoryVM history, ICameraMediator cameraMediator, IFilterWheelMediator filterWheelMediator, IFocuserMediator focuserMediator, IAutoFocusVMFactory autoFocusVMFactory)
            : base(profileService, history, cameraMediator, filterWheelMediator, focuserMediator, autoFocusVMFactory) { }

        protected override SequenceTrigger CreateHosted() => new AutofocusAfterHFRIncreaseTrigger(profileService, history, cameraMediator, filterWheelMediator, focuserMediator, autoFocusVMFactory);
        private AutofocusAfterHFRIncreaseTrigger H => (AutofocusAfterHFRIncreaseTrigger)hosted;

        [JsonProperty]
        public double Amount { get => H.Amount; set { H.Amount = value; RaisePropertyChanged(); } }
        [JsonProperty]
        public int SampleSize { get => H.SampleSize; set { H.SampleSize = value; RaisePropertyChanged(); } }

        public override object Clone() {
            var clone = new SyncedAutofocusAfterHFRIncrease(profileService, history, cameraMediator, filterWheelMediator, focuserMediator, autoFocusVMFactory);
            clone.CopyMetaData(this);
            clone.hosted = (SequenceTrigger)hosted.Clone();
            return clone;
        }

        public override string ToString() => $"Trigger: {nameof(SyncedAutofocusAfterHFRIncrease)}, Amount: {Amount}%";
    }

    [ExportMetadata("Name", "Synced Autofocus after Temperature Change")]
    [ExportMetadata("Description", "Runs this instance's autofocus after the temperature changes by a configured amount. Holds the mount instance from slewing while focusing; a due meridian flip preempts and the autofocus re-runs after the flip.")]
    [ExportMetadata("Icon", "SyncAutofocusSVG")]
    [ExportMetadata("Category", "SyncService")]
    [Export(typeof(ISequenceTrigger))]
    [JsonObject(MemberSerialization.OptIn)]
    public class SyncedAutofocusAfterTemperatureChange : SyncedAutofocusBase {
        [ImportingConstructor]
        public SyncedAutofocusAfterTemperatureChange(IProfileService profileService, IImageHistoryVM history, ICameraMediator cameraMediator, IFilterWheelMediator filterWheelMediator, IFocuserMediator focuserMediator, IAutoFocusVMFactory autoFocusVMFactory)
            : base(profileService, history, cameraMediator, filterWheelMediator, focuserMediator, autoFocusVMFactory) { }

        protected override SequenceTrigger CreateHosted() => new AutofocusAfterTemperatureChangeTrigger(profileService, history, cameraMediator, filterWheelMediator, focuserMediator, autoFocusVMFactory);
        private AutofocusAfterTemperatureChangeTrigger H => (AutofocusAfterTemperatureChangeTrigger)hosted;

        [JsonProperty]
        public double Amount { get => H.Amount; set { H.Amount = value; RaisePropertyChanged(); } }

        public override object Clone() {
            var clone = new SyncedAutofocusAfterTemperatureChange(profileService, history, cameraMediator, filterWheelMediator, focuserMediator, autoFocusVMFactory);
            clone.CopyMetaData(this);
            clone.hosted = (SequenceTrigger)hosted.Clone();
            return clone;
        }

        public override string ToString() => $"Trigger: {nameof(SyncedAutofocusAfterTemperatureChange)}, DeltaT: {Amount}";
    }

    [ExportMetadata("Name", "Synced Autofocus after Filter Change")]
    [ExportMetadata("Description", "Runs this instance's autofocus when the filter changes. Holds the mount instance from slewing while focusing; a due meridian flip preempts and the autofocus re-runs after the flip.")]
    [ExportMetadata("Icon", "SyncAutofocusSVG")]
    [ExportMetadata("Category", "SyncService")]
    [Export(typeof(ISequenceTrigger))]
    [JsonObject(MemberSerialization.OptIn)]
    public class SyncedAutofocusAfterFilterChange : SyncedAutofocusBase {
        [ImportingConstructor]
        public SyncedAutofocusAfterFilterChange(IProfileService profileService, IImageHistoryVM history, ICameraMediator cameraMediator, IFilterWheelMediator filterWheelMediator, IFocuserMediator focuserMediator, IAutoFocusVMFactory autoFocusVMFactory)
            : base(profileService, history, cameraMediator, filterWheelMediator, focuserMediator, autoFocusVMFactory) { }

        protected override SequenceTrigger CreateHosted() => new AutofocusAfterFilterChange(profileService, history, cameraMediator, filterWheelMediator, focuserMediator, autoFocusVMFactory);

        public override object Clone() {
            var clone = new SyncedAutofocusAfterFilterChange(profileService, history, cameraMediator, filterWheelMediator, focuserMediator, autoFocusVMFactory);
            clone.CopyMetaData(this);
            clone.hosted = (SequenceTrigger)hosted.Clone();
            return clone;
        }

        public override string ToString() => $"Trigger: {nameof(SyncedAutofocusAfterFilterChange)}";
    }
}
