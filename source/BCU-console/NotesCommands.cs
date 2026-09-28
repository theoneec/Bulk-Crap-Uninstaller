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

namespace BcuCli;

/// <summary>
/// Custom notes (upstream v6.3 feature, <see cref="CustomNotesManager"/>): free-text notes
/// attached to an uninstaller entry and shown in the GUI's "Custom note" column.
///   bcu notes list [--format json]
///   bcu notes get   &lt;target&gt;
///   bcu notes set   &lt;target&gt; &lt;text&gt; [--yes]
///   bcu notes clear &lt;target&gt; [--yes]
/// Notes are stored in CustomNotes.xml next to the executable, shared with the GUI.
/// </summary>
public static class NotesCommands
{
    public static int Run(CliArgs args)
    {
        return (args.SubAction ?? "list") switch
        {
            "list"  => List(args),
            "get"   => Get(args),
            "set"   => Set(args, args.Value),
            "clear" => Set(args, null),
            _       => Usage()
        };
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: bcu notes list|get|set|clear [<target>] [<text>] [--yes]");
        return ExitCodes.BadUsage;
    }

    private static int List(CliArgs args)
    {
        var all = Engine.ScanWithBanner(args);
        var noted = Engine.Sort(all.Where(e => !string.IsNullOrEmpty(e.CustomNote)).ToList(), args);

        if (args.Format == OutputFormat.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(noted.Select(e => new
            {
                name = e.DisplayName,
                registryPath = e.RegistryPath,
                ratingId = e.RatingId,
                note = e.CustomNote
            }), new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }));
        }
        else
        {
            foreach (var e in noted)
                Console.WriteLine($"{Output.T(e.DisplayName ?? "", 45),-45}  {e.CustomNote}");
        }

        if (!args.Quiet)
            Console.Error.WriteLine($"\n{noted.Count} note(s) on installed apps; store: {CustomNotesManager.NotesFile}");
        return ExitCodes.Success;
    }

    private static int Get(CliArgs args)
    {
        if (!Engine.HasTarget(args)) return Usage();
        var entry = Engine.ResolveSingle(Engine.ScanWithBanner(args), args);
        if (entry == null) return ExitCodes.NotFound;
        Console.WriteLine(entry.CustomNote);
        return ExitCodes.Success;
    }

    private static int Set(CliArgs args, string? note)
    {
        if (!Engine.HasTarget(args)) return Usage();
        if (note == null && args.SubAction == "set")
        {
            Console.Error.WriteLine("Missing note text: bcu notes set <target> <text> [--yes]");
            return ExitCodes.BadUsage;
        }

        var entry = Engine.ResolveSingle(Engine.ScanWithBanner(args), args);
        if (entry == null) return ExitCodes.NotFound;

        Console.WriteLine($"Entry:    {entry.DisplayName}");
        Console.WriteLine($"Current:  {(string.IsNullOrEmpty(entry.CustomNote) ? "(none)" : entry.CustomNote)}");
        Console.WriteLine($"New:      {(string.IsNullOrEmpty(note) ? "(cleared)" : note)}");

        if (!args.WillExecute)
        {
            Console.WriteLine("\nDRY RUN: re-run with --yes to save the note.");
            return ExitCodes.Success;
        }

        if (!CustomNotesManager.TrySetNote(entry.GetCacheId(), note ?? "", out var error))
        {
            Console.Error.WriteLine($"Failed to save note: {error}");
            return ExitCodes.Error;
        }
        Console.WriteLine("Saved.");
        return ExitCodes.Success;
    }
}
