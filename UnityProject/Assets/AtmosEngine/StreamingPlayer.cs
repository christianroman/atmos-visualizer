// Real-time playback of a DAMF file: a background thread streams element audio off disk into a
// ring buffer; the audio thread pulls from it and mixes to speaker feeds with a GainTimeline.
//
// Threads:
//   main thread   Play / Pause / Seek, reads the clock
//   disk thread   CafReader -> ring buffer (the only writer)
//   audio thread  ring buffer -> Mixer -> output (the only reader); must not block or allocate
//
// The ring buffer is lock-free: one writer, one reader, each owning one counter. A seek bumps a
// "generation" so stale frames already in the ring are never played.
//
// Metering: besides the audible output (stereo), the audio thread also renders the speaker feeds
// of the layout being viewed and records, per audio buffer, each speaker's and each element's
// energy. The main thread reads those to drive the 3D view, so speaker brightness comes from the
// actual rendered feeds.

using System;
using System.Threading;

namespace AtmosEngine
{
    public sealed class StreamingPlayer : IDisposable
    {
        public DamfFile Damf { get; }
        public GainTimeline Output { get; }
        public double SampleRate { get; }
        public long FrameCount { get; }
        public int InputChannels { get; }

        // statistics, for measurement
        public long Underruns => Volatile.Read(ref underruns);
        public long CallbackCount => Volatile.Read(ref callbacks);
        public double MaxRenderMs => Volatile.Read(ref maxRenderTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        /// <summary>File frame at the start of the most recent Render call (-1 when it output silence).</summary>
        public long LastBlockStartFrame => Volatile.Read(ref lastBlockStart);

        readonly CafReader reader;
        readonly Thread diskThread;
        volatile bool stopping;
        volatile bool playing;

        // ring buffer of element frames
        readonly float[] ring;
        readonly int ringFrames;
        long writeFrame;            // next file frame the disk thread will write (disk thread owns)
        long readFrame;             // next file frame the audio thread will play (audio thread owns)
        long publishedGeneration;   // generation of the frames in the ring (disk thread publishes)

        sealed class SeekToken { public long Generation; public long Frame; }
        SeekToken pendingSeek;      // main -> audio thread
        SeekToken activeSeek;       // audio thread -> disk thread
        long generation;            // audio thread's current generation
        long nextGeneration;        // main thread counter

        // audio-thread scratch
        readonly float[] inBlock;
        readonly float[] outBlock;
        const int MaxBlock = 8192;
        bool primed;                                    // audio thread: buffered enough since the last seek
        int PrebufferFrames => (int)(0.25 * SampleRate);

        long underruns, callbacks, maxRenderTicks, lastBlockStart = -1;

        // ---- metering ----
        public const int MaxMeterChannels = 32;
        const int MeterRing = 512;                      // ~11 s of 1024-frame buffers
        sealed class MeterTarget { public GainTimeline Timeline; public int Id; }
        MeterTarget meterTarget;                        // main -> audio thread
        int nextMeterId;
        readonly float[] meterBlock = new float[MaxBlock * MaxMeterChannels];
        readonly double[] accSpeaker = new double[MaxMeterChannels];
        readonly double[] accElement;
        readonly long[] mbStart = new long[MeterRing];
        readonly int[] mbFrames = new int[MeterRing];
        readonly int[] mbLayout = new int[MeterRing];
        readonly float[] mbSpeaker = new float[MeterRing * MaxMeterChannels];
        readonly float[] mbElement;
        long mbWritten;                                 // entries written so far (audio thread)

        public StreamingPlayer(DamfFile damf, GainTimeline output, double bufferSeconds = 2.0)
        {
            Damf = damf;
            Output = output;
            reader = damf.OpenAudio();
            if (reader.Channels != damf.Elements.Count)
                throw new InvalidOperationException("CAF has " + reader.Channels + " channels, header lists " + damf.Elements.Count + " elements");
            SampleRate = reader.SampleRate;
            FrameCount = reader.FrameCount;
            InputChannels = reader.Channels;

            ringFrames = (int)(bufferSeconds * SampleRate);
            ring = new float[ringFrames * InputChannels];
            inBlock = new float[MaxBlock * InputChannels];
            outBlock = new float[MaxBlock * output.Channels];
            accElement = new double[InputChannels];
            mbElement = new float[MeterRing * InputChannels];

            activeSeek = new SeekToken { Generation = 0, Frame = 0 };
            diskThread = new Thread(DiskLoop) { IsBackground = true, Name = "Atmos disk reader", Priority = ThreadPriority.AboveNormal };
            diskThread.Start();
        }

        /// <summary>Volume of the audible output in dB (meters are unaffected). The stereo downmix
        /// of a full Atmos mix can exceed full scale (RPO peaks at +9.6 dBFS), so the app turns
        /// this down; 0 dB leaves the render untouched for measurement.</summary>
        public double OutputGainDb
        {
            get => outputGainDb;
            set { outputGainDb = value; Volatile.Write(ref outputGainLin, (float)Math.Pow(10.0, value / 20.0)); }
        }
        double outputGainDb;
        float outputGainLin = 1f;

        /// <summary>Safety limiter on the audible output: holds peaks at -0.2 dBFS (instant
        /// attack, 150 ms release). Off by default so measurements see the plain render.</summary>
        public bool SafetyLimiter { get; set; }
        public long LimitedSamples => Volatile.Read(ref limitedSamples);
        float limiterGain = 1f;
        long limitedSamples;

        public bool Playing
        {
            get => playing;
            set => playing = value;
        }

        /// <summary>Playback position: the next frame the audio thread will output.</summary>
        public long PositionFrames => Volatile.Read(ref readFrame);
        public double PositionSeconds => PositionFrames / SampleRate;

        /// <summary>Choose the layout whose speaker feeds are metered (null = none). Takes effect
        /// from the next audio buffer. Returns an id that tags the meter readings.</summary>
        public int SetMeterTimeline(GainTimeline timeline)
        {
            if (timeline != null && timeline.Channels > MaxMeterChannels)
                throw new ArgumentException("layout has more than " + MaxMeterChannels + " speakers");
            int id = Interlocked.Increment(ref nextMeterId);
            Volatile.Write(ref meterTarget, timeline == null ? null : new MeterTarget { Timeline = timeline, Id = id });
            return id;
        }

        /// <summary>Main thread: mean-square level of every metered speaker and every element over
        /// the audio buffers that overlap [fromFrame, toFrame), for meter layout id. Returns the
        /// number of frames found (0 = nothing played there).</summary>
        public int ReadMeters(long fromFrame, long toFrame, int layoutId, double[] speakerMeanSquare, double[] elementMeanSquare)
        {
            Array.Clear(speakerMeanSquare, 0, speakerMeanSquare.Length);
            Array.Clear(elementMeanSquare, 0, elementMeanSquare.Length);
            long written = Volatile.Read(ref mbWritten);
            int frames = 0;
            for (long k = written - 1; k >= 0 && k >= written - 64; k--)
            {
                int slot = (int)(k % MeterRing);
                long s = mbStart[slot];
                int f = mbFrames[slot];
                if (s + f <= fromFrame || s >= toFrame || mbLayout[slot] != layoutId) continue;
                frames += f;
                for (int c = 0; c < speakerMeanSquare.Length && c < MaxMeterChannels; c++)
                    speakerMeanSquare[c] += mbSpeaker[slot * MaxMeterChannels + c];
                for (int e = 0; e < elementMeanSquare.Length && e < InputChannels; e++)
                    elementMeanSquare[e] += mbElement[slot * InputChannels + e];
            }
            if (frames > 0)
            {
                for (int c = 0; c < speakerMeanSquare.Length; c++) speakerMeanSquare[c] /= frames;
                for (int e = 0; e < elementMeanSquare.Length; e++) elementMeanSquare[e] /= frames;
            }
            return frames;
        }

        public void Seek(double seconds)
        {
            long frame = (long)Math.Round(Math.Max(0, Math.Min(seconds * SampleRate, FrameCount)));
            var token = new SeekToken { Generation = Interlocked.Increment(ref nextGeneration), Frame = frame };
            Volatile.Write(ref pendingSeek, token);
        }

        /// <summary>Called on the audio thread. Writes `frames` frames of interleaved output with
        /// outChannels per frame; the first min(outChannels, layout channels) get speaker feeds,
        /// the rest zeros. Returns the number of frames that came from the file.</summary>
        public int Render(float[] data, int frames, int outChannels)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Increment(ref callbacks);

            var seek = Interlocked.Exchange(ref pendingSeek, null);
            if (seek != null)
            {
                generation = seek.Generation;
                primed = false;
                Volatile.Write(ref readFrame, seek.Frame);
                Volatile.Write(ref activeSeek, seek);
            }

            Array.Clear(data, 0, frames * outChannels);
            int produced = 0;
            Volatile.Write(ref lastBlockStart, -1);

            if (playing)
            {
                long start = readFrame;
                bool ready = Volatile.Read(ref publishedGeneration) == generation;
                long available = ready ? Volatile.Read(ref writeFrame) - start : 0;
                long remaining = Math.Max(0, FrameCount - start);

                // After a seek (or at start), wait silently until enough is buffered, so the
                // first audible block isn't followed by a gap. Not counted as an underrun.
                if (!primed)
                    primed = ready && available >= Math.Min(PrebufferFrames, remaining);

                if (primed)
                {
                    int want = (int)Math.Min(frames, remaining);
                    int n = (int)Math.Min(want, Math.Max(0, available));
                    if (n < want) Interlocked.Increment(ref underruns);

                    int ch = InputChannels, oc = Output.Channels, copy = Math.Min(oc, outChannels);
                    var meter = Volatile.Read(ref meterTarget);
                    int mc = meter != null ? meter.Timeline.Channels : 0;
                    Array.Clear(accSpeaker, 0, accSpeaker.Length);
                    Array.Clear(accElement, 0, accElement.Length);
                    for (int done = 0; done < n;)
                    {
                        int m = Math.Min(MaxBlock, n - done);
                        long f0 = start + done;
                        // copy from the ring (may wrap)
                        int pos = (int)(f0 % ringFrames);
                        int first = Math.Min(m, ringFrames - pos);
                        Array.Copy(ring, pos * ch, inBlock, 0, first * ch);
                        if (first < m) Array.Copy(ring, 0, inBlock, first * ch, (m - first) * ch);

                        Mixer.Render(Output, f0, inBlock, ch, m, outBlock);

                        for (int f = 0; f < m; f++)
                            for (int k = 0; k < copy; k++)
                                data[(done + f) * outChannels + k] = outBlock[f * oc + k];

                        if (meter != null)
                        {
                            Mixer.Render(meter.Timeline, f0, inBlock, ch, m, meterBlock);
                            for (int f = 0; f < m; f++)
                                for (int k = 0; k < mc; k++)
                                {
                                    double v = meterBlock[f * mc + k];
                                    accSpeaker[k] += v * v;
                                }
                        }
                        for (int i = 0; i < m * ch; i++)
                        {
                            double v = inBlock[i];
                            accElement[i % ch] += v * v;
                        }
                        done += m;
                    }

                    ApplyOutputGain(data, n, outChannels, copy);

                    if (n > 0)
                    {
                        long w = mbWritten;
                        int slot = (int)(w % MeterRing);
                        mbStart[slot] = start;
                        mbFrames[slot] = n;
                        mbLayout[slot] = meter != null ? meter.Id : 0;
                        for (int k = 0; k < MaxMeterChannels; k++) mbSpeaker[slot * MaxMeterChannels + k] = k < mc ? (float)accSpeaker[k] : 0f;
                        for (int e = 0; e < ch; e++) mbElement[slot * ch + e] = (float)accElement[e];
                        Volatile.Write(ref mbWritten, w + 1);
                    }

                    if (n > 0)
                    {
                        Volatile.Write(ref lastBlockStart, start);
                        Volatile.Write(ref readFrame, start + n);
                        produced = n;
                    }
                    if (start + n >= FrameCount) playing = false;   // end of file
                }
            }

            long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            if (dt > Volatile.Read(ref maxRenderTicks)) Volatile.Write(ref maxRenderTicks, dt);
            return produced;
        }

        void ApplyOutputGain(float[] data, int frames, int outChannels, int used)
        {
            float g = Volatile.Read(ref outputGainLin);
            bool limit = SafetyLimiter;
            if (g == 1f && !limit) return;
            const float ceiling = 0.977f;                                    // -0.2 dBFS
            float release = (float)(1.0 - Math.Exp(-1.0 / (0.15 * SampleRate)));
            for (int f = 0; f < frames; f++)
            {
                int o = f * outChannels;
                float gain = g;
                if (limit)
                {
                    float peak = 0f;
                    for (int k = 0; k < used; k++) peak = Math.Max(peak, Math.Abs(data[o + k]) * g);
                    float target = peak > ceiling ? ceiling / peak : 1f;
                    if (target < limiterGain) limiterGain = target;
                    else limiterGain = Math.Min(target, limiterGain + (1f - limiterGain) * release);
                    if (limiterGain < 0.9999f) limitedSamples++;
                    gain *= limiterGain;
                }
                for (int k = 0; k < used; k++) data[o + k] *= gain;
            }
        }

        void DiskLoop()
        {
            long gen = -1;
            long wf = 0;
            const int chunk = 4096;
            var tmp = new float[chunk * InputChannels];
            while (!stopping)
            {
                var seek = Volatile.Read(ref activeSeek);
                if (seek.Generation != gen)
                {
                    gen = seek.Generation;
                    wf = seek.Frame;
                    Volatile.Write(ref writeFrame, wf);
                    Volatile.Write(ref publishedGeneration, gen);
                }

                long rf = Volatile.Read(ref readFrame);
                long free = ringFrames - (wf - rf);
                int n = (int)Math.Min(Math.Min(chunk, free), FrameCount - wf);
                if (n <= 0 || rf > wf || free > ringFrames)
                {
                    Thread.Sleep(2);   // buffer full, at end of file, or a seek is in flight
                    continue;
                }

                int got = reader.ReadFloat(wf, tmp, n);
                int ch = InputChannels;
                int pos = (int)(wf % ringFrames);
                int first = Math.Min(got, ringFrames - pos);
                Array.Copy(tmp, 0, ring, pos * ch, first * ch);
                if (first < got) Array.Copy(tmp, first * ch, ring, 0, (got - first) * ch);

                // Publish only if no seek arrived while reading; otherwise drop this chunk.
                if (Volatile.Read(ref activeSeek).Generation == gen)
                {
                    wf += got;
                    Volatile.Write(ref writeFrame, wf);
                }
            }
        }

        public void Dispose()
        {
            stopping = true;
            diskThread.Join(1000);
            reader.Dispose();
        }
    }
}
