/*
    Copyright (c) 2026 theoneec
    Apache License Version 2.0

    Part of a personal fork of Bulk Crap Uninstaller
    (Marcin Szeniak, https://github.com/Klocman/). Original file authored for
    the merged BCU-console command-line interface.
*/
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
            return ExitCodes.BadUsage;
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
            return ExitCodes.NotFound;
        }

        return RunForEntries(args, entries, all);
    }

    /// <summary>
    /// Execute a bulk uninstall over an already-resolved set of entries.
    /// Shared by the by-name <see cref="Run"/> path, the legacy .bcul list path, `target --uninstall`
    /// and `uninstall --with-related`, so all honour the same plan/dry-run/execute flow.
    /// <paramref name="all"/> (the full scan) enables --with-related and the post-uninstall --junk pass.
    /// </summary>
    public static int RunForEntries(CliArgs args, List<ApplicationUninstallerEntry> entries,
        IList<ApplicationUninstallerEntry>? all = null)
    {
        var json = args.Format == OutputFormat.Json;
        // In JSON mode the human-readable plan goes to stderr so stdout is one parseable document.
        var plan = json ? Console.Error : Console.Out;

        if (entries.Count == 0)
        {
            Console.Error.WriteLine("No applications matched. Nothing to do.");
            return ExitCodes.NotFound;
        }

        if (args.WithRelated && all != null)
        {
            var related = UninstallSupport.FindRelated(entries, all);
            if (related.Count > 0)
            {
                plan.WriteLine($"Adding {related.Count} related entr(ies): {string.Join(", ", related.Select(r => r.DisplayName))}");
                entries = entries.Concat(related).ToList();
            }
        }

        var taskEntries = entries
            .Select(e => new BulkUninstallEntry(e, e.QuietUninstallPossible, UninstallStatus.Waiting))
            .ToList();
        IReadOnlyList<BulkUninstallEntry> ordered = args.IntelligentSort
            ? UninstallSupport.SortIntelligently(taskEntries).ToList()
            : taskEntries.OrderBy(x => x.UninstallerEntry.DisplayName).ToList();

        // Show the plan (in execution order).
        plan.WriteLine($"Targets ({entries.Count}):");
        foreach (var e in ordered.Select(x => x.UninstallerEntry))
        {
            var prot = e.IsProtected ? "  [PROTECTED]" : "";
            var quiet = e.QuietUninstallPossible ? "" : "  (no quiet uninstaller)";
            plan.WriteLine($"  [{e.UninstallerKind}] {e.DisplayName} {e.DisplayVersion}{quiet}{prot}");
        }

        var protectedCount = entries.Count(e => e.IsProtected);
        if (protectedCount > 0 && !args.IgnoreProtected)
            plan.WriteLine($"\nNote: {protectedCount} protected item(s) will be SKIPPED unless --ignore-protected is given.");

        plan.WriteLine($"\nMode: prefer-quiet={args.PreferQuiet}  concurrent={args.Concurrent}  " +
                       $"auto-kill-stuck={args.AutoKillStuck}  retry-failed={args.RetryFailed}  " +
                       $"loud-limit={!args.NoLoudLimit}  intelligent-sort={args.IntelligentSort}" +
                       (args.Simulate ? "  SIMULATE" : ""));

        if (!args.WillExecute)
        {
            if (json) WriteJson(ordered, executed: false);
            plan.WriteLine("\nDRY RUN: re-run with --yes to execute the bulk uninstall.");
            return ExitCodes.Success;
        }

        if (!UninstallSupport.Before(entries, args, doNotKillSteam: !args.PreferQuiet))
            return ExitCodes.Error;

        BulkUninstallTask task;
        try
        {
            var config = new BulkUninstallConfiguration(
                ignoreProtection:   args.IgnoreProtected,
                preferQuiet:        args.PreferQuiet,
                simulate:           args.Simulate,
                autoKillStuckQuiet: args.AutoKillStuck,
                retryFailedQuiet:   args.RetryFailed);

            task = UninstallManager.CreateBulkUninstallTask(ordered, config);
            task.ConcurrentUninstallerCount = Math.Max(1, args.Concurrent);
            task.OneLoudLimit = !args.NoLoudLimit;

            plan.WriteLine("\nStarting bulk uninstall...\n");
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

            // Post-uninstall junk pass, over the same set the GUI offers: completed, invalid,
            // or skipped entries whose registry key is gone.
            if (args.RunJunk && all != null)
            {
                var junkTargets = task.AllUninstallersList
                    .Where(x => x.CurrentStatus is UninstallStatus.Completed or UninstallStatus.Invalid
                                || (x.CurrentStatus == UninstallStatus.Skipped && !x.UninstallerEntry.RegKeyStillExists()))
                    .Select(x => x.UninstallerEntry).ToList();
                if (junkTargets.Count > 0)
                {
                    // Junk output must not break the JSON document on stdout.
                    var original = Console.Out;
                    if (json) Console.SetOut(Console.Error);
                    try { JunkCommands.ScanAndClean(junkTargets, all.Where(a => a.RegKeyStillExists()).ToList(), args); }
                    finally { if (json) Console.SetOut(original); }
                }
            }
        }
        finally
        {
            UninstallSupport.After(args);
        }

        // Summary.
        var results = task.AllUninstallersList.OrderBy(x => x.UninstallerEntry.DisplayName).ToList();
        int ok = results.Count(t => t.CurrentStatus == UninstallStatus.Completed);
        int failed = results.Count(t => t.CurrentStatus == UninstallStatus.Failed);
        int skipped = results.Count(t => t.CurrentStatus is UninstallStatus.Skipped or UninstallStatus.Protected);
        int other = results.Count - ok - failed - skipped;

        if (json)
        {
            WriteJson(results, executed: true);
        }
        else
        {
            Console.WriteLine("\nResults:");
            foreach (var t in results)
            {
                var name = t.UninstallerEntry.DisplayName;
                switch (t.CurrentStatus)
                {
                    case UninstallStatus.Completed: Console.WriteLine($"  OK       {name}"); break;
                    case UninstallStatus.Failed:    Console.WriteLine($"  FAILED   {name}  — {t.CurrentError?.Message}"); break;
                    case UninstallStatus.Skipped:   Console.WriteLine($"  SKIPPED  {name}"); break;
                    case UninstallStatus.Protected: Console.WriteLine($"  PROTECTED {name}"); break;
                    case UninstallStatus.Invalid:   Console.WriteLine($"  INVALID  {name}"); break;
                    default:                        Console.WriteLine($"  {t.CurrentStatus,-8} {name}"); break;
                }
            }
            Console.WriteLine($"\nDone. Completed: {ok}  Failed: {failed}  Skipped: {skipped}  Other: {other}");
        }
        return failed > 0 ? ExitCodes.PartialFailure : ExitCodes.Success;
    }

    /// <summary>Machine-readable plan/result for RMM scripts (one JSON document on stdout).</summary>
    private static void WriteJson(IEnumerable<BulkUninstallEntry> items, bool executed)
    {
        var list = items.ToList();
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            executed,
            total = list.Count,
            completed = executed ? list.Count(t => t.CurrentStatus == UninstallStatus.Completed) : (int?)null,
            failed = executed ? list.Count(t => t.CurrentStatus == UninstallStatus.Failed) : (int?)null,
            items = list.Select(t => new
            {
                name = t.UninstallerEntry.DisplayName,
                version = t.UninstallerEntry.DisplayVersion,
                kind = t.UninstallerEntry.UninstallerKind.ToString(),
                registryPath = t.UninstallerEntry.RegistryPath,
                quiet = t.IsSilentPossible,
                isProtected = t.UninstallerEntry.IsProtected,
                status = executed ? t.CurrentStatus.ToString() : null,
                error = t.CurrentError?.Message
            })
        }, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        }));
    }
}
