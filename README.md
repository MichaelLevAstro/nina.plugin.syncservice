# SyncService

A [N.I.N.A.](https://nighttime-imaging.eu/) plugin for **multi-camera rigs that run several N.I.N.A. instances on one shared mount**. It keeps those instances coordinated so they don't ruin each other's frames: only one instance drives the mount, and the others pause at the right moments (slews, centering, meridian flips, dithers) and start/autofocus in a controlled way.

It works by running a small named-pipe server inside the first N.I.N.A. instance that starts; every instance talks to that server. Coordination is added to your sequences through a set of **Advanced Sequencer** instructions (all grouped under a **SyncService** category in the add menus).

---

## How it works — one simple rule

> **Only the main instance ever moves the mount. Every other instance just *holds* while it does. The one thing that pushes back on the main is autofocus — and it does so without ever competing for control.**

That gives you two clear roles:

- **Main instance** — the one connected to the telescope (and guider). It performs **every** mount operation: slew, center, meridian flip, center-after-drift, and the dither. Each of these pauses the other instances, runs, then releases them.
- **Other ("aux") instances** — image through the same mount. They never touch the mount; a single **Synced Mount Check** holds them whenever the main is doing *anything* to the mount.

Because the (single-threaded) main is the only initiator, two mount operations can never overlap, so the instances can never deadlock waiting on each other. Autofocus runs per-instance and simply makes the main *wait* before it moves (a meridian flip is the one exception — it's time-critical, so it interrupts autofocus, which then re-runs after the flip).

The plugin auto-detects each instance's role from whether a telescope is connected — no "who is the mount" configuration needed. The first N.I.N.A. instance to start also hosts the coordination server; **it must stay running** for the whole session.

---

## Starting and stopping the service

The synchronization service is **off by default** — on N.I.N.A. launch nothing runs (no status spinner, no mount watching). You turn it on with either:

- **Start / Stop buttons** in the plugin options (*Tools → Options → Plugins → SyncService*), or
- the **Start Sync Service** / **Stop Sync Service** sequencer instructions.

Start/Stop is **fleet-wide**: starting or stopping on any one instance is picked up by every instance sharing the server, so you control the whole rig from one place.

While the service is **stopped**, every Synced instruction simply **passes through**: it performs its own underlying action (slew/center, dither, flip, recenter, autofocus) but does **not** hold the other instances, and the aux *Synced Mount Check* never pauses. Start the service to get the coordinated behaviour. The simplest setup is a single **Start Sync Service** at the very start of your sequence (on any one instance), or just click **Start** in the options at the beginning of the session.

---

## Naming convention

Each instruction's name tells you where to place it:

| Suffix | Place it on | Meaning |
|---|---|---|
| **(main)** | the mount instance only | It disturbs the mount. Does nothing on instances without a mount. |
| **(aux)** | every instance *except* the mount | It holds this instance while the main disturbs the mount. Does nothing on the mount instance. |
| *(no suffix)* | every instance | Per-instance, but fleet-aware (autofocus, start-together). |

---

## Instructions

All appear under **SyncService** in the "add instruction / add trigger" menus.

### Place on the mount instance — **(main)**
| Instruction | Type | What it does |
|---|---|---|
| **Synced Meridian Flip (main)** | trigger | When a flip is due, interrupts the other instances' autofocus, waits until they have all stopped, runs N.I.N.A.'s full flip (stop guiding → flip → recenter → resume guiding → optional AF → settle), then releases them. |
| **Synced Center after Drift (main)** | trigger | When measured drift exceeds your threshold, waits for any autofocus to finish, pauses the others, recenters, then releases them. Shows the live *last drift / max* readout. |
| **Synced Dither (main)** | trigger | After N exposures, waits for any autofocus to finish, pauses the others, performs one real dither of the shared mount, then releases them. |
| **Synced Slew and Center (main)** | instruction | Waits for any autofocus to finish, then slews and plate-solve centers while the others are held until the mount settles. Safe to run **before** the others are imaging (e.g. at session start). |
| **Synced Slew, Center and Rotate (main)** | instruction | Same as above, but also rotates to the target position angle. Waits for any autofocus to finish, then slews, plate-solve centers and rotates while the others are held until the mount settles. Safe to run **before** the others are imaging (e.g. at session start). |

### Place on every other instance — **(aux)**
| Instruction | Type | What it does |
|---|---|---|
| **Synced Mount Check (aux)** | trigger | Place once around the exposures. It pauses this instance whenever the main runs **any** mount operation — dither, flip, recenter — and, as a safety net, whenever the mount moves unexpectedly. **Required** — it's how the main knows the others have stopped. The current exposure finishes first, then it holds. |

### Place on every instance — *(no suffix)*
| Instruction | Type | What it does |
|---|---|---|
| **Synced Begin Imaging** | instruction | Place at each instance's ready-to-image point — on the main, after slewing / centering / guiding. The main releases the others, which wait here, so all start imaging together. |
| **Synced Autofocus** (after Exposures / Time / HFR Increase / Temperature Change / Filter Change) | trigger | Each instance runs its own autofocus. While focusing it blocks the main from moving the mount. A due meridian flip interrupts the autofocus and it re-runs automatically after the flip. |

### Place on any one instance — control
| Instruction | Type | What it does |
|---|---|---|
| **Start Sync Service** | instruction | Turns the synchronization service **on** across all instances. Until it runs (or you click Start in the options) every Synced instruction passes through uncoordinated. |
| **Stop Sync Service** | instruction | Turns the service **off** across all instances — coordination ends, the status spinner and mount watching go idle. |

### Automatic safety net
Even without the instructions above, the main watches the telescope and, if it moves **unexpectedly** (a manual slew, another plugin, ...), it pauses the other instances until it settles. Toggle it off in the options if you don't want it.

Everything **fails open**: crashes, lost connections and timeouts always let a waiting instance resume rather than hang all night.

---

## Quick start

A typical layout once installed:

**Main instance** (connected to the mount + guider):
1. Connect equipment, cool camera, etc.
2. **Start Sync Service** (or click *Start* in the options) → turns coordination on for the whole rig.
3. **Synced Slew and Center (main)** (or a normal slew/center) → start guiding on the target.
4. **Synced Begin Imaging**  ← releases the others.
5. Imaging loop, with triggers as needed: **Synced Dither (main)**, **Synced Meridian Flip (main)**, **Synced Center after Drift (main)**, **Synced Autofocus…**.

**Every other instance**:
1. Connect equipment, cool camera, etc.
2. **Synced Begin Imaging**  ← waits for the main.
3. Imaging loop, with **Synced Mount Check (aux)** around the exposures (plus **Synced Autofocus…** if you use it).

Start all instances' sequences at roughly the same time. The others wait at *Synced Begin Imaging* while the main does its setup; when the main reaches its own *Synced Begin Imaging* they all begin together. Thereafter, every mount operation on the main automatically pauses the others via their *Synced Mount Check*.

> **Remember to start the service.** It is off by default, so place a single **Start Sync Service** at the top of one sequence (or click *Start* in the options) before imaging — otherwise the Synced instructions run uncoordinated.

> **The dither lives only on the main now.** The shared mount is dithered once, by the guiding (main) instance; the other instances simply hold during it via their Mount Check — they do **not** need their own Synced Dither. (See *Migration* below if you are upgrading.)

### Prerequisites
- Only **one** instance connected to the mount.
- For **Synced Dither**, that main instance must also be connected to a guider.

---

## Installation

The plugin is the same on every machine; on a single PC running multiple N.I.N.A. instances, **one install in the plugins folder serves all of them**.

### Option A — copy the prebuilt files (simplest)
1. Grab the prebuilt `dist/SyncService` folder from this repo — the 5 files: `SyncService.dll`, `NINA.Plugins.SyncService.Service.dll`, `Grpc.Core.Api.dll`, `GrpcDotNetNamedPipes.dll`, `Google.Protobuf.dll`.
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
powershell -ExecutionPolicy Bypass -File build.ps1 1.5.0.0
```
or from `cmd`:
```bat
build.bat 1.5.0.0
```
Options: `-SkipDeploy` (build + package only), `-Configuration Debug`, `-NinaPluginsRoot <path>`.
The portable copy lands in `dist\SyncService\` (+ `dist\SyncService-1.5.0.0.zip`) for moving to another PC.

---

## Settings

**Tools → Options → Plugins → SyncService**:

| Setting | Purpose |
|---|---|
| Service (Start / Stop) | Turns the whole synchronization service on/off across all instances. Shows the current *Running* / *Stopped* state. Off by default. |
| Pause imaging while the mount moves | Master on/off for mount-move pausing. |
| Mount Settle Time | Quiet time after a move before the others resume. |
| Mount Max. Wait Timeout | Max hold while the mount is busy (reactive / unexpected moves). |
| Mount Move Threshold | Coordinate change (arcsec) that counts as a move. |
| Pause aux on unexpected mount moves | The automatic safety net for unplanned moves. |
| Flip / Drift Rendezvous Timeout | Max the main waits for the others before **any** mount operation (flip / recenter / dither). Set it above your longest sub-exposure. |
| Autofocus Wait Timeout | Max the main waits for an autofocus to finish before moving. |
| Start Imaging Timeout | Max wait at *Synced Begin Imaging*. |

---

## Migration (upgrading from 1.3.x)

The dither moved from "every instance" to the main instance only. To upgrade your sequences:

- **Remove *Synced Dither* from every aux instance.** Keep (or add) one **Synced Dither (main)** on the mount/guider instance. The aux instances hold during the dither through their existing **Synced Mount Check (aux)** — no extra instruction needed.
- The instruction display names now carry **(main)** / **(aux)** suffixes; existing instructions keep working and just show the new names.
- The old "SyncService Max. Wait Timeout" setting is gone — the single **Flip / Drift Rendezvous Timeout** now covers dithers too.

If you leave a *Synced Dither* on an aux instance it simply does nothing (it only acts on the guider-connected main), but you should remove it to keep the sequence clean.

---

## Notes & limitations

- When a mount operation starts, an in-flight exposure on another instance is **allowed to finish** before that instance holds (N.I.N.A. has no safe way for a plugin to abort a running exposure mid-frame) — so expect at most one discardable frame per operation.
- The server-hosting instance (first to start) must remain running for the session.
- Built against N.I.N.A. `3.0.0.1056`; tested on `3.1.x`.

## License

MPL-2.0.
