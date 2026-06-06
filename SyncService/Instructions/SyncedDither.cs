using Grpc.Core;
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

    [ExportMetadata("Name", "Synced Dither")]
    [ExportMetadata("Description", "An instruction to coordinate a dither between multiple instances of N.I.N.A. - each instance needs to place this trigger into its sequence.")]
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
                client.RegisterSync(SyncSources.Dither);
            } catch (Exception ex) {
                Logger.Error(ex);
            }
        }

        public override void Teardown() {
            try {
                client.UnregisterSync(SyncSources.Dither);
            } catch (Exception ex) {
                Logger.Error(ex);
            }
        }

        private PluginOptionsAccessor pluginSettings;

        public int ProgressExposures {
            get => AfterExposures > 0 ? history.ImageHistory.Count % AfterExposures : 0;
        }

        public override async Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) {
            var source = SyncSources.Dither;
            var announced = false;
            var syncCompleted = false;

            try {
                if (AfterExposures > 0) {
                    var waitTimeout = TimeSpan.FromSeconds(pluginSettings.GetValueInt32(nameof(SyncServicePlugin.DitherWaitTimeout), 300));
                    lastTriggerId = history.ImageHistory.Count;
                    Logger.Info("Waiting for synchronization");
                    progress?.Report(new ApplicationStatus() { Status = "Waiting for synchronization" });
                    var info = guiderMediator.GetInfo();
                    announced = true;
                    await client.AnnounceToSync(source, info.Connected, token);
                    var isLeader = await client.WaitForSyncStart(source, token, waitTimeout);

                    Logger.Info("All Synchronized");
                    progress?.Report(new ApplicationStatus() { Status = "All Synchronized" });
                    if (isLeader) {
                        try {
                            Logger.Info("This instance leads the dither");
                            await client.SetSyncInProgress(source, token);
                            progress?.Report(new ApplicationStatus() { Status = "This instance leads the dither" });
                            await TriggerRunner.Run(progress, token);
                            Logger.Info("Marking dither as complete");
                            await client.SetSyncComplete(source, token);
                            syncCompleted = true;
                        } catch (RpcException e) {
                            if (e.StatusCode == StatusCode.Cancelled) {
                                Logger.Info("The dither was cancelled - marking dither as complete");
                                await client.SetSyncComplete(source, CancellationToken.None);
                                syncCompleted = true;
                                throw new OperationCanceledException("The synchronized dither was cancelled", e, token);
                            }

                            throw;
                        } catch (OperationCanceledException) {
                            Logger.Info("The dither was cancelled - marking dither as complete");
                            await client.SetSyncComplete(source, CancellationToken.None);
                            syncCompleted = true;
                            throw;
                        }

                        progress?.Report(new ApplicationStatus() { Status = "Dither is complete" });
                    } else {
                        Logger.Info("Waiting for leader to dither");
                        progress?.Report(new ApplicationStatus() { Status = "Waiting for leader to dither" });
                        await client.WaitForSyncComplete(source, token, waitTimeout);
                        syncCompleted = true;
                    }
                } else {
                    return;
                }
            } catch (RpcException e) when (e.StatusCode == StatusCode.Cancelled) {
                Logger.Info("The dither was cancelled");
                throw new OperationCanceledException("The synchronized dither was cancelled", e, token);
            } catch (OperationCanceledException) {
                Logger.Info("The dither was cancelled");
                throw;
            } finally {
                if (announced && !syncCompleted) {
                    try {
                        await client.WithdrawFromSync(source, CancellationToken.None);
                    } catch (Exception ex) {
                        Logger.Error("Failed to withdraw from synchronized dither", ex);
                    }
                }
                progress?.Report(new ApplicationStatus() { Status = "" });
            }
        }

        public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) {
            if (previousItem == null && nextItem == null) { return false; }
            if (AfterExposures <= 0) { return false; }
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
