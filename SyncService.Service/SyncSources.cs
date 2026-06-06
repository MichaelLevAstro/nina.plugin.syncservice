namespace SyncService.Service {
    /// <summary>
    /// Shared wire-string constants for sync barrier sources and flag keys. Lives in the service project so
    /// both the client and the plugin instructions agree on the exact strings (the plugin references the
    /// service project, not the other way round). These strings must stay stable across a fleet running the
    /// same plugin version - they are independent of C# class names.
    /// </summary>
    public static class SyncSources {
        // Barrier sources
        public const string Dither = "Dither";
        public const string MeridianFlip = "MeridianFlip";
        public const string CenterAfterDrift = "CenterAfterDrift";
        public const string StartImaging = "StartImaging";
        public const string MountGuard = "MountGuard";
        // Non-rendezvous keep-alive registration so an instance running only Synced Autofocus still
        // heartbeats (and thus keeps FlipPreemptCached fresh). Never used with WaitForSyncStart.
        public const string Autofocus = "Autofocus";

        // Flag keys
        public const string MountBusy = "MountBusy";
        public const string AutofocusBusy = "Autofocus.Busy";
        public const string PendingSuffix = ".Pending";

        /// <summary>The "operation pending" flag key for a rendezvous source (wakes the aux check triggers).</summary>
        public static string PendingKey(string source) => source + PendingSuffix;
    }
}
