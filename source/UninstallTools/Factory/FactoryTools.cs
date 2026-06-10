/*
    Copyright (c) 2018 Marcin Szeniak (https://github.com/Klocman/)
    Apache License Version 2.0

    Modified 2026 (BCU personal fork, theoneec): StartHelperAndReadOutput reads
    stdout asynchronously and enforces UninstallToolsGlobalConfig.HelperProcessTimeout
    (kills the helper process tree on timeout) so a hung helper can't hang the scan.
*/

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Klocman.Extensions;
using Klocman.Forms.Tools;

namespace UninstallTools.Factory
{
    internal static class FactoryTools
    {
        /// <summary>
        /// Warning: only use with helpers that output unicode and use 0 as success return code.
        /// </summary>
        internal static string StartHelperAndReadOutput(string filename, string args)
        {
            if (!File.Exists(filename)) return null;

            using (var process = Process.Start(new ProcessStartInfo(filename, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.Unicode
            }))
            {
                if (process == null) return null;
                try
                {
                    var sw = Stopwatch.StartNew();

                    // Read stdout asynchronously: avoids the classic full-pipe deadlock and
                    // lets us bound the helper with a timeout. A hung helper must never hang
                    // the whole scan (see HelperProcessTimeout).
                    var readTask = process.StandardOutput.ReadToEndAsync();

                    var timeoutMs = (int)UninstallToolsGlobalConfig.HelperProcessTimeout.TotalMilliseconds;
                    bool exited;
                    if (timeoutMs > 0)
                        exited = process.WaitForExit(timeoutMs);
                    else
                    {
                        process.WaitForExit();   // 0 / negative => wait indefinitely (legacy behaviour)
                        exited = true;
                    }

                    if (!exited)
                    {
                        Trace.WriteLine($"[Helper] {filename} {args} timed out after {timeoutMs}ms - killing process tree");
                        try { process.Kill(true); } catch (Exception killEx) { Trace.WriteLine(killEx); }
                        return null;
                    }

                    var output = readTask.GetAwaiter().GetResult();
                    Trace.WriteLine($"[Performance] Running command {filename} {args} took {sw.ElapsedMilliseconds}ms");
                    return process.ExitCode == 0 ? output : null;
                }
                catch (Win32Exception ex)
                {
                    PremadeDialogs.GenericError(ex);
                    return null;
                }
            }
        }

        internal static IEnumerable<Dictionary<string, string>> ExtractAppDataSetsFromHelperOutput(string helperOutput)
        {
            ICollection<string> allParts = helperOutput.SplitNewlines(StringSplitOptions.None);
            while (allParts.Count > 0)
            {
                var singleAppParts = allParts.TakeWhile(x => !String.IsNullOrEmpty(x)).ToList();
                allParts = allParts.Skip(singleAppParts.Count + 1).ToList();

                if (!singleAppParts.Any())
                    continue;

                yield return singleAppParts.Where(x => x.Contains(':')).ToDictionary(
                    x => x.Substring(0, x.IndexOf(":", StringComparison.Ordinal)).Trim(),
                    x => x.Substring(x.IndexOf(":", StringComparison.Ordinal) + 1).Trim());
            }
        }

        internal static bool CheckIsValid(ApplicationUninstallerEntry target, IEnumerable<Guid> msiProducts)
        {
            if (string.IsNullOrEmpty(target.UninstallerFullFilename))
                return false;

            bool isPathRooted;
            try
            {
                isPathRooted = Path.IsPathRooted(target.UninstallerFullFilename);
            }
            catch (ArgumentException)
            {
                isPathRooted = false;
            }

            if (isPathRooted && File.Exists(target.UninstallerFullFilename))
                return true;

            if (target.UninstallerKind == UninstallerType.Msiexec)
                return msiProducts.Contains(target.BundleProviderKey);

            return !isPathRooted;
        }
    }
}