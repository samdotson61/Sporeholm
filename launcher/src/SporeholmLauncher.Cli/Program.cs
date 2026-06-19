using SporeholmLauncher.Core;

// Headless launcher: install/update/rollback/play + mod + config management.
// The graphical launcher (SporeholmLauncher) drives the same Core engine.
LauncherSelfUpdater.CleanupStale();   // remove a prior self-update's .old
return await CliRunner.RunAsync(args, Console.Out);
