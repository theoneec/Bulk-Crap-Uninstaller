using Klocman.Tools;
using UninstallTools;
using UninstallTools.Uninstaller;

namespace BcuCli;

/// <summary>
/// Single-entry actions that mirror the GUI's right-click menu: repair, modify/change,
/// rename, delete-registry-entry, and a detailed properties dump.
/// All state-changing actions obey the safety model (execute only with --yes, else dry-run).
/// </summary>
public static class EntryActions
{
    // ── Modify / Change ─────────────────────────────────────────────────────────
    public static int RunModify(CliArgs args) => RunModifyOrRepair(args, "Modify");

    // ── Repair ──────────────────────────────────────────────────────────────────
    // BCU's engine exposes a single "modify/change" mechanism. For MSI packages this
    // opens msiexec maintenance mode (which is also where repair lives); for others it
    // runs the app's ModifyPath. There is no separate engine-level "repair" verb.
    public static int RunRepair(CliArgs args) => RunModifyOrRepair(args, "Repair");

    private static int RunModifyOrRepair(CliArgs args, string verb)
    {
        if (string.IsNullOrWhiteSpace(args.TargetName) &&
            string.IsNullOrWhiteSpace(args.TargetRegistryPath) &&
            string.IsNullOrWhiteSpace(args.TargetRatingId))
        {
            Console.Error.WriteLine($"Usage: bcu {verb.ToLowerInvariant()} <name>|--registry-path <path> [--yes]");
            return 1;
        }

        var all = Engine.ScanWithBanner(args);
        var entry = Engine.ResolveSingle(all, args);
        if (entry == null) return 1;

        bool isMsi = entry.BundleProviderKey != Guid.Empty;
        bool hasModify = !string.IsNullOrEmpty(entry.ModifyPath);

        Console.WriteLine($"Found:    {entry.DisplayName}  {entry.DisplayVersion}");
        Console.WriteLine($"Type:     {entry.UninstallerKind}");
        if (isMsi)
            Console.WriteLine($"Action:   msiexec /I {entry.BundleProviderKey:B}  (maintenance/repair dialog)");
        else if (hasModify)
            Console.WriteLine($"Action:   {entry.ModifyPath}");
        else
        {
            Console.Error.WriteLine($"This entry has no modify/repair mechanism (no MSI product code and no ModifyPath).");
            return 1;
        }

        if (!args.WillExecute)
        {
            Console.WriteLine($"\nDRY RUN: re-run with --yes to {verb.ToLowerInvariant()}.");
            return 0;
        }

        Console.WriteLine($"\n{verb}ing...");
        try
        {
            int code = isMsi
                ? entry.UninstallUsingMsi(MsiUninstallModes.InstallModify, false)
                : entry.Modify(false);
            Console.WriteLine(code == 0 ? $"{verb} completed (exit 0)." : $"{verb} process exited with code {code}.");
            return code == 0 ? 0 : code;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{verb} error: {ex.Message}");
            return 1;
        }
    }

    // ── Rename ──────────────────────────────────────────────────────────────────
    public static int RunRename(CliArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.TargetName) && string.IsNullOrWhiteSpace(args.TargetRegistryPath))
        {
            Console.Error.WriteLine("Usage: bcu rename <name>|--registry-path <path> <new-name> [--yes]");
            Console.Error.WriteLine("       (or use --new-name <new-name>)");
            return 1;
        }
        if (string.IsNullOrWhiteSpace(args.NewName))
        {
            Console.Error.WriteLine("Missing new name. Provide it as the second argument or via --new-name.");
            return 1;
        }

        var all = Engine.ScanWithBanner(args);
        var entry = Engine.ResolveSingle(all, args);
        if (entry == null) return 1;

        Console.WriteLine($"Found:    {entry.DisplayName}");
        Console.WriteLine($"Rename → {args.NewName}");
        Console.WriteLine($"Reg key:  {entry.RegistryPath ?? "(none)"}");

        if (!entry.IsRegistered || string.IsNullOrEmpty(entry.RegistryPath))
        {
            Console.Error.WriteLine("This entry is not backed by a writable registry key; rename is not possible.");
            return 1;
        }

        if (!args.WillExecute)
        {
            Console.WriteLine("\nDRY RUN: re-run with --yes to rename.");
            return 0;
        }

        try
        {
            var ok = entry.Rename(args.NewName);
            Console.WriteLine(ok ? "Renamed." : "Rename failed (invalid name or registry write error).");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Rename error: {ex.Message}");
            return 1;
        }
    }

    // ── Delete registry entry only (no uninstall) ────────────────────────────────
    public static int RunDeleteEntry(CliArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.TargetName) &&
            string.IsNullOrWhiteSpace(args.TargetRegistryPath) &&
            string.IsNullOrWhiteSpace(args.TargetRatingId))
        {
            Console.Error.WriteLine("Usage: bcu delete-entry <name>|--registry-path <path> [--yes]");
            return 1;
        }

        var all = Engine.ScanWithBanner(args);
        var entry = Engine.ResolveSingle(all, args);
        if (entry == null) return 1;

        Console.WriteLine($"Found:    {entry.DisplayName}  {entry.DisplayVersion}");
        Console.WriteLine($"Reg key:  {entry.RegistryPath ?? "(none)"}");
        Console.WriteLine("This removes the registry uninstall entry ONLY. Installed files are left in place.");
        if (entry.IsProtected)
            Console.WriteLine("WARNING: this entry is marked PROTECTED.");

        if (!entry.IsRegistered || string.IsNullOrEmpty(entry.RegistryPath))
        {
            Console.Error.WriteLine("This entry is not registered in the registry; nothing to delete.");
            return 1;
        }

        if (!args.WillExecute)
        {
            Console.WriteLine("\nDRY RUN: re-run with --yes to delete the registry entry.");
            return 0;
        }

        try
        {
            RegistryTools.RemoveRegistryKey(entry.RegistryPath);
            Console.WriteLine("Registry entry deleted.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Delete error: {ex.Message}");
            return 1;
        }
    }

    // ── Info / properties dump ────────────────────────────────────────────────────
    public static int RunInfo(CliArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.TargetName) &&
            string.IsNullOrWhiteSpace(args.TargetRegistryPath) &&
            string.IsNullOrWhiteSpace(args.TargetRatingId))
        {
            Console.Error.WriteLine("Usage: bcu info <name>|--registry-path <path>");
            return 1;
        }

        var all = Engine.ScanWithBanner(args);
        var entry = Engine.ResolveSingle(all, args);
        if (entry == null) return 1;

        // Associate startup entries so they show up in the dump.
        try
        {
            UninstallTools.Startup.StartupManager.AssignStartupEntries(
                all, UninstallTools.Startup.StartupManager.GetAllStartupItems());
        }
        catch { /* startup enumeration is best-effort */ }

        Console.WriteLine(entry.ToLongString());
        Console.WriteLine();
        Console.WriteLine($"UninstallerKind:        {entry.UninstallerKind}");
        Console.WriteLine($"RegistryPath:           {entry.RegistryPath ?? "(none)"}");
        Console.WriteLine($"RatingId:               {entry.RatingId ?? "(none)"}");
        Console.WriteLine($"ModifyPath:             {entry.ModifyPath ?? "(none)"}");
        Console.WriteLine($"QuietUninstallPossible: {entry.QuietUninstallPossible}");
        Console.WriteLine($"IsProtected:            {entry.IsProtected}");
        Console.WriteLine($"IsOrphaned:             {entry.IsOrphaned}");
        Console.WriteLine($"IsUpdate:               {entry.IsUpdate}");
        if (entry.BundleProviderKey != Guid.Empty)
            Console.WriteLine($"MSI ProductCode:        {entry.BundleProviderKey:B}");

        // Certificate / digital signature.
        try
        {
            var cert = entry.GetCertificate();
            if (cert == null)
                Console.WriteLine("Certificate:            (unsigned / not found)");
            else
            {
                var valid = entry.IsCertificateValid(false);
                Console.WriteLine("Certificate:");
                Console.WriteLine($"  Subject:   {cert.Subject}");
                Console.WriteLine($"  Issuer:    {cert.Issuer}");
                Console.WriteLine($"  Valid:     {(valid == null ? "unknown" : valid.Value ? "yes" : "no")}");
                Console.WriteLine($"  NotBefore: {cert.NotBefore:yyyy-MM-dd}  NotAfter: {cert.NotAfter:yyyy-MM-dd}");
                Console.WriteLine($"  Thumbprint:{cert.Thumbprint}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Certificate:            (error: {ex.Message})");
        }

        if (entry.StartupEntries != null && entry.StartupEntries.Any())
        {
            Console.WriteLine("Startup entries:");
            foreach (var s in entry.StartupEntries)
                Console.WriteLine($"  {(s.Disabled ? "[disabled] " : "")}{s}");
        }

        return 0;
    }
}
