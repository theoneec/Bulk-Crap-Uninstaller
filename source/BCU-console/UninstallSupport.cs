/*
    Copyright (c) 2017 Marcin Szeniak (https://github.com/Klocman/)  — logic ported from the GUI
    Copyright (c) 2026 theoneec                                      — CLI adaptation
    Apache License Version 2.0

    Part of a personal fork of Bulk Crap Uninstaller. The helpers below are headless
    ports of GUI-layer code (BulkCrapUninstaller/Functions/AppUninstaller.cs and
    Functions/Tools/SystemRestore.cs) that the UninstallTools engine does not expose,
    adapted to print to the console instead of showing dialogs.
*/
using System.Diagnostics;
using Klocman.Extensions;
using Klocman.IO;
using Klocman.Native;
using Klocman.Tools;
using UninstallTools;
using UninstallTools.Factory;
using UninstallTools.Uninstaller;

namespace BcuCli;

/// <summary>
/// GUI behaviours around an uninstall that the engine leaves to the front-end:
/// intelligent ordering, related-app expansion, closing running apps, restore points,
/// registry backups and user-defined pre/post commands.
/// </summary>
public static class UninstallSupport
{
    // ── Ordering ────────────────────────────────────────────────────────────────

    /// <summary>Port of AppUninstaller.SortIntelligently (GUI setting "intelligent uninstaller sorting").</summary>
    public static IEnumerable<BulkUninstallEntry> SortIntelligently(IEnumerable<BulkUninstallEntry> entries) =>
        from item in entries
        orderby
            // Simple deletes last so real uninstallers get a chance to run first
            item.UninstallerEntry.UninstallerKind == UninstallerType.SimpleDelete ascending,
            // Loud first so they can be attended while the quiet ones run
            item.IsSilentPossible ascending,
            // Updates, system components and protected entries usually go with their parent
            item.UninstallerEntry.IsUpdate ascending,
            item.UninstallerEntry.SystemComponent ascending,
            item.UninstallerEntry.IsProtected ascending,
            // Size buckets (number of digits / 4), largest first
            Math.Round(Math.Floor(Math.Log10(item.UninstallerEntry.EstimatedSize.GetKbSize(true)) + 1) / 4) descending,
            // MSI tends to take longest
            item.UninstallerEntry.UninstallerKind == UninstallerType.Msiexec descending,
            item.UninstallerEntry.EstimatedSize.GetKbSize(true) descending
        select item;

    // ── Related apps (GUI uninstall wizard) ─────────────────────────────────────

    /// <summary>
    /// Entries related to the targets (same install folder, same publisher/name family, ...),
    /// using the same threshold as the GUI's BeginUninstallTaskWizard.
    /// </summary>
    public static List<ApplicationUninstallerEntry> FindRelated(
        IReadOnlyCollection<ApplicationUninstallerEntry> targets, IEnumerable<ApplicationUninstallerEntry> all)
    {
        return all
            .Where(o => !targets.Contains(o))
            .Where(o => targets.Any(t => ApplicationEntryTools.AreEntriesRelated(t, o, -3)))
            .ToList();
    }

    // ── Running processes ───────────────────────────────────────────────────────

    /// <summary>Paths an app's running processes would be loaded from (install + uninstaller dirs).</summary>
    public static string[] ProcessFilters(IEnumerable<ApplicationUninstallerEntry> entries) =>
        entries.SelectMany(e => new[] { e.InstallLocation, e.UninstallerLocation })
            .Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()!;

    /// <summary>
    /// Port of AppUninstaller.GetRelatedProcessIds: processes that have a module loaded from
    /// under any of <paramref name="filters"/> (System32 processes are ignored).
    /// </summary>
    public static List<Process> GetRelatedProcesses(string[] filters, bool doNotKillSteam)
    {
        var result = new List<Process>();
        if (filters.Length == 0) return result;

        var myId = Environment.ProcessId;
        var system = WindowsTools.GetEnvironmentPath(CSIDL.CSIDL_SYSTEM);
        foreach (var pr in Process.GetProcesses())
        {
            try
            {
                if (pr.Id == myId || pr.HasExited) continue;
                if (doNotKillSteam && pr.ProcessName.Equals("steam", StringComparison.OrdinalIgnoreCase)) continue;

                var main = pr.MainModule?.FileName;
                if (string.IsNullOrEmpty(main) || main.StartsWith(system, StringComparison.OrdinalIgnoreCase)) continue;

                var hit = pr.Modules.Cast<ProcessModule>()
                    .Select(m => m.FileName)
                    .Where(f => !string.IsNullOrEmpty(f) && Path.IsPathRooted(f))
                    .Any(f => filters.Any(filter => f!.StartsWith(filter, StringComparison.OrdinalIgnoreCase)));
                if (hit) result.Add(pr);
            }
            catch
            {
                // Access denied / exited mid-enumeration - ignore, like the GUI does
            }
        }
        return result;
    }

    /// <summary>
    /// Report (and with <paramref name="kill"/>, terminate) processes running from the given paths.
    /// Mirrors the GUI's "close running applications" dialog. Returns the number still running.
    /// </summary>
    public static int CheckRunningProcesses(string[] filters, bool kill, bool doNotKillSteam, bool quiet)
    {
        var procs = GetRelatedProcesses(filters, doNotKillSteam);
        if (procs.Count == 0) return 0;

        if (!quiet)
        {
            Console.Error.WriteLine($"{procs.Count} running process(es) use the target's files:");
            foreach (var p in procs)
                Console.Error.WriteLine($"  [{p.Id}] {SafeName(p)}");
        }

        if (!kill)
        {
            if (!quiet) Console.Error.WriteLine("Tip: add --close-apps to terminate them first (the GUI asks to close them).");
            return procs.Count;
        }

        var remaining = 0;
        foreach (var p in procs)
        {
            try
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(10000);
                if (!quiet) Console.Error.WriteLine($"  killed [{p.Id}] {SafeName(p)}");
            }
            catch (Exception ex)
            {
                remaining++;
                Console.Error.WriteLine($"  could not kill [{p.Id}]: {ex.Message}");
            }
        }
        return remaining;
    }

    private static string SafeName(Process p)
    {
        try { return p.MainModule?.FileName ?? p.ProcessName; }
        catch { try { return p.ProcessName; } catch { return "?"; } }
    }

    // ── System restore ──────────────────────────────────────────────────────────

    private static long _restoreSeq = long.MinValue;

    /// <summary>Begin a restore point (engine: KlocTools SysRestore). Returns false if one could not be created.</summary>
    public static bool BeginRestorePoint(string description, bool quiet)
    {
        if (!SysRestore.SysRestoreAvailable())
        {
            Console.Error.WriteLine("System Restore is not available (disabled, or not running as administrator).");
            return false;
        }
        if (_restoreSeq != long.MinValue) return true;

        if (!quiet) Console.Error.WriteLine($"Creating system restore point \"{description}\"...");
        // creationFrequency 3 = BEGIN_NESTED_SYSTEM_CHANGE semantics used by the GUI
        var result = SysRestore.StartRestore(description, out _restoreSeq, 3);
        if (result < 0)
        {
            _restoreSeq = long.MinValue;
            Console.Error.WriteLine($"Failed to create a restore point (error {result}).");
            return false;
        }
        return true;
    }

    public static void EndRestorePoint()
    {
        try
        {
            if (_restoreSeq != long.MinValue)
                SysRestore.EndRestore(_restoreSeq);
        }
        catch (Exception ex) { Console.Error.WriteLine($"Restore point finalisation failed: {ex.Message}"); }
        finally { _restoreSeq = long.MinValue; }
    }

    public static string RestoreDescription(int count) =>
        $"BCU: uninstall of {count} application{(count == 1 ? "" : "s")}";

    // ── Registry backup ─────────────────────────────────────────────────────────

    /// <summary>Export the entries' uninstall registry keys to a .reg file (GUI: "Create registry backup").</summary>
    public static bool ExportRegistryBackup(IEnumerable<ApplicationUninstallerEntry> entries, string file, bool quiet)
    {
        var paths = entries.Where(e => e.RegKeyStillExists()).Select(e => e.RegistryPath).Distinct().ToList();
        if (paths.Count == 0)
        {
            Console.Error.WriteLine("None of the targets have a registry key to back up.");
            return false;
        }
        if (!quiet) Console.Error.WriteLine($"Backing up {paths.Count} registry key(s) to {file}...");
        var ok = RegistryTools.ExportRegistry(file, paths);
        if (!ok) Console.Error.WriteLine("Registry export failed.");
        return ok;
    }

    // ── External commands (GUI Settings > External commands) ────────────────────

    /// <summary>Run each command and wait for it. Returns the number of failures.</summary>
    public static int RunExternalCommands(IEnumerable<string> commands, string label, bool quiet)
    {
        var failed = 0;
        foreach (var line in commands.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            try
            {
                if (!quiet) Console.Error.WriteLine($"[{label}] {line}");
                var cmd = ProcessTools.SeparateArgsFromCommand(line);
                var code = cmd.ToProcessStartInfo().StartAndWait();
                if (code != 0) Console.Error.WriteLine($"[{label}] exited with code {code}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"[{label}] failed: {ex.Message}");
            }
        }
        return failed;
    }

    // ── Composite "before" / "after" used by uninstall, bulk and msi ─────────────

    /// <summary>
    /// Everything the GUI does between confirming an uninstall and starting it. Returns false
    /// when the operation should be aborted (registry backup requested but failed, or processes
    /// still running after --close-apps).
    /// </summary>
    public static bool Before(IReadOnlyCollection<ApplicationUninstallerEntry> entries, CliArgs args, bool doNotKillSteam)
    {
        if (!string.IsNullOrEmpty(args.RegBackupFile) &&
            !ExportRegistryBackup(entries, args.RegBackupFile!, args.Quiet))
        {
            Console.Error.WriteLine("Aborting: --reg-backup was requested but the backup failed.");
            return false;
        }

        if (args.RestorePoint && !BeginRestorePoint(RestoreDescription(entries.Count), args.Quiet))
            Console.Error.WriteLine("Continuing without a restore point.");

        var stillRunning = CheckRunningProcesses(ProcessFilters(entries), args.CloseApps, doNotKillSteam, args.Quiet);
        if (args.CloseApps && stillRunning > 0)
        {
            Console.Error.WriteLine("Aborting: some processes could not be closed.");
            EndRestorePoint();
            return false;
        }

        if (args.PreCommands.Count > 0)
            RunExternalCommands(args.PreCommands, "pre", args.Quiet);
        return true;
    }

    public static void After(CliArgs args)
    {
        if (args.PostCommands.Count > 0)
            RunExternalCommands(args.PostCommands, "post", args.Quiet);
        EndRestorePoint();
    }
}
