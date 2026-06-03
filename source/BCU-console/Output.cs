/*
    Copyright (c) 2026 theoneec
    Apache License Version 2.0

    Part of a personal fork of Bulk Crap Uninstaller
    (Marcin Szeniak, https://github.com/Klocman/). Original file authored for
    the merged BCU-console command-line interface.
*/
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UninstallTools;

namespace BcuCli;

/// <summary>Rendering of application lists to table / JSON / CSV, plus shared IO helpers.</summary>
public static class Output
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void PrintJson(IEnumerable<ApplicationUninstallerEntry> apps, bool verifyCerts, TextWriter? writer = null)
    {
        writer ??= Console.Out;
        var records = apps.Select(e => new AppRecord(e, verifyCerts)).ToList();
        writer.WriteLine(JsonSerializer.Serialize(records, JsonOpts));
    }

    public static void PrintCsv(IEnumerable<ApplicationUninstallerEntry> apps, bool verifyCerts, TextWriter? writer = null)
    {
        writer ??= Console.Out;
        var header = "Name,Version,Publisher,InstallerType,Source,InstallDate,SizeKB,InstallLocation,UninstallString,QuietUninstallString,Is64Bit,IsUpdate,IsOrphaned,AboutUrl";
        if (verifyCerts) header += ",Signed,CertValid";
        writer.WriteLine(header);

        foreach (var e in apps)
        {
            var line =
                Csv(e.DisplayName ?? "") + "," +
                Csv(e.DisplayVersion ?? "") + "," +
                Csv(e.Publisher ?? "") + "," +
                Csv(e.UninstallerKind.ToString()) + "," +
                Csv(e.RegistryPath ?? "") + "," +
                Csv(e.InstallDate != DateTime.MinValue ? e.InstallDate.ToString("yyyy-MM-dd") : "") + "," +
                Csv(e.EstimatedSize.GetKbSize() > 0 ? e.EstimatedSize.GetKbSize().ToString() : "") + "," +
                Csv(e.InstallLocation ?? "") + "," +
                Csv(e.UninstallString ?? "") + "," +
                Csv(e.QuietUninstallString ?? "") + "," +
                Csv(e.Is64Bit.ToString()) + "," +
                Csv(e.IsUpdate.ToString()) + "," +
                Csv(e.IsOrphaned.ToString()) + "," +
                Csv(e.AboutUrl ?? "");

            if (verifyCerts)
            {
                var (signed, valid) = CertInfo(e);
                line += "," + Csv(signed) + "," + Csv(valid);
            }
            writer.WriteLine(line);
        }
    }

    public static void PrintTable(IEnumerable<ApplicationUninstallerEntry> apps, bool wide, bool verifyCerts)
    {
        if (wide)
        {
            const int nW = 45, verW = 16, pubW = 26, typeW = 13, dateW = 11, sizeW = 9;
            var hdr = $"{"Name",-nW}  {"Version",-verW}  {"Publisher",-pubW}  {"Type",-typeW}  {"Installed",-dateW}  {"KB",sizeW}";
            if (verifyCerts) hdr += $"  {"Signed",-7}{"Valid",-6}";
            Console.WriteLine(hdr);
            Console.WriteLine(new string('-', hdr.Length));
            foreach (var e in apps)
            {
                var kb = e.EstimatedSize.GetKbSize();
                var row =
                    $"{T(e.DisplayName ?? "", nW),-nW}  {T(e.DisplayVersion ?? "", verW),-verW}  " +
                    $"{T(e.Publisher ?? "", pubW),-pubW}  {T(e.UninstallerKind.ToString(), typeW),-typeW}  " +
                    $"{(e.InstallDate != DateTime.MinValue ? e.InstallDate.ToString("yyyy-MM-dd") : ""),-dateW}  {(kb > 0 ? kb.ToString() : ""),sizeW}";
                if (verifyCerts)
                {
                    var (signed, valid) = CertInfo(e);
                    row += $"  {signed,-7}{valid,-6}";
                }
                Console.WriteLine(row);
            }
        }
        else
        {
            const int nW = 48, verW = 18, pubW = 28, typeW = 16;
            var hdr = $"{"Name",-nW}  {"Version",-verW}  {"Publisher",-pubW}  {"Type",-typeW}";
            Console.WriteLine(hdr);
            Console.WriteLine(new string('-', hdr.Length));
            foreach (var e in apps)
                Console.WriteLine($"{T(e.DisplayName ?? "", nW),-nW}  {T(e.DisplayVersion ?? "", verW),-verW}  {T(e.Publisher ?? "", pubW),-pubW}  {e.UninstallerKind,-typeW}");
        }
    }

    /// <summary>Returns (signed?, valid?) display strings for certificate columns.</summary>
    public static (string signed, string valid) CertInfo(ApplicationUninstallerEntry e)
    {
        try
        {
            var cert = e.GetCertificate();
            if (cert == null) return ("no", "-");
            var valid = e.IsCertificateValid(false);
            return ("yes", valid == null ? "?" : valid.Value ? "yes" : "no");
        }
        catch
        {
            return ("?", "?");
        }
    }

    public static string T(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "~";

    public static string Csv(string s) =>
        (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;

    public static TextWriter? OpenOutput(CliArgs args)
    {
        if (string.IsNullOrEmpty(args.OutputFile))
            return null;
        if (!args.Quiet)
            Console.Error.WriteLine($"Writing output to {args.OutputFile}...");
        return new StreamWriter(args.OutputFile, false, Encoding.UTF8);
    }

    public static void WriteError(string message, Exception ex, bool json)
    {
        if (!json)
        {
            Console.Error.WriteLine($"{message}: {ex.Message}");
            return;
        }

        Console.Error.WriteLine(JsonSerializer.Serialize(new
        {
            ok = false,
            error = message,
            detail = ex.Message,
            type = ex.GetType().Name,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
