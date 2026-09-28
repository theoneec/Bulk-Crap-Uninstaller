/*
    Copyright (c) 2026 theoneec
    Apache License Version 2.0

    Part of a personal fork of Bulk Crap Uninstaller
    (Marcin Szeniak, https://github.com/Klocman/). Original file authored for
    the merged BCU-console command-line interface.
*/
using UninstallTools;
using UninstallTools.Factory;
using UninstallTools.Lists;
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
    /// Cooperative cancellation for scans. Set by the CLI from Console.CancelKeyPress.
    /// Checked inside the scan progress callback; uses BCU's existing
    /// OperationCanceledException unwinding. Does not interrupt an in-flight uninstall.
    /// </summary>
    public static CancellationToken CancelToken { get; set; } = CancellationToken.None;

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
        UninstallToolsGlobalConfig.EnableAppInfoCache = args.UseInfoCache;
        UninstallToolsGlobalConfig.QuietAutomatization = args.RmmSafe || args.QuietAutomation;
        UninstallToolsGlobalConfig.QuietAutomatizationKillStuck = args.RmmSafe || args.QuietAutomationKill;
        UninstallToolsGlobalConfig.UseQuietUninstallDaemon = args.UseQuietDaemon;
        UninstallToolsGlobalConfig.ScanPreDefined = !args.RmmSafe && args.ScanPreDefined;
        UninstallToolsGlobalConfig.AutoDetectCustomProgramFiles = args.AutoDetectFolders ?? !args.RmmSafe;
        UninstallToolsGlobalConfig.AutoDetectScanRemovable = args.ScanRemovable;
        if (args.CustomFolders != null)
            UninstallToolsGlobalConfig.CustomProgramFiles = args.CustomFolders;
        UninstallToolsGlobalConfig.HelperProcessTimeout = args.SourceTimeout;

        string? lastMsg = null;
        var originalError = Console.Error;
        if (args.Quiet)
            Console.SetError(TextWriter.Null);
        try
        {
            return ApplicationUninstallerFactory.GetUninstallerEntries(report =>
            {
                // Cooperative cancellation: throws OperationCanceledException, which BCU's
                // engine already unwinds (ConcurrentApplicationFactory + main-thread scans).
                CancelToken.ThrowIfCancellationRequested();
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

    /// <summary>
    /// View presets from the GUI's View &gt; Filtering menu. "Only" presets show just the matching
    /// entries (and therefore bypass the default hiding of system components / updates / orphans).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Func<ApplicationUninstallerEntry, bool>> OnlyPresets =
        new Dictionary<string, Func<ApplicationUninstallerEntry, bool>>(StringComparer.OrdinalIgnoreCase)
        {
            ["system"]    = e => e.SystemComponent,
            ["startup"]   = e => e.HasStartups,
            ["browsers"]  = e => e.IsWebBrowser,
            ["tweaks"]    = e => e.IsScriptTweak,
            ["orphaned"]  = e => e.IsOrphaned,
            ["updates"]   = e => e.IsUpdate,
            ["invalid"]   = e => !e.IsValid,
            ["features"]  = e => e.UninstallerKind == UninstallerType.WindowsFeature,
            ["store"]     = e => e.UninstallerKind == UninstallerType.StoreApp,
            ["protected"] = e => e.IsProtected,
        };

    public static readonly string[] PresetNames =
        new[] { "basic", "advanced", "everything" }.Concat(OnlyPresets.Keys).ToArray();

    public static List<ApplicationUninstallerEntry> Filter(IList<ApplicationUninstallerEntry> all, CliArgs args)
    {
        var q = all.AsEnumerable();

        if (!string.IsNullOrEmpty(args.Filter))
            q = q.Where(e =>
                (e.DisplayName ?? "").Contains(args.Filter, StringComparison.OrdinalIgnoreCase) ||
                (e.Publisher   ?? "").Contains(args.Filter, StringComparison.OrdinalIgnoreCase));

        bool includeSystem = args.IncludeSystem, includeUpdates = args.IncludeUpdates, includeOrphaned = args.IncludeOrphaned;
        switch (args.Preset)
        {
            case null: case "basic": break;
            case "advanced":   includeOrphaned = true; break;
            case "everything": includeSystem = includeUpdates = includeOrphaned = true; break;
            default:
                if (!OnlyPresets.TryGetValue(args.Preset, out var only))
                    throw new ArgumentException($"Unknown preset \"{args.Preset}\". Valid: {string.Join(", ", PresetNames)}");
                q = q.Where(only);
                includeSystem = includeUpdates = includeOrphaned = true;
                break;
        }

        if (!includeSystem)
            q = q.Where(e => !e.SystemComponent);

        if (!includeUpdates)
            q = q.Where(e => !e.IsUpdate);

        if (!includeOrphaned)
            q = q.Where(e => !e.IsOrphaned);

        if (args.HideMicrosoft)
            q = q.Where(e => !(e.Publisher ?? "").Contains("Microsoft", StringComparison.OrdinalIgnoreCase));

        if (args.OnlyInvalid)
            q = q.Where(e => !e.IsValid);

        if (args.Kinds.Count > 0)
        {
            var kinds = args.Kinds.Select(k => Enum.TryParse<UninstallerType>(k, true, out var t)
                ? t : throw new ArgumentException($"Unknown --kind \"{k}\". Valid: {string.Join(", ", Enum.GetNames<UninstallerType>())}"))
                .ToHashSet();
            q = q.Where(e => kinds.Contains(e.UninstallerKind));
        }

        if (!string.IsNullOrEmpty(args.ListFile))
        {
            var list = UninstallList.ReadFromFile(args.ListFile)
                       ?? throw new ArgumentException($"Could not read uninstall list \"{args.ListFile}\"");
            q = q.Where(e => list.TestEntry(e) == true);
        }

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

        if (!string.IsNullOrWhiteSpace(args.TargetMsiGuid))
        {
            if (!Guid.TryParse(args.TargetMsiGuid, out var guid))
                throw new ArgumentException($"Invalid MSI product code \"{args.TargetMsiGuid}\"");
            return all.Where(e => e.BundleProviderKey == guid).ToList();
        }

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

    /// <summary>True when any single-entry targeting option (name, registry path, rating id, MSI guid) was given.</summary>
    public static bool HasTarget(CliArgs args) =>
        !string.IsNullOrWhiteSpace(args.TargetName) ||
        !string.IsNullOrWhiteSpace(args.TargetRegistryPath) ||
        !string.IsNullOrWhiteSpace(args.TargetRatingId) ||
        !string.IsNullOrWhiteSpace(args.TargetMsiGuid);

    /// <summary>Scan and resolve one target, printing usage when none was given.</summary>
    public static ApplicationUninstallerEntry? ScanAndResolve(CliArgs args, string usage, out IList<ApplicationUninstallerEntry> all, out int exitCode)
    {
        all = Array.Empty<ApplicationUninstallerEntry>();
        if (!HasTarget(args))
        {
            Console.Error.WriteLine("Usage: " + usage);
            exitCode = ExitCodes.BadUsage;
            return null;
        }
        all = ScanWithBanner(args);
        var entry = ResolveSingle(all, args);
        exitCode = entry == null ? ExitCodes.NotFound : ExitCodes.Success;
        return entry;
    }

    public static string RunAsText(RunAsMode runAs) =>
        runAs == RunAsMode.ActiveUser ? "active-user" : "system";

    public static string? UninstallCommand(ApplicationUninstallerEntry entry, CliArgs args) =>
        args.UseQuietUninstall && entry.QuietUninstallPossible
            ? entry.QuietUninstallString
            : entry.UninstallString;
}
