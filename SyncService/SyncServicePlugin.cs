using GrpcDotNetNamedPipes;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using SyncService.MountActivity;
using SyncService.Service;
using System;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace SyncService {

    [Export(typeof(IPluginManifest))]
    public class SyncServicePlugin : PluginBase {

        [ImportingConstructor]
        public SyncServicePlugin(IProfileService profileService, IApplicationStatusMediator statusMediator, ITelescopeMediator telescopeMediator) {
            mutexid = $"Global\\{this.Identifier}";
            this.statusMediator = statusMediator;
            this.telescopeMediator = telescopeMediator;

            var assembly = this.GetType().Assembly;
            var id = assembly.GetCustomAttribute<GuidAttribute>().Value;
            this.pluginSettings = new PluginOptionsAccessor(profileService, Guid.Parse(id));
        }

        private NamedPipeServer pipe;
        private CancellationTokenSource cts;
        private CancellationTokenSource serviceStateCts;
        private bool isServer = false;

        private string mutexid;
        private IApplicationStatusMediator statusMediator;
        private ITelescopeMediator telescopeMediator;
        private MountBusyObserver mountObserver;
        private PluginOptionsAccessor pluginSettings;

        private async Task StartServerIfNotStarted() {
            var allowEveryoneRule = new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), MutexRights.FullControl, AccessControlType.Allow);
            var securitySettings = new MutexSecurity();
            securitySettings.AddAccessRule(allowEveryoneRule);

            // Ensure that only one server will be spawned when multiple application instances are started
            // Ref: https://docs.microsoft.com/en-us/dotnet/api/system.threading.mutex?redirectedfrom=MSDN&view=net-5.0
            using (var mutex = new Mutex(false, mutexid, out var createNew)) {
                var hasHandle = false;
                try {
                    try {
                        // Wait for 5 seconds to receive the mutex
                        hasHandle = mutex.WaitOne(5000, false);
                        if (hasHandle == false) {
                            throw new TimeoutException("Timeout waiting for exclusive access");
                        }

                        try {
                            var pipeName = "NINA.SyncService.Service.Sync";

                            if (!NamedPipeExist(pipeName)) {
                                var user = WindowsIdentity.GetCurrent().User;
                                var security = new PipeSecurity();
                                security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
                                security.SetOwner(user);
                                security.SetGroup(user);

                                pipe = new NamedPipeServer(pipeName, new NamedPipeServerOptions() { PipeSecurity = security });
                                NINA.SyncService.Service.Sync.SyncBus.BindService(pipe.ServiceBinder, SyncServiceServer.Instance);
                                pipe.Start();
                                isServer = true;
                                Logger.Info($"Started synchronization plugin server on pipe {pipeName}");
                            }
                        } catch (Exception ex) {
                            Logger.Error("Failed to start synchronization plugin server ", ex);
                        }
                    } catch (AbandonedMutexException) {
                        hasHandle = true;
                    }
                } finally {
                    if (hasHandle) {
                        mutex.ReleaseMutex();
                    }
                }
            }
        }

        public override async Task Initialize() {
            await StartServerIfNotStarted();

            if (isServer) {
                _ = StartServerHeartbeat();
            }

            mountObserver = new MountBusyObserver(
                telescopeMediator,
                SyncServiceClient.Instance,
                () => SyncServiceClient.Instance.ServiceActiveCached && ReactiveMountObserverEnabled,
                () => MountSettleTime,
                () => MountCoordinateThreshold);
            mountObserver.Start();

            // Always-on, lightweight: every instance polls the fleet-wide on/off so a Start/Stop anywhere
            // (button or instruction) is picked up here. While stopped this poll is the only activity.
            _ = StartServiceStateWatcher();
        }

        private Task StartServiceStateWatcher() {
            return Task.Run(async () => {
                using (serviceStateCts = new CancellationTokenSource()) {
                    var token = serviceStateCts.Token;
                    while (!token.IsCancellationRequested) {
                        try {
                            await Task.Delay(2000, token);
                            var active = await SyncServiceClient.Instance.RefreshServiceState(token);
                            if (active != IsServiceActive) { IsServiceActive = active; }
                        } catch (OperationCanceledException) {
                        } catch (Exception ex) {
                            // Server unreachable (e.g. host starting/restarting): keep the last known state.
                            Logger.Debug($"Sync service state poll failed: {ex.Message}");
                        }
                    }
                }
            });
        }

        private Task StartServerHeartbeat() {
            return Task.Run(async () => {
                var symbols = new string[] { "▖", "▘", "▝", "▗" };
                int roller = 0;
                using (cts = new CancellationTokenSource()) {
                    while (!cts.IsCancellationRequested) {
                        try {
                            await Task.Delay(1000, cts.Token);

                            // While the service is stopped show nothing - the plugin is meant to be idle.
                            if (!SyncServiceServer.Instance.ServiceActive) {
                                statusMediator.StatusUpdate(new NINA.Core.Model.ApplicationStatus() { Status = "", Source = "Sync Service" });
                                continue;
                            }

                            var status = SyncServiceServer.Instance.GetStatus();
                            statusMediator.StatusUpdate(new NINA.Core.Model.ApplicationStatus() { Status = $"{status} {symbols[roller++ % 4]}", Source = "Sync Service" });
                        } catch (OperationCanceledException) {
                            Logger.Info("Stopping server heartbeat");

                            statusMediator.StatusUpdate(new NINA.Core.Model.ApplicationStatus() { Status = "SyncService server shutting down", Source = "Sync Service" });
                        } catch (Exception ex) {
                            Logger.Error("An error occurred while pinging the server", ex);
                            statusMediator.StatusUpdate(new NINA.Core.Model.ApplicationStatus() { Status = "SyncService server encountered an error", Source = "Sync Service" });
                        }
                    }
                }
                statusMediator.StatusUpdate(new NINA.Core.Model.ApplicationStatus() { Status = "", Source = "Sync Service" });
            });
        }

        public override async Task Teardown() {
            try {
                serviceStateCts?.Cancel();
            } catch (Exception) {
            }

            // Dispose the observer first so it can send a final "mount idle" while the pipe is still alive.
            try {
                mountObserver?.Dispose();
            } catch (Exception ex) {
                Logger.Error("Failed to dispose mount observer", ex);
            }
            mountObserver = null;

            StopHeartbeat();

            try {
                if (pipe != null) {
                    Logger.Info("Shutting down server");
                    pipe.Kill();
                    pipe.Dispose();
                    pipe = null;
                }
            } catch (Exception ex) {
                Logger.Error("Failed to shutdown pipe", ex);
            }
        }

        private void StopHeartbeat() {
            try {
                cts?.Cancel();
            } catch (Exception) {
            }
        }

        [return: MarshalAs(UnmanagedType.Bool)]
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool WaitNamedPipe(string name, int timeout);

        public static bool NamedPipeExist(string pipeName) {
            try {
                int timeout = 0;
                string normalizedPath = System.IO.Path.GetFullPath(
                    string.Format(@"\\.\pipe\{0}", pipeName));
                bool exists = WaitNamedPipe(normalizedPath, timeout);
                if (!exists) {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 0) {
                        // pipe does not exist
                        return false;
                    } else if (error == 2) {
                        // win32 error code for file not found
                        return false;
                        // all other errors indicate other issues
                    }
                }
                return true;
            } catch (Exception) {
                // assume it doesn't exist
                return false;
            }
        }
        public bool MountSyncEnabled {
            get => pluginSettings.GetValueBoolean(nameof(MountSyncEnabled), true);
            set {
                pluginSettings.SetValueBoolean(nameof(MountSyncEnabled), value);
                RaisePropertyChanged();
            }
        }

        public int MountSettleTime {
            get => pluginSettings.GetValueInt32(nameof(MountSettleTime), 5);
            set {
                pluginSettings.SetValueInt32(nameof(MountSettleTime), Math.Max(0, value));
                RaisePropertyChanged();
            }
        }

        public int MountWaitTimeout {
            get => pluginSettings.GetValueInt32(nameof(MountWaitTimeout), 600);
            set {
                pluginSettings.SetValueInt32(nameof(MountWaitTimeout), Math.Max(1, value));
                RaisePropertyChanged();
            }
        }

        public double MountCoordinateThreshold {
            get => pluginSettings.GetValueDouble(nameof(MountCoordinateThreshold), 10d);
            set {
                pluginSettings.SetValueDouble(nameof(MountCoordinateThreshold), Math.Max(0d, value));
                RaisePropertyChanged();
            }
        }

        public bool ReactiveMountObserverEnabled {
            get => pluginSettings.GetValueBoolean(nameof(ReactiveMountObserverEnabled), true);
            set {
                pluginSettings.SetValueBoolean(nameof(ReactiveMountObserverEnabled), value);
                RaisePropertyChanged();
            }
        }

        public int RendezvousTimeout {
            get => pluginSettings.GetValueInt32(nameof(RendezvousTimeout), 600);
            set {
                pluginSettings.SetValueInt32(nameof(RendezvousTimeout), Math.Max(1, value));
                RaisePropertyChanged();
            }
        }

        public int AutofocusBusyWaitTimeout {
            get => pluginSettings.GetValueInt32(nameof(AutofocusBusyWaitTimeout), 300);
            set {
                pluginSettings.SetValueInt32(nameof(AutofocusBusyWaitTimeout), Math.Max(1, value));
                RaisePropertyChanged();
            }
        }

        public int StartImagingTimeout {
            get => pluginSettings.GetValueInt32(nameof(StartImagingTimeout), 1800);
            set {
                pluginSettings.SetValueInt32(nameof(StartImagingTimeout), Math.Max(1, value));
                RaisePropertyChanged();
            }
        }

        public int PostFlipSettleTime {
            get => pluginSettings.GetValueInt32(nameof(PostFlipSettleTime), 30);
            set {
                pluginSettings.SetValueInt32(nameof(PostFlipSettleTime), Math.Max(0, value));
                RaisePropertyChanged();
            }
        }
        private bool isServiceActive;
        public bool IsServiceActive {
            get => isServiceActive;
            private set {
                isServiceActive = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(CanStartService));
                RaisePropertyChanged(nameof(CanStopService));
                RaisePropertyChanged(nameof(ServiceStatusText));
            }
        }

        public bool CanStartService => !IsServiceActive;
        public bool CanStopService => IsServiceActive;
        public string ServiceStatusText => IsServiceActive ? "Running" : "Stopped";

        private ICommand startServiceCommand;
        public ICommand StartServiceCommand => startServiceCommand ??= new AsyncCommand<bool>(() => SetServiceStateAsync(true));

        private ICommand stopServiceCommand;
        public ICommand StopServiceCommand => stopServiceCommand ??= new AsyncCommand<bool>(() => SetServiceStateAsync(false));

        private async Task<bool> SetServiceStateAsync(bool active) {
            try {
                await SyncServiceClient.Instance.SetServiceState(active, CancellationToken.None);
                IsServiceActive = active;
                return true;
            } catch (Exception ex) {
                Logger.Error($"Failed to {(active ? "start" : "stop")} the sync service", ex);
                return false;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void RaisePropertyChanged([CallerMemberName] string propertyName = null) {
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /* static class Program {
         [STAThread]
         static void Main() {
             AsyncContext.Run(() => MainAsync());
         }

         static async void MainAsync() {
             new SyncServicePlugin();

             var client = DitherServiceClient.Instance;
             client.RegisterSync();

             await client.AnnounceToSync();

             await client.WaitForSync();

             var isLeader = await client.IsLeader();

             await client.SetDitherCompleted();

             client.UnregisterSync();
         }
     }*/
}