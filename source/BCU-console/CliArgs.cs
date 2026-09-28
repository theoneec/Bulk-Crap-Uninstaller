/*
    Copyright (c) 2026 theoneec
    Apache License Version 2.0

    Part of a personal fork of Bulk Crap Uninstaller
    (Marcin Szeniak, https://github.com/Klocman/). Original file authored for
    the merged BCU-console command-line interface.
*/
using UninstallTools.Junk.Confidence;

namespace BcuCli;

public enum Command
{
    List, Export, Uninstall, Junk, Help,
    Bulk, Repair, Modify, Rename, DeleteEntry, Startup, Info, ImportList, Serve, Tui,
    // GUI-parity additions
    Notes, Msi, UninstallDir, Target, ManualUninstall, CleanProgramFiles, RestorePoint,
    RegBackup, Open, SearchOnline, Run, TakeOwnership, MakeList, Tools
}

public enum OutputFormat { Table, Json, Csv, Xml, Bat, Ps1, StorePs1 }
public enum RunAsMode { System, ActiveUser }

public class CliArgs
{
    public Command Command { get; set; } = Command.List;

    // ── Shared ────────────────────────────────────────────────────────────────
    public OutputFormat Format   { get; set; } = OutputFormat.Table;
    public string?      Filter   { get; set; }
    public string       SortBy   { get; set; } = "name";
    public bool         Wide     { get; set; }
    public bool         Quiet    { get; set; }
    public bool         JsonErrors { get; set; }
    public string?      OutputFile { get; set; }
    public string?      PipeName   { get; set; }   // bcu serve --pipe <name>
    public string?      BackupDir  { get; set; }   // junk --backup <dir>: back up each item before deleting
    public RunAsMode    RunAs { get; set; } = RunAsMode.System;
    public bool         RmmSafe { get; set; }
    public bool         VerifyCerts { get; set; }

    /// <summary>Max time to wait for any detection helper EXE before killing it. Zero = no timeout.</summary>
    public TimeSpan     SourceTimeout { get; set; } = TimeSpan.FromSeconds(120);

    // ── Inclusion ─────────────────────────────────────────────────────────────
    public bool IncludeSystem   { get; set; }
    public bool IncludeUpdates  { get; set; }
    public bool IncludeOrphaned { get; set; }

    // ── Sources ───────────────────────────────────────────────────────────────
    public bool ScanRegistry { get; set; } = true;
    public bool ScanDrives   { get; set; }
    public bool ScanSteam    { get; set; } = true;
    public bool ScanStore    { get; set; } = true;
    public bool ScanChoco    { get; set; } = true;
    public bool ScanScoop    { get; set; } = true;
    public bool ScanFeatures { get; set; } = true;
    public bool ScanUpdates  { get; set; } = true;
    public bool ScanOculus   { get; set; }

    // ── Targeting (uninstall / junk / actions) ─────────────────────────────────
    public string?         TargetName       { get; set; }
    public List<string>    Targets          { get; } = new();   // bulk uninstall: many names
    public string?         TargetRegistryPath { get; set; }
    public string?         TargetRatingId   { get; set; }
    public string?         NewName          { get; set; }       // rename: second positional
    public string?         ImportFile       { get; set; }       // import-list: path
    public string?         TargetMsiGuid    { get; set; }       // --msi-guid: target by MSI product code

    /// <summary>First positional of sub-command style commands (startup/notes/tools/...).</summary>
    public string?         SubAction        { get; set; }
    /// <summary>Extra free-text positional (notes set: text, restore-point: description, target: what).</summary>
    public string?         Value            { get; set; }
    public List<string>    Positionals      { get; } = new();

    /// <summary>Backwards-compatible name for <see cref="SubAction"/> as used by `startup`.</summary>
    public string?         StartupAction    { get => SubAction; set => SubAction = value; }
    public bool            ExactMatch       { get; set; }
    public bool            UseQuietUninstall{ get; set; }
    public bool            Yes              { get; set; }
    public bool            DryRun           { get; set; }
    public bool            RunJunk          { get; set; }
    public ConfidenceLevel JunkLevel        { get; set; } = ConfidenceLevel.Good;

    // ── Bulk / uninstall behaviour ──────────────────────────────────────────────
    public bool Bulk            { get; set; }
    public int  Concurrent      { get; set; } = 1;
    public bool PreferQuiet     { get; set; }
    public bool AutoKillStuck   { get; set; }
    public bool RetryFailed     { get; set; }
    public bool IgnoreProtected { get; set; }
    public bool NoLoudLimit     { get; set; }
    public bool SafeMode        { get; set; }
    public bool Simulate        { get; set; }       // --simulate: run the engine's own simulated uninstall
    public bool IntelligentSort { get; set; } = true;   // GUI default: AdvancedIntelligentUninstallerSorting
    public bool WithRelated     { get; set; }       // add related entries (GUI wizard "related apps")
    public bool RestorePoint    { get; set; }       // create a system restore point first
    public bool CloseApps       { get; set; }       // kill processes running from the target's folders
    public string? RegBackupFile { get; set; }      // export target registry keys to .reg before changing them
    public List<string> PreCommands  { get; } = new();
    public List<string> PostCommands { get; } = new();

    // ── Scan tuning (GUI Settings > Folders / Quiet / Cache) ─────────────────────
    public bool ScanPreDefined        { get; set; } = true;
    public bool? AutoDetectFolders    { get; set; }
    public bool ScanRemovable         { get; set; }
    public string[]? CustomFolders    { get; set; }
    public bool QuietAutomation       { get; set; }
    public bool QuietAutomationKill   { get; set; }
    public bool UseQuietDaemon        { get; set; }
    public bool UseInfoCache          { get; set; }

    // ── View / filtering (GUI sidebar + View menu presets) ───────────────────────
    public string?      Preset        { get; set; }
    public bool         HideMicrosoft { get; set; }
    public bool         OnlyInvalid   { get; set; }
    public List<string> Kinds         { get; } = new();    // --kind Msiexec,StoreApp,...
    public string?      ListFile      { get; set; }        // --list <file.bcul>: filter through a saved uninstall list

    // ── Misc command options ─────────────────────────────────────────────────────
    public string? What      { get; set; }   // open --what install|uninstaller|source|web|registry
    public string? Site      { get; set; }   // search-online --site google|github|...
    public string? MsiMode   { get; set; }   // msi --mode configure|uninstall|quiet
    public bool    Launch    { get; set; }   // open/search-online: shell-open instead of printing
    public int?    Index     { get; set; }   // run --index N
    public bool    Exclude   { get; set; }   // make-list --exclude
    public bool    Append    { get; set; }   // make-list --append
    public bool    DoUninstall { get; set; } // target --uninstall
    public string? StartupType { get; set; } // startup --type normal|task|service|browser

    /// <summary>
    /// True only when a destructive operation should really run.
    /// Per the safety model: execute only with --yes and without --dry-run; otherwise dry-run.
    /// </summary>
    public bool WillExecute => Yes && !DryRun;

    public static CliArgs Parse(string[] raw)
    {
        var a = new CliArgs();
        if (raw.Length == 0) return a;

        int i = 0;
        var positionals = new List<string>();

        // First token — subcommand or flag
        switch (raw[0].ToLowerInvariant())
        {
            case "list":        a.Command = Command.List;        i = 1; break;
            case "export":      a.Command = Command.Export;      i = 1; a.Format = OutputFormat.Json; break;
            case "uninstall":   a.Command = Command.Uninstall;   i = 1; break;
            case "bulk":        a.Command = Command.Bulk;        i = 1; a.Bulk = true; break;
            case "junk":        a.Command = Command.Junk;        i = 1; break;
            case "repair":      a.Command = Command.Repair;      i = 1; break;
            case "modify":      a.Command = Command.Modify;      i = 1; break;
            case "rename":      a.Command = Command.Rename;      i = 1; break;
            case "delete-entry":a.Command = Command.DeleteEntry; i = 1; break;
            case "startup":     a.Command = Command.Startup;     i = 1; break;
            case "info":        a.Command = Command.Info;        i = 1; break;
            case "import-list": a.Command = Command.ImportList;  i = 1; break;
            case "serve":       a.Command = Command.Serve;       i = 1; break;
            case "tui": case "ui": case "interactive":
                                a.Command = Command.Tui;         i = 1; break;
            case "notes": case "note":
                                a.Command = Command.Notes;       i = 1; break;
            case "msi": case "msiexec":
                                a.Command = Command.Msi;         i = 1; break;
            case "uninstall-dir": case "uninstall-from-directory":
                                a.Command = Command.UninstallDir; i = 1; break;
            case "target": case "find-owner":
                                a.Command = Command.Target;      i = 1; break;
            case "manual-uninstall": case "leftovers":
                                a.Command = Command.ManualUninstall; i = 1; break;
            case "clean-program-files": case "orphans":
                                a.Command = Command.CleanProgramFiles; i = 1; break;
            case "restore-point":
                                a.Command = Command.RestorePoint; i = 1; break;
            case "reg-backup":  a.Command = Command.RegBackup;   i = 1; break;
            case "open": case "locate":
                                a.Command = Command.Open;        i = 1; break;
            case "search-online": case "search":
                                a.Command = Command.SearchOnline; i = 1; break;
            case "run":         a.Command = Command.Run;         i = 1; break;
            case "take-ownership": case "takeown":
                                a.Command = Command.TakeOwnership; i = 1; break;
            case "make-list":   a.Command = Command.MakeList;    i = 1; break;
            case "tools": case "tool":
                                a.Command = Command.Tools;       i = 1; break;
            case "help": case "--help": case "-h": case "/?":
                a.Command = Command.Help; return a;
        }

        for (; i < raw.Length; i++)
        {
            var arg = raw[i];

            // Legacy BCU-console switches (back-compat with pre-merge scripts): /Q /U /V /J[=Level].
            // /? is intentionally excluded so it falls through to the help case below.
            if (arg.Length > 1 && arg[0] == '/' && !arg.Equals("/?", StringComparison.Ordinal))
            {
                var legacy = arg.ToLowerInvariant();
                if (legacy == "/q") { a.UseQuietUninstall = true; a.PreferQuiet = true; continue; }
                if (legacy == "/u") { a.Yes = true; continue; }        // unattended == execute
                if (legacy == "/v") { continue; }                      // verbose: progress already streams to stderr
                if (legacy == "/j" || legacy.StartsWith("/j=", StringComparison.Ordinal))
                {
                    a.RunJunk = true;
                    a.JunkLevel = ConfidenceLevel.VeryGood;             // upstream BCU-console default
                    var eq = arg.IndexOf('=');
                    if (eq >= 0 && Enum.TryParse<ConfidenceLevel>(arg[(eq + 1)..], true, out var jl))
                        a.JunkLevel = jl;
                    continue;
                }
            }

            switch (arg.ToLowerInvariant())
            {
                case "--help": case "-h": case "/?":
                    a.Command = Command.Help; return a;

                case "--format":
                    if (i + 1 < raw.Length)
                        a.Format = raw[++i].ToLowerInvariant() switch
                        {
                            "json" => OutputFormat.Json,
                            "csv"  => OutputFormat.Csv,
                            "xml"  => OutputFormat.Xml,
                            "bat"  => OutputFormat.Bat,
                            "ps1" or "powershell" => OutputFormat.Ps1,
                            "store-ps1" or "storeps1" => OutputFormat.StorePs1,
                            _      => OutputFormat.Table
                        };
                    break;

                case "--filter":
                    if (i + 1 < raw.Length) a.Filter = raw[++i];
                    break;

                case "--source-timeout":
                    if (i + 1 < raw.Length && int.TryParse(raw[++i], out var stSecs) && stSecs >= 0)
                        a.SourceTimeout = TimeSpan.FromSeconds(stSecs);
                    break;

                case "--sort":
                    if (i + 1 < raw.Length) a.SortBy = raw[++i].ToLowerInvariant();
                    break;

                case "--output": case "-o":
                    if (i + 1 < raw.Length) a.OutputFile = raw[++i];
                    break;

                case "--pipe":
                    if (i + 1 < raw.Length) a.PipeName = raw[++i];
                    break;

                case "--backup":
                    if (i + 1 < raw.Length) a.BackupDir = raw[++i];
                    break;

                case "--new-name":
                    if (i + 1 < raw.Length) a.NewName = raw[++i];
                    break;

                case "--wide":     a.Wide           = true; break;
                case "--quiet": case "-q": a.Quiet  = true; break;
                case "--json-errors": a.JsonErrors = true; break;
                case "--verify-certs": a.VerifyCerts = true; break;
                case "--system":   a.IncludeSystem  = true; break;
                case "--updates":  a.IncludeUpdates = true; break;
                case "--orphaned": a.IncludeOrphaned = true; break;
                case "--rmm-safe":
                    a.RmmSafe = true;
                    a.ScanDrives = false;
                    a.ScanSteam = false;
                    a.ScanStore = false;
                    a.ScanChoco = false;
                    a.ScanScoop = false;
                    a.ScanFeatures = false;
                    a.ScanUpdates = false;
                    a.ScanOculus = false;
                    break;
                case "--all":
                    a.IncludeSystem = a.IncludeUpdates = a.IncludeOrphaned = true;
                    break;

                case "--run-as":
                    if (i + 1 < raw.Length)
                        a.RunAs = ParseRunAs(raw[++i]);
                    break;

                // Sources
                case "--drives":      a.ScanDrives   = true; break;
                case "--oculus":      a.ScanOculus   = true; break;
                case "--no-registry": a.ScanRegistry = false; break;
                case "--no-drives":   a.ScanDrives   = false; break;
                case "--no-steam":    a.ScanSteam    = false; break;
                case "--no-store":    a.ScanStore    = false; break;
                case "--no-choco":    a.ScanChoco    = false; break;
                case "--no-scoop":    a.ScanScoop    = false; break;
                case "--no-features": a.ScanFeatures = false; break;
                case "--no-updates":  a.ScanUpdates  = false; break;

                // Scan tuning
                case "--no-predefined":       a.ScanPreDefined = false; break;
                case "--no-folder-autodetect":a.AutoDetectFolders = false; break;
                case "--folder-autodetect":   a.AutoDetectFolders = true; break;
                case "--scan-removable":      a.ScanRemovable = true; break;
                case "--custom-folders":
                    if (i + 1 < raw.Length)
                        a.CustomFolders = raw[++i].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(x => x.Trim('"')).ToArray();
                    break;
                case "--quiet-automation":    a.QuietAutomation = true; break;
                case "--quiet-automation-kill-stuck": a.QuietAutomation = a.QuietAutomationKill = true; break;
                case "--use-daemon":          a.UseQuietDaemon = true; break;
                case "--cache":               a.UseInfoCache = true; break;

                // View / filtering
                case "--preset": case "--view":
                    if (i + 1 < raw.Length) a.Preset = raw[++i].ToLowerInvariant();
                    break;
                case "--hide-microsoft": a.HideMicrosoft = true; break;
                case "--invalid":        a.OnlyInvalid = true; break;
                case "--kind":
                    if (i + 1 < raw.Length)
                        a.Kinds.AddRange(raw[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--list":
                    if (i + 1 < raw.Length) a.ListFile = raw[++i];
                    break;

                // Misc command options
                case "--what":  if (i + 1 < raw.Length) a.What = raw[++i].ToLowerInvariant(); break;
                case "--site":  if (i + 1 < raw.Length) a.Site = raw[++i].ToLowerInvariant(); break;
                case "--mode":  if (i + 1 < raw.Length) a.MsiMode = raw[++i].ToLowerInvariant(); break;
                case "--type":  if (i + 1 < raw.Length) a.StartupType = raw[++i].ToLowerInvariant(); break;
                case "--index":
                    if (i + 1 < raw.Length && int.TryParse(raw[++i], out var idx)) a.Index = idx;
                    break;
                case "--launch": a.Launch  = true; break;
                case "--exclude":a.Exclude = true; break;
                case "--append": a.Append  = true; break;
                case "--uninstall": a.DoUninstall = true; break;
                case "--description": case "--text":
                    if (i + 1 < raw.Length) a.Value = raw[++i];
                    break;

                // Targeting / uninstall
                case "--msi-guid": case "--product-code":
                    if (i + 1 < raw.Length) a.TargetMsiGuid = raw[++i];
                    break;
                case "--simulate":        a.Simulate          = true; break;
                case "--no-intelligent-sort": a.IntelligentSort = false; break;
                case "--intelligent-sort":    a.IntelligentSort = true;  break;
                case "--with-related":    a.WithRelated       = true; break;
                case "--restore-point":   a.RestorePoint      = true; break;
                case "--close-apps":      a.CloseApps         = true; break;
                case "--reg-backup":
                    if (i + 1 < raw.Length) a.RegBackupFile = raw[++i];
                    break;
                case "--pre-command":
                    if (i + 1 < raw.Length) a.PreCommands.Add(raw[++i]);
                    break;
                case "--post-command":
                    if (i + 1 < raw.Length) a.PostCommands.Add(raw[++i]);
                    break;
                case "--exact":           a.ExactMatch        = true; break;
                case "--yes": case "-y":  a.Yes               = true; break;
                case "--dry-run":         a.DryRun            = true; break;
                case "--junk":            a.RunJunk           = true; break;
                case "--quiet-uninstall": a.UseQuietUninstall = true; break;
                case "--safe-mode":       a.SafeMode          = true; break;
                case "--registry-path":
                    if (i + 1 < raw.Length) a.TargetRegistryPath = raw[++i];
                    break;
                case "--rating-id":
                    if (i + 1 < raw.Length) a.TargetRatingId = raw[++i];
                    break;

                // Bulk behaviour
                case "--bulk":            a.Bulk            = true; break;
                case "--prefer-quiet":    a.PreferQuiet     = true; break;
                case "--auto-kill-stuck": a.AutoKillStuck   = true; break;
                case "--retry-failed":    a.RetryFailed     = true; break;
                case "--ignore-protected":a.IgnoreProtected = true; break;
                case "--no-loud-limit":   a.NoLoudLimit     = true; break;
                case "--concurrent":
                    if (i + 1 < raw.Length && int.TryParse(raw[++i], out var c) && c > 0)
                        a.Concurrent = c;
                    break;

                case "--junk-level":
                    if (i + 1 < raw.Length && Enum.TryParse<ConfidenceLevel>(raw[++i], true, out var lvl))
                        a.JunkLevel = lvl;
                    break;

                default:
                    // Skip unrecognised option-like tokens; only bare values become positionals.
                    // (Windows paths never start with '/', so a leading '/' means an unknown switch.)
                    if (!arg.StartsWith('-') && !arg.StartsWith('/'))
                        positionals.Add(arg);
                    break;
            }
        }

        AssignPositionals(a, positionals);
        return a;
    }

    /// <summary>Give positional arguments meaning based on the chosen command.</summary>
    private static void AssignPositionals(CliArgs a, List<string> positionals)
    {
        a.Positionals.AddRange(positionals);
        switch (a.Command)
        {
            case Command.Uninstall:
            case Command.Bulk:
            case Command.RegBackup:
            case Command.MakeList:
            case Command.ManualUninstall:
                a.Targets.AddRange(positionals);
                a.TargetName = positionals.FirstOrDefault();
                break;

            case Command.Junk:
            case Command.Repair:
            case Command.Modify:
            case Command.DeleteEntry:
            case Command.Info:
            case Command.Msi:
            case Command.Open:
            case Command.SearchOnline:
            case Command.Run:
            case Command.TakeOwnership:
                a.TargetName = positionals.FirstOrDefault();
                break;

            case Command.UninstallDir:
            case Command.Target:
                a.Value = positionals.FirstOrDefault();
                break;

            case Command.RestorePoint:
                a.Value ??= positionals.FirstOrDefault();
                break;

            case Command.Notes:
                a.SubAction = positionals.ElementAtOrDefault(0)?.ToLowerInvariant();
                a.TargetName = positionals.ElementAtOrDefault(1);
                a.Value ??= positionals.ElementAtOrDefault(2);
                break;

            case Command.Tools:
                a.SubAction = positionals.ElementAtOrDefault(0)?.ToLowerInvariant();
                break;

            case Command.Rename:
                a.TargetName = positionals.ElementAtOrDefault(0);
                a.NewName  ??= positionals.ElementAtOrDefault(1);
                break;

            case Command.Startup:
                a.SubAction  = positionals.ElementAtOrDefault(0)?.ToLowerInvariant();
                a.TargetName = positionals.ElementAtOrDefault(1);
                a.Value      = positionals.ElementAtOrDefault(2);   // startup backup <match> <dir>
                break;

            case Command.ImportList:
                a.ImportFile = positionals.FirstOrDefault();
                break;

            case Command.Export:
                if (a.OutputFile == null) a.OutputFile = positionals.FirstOrDefault();
                break;

            case Command.List:
                if (a.Filter == null) a.Filter = positionals.FirstOrDefault();
                break;
        }
    }

    private static RunAsMode ParseRunAs(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "system" or "localsystem" or "local-system" => RunAsMode.System,
            "active-user" or "activeuser" or "interactive" or "user" => RunAsMode.ActiveUser,
            _ => throw new ArgumentException("run-as must be system or active-user")
        };
}
