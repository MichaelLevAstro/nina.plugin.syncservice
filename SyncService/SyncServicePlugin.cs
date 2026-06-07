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
                () => ReactiveMountObserverEnabled,
                () => MountSettleTime,
                () => MountCoordinateThreshold);
            mountObserver.Start();
        }

        private Task StartServerHeartbeat() {
            return Task.Run(async () => {
                var symbols = new string[] { "▖", "▘", "▝", "▗" };
                int roller = 0;
                using (cts = new CancellationTokenSource()) {
                    while (!cts.IsCancellationRequested) {
                        try {
                            await Task.Delay(1000, cts.Token);
                            var status = SyncServiceServer.Instance.GetStatus();

                            // Always animate the spinner so the status line never freezes on a static "Idle".
                            bool showActivitySymbols = true;

                            statusMediator.StatusUpdate(new NINA.Core.Model.ApplicationStatus() { Status = $"{status} {(showActivitySymbols ? symbols[roller++ % 4] : string.Empty)}", Source = "Sync Service" });
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
        public int DitherWaitTimeout {
            get => pluginSettings.GetValueInt32(nameof(DitherWaitTimeout), 300);
            set {
                pluginSettings.SetValueInt32(nameof(DitherWaitTimeout), value);
                RaisePropertyChanged();
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