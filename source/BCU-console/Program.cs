/*
    Copyright (c) 2017 Marcin Szeniak (https://github.com/Klocman/)  — original BCU-console
    Copyright (c) 2026 theoneec                                      — merged CLI rewrite
    Apache License Version 2.0

    Modified 2026 (BCU personal fork): the original 3-command BCU-console
    Program.cs was replaced with the full bcu-cli command dispatcher.
*/

/*
 * BCU-console — Bulk Crap Uninstaller, command-line interface.
 *
 * Full UninstallTools-engine CLI (merged from bcu-cli) so the command line has
 * feature parity with the GUI. Builds to bcu.exe (with a BCU-console.exe alias).
 *
 * Commands:
 *   bcu list   [options]              — list installed apps
 *   bcu export [options] [file]       — export full list (json/csv/xml/bat/ps1)
 *   bcu uninstall <name> [options]    — uninstall by name/target (+ optional junk)
 *   bcu uninstall <list.bcul> [/Q /U] — LEGACY: uninstall every app matched by a saved BCU list
 *   bcu bulk <name...> [options]      — bulk uninstall many apps at once
 *   bcu repair  <name> [options]      — run modify/repair (MSI maintenance / ModifyPath)
 *   bcu modify  <name> [options]      — run the app's modify/change command
 *   bcu rename  <name> <new> [opts]   — rename the registry entry
 *   bcu delete-entry <name> [opts]    — delete the registry entry only (keep files)
 *   bcu startup list|enable|disable   — manage system startup entries
 *   bcu info    <name>                — detailed properties + certificate dump
 *   bcu junk   [<name>] [options]     — find/remove leftover junk
 *   bcu import-list <file>            — load a saved BCU list
 *   bcu help                          — show help
 *
 * Safety model: state-changing commands run only with --yes (and without --dry-run);
 * otherwise they print what they would do and exit without changing anything.
 * (Legacy /U maps to --yes, /Q to quiet uninstallers, /J[=Level] to junk cleanup.)
 */

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BcuCli;
using Klocman.Forms.Tools;
using UninstallTools;
using UninstallTools.Junk.Confidence;
using UninstallTools.Junk.Containers;
using UninstallTools.Lists;
using UninstallTools.Uninstaller;

// BCU needs InvariantCulture and an assembly location so it can find helper EXEs
Thread.CurrentThread.CurrentCulture   = CultureInfo.InvariantCulture;
Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;
try { Console.OutputEncoding = Encoding.UTF8; } catch { }

var cliArgs = CliArgs.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray());

// Headless: route the engine's non-fatal errors to stderr instead of a modal
// message box (PremadeDialogs.GenericError would otherwise block with no desktop
// and pollute stdout, corrupting --format json output).
PremadeDialogs.HeadlessErrorHandler = ex =>
{
    if (!cliArgs.Quiet)
        Console.Error.WriteLine($"[engine] {ex.Message}");
};

// Ctrl+C: cancel the scan cooperatively and exit cleanly. First press unwinds the
// scan (the long, hang-prone part); a second press force-quits. An in-flight
// uninstall is intentionally not interrupted, to avoid leaving an app half-removed.
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    if (cancellation.IsCancellationRequested) return;   // second Ctrl+C => default behaviour (terminate)
    e.Cancel = true;
    Console.Error.WriteLine("\nCancelling... (press Ctrl+C again to force-quit)");
    cancellation.Cancel();
};
Engine.CancelToken = cancellation.Token;

try
{
    return cliArgs.Command switch
    {
        Command.Help        => RunHelp(),
        Command.List        => RunList(cliArgs),
        Command.Export      => RunExport(cliArgs),
        Command.Uninstall   => RunUninstall(cliArgs),
        Command.Bulk        => BulkUninstall.Run(cliArgs),
        Command.Repair      => EntryActions.RunRepair(cliArgs),
        Command.Modify      => EntryActions.RunModify(cliArgs),
        Command.Rename      => EntryActions.RunRename(cliArgs),
        Command.DeleteEntry => EntryActions.RunDeleteEntry(cliArgs),
        Command.Startup     => StartupCommands.Run(cliArgs),
        Command.Info        => EntryActions.RunInfo(cliArgs),
        Command.Junk        => JunkCommands.Run(cliArgs),
        Command.ImportList  => Exporters.RunImportList(cliArgs),
        Command.Serve       => ServeCommand.Run(cliArgs),
        Command.Tui         => TuiCommand.Run(cliArgs),
        Command.Notes       => NotesCommands.Run(cliArgs),
        Command.Msi         => GuiParityCommands.RunMsi(cliArgs),
        Command.UninstallDir=> GuiParityCommands.RunUninstallDir(cliArgs),
        Command.Target      => GuiParityCommands.RunTarget(cliArgs),
        Command.ManualUninstall   => JunkCommands.RunManualUninstall(cliArgs),
        Command.CleanProgramFiles => JunkCommands.RunCleanProgramFiles(cliArgs),
        Command.RestorePoint=> GuiParityCommands.RunRestorePoint(cliArgs),
        Command.RegBackup   => GuiParityCommands.RunRegBackup(cliArgs),
        Command.Open        => GuiParityCommands.RunOpen(cliArgs),
        Command.SearchOnline=> GuiParityCommands.RunSearchOnline(cliArgs),
        Command.Run         => GuiParityCommands.RunRun(cliArgs),
        Command.TakeOwnership => GuiParityCommands.RunTakeOwnership(cliArgs),
        Command.MakeList    => GuiParityCommands.RunMakeList(cliArgs),
        Command.Tools       => GuiParityCommands.RunTools(cliArgs),
        _                   => RunList(cliArgs)
    };
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return ExitCodes.Cancelled;
}
catch (Exception ex)
{
    Output.WriteError("BCU-console failed", ex, cliArgs.JsonErrors);
    return ExitCodes.Error;
}

// ═══════════════════════════════════════════════════════════════════════════════
// LIST
// ═══════════════════════════════════════════════════════════════════════════════

static int RunList(CliArgs args)
{
    var all = Engine.ScanWithBanner(args);
    var apps = Engine.Sort(Engine.Filter(all, args), args);

    switch (args.Format)
    {
        case OutputFormat.Json:
            using (var output = Output.OpenOutput(args)) Output.PrintJson(apps, args.VerifyCerts, output ?? Console.Out);
            break;
        case OutputFormat.Csv:
            using (var output = Output.OpenOutput(args)) Output.PrintCsv(apps, args.VerifyCerts, output ?? Console.Out);
            break;
        default:
            Output.PrintTable(apps, args.Wide, args.VerifyCerts);
            break;
    }

    if (!args.Quiet)
        Console.Error.WriteLine($"\nTotal: {apps.Count} application(s)");
    return ExitCodes.Success;
}

// ═══════════════════════════════════════════════════════════════════════════════
// EXPORT
// ═══════════════════════════════════════════════════════════════════════════════

static int RunExport(CliArgs args)
{
    var all = Engine.ScanWithBanner(args);
    var apps = Engine.Sort(Engine.Filter(all, args), args);

    // xml / bat / ps1 are handled by the dedicated exporter.
    if (Exporters.TryExport(apps, args))
        return ExitCodes.Success;

    TextWriter output = !string.IsNullOrEmpty(args.OutputFile)
        ? new StreamWriter(args.OutputFile, false, Encoding.UTF8)
        : Console.Out;

    if (!string.IsNullOrEmpty(args.OutputFile))
        Console.Error.WriteLine($"Exporting {apps.Count} entries to {args.OutputFile}...");

    using (output != Console.Out ? output : null)
    {
        if (args.Format == OutputFormat.Csv) Output.PrintCsv(apps, args.VerifyCerts, output);
        else                                 Output.PrintJson(apps, args.VerifyCerts, output);
    }

    if (!string.IsNullOrEmpty(args.OutputFile))
        Console.Error.WriteLine("Done.");
    return ExitCodes.Success;
}

// ═══════════════════════════════════════════════════════════════════════════════
// UNINSTALL (single target). Multiple targets require the bulk command/flag.
// ═══════════════════════════════════════════════════════════════════════════════

static int RunUninstall(CliArgs args)
{
    if (args.Bulk)
        return BulkUninstall.Run(args);

    // Back-compat with upstream BCU-console: `uninstall <listfile.bcul>` uninstalls every
    // installed application matched by a saved BCU uninstall list, rather than by name.
    if (args.Targets.Count == 1 && TryLoadUninstallList(args.Targets[0], out var list))
        return RunListUninstall(list!, args);

    if (args.Targets.Count > 1)
    {
        Console.Error.WriteLine($"{args.Targets.Count} targets given. Use 'bcu bulk ...' or add --bulk to uninstall multiple apps.");
        return ExitCodes.BadUsage;
    }

    if (!Engine.HasTarget(args))
    {
        Console.Error.WriteLine("Usage: bcu uninstall <name>|<list.bcul>|--registry-path <path>|--rating-id <id>|--msi-guid <guid> [--exact] [--quiet-uninstall] [--safe-mode] [--yes] [--junk] [--junk-level <level>] [--dry-run]");
        return ExitCodes.BadUsage;
    }

    var all = Engine.ScanWithBanner(args);
    var entry = Engine.ResolveSingle(all, args);
    if (entry == null) return ExitCodes.NotFound;

    // Related entries (GUI wizard) and machine-readable results (RMM) go through the bulk
    // executor, which supports both; --quiet-uninstall maps to its prefer-quiet option.
    if (args.WithRelated || args.Format == OutputFormat.Json)
    {
        args.PreferQuiet |= args.UseQuietUninstall;
        return BulkUninstall.RunForEntries(args, new List<ApplicationUninstallerEntry> { entry }, all);
    }

    Console.WriteLine($"Found:    {entry.DisplayName}");
    Console.WriteLine($"Version:  {entry.DisplayVersion ?? "(unknown)"}");
    Console.WriteLine($"Type:     {entry.UninstallerKind}");
    Console.WriteLine($"Location: {entry.InstallLocation ?? "(unknown)"}");
    Console.WriteLine($"Command:  {Engine.UninstallCommand(entry, args) ?? "(none)"}");
    Console.WriteLine($"RunAs:    {Engine.RunAsText(args.RunAs)}");

    if (!args.WillExecute)
    {
        Console.WriteLine("\nDRY RUN: re-run with --yes to uninstall.");
        if (args.RunJunk) Console.WriteLine("        (--junk cleanup would run afterwards)");
        return ExitCodes.Success;
    }

    if (!UninstallSupport.Before(new[] { entry }, args, doNotKillSteam: !args.UseQuietUninstall))
        return ExitCodes.Error;

    Console.WriteLine(args.Simulate ? "\nUninstalling (simulated)..." : "\nUninstalling...");
    try
    {
        var proc = entry.RunUninstaller(args.UseQuietUninstall, args.Simulate, args.SafeMode);
        proc?.WaitForExit();
        var code = proc?.ExitCode ?? 0;
        Console.WriteLine(code == 0 ? "Uninstall completed." : $"Uninstaller exited with code {code}.");

        if (args.RunJunk)
            JunkCommands.ScanAndClean(new[] { entry }, all, args);
        return ExitCodes.FromUninstaller(code);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Uninstall error: {ex.Message}");
        return ExitCodes.Error;
    }
    finally
    {
        UninstallSupport.After(args);
    }
}

// ───────────────────────────────────────────────────────────────────────────────
// LEGACY .bcul LIST UNINSTALL (upstream BCU-console behaviour)
// ───────────────────────────────────────────────────────────────────────────────

/// <summary>
/// True only when <paramref name="path"/> is an existing file that parses as a non-empty
/// BCU uninstall list. Keeps `uninstall "SomeAppName"` (not a file) on the by-name path.
/// </summary>
static bool TryLoadUninstallList(string path, out UninstallList? list)
{
    list = null;
    try
    {
        if (!File.Exists(path)) return false;
        var loaded = UninstallList.ReadFromFile(path);
        if (loaded == null || loaded.Filters.Count == 0) return false;
        list = loaded;
        return true;
    }
    catch
    {
        return false;
    }
}

static int RunListUninstall(UninstallList list, CliArgs args)
{
    var all = Engine.ScanWithBanner(args);
    var entries = all.Where(a => list.TestEntry(a) == true)
                     .OrderBy(x => x.DisplayName)
                     .ToList();

    if (entries.Count == 0)
    {
        Console.WriteLine("No installed applications matched the supplied uninstall list.");
        return ExitCodes.Success;
    }

    Console.WriteLine($"Uninstall list matched {entries.Count} installed application(s).");
    // Reuse the by-name bulk executor: same plan/dry-run/--yes safety model.
    return BulkUninstall.RunForEntries(args, entries, all);
}

// ═══════════════════════════════════════════════════════════════════════════════
// HELP
// ═══════════════════════════════════════════════════════════════════════════════

static int RunHelp()
{
    Console.WriteLine("""
        bcu — Bulk Crap Uninstaller CLI  (BCU-console)
        ================================================
        Powered by BCU's UninstallTools engine. Detects every source BCU supports.

        SAFETY MODEL
          State-changing commands (uninstall, bulk, repair, modify, rename,
          delete-entry, startup changes, junk, msi, notes set, ...) run ONLY with --yes.
          Without --yes (or with --dry-run) they print what they WOULD do and
          change nothing.

        COMMANDS
          bcu list      [options]              List installed apps (default)
          bcu export    [options] [file]       Export list (json|csv|xml|bat|ps1)
          bcu uninstall <target> [options]     Uninstall one app
          bcu uninstall <list.bcul> [/Q /U]    Legacy: uninstall everything a saved list matches
          bcu bulk      <name...> [options]    Uninstall many apps at once
          bcu repair    <target> [options]     Repair (MSI maintenance / modify)
          bcu modify    <target> [options]     Run the app's modify/change command
          bcu rename    <target> <new-name>    Rename the registry entry
          bcu delete-entry <target> [options]  Delete registry entry only (keep files)
          bcu startup   list|enable|disable    Manage system startup entries
          bcu info      <target>               Detailed properties + certificate
          bcu junk      [<name>] [options]     Find/clean leftover junk
          bcu import-list <file>               Load a saved BCU list (.xml)
          bcu serve     [--pipe <name>]        Run a JSON-RPC API daemon over a named pipe
          bcu tui                              Interactive full-screen terminal UI (arrows/space)
          bcu help                             Show this help

        MORE COMMANDS (GUI parity)
          bcu msi <target> [--mode configure|uninstall|quiet]   Run msiexec /I, /X or /qb /X
          bcu uninstall-dir <directory>        Uninstall whatever is installed in a folder
          bcu target <pid|process|file|dir>    Which app owns this? (+ --uninstall to remove it)
          bcu manual-uninstall <name...>       Remove leftovers + registry entry, no uninstaller
          bcu clean-program-files              Remove orphaned Program Files folders
          bcu notes list|get|set|clear [<target>] [<text>]      Custom notes (shared with GUI)
          bcu restore-point [<description>]    Create a System Restore point
          bcu reg-backup <name...> -o f.reg    Export the uninstall registry keys to a .reg file
          bcu make-list <name...> -o f.bcul [--exclude] [--append]  Build a .bcul uninstall list
          bcu open <target> [--what install|uninstaller|source|web|registry] [--launch]
          bcu search-online <target> [--site google|alternativeto|slant|fosshub|sourceforge|filehippo|github]
          bcu run <target> [--index N]         List / launch the app's executables  (interactive)
          bcu take-ownership <target>          takeown + icacls on the app's folders
          bcu tools netfx3|features|disk-cleanup|troubleshoot|programs-and-features|system-restore
                                    netfx3 and features work headless; the rest open windows

        LIST / EXPORT OPTIONS
          --format table|json|csv|xml|bat|ps1|store-ps1  Output format
                                    (xml = native BCU list; bat/ps1 = uninstall scripts;
                                     store-ps1 = Remove-AppxPackage script for Store apps)
          --filter <text>           Filter by name or publisher
          --sort name|publisher|date|size|source  (default: name)
          --wide                    Show more columns (type, date, size)
          --verify-certs            Check digital signatures (adds signed/valid columns)
          --system                  Include system components (hidden by default)
          --updates                 Include Windows Updates
          --orphaned                Include orphaned entries
          --all                     Include system + updates + orphaned
          --preset <p>              GUI view preset: basic|advanced|everything|system|startup|
                                    browsers|tweaks|orphaned|updates|invalid|features|store|protected
          --kind <t[,t]>            Only these uninstaller types (Msiexec,Nsis,InnoSetup,StoreApp,
                                    Steam,WindowsFeature,WindowsUpdate,Chocolatey,Scoop,...)
          --hide-microsoft          Hide entries published by Microsoft
          --invalid                 Only entries whose uninstaller is missing/invalid
          --list <file.bcul>        Only entries matched by a saved uninstall list
          --output <file>, -o       Write output to file (required for --format xml)
          --quiet, -q               Suppress progress text
          --json-errors             Emit machine-readable errors

        SOURCES
          --rmm-safe                Registry-only scan for service-context RMM use
          --drives                  Enable drive scanning (directory-based detection)
          --oculus                  Enable Oculus scanning
          --no-registry/-drives/-steam/-store/-choco/-scoop/-features/-updates
                                    Skip a specific source
          --source-timeout <secs>   Max seconds to wait for any detection helper EXE
                                    before killing it (default 120; 0 = no timeout)
          --no-predefined           Skip predefined templates / tweaks
          --custom-folders "a;b"    Extra program folders to scan (with --drives)
          --no-folder-autodetect    Don't auto-detect custom Program Files folders
          --scan-removable          Also scan removable drives
          --quiet-automation        Automate loud uninstallers into quiet ones (UninstallerAutomatizer)
          --quiet-automation-kill-stuck   ...and kill them if they get stuck
          --use-daemon              Use the bulk quiet-uninstall daemon
          --cache                   Use/refresh the app info cache (InfoCache.xml)

        EXECUTION CONTEXT
          --run-as system|active-user   Intended context hint for RMM agents

        UNINSTALL OPTIONS
          --registry-path <path>    Target by registry uninstall key path
          --rating-id <id>          Target by BCU rating id
          --msi-guid <guid>         Target by MSI product code
          --exact                   Exact name match only (default: partial)
          --quiet-uninstall         Use quiet/silent uninstaller if available
          --safe-mode               Don't modify the uninstall command (NSIS fix off)
          --yes, -y                 Actually execute (without it: dry-run)
          --dry-run                 Force dry-run even with --yes
          --junk                    Also clean leftover junk after uninstall
          --junk-level <level>      VeryGood|Good|Questionable|Bad|Unknown (default Good)
          --simulate                Engine-level simulation (runs nothing, reports as if it had)
          --with-related            Also uninstall related entries (same app family / folder)
          --close-apps              Kill processes running from the app's folders first
          --restore-point           Create a System Restore point before uninstalling
          --reg-backup <file.reg>   Export the targets' registry keys first (abort if it fails)
          --pre-command <cmd>       Run before uninstalling (repeatable)
          --post-command <cmd>      Run after uninstalling (repeatable)
          --format json             Machine-readable plan/result on stdout (for RMM)

        JUNK OPTIONS  (bcu junk [<name>])
          --junk-level <level>      Minimum confidence to act on (default Good)
          --backup <dir>            Back up each item before deleting (item is skipped if
                                    its backup fails). Files also go to the Recycle Bin.
          --format json|csv         Machine-readable junk preview/result (+ --output <file>)
          --yes                     Delete (without it: preview only) · --dry-run forces preview

        LEGACY BCU-console SWITCHES (for `uninstall <list.bcul>` back-compat)
          /Q                        Prefer quiet uninstallers
          /U                        Unattended — execute (equivalent to --yes)
          /V                        Verbose (progress already streams to stderr)
          /J[=Level]                Clean junk after uninstall (default level VeryGood)

        BULK OPTIONS  (bcu bulk <name...>  /  bcu uninstall <name...> --bulk)
          --prefer-quiet            Prefer silent uninstallers
          --concurrent <N>          Run up to N uninstallers at once (default 1)
          --auto-kill-stuck         Force-kill stuck quiet uninstallers
          --retry-failed            Retry failed quiet uninstalls loudly
          --ignore-protected        Include protected entries (default: skip them)
          --no-loud-limit           Allow multiple visible uninstallers at once
          --no-intelligent-sort     Run in name order (default: GUI's intelligent ordering)
          --junk                    Clean leftovers of everything that was removed
          --yes                     Execute (otherwise dry-run lists the targets)

        STARTUP OPTIONS
          bcu startup list [--format json]
          bcu startup disable <match> [--yes]
          bcu startup enable  <match> [--yes]
          bcu startup delete  <match> [--yes]
          bcu startup backup  <match> <directory> [--yes]
          bcu startup all-users|current-user|move-to-registry <match> [--yes]
          --type normal|task|service|browser   Restrict to one kind of startup entry

        EXIT CODES
          0 success   1 error   2 bad usage   3 not found   4 partial failure
          5 needs elevation   6 needs user session   7 timeout   8 cancelled
          9 success, reboot required (msiexec 3010/1641)

        EXAMPLES
          bcu list --format json --quiet
          bcu list --filter visual --wide --verify-certs
          bcu export --format xml -o apps.xml
          bcu export --format ps1 -o uninstall-all.ps1
          bcu uninstall "Git" --quiet-uninstall --yes
          bcu uninstall removelist.bcul /Q /U          (legacy list form)
          bcu bulk "Toolbar" "Ask" "Weather" --prefer-quiet --concurrent 2 --yes
          bcu repair "Microsoft Office" --yes
          bcu rename "App (x64)" "App" --yes
          bcu delete-entry "Broken Entry" --yes
          bcu startup disable Spotify --yes
          bcu info "Visual Studio Code"
          bcu junk "Discord" --junk-level VeryGood --yes

        RMM EXAMPLES  (run as SYSTEM; parse stdout, branch on exit code)
          bcu list --rmm-safe --format json --quiet
          bcu uninstall "7-Zip" --quiet-uninstall --format json --quiet --yes
          bcu bulk "Toolbar" "Coupon" --prefer-quiet --auto-kill-stuck --close-apps --junk --format json --yes
          bcu msi --msi-guid {23170F69-40C1-2702-2301-000001000000} --mode quiet --yes
          bcu uninstall-dir "C:\Program Files\OldApp" --yes
          bcu list --preset startup --format csv -o startup-apps.csv
        """);
    return ExitCodes.Success;
}
