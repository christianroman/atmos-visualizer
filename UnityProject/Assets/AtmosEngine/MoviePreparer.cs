// Turns a movie file (e.g. a Blu-ray MKV with a TrueHD Atmos track) into what the app plays:
//   1. Read the movie        ffprobe: find the TrueHD Atmos track and the video's properties
//   2. Extract TrueHD        mkvextract (byte-exact) - or ffmpeg for other containers
//   3. Decode Atmos          truehdd -> DAMF: NAME.atmos, .atmos.metadata, .atmos.audio (CAF)
// The video is not converted: the app decodes the original live while it plays
// (FfmpegVideoReader). Results go into a per-movie cache folder, so this happens once.
//
// Runs on a background thread; the app polls the public state.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace AtmosEngine
{
    public sealed class PrepStep
    {
        public string Name;
        public volatile int State;          // 0 waiting, 1 running, 2 done, 3 failed, 4 skipped
        public double Progress;             // 0..1 while running
        public string Detail = "";
        public DateTime Started;

        public const int Waiting = 0, Running = 1, Done = 2, Failed = 3, Skipped = 4;

        public TimeSpan? Remaining()
        {
            if (State != Running || Progress < 0.03) return null;
            double elapsed = (DateTime.UtcNow - Started).TotalSeconds;
            return TimeSpan.FromSeconds(elapsed / Progress * (1 - Progress));
        }
    }

    /// <summary>What ffprobe says about a movie file, and what the player needs to show its video.</summary>
    public sealed class MovieInfo
    {
        public string SourcePath = "";
        public double DurationSeconds;
        public int AudioStream = -1, VideoStream = -1;
        public string AudioProfile = "", VideoCodec = "", PixFmt = "", Transfer = "", ColorSpace = "";
        public int Width, Height;
        public double Fps;
        public double AudioStart, VideoStart;   // stream start times on the file's timeline
        public double AudioBitrate;             // bits per second, the track's average (0 = unknown)
        public bool DdPlusAtmos, TrueHdWithoutAtmos;

        public bool HasVideo => VideoStream >= 0 && Width > 0 && Height > 0;
        public bool TenBit => PixFmt.Contains("10");
        public bool IsPq => Transfer == "smpte2084";
        public bool IsBt2020 => ColorSpace.StartsWith("bt2020", StringComparison.Ordinal);
        public string VideoSummary => !HasVideo ? "no video"
            : VideoCodec.ToUpperInvariant() + " " + Width + "x" + Height + (IsPq ? " HDR10" : TenBit ? " 10-bit" : "")
              + " " + Fps.ToString("0.###", CultureInfo.InvariantCulture) + " fps";

        /// <summary>Runs ffprobe on a file.</summary>
        public static MovieInfo Probe(ExternalTools tools, string path, CancellationToken cancel = default)
        {
            var r = ProcessRunner.Run(tools.Ffprobe, new[] { "-v", "error", "-show_entries",
                "stream=index,codec_type,codec_name,profile,width,height,pix_fmt,r_frame_rate,color_transfer,color_space,start_time,bit_rate"
                + ":stream_tags=BPS,BPS-eng,NUMBER_OF_BYTES,NUMBER_OF_BYTES-eng,DURATION,DURATION-eng:format=duration",
                "-of", "compact=p=0", path }, null, cancel);
            if (r.ExitCode != 0)
                throw new InvalidDataException("ffprobe couldn't read the file: " + (r.StdErr ?? "").Split('\n').FirstOrDefault()?.Trim());

            var info = new MovieInfo { SourcePath = Path.GetFullPath(path) };
            foreach (var line in r.StdOut.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
            {
                var kv = line.Split('|').Select(p => p.Split(new[] { '=' }, 2)).Where(p => p.Length == 2)
                             .ToDictionary(p => p[0], p => p[1]);
                if (kv.TryGetValue("duration", out var dur) && !kv.ContainsKey("index"))
                {
                    double.TryParse(dur, NumberStyles.Float, CultureInfo.InvariantCulture, out info.DurationSeconds);
                    continue;
                }
                if (!kv.TryGetValue("index", out var idx)) continue;
                int index = int.Parse(idx, CultureInfo.InvariantCulture);
                string type = Get(kv, "codec_type"), codec = Get(kv, "codec_name"), profile = Get(kv, "profile");
                double start = 0;
                double.TryParse(Get(kv, "start_time"), NumberStyles.Float, CultureInfo.InvariantCulture, out start);
                if (type == "audio" && codec == "truehd")
                {
                    bool isAtmos = profile.IndexOf("Atmos", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (isAtmos && info.AudioStream < 0)
                    {
                        info.AudioStream = index;
                        info.AudioProfile = profile;
                        info.AudioStart = start;
                        info.AudioBitrate = Bitrate(kv);
                    }
                    else if (!isAtmos) info.TrueHdWithoutAtmos = true;
                }
                else if (type == "audio" && codec == "eac3" && profile.IndexOf("Atmos", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    info.DdPlusAtmos = true;
                }
                else if (type == "video" && info.VideoStream < 0 && codec != "mjpeg" && codec != "png")
                {
                    info.VideoStream = index;
                    info.VideoCodec = codec;
                    int.TryParse(Get(kv, "width"), out info.Width);
                    int.TryParse(Get(kv, "height"), out info.Height);
                    info.PixFmt = Get(kv, "pix_fmt");
                    info.Transfer = Get(kv, "color_transfer");
                    info.ColorSpace = Get(kv, "color_space");
                    info.Fps = ParseRate(Get(kv, "r_frame_rate"));
                    info.VideoStart = start;
                }
            }
            return info;
        }

        // ---- persistence in the cache folder ("key: value" lines) ----

        public void Save(string file)
        {
            var c = CultureInfo.InvariantCulture;
            var lines = new[]
            {
                "source: " + SourcePath, "duration: " + DurationSeconds.ToString(c),
                "audioStream: " + AudioStream, "audioProfile: " + AudioProfile, "audioStart: " + AudioStart.ToString(c),
                "audioBitrate: " + AudioBitrate.ToString("0", c),
                "videoStream: " + VideoStream, "videoCodec: " + VideoCodec, "width: " + Width, "height: " + Height,
                "pixFmt: " + PixFmt, "transfer: " + Transfer, "colorSpace: " + ColorSpace,
                "fps: " + Fps.ToString("R", c), "videoStart: " + VideoStart.ToString(c),
            };
            File.WriteAllText(file, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        }

        public static MovieInfo Load(string file)
        {
            var d = new Dictionary<string, string>();
            foreach (var line in File.ReadAllLines(file))
            {
                int i = line.IndexOf(": ", StringComparison.Ordinal);
                if (i > 0) d[line.Substring(0, i)] = line.Substring(i + 2).Trim();
            }
            var c = CultureInfo.InvariantCulture;
            double D(string k) => d.TryGetValue(k, out var v) && double.TryParse(v, NumberStyles.Float, c, out var x) ? x : 0;
            int I(string k) => d.TryGetValue(k, out var v) && int.TryParse(v, NumberStyles.Integer, c, out var x) ? x : -1;
            string S(string k) => d.TryGetValue(k, out var v) ? v : "";
            return new MovieInfo
            {
                SourcePath = S("source"), DurationSeconds = D("duration"),
                AudioStream = I("audioStream"), AudioProfile = S("audioProfile"), AudioStart = D("audioStart"), AudioBitrate = D("audioBitrate"),
                VideoStream = I("videoStream"), VideoCodec = S("videoCodec"), Width = Math.Max(0, I("width")), Height = Math.Max(0, I("height")),
                PixFmt = S("pixFmt"), Transfer = S("transfer"), ColorSpace = S("colorSpace"), Fps = D("fps"), VideoStart = D("videoStart"),
            };
        }

        /// <summary>For a .atmos file inside the app's cache: the movie it was decoded from.</summary>
        public static MovieInfo ForCachedAtmos(string atmosPath)
        {
            try
            {
                string dir = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(atmosPath)));
                string file = Path.Combine(dir ?? "", "source.txt");
                return File.Exists(file) ? Load(file) : null;
            }
            catch (Exception) { return null; }
        }

        static string Get(Dictionary<string, string> d, string k) => d.TryGetValue(k, out var v) ? v : "";

        /// <summary>A stream's average bitrate. MKV files rarely state it directly; MKVToolNix
        /// writes statistics tags (BPS, or the byte count and duration) that give it.</summary>
        static double Bitrate(Dictionary<string, string> kv)
        {
            var c = CultureInfo.InvariantCulture;
            foreach (var k in new[] { "bit_rate", "tag:BPS", "tag:BPS-eng" })
                if (double.TryParse(Get(kv, k), NumberStyles.Float, c, out double b) && b > 0) return b;
            foreach (var suffix in new[] { "", "-eng" })
                if (double.TryParse(Get(kv, "tag:NUMBER_OF_BYTES" + suffix), NumberStyles.Float, c, out double bytes)
                    && TimeSpan.TryParse(Get(kv, "tag:DURATION" + suffix).Split('.')[0], c, out var dur) && dur.TotalSeconds > 0)
                    return bytes * 8 / dur.TotalSeconds;
            return 0;
        }

        static double ParseRate(string rate)
        {
            var p = rate.Split('/');
            if (p.Length == 2 && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double a)
                && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double b) && b > 0)
                return a / b;
            return 0;
        }
    }

    public sealed class MoviePreparer
    {
        public string SourcePath { get; }
        public string CacheDir { get; private set; }
        public MovieInfo Info { get; private set; } = new MovieInfo();
        public PrepStep Probe { get; } = new PrepStep { Name = "Read the movie" };
        public PrepStep Extract { get; } = new PrepStep { Name = "Extract the TrueHD track" };
        public PrepStep Decode { get; } = new PrepStep { Name = "Decode the Atmos objects" };
        public IReadOnlyList<PrepStep> Steps => new[] { Probe, Extract, Decode };

        /// <summary>Set when something fails; a sentence for the user.</summary>
        public volatile string Error;
        public string AtmosPath { get; private set; }   // valid once AudioReady
        public bool AudioReady => Decode.State == PrepStep.Done;
        public bool Finished { get; private set; }
        public bool FromCache { get; private set; }

        readonly string cacheRoot;
        readonly ExternalTools tools;
        readonly CancellationTokenSource cts = new CancellationTokenSource();

        public MoviePreparer(string sourcePath, string cacheRoot, ExternalTools tools)
        {
            SourcePath = Path.GetFullPath(sourcePath);
            this.cacheRoot = cacheRoot;
            this.tools = tools;
        }

        public void Start() => new Thread(Run) { IsBackground = true, Name = "Movie preparer" }.Start();

        public void Cancel() => cts.Cancel();

        void Run()
        {
            try
            {
                RunProbe();
                if (Error != null) return;
                Guard(Extract, RunExtract);
                if (Extract.State == PrepStep.Done || Extract.State == PrepStep.Skipped) Guard(Decode, RunDecode);
                if (cts.IsCancellationRequested && Error == null) Error = "Cancelled.";
            }
            catch (Exception e)
            {
                if (Error == null) Error = e.Message;
            }
            finally
            {
                Finished = true;
            }
        }

        void Guard(PrepStep step, Action body)
        {
            if (step.State == PrepStep.Done || step.State == PrepStep.Skipped) return;
            try
            {
                step.State = PrepStep.Running;
                step.Started = DateTime.UtcNow;
                body();
                if (step.State == PrepStep.Running) step.State = PrepStep.Done;
            }
            catch (Exception e)
            {
                step.State = PrepStep.Failed;
                step.Detail = cts.IsCancellationRequested ? "cancelled" : e.Message;
                if (!cts.IsCancellationRequested && Error == null) Error = step.Name + ": " + e.Message;
            }
        }

        // ---- 1. probe ------------------------------------------------------------------------

        void RunProbe()
        {
            Probe.State = PrepStep.Running;
            Probe.Started = DateTime.UtcNow;
            var missing = tools.Missing().ToList();
            if (missing.Count > 0)
                throw Fail(Probe, "Missing tools: " + string.Join(", ", missing) + ". Install them, or list their paths in settings.yaml.");
            if (!File.Exists(SourcePath)) throw Fail(Probe, "File not found: " + SourcePath);

            try { Info = MovieInfo.Probe(tools, SourcePath, cts.Token); }
            catch (Exception e) { throw Fail(Probe, e.Message); }

            if (Info.AudioStream < 0)
            {
                if (Info.DdPlusAtmos)
                    throw Fail(Probe, "This file's Atmos is Dolby Digital Plus (E-AC-3 JOC), as on streaming services and demo clips. "
                                     + "Only TrueHD Atmos (Blu-ray) can be decoded into objects: there is no open decoder for DD+ Atmos objects.");
                throw Fail(Probe, Info.TrueHdWithoutAtmos ? "The TrueHD track has no Atmos, so there are no objects to show."
                                                         : "No Dolby TrueHD Atmos track found in this file.");
            }

            // one cache folder per file version (path, size and modification time)
            var fi = new FileInfo(SourcePath);
            string key = SourcePath + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
            string hash;
            using (var sha = SHA1.Create())
                hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "").Substring(0, 8).ToLowerInvariant();
            CacheDir = Path.Combine(cacheRoot, SafeName(Path.GetFileNameWithoutExtension(SourcePath)) + "-" + hash);
            Directory.CreateDirectory(CacheDir);
            Info.Save(Path.Combine(CacheDir, "source.txt"));

            string atmos = Path.Combine(CacheDir, "atmos", "movie.atmos");
            if (File.Exists(Path.Combine(CacheDir, "audio.done")) && File.Exists(atmos))
            {
                AtmosPath = atmos;
                Extract.State = PrepStep.Skipped;
                Decode.State = PrepStep.Done;
                Decode.Detail = "from cache";
                FromCache = true;
            }
            Probe.Detail = Describe();
            Probe.State = PrepStep.Done;
        }

        public string Describe()
        {
            var t = TimeSpan.FromSeconds(Info.DurationSeconds);
            return ((int)t.TotalHours > 0 ? (int)t.TotalHours + "h " : "") + t.Minutes + "m " + t.Seconds + "s, "
                   + Info.AudioProfile + ", " + Info.VideoSummary;
        }

        // ---- 2. extract -------------------------------------------------------------------------

        string ThdPath => Path.Combine(CacheDir, "audio.thd");

        void RunExtract()
        {
            string ext = Path.GetExtension(SourcePath).ToLowerInvariant();
            bool matroska = ext == ".mkv" || ext == ".mka" || ext == ".mk3d" || ext == ".webm";
            ProcessRunner.Result r;
            if (matroska)
            {
                // mkvextract's track IDs are the stream indexes for Matroska; --gui-mode prints
                // "#GUI#progress NN%" lines we can follow.
                r = ProcessRunner.Run(tools.Mkvextract, new[] { "--gui-mode", SourcePath, "tracks", Info.AudioStream + ":" + ThdPath },
                    line =>
                    {
                        int i = line.IndexOf("#GUI#progress ", StringComparison.Ordinal);
                        if (i >= 0 && int.TryParse(line.Substring(i + 14).TrimEnd('%', ' '), out int pct)) Extract.Progress = pct / 100.0;
                    }, cts.Token);
            }
            else
            {
                r = ProcessRunner.Run(tools.Ffmpeg, new[] { "-hide_banner", "-nostdin", "-y", "-i", SourcePath, "-map", "0:" + Info.AudioStream,
                    "-c", "copy", "-f", "truehd", "-progress", "pipe:1", "-nostats", ThdPath }, FfmpegProgress(Extract), cts.Token);
            }
            Check(r, "extraction");
            if (!File.Exists(ThdPath) || new FileInfo(ThdPath).Length == 0) throw new Exception("no TrueHD data was extracted");
            Extract.Detail = Gb(new FileInfo(ThdPath).Length);
        }

        // ---- 3. decode ----------------------------------------------------------------------------

        void RunDecode()
        {
            string dir = Path.Combine(CacheDir, "atmos");
            Directory.CreateDirectory(dir);
            string basePath = Path.Combine(dir, "movie");
            string caf = basePath + ".atmos.audio";
            foreach (var f in Directory.GetFiles(dir)) File.Delete(f);   // remove a partial earlier attempt

            // truehdd's progress bars aren't line-based, so follow the size of the audio it writes
            var watch = new Timer(_ => Decode.Progress = Math.Min(0.99, EstimateCafProgress(caf)), null, 500, 500);
            ProcessRunner.Result r;
            try
            {
                r = ProcessRunner.Run(tools.Truehdd, new[] { "decode", ThdPath, "--presentation", "3", "--output-path", basePath, "--loglevel", "warn" },
                    null, cts.Token);
            }
            finally
            {
                watch.Dispose();
            }
            if (r.StdErr.IndexOf("ISF", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new Exception("this track uses ISF objects, which truehdd can't decode yet");
            Check(r, "truehdd");

            string atmos = basePath + ".atmos";
            if (!File.Exists(atmos)) throw new Exception("truehdd wrote no .atmos file");
            var damf = DamfFile.Load(atmos);                      // make sure it reads
            using (var reader = new CafReader(damf.AudioPath))
                if (reader.Channels != damf.Elements.Count)
                    throw new Exception("decoded audio has " + reader.Channels + " channels but " + damf.Elements.Count + " elements");
            Decode.Detail = damf.Objects.Count() + " objects, " + Gb(new FileInfo(caf).Length);
            File.WriteAllText(Path.Combine(CacheDir, "audio.done"), DateTime.UtcNow.ToString("o"));
            try { File.Delete(ThdPath); } catch (IOException) { }   // only needed for decoding
            AtmosPath = atmos;
        }

        double EstimateCafProgress(string caf)
        {
            try
            {
                if (!File.Exists(caf) || Info.DurationSeconds <= 0) return 0;
                long size = new FileInfo(caf).Length;
                int channels = 12;
                using (var fs = new FileStream(caf, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var head = new byte[48];
                    if (fs.Read(head, 0, 48) == 48 && Encoding.ASCII.GetString(head, 8, 4) == "desc")
                        channels = (head[44] << 24) | (head[45] << 16) | (head[46] << 8) | head[47];
                }
                return size / (Info.DurationSeconds * 48000.0 * 3 * Math.Max(1, channels));
            }
            catch (IOException) { return 0; }
        }

        // ---- helpers ----------------------------------------------------------------------------------

        Action<string> FfmpegProgress(PrepStep step) => line =>
        {
            // -progress prints "out_time_us=123456" (microseconds)
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && Info.DurationSeconds > 0 &&
                long.TryParse(line.Substring(12), out long us))
                step.Progress = Math.Max(0, Math.Min(0.999, us / 1e6 / Info.DurationSeconds));
        };

        void Check(ProcessRunner.Result r, string what)
        {
            if (r.Cancelled) throw new OperationCanceledException();
            if (r.ExitCode != 0) throw new Exception(what + " failed (exit " + r.ExitCode + "): " + LastLines(r.StdErr, 3));
        }

        Exception Fail(PrepStep step, string message)
        {
            step.State = PrepStep.Failed;
            step.Detail = message;
            Error = message;
            return new InvalidOperationException(message);
        }

        static string SafeName(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s) sb.Append(c < 128 && (char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_' || c == '.') ? c : '_');
            var name = sb.ToString().Trim();
            return name.Length > 48 ? name.Substring(0, 48).Trim() : name;
        }

        static string Gb(long bytes) => (bytes / 1e9).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
        static string LastLines(string s, int n) =>
            string.Join(" / ", (s ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Reverse().Take(n).Reverse());
    }
}
