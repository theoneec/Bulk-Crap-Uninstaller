/*
    Copyright (c) 2017 Marcin Szeniak (https://github.com/Klocman/)  — behaviour ported from the GUI
    Copyright (c) 2026 theoneec                                      — CLI implementation
    Apache License Version 2.0

    Part of a personal fork of Bulk Crap Uninstaller. Command-line versions of GUI
    features (MainWindow menus, AppUninstaller, OnlineSearchTools, Tools menu) that the
    original CLI did not cover. Dialogs are replaced by the CLI's dry-run/--yes model.
*/
using System.Diagnostics;
using System.Text.Json;
using System.Web;
using Klocman.IO;
using Klocman.Tools;
using UninstallTools;
using UninstallTools.Factory;
using UninstallTools.Factory.InfoAdders;
using UninstallTools.Lists;
using UninstallTools.Uninstaller;

namespace BcuCli;

public static class GuiParityCommands
{
    private const string TargetUsage = "<name>|--registry-path <path>|--rating-id <id>|--msi-guid <guid>";

    // ═══════════════════════════════════════════════════════════════════════════
    // msi — GUI "Uninstall using MsiExec" (Install or configure /I, Uninstall /X, Quiet /qb /X)
    // ═══════════════════════════════════════════════════════════════════════════

    public static int RunMsi(CliArgs args)
    {
        var mode = (args.MsiMode ?? "uninstall") switch
        {
            "configure" or "install" or "modify" or "repair" or "i" => MsiUninstallModes.InstallModify,
            "uninstall" or "x"                                     => MsiUninstallModes.Uninstall,
            "quiet" or "quiet-uninstall" or "qb"                   => MsiUninstallModes.QuietUninstall,
            _ => (MsiUninstallModes?)null
        };
        if (mode == null)
        {
            Console.Error.WriteLine("--mode must be configure|uninstall|quiet");
            return ExitCodes.BadUsage;
        }

        var entry = Engine.ScanAndResolve(args, $"bcu msi {TargetUsage} [--mode configure|uninstall|quiet] [--yes]", out var all, out var rc);
        if (entry == null) return rc;

        if (entry.BundleProviderKey == Guid.Empty)
        {
            Console.Error.WriteLine($"\"{entry.DisplayName}\" has no MSI product code; use 'bcu uninstall' instead.");
            return ExitCodes.Error;
        }
        if (entry.IsProtected && !args.IgnoreProtected)
        {
            Console.Error.WriteLine($"\"{entry.DisplayName}\" is protected. Add --ignore-protected to proceed.");
            return ExitCodes.Error;
        }

        var flag = mode switch
        {
            MsiUninstallModes.InstallModify => "/I",
            MsiUninstallModes.QuietUninstall => "/qb /X",
            _ => "/X"
        };
        Console.WriteLine($"Found:    {entry.DisplayName}  {entry.DisplayVersion}");
        Console.WriteLine($"Action:   msiexec {flag} {entry.BundleProviderKey:B}");

        if (!args.WillExecute)
        {
            Console.WriteLine("\nDRY RUN: re-run with --yes to run msiexec.");
            return ExitCodes.Success;
        }

        if (!UninstallSupport.Before(new[] { entry }, args, doNotKillSteam: true)) return ExitCodes.Error;
        try
        {
            var code = entry.UninstallUsingMsi(mode.Value, args.Simulate);
            Console.WriteLine($"msiexec exited with code {code}.");
            if (args.RunJunk && mode != MsiUninstallModes.InstallModify)
                JunkCommands.ScanAndClean(new[] { entry }, all, args);
            return ExitCodes.FromUninstaller(code);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"msiexec error: {ex.Message}");
            return ExitCodes.Error;
        }
        finally { UninstallSupport.After(args); }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // uninstall-dir — GUI "Uninstall from directory"
    // ═══════════════════════════════════════════════════════════════════════════

    public static int RunUninstallDir(CliArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.Value) || !Directory.Exists(args.Value))
        {
            Console.Error.WriteLine("Usage: bcu uninstall-dir <existing directory> [--yes] [--junk-level <level>] [--backup <dir>]");
            return ExitCodes.BadUsage;
        }

        var dir = new DirectoryInfo(Path.GetFullPath(args.Value));
        var all = Engine.ScanWithBanner(args);

        if (!args.Quiet) Console.Error.WriteLine($"Looking for applications in {dir.FullName}...");
        var found = DirectoryFactory.TryCreateFromDirectory(dir, Array.Empty<string>()).ToList();
        if (found.Count == 0)
            found.AddRange(all.Where(x => PathTools.PathsEqual(dir.FullName, x.InstallLocation)));

        if (found.Count == 0)
        {
            Console.Error.WriteLine("No applications were found in that directory.");
            return ExitCodes.NotFound;
        }

        // Same resolution as the GUI: prefer a real uninstaller (own or of an installed entry at
        // the same location); everything else is removed as leftovers.
        var toRun = new List<ApplicationUninstallerEntry>();
        var leftovers = new List<ApplicationUninstallerEntry>();
        foreach (var item in found)
        {
            if (HasRealUninstaller(item)) { toRun.Add(item); continue; }

            var installed = all.Where(x => PathTools.PathsEqual(item.InstallLocation, x.InstallLocation)).ToList();
            if (installed.Count == 0) { leftovers.Add(item); continue; }
            foreach (var e in installed)
                (HasRealUninstaller(e) ? toRun : leftovers).Add(e);
        }
        toRun = toRun.Distinct().ToList();
        leftovers = leftovers.Distinct().Where(x => !toRun.Contains(x)).ToList();

        Console.WriteLine($"Directory: {dir.FullName}");
        foreach (var e in toRun)
            Console.WriteLine($"  run uninstaller  {e.DisplayName}  ({e.UninstallString})");
        foreach (var e in leftovers)
            Console.WriteLine($"  remove leftovers {e.DisplayName}  ({e.InstallLocation})");

        if (!args.WillExecute)
        {
            Console.WriteLine("\nDRY RUN: re-run with --yes to run the uninstallers and remove the leftovers.");
            return ExitCodes.Success;
        }

        if (!UninstallSupport.Before(toRun.Concat(leftovers).ToList(), args, doNotKillSteam: false)) return ExitCodes.Error;
        var failed = 0;
        try
        {
            foreach (var e in toRun)
            {
                try
                {
                    Console.WriteLine($"Uninstalling {e.DisplayName}...");
                    var p = e.RunUninstaller(args.UseQuietUninstall, args.Simulate, args.SafeMode);
                    p?.WaitForExit();
                    Console.WriteLine($"  exit code {p?.ExitCode ?? 0}");
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.Error.WriteLine($"  failed: {ex.Message}");
                }
            }

            if (leftovers.Count > 0)
            {
                var others = all.Where(x => !leftovers.Any(y => PathTools.PathsEqual(y.InstallLocation, x.InstallLocation))).ToList();
                var rc = JunkCommands.ScanAndClean(leftovers, others, args);
                if (rc != ExitCodes.Success) failed++;
            }
        }
        finally { UninstallSupport.After(args); }

        return failed > 0 ? ExitCodes.PartialFailure : ExitCodes.Success;
    }

    private static bool HasRealUninstaller(ApplicationUninstallerEntry e) =>
        e.UninstallPossible && e.UninstallerKind != UninstallerType.SimpleDelete;

    // ═══════════════════════════════════════════════════════════════════════════
    // target — GUI "Uninstall by window / process / file" (the drag-target)
    // ═══════════════════════════════════════════════════════════════════════════

    public static int RunTarget(CliArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.Value))
        {
            Console.Error.WriteLine("Usage: bcu target <pid|process-name|file|directory> [--uninstall [--yes]] [--format json]");
            return ExitCodes.BadUsage;
        }

        var dirs = ResolveTargetDirectories(args.Value!);
        if (dirs.Count == 0)
        {
            Console.Error.WriteLine($"\"{args.Value}\" is not a running process id/name or an existing file/directory.");
            return ExitCodes.NotFound;
        }

        var all = Engine.ScanWithBanner(args);
        var apps = GetApplicationsFromDirectories(all, dirs);
        if (apps.Count == 0)
        {
            Console.Error.WriteLine("No application owns that location.");
            return ExitCodes.NotFound;
        }

        if (!args.DoUninstall)
        {
            if (args.Format == OutputFormat.Json) Output.PrintJson(apps, args.VerifyCerts);
            else Output.PrintTable(apps, args.Wide, args.VerifyCerts);
            return ExitCodes.Success;
        }

        // The GUI hands target results to the quiet uninstall flow.
        args.PreferQuiet = true;
        return BulkUninstall.RunForEntries(args, apps.ToList(), all);
    }

    private static List<DirectoryInfo> ResolveTargetDirectories(string what)
    {
        var dirs = new List<DirectoryInfo>();
        if (Directory.Exists(what)) { dirs.Add(new DirectoryInfo(Path.GetFullPath(what))); return dirs; }
        if (File.Exists(what)) { dirs.Add(new FileInfo(Path.GetFullPath(what)).Directory!); return dirs; }

        IEnumerable<Process> procs;
        if (int.TryParse(what, out var pid))
        {
            try { procs = new[] { Process.GetProcessById(pid) }; }
            catch (ArgumentException) { procs = Array.Empty<Process>(); }
        }
        else
        {
            procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(what));
        }

        foreach (var p in procs)
        {
            try
            {
                var file = p.MainModule?.FileName;
                if (!string.IsNullOrEmpty(file)) dirs.Add(new FileInfo(file).Directory!);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Cannot inspect process {p.Id}: {ex.Message} (try running elevated)");
            }
        }
        return dirs;
    }

    /// <summary>Port of AppUninstaller.GetApplicationsFromDirectories (without message boxes).</summary>
    public static List<ApplicationUninstallerEntry> GetApplicationsFromDirectories(
        IList<ApplicationUninstallerEntry> all, ICollection<DirectoryInfo> dirs)
    {
        var output = new List<ApplicationUninstallerEntry>();
        var processed = new List<DirectoryInfo>();
        foreach (var dir in dirs.DistinctBy(d => d.FullName.TrimEnd('\\').ToLowerInvariant()).OrderBy(x => x.FullName))
        {
            if (processed.Any(x => dir.FullName.StartsWith(x.FullName, StringComparison.OrdinalIgnoreCase))) continue;
            processed.Add(dir);
        }

        var withLocation = all.Where(x => x.IsInstallLocationValid() && !UninstallToolsGlobalConfig.IsSystemDirectory(x.InstallLocation)).ToList();
        var infoAdder = new InfoAdderManager();
        foreach (var dir in processed)
        {
            // Installed apps whose folder contains (or is) the target path
            var owners = withLocation.Where(x =>
                    PathTools.SubPathIsInsideBasePath(x.InstallLocation, dir.FullName, true, true) ||
                    PathTools.SubPathIsInsideBasePath(dir.FullName, x.InstallLocation, true, true))
                .ToList();
            if (owners.Count > 0) { output.AddRange(owners); continue; }

            var created = DirectoryFactory.TryCreateFromDirectory(dir, withLocation).ToList();
            if (created.Count == 0)
            {
                Console.Error.WriteLine($"Nothing found in {dir.FullName}");
                continue;
            }
            foreach (var r in created)
            {
                infoAdder.AddMissingInformation(r);
                output.Add(r);
            }
        }
        return output.Distinct().ToList();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // restore-point — Tools > Create restore point
    // ═══════════════════════════════════════════════════════════════════════════

    public static int RunRestorePoint(CliArgs args)
    {
        var desc = string.IsNullOrWhiteSpace(args.Value) ? "Bulk Crap Uninstaller restore point" : args.Value!;
        Console.WriteLine($"Restore point: \"{desc}\"");
        if (!args.WillExecute)
        {
            Console.WriteLine($"System Restore available: {(SysRestore.SysRestoreAvailable() ? "yes" : "no")}");
            Console.WriteLine("\nDRY RUN: re-run with --yes to create it.");
            return ExitCodes.Success;
        }
        if (!UninstallSupport.BeginRestorePoint(desc, args.Quiet)) return ExitCodes.Error;
        UninstallSupport.EndRestorePoint();
        Console.WriteLine("Restore point created.");
        return ExitCodes.Success;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // reg-backup — "Create registry backup" (.reg export of the uninstall keys)
    // ═══════════════════════════════════════════════════════════════════════════

    public static int RunRegBackup(CliArgs args)
    {
        var file = args.OutputFile ?? args.RegBackupFile;
        if (string.IsNullOrWhiteSpace(file))
        {
            Console.Error.WriteLine("Usage: bcu reg-backup <name...>|--registry-path <path> -o <file.reg>");
            return ExitCodes.BadUsage;
        }

        var all = Engine.ScanWithBanner(args);
        var entries = ResolveMany(all, args, out var rc);
        if (entries == null) return rc;

        foreach (var e in entries) Console.WriteLine($"  {e.DisplayName}  {e.RegistryPath}");
        return UninstallSupport.ExportRegistryBackup(entries, file!, args.Quiet) ? ExitCodes.Success : ExitCodes.Error;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // open — Open install location / uninstaller location / install source / web page / registry key
    // ═══════════════════════════════════════════════════════════════════════════

    public static int RunOpen(CliArgs args)
    {
        var entry = Engine.ScanAndResolve(args, $"bcu open {TargetUsage} [--what install|uninstaller|source|web|registry] [--launch]", out _, out var rc);
        if (entry == null) return rc;

        var what = args.What ?? "install";
        string? value = what switch
        {
            "install"     => entry.IsInstallLocationValid() ? entry.InstallLocation : null,
            "uninstaller" => entry.UninstallerLocation ?? entry.UninstallerFullFilename,
            "source"      => entry.InstallSource,
            "web" or "url"=> entry.GetAboutUri()?.ToString(),
            "registry"    => entry.RegKeyStillExists() ? entry.RegistryPath : null,
            _ => throw new ArgumentException("--what must be install|uninstaller|source|web|registry")
        };

        if (string.IsNullOrEmpty(value))
        {
            Console.Error.WriteLine($"\"{entry.DisplayName}\" has no {what} location.");
            return ExitCodes.NotFound;
        }

        Console.WriteLine(value);
        if (!args.Launch) return ExitCodes.Success;

        try
        {
            if (what == "registry") RegistryTools.OpenRegKeyInRegedit(value);
            else Process.Start(new ProcessStartInfo(value) { UseShellExecute = true });
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to open: {ex.Message}");
            return ExitCodes.Error;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // search-online — Search for the app on Google / AlternativeTo / GitHub / ...
    // ═══════════════════════════════════════════════════════════════════════════

    private static readonly Dictionary<string, (string Prefix, bool FullName, bool Escaped)> SearchSites = new()
    {
        ["google"]        = ("https://www.google.com/search?q=", true, false),
        ["alternativeto"] = ("https://alternativeto.net/browse/search/?q=", false, false),
        ["slant"]         = ("https://www.slant.co/search?query=", false, true),
        ["fosshub"]       = ("https://www.fosshub.com/search/", false, true),
        ["sourceforge"]   = ("https://sourceforge.net/directory/?q=", false, true),
        ["filehippo"]     = ("http://filehippo.com/search?q=", false, false),
        ["github"]        = ("https://github.com/search?q=", false, false),
    };

    public static int RunSearchOnline(CliArgs args)
    {
        var site = args.Site ?? "google";
        if (!SearchSites.TryGetValue(site, out var s))
        {
            Console.Error.WriteLine($"--site must be one of: {string.Join(", ", SearchSites.Keys)}");
            return ExitCodes.BadUsage;
        }

        var entry = Engine.ScanAndResolve(args, $"bcu search-online {TargetUsage} [--site {string.Join("|", SearchSites.Keys)}] [--launch]", out _, out var rc);
        if (entry == null) return rc;

        var term = s.FullName ? entry.DisplayName
            : entry.DisplayNameTrimmed is { Length: > 3 } t ? t : entry.DisplayName;
        var encoded = HttpUtility.UrlEncode(term ?? "");
        if (s.Escaped) encoded = encoded.Replace("+", "%20");
        var url = s.Prefix + encoded;

        Console.WriteLine(url);
        if (args.Launch)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return ExitCodes.Success;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // run — the context menu's "Run" submenu (the app's own executables)
    // ═══════════════════════════════════════════════════════════════════════════

    public static int RunRun(CliArgs args)
    {
        var entry = Engine.ScanAndResolve(args, $"bcu run {TargetUsage} [--index N --yes]", out _, out var rc);
        if (entry == null) return rc;

        var exes = entry.GetSortedExecutables().ToList();
        if (exes.Count == 0)
        {
            Console.Error.WriteLine($"No executables found for \"{entry.DisplayName}\".");
            return ExitCodes.NotFound;
        }

        for (var i = 0; i < exes.Count; i++)
            Console.WriteLine($"  [{i}] {exes[i]}");

        if (args.Index == null)
        {
            Console.WriteLine("\nPass --index N --yes to launch one.");
            return ExitCodes.Success;
        }
        if (args.Index < 0 || args.Index >= exes.Count)
        {
            Console.Error.WriteLine("--index out of range.");
            return ExitCodes.BadUsage;
        }

        var exe = exes[args.Index.Value];
        if (!args.WillExecute)
        {
            Console.WriteLine($"\nDRY RUN: re-run with --yes to launch {exe}.");
            return ExitCodes.Success;
        }
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
        Console.WriteLine($"Launched {exe}");
        return ExitCodes.Success;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // take-ownership — takeown + icacls on the install/uninstaller folders
    // ═══════════════════════════════════════════════════════════════════════════

    public static int RunTakeOwnership(CliArgs args)
    {
        var entry = Engine.ScanAndResolve(args, $"bcu take-ownership {TargetUsage} [--yes]", out _, out var rc);
        if (entry == null) return rc;

        var dirs = new[] { entry.InstallLocation, entry.UninstallerLocation }
            .Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d))
            .DistinctBy(d => d!.ToLowerInvariant()).ToList();
        if (dirs.Count == 0)
        {
            Console.Error.WriteLine($"\"{entry.DisplayName}\" has no existing install/uninstaller folder.");
            return ExitCodes.NotFound;
        }

        foreach (var d in dirs)
            Console.WriteLine($"  takeown /f \"{d}\" /r /d y  &&  icacls \"{d}\" /grant administrators:F /t");

        if (!args.WillExecute)
        {
            Console.WriteLine("\nDRY RUN: re-run with --yes (as administrator) to take ownership.");
            return ExitCodes.Success;
        }

        var failed = 0;
        foreach (var d in dirs)
        {
            var c1 = RunAndWait("takeown.exe", $"/f \"{d}\" /r /d y", args.Quiet);
            var c2 = RunAndWait("icacls.exe", $"\"{d}\" /grant administrators:F /t /c /q", args.Quiet);
            if (c1 != 0 || c2 != 0) failed++;
        }
        Console.WriteLine(failed == 0 ? "Ownership taken." : $"{failed} folder(s) failed (are you elevated?).");
        return failed == 0 ? ExitCodes.Success : ExitCodes.PartialFailure;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // make-list — "Include/Exclude selected in advanced filters" → save a .bcul uninstall list
    // ═══════════════════════════════════════════════════════════════════════════

    public static int RunMakeList(CliArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.OutputFile) || (args.Targets.Count == 0 && string.IsNullOrEmpty(args.Filter) && args.Preset == null))
        {
            Console.Error.WriteLine("Usage: bcu make-list <name...> | --filter <text> | --preset <p>  -o <list.bcul> [--exclude] [--append]");
            return ExitCodes.BadUsage;
        }

        var all = Engine.ScanWithBanner(args);
        List<ApplicationUninstallerEntry> entries;
        if (args.Targets.Count > 0)
        {
            entries = ResolveMany(all, args, out var rc)!;
            if (entries == null) return rc;
        }
        else
        {
            entries = Engine.Filter(all, args);
        }

        var list = args.Append && File.Exists(args.OutputFile)
            ? UninstallList.ReadFromFile(args.OutputFile) ?? new UninstallList()
            : new UninstallList();

        var names = entries.Select(e => e.DisplayName).Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
        list.Filters.RemoveAll(f => names.Contains(f.Name));
        list.AddItems(names.Select(n => new Filter(n, args.Exclude,
            new FilterCondition(n, ComparisonMethod.Equals, nameof(ApplicationUninstallerEntry.DisplayName)))));
        list.SaveToFile(args.OutputFile);

        foreach (var n in names) Console.WriteLine($"  {(args.Exclude ? "exclude" : "include")}  {n}");
        Console.WriteLine($"Saved {list.Filters.Count} filter(s) to {args.OutputFile}");
        Console.WriteLine($"Use it with: bcu uninstall \"{args.OutputFile}\"  or  bcu list --list \"{args.OutputFile}\"");
        return ExitCodes.Success;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // tools — Tools menu
    // ═══════════════════════════════════════════════════════════════════════════

    public static int RunTools(CliArgs args)
    {
        switch (args.SubAction)
        {
            case "netfx3":
                return RunTool(args, DismTools.DismFullPath, "/Online /Enable-Feature /FeatureName:NetFx3 /All /NoRestart", wait: true);
            case "disk-cleanup":
                return RunTool(args, "cleanmgr.exe", "", wait: false);
            case "troubleshoot":
            {
                var cab = Path.Combine(AppContext.BaseDirectory, "Resources", "MicrosoftProgram_Install_and_Uninstall.meta.diagcab");
                if (!File.Exists(cab))
                {
                    Console.Error.WriteLine($"Troubleshooter not found at {cab} (it ships with the GUI).");
                    return ExitCodes.NotFound;
                }
                return RunTool(args, cab, "", wait: false, shell: true);
            }
            case "programs-and-features":
                return RunTool(args, "control.exe", "appwiz.cpl", wait: false);
            case "system-restore":
                return RunTool(args, "rstrui.exe", "", wait: false);
            case "features":
                return ListWindowsFeatures(args);
            default:
                Console.Error.WriteLine("Usage: bcu tools netfx3|disk-cleanup|troubleshoot|programs-and-features|system-restore|features [--yes]");
                return ExitCodes.BadUsage;
        }
    }

    private static int ListWindowsFeatures(CliArgs args)
    {
        if (!DismTools.DismIsAvailable)
        {
            Console.Error.WriteLine("DISM is not available on this system.");
            return ExitCodes.Error;
        }
        var features = DismTools.GetWindowsFeatures().OrderBy(f => f.Key).ToList();
        if (args.Format == OutputFormat.Json)
            Console.WriteLine(JsonSerializer.Serialize(features.Select(f => new { name = f.Key, enabled = f.Value }),
                new JsonSerializerOptions { WriteIndented = true }));
        else
            foreach (var f in features) Console.WriteLine($"{(f.Value ? "enabled " : "disabled")}  {f.Key}");
        return ExitCodes.Success;
    }

    private static int RunTool(CliArgs args, string file, string arguments, bool wait, bool shell = false)
    {
        Console.WriteLine($"Command: {file} {arguments}".TrimEnd());
        if (!args.WillExecute)
        {
            Console.WriteLine("\nDRY RUN: re-run with --yes to run it.");
            return ExitCodes.Success;
        }
        try
        {
            var p = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = shell || !wait });
            if (!wait || p == null) return ExitCodes.Success;
            p.WaitForExit();
            Console.WriteLine($"Exit code {p.ExitCode}");
            return p.ExitCode == 0 ? ExitCodes.Success : p.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to start: {ex.Message}");
            return ExitCodes.Error;
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static int RunAndWait(string file, string arguments, bool quiet)
    {
        var psi = new ProcessStartInfo(file, arguments) { UseShellExecute = false, RedirectStandardOutput = quiet };
        using var p = Process.Start(psi)!;
        if (quiet) p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    /// <summary>Resolve several name targets (or one --registry-path / --rating-id / --msi-guid target).</summary>
    private static List<ApplicationUninstallerEntry>? ResolveMany(IList<ApplicationUninstallerEntry> all, CliArgs args, out int rc)
    {
        rc = ExitCodes.Success;
        List<ApplicationUninstallerEntry> result;
        if (args.Targets.Count > 1)
        {
            result = args.Targets.SelectMany(n => Engine.FindByName(all, n, args.ExactMatch)).Distinct().ToList();
        }
        else if (Engine.HasTarget(args))
        {
            result = Engine.FindTargets(all, args);
        }
        else
        {
            rc = ExitCodes.BadUsage;
            Console.Error.WriteLine("No target given.");
            return null;
        }

        if (result.Count == 0)
        {
            Console.Error.WriteLine("No application matched.");
            rc = ExitCodes.NotFound;
            return null;
        }
        return result;
    }
}
