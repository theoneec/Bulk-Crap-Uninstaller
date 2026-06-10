/*
    Copyright (c) 2026 theoneec
    Apache License Version 2.0

    Part of a personal fork of Bulk Crap Uninstaller
    (Marcin Szeniak, https://github.com/Klocman/). Original file authored for
    the merged BCU-console command-line interface.
*/

using System.Runtime.InteropServices;

namespace BcuCli;

/// <summary>
/// Launches a process in the active console (interactive user) session from a
/// SYSTEM / Session-0 context, via WTSQueryUserToken + CreateProcessAsUser. Used by
/// `bcu serve` (session.launch) so an RMM agent running as SYSTEM can perform
/// per-user (Store/UWP) and GUI-automation uninstall work that needs a desktop.
///
/// IMPORTANT: this only works when the caller is SYSTEM (holds SeTcbPrivilege). It
/// cannot be exercised from a normal user session, so it is verified by review.
/// Fire-and-forget: no cross-session stdout capture (that would need a relay pipe).
/// </summary>
internal static class UserSessionLauncher
{
    public static (bool ok, int pid, string? error) TryLaunchInActiveSession(string exePath, string arguments)
    {
        var session = WTSGetActiveConsoleSessionId();
        if (session == 0xFFFFFFFF) return (false, 0, "No active console session");

        if (!WTSQueryUserToken(session, out var userToken))
            return (false, 0, "WTSQueryUserToken failed (need SYSTEM): " + Marshal.GetLastWin32Error());

        var dupToken = IntPtr.Zero;
        var envBlock = IntPtr.Zero;
        try
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>() };
            if (!DuplicateTokenEx(userToken, MAXIMUM_ALLOWED, ref sa,
                    SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation, TOKEN_TYPE.TokenPrimary, out dupToken))
                return (false, 0, "DuplicateTokenEx failed: " + Marshal.GetLastWin32Error());

            CreateEnvironmentBlock(out envBlock, dupToken, false);

            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            var cmd = $"\"{exePath}\" {arguments}";

            var ok = CreateProcessAsUser(dupToken, null, cmd, ref sa, ref sa, false,
                CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW, envBlock, null, ref si, out var pi);
            if (!ok) return (false, 0, "CreateProcessAsUser failed: " + Marshal.GetLastWin32Error());

            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            var pid = (int)pi.dwProcessId;
            if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
            return (true, pid, null);
        }
        catch (Exception ex) { return (false, 0, ex.Message); }
        finally
        {
            if (envBlock != IntPtr.Zero) DestroyEnvironmentBlock(envBlock);
            if (dupToken != IntPtr.Zero) CloseHandle(dupToken);
            if (userToken != IntPtr.Zero) CloseHandle(userToken);
        }
    }

    // ── Win32 interop ────────────────────────────────────────────────────────────

    private const uint MAXIMUM_ALLOWED = 0x02000000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW = 0x08000000;

    private enum SECURITY_IMPERSONATION_LEVEL { SecurityAnonymous, SecurityIdentification, SecurityImpersonation, SecurityDelegation }
    private enum TOKEN_TYPE { TokenPrimary = 1, TokenImpersonation }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public bool bInheritHandle; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess,
        ref SECURITY_ATTRIBUTES lpTokenAttributes, SECURITY_IMPERSONATION_LEVEL impersonationLevel,
        TOKEN_TYPE tokenType, out IntPtr phNewToken);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr hToken, string? lpApplicationName, string lpCommandLine,
        ref SECURITY_ATTRIBUTES lpProcessAttributes, ref SECURITY_ATTRIBUTES lpThreadAttributes, bool bInheritHandles,
        uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
