using UninstallTools;
using UninstallTools.Factory;
using UninstallTools.Uninstaller;

namespace BcuCli;

/// <summary>
/// Thin wrapper around BCU's UninstallTools engine: scanning, filtering, sorting and
/// resolving CLI targets to <see cref="ApplicationUninstallerEntry"/> objects.
/// Shared by every command so detection behaves identically everywhere.
/// </summary>
public static class Engine
{
    /// <summary>
    /// Run BCU's full application scan, applying the source flags from the CLI.
    /// Progress is written to stderr unless --quiet.
    /// </summary>
    public static IList<ApplicationUninstallerEntry> Scan(CliArgs args)
    {
        UninstallToolsGlobalConfig.ScanRegistry      = args.ScanRegistry;
        UninstallToolsGlobalConfig.ScanDrives        = args.ScanDrives;
        UninstallToolsGlobalConfig.ScanSteam         = args.ScanSteam;
        UninstallToolsGlobalConfig.ScanStoreApps     = args.ScanStore;
        UninstallToolsGlobalConfig.ScanChocolatey    = args.ScanChoco;
        UninstallToolsGlobalConfig.ScanScoop         = args.ScanScoop;
        UninstallToolsGlobalConfig.ScanWinFeatures   = args.ScanFeatures;
        UninstallToolsGlobalConfig.ScanWinUpdates    = args.ScanUpdates;
        UninstallToolsGlobalConfig.ScanOculus        = args.ScanOculus;
        UninstallToolsGlobalConfig.EnableAppInfoCache = false;
        UninstallToolsGlobalConfig.QuietAutomatization = args.RmmSafe;
        UninstallToolsGlobalConfig.QuietAutomatizationKillStuck = args.RmmSafe;
        UninstallToolsGlobalConfig.ScanPreDefined = !args.RmmSafe;
        UninstallToolsGlobalConfig.AutoDetectCustomProgramFiles = !args.RmmSafe;
        UninstallToolsGlobalConfig.AutoDetectScanRemovable = false;

        string? lastMsg = null;
        var originalError = Console.Error;
        if (args.Quiet)
            Console.SetError(TextWriter.Null);
        try
        {
            return ApplicationUninstallerFactory.GetUninstallerEntries(report =>
            {
                if (!args.Quiet && report.Message != lastMsg)
                {
                    lastMsg = report.Message;
                    Console.Error.Write($"\r  {report.Message,-60}");
                }
            });
        }
        finally
        {
            if (args.Quiet)
                Console.SetError(originalError);
        }
    }

    /// <summary>Scan and print a "Scanning..." banner to stderr (unless quiet).</summary>
    public static IList<ApplicationUninstallerEntry> ScanWithBanner(CliArgs args)
    {
        if (!args.Quiet) Console.Error.WriteLine("Scanning installed applications...");
        var all = Scan(args);
        if (!args.Quiet) Console.Error.WriteLine();
        return all;
    }

    public static List<ApplicationUninstallerEntry> Filter(IList<ApplicationUninstallerEntry> all, CliArgs args)
    {
        var q = all.AsEnumerable();

        if (!string.IsNullOrEmpty(args.Filter))
            q = q.Where(e =>
                (e.DisplayName ?? "").Contains(args.Filter, StringComparison.OrdinalIgnoreCase) ||
                (e.Publisher   ?? "").Contains(args.Filter, StringComparison.OrdinalIgnoreCase));

        if (!args.IncludeSystem)
            q = q.Where(e => !e.SystemComponent);

        if (!args.IncludeUpdates)
            q = q.Where(e => !e.IsUpdate);

        if (!args.IncludeOrphaned)
            q = q.Where(e => !e.IsOrphaned);

        return q.ToList();
    }

    public static List<ApplicationUninstallerEntry> Sort(List<ApplicationUninstallerEntry> apps, CliArgs args) =>
        args.SortBy switch
        {
            "publisher" => [.. apps.OrderBy(a => a.Publisher ?? "", StringComparer.OrdinalIgnoreCase)],
            "date"      => [.. apps.OrderByDescending(a => a.InstallDate)],
            "size"      => [.. apps.OrderByDescending(a => a.EstimatedSize.GetKbSize())],
            "source"    => [.. apps.OrderBy(a => a.UninstallerKind.ToString()).ThenBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)],
            _           => [.. apps.OrderBy(a => a.DisplayName ?? "", StringComparer.OrdinalIgnoreCase)]
        };

    public static List<ApplicationUninstallerEntry> FindByName(
        IList<ApplicationUninstallerEntry> all, string name, bool exact)
    {
        var exactMatches = all
            .Where(e => (e.DisplayName ?? "").Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (exactMatches.Count > 0 || exact) return exactMatches;

        return all
            .Where(e => (e.DisplayName ?? "").Contains(name, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>Resolve a target by registry path, rating id, or name.</summary>
    public static List<ApplicationUninstallerEntry> FindTargets(IList<ApplicationUninstallerEntry> all, CliArgs args)
    {
        if (!string.IsNullOrWhiteSpace(args.TargetRegistryPath))
            return all
                .Where(e => string.Equals(e.RegistryPath, args.TargetRegistryPath, StringComparison.OrdinalIgnoreCase))
                .ToList();

        if (!string.IsNullOrWhiteSpace(args.TargetRatingId))
            return all
                .Where(e => string.Equals(e.RatingId, args.TargetRatingId, StringComparison.OrdinalIgnoreCase))
                .ToList();

        return FindByName(all, args.TargetName ?? "", args.ExactMatch);
    }

    /// <summary>
    /// Resolve exactly one target for single-entry actions. Returns null and prints a
    /// disambiguation list to stderr if zero or multiple matches are found.
    /// </summary>
    public static ApplicationUninstallerEntry? ResolveSingle(IList<ApplicationUninstallerEntry> all, CliArgs args)
    {
        var matches = FindTargets(all, args);
        if (matches.Count == 0)
        {
            Console.Error.WriteLine("No application found matching the requested target.");
            Console.Error.WriteLine("Tip: use 'bcu list --format json' and target by --registry-path or --rating-id.");
            return null;
        }

        if (matches.Count > 1)
        {
            Console.Error.WriteLine($"Multiple matches for \"{args.TargetName}\":");
            foreach (var m in matches.OrderBy(x => x.DisplayName))
                Console.Error.WriteLine($"  [{m.UninstallerKind}] {m.DisplayName}  {m.DisplayVersion}  ({m.InstallLocation})");
            Console.Error.WriteLine("\nUse a more specific name, --exact, --registry-path, or --rating-id.");
            return null;
        }

        return matches[0];
    }

    public static string RunAsText(RunAsMode runAs) =>
        runAs == RunAsMode.ActiveUser ? "active-user" : "system";

    public static string? UninstallCommand(ApplicationUninstallerEntry entry, CliArgs args) =>
        args.UseQuietUninstall && entry.QuietUninstallPossible
            ? entry.QuietUninstallString
            : entry.UninstallString;
}
