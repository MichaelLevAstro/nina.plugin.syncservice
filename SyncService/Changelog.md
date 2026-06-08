# Changelog

## Version 1.3.3.0
- 'Synced Begin Imaging' now auto-detects whether the instance is connected to the mount: the mount instance releases the others, every other instance waits here - so the single instruction goes on every instance at its ready-to-image point
- Removed 'Synced Start Imaging' - place 'Synced Begin Imaging' on every instance instead. This fixes the mount instance getting stuck "waiting for the mount instance to begin imaging" when 'Synced Start Imaging' was placed on it by mistake
- Breaking: sequences that used 'Synced Start Imaging' must replace it with 'Synced Begin Imaging'

## Version 1.3.2.0
- Synced Meridian Flip now holds the other instances for a configurable 'Guider Settle After Flip' time after the flip completes, so they no longer resume imaging before the guider has resumed and settled (default 30s; set 0 if not guiding)

## Version 1.3.1.0
- 'Synced Center and Slew' renamed to 'Synced Slew and Center' (matches N.I.N.A.'s 'Slew and Center')
- 'Synced Center after Drift' now shows the live last-check drift out of the configured maximum
- All SyncService instructions are now grouped under a single 'SyncService' category in the add-instruction/trigger menus

## Version 1.3.0.0
- New 'Synced Start Imaging' (other instances) + 'Synced Begin Imaging' (mount instance): the other instances wait at Start Imaging until the mount instance reaches Begin Imaging (after it has slewed, centered and is guiding), so all instances start imaging together
- The status bar spinner now animates continuously instead of dropping to a static "Idle"

## Version 1.2.0.0
- Reworked around explicit Advanced Sequencer instructions instead of auto-handling every mount move
- 'Synchronized Dither' renamed to 'Synced Dither' (same behaviour)
- New 'Synced Meridian Flip' (mount instance) + 'Synced Mount Check' (every other instance): the flip only runs once all other instances have paused, then they resume
- New 'Synced Center after Drift' (mount instance): waits for the other instances to pause before recentering, using the same 'Synced Mount Check'
- New 'Synced Center and Slew' (mount instance): the others pause until the slew/center finishes (replaces the old Mount Action container)
- New 'Synced Autofocus' triggers (after exposures / time / HFR increase / temperature change / filter change): each instance focuses on its own, holds the mount instance from slewing while focusing, and a due meridian flip interrupts the autofocus and re-runs it after the flip
- The automatic mount-movement detector is now a safety net for UNEXPECTED moves only (toggle: "Pause aux on unexpected mount moves")
- Removed 'Synchronized Wait'
- Breaking: sequences saved with the previous Synchronized* instructions must be rebuilt with the new Synced* instructions

## Version 1.1.0.0
- Added mount synchronization so instances stop imaging while the mount moves (new target, center after drift, meridian flip or any manual slew)
- The instance connected to the mount automatically detects movement and reports it to the others - no instruction needed on the mount instance
- New 'Mount Sync Guard' trigger: place it around the exposures on instances that are not connected to the mount. It lets the current exposure finish, then holds before the next one until the mount has stopped and settled, then resumes
- New 'Synchronized Mount Action' container: optionally wrap a Slew/Center on the mount instance to hold the other instances deterministically for the whole operation
- New plugin options to tune mount synchronization (settle time, max wait timeout, move threshold) or turn it off

## Version 1.0.3.0
- Fixed a race where a fast leader could complete a synchronized wait before another instance observed the sync start, leaving that instance blocked until the leader sequence stopped

## Version 1.0.2.0
- Improved synchronization reliability when N.I.N.A. instances disconnect, unregister, time out, or cancel while waiting at a synchronized point
- Reduced the chance that a failed synchronized dither or wait can affect later synchronization points in the same session
- Heartbeat handling is more robust when synchronization starts and stops quickly
- Synchronized Dither now prevents invalid "After Exposures" values instead of silently running without dithering

## Version 1.0.1.0
- Sync Dither will now register when starting the sequence, instead of when starting the instruction set it is inside
- Sync Dither after exposures will no longer skip execution when clearing the image history
- SyncService registration now counts registrations and a client source is only fully unregistered when all instructions have done so

## Version 1.0.0.0
- Upgrade to .NET7

## Version 0.2.6.0
- Fix Synchronized Dither Exposure Count amount not being restored correctly on load
- Improved synchronization robustness

## Version 0.2.5.0

- Make sure that only one heartbeat task is running per instance
- Improved logging
- Show the rectangle spinner on the server only when sync is in progress

## Version 0.2.4.0

- Prevent types of sync instructions to interfer with each other. E.g. Sync Dither to not continue when another instance hits Sync Wait
- Report Status individually per instruction type. E.g. Sync Dither and Sync Wait will be shown separately if they are hit concurrently for whatever reason

## Version 0.2.3.0

- Refactored the code for more general purpose sync instructions
- Added a "Synchronized Wait" instruction that can be used to sync up all instances to wait until each one hits the instruction. This can be used for example when a new target is started.
- Make sure that all sequences are running before hitting the synchronization instructions, as only on sequence startup each instance registers itself to the synchronization service

## Version 0.2.2.0

- Add a setting to adjust the maximum dither wait timeout. Previously was fixed to 300 seconds.

## Version 0.2.0.0

- It is now possible to connect only one instance to the guider instead of having all instances to be connected to it. This will make it possible to synchronize other guider sources like the MGEN.
- Heartbeats to the server are only sent when the sequence is running, instead of starting heartbeats on application startup

## Version 0.1.0.0

- Initial release for testing
