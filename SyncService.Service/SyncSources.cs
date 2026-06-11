namespace SyncService.Service {
    /// <summary>
    /// Shared wire-string constants for sync barrier sources and flag keys. Lives in the service project so
    /// both the client and the plugin instructions agree on the exact strings (the plugin references the
    /// service project, not the other way round). These strings must stay stable across a fleet running the
    /// same plugin version - they are independent of C# class names.
    ///
    /// Authority model: the MAIN (mount) instance is the ONLY initiator of mount motion. Every kind of
    /// planned mount operation - dither, meridian flip, center-after-drift - rendezvouses on the single
    /// <see cref="MountOp"/> source: the main leads, every other instance follows via its Synced Mount Check.
    /// Because only the (single-threaded) main starts a MountOp, two of them can never overlap, so no
    /// cross-instruction deadlock is possible. Autofocus is the one thing that pushes back on the main, and it
    /// does so with a flag (<see cref="AutofocusBusy"/>), never a competing barrier.
    /// </summary>
    public static class SyncSources {
        // ---- Barrier sources (used with WaitForSyncStart) ----

        /// <summary>The single rendezvous source for every planned mount operation (dither / meridian flip /
        /// center-after-drift). The main leads; every other instance follows via its Synced Mount Check.</summary>
        public const string MountOp = "MountOp";

        /// <summary>One-time "all instances start imaging together" rendezvous (Synced Begin Imaging).</summary>
        public const string StartImaging = "StartImaging";

        // Non-rendezvous keep-alive registration so an instance running only Synced Autofocus still
        // heartbeats (and thus keeps its flag caches fresh). Never used with WaitForSyncStart.
        public const string Autofocus = "Autofocus";

        // ---- Flag keys (push state, polled via the heartbeat cache, each with a ttl) ----

        /// <summary>Reactive safety net: the mount moved unexpectedly (manual slew, another plugin, a
        /// flag-based Synced Slew and Center). Aux hold best-effort until it clears.</summary>
        public const string MountBusy = "MountBusy";

        /// <summary>Reverse hold: an instance is autofocusing, so the main must not move the mount.</summary>
        public const string AutofocusBusy = "Autofocus.Busy";

        /// <summary>Set ONLY by the meridian flip (which is time-critical and cannot wait): tells every
        /// instance's autofocus to cancel now and re-run after the flip, instead of holding the flip.</summary>
        public const string AutofocusPreempt = "Autofocus.Preempt";

        public const string PendingSuffix = ".Pending";

        /// <summary>The "operation pending" flag key for a rendezvous source (wakes the aux check triggers).</summary>
        public static string PendingKey(string source) => source + PendingSuffix;
    }
}
