/*
    Copyright (c) 2017 Marcin Szeniak (https://github.com/Klocman/)
    Apache License Version 2.0

    Modified 2026 (BCU personal fork, theoneec): FindJunk runs the independent
    junk scanners in parallel (Parallel.ForEach) and reports accurate scanner
    done/total progress, instead of scanning sequentially.
*/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Klocman.Extensions;
using Klocman.Forms.Tools;
using Klocman.Tools;
using UninstallTools.Junk.Containers;
using UninstallTools.Properties;

namespace UninstallTools.Junk
{
    public static class JunkManager
    {
        private static IEnumerable<IJunkResult> CleanUpResults(IEnumerable<IJunkResult> input)
        {
            var prohibitedLocations = GetProhibitedLocations();

            return RemoveDuplicates(input)
                .Where(x => JunkDoesNotPointToDirectories(x, prohibitedLocations))
                .Where(JunkDoesNotPointToSelf);
        }

        /// <summary>
        /// Make sure that the junk result doesn't point to this application.
        /// </summary>
        private static bool JunkDoesNotPointToSelf(IJunkResult x)
        {
            if (x is FileSystemJunk fileSystemJunk)
            {
                return fileSystemJunk.Path == null || 
                       !fileSystemJunk.Path.FullName.StartsWith(UninstallToolsGlobalConfig.AppLocation, StringComparison.OrdinalIgnoreCase);
            }

            if (x is StartupJunkNode startupJunk)
            {
                return startupJunk.Entry?.CommandFilePath == null || 
                       !startupJunk.Entry.CommandFilePath.StartsWith(UninstallToolsGlobalConfig.AppLocation, StringComparison.OrdinalIgnoreCase);
            }

            return true;
        }

        /// <summary>
        /// Merge duplicate junk entries and their confidence parts
        /// </summary>
        private static IEnumerable<IJunkResult> RemoveDuplicates(IEnumerable<IJunkResult> input)
        {
            foreach (var appGroup in input.GroupBy(x => x.Application))
            {
                foreach (var group in appGroup.GroupBy(x => PathTools.NormalizePath(x.GetDisplayName()).ToLowerInvariant()))
                {
                    IJunkResult firstJunkResult = null;
                    foreach (var junkResult in group)
                    {
                        if (firstJunkResult == null)
                            firstJunkResult = junkResult;
                        else
                            firstJunkResult.Confidence.AddRange(junkResult.Confidence.ConfidenceParts);
                    }

                    if (firstJunkResult != null)
                        yield return firstJunkResult;
                }
            }
        }

        private static bool JunkDoesNotPointToDirectories(IJunkResult arg, HashSet<string> prohibitedDirs)
        {
            if (arg is not FileSystemJunk fileSystemJunk)
                return true;

            return !prohibitedDirs.Contains(fileSystemJunk.Path.FullName.ToLowerInvariant());
        }

        /// <summary>
        /// Prevent suggesting removing special directories if the app for some reason was installed into them or otherwise used them
        /// </summary>
        private static HashSet<string> GetProhibitedLocations()
        {
            var results = new HashSet<string>();

            void AddRange(IEnumerable<string> paths)
            {
                foreach (var path in paths
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Attempt(System.IO.Path.GetFullPath)
                    .Select(x => x.ToLowerInvariant()))
                {
                    results.Add(path);
                }
            }

            AddRange(Enum.GetValues<Klocman.Native.CSIDL>().Attempt(WindowsTools.GetEnvironmentPath));

            var knownFolderstype = Type.GetType("Windows.Storage.KnownFolders, Microsoft.Windows.SDK.NET", false);
            // Might not be available on some systems
            if (knownFolderstype != null)
            {
                try
                {
                    AddRange(knownFolderstype.GetProperties().Attempt(p => ((Windows.Storage.StorageFolder)p.GetValue(null))!.Path));
                }
                catch (Exception ex)
                {
                    Trace.WriteLine("Failed to collect KnownFolders: " + ex);
                }
            }

            return results;
        }

        public static IEnumerable<IJunkResult> FindJunk(IEnumerable<ApplicationUninstallerEntry> targets,
            ICollection<ApplicationUninstallerEntry> allUninstallers, ListGenerationProgress.ListGenerationCallback progressCallback)
        {
            progressCallback(new ListGenerationProgress(-1, 0, Localisation.Junk_Progress_Startup));

            var scanners = ReflectionTools.GetTypesImplementingBase<IJunkCreator>()
                .Attempt(Activator.CreateInstance)
                .Cast<IJunkCreator>()
                .ToList();

            foreach (var junkCreator in scanners)
            {
                junkCreator.Setup(allUninstallers);
            }

            var targetEntries = targets as IList<ApplicationUninstallerEntry> ?? targets.ToList();

            // Scanners are independent (separate instances; no shared mutable state after Setup),
            // so run them in parallel. Results go into a thread-safe bag; progress is reported
            // under a lock as each scanner finishes, giving accurate done/total counts.
            var results = new ConcurrentBag<IJunkResult>();
            var progressLock = new object();
            var done = 0;
            Parallel.ForEach(scanners,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) },
                junkCreator =>
                {
                    foreach (var target in targetEntries)
                    {
                        try
                        {
                            foreach (var r in junkCreator.FindJunk(target))
                                results.Add(r);
                        }
                        catch (SystemException ex) { PremadeDialogs.GenericError(ex); }
                    }

                    lock (progressLock)
                    {
                        done++;
                        progressCallback(new ListGenerationProgress(done, scanners.Count, junkCreator.CategoryName));
                    }
                });

            progressCallback(new ListGenerationProgress(-1, 0, Localisation.Junk_Progress_Finishing));

            var resultList = results.ToList();
            foreach (var target in targetEntries)
                resultList.AddRange(target.AdditionalJunk);

            return CleanUpResults(resultList);
        }

        public static IEnumerable<IJunkResult> FindProgramFilesJunk(
            ICollection<ApplicationUninstallerEntry> allUninstallers)
        {
            var pfScanner = new ProgramFilesOrphans();
            pfScanner.Setup(allUninstallers);
            return CleanUpResults(pfScanner.FindAllJunk().ToList());
        }
    }
}