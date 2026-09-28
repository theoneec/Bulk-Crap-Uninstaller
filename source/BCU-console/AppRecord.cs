/*
    Copyright (c) 2026 theoneec
    Apache License Version 2.0

    Part of a personal fork of Bulk Crap Uninstaller
    (Marcin Szeniak, https://github.com/Klocman/). Original file authored for
    the merged BCU-console command-line interface.
*/
using System.Text.Json.Serialization;
using UninstallTools;

namespace BcuCli;

/// <summary>
/// Serialization-friendly DTO for JSON/CSV output, mapping BCU's ApplicationUninstallerEntry.
/// </summary>
public class AppRecord
{
    public AppRecord() { }

    public AppRecord(ApplicationUninstallerEntry e) : this(e, false) { }

    public AppRecord(ApplicationUninstallerEntry e, bool verifyCerts)
    {
        Name                = e.DisplayName;
        Version             = e.DisplayVersion;
        Publisher           = e.Publisher;
        InstallerType       = e.UninstallerKind.ToString();
        InstallDate         = e.InstallDate != DateTime.MinValue ? e.InstallDate : (DateTime?)null;
        EstimatedSizeKb     = e.EstimatedSize.GetKbSize() > 0 ? e.EstimatedSize.GetKbSize() : null;
        InstallLocation     = string.IsNullOrEmpty(e.InstallLocation) ? null : e.InstallLocation;
        UninstallString     = e.UninstallString;
        QuietUninstallString= e.QuietUninstallString;
        ModifyPath          = string.IsNullOrEmpty(e.ModifyPath) ? null : e.ModifyPath;
        Is64Bit             = e.Is64Bit.ToString();
        IsSystemComponent   = e.SystemComponent ? true : null;
        IsUpdate            = e.IsUpdate ? true : null;
        IsOrphaned          = e.IsOrphaned ? true : null;
        IsProtected         = e.IsProtected ? true : null;
        RegistryPath        = e.RegistryPath;
        AboutUrl            = e.AboutUrl;
        RatingId            = e.RatingId;
        QuietUninstallPossible = e.QuietUninstallPossible ? true : null;
        SourceKind          = e.UninstallerKind.ToString();
        HasStartup          = e.StartupEntries != null && e.StartupEntries.Any() ? true : null;
        CustomNote          = string.IsNullOrEmpty(e.CustomNote) ? null : e.CustomNote;
        IsInvalid           = e.IsValid ? null : true;
        IsTweak             = e.IsScriptTweak ? true : null;
        IsWebBrowser        = e.IsWebBrowser ? true : null;
        MsiProductCode      = e.BundleProviderKey != Guid.Empty ? e.BundleProviderKey.ToString("B").ToUpperInvariant() : null;
        UninstallerLocation = string.IsNullOrEmpty(e.UninstallerLocation) ? null : e.UninstallerLocation;
        InstallSource       = string.IsNullOrEmpty(e.InstallSource) ? null : e.InstallSource;

        if (verifyCerts)
        {
            var (signed, valid) = Output.CertInfo(e);
            Signed    = signed == "yes" ? true : signed == "no" ? false : null;
            CertValid = valid is "yes" ? true : valid is "no" ? false : null;
        }
    }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("publisher")]
    public string? Publisher { get; set; }

    [JsonPropertyName("installerType")]
    public string? InstallerType { get; set; }

    [JsonPropertyName("sourceKind")]
    public string? SourceKind { get; set; }

    [JsonPropertyName("installDate")]
    public DateTime? InstallDate { get; set; }

    [JsonPropertyName("estimatedSizeKb")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? EstimatedSizeKb { get; set; }

    [JsonPropertyName("installLocation")]
    public string? InstallLocation { get; set; }

    [JsonPropertyName("uninstallString")]
    public string? UninstallString { get; set; }

    [JsonPropertyName("quietUninstallString")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? QuietUninstallString { get; set; }

    [JsonPropertyName("quietUninstallPossible")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? QuietUninstallPossible { get; set; }

    [JsonPropertyName("is64Bit")]
    public string? Is64Bit { get; set; }

    [JsonPropertyName("isSystemComponent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsSystemComponent { get; set; }

    [JsonPropertyName("isUpdate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsUpdate { get; set; }

    [JsonPropertyName("isOrphaned")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsOrphaned { get; set; }

    [JsonPropertyName("isProtected")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsProtected { get; set; }

    [JsonPropertyName("registryPath")]
    public string? RegistryPath { get; set; }

    [JsonPropertyName("aboutUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AboutUrl { get; set; }

    [JsonPropertyName("ratingId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RatingId { get; set; }

    [JsonPropertyName("modifyPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModifyPath { get; set; }

    [JsonPropertyName("hasStartup")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? HasStartup { get; set; }

    [JsonPropertyName("isInvalid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsInvalid { get; set; }

    [JsonPropertyName("isTweak")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsTweak { get; set; }

    [JsonPropertyName("isWebBrowser")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsWebBrowser { get; set; }

    [JsonPropertyName("msiProductCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MsiProductCode { get; set; }

    [JsonPropertyName("uninstallerLocation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UninstallerLocation { get; set; }

    [JsonPropertyName("installSource")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InstallSource { get; set; }

    [JsonPropertyName("customNote")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CustomNote { get; set; }

    [JsonPropertyName("signed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Signed { get; set; }

    [JsonPropertyName("certValid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CertValid { get; set; }
}
