/*
    Copyright (c) 2026 theoneec
    Apache License Version 2.0

    Part of a personal fork of Bulk Crap Uninstaller
    (Marcin Szeniak, https://github.com/Klocman/). Original file authored for
    the merged BCU-console command-line interface.
*/

using System.Text;
using UninstallTools;
using UninstallTools.Junk.Confidence;
using UninstallTools.Junk.Containers;
using UninstallTools.Uninstaller;

namespace BcuCli;

/// <summary>
/// `bcu tui` — an interactive, old-school full-screen terminal UI driven by the
/// keyboard (arrows + space). For hands-on use without RMM. Hand-rolled on
/// System.Console with no external dependencies, so it ships clean in the
/// portable/self-contained build. Refuses to run when stdin/stdout is redirected.
/// </summary>
public static class TuiCommand
{
    private sealed class Row
    {
        public required ApplicationUninstallerEntry Entry;
        public bool Selected;
    }

    private static readonly string[] SortModes = { "name", "publisher", "size", "source", "date" };

    public static int Run(CliArgs args)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("bcu tui needs an interactive terminal (stdin/stdout must not be redirected).");
            Console.Error.WriteLine("Use 'bcu list' for scripting/headless output.");
            return ExitCodes.BadUsage;
        }

        var origFg = Console.ForegroundColor;
        var origBg = Console.BackgroundColor;
        var origCursor = true;
        try { origCursor = Console.CursorVisible; } catch { }

        try
        {
            return RunLoop(args);
        }
        finally
        {
            Console.ResetColor();
            Console.ForegroundColor = origFg;
            Console.BackgroundColor = origBg;
            try { Console.CursorVisible = origCursor; } catch { }
            try { Console.Clear(); } catch { }
        }
    }

    // ── Main loop ─────────────────────────────────────────────────────────────

    private static int RunLoop(CliArgs args)
    {
        Console.Clear();
        ShowBanner();
        Console.WriteLine("\n   Scanning installed applications...");
        var all = LoadRows(args, "name");
        var sortMode = 0;
        var dryRun = false;
        var filter = string.Empty;
        var searching = false;
        var status = "Loaded. Press ? for help.";
        var cursor = 0;
        var top = 0;

        List<Row> View() => string.IsNullOrEmpty(filter)
            ? all
            : all.Where(r => Match(r.Entry, filter)).ToList();

        try { Console.CursorVisible = false; } catch { }

        while (true)
        {
            var view = View();
            if (cursor >= view.Count) cursor = Math.Max(0, view.Count - 1);
            if (cursor < 0) cursor = 0;

            var width = Math.Max(40, Console.WindowWidth);
            var height = Math.Max(10, Console.WindowHeight);
            var listRows = height - 4;            // 2 header + 2 footer
            if (cursor < top) top = cursor;
            if (cursor >= top + listRows) top = cursor - listRows + 1;
            if (top < 0) top = 0;

            Render(view, all, cursor, top, listRows, width, filter, searching, dryRun, sortMode, status);

            var key = Console.ReadKey(true);

            if (searching)
            {
                if (key.Key == ConsoleKey.Enter) { searching = false; status = $"Filter: \"{filter}\""; }
                else if (key.Key == ConsoleKey.Escape) { searching = false; filter = string.Empty; status = "Filter cleared."; }
                else if (key.Key == ConsoleKey.Backspace) { if (filter.Length > 0) filter = filter[..^1]; cursor = 0; top = 0; }
                else if (key.Key is ConsoleKey.UpArrow) cursor--;
                else if (key.Key is ConsoleKey.DownArrow) cursor++;
                else if (!char.IsControl(key.KeyChar)) { filter += key.KeyChar; cursor = 0; top = 0; }
                continue;
            }

            switch (key.Key)
            {
                case ConsoleKey.Q:
                case ConsoleKey.Escape:
                    return ExitCodes.Success;

                case ConsoleKey.UpArrow: case ConsoleKey.K: cursor--; break;
                case ConsoleKey.DownArrow: case ConsoleKey.J: cursor++; break;
                case ConsoleKey.PageUp: cursor -= listRows; break;
                case ConsoleKey.PageDown: cursor += listRows; break;
                case ConsoleKey.Home: cursor = 0; break;
                case ConsoleKey.End: cursor = view.Count - 1; break;

                case ConsoleKey.Spacebar:
                    if (view.Count > 0) view[cursor].Selected = !view[cursor].Selected;
                    cursor++;
                    break;

                case ConsoleKey.A:
                    if ((key.Modifiers & ConsoleModifiers.Shift) != 0)
                    { foreach (var r in all) r.Selected = false; status = "Selection cleared."; }
                    else
                    { foreach (var r in view) r.Selected = true; status = $"Selected {view.Count} filtered item(s)."; }
                    break;

                case ConsoleKey.Oem2:   // '/'
                case ConsoleKey.S:
                    searching = true;
                    break;

                case ConsoleKey.Enter:
                    if (view.Count > 0) ShowDetails(view[cursor].Entry, dryRun);
                    break;

                case ConsoleKey.D:
                    dryRun = !dryRun;
                    status = dryRun ? "DRY-RUN on: 'u' will only simulate." : "DRY-RUN off: 'u' will really uninstall.";
                    break;

                case ConsoleKey.O:
                    sortMode = (sortMode + 1) % SortModes.Length;
                    Resort(all, SortModes[sortMode]);
                    status = $"Sorted by {SortModes[sortMode]}.";
                    break;

                case ConsoleKey.R:
                    Console.Clear();
                    Console.WriteLine("Rescanning...");
                    var sel = new HashSet<string?>(all.Where(r => r.Selected).Select(r => r.Entry.RegistryPath));
                    all = LoadRows(args, SortModes[sortMode]);
                    foreach (var r in all) if (r.Entry.RegistryPath != null && sel.Contains(r.Entry.RegistryPath)) r.Selected = true;
                    status = "Rescanned.";
                    break;

                case ConsoleKey.C:
                    status = DoJunk(view, cursor, all, dryRun);
                    break;

                case ConsoleKey.U:
                    status = DoUninstall(view, cursor, dryRun, args);
                    if (!dryRun)
                    {
                        // Show the scan banner before the (slow) rescan, otherwise the
                        // post-uninstall summary stays on screen and looks frozen.
                        Console.ResetColor();
                        Console.Clear();
                        Console.WriteLine("Rescanning installed applications...");
                        all = LoadRows(args, SortModes[sortMode]);   // refresh after real uninstall
                        cursor = 0; top = 0;
                        status = "Rescanned after uninstall.";
                    }
                    break;

                case ConsoleKey.F1:
                    ShowHelp();
                    break;
            }

            // '?' arrives as a char rather than a dedicated key on most layouts.
            if (key.KeyChar == '?') ShowHelp();
        }
    }

    // ── Rendering ───────────────────────────────────────────────────────────────

    private static void Render(List<Row> view, List<Row> all, int cursor, int top, int listRows,
        int width, string filter, bool searching, bool dryRun, int sortMode, string status)
    {
        // Draw each line directly (no Console.Clear per frame -> minimal flicker).
        // Every line is padded to full width so the previous frame is fully overwritten.
        var selCount = all.Count(r => r.Selected);
        var dry = dryRun ? "  DRY-RUN" : "";
        var header = $" bcu  ·  {view.Count}/{all.Count} apps  ·  {selCount} selected  ·  sort:{SortModes[sortMode]}{dry}";
        WriteAt(0, 0, header, ConsoleColor.Black, ConsoleColor.Cyan);

        var filterLine = searching
            ? $" Search: {filter}_"
            : (string.IsNullOrEmpty(filter) ? " (no filter — press / to search)" : $" filter: \"{filter}\"  (Esc to clear)");
        WriteAt(0, 1, filterLine, ConsoleColor.Gray, ConsoleColor.Black);

        // marker(4) + name + publisher + source
        var rest = width - 4;
        var nameW = Math.Max(12, (int)(rest * 0.50));
        var pubW = Math.Max(8, (int)(rest * 0.28));
        var srcW = Math.Max(6, rest - nameW - pubW);

        for (var i = 0; i < listRows; i++)
        {
            var rowY = 2 + i;
            var idx = top + i;
            if (idx >= view.Count) { WriteAt(0, rowY, "", ConsoleColor.Gray, ConsoleColor.Black); continue; }
            var r = view[idx];
            var mark = r.Selected ? "[x] " : "[ ] ";
            var line = mark
                       + Cell(r.Entry.DisplayName, nameW) + " "
                       + Cell(r.Entry.Publisher, pubW) + " "
                       + Cell(r.Entry.UninstallerKind.ToString(), srcW);
            var isCursor = idx == cursor;
            var fg = isCursor ? ConsoleColor.Black : (r.Selected ? ConsoleColor.Yellow : ConsoleColor.Gray);
            var bg = isCursor ? ConsoleColor.White : ConsoleColor.Black;
            WriteAt(0, rowY, line, fg, bg);
        }

        var hint = " ↑↓/jk move · space select · / search · enter info · u uninstall · c clean-junk · d dry-run · o sort · r rescan · ? help · q quit";
        WriteAt(0, listRows + 2, hint, ConsoleColor.Black, ConsoleColor.DarkGray);
        WriteAt(0, listRows + 3, " " + status, ConsoleColor.Green, ConsoleColor.Black);
    }

    /// <summary>Write a full-width colored line at (x,y), padded/truncated to the window width.</summary>
    private static void WriteAt(int x, int y, string text, ConsoleColor fg, ConsoleColor bg)
    {
        try
        {
            if (y >= Console.WindowHeight) return;
            // Never paint the bottom-right-most cell: writing it advances the cursor and
            // scrolls the whole screen. Leave the last column on the last row untouched.
            var lastRow = y >= Console.WindowHeight - 1;
            var max = Console.WindowWidth - x - (lastRow ? 1 : 0);
            if (max <= 0) return;
            Console.SetCursorPosition(x, y);
            Console.ForegroundColor = fg;
            Console.BackgroundColor = bg;
            Console.Write(text.Length > max ? text[..max] : text.PadRight(max));
            Console.ResetColor();
        }
        catch { /* terminal resized mid-draw; next frame fixes it */ }
    }

    private static string Cell(string? s, int w) => Trunc(s ?? "", w).PadRight(w);

    private static string Trunc(string s, int w)
    {
        if (w <= 0) return "";
        if (s.Length <= w) return s;
        return w <= 1 ? s[..w] : s[..(w - 1)] + "…";
    }

    // ── Sub-screens ─────────────────────────────────────────────────────────────

    private static void ShowDetails(ApplicationUninstallerEntry e, bool dryRun)
    {
        Console.ResetColor();
        Console.Clear();
        Console.WriteLine("=== Application details ===\n");
        void L(string k, object? v) => Console.WriteLine($"  {k,-18}{v}");
        L("Name", e.DisplayName);
        L("Version", e.DisplayVersion);
        L("Publisher", e.Publisher);
        L("Source", e.UninstallerKind);
        L("Install date", e.InstallDate == DateTime.MinValue ? "" : e.InstallDate.ToString("yyyy-MM-dd"));
        L("Size", e.EstimatedSize.GetKbSize() > 0 ? e.EstimatedSize.ToString() : "");
        L("Location", e.InstallLocation);
        L("Registry path", e.RegistryPath);
        L("Rating id", e.RatingId);
        L("Quiet possible", e.QuietUninstallPossible);
        Console.WriteLine();
        L("Uninstall", e.UninstallString);
        L("Quiet uninstall", e.QuietUninstallString);
        Console.WriteLine("\n\nPress any key to return...");
        Console.ReadKey(true);
    }

    private static void ShowHelp()
    {
        Console.ResetColor();
        Console.Clear();
        Console.WriteLine("""
            === bcu tui — keys ===

              ↑ ↓ / j k        Move cursor
              PgUp / PgDn      Page up / down
              Home / End       Jump to top / bottom
              Space            Toggle-select the highlighted app
              a                Select all (current filter)
              A (Shift+a)      Clear all selections
              / or s           Search/filter (type to filter live; Esc clears)
              Enter            Show details for the highlighted app
              u                Uninstall selected (or highlighted) apps — asks to confirm
              c                Scan/clean leftover junk for selected (or highlighted) apps
              d                Toggle DRY-RUN (u and c only simulate)
              o                Cycle sort (name / publisher / size / source / date)
              r                Rescan installed apps
              ? or F1          This help
              q or Esc         Quit

            Safety: 'u' always shows a confirmation screen first. With DRY-RUN on,
            it simulates and changes nothing.

            Press any key to return...
            """);
        Console.ReadKey(true);
    }

    private static void ShowBanner()
    {
        // Old-school block-letter splash (ANSI Shadow). Console.OutputEncoding is UTF-8
        // (set in Program.cs) so the block/box-drawing glyphs render.
        string[] art =
        {
            @"   ██████╗  ██████╗██╗   ██╗",
            @"   ██╔══██╗██╔════╝██║   ██║",
            @"   ██████╔╝██║     ██║   ██║",
            @"   ██╔══██╗██║     ██║   ██║",
            @"   ██████╔╝╚██████╗╚██████╔╝",
            @"   ╚═════╝  ╚═════╝ ╚═════╝ ",
        };
        var v = typeof(TuiCommand).Assembly.GetName().Version;
        var ver = v == null ? "" : $"v{v.Major}.{v.Minor}.{v.Build}";

        var prevFg = Console.ForegroundColor;
        Console.WriteLine();
        // simple top-to-bottom shade for a little retro gradient
        var shades = new[] { ConsoleColor.Cyan, ConsoleColor.Cyan, ConsoleColor.DarkCyan, ConsoleColor.DarkCyan, ConsoleColor.Blue, ConsoleColor.Blue };
        for (var i = 0; i < art.Length; i++)
        {
            Console.ForegroundColor = shades[i % shades.Length];
            Console.WriteLine(art[i]);
        }
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"      B u l k   C r a p   U n i n s t a l l e r  ·  CLI  {ver}");
        Console.ForegroundColor = prevFg;
    }

    private static string DoJunk(List<Row> view, int cursor, List<Row> all, bool dryRun)
    {
        var targets = all.Where(r => r.Selected).Select(r => r.Entry).ToList();
        if (targets.Count == 0 && view.Count > 0) targets.Add(view[cursor].Entry);
        if (targets.Count == 0) return "No target for junk scan.";

        Console.ResetColor();
        Console.Clear();
        Console.WriteLine($"Scanning leftover junk for {targets.Count} app(s)...\n");
        List<IJunkResult> junk;
        try
        {
            junk = JunkService.Scan(targets, all.Select(r => r.Entry).ToList(), ConfidenceLevel.Good,
                msg => Console.Write($"\r  {msg,-60}"));
        }
        catch (Exception ex) { return "Junk scan failed: " + ex.Message; }
        Console.WriteLine();

        if (junk.Count == 0) { Console.WriteLine("\nNo junk found (Good+). Press any key..."); Console.ReadKey(true); return "No junk found."; }

        Console.WriteLine($"\nFound {junk.Count} junk item(s) (confidence Good+):\n");
        foreach (var level in new[] { ConfidenceLevel.VeryGood, ConfidenceLevel.Good })
        {
            var group = junk.Where(j => j.Confidence.GetConfidence() == level).ToList();
            if (group.Count == 0) continue;
            Console.WriteLine($"  [{level}]  ({group.Count})");
            foreach (var j in group.Take(40))
                Console.WriteLine($"    {j.Source?.CategoryName ?? "?"} — {j.GetDisplayName()}");
            if (group.Count > 40) Console.WriteLine($"    ... and {group.Count - 40} more");
        }

        if (dryRun)
        {
            Console.WriteLine($"\nDRY-RUN: would delete {junk.Count} item(s) (files -> Recycle Bin, registry permanent). Press any key...");
            Console.ReadKey(true);
            return $"Dry-run: {junk.Count} junk item(s).";
        }

        Console.Write($"\nDelete these {junk.Count} item(s)? [y/N] ");
        if (Console.ReadKey(true).Key != ConsoleKey.Y) return "Junk cleanup cancelled.";

        Console.WriteLine("\n\nCleaning...");
        var res = JunkService.Clean(junk, null, (j, ok, err) =>
        {
            if (!ok) Console.WriteLine($"  failed: {j.GetDisplayName()} — {err}");
        });
        Console.WriteLine($"\nDone. deleted={res.Deleted} failed={res.Failed}. Press any key...");
        Console.ReadKey(true);
        return $"Junk: deleted={res.Deleted} failed={res.Failed}.";
    }

    private static string DoUninstall(List<Row> view, int cursor, bool dryRun, CliArgs args)
    {
        var targets = view.Where(r => r.Selected).Select(r => r.Entry).ToList();
        if (targets.Count == 0 && view.Count > 0) targets.Add(view[cursor].Entry);
        if (targets.Count == 0) return "Nothing to uninstall.";

        Console.ResetColor();
        Console.Clear();
        Console.WriteLine(dryRun ? "=== Uninstall (DRY-RUN — nothing will change) ===\n" : "=== Uninstall ===\n");
        Console.WriteLine($"{targets.Count} application(s) will be {(dryRun ? "simulated" : "PERMANENTLY uninstalled")}:\n");
        foreach (var t in targets) Console.WriteLine($"  • {t.DisplayName}  [{t.UninstallerKind}]");
        Console.Write($"\nProceed? [y/N] ");
        var k = Console.ReadKey(true);
        if (k.Key != ConsoleKey.Y) return "Uninstall cancelled.";

        Console.WriteLine("\n");
        int ok = 0, fail = 0;
        foreach (var t in targets)
        {
            Console.Write($"  {(dryRun ? "simulate" : "uninstall")}: {t.DisplayName} ... ");
            try
            {
                var proc = t.RunUninstaller(t.QuietUninstallPossible, dryRun, false);
                proc?.WaitForExit();
                var code = proc?.ExitCode ?? 0;
                if (code == 0) { ok++; Console.WriteLine("ok"); }
                else { fail++; Console.WriteLine($"exit {code}"); }
            }
            catch (Exception ex) { fail++; Console.WriteLine($"error: {ex.Message}"); }
        }
        Console.WriteLine($"\nDone. ok={ok} failed={fail}. Press any key to return...");
        Console.ReadKey(true);
        return dryRun ? $"Dry-run complete ({targets.Count})." : $"Uninstall done: ok={ok} failed={fail}.";
    }

    // ── Data ────────────────────────────────────────────────────────────────────

    private static List<Row> LoadRows(CliArgs args, string sortBy)
    {
        var scanArgs = new CliArgs
        {
            Quiet = true,
            RmmSafe = args.RmmSafe,
            ScanRegistry = args.ScanRegistry, ScanDrives = args.ScanDrives, ScanSteam = args.ScanSteam,
            ScanStore = args.ScanStore, ScanChoco = args.ScanChoco, ScanScoop = args.ScanScoop,
            ScanFeatures = args.ScanFeatures, ScanUpdates = args.ScanUpdates, ScanOculus = args.ScanOculus,
            IncludeSystem = args.IncludeSystem, IncludeUpdates = args.IncludeUpdates, IncludeOrphaned = args.IncludeOrphaned,
            SortBy = sortBy, SourceTimeout = args.SourceTimeout,
        };
        var scan = Engine.Scan(scanArgs);
        var ordered = Engine.Sort(Engine.Filter(scan, scanArgs), scanArgs);
        return ordered.Select(e => new Row { Entry = e }).ToList();
    }

    private static void Resort(List<Row> rows, string sortBy)
    {
        var sortArgs = new CliArgs { SortBy = sortBy };
        var ordered = Engine.Sort(rows.Select(r => r.Entry).ToList(), sortArgs);
        var bySel = rows.ToDictionary(r => r.Entry, r => r.Selected);
        rows.Clear();
        rows.AddRange(ordered.Select(e => new Row { Entry = e, Selected = bySel.TryGetValue(e, out var s) && s }));
    }

    private static bool Match(ApplicationUninstallerEntry e, string filter) =>
        (e.DisplayName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (e.Publisher?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);
}
