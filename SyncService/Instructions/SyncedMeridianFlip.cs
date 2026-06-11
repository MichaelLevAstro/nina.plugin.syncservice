using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Trigger.MeridianFlip;
using NINA.Sequencer.Utility;
using NINA.WPF.Base.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using SyncService.Service;
using System;
using System.ComponentModel.Composition;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SyncService.Instructions {

    [ExportMetadata("Name", "Synced Meridian Flip (main)")]
    [ExportMetadata("Description", "Place on the mount instance. When a meridian flip is due it pauses every other N.I.N.A. instance (interrupting their autofocus), waits until they have all arrived at their Synced Mount Check, runs the full N.I.N.A. flip (stop guiding, flip, recenter, resume guiding, optional autofocus, settle), then releases them. Does nothing on instances without a mount - put a Synced Mount Check on those.")]
    [ExportMetadata("Icon", "SyncMountSVG")]
    [ExportMetadata("Category", "SyncService")]
    [Export(typeof(ISequenceTrigger))]
    [JsonObject(MemberSerialization.OptIn)]
    public class SyncedMeridianFlip : SequenceTrigger {
        private readonly IProfileService profileService;
        private readonly ICameraMediator cameraMediator;
        private readonly ITelescopeMediator telescopeMediator;
        private readonly IFocuserMediator focuserMediator;
        private readonly IApplicationStatusMediator applicationStatusMediator;
        private readonly IMeridianFlipVMFactory meridianFlipVMFactory;
        private readonly PluginOptionsAccessor pluginSettings;
        private readonly MeridianFlipTrigger hosted;

        [ImportingConstructor]
        public SyncedMeridianFlip(IProfileService profileService, ICameraMediator cameraMediator, ITelescopeMediator telescopeMediator,
                                  IFocuserMediator focuserMediator, IApplicationStatusMediator applicationStatusMediator, IMeridianFlipVMFactory meridianFlipVMFactory) : base() {
            this.profileService = profileService;
            this.cameraMediator = cameraMediator;
            this.telescopeMediator = telescopeMediator;
            this.focuserMediator = focuserMediator;
            this.applicationStatusMediator = applicationStatusMediator;
            this.meridianFlipVMFactory = meridianFlipVMFactory;
            this.hosted = new MeridianFlipTrigger(profileService, cameraMediator, telescopeMediator, focuserMediator, applicationStatusMediator, meridianFlipVMFactory);

            var assembly = this.GetType().Assembly;
            var id = assembly.GetCustomAttribute<GuidAttribute>().Value;
            this.pluginSettings = new PluginOptionsAccessor(profileService, Guid.Parse(id));
        }

        public override object Clone() {
            var clone = new SyncedMeridianFlip(profileService, cameraMediator, telescopeMediator, focuserMediator, applicationStatusMediator, meridianFlipVMFactory);
            clone.CopyMetaData(this);
            return clone;
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
            var settle = pluginSettings.GetValueInt32(nameof(SyncServicePlugin.PostFlipSettleTime), 30);

            // A meridian flip is time-critical, so it PREEMPTS autofocus rather than waiting for it.
            await SyncBarrier.RunMountOperation(client, "Meridian flip", preemptAutofocus: true, rendezvous, TimeSpan.Zero, async () => {
                progress?.Report(new ApplicationStatus() { Status = "Performing meridian flip" });
                await hosted.Execute(context, progress, token);

                // Hold the other instances a bit longer so the guider has resumed and settled before they
                // resume imaging - the flip routine can return while the guider is still recovering.
                if (settle > 0) {
                    Logger.Info($"Meridian flip complete - holding {settle}s for the guider to settle before releasing the other instances");
                    progress?.Report(new ApplicationStatus() { Status = $"Waiting {settle}s for the guider to settle after the flip" });
                    await Task.Delay(TimeSpan.FromSeconds(settle), token);
                }
            }, progress, token);
        }

        public override string ToString() {
            return $"Trigger: {nameof(SyncedMeridianFlip)}";
        }
    }
}
