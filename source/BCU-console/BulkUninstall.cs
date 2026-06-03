using UninstallTools;
using UninstallTools.Uninstaller;

namespace BcuCli;

/// <summary>
/// Multi-target uninstall using BCU's <see cref="BulkUninstallTask"/> engine.
/// Mirrors the GUI's bulk-uninstall flow: concurrency, prefer-quiet, auto-kill-stuck,
/// retry-failed, protection handling and the "one loud uninstaller at a time" limit.
/// </summary>
public static class BulkUninstall
{
    public static int Run(CliArgs args)
    {
        if (args.Targets.Count == 0)
        {
            Console.Error.WriteLine("Usage: bcu bulk <name> [<name> ...] [--prefer-quiet] [--concurrent N] [--yes]");
            Console.Error.WriteLine("       (or: bcu uninstall <name> <name> ... --bulk)");
            return 1;
        }

        var all = Engine.ScanWithBanner(args);

        // Resolve every requested target; collect distinct matching entries.
        var resolved = new List<ApplicationUninstallerEntry>();
        var unmatched = new List<string>();
        foreach (var name in args.Targets)
        {
            var matches = Engine.FindByName(all, name, args.ExactMatch);
            if (matches.Count == 0) unmatched.Add(name);
            else resolved.AddRange(matches);
        }

        var entries = resolved
            .GroupBy(e => e.RegistryPath ?? (e.DisplayName + "|" + e.UninstallString))
            .Select(g => g.First())
            .ToList();

        if (unmatched.Count > 0)
            Console.Error.WriteLine($"Warning: no match for: {string.Join(", ", unmatched)}");

        if (entries.Count == 0)
        {
            Console.Error.WriteLine("No applications matched. Nothing to do.");
            return 1;
        }

        return RunForEntries(args, entries);
    }

    /// <summary>
    /// Execute a bulk uninstall over an already-resolved set of entries.
    /// Shared by the by-name <see cref="Run"/> path and the legacy .bcul list path
    /// (BCU-console back-compat), so both honour the same plan/dry-run/execute flow.
    /// </summary>
    public static int RunForEntries(CliArgs args, List<ApplicationUninstallerEntry> entries)
    {
        if (entries.Count == 0)
        {
            Console.Error.WriteLine("No applications matched. Nothing to do.");
            return 1;
        }

        // Show the plan.
        Console.WriteLine($"Targets ({entries.Count}):");
        foreach (var e in entries.OrderBy(x => x.DisplayName))
        {
            var prot = e.IsProtected ? "  [PROTECTED]" : "";
            var quiet = e.QuietUninstallPossible ? "" : "  (no quiet uninstaller)";
            Console.WriteLine($"  [{e.UninstallerKind}] {e.DisplayName} {e.DisplayVersion}{quiet}{prot}");
        }

        var protectedCount = entries.Count(e => e.IsProtected);
        if (protectedCount > 0 && !args.IgnoreProtected)
            Console.WriteLine($"\nNote: {protectedCount} protected item(s) will be SKIPPED unless --ignore-protected is given.");

        Console.WriteLine($"\nMode: prefer-quiet={args.PreferQuiet}  concurrent={args.Concurrent}  " +
                          $"auto-kill-stuck={args.AutoKillStuck}  retry-failed={args.RetryFailed}  " +
                          $"loud-limit={!args.NoLoudLimit}");

        if (!args.WillExecute)
        {
            Console.WriteLine("\nDRY RUN: re-run with --yes to execute the bulk uninstall.");
            return 0;
        }

        // Build and run the task.
        var taskEntries = entries
            .Select(e => new BulkUninstallEntry(e, e.QuietUninstallPossible, UninstallStatus.Waiting))
            .ToList();

        var config = new BulkUninstallConfiguration(
            ignoreProtection:   args.IgnoreProtected,
            preferQuiet:        args.PreferQuiet,
            simulate:           false,
            autoKillStuckQuiet: args.AutoKillStuck,
            retryFailedQuiet:   args.RetryFailed);

        var task = UninstallManager.CreateBulkUninstallTask(taskEntries, config);
        task.ConcurrentUninstallerCount = Math.Max(1, args.Concurrent);
        task.OneLoudLimit = !args.NoLoudLimit;

        Console.WriteLine("\nStarting bulk uninstall...\n");
        task.Start();

        // Poll until finished, streaming progress to stderr.
        string? lastLine = null;
        while (!task.Finished)
        {
            Thread.Sleep(300);
            if (!args.Quiet)
            {
                var done = task.AllUninstallersList.Count(x => x.Finished);
                var running = task.AllUninstallersList.Count(x => x.IsRunning);
                var line = $"  progress: {done}/{task.AllUninstallersList.Count} finished, {running} running";
                if (line != lastLine)
                {
                    lastLine = line;
                    Console.Error.Write($"\r{line,-60}");
                }
            }
        }
        if (!args.Quiet) Console.Error.WriteLine();

        // Summary.
        Console.WriteLine("\nResults:");
        int ok = 0, failed = 0, skipped = 0, other = 0;
        foreach (var t in task.AllUninstallersList.OrderBy(x => x.UninstallerEntry.DisplayName))
        {
            var name = t.UninstallerEntry.DisplayName;
            switch (t.CurrentStatus)
            {
                case UninstallStatus.Completed: ok++;      Console.WriteLine($"  OK       {name}"); break;
                case UninstallStatus.Failed:    failed++;  Console.WriteLine($"  FAILED   {name}  — {t.CurrentError?.Message}"); break;
                case UninstallStatus.Skipped:   skipped++; Console.WriteLine($"  SKIPPED  {name}"); break;
                case UninstallStatus.Protected: skipped++; Console.WriteLine($"  PROTECTED {name}"); break;
                case UninstallStatus.Invalid:   other++;   Console.WriteLine($"  INVALID  {name}"); break;
                default:                        other++;   Console.WriteLine($"  {t.CurrentStatus,-8} {name}"); break;
            }
        }

        Console.WriteLine($"\nDone. Completed: {ok}  Failed: {failed}  Skipped: {skipped}  Other: {other}");
        return failed > 0 ? 1 : 0;
    }
}
