# SyncService

A [N.I.N.A.](https://nighttime-imaging.eu/) plugin for **multi-camera rigs that run several N.I.N.A. instances on one shared mount**. It keeps those instances coordinated so they don't ruin each other's frames: only one instance drives the mount, and the others pause at the right moments (slews, centering, meridian flips) and start/dither/autofocus in a controlled way.

It works by running a small named-pipe server inside the first N.I.N.A. instance that starts; every instance talks to that server. Coordination is added to your sequences through a set of **Advanced Sequencer** instructions (all grouped under a **SyncService** category in the add menus).

---

## Concepts

- **Mount instance** – the single instance connected to the telescope. It performs all mount moves (slew, center, flip).
- **Other instances** – every instance *not* connected to the mount. They image through the same optics/mount and must hold still while the mount moves.
- **Server host** – the first N.I.N.A. instance to start hosts the coordination server. **It must stay running** for the whole session.

The plugin auto-detects each instance's role from whether a telescope is connected — no per-instance configuration of "who is the mount" is needed.

---

## Features

All instructions appear under **SyncService** in the "add instruction / add trigger" menus.

### Place on the mount instance
| Instruction | Type | What it does |
|---|---|---|
| **Synced Meridian Flip** | trigger | When a flip is due, pauses every other instance, waits until they have all stopped, runs N.I.N.A.'s full flip (stop guiding → flip → recenter → resume guiding → optional AF → settle), then releases them. |
| **Synced Center after Drift** | trigger | When measured drift exceeds your threshold, pauses the others, recenters, then releases them. Shows the live *last drift / max* readout. |
| **Synced Slew and Center** | instruction | Slews to the target and plate-solve centers while the others are held until it settles. |
| **Synced Begin Imaging** | instruction | Place it where the mount instance is ready to image (after slewing, centering, guiding). Releases everyone waiting at *Synced Start Imaging* so all instances start together. |

### Place on every other (non-mount) instance
| Instruction | Type | What it does |
|---|---|---|
| **Synced Mount Check** | trigger | Place once around the exposures. It pauses this instance whenever the mount instance runs a flip / recenter, and as a safety net for unexpected mount moves. **Required** — it's how the mount instance knows the others have stopped. The current exposure finishes first, then it holds. |
| **Synced Start Imaging** | instruction | Place at the start of imaging. Waits here until the mount instance reaches *Synced Begin Imaging*, so all instances start together. |

### Place on any instance
| Instruction | Type | What it does |
|---|---|---|
| **Synced Dither** | trigger | Coordinates a dither across all instances. One guider-connected instance leads the real dither; the rest wait for it to finish. |
| **Synced Autofocus** (after Exposures / Time / HFR Increase / Temperature Change / Filter Change) | trigger | Each instance runs its own autofocus. While focusing it prevents the mount instance from slewing. A due meridian flip interrupts the autofocus and it re-runs automatically after the flip. |

### Automatic safety net
Even without the instructions above, the mount instance watches the telescope and, if it moves **unexpectedly** (a manual slew, another plugin, ...), it pauses the other instances until it settles. Toggle it off in the options if you don't want it.

Everything **fails open**: crashes, lost connections and timeouts always let a waiting instance resume rather than hang all night.

---

## Installation

The plugin is the same on every machine; on a single PC running multiple N.I.N.A. instances, **one install in the plugins folder serves all of them**.

### Option A — copy the prebuilt files (simplest)
1. Get the `SyncService` folder (or the `SyncService-x.y.z.z.zip`) — the 5 files: `SyncService.dll`, `NINA.Plugins.SyncService.Service.dll`, `Grpc.Core.Api.dll`, `GrpcDotNetNamedPipes.dll`, `Google.Protobuf.dll`.
2. Close N.I.N.A.
3. Copy those files into:
   ```
   %LOCALAPPDATA%\NINA\Plugins\3.0.0\SyncService
   ```
   (create the `SyncService` folder if it doesn't exist).
4. Start N.I.N.A. → **Plugins** → confirm **SyncService** is listed and enabled.
5. Repeat on every PC that runs an instance (not needed per-instance on the same PC).

### Option B — build from source
Requires the .NET 8 SDK.

From a terminal in the repo root:
```powershell
# builds Release, packages dist\SyncService + a zip, and deploys to %LOCALAPPDATA%\NINA\Plugins\3.0.0\SyncService
powershell -ExecutionPolicy Bypass -File build.ps1 1.3.1.0
```
or from `cmd`:
```bat
build.bat 1.3.1.0
```
Options: `-SkipDeploy` (build + package only), `-Configuration Debug`, `-NinaPluginsRoot <path>`.
The portable copy lands in `dist\SyncService\` (+ `dist\SyncService-1.3.1.0.zip`) for moving to another PC.

---

## Quick start

A typical layout once installed:

**Mount instance** sequence:
1. Connect equipment, cool camera, etc.
2. Slew / Center / start guiding on the target.
3. **Synced Begin Imaging**  ← releases the others.
4. Imaging loop, with triggers as needed: **Synced Dither**, **Synced Meridian Flip**, **Synced Center after Drift**, **Synced Autofocus…**.

**Every other instance** sequence:
1. Connect equipment, cool camera, etc.
2. **Synced Start Imaging**  ← waits for the mount instance.
3. Imaging loop, with **Synced Mount Check** around the exposures (plus **Synced Dither** / **Synced Autofocus** if you use them).

Start all instances' sequences at roughly the same time. The others wait at *Synced Start Imaging* while the mount instance does its setup; when it reaches *Synced Begin Imaging* they all begin together. Thereafter, mount moves on the mount instance automatically pause the others via their *Synced Mount Check*.

### Prerequisites
- Only **one** instance connected to the mount.
- For **Synced Dither**, at least one instance connected to a guider.

---

## Settings

**Tools → Options → Plugins → SyncService**:

| Setting | Purpose |
|---|---|
| Synchronization Max. Wait Timeout | Max wait at a dither rendezvous. |
| Pause imaging while the mount moves | Master on/off for mount-move pausing. |
| Mount Settle Time | Quiet time after a move before the others resume. |
| Mount Max. Wait Timeout | Max hold while the mount is busy. |
| Mount Move Threshold | Coordinate change (arcsec) that counts as a move. |
| Pause aux on unexpected mount moves | The automatic safety net for unplanned moves. |
| Flip / Drift Rendezvous Timeout | Max the mount instance waits for the others before a flip/recenter (set above your longest sub-exposure). |
| Autofocus Wait Timeout | Max the mount instance waits for an autofocus to finish before moving. |
| Start Imaging Timeout | Max wait at *Synced Start Imaging*. |

---

## Notes & limitations

- When a mount move starts, an in-flight exposure on another instance is **allowed to finish** before that instance holds (N.I.N.A. has no safe way for a plugin to abort a running exposure mid-frame) — so expect at most one discardable frame per move.
- The server-hosting instance (first to start) must remain running for the session.
- Built against N.I.N.A. `3.0.0.1056`; tested on `3.1.x`.

## License

MPL-2.0.
