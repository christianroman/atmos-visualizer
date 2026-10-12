// Finds and runs the command-line tools the app uses to prepare a movie: ffprobe and ffmpeg
// (probe, video proxy), mkvextract (TrueHD track out of an MKV) and truehdd (TrueHD -> DAMF).
//
// Each tool is looked up in an optional settings file first, then on PATH, then in its usual
// install folder. The settings file is plain "name: path" lines, e.g.
//   truehdd: D:\tools\truehdd.exe
// It lives where Unity keeps the app's data (Application.persistentDataPath):
//   %USERPROFILE%\AppData\LocalLow\AtmosVisualizer\Atmos Visualizer\settings.yaml

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace AtmosEngine
{
    public sealed class ExternalTools
    {
        public string Ffmpeg, Ffprobe, Mkvextract, Truehdd;

        public static readonly string[] Names = { "ffmpeg", "ffprobe", "mkvextract", "truehdd" };

        /// <summary>The app's settings file (also read by the command-line tools in tools/cs).</summary>
        public static string DefaultSettingsFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "LocalLow", "AtmosVisualizer", "Atmos Visualizer", "settings.yaml");

        public static ExternalTools Find(string settingsFile = null)
        {
            settingsFile = settingsFile ?? DefaultSettingsFile;
            var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (settingsFile != null && File.Exists(settingsFile))
            {
                foreach (var line in File.ReadAllLines(settingsFile))
                {
                    int colon = line.IndexOf(':');
                    if (line.TrimStart().StartsWith("#") || colon <= 0) continue;
                    overrides[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim().Trim('"');
                }
            }
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return new ExternalTools
            {
                Ffmpeg = Locate("ffmpeg", overrides),
                Ffprobe = Locate("ffprobe", overrides),
                Mkvextract = Locate("mkvextract", overrides, Path.Combine(programFiles, "MKVToolNix", "mkvextract.exe")),
                Truehdd = Locate("truehdd", overrides, Path.Combine(home, ".cargo", "bin", "truehdd.exe")),   // cargo install
            };
        }

        static string Locate(string name, Dictionary<string, string> overrides, params string[] usual)
        {
            if (overrides.TryGetValue(name, out var o) && File.Exists(o)) return o;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try
                {
                    var p = Path.Combine(dir.Trim().Trim('"'), name + ".exe");
                    if (File.Exists(p)) return p;
                }
                catch (ArgumentException) { }   // malformed PATH entry
            }
            return usual.FirstOrDefault(File.Exists);
        }

        public IEnumerable<string> Missing()
        {
            if (Ffmpeg == null) yield return "ffmpeg";
            if (Ffprobe == null) yield return "ffprobe";
            if (Mkvextract == null) yield return "mkvextract (MKVToolNix)";
            if (Truehdd == null) yield return "truehdd";
        }
    }

    /// <summary>Runs a process, streaming its output lines to a callback, cancellable.</summary>
    public static class ProcessRunner
    {
        public sealed class Result
        {
            public int ExitCode;
            public string StdErr;
            public string StdOut;
            public bool Cancelled;
        }

        public static Result Run(string exe, IEnumerable<string> args, Action<string> onLine, CancellationToken cancel)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = string.Join(" ", args.Select(Quote)),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            var stderr = new StringBuilder();
            var stdout = new StringBuilder();
            using (var p = new Process { StartInfo = psi })
            {
                p.OutputDataReceived += (_, e) => { if (e.Data != null) { lock (stdout) stdout.AppendLine(e.Data); onLine?.Invoke(e.Data); } };
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) { lock (stderr) { if (stderr.Length < 200000) stderr.AppendLine(e.Data); } onLine?.Invoke(e.Data); } };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                while (!p.WaitForExit(200))
                {
                    if (cancel.IsCancellationRequested)
                    {
                        try { p.Kill(); } catch (InvalidOperationException) { }
                        p.WaitForExit();
                        return new Result { ExitCode = -1, Cancelled = true, StdErr = stderr.ToString(), StdOut = stdout.ToString() };
                    }
                }
                p.WaitForExit();   // flush the async readers
                return new Result { ExitCode = p.ExitCode, StdErr = stderr.ToString(), StdOut = stdout.ToString() };
            }
        }

        /// <summary>Windows command-line quoting (CommandLineToArgvW rules).</summary>
        public static string Quote(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
            var sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') sb.Append('\\', backslashes * 2 + 1);
                else sb.Append('\\', backslashes);
                backslashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', backslashes * 2);
            return sb.Append('"').ToString();
        }
    }
}
