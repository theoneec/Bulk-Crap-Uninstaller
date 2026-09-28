/*
    Copyright (c) 2026 theoneec
    Apache License Version 2.0

    Part of a personal fork of Bulk Crap Uninstaller
    (Marcin Szeniak, https://github.com/Klocman/). Original file authored for
    the merged BCU-console command-line interface.
*/

namespace BcuCli;

/// <summary>
/// Documented, stable process exit codes for the CLI. Automation should branch on
/// these. Rule of thumb: 0 == fully successful; any non-zero == not fully successful.
///
/// Kept deliberately small and distinct. 1 remains a generic catch-all so existing
/// "exit != 0" checks keep working; the richer codes let callers react specifically.
/// </summary>
public static class ExitCodes
{
    /// <summary>Completed successfully (a dry-run that printed its plan also returns this).</summary>
    public const int Success = 0;

    /// <summary>Generic / unexpected failure (catch-all).</summary>
    public const int Error = 1;

    /// <summary>Invalid arguments or command usage.</summary>
    public const int BadUsage = 2;

    /// <summary>The requested target application/entry was not found.</summary>
    public const int NotFound = 3;

    /// <summary>A multi-item operation (bulk/junk) completed but one or more items failed.</summary>
    public const int PartialFailure = 4;

    /// <summary>The operation requires elevation (administrator) that was not available.</summary>
    public const int NeedsElevation = 5;

    /// <summary>
    /// The operation requires an interactive user session and cannot run in the current
    /// context (e.g. Store-app or GUI-automated uninstall under SYSTEM / Session 0).
    /// </summary>
    public const int NeedsUserSession = 6;

    /// <summary>A source scan or operation exceeded its timeout.</summary>
    public const int Timeout = 7;

    /// <summary>Cancelled by the user (e.g. Ctrl+C).</summary>
    public const int Cancelled = 8;

    /// <summary>
    /// The uninstaller/msiexec succeeded but asked for a reboot (Windows Installer 3010 /
    /// 1641). RMM scripts can use this to schedule a restart.
    /// </summary>
    public const int RebootRequired = 9;

    /// <summary>
    /// Map an uninstaller's own exit code onto this table, so raw codes (which can collide
    /// with ours, e.g. msiexec returning 3 or 4) never leak out as the CLI's exit code.
    /// The raw code is still printed.
    /// </summary>
    public static int FromUninstaller(int code) => code switch
    {
        0 => Success,
        3010 or 1641 => RebootRequired,
        1602 or 1223 => Cancelled,        // ERROR_INSTALL_USEREXIT / ERROR_CANCELLED
        _ => Error
    };
}
