// Decodes a movie's video live with ffmpeg, at its original resolution, while it plays - no
// converted copy. ffmpeg (hardware decoding when the GPU offers it) writes raw YUV frames to a
// pipe; a reader thread collects them, each with its exact timestamp, into a small queue that the
// app takes from by audio time. Seeking restarts ffmpeg at the new position.
//
// Formats: 10-bit sources come out as P010 (16-bit samples, the HDR case), 8-bit ones as NV12.
// Either way: a full-resolution luma plane followed by a half-resolution interleaved chroma plane.
// Colour conversion (and HDR tonemapping) is left to the GPU.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace AtmosEngine
{
    public sealed class VideoFrame
    {
        public byte[] Data;
        public double Pts;          // seconds, on the source file's timeline
        internal long Generation;
    }

    public sealed class FfmpegVideoReader : IDisposable
    {
        public int Width { get; }
        public int Height { get; }
        public bool TenBit { get; }
        public int FrameBytes { get; }
        public double Fps { get; }
        public volatile string Error;
        public bool Ended => ended;
        public long FramesDecoded => Interlocked.Read(ref framesDecoded);

        readonly string ffmpeg, source;
        volatile string hwaccel;           // "d3d11va" (any GPU vendor) until it fails, then "none" (CPU)
        volatile bool hwFailed;
        double lastStart;
        public string Decoder => hwaccel == "none" ? "CPU" : "GPU (" + hwaccel + ")";
        readonly int stream;
        const int QueueFrames = 6;

        readonly ConcurrentQueue<VideoFrame> ready = new ConcurrentQueue<VideoFrame>();
        readonly ConcurrentBag<byte[]> pool = new ConcurrentBag<byte[]>();
        readonly SemaphoreSlim slots = new SemaphoreSlim(QueueFrames, QueueFrames);
        readonly object processLock = new object();
        Process process;
        long generation;
        volatile bool ended;
        long framesDecoded;
        readonly double fileStart;
        static readonly Regex PtsTime = new Regex(@"pts_time:\s*(-?[0-9.]+(?:e[-+]?[0-9]+)?)", RegexOptions.Compiled);

        /// <param name="fileStart">the file's own start time (ffprobe's format start_time): 0 in an
        /// MKV, 600 s in a Blu-ray .m2ts</param>
        public FfmpegVideoReader(string ffmpegPath, string sourcePath, int streamIndex, int width, int height, bool tenBit,
                                 double fps, string hwaccel = "d3d11va", double fileStart = 0)
        {
            ffmpeg = ffmpegPath;
            source = sourcePath;
            this.fileStart = fileStart;
            stream = streamIndex;
            Width = width;
            Height = height;
            TenBit = tenBit;
            Fps = fps > 0 ? fps : 24;
            this.hwaccel = hwaccel;
            FrameBytes = width * height * 3 / 2 * (tenBit ? 2 : 1);
        }

        /// <summary>(Re)start decoding so the first frame is the one at the given source time.</summary>
        public void Start(double seconds)
        {
            lock (processLock)
            {
                StopProcess();
                long gen = Interlocked.Increment(ref generation);
                Drain();
                ended = false;
                Error = null;
                hwFailed = false;
                lastStart = seconds;

                string fmt = TenBit ? "p010le" : "nv12";
                var args = new[]
                {
                    "-hide_banner", "-nostdin", "-nostats", "-loglevel", "info",
                    "-hwaccel", hwaccel,
                    // seconds is on the file's clock (as are the frame times -copyts keeps), but
                    // -ss counts from the file's start: they differ when that isn't 0 (.m2ts)
                    "-ss", Math.Max(0, seconds - fileStart).ToString("0.######", CultureInfo.InvariantCulture), "-copyts",
                    "-i", source,
                    "-map", "0:" + stream, "-an", "-sn", "-dn",
                    "-fps_mode", "passthrough",                        // one output frame per decoded frame
                    "-vf", "format=" + fmt + ",showinfo=checksum=0",   // showinfo logs each frame's timestamp
                    "-f", "rawvideo", "-pix_fmt", fmt, "pipe:1",
                };
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = string.Join(" ", Array.ConvertAll(args, ProcessRunner.Quote)),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                var p = new Process { StartInfo = psi };
                p.Start();
                process = p;
                var timestamps = new BlockingCollection<double>();
                new Thread(() => ReadStderr(p, timestamps, gen)) { IsBackground = true, Name = "ffmpeg video log" }.Start();
                new Thread(() => ReadFrames(p, timestamps, gen)) { IsBackground = true, Name = "ffmpeg video frames" }.Start();
            }
        }

        void ReadStderr(Process p, BlockingCollection<double> timestamps, long gen)
        {
            try
            {
                string line;
                var errs = new System.Text.StringBuilder();
                while ((line = p.StandardError.ReadLine()) != null)
                {
                    if (line.IndexOf("Device creation failed", StringComparison.OrdinalIgnoreCase) >= 0
                        || line.IndexOf("Failed setup for format", StringComparison.OrdinalIgnoreCase) >= 0
                        || line.IndexOf("hwaccel initialisation returned error", StringComparison.OrdinalIgnoreCase) >= 0)
                        hwFailed = true;
                    if (line.Contains("showinfo"))
                    {
                        var m = PtsTime.Match(line);
                        if (m.Success) timestamps.Add(double.Parse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture));
                    }
                    else if (line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 && errs.Length < 2000)
                    {
                        errs.AppendLine(line.Trim());
                    }
                }
                if (Interlocked.Read(ref generation) == gen && errs.Length > 0 && FramesDecoded == 0) Error = errs.ToString().Trim();
            }
            catch (Exception) { /* process killed */ }
            finally
            {
                timestamps.CompleteAdding();
            }
        }

        void ReadFrames(Process p, BlockingCollection<double> timestamps, long gen)
        {
            var output = p.StandardOutput.BaseStream;
            double lastPts = double.NaN;
            try
            {
                while (Interlocked.Read(ref generation) == gen)
                {
                    slots.Wait();                                 // backpressure: at most QueueFrames waiting
                    if (Interlocked.Read(ref generation) != gen) { slots.Release(); break; }
                    if (!pool.TryTake(out var buffer) || buffer.Length != FrameBytes) buffer = new byte[FrameBytes];
                    int got = 0;
                    while (got < FrameBytes)
                    {
                        int r = output.Read(buffer, got, FrameBytes - got);
                        if (r <= 0) break;
                        got += r;
                    }
                    if (got < FrameBytes)
                    {
                        pool.Add(buffer);
                        slots.Release();
                        if (Interlocked.Read(ref generation) != gen) break;
                        p.WaitForExit(2000);
                        if (FramesDecoded == 0 && hwaccel != "none" && (hwFailed || p.ExitCode != 0))
                        {
                            // hardware decoding unavailable here: carry on with the CPU decoder
                            hwaccel = "none";
                            var start = lastStart;
                            new Thread(() => Start(start)) { IsBackground = true }.Start();
                            break;
                        }
                        ended = true;   // end of file (or ffmpeg stopped)
                        break;
                    }
                    // the timestamp line normally arrives just before the frame's bytes
                    double pts;
                    if (!timestamps.TryTake(out pts, 500))
                        pts = double.IsNaN(lastPts) ? 0 : lastPts + 1.0 / Fps;
                    lastPts = pts;
                    Interlocked.Increment(ref framesDecoded);
                    ready.Enqueue(new VideoFrame { Data = buffer, Pts = pts, Generation = gen });
                }
            }
            catch (Exception)
            {
                // pipe closed by a restart or Dispose
            }
        }

        /// <summary>The newest decoded frame at or before pts (older ones are recycled), or null if
        /// none has reached that time yet. Pass the frame back with Recycle when done with it.</summary>
        public VideoFrame TakeFrameFor(double pts)
        {
            long gen = Interlocked.Read(ref generation);
            VideoFrame best = null;
            while (ready.TryPeek(out var f))
            {
                if (f.Generation != gen) { ready.TryDequeue(out f); Recycle(f); continue; }
                if (f.Pts > pts + 0.002) break;
                ready.TryDequeue(out f);
                if (best != null) Recycle(best);
                best = f;
            }
            return best;
        }

        /// <summary>Timestamp of the next frame waiting, if any.</summary>
        public double? NextPts => ready.TryPeek(out var f) && f.Generation == Interlocked.Read(ref generation) ? f.Pts : (double?)null;

        public void Recycle(VideoFrame f)
        {
            if (f == null) return;
            if (f.Data != null && f.Data.Length == FrameBytes) pool.Add(f.Data);
            f.Data = null;
            slots.Release();
        }

        void Drain()
        {
            while (ready.TryDequeue(out var f)) Recycle(f);
        }

        void StopProcess()
        {
            var p = process;
            process = null;
            if (p == null) return;
            try { if (!p.HasExited) p.Kill(); } catch (Exception) { }
            try { p.WaitForExit(1000); } catch (Exception) { }
            p.Dispose();
        }

        public void Dispose()
        {
            lock (processLock)
            {
                Interlocked.Increment(ref generation);
                StopProcess();
                Drain();
            }
        }
    }
}
