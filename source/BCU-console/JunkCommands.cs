/*
    Copyright (c) 2026 theoneec
    Apache License Version 2.0

    Part of a personal fork of Bulk Crap Uninstaller
    (Marcin Szeniak, https://github.com/Klocman/). Original file authored for
    the merged BCU-console command-line interface.
*/
using System.Text.Json;
using System.Text.Json.Serialization;
using UninstallTools;
using UninstallTools.Junk.Confidence;
using UninstallTools.Junk.Containers;

namespace BcuCli;

/// <summary>
/// `bcu junk`, `bcu manual-uninstall`, `bcu clean-program-files` and the post-uninstall
/// junk pass shared by uninstall / bulk / msi / uninstall-dir.
/// </summary>
public static class JunkCommands
{
    /// <summary>Scan leftovers for <paramref name="targets"/> and preview/clean them per the safety model.</summary>
    public static int ScanAndClean(IEnumerable<ApplicationUninstallerEntry> targets,
        ICollection<ApplicationUninstallerEntry> all, CliArgs args) =>
        PresentAndClean(ScanForEntries(targets, all, args), args);

    /// <summary>GUI "Clean up Program Files": orphaned folders not owned by any installed app.</summary>
    public static int RunCleanProgramFiles(CliArgs args)
    {
        var all = Engine.ScanWithBanner(args);
        if (!args.Quiet) Console.Error.WriteLine("Scanning Program Files for orphaned folders...");
        return PresentAndClean(JunkService.ScanOrphans(all, args.JunkLevel), args);
    }

    /// <summary>GUI "Manual uninstall": remove an app's leftovers (incl. its registry entry) without running its uninstaller.</summary>
    public static int RunManualUninstall(CliArgs args)
    {
        if (args.Targets.Count == 0 && !Engine.HasTarget(args))
        {
            Console.Error.WriteLine("Usage: bcu manual-uninstall <name...>|--registry-path <path> [--junk-level <level>] [--backup <dir>] [--yes]");
            return ExitCodes.BadUsage;
        }
        var all = Engine.ScanWithBanner(args);
        var targets = args.Targets.Count > 1
            ? args.Targets.SelectMany(n => Engine.FindByName(all, n, args.ExactMatch)).Distinct().ToList()
            : Engine.FindTargets(all, args);
        if (targets.Count == 0)
        {
            Console.Error.WriteLine("No application matched.");
            return ExitCodes.NotFound;
        }
        foreach (var t in targets) Console.WriteLine($"Target: {t.DisplayName}");
        if (args.WillExecute && !UninstallSupport.Before(targets, args, doNotKillSteam: false)) return ExitCodes.Error;
        try { return ScanAndClean(targets, all, args); }
        finally { if (args.WillExecute) UninstallSupport.After(args); }
    }


    public static int Run(CliArgs args)
    {
        var all = Engine.ScanWithBanner(args);
        List<IJunkResult> junk;

        if (!string.IsNullOrWhiteSpace(args.TargetName))
        {
            var targets = Engine.FindByName(all, args.TargetName, args.ExactMatch);
            if (targets.Count == 0)
            {
                Console.Error.WriteLine($"No application found matching \"{args.TargetName}\".");
                return ExitCodes.NotFound;
            }
            junk = ScanForEntries(targets, all, args);
        }
        else
        {
            if (!args.Quiet) Console.Error.WriteLine("Scanning for orphaned program files across all installed apps...");
            junk = JunkService.ScanOrphans(all, args.JunkLevel);
        }

        return PresentAndClean(junk, args);
    }

    // Used by both `bcu junk <name>` and `bcu uninstall --junk`.
    public static List<IJunkResult> ScanForEntries(
        IEnumerable<ApplicationUninstallerEntry> targets,
        ICollection<ApplicationUninstallerEntry> all,
        CliArgs args)
    {
        if (!args.Quiet) Console.Error.WriteLine("Scanning for leftover junk...");
        var junk = JunkService.Scan(targets, all, args.JunkLevel, args.Quiet ? null
            : msg => Console.Error.Write($"\r  {msg,-60}"));
        if (!args.Quiet) Console.Error.WriteLine();
        return junk;
    }

    public static int PresentAndClean(List<IJunkResult> junk, CliArgs args)
    {
        var machine = args.Format is OutputFormat.Json or OutputFormat.Csv;

        if (junk.Count == 0)
        {
            if (machine) WriteJunkOutput(junk, args, null);
            else Console.WriteLine("No junk found above the confidence threshold.");
            return ExitCodes.Success;
        }

        if (!args.WillExecute)
        {
            // Preview (dry-run). Machine formats emit the full list (for RMM review); text groups by confidence.
            if (machine)
            {
                WriteJunkOutput(junk, args, null);
            }
            else
            {
                Console.WriteLine($"\nFound {junk.Count} junk item(s):\n");
                foreach (var level in new[] { ConfidenceLevel.VeryGood, ConfidenceLevel.Good, ConfidenceLevel.Questionable, ConfidenceLevel.Bad, ConfidenceLevel.Unknown })
                {
                    var group = junk.Where(j => j.Confidence.GetConfidence() == level).ToList();
                    if (group.Count == 0) continue;
                    Console.WriteLine($"  [{level}]");
                    foreach (var j in group)
                        Console.WriteLine($"    {j.Source?.CategoryName ?? "?"} — {j.GetDisplayName()}");
                }
                Console.WriteLine($"\nDRY RUN: re-run with --yes to delete the above {junk.Count} item(s)"
                    + (args.BackupDir != null ? $" (backing up to {args.BackupDir} first)." : ". Files go to the Recycle Bin; registry/other are permanent."));
            }
            return ExitCodes.Success;
        }

        if (!machine) Console.WriteLine(args.BackupDir != null ? $"Cleaning junk (backup -> {args.BackupDir})..." : "Cleaning junk...");
        var res = JunkService.Clean(junk, args.BackupDir, (j, ok, err) =>
        {
            if (!machine && !ok) Console.Error.WriteLine($"  Failed: [{j.GetDisplayName()}] — {err}");
        });

        WriteJunkOutput(junk, args, res);
        if (!machine)
            Console.WriteLine($"Done. Deleted: {res.Deleted}  Failed: {res.Failed}"
                + (args.BackupDir != null ? $"  BackedUp: {res.BackedUp}" : ""));
        return res.Failed > 0 ? ExitCodes.PartialFailure : ExitCodes.Success;
    }

    // Machine-readable (json/csv) junk output, honouring --output. res==null => preview only.
    private static void WriteJunkOutput(List<IJunkResult> junk, CliArgs args, JunkCleanResult? res)
    {
        if (args.Format is not (OutputFormat.Json or OutputFormat.Csv)) return;

        string? ErrOf(IJunkResult j) => res?.Items.FirstOrDefault(i => ReferenceEquals(i.Item, j)).Error;
        bool? DelOf(IJunkResult j) => res == null ? null : ErrOf(j) == null;

        using var output = Output.OpenOutput(args);
        var w = output ?? Console.Out;

        if (args.Format == OutputFormat.Json)
        {
            var payload = new
            {
                count = junk.Count,
                executed = res != null,
                deleted = res?.Deleted,
                failed = res?.Failed,
                backedUp = res?.BackedUp,
                items = junk.Select(j => new
                {
                    category = j.Source?.CategoryName,
                    name = j.GetDisplayName(),
                    confidence = j.Confidence.GetConfidence().ToString(),
                    application = j.Application?.DisplayName,
                    deleted = DelOf(j),
                    error = ErrOf(j)
                })
            };
            w.WriteLine(JsonSerializer.Serialize(payload,
                new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }));
        }
        else // Csv
        {
            w.WriteLine("confidence,category,application,name,deleted,error");
            foreach (var j in junk)
            {
                string C(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
                w.WriteLine(string.Join(",",
                    C(j.Confidence.GetConfidence().ToString()), C(j.Source?.CategoryName),
                    C(j.Application?.DisplayName), C(j.GetDisplayName()),
                    C(DelOf(j)?.ToString()), C(ErrOf(j))));
            }
        }
    }
}
