// Tests AtmosEngine.FfmpegVideoReader outside Unity: seek accuracy (first frame timestamp after a
// start/seek), decoding throughput at full resolution, and real-time consumption by "audio time".
//
//   VideoReadTest <movie> <streamIndex> <width> <height> <tenBit 0|1> <fps>

using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using AtmosEngine;

static class VideoReadTest
{
    static int Main(string[] a)
    {
        var tools = ExternalTools.Find();
        double fps = double.Parse(a[5], CultureInfo.InvariantCulture);
        var reader = new FfmpegVideoReader(tools.Ffmpeg, a[0], int.Parse(a[1]), int.Parse(a[2]), int.Parse(a[3]), a[4] == "1", fps);
        Console.WriteLine("frame size {0:F1} MB", reader.FrameBytes / 1e6);

        foreach (double start in new[] { 600.0, 100.0, 0.0 })
        {
            var sw = Stopwatch.StartNew();
            reader.Start(start);
            VideoFrame first = null;
            while (first == null && sw.Elapsed.TotalSeconds < 10)
            {
                first = reader.TakeFrameFor(double.MaxValue);
                if (first == null) Thread.Sleep(1);
            }
            if (first == null) { Console.WriteLine("no frame after start at {0}: {1}", start, reader.Error); return 1; }
            Console.WriteLine("start at {0,6:F3} s: first frame pts {1:F3} s after {2:F0} ms", start, first.Pts, sw.Elapsed.TotalMilliseconds);
            double firstPts = first.Pts;
            reader.Recycle(first);

            // as fast as possible for 10 s of film
            int n = 0;
            double last = firstPts;
            var t = Stopwatch.StartNew();
            while (last < firstPts + 10 && t.Elapsed.TotalSeconds < 20)
            {
                var f = reader.TakeFrameFor(double.MaxValue);
                if (f == null) { Thread.Sleep(0); continue; }
                if (f.Pts <= last && n > 0) Console.WriteLine("  timestamps not increasing: {0} after {1}", f.Pts, last);
                last = f.Pts;
                n++;
                reader.Recycle(f);
            }
            Console.WriteLine("  decoded {0} frames covering {1:F2} s of film in {2:F2} s = {3:F0} frames/s ({4:F1}x real time)",
                n, last - firstPts, t.Elapsed.TotalSeconds, n / t.Elapsed.TotalSeconds, (last - firstPts) / t.Elapsed.TotalSeconds);
        }

        // real-time consumption: take the frame for "now" as audio would advance
        reader.Start(300);
        var clock = Stopwatch.StartNew();
        int shown = 0, late = 0;
        double worstLag = 0;
        while (clock.Elapsed.TotalSeconds < 5)
        {
            double now = 300 + clock.Elapsed.TotalSeconds;
            var f = reader.TakeFrameFor(now);
            if (f != null)
            {
                shown++;
                double lag = now - f.Pts;
                worstLag = Math.Max(worstLag, lag);
                if (lag > 1.5 / fps) late++;
                reader.Recycle(f);
            }
            Thread.Sleep(16);   // a 60 Hz render loop
        }
        Console.WriteLine("real-time test, 5 s at 60 Hz: {0} frames shown, {1} later than 1.5 frames, worst lag {2:F0} ms (includes the start-up seek)",
            shown, late, worstLag * 1000);
        reader.Dispose();
        return 0;
    }
}
