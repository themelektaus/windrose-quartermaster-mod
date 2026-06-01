using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Windrose.Quartermaster.Core
{
    static class ToolProcess
    {
        public struct Result
        {
            public int ExitCode;
            public string Stdout;
            public string Stderr;
            public string ErrOrOut => string.IsNullOrEmpty(Stderr) ? Stdout : Stderr;
        }

        // Runs a Windows tool exe (Wine-wrapped off-Windows) and captures its output.
        public static Result RunCapture(string exe, IEnumerable<string> args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            WineHelper.ApplyWine(psi);
            var proc = Process.Start(psi);
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return new Result { ExitCode = proc.ExitCode, Stdout = stdout, Stderr = stderr };
        }
    }
}
