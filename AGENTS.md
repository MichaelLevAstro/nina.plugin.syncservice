# AGENTS.md

## Project Purpose

This repository contains the N.I.N.A. SyncService plugin. It is intended for multi-camera imaging rigs that run multiple N.I.N.A. instances on one mount and need those instances to coordinate sequencing milestones.

### Authority model (the core invariant)

Only the MAIN (mount) instance ever initiates mount motion. EVERY planned mount operation - dither, meridian
flip, center-after-drift - is the main leading the single `MountOp` rendezvous; every other instance only ever
*follows*, via its one `Synced Mount Check`. Because the single-threaded main is the only initiator, two mount
operations can never overlap, so no cross-instruction deadlock is possible by construction. Autofocus is the only
thing that pushes back on the main, and it does so with a flag (`Autofocus.Busy`), never a competing barrier.

Instruction display names carry the placement in a suffix: `(main)` = place on the mount instance only;
`(aux)` = place on every other instance; no suffix = place on every instance.

The Advanced Sequencer instructions:

- `Synced Dither (main)`: after N exposures the guider-connected main runs the shared dither as a `MountOp` - waits for any autofocus, pauses the others, dithers once, releases them. Other instances do NOT place this.
- `Synced Meridian Flip (main)` + `Synced Mount Check (aux)`: the main only flips after all other instances have arrived at their check; they hold until the flip completes. The check handles EVERY mount operation plus the reactive mount-busy hold.
- `Synced Center after Drift (main)`: on measured drift, waits for the others (via their `Synced Mount Check`) before recentering.
- `Synced Slew and Center (main)`: slews/centers while the others are held by their `Synced Mount Check` via the mount-busy flag (NOT the rendezvous - it can run before the others are imaging, e.g. at session start).
- `Synced Autofocus` (after exposures / time / HFR increase / temperature change / filter change): each instance runs its own autofocus, holds the main from moving the mount while focusing, and yields to a due meridian flip (which preempts it; it re-runs after the flip).
- `Synced Begin Imaging`: every instance waits here so they all start together; the main releases them.

Every (main) rendezvous operation funnels through `SyncBarrier.RunMountOperation`, the single helper that does the
autofocus wait-or-preempt, suppresses the reactive observer, raises the `MountOp` pending flag, runs the leader
rendezvous, and cleans up. These delegate to the matching built-in N.I.N.A. trigger/item (hosted as a private
field) and wrap them with the sync coordination, rather than re-implementing N.I.N.A.'s logic.

A reactive mount-movement observer remains as a safety net for UNEXPECTED moves only (manual slews etc.), suppressed while a planned synced mount operation runs, and toggleable in the options.

Only one instance hosts the synchronization server. The first N.I.N.A. process that loads the plugin starts a named-pipe gRPC server, and all instances communicate with that server. That host instance must stay alive for the whole synchronized acquisition.

## Repository Layout

- `SyncService.sln`: Visual Studio solution containing the plugin and service projects.
- `SyncService/`: main N.I.N.A. plugin assembly.
  - `SyncServicePlugin.cs`: plugin manifest/runtime entry point, server startup, status heartbeat, and plugin setting storage.
  - `Instructions/`: the Advanced Sequencer instructions. `SyncBarrier.cs` holds the shared leader/follower rendezvous helpers (incl. `RunMountOperation`). The (main) mount operations are `SyncedDither.cs`, `SyncedMeridianFlip.cs`, `SyncedCenterAfterDrift.cs`, `SyncedSlewAndCenter.cs`; the (aux) hold is `SyncedMountCheck.cs`; the shared instructions are `SyncedAutofocus.cs` and `SyncedBeginImaging.cs`.
  - `MountActivity/MountBusyObserver.cs`: the reactive observer for unexpected moves (uses the testable `SyncService.Service/MountActivityMonitor.cs` state machine).
  - `Options.xaml` and `Options.xaml.cs`: plugin settings UI, icons, and sequence-item templates.
  - `Properties/AssemblyInfo.cs`: N.I.N.A. plugin metadata, version, minimum N.I.N.A. version, tags, URLs, and long description.
  - `Changelog.md`: user-facing release history.
  - `SyncServiceLogo.jpg`, `SyncServiceSample.jpg`, `*.svg`, `*.ai`: plugin visuals and sequence icons.
- `SyncService.Service/`: shared named-pipe gRPC synchronization service.
  - `SyncService.proto`: gRPC service contract and generated-code source.
  - `SyncServiceServer.cs`: in-process coordination server used by the first plugin instance.
  - `SyncServiceClient.cs`: singleton client used by all plugin instructions/triggers.
  - `ISyncServiceClient.cs`: client abstraction used by instruction code.
- `bitbucket-pipelines.yml`: legacy publishing pipeline. It still references .NET 7 SDK/output paths while the project files target `net8.0-windows`; update it before relying on CI packaging.

## Runtime Architecture

The plugin uses `GrpcDotNetNamedPipes` on the pipe name `NINA.SyncService.Service.Sync`.

`SyncServicePlugin.Initialize()` calls `StartServerIfNotStarted()`. A global mutex based on the plugin identifier prevents multiple N.I.N.A. instances from starting competing servers. If the pipe does not already exist, the instance creates a `NamedPipeServer`, binds `SyncServiceServer.Instance`, and starts a server heartbeat that writes the current sync status through `IApplicationStatusMediator`.

The service tracks barrier state by `source` (see `SyncSources`: the single `MountOp` rendezvous for all planned mount operations, `StartImaging` for the start-together barrier, plus `Autofocus` for heartbeat keep-alive). Collapsing every mount operation onto one `MountOp` source is deliberate - it is what makes the authority model deadlock-free (one source, only the main leads, the aux only follow via their `Synced Mount Check`). Alongside the barrier it keeps generic per-(key,client) push flags (`SetFlag`/`ClearFlag`/`GetFlags`) used for `MountBusy` (reactive unexpected moves + flag-based slew), `MountOp.Pending` (rendezvous wake-up; the reason carries the operation kind for the status line), `Autofocus.Busy` (reverse hold) and `Autofocus.Preempt` (meridian-flip-only autofocus cancel) - each with a ttl so a crashed setter cannot strand others.

Each participating instance has a process-local GUID client id. Instructions register when their parent sequence context is running and unregister on teardown. The client sends a heartbeat once per second while registered. The server treats clients whose heartbeat timestamp is older than 30 seconds as inactive.

The normal synchronization flow is:

1. Register the client for a source.
2. Announce that this client reached the sync point and whether it can lead.
3. Wait until all active registered clients for that source have announced.
4. Elect a leader from announced clients where `canLead` is true.
5. The leader marks the sync in progress, performs the leader-only work, then marks it complete.
6. Non-leaders wait for completion.
7. Clients withdraw/unregister when canceled or torn down.

On the `MountOp` rendezvous the leader (the main, via `RunMountOperation` → `RunAsLeader`) announces `canLead = true`; every aux follows via `RunAsFollower` with `canLead = false`. Because the (main) instructions guard on telescope/guider connection, only the main ever executes one, so only the main is ever elected leader.

## Key Behavior To Preserve

- Preserve the authority model: only the main initiates a `MountOp`; the aux only follow via `Synced Mount Check`. Do NOT reintroduce per-operation barrier sources or let an aux independently enter a barrier - that is exactly what reintroduces the cross-instruction deadlock. New mount operations must go through `SyncBarrier.RunMountOperation` on the single `MountOp` source.
- Keep `MountOp` (mount operations) and `StartImaging` isolated by source name - they are different concerns.
- Keep registration balanced. Multiple instructions can register the same client/source, and the server uses a registration count before fully removing a client.
- Keep the heartbeat behavior aligned with registration. Heartbeats are how the server detects dead/inactive instances.
- Preserve cancellation handling. Leaders should mark the sync complete when canceling during leader work; waiting clients should withdraw so the server does not wait on stale participants.
- Preserve the guider-connected rule for dithering. Only the guider-connected (main) instance runs the actual dither; `Synced Dither (main)` does nothing elsewhere.
- A meridian flip PREEMPTS autofocus (`Autofocus.Preempt`); every other mount operation WAITS for autofocus (`WaitWhileAutofocusBusy`). Keep that split in `RunMountOperation`'s `preemptAutofocus` argument.
- Be careful with timeout semantics. `RendezvousTimeout` (default 600s) is the single max wait for all mount operations - leader and follower must use the same value to avoid one side giving up early.

## Build And Development

The projects target `net8.0-windows` and use WPF:

```powershell
dotnet restore .\SyncService.sln
dotnet build .\SyncService.sln -c Debug
```

The plugin depends on `NINA.Plugin` version `3.0.0.1056-nightly` and `Grpc.Tools` version `2.58.0`.

There is a NUnit test project (`NINA.Plugins.Test`) covering the server barrier, the generic flags (set/get/clear/ttl/multi-setter), and the planned-op gate via a `TestServerCallContext` harness. Run `dotnet test SyncService.sln`. The instructions themselves need manual verification in multiple N.I.N.A. instances (with an ASCOM telescope simulator on the mount instance). The important manual scenarios are:

- only one guider-connected instance leads `Synced Dither`; non-leaders wait and resume;
- the mount instance only runs a `Synced Meridian Flip` / `Synced Center after Drift` once all other instances have arrived at their `Synced Mount Check`, then they resume;
- `Synced Center and Slew` holds the other instances until the slew/center settles;
- a mount move waits for an in-progress `Synced Autofocus`, but a due meridian flip preempts the autofocus (which re-runs after the flip);
- an UNEXPECTED manual slew still pauses the others via the reactive observer (and not when its toggle is off);
- timeout/cancellation/crash never leaves an instance stranded (ttl + heartbeat + per-wait timeouts fail open).

The main plugin project has a post-build target that attempts to sign release builds and copy output files into `%localappdata%\NINA\Plugins\3.0.0\SyncService`. The target has `IgnoreExitCode="true"`, but still be aware of local deployment side effects when building on Windows.

## Packaging Notes

The deployed plugin needs the main plugin assembly plus service/runtime dependencies. The current post-build copy list includes:

- `SyncService.dll`
- `NINA.Plugins.SyncService.Service.dll`
- `Grpc.Core.Api.dll`
- `GrpcDotNetNamedPipes.dll`
- `Google.Protobuf.dll`

If dependency versions or target frameworks change, revisit both the post-build copy list and the publishing pipeline.

## Code Style Notes

- This codebase uses C# with explicit N.I.N.A. MEF exports and WPF resource dictionaries. Follow the existing pattern for new sequence items or triggers.
- Plugin settings are stored with `PluginOptionsAccessor` using the main plugin assembly GUID from `AssemblyInfo.cs`.
- The gRPC contract lives in `SyncService.proto`; update server, client, and generated call sites together when changing protocol behavior.
- Keep changes narrowly scoped. The synchronization protocol is stateful and shared across all N.I.N.A. instances, so small-looking changes can alter multi-instance behavior.
- Prefer logging through `NINA.Core.Utility.Logger`, matching the existing severity conventions.
