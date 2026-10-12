// Tests StreamingPlayer (disk thread + lock-free ring buffer + mixing) outside Unity.
//
// Simulates the audio thread: calls Render with 1024-frame buffers, seeks mid-stream, and checks
// that every frame it outputs equals an offline render of the same file frames (Mixer applied
// directly to CafReader data). Any ring-buffer, wrap-around or seek bug shows up as a mismatch.
//
//   StreamTest <NAME.atmos>

using System;
using System.Diagnostics;
using System.Threading;
using AtmosEngine;

static class StreamTest
{
    static int Main(string[] args)
    {
        var damf = DamfFile.Load(args[0]);
        string dir = System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "UnityProject", "Assets", "StreamingAssets", "Layouts");
        var presets = LayoutPreset.LoadAll(dir);
        var stereo = presets.Find(p => p.Name == "2.0");
        var meterPreset = presets.Find(p => p.Name == "9.1.6");
        using (var caf = new CafReader(damf.AudioPath))
        {
            var timeline = new GainTimeline(damf, stereo);
            var player = new StreamingPlayer(damf, timeline, bufferSeconds: 0.5);   // small ring: many wrap-arounds
            int meterId = player.SetMeterTimeline(new GainTimeline(damf, meterPreset));   // metering on, as in the app
            const int block = 1024, outCh = 2;
            var data = new float[block * outCh];
            var refIn = new float[block * caf.Channels];
            var refOut = new float[block * timeline.Channels];

            long checkedFrames = 0, mismatches = 0, silentCalls = 0, calls = 0;
            double maxDiff = 0;
            // (seek target seconds, real-time seconds to play after it); paced like real audio callbacks
            var plan = new[] { (20.0, 3.0), (100.0, 2.0), (28.0, 2.0), (300.5, 1.0), (0.0, 1.0) };
            var sw = Stopwatch.StartNew();
            foreach (var (target, seconds) in plan)
            {
                player.Seek(target);
                player.Playing = true;
                long expectedNext = -1;
                var until = sw.Elapsed.TotalSeconds + seconds;
                while (sw.Elapsed.TotalSeconds < until)
                {
                    int got = player.Render(data, block, outCh);
                    calls++;
                    if (got == 0) { silentCalls++; Thread.Sleep(1); continue; }
                    long start = player.LastBlockStartFrame;
                    if (expectedNext >= 0 && start != expectedNext)
                        throw new Exception("discontinuity: expected frame " + expectedNext + ", got " + start);
                    if (expectedNext < 0 && start != (long)Math.Round(target * 48000))
                        throw new Exception("first frame after seek to " + target + " s is " + start);
                    expectedNext = start + got;

                    // reference: same frames straight from the file
                    int n = caf.ReadFloat(start, refIn, got);
                    Mixer.Render(timeline, start, refIn, caf.Channels, n, refOut);
                    for (int i = 0; i < got * outCh; i++)
                    {
                        double d = Math.Abs(data[i] - refOut[i]);
                        if (d > 0) mismatches++;
                        if (d > maxDiff) maxDiff = d;
                    }
                    checkedFrames += got;
                    Thread.Sleep(15);   // roughly real time for 1024-frame buffers at 48 kHz (21 ms)
                }
                Console.WriteLine("seek to {0,6:F1} s: played through frame {1} ({2:F3} s)", target, expectedNext, expectedNext / 48000.0);
            }
            Console.WriteLine("calls {0}, silent (priming) calls {1}, underruns {2}, frames checked {3}, mismatching samples {4}, max |diff| {5}",
                calls, silentCalls, player.Underruns, checkedFrames, mismatches, maxDiff);
            Console.WriteLine("max render time per call {0:F3} ms (stereo output + 9.1.6 metering)", player.MaxRenderMs);
            var spk = new double[meterPreset.Speakers.Count];
            var el = new double[damf.Elements.Count];
            int mf = player.ReadMeters(0, long.MaxValue, meterId, spk, el);
            Console.WriteLine("meters readable: {0} frames in the last buffers; e.g. {1} {2:F1} dBFS", mf,
                meterPreset.Speakers[2].Name, 10 * Math.Log10(Math.Max(1e-20, spk[2])));
            player.Dispose();
            return mismatches == 0 ? 0 : 1;
        }
    }
}
