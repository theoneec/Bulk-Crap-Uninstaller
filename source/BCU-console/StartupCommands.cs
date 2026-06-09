/*
    Copyright (c) 2026 theoneec
    Apache License Version 2.0

    Part of a personal fork of Bulk Crap Uninstaller
    (Marcin Szeniak, https://github.com/Klocman/). Original file authored for
    the merged BCU-console command-line interface.
*/
using System.Text.Json;
using System.Text.Json.Serialization;
using UninstallTools.Startup;

namespace BcuCli;

/// <summary>
/// Startup manager: list every autostart entry on the system and enable/disable them
/// without deleting (mirrors the GUI's Startup Manager window).
///   bcu startup list [--format json]
///   bcu startup disable &lt;match&gt; [--yes]
///   bcu startup enable  &lt;match&gt; [--yes]
/// </summary>
public static class StartupCommands
{
    public static int Run(CliArgs args)
    {
        var action = args.StartupAction ?? "list";
        return action switch
        {
            "list"    => List(args),
            "enable"  => Toggle(args, enable: true),
            "disable" => Toggle(args, enable: false),
            _ => Usage()
        };
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: bcu startup list|enable|disable [<match>] [--format json] [--yes]");
        return ExitCodes.BadUsage;
    }

    private static List<StartupEntryBase> GetAll(CliArgs args)
    {
        if (!args.Quiet) Console.Error.WriteLine("Enumerating startup entries...");
        return StartupManager.GetAllStartupItems().ToList();
    }

    private static int List(CliArgs args)
    {
        var items = GetAll(args)
            .OrderBy(s => s.ParentShortName ?? "")
            .ThenBy(s => s.ProgramName ?? s.Command ?? "")
            .ToList();

        if (args.Format == OutputFormat.Json)
        {
            var records = items.Select(s => new
            {
                programName = s.ProgramName,
                company = s.Company,
                command = s.Command,
                commandFilePath = s.CommandFilePath,
                location = s.ParentShortName,
                fullName = s.FullLongName,
                disabled = s.Disabled
            });
            Console.WriteLine(JsonSerializer.Serialize(records, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            }));
        }
        else
        {
            const int nW = 38, locW = 16, cmpW = 24;
            var hdr = $"{"Program",-nW}  {"Location",-locW}  {"Company",-cmpW}  State";
            Console.WriteLine(hdr);
            Console.WriteLine(new string('-', hdr.Length + 8));
            foreach (var s in items)
                Console.WriteLine(
                    $"{Output.T(s.ProgramName ?? s.Command ?? "?", nW),-nW}  " +
                    $"{Output.T(s.ParentShortName ?? "", locW),-locW}  " +
                    $"{Output.T(s.Company ?? "", cmpW),-cmpW}  " +
                    $"{(s.Disabled ? "disabled" : "enabled")}");
        }

        if (!args.Quiet) Console.Error.WriteLine($"\nTotal: {items.Count} startup entr(ies)");
        return ExitCodes.Success;
    }

    private static int Toggle(CliArgs args, bool enable)
    {
        if (string.IsNullOrWhiteSpace(args.TargetName))
        {
            Console.Error.WriteLine($"Usage: bcu startup {(enable ? "enable" : "disable")} <match> [--yes]");
            return ExitCodes.BadUsage;
        }

        var match = args.TargetName;
        var matched = GetAll(args)
            .Where(s =>
                (s.ProgramName ?? "").Contains(match, StringComparison.OrdinalIgnoreCase) ||
                (s.Command ?? "").Contains(match, StringComparison.OrdinalIgnoreCase) ||
                (s.CommandFilePath ?? "").Contains(match, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matched.Count == 0)
        {
            Console.Error.WriteLine($"No startup entry matching \"{match}\".");
            return ExitCodes.NotFound;
        }

        var verb = enable ? "enable" : "disable";
        Console.WriteLine($"Matched {matched.Count} startup entr(ies) to {verb}:");
        foreach (var s in matched)
            Console.WriteLine($"  [{(s.Disabled ? "disabled" : "enabled")}] {s.ProgramName ?? s.Command}  ({s.ParentShortName})");

        if (!args.WillExecute)
        {
            Console.WriteLine($"\nDRY RUN: re-run with --yes to {verb}.");
            return ExitCodes.Success;
        }

        int changed = 0, failed = 0;
        foreach (var s in matched)
        {
            try
            {
                s.Disabled = !enable;   // disable => true, enable => false
                changed++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  Failed [{s.ProgramName ?? s.Command}]: {ex.Message}");
                failed++;
            }
        }

        Console.WriteLine($"Done. {verb}d: {changed}  Failed: {failed}");
        return failed > 0 ? ExitCodes.PartialFailure : ExitCodes.Success;
    }
}
