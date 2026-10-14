// Offline render of a DAMF file to a speaker layout, written as 32-bit float WAV.
//
//   RenderDamf <NAME.atmos> <preset> <out.wav> [startSeconds] [durationSeconds] [--polar]
//
// preset: a layout preset name ("7.1.4", "5.1.2", ...) from UnityProject/Assets/StreamingAssets/
// Layouts, or a path to a preset .yaml. Objects use BS.2127's room-relative panner unless
// --polar (libear's angle route, for comparison). Uses the same engine code as the Unity player
// (DamfFile, CafReader, GainTimeline, Mixer), so its output can be checked independently.

using System;
using System.Diagnostics;
using System.IO;
using AtmosEngine;

static class RenderDamf
{
    static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: RenderDamf <NAME.atmos> <layout> <out.wav> [startSeconds] [durationSeconds]");
            return 2;
        }
        var sw = Stopwatch.StartNew();
        var damf = DamfFile.Load(args[0]);
        var preset = FindPreset(args[1]);
        bool polar = Array.IndexOf(args, "--polar") >= 0;
        var numeric = Array.FindAll(args, a => !a.StartsWith("--"));
        using (var caf = damf.OpenAudio())
        {
            Console.WriteLine("ear_shim  : " + EarShim.Version);
            Console.WriteLine("DAMF      : {0} elements ({1} bed, {2} objects), {3} events",
                damf.Elements.Count, CountBeds(damf), damf.Elements.Count - CountBeds(damf), CountEvents(damf));
            Console.WriteLine("audio     : {0} ch, {1} frames", caf.Channels, caf.FrameCount);
            if (caf.Channels != damf.Elements.Count)
                throw new InvalidDataException("CAF has " + caf.Channels + " channels but the header lists " + damf.Elements.Count + " elements");

            var timeline = new GainTimeline(damf, preset, polar ? ObjectPanning.LibearPolar : ObjectPanning.Allocentric);
            var names = preset.Speakers.ConvertAll(sp => sp.Name);
            Console.WriteLine("layout    : {0} ({1}), objects panned {2}", preset.Name, string.Join(" ", names),
                polar ? "via libear angles (comparison)" : "room-relative (BS.2127 allocentric)");
            Console.WriteLine("gains     : computed in {0:F2} s", sw.Elapsed.TotalSeconds);

            long start = numeric.Length > 3 ? (long)Math.Round(double.Parse(numeric[3], System.Globalization.CultureInfo.InvariantCulture) * caf.SampleRate) : 0;
            long count = numeric.Length > 4 ? (long)Math.Round(double.Parse(numeric[4], System.Globalization.CultureInfo.InvariantCulture) * caf.SampleRate) : caf.FrameCount - start;
            count = Math.Min(count, caf.FrameCount - start);

            int block = 4096;
            var input = new float[block * caf.Channels];
            var output = new float[block * timeline.Channels];
            var sumSq = new double[timeline.Channels];
            var peak = new double[timeline.Channels];
            var over = new long[timeline.Channels];
            var renderTime = new Stopwatch();
            using (var wav = new WavWriter(args[2], timeline.Channels, (int)caf.SampleRate))
            {
                for (long pos = start; pos < start + count; pos += block)
                {
                    int want = (int)Math.Min(block, start + count - pos);
                    int got = caf.ReadFloat(pos, input, want);
                    renderTime.Start();
                    Mixer.Render(timeline, pos, input, caf.Channels, got, output);
                    renderTime.Stop();
                    for (int i = 0; i < got * timeline.Channels; i++)
                    {
                        int k = i % timeline.Channels;
                        double v = output[i];
                        sumSq[k] += v * v;
                        if (Math.Abs(v) > peak[k]) peak[k] = Math.Abs(v);
                        if (Math.Abs(v) > 1.0) over[k]++;
                    }
                    wav.Write(output, got * timeline.Channels);
                }
            }
            double seconds = count / caf.SampleRate;
            Console.WriteLine("rendered  : {0} frames ({1:F1} s of audio) from frame {2}; mixing took {3:F2} s ({4:F0}x real time)",
                count, seconds, start, renderTime.Elapsed.TotalSeconds, seconds / Math.Max(1e-9, renderTime.Elapsed.TotalSeconds));
            Console.WriteLine("output         RMS dBFS   peak dBFS   samples over 0 dBFS");
            for (int k = 0; k < timeline.Channels; k++)
            {
                double rms = Math.Sqrt(sumSq[k] / count);
                Console.WriteLine("  {0,-10} {1,10} {2,10}   {3}", names[k], rms > 0 ? (20 * Math.Log10(rms)).ToString("F3") : "-inf",
                    peak[k] > 0 ? (20 * Math.Log10(peak[k])).ToString("F2") : "-inf", over[k]);
            }
        }
        return 0;
    }

    static LayoutPreset FindPreset(string nameOrPath)
    {
        if (File.Exists(nameOrPath)) return LayoutPreset.Load(nameOrPath);
        string dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "UnityProject", "Assets", "StreamingAssets", "Layouts");
        foreach (var p in LayoutPreset.LoadAll(dir))
            if (p.Name == nameOrPath) return p;
        throw new ArgumentException("no layout preset named '" + nameOrPath + "' in " + Path.GetFullPath(dir));
    }

    static int CountBeds(DamfFile d) { int n = 0; foreach (var e in d.Elements) if (e.IsBed) n++; return n; }
    static int CountEvents(DamfFile d) { int n = 0; foreach (var e in d.Elements) n += e.Events.Count; return n; }

    /// <summary>Minimal WAV writer: IEEE float, interleaved.</summary>
    sealed class WavWriter : IDisposable
    {
        readonly BinaryWriter w;
        readonly int channels;
        long dataBytes;

        public WavWriter(string path, int channels, int sampleRate)
        {
            this.channels = channels;
            w = new BinaryWriter(File.Create(path));
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(0);
            w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            w.Write(System.Text.Encoding.ASCII.GetBytes("fmt ")); w.Write(16);
            w.Write((short)3); w.Write((short)channels); w.Write(sampleRate);
            w.Write(sampleRate * channels * 4); w.Write((short)(channels * 4)); w.Write((short)32);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(0);
        }

        public void Write(float[] samples, int count)
        {
            for (int i = 0; i < count; i++) w.Write(samples[i]);
            dataBytes += count * 4L;
        }

        public void Dispose()
        {
            w.Seek(4, SeekOrigin.Begin); w.Write((int)(36 + dataBytes));
            w.Seek(40, SeekOrigin.Begin); w.Write((int)dataBytes);
            w.Dispose();
        }
    }
}
