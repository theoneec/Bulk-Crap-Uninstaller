/*
    Copyright (c) 2017 Marcin Szeniak (https://github.com/Klocman/)
    Apache License Version 2.0
*/

using System;
using System.Diagnostics;
using Klocman.Forms.Tools;
using Klocman.Tools;

namespace UninstallTools.Junk.Containers
{
    public class RunProcessJunk : JunkResultBase
    {
        public ProcessStartCommand ProcessToStart { get; }

        private readonly string _junkName;

        public RunProcessJunk(ApplicationUninstallerEntry application, IJunkCreator source, ProcessStartCommand processToStart, string junkName) : base(application, source)
        {
            _junkName = junkName;
            ProcessToStart = processToStart;
        }

        public override void Backup(string backupDirectory)
        {

        }

        public override void Delete()
        {
            // Fork (theoneec): surface failure instead of swallowing it - verify the cleanup
            // process actually started and exited cleanly so callers get accurate results.
            var info = ProcessToStart.ToProcessStartInfo();
            info.WindowStyle = ProcessWindowStyle.Minimized;
            info.UseShellExecute = true;

            var process = Process.Start(info);
            if (process == null)
                throw new InvalidOperationException($"Could not start cleanup process for {GetDisplayName()}");

            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Cleanup process for {GetDisplayName()} exited with code {process.ExitCode}");
        }

        public override string GetDisplayName()
        {
            return $"{_junkName} ({ProcessToStart})";
        }

        public override void Open()
        {
            try
            {
                WindowsTools.OpenExplorerFocusedOnObject(ProcessToStart.FileName);
            }
            catch (SystemException ex)
            {
                PremadeDialogs.GenericError(ex);
            }
        }
    }
}