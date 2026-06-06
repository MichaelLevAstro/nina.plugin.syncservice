# AGENTS.md

## Project Purpose

This repository contains the N.I.N.A. SyncService plugin. It is intended for multi-camera imaging rigs that run multiple N.I.N.A. instances on one mount and need those instances to coordinate sequencing milestones.

The plugin adds explicit Advanced Sequencer instructions that coordinate planned operations across instances:

- `Synced Dither`: a guider trigger that waits until all active instances reach the dither point, elects one guider-connected leader, runs one real dither, and lets the others wait until it completes.
- `Synced Meridian Flip` (mount instance) + `Synced Mount Check` (every other instance): the mount instance only flips after all other instances have arrived at their check; they hold until the flip completes. The check trigger also handles the recenter rendezvous and the reactive mount-busy hold.
- `Synced Center after Drift` (mount instance): on measured drift, waits for the others (via their `Synced Mount Check`) before recentering.
- `Synced Center and Slew` (mount instance): slews/centers while the others are held by their `Synced Mount Check` via the mount-busy flag.
- `Synced Autofocus` (after exposures / time / HFR increase / temperature change / filter change): each instance runs its own autofocus, holds the mount instance from slewing while focusing, and yields to a due meridian flip (which preempts it; it re-runs after the flip).

These delegate to the matching built-in N.I.N.A. trigger/item (hosted as a private field) and wrap them with the sync coordination, rather than re-implementing N.I.N.A.'s logic.

A reactive mount-movement observer remains as a safety net for UNEXPECTED moves only (manual slews etc.), suppressed while a planned synced mount operation runs, and toggleable in the options.

Only one instance hosts the synchronization server. The first N.I.N.A. process that loads the plugin starts a named-pipe gRPC server, and all instances communicate with that server. That host instance must stay alive for the whole synchronized acquisition.

## Repository Layout

- `SyncService.sln`: Visual Studio solution containing the plugin and service projects.
- `SyncService/`: main N.I.N.A. plugin assembly.
  - `SyncServicePlugin.cs`: plugin manifest/runtime entry point, server startup, status heartbeat, and plugin setting storage.
  - `Instructions/SynchronizedDither.cs`: synchronized guider trigger implementation.
  - `Instructions/SynchronizedWait.cs`: synchronized utility instruction implementation.
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

The service tracks barrier state by `source` (see `SyncSources`: `Dither`, `MeridianFlip`, `CenterAfterDrift`, plus `MountGuard`/`Autofocus` for heartbeat keep-alive). Keeping separate source keys prevents one instruction type from unblocking another. Alongside the barrier it keeps generic per-(key,client) push flags (`SetFlag`/`ClearFlag`/`GetFlags`) used for `MountBusy`, `<source>.Pending` (rendezvous wake-up), and `Autofocus.Busy` (reverse hold) - each with a ttl so a crashed setter cannot strand others.

Each participating instance has a process-local GUID client id. Instructions register when their parent sequence context is running and unregister on teardown. The client sends a heartbeat once per second while registered. The server treats clients whose heartbeat timestamp is older than 30 seconds as inactive.

The normal synchronization flow is:

1. Register the client for a source.
2. Announce that this client reached the sync point and whether it can lead.
3. Wait until all active registered clients for that source have announced.
4. Elect a leader from announced clients where `canLead` is true.
5. The leader marks the sync in progress, performs the leader-only work, then marks it complete.
6. Non-leaders wait for completion.
7. Clients withdraw/unregister when canceled or torn down.

For `SynchronizedDither`, `canLead` is true only when `IGuiderMediator.GetInfo().Connected` is true. For `SynchronizedWait`, all instances announce `canLead = true` because the leader only marks the wait complete.

## Key Behavior To Preserve

- Do not let `SynchronizedDither` and `SynchronizedWait` share synchronization state. They must stay isolated by source name.
- Keep registration balanced. Multiple instructions can register the same client/source, and the server uses a registration count before fully removing a client.
- Keep the heartbeat behavior aligned with registration. Heartbeats are how the server detects dead/inactive instances.
- Preserve cancellation handling. Leaders should mark the sync complete when canceling during leader work; waiting clients should withdraw so the server does not wait on stale participants.
- Preserve the guider-connected leader rule for dithering. Only an instance connected to a guider should run the actual dither.
- Preserve the meridian-flip guard in `SynchronizedDither.ShouldTrigger()`.
- Be careful with timeout semantics. The user setting `DitherWaitTimeout` is a global maximum sync wait timeout in seconds and defaults to 300.

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
