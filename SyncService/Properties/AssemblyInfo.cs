using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

// General Information about an assembly is controlled through the following
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTitle("SyncService")]
[assembly: AssemblyDescription("A plugin that introduces synchronization instructions for multi camera imaging rigs")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("Stefan Berg @isbeorn")]
[assembly: AssemblyProduct("NINA.Plugins")]
[assembly: AssemblyCopyright("Copyright ©  2021-2025")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

[assembly: InternalsVisibleTo("NINA.Plugins.Test")]
[assembly: SupportedOSPlatform("windows")]

// Setting ComVisible to false makes the types in this assembly not visible
// to COM components.  If you need to access a type in this assembly from
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

// The following GUID is for the ID of the typelib if this project is exposed to COM
[assembly: Guid("a79e7f82-91ec-4587-bfd1-17adced9932a")]

// Version information for an assembly consists of the following four values:
//
//      Major Version
//      Minor Version
//      Build Number
//      Revision
//
// You can specify all the values or you can default the Build and Revision Numbers
// by using the '*' as shown below:
// [assembly: AssemblyVersion("1.4.0.0")]
[assembly: AssemblyVersion("1.4.0.0")]
[assembly: AssemblyFileVersion("1.4.0.0")]

//The minimum Version of N.I.N.A. that this plugin is compatible with
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.0.0.1056")]

//Your plugin homepage - omit if not applicaple
[assembly: AssemblyMetadata("Homepage", "https://www.patreon.com/stefanberg/")]
//The license your plugin code is using
[assembly: AssemblyMetadata("License", "MPL-2.0")]
//The url to the license
[assembly: AssemblyMetadata("LicenseURL", "https://www.mozilla.org/en-US/MPL/2.0/")]
//The repository where your pluggin is hosted
[assembly: AssemblyMetadata("Repository", "https://github.com/MichaelLevAstro/nina.plugin.syncservice")]

[assembly: AssemblyMetadata("ChangelogURL", "https://github.com/MichaelLevAstro/nina.plugin.syncservice/blob/master/SyncService/Changelog.md")]

//Common tags that quickly describe your plugin
[assembly: AssemblyMetadata("Tags", "Dither,Synchronization,Multiple Cameras,Mount")]

//The featured logo that will be displayed in the plugin list next to the name
[assembly: AssemblyMetadata("FeaturedImageURL", "https://github.com/MichaelLevAstro/nina.plugin.syncservice/blob/master/SyncService/SynchronizationLogo.jpg?raw=true")]
//An example screenshot of your plugin in action
[assembly: AssemblyMetadata("ScreenshotURL", "https://github.com/MichaelLevAstro/nina.plugin.syncservice/blob/master/SyncService/SynchronizationSample.jpg?raw=true")]
//An additional example screenshot of your plugin in action
[assembly: AssemblyMetadata("AltScreenshotURL", "")]
[assembly: AssemblyMetadata("LongDescription", @"Coordinates multiple cameras running separate N.I.N.A. instances on one shared mount so they don't ruin each other's frames.

*One simple rule:* only the mount instance ever moves the mount; every other instance just holds while it does. Because the mount instance is the single initiator, the instances can never deadlock waiting on each other.

*Naming:* instruction names carry a placement suffix - **(main)** = place on the mount instance, **(aux)** = place on every other instance, no suffix = place on every instance.

*Prerequisites*:
* Only one instance connected to the mount.
* For dithering, that mount instance is also connected to the guider.
* The first N.I.N.A. instance to start hosts the coordination server and must stay running for the whole session.

*Place on the mount instance (main)*:
* 'Synced Meridian Flip (main)' - interrupts the others' autofocus, waits for them to stop, runs the full flip, then releases them.
* 'Synced Center after Drift (main)' - on measured drift, waits for autofocus and for the others, recenters, then releases them.
* 'Synced Dither (main)' - dithers the shared mount once after N exposures, pausing the others while it does.
* 'Synced Slew and Center (main)' - slews and centers while the others are held until the mount settles.

*Place on every other instance (aux)*:
* 'Synced Mount Check (aux)' - holds this instance whenever the mount instance runs ANY mount operation, and as a safety net for unexpected moves.

*Place on every instance*:
* 'Synced Begin Imaging' - all instances start imaging together once the mount instance is on target.
* 'Synced Autofocus' (after exposures / time / HFR increase / temperature / filter change) - each instance focuses on its own and blocks the mount instance from moving while it does; a due meridian flip interrupts it and it re-runs afterwards.

A safety net also pauses the other instances if the mount moves unexpectedly (a manual slew, another plugin, ...); this can be tuned or toggled in the plugin options.

")]
