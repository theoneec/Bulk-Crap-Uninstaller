/*
    Copyright (c) 2026 theoneec
    Apache License Version 2.0

    Part of a personal fork of Bulk Crap Uninstaller
    (Marcin Szeniak, https://github.com/Klocman/). Original file authored for
    the merged BCU-console command-line interface.
*/

using UninstallTools;
using UninstallTools.Junk;
using UninstallTools.Junk.Confidence;
using UninstallTools.Junk.Containers;

namespace BcuCli;

/// <summary>Outcome of a junk cleanup pass.</summary>
public sealed class JunkCleanResult
{
    public int Deleted;
    public int Failed;
    public int BackedUp;
    public readonly List<(IJunkResult Item, string? Error)> Items = new();
}

/// <summary>
/// Single shared implementation of junk scanning + cleaning, used by the `junk`
/// command, `serve` (junk.scan/junk.clean) and the TUI. Centralising it means the
/// safety behaviour (accurate deleted/failed counts, optional backup, cancellation)
/// lives in one place rather than being duplicated per caller.
/// </summary>
public static class JunkService
{
    /// <summary>Scan leftover junk for specific target apps, filtered to <paramref name="minLevel"/>+.</summary>
    public static List<IJunkResult> Scan(
        IEnumerable<ApplicationUninstallerEntry> targets,
        ICollection<ApplicationUninstallerEntry> all,
        ConfidenceLevel minLevel,
        Action<string>? progress = null)
    {
        string? last = null;
        return JunkManager.FindJunk(targets, all, report =>
            {
                Engine.CancelToken.ThrowIfCancellationRequested();   // Ctrl+C / serve cancellation
                if (progress != null && report.Message != last)
                {
                    last = report.Message;
                    progress(report.Message);
                }
            })
            .Where(j => j.Confidence.GetConfidence() >= minLevel)
            .ToList();
    }

    /// <summary>Scan orphaned program-files junk across all installed apps, filtered to <paramref name="minLevel"/>+.</summary>
    public static List<IJunkResult> ScanOrphans(ICollection<ApplicationUninstallerEntry> all, ConfidenceLevel minLevel)
    {
        return JunkManager.FindProgramFilesJunk(all.ToList())
            .Where(j => j.Confidence.GetConfidence() >= minLevel)
            .ToList();
    }

    /// <summary>
    /// Delete the supplied junk. If <paramref name="backupDir"/> is given, each item is backed up
    /// first and is NOT deleted if its backup fails (safe-by-default for review workflows).
    /// Relies on the engine's Delete() now surfacing failures, so counts are accurate.
    /// </summary>
    public static JunkCleanResult Clean(
        IEnumerable<IJunkResult> junk,
        string? backupDir = null,
        Action<IJunkResult, bool, string?>? onItem = null)
    {
        var result = new JunkCleanResult();
        foreach (var j in junk)
        {
            Engine.CancelToken.ThrowIfCancellationRequested();
            try
            {
                if (!string.IsNullOrEmpty(backupDir))
                {
                    j.Backup(backupDir!);   // throws on failure -> item is skipped (not deleted)
                    result.BackedUp++;
                }
                j.Delete();
                result.Deleted++;
                result.Items.Add((j, null));
                onItem?.Invoke(j, true, null);
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Items.Add((j, ex.Message));
                onItem?.Invoke(j, false, ex.Message);
            }
        }
        return result;
    }
}
