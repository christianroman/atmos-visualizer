// Phase 0, experiment 5: prove the CAF reader by measuring per-channel RMS.
//
//   CafRms <file.caf> [--float]
//
// Reads the whole file through AtmosEngine.CafReader in 1-second blocks and prints each
// channel's RMS in dBFS: 20*log10(sqrt(mean(x^2))), x in [-1, 1). By default the samples go
// through ReadDouble (exact); --float uses ReadFloat, the path the player uses.

using System;
using System.Diagnostics;
using AtmosEngine;

static class CafRms
{
    static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: CafRms <file.caf> [--float]");
            return 2;
        }
        bool useFloat = args.Length > 1 && args[1] == "--float";
        var sw = Stopwatch.StartNew();
        using (var r = new CafReader(args[0]))
        {
            Console.WriteLine("file      : " + r.Path);
            Console.WriteLine("format    : {0} Hz, {1} ch, {2}-bit {3} {4}-endian, {5} bytes/frame",
                r.SampleRate, r.Channels, r.BitsPerChannel, r.IsFloat ? "float" : "int",
                r.IsLittleEndian ? "little" : "big", r.BytesPerFrame);
            Console.WriteLine("data      : offset {0}, {1} frames = {2:F4} s", r.DataOffset, r.FrameCount, r.DurationSeconds);
            Console.WriteLine("read path : " + (useFloat ? "ReadFloat (player path)" : "ReadDouble (exact)"));

            int block = (int)r.SampleRate;
            var sumSq = new double[r.Channels];
            var peak = new double[r.Channels];
            var d = new double[block * r.Channels];
            var f = new float[block * r.Channels];
            long total = 0;
            for (long pos = 0; pos < r.FrameCount; pos += block)
            {
                int got;
                if (useFloat)
                {
                    got = r.ReadFloat(pos, f, block);
                    for (int i = 0; i < got * r.Channels; i++) d[i] = f[i];
                }
                else
                {
                    got = r.ReadDouble(pos, d, block);
                }
                for (int i = 0, c = 0; i < got * r.Channels; i++)
                {
                    double v = d[i];
                    sumSq[c] += v * v;
                    double a = Math.Abs(v);
                    if (a > peak[c]) peak[c] = a;
                    if (++c == r.Channels) c = 0;
                }
                total += got;
            }
            Console.WriteLine("frames read: {0} in {1:F1} s", total, sw.Elapsed.TotalSeconds);
            Console.WriteLine("ch   RMS dBFS      peak dBFS");
            for (int c = 0; c < r.Channels; c++)
            {
                double rms = Math.Sqrt(sumSq[c] / total);
                Console.WriteLine("{0,-4} {1,-13} {2}", c + 1, Db(rms), Db(peak[c]));
            }
        }
        return 0;
    }

    static string Db(double linear) => linear > 0 ? (20 * Math.Log10(linear)).ToString("F6") : "-inf";
}
