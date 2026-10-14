// Gains over time for every element of a DAMF file, for one speaker layout preset.
//
// Objects (Cartesian, room-relative) are panned with BS.2127's allocentric panner, ported from
// the EBU reference renderer into ear_shim (libear lacks it). Bed channels such as the LFE use
// libear's DirectSpeakers. LibearPolar is kept only to compare against libear's angle route.
//
// How gains change over time mirrors the BS.2127 reference renderer (ear 2.1.0,
// core/objectbased/renderer.py): each metadata event is a block; its target gains are computed
// once; over the event's rampLength the GAINS (not the position) move linearly from the
// previous event's gains to the new ones, then hold until the next event.
//
// Two deliberate differences, both affecting edge cases only:
//  - Before an element's first event the reference outputs nothing; here the first event's
//    gains apply from sample 0, so the first ~23 ms of a file isn't dropped.
//  - A ramp longer than the gap to the next event is shortened to fit (the reference throws).

using System;
using System.Collections.Generic;

namespace AtmosEngine
{
    public enum ObjectPanning
    {
        /// <summary>BS.2127 for cartesian=true metadata (room-relative). The default.</summary>
        Allocentric,
        /// <summary>BS.2127 section 10 conversion to angles, then libear's polar panner.</summary>
        LibearPolar,
    }

    public sealed class ElementGains
    {
        public long[] Start;     // sample position of each event
        public long[] RampEnd;   // where each event's ramp finishes (== Start when there is no ramp)
        public int[] Offset;     // per event: offset of its target gains in Gains (shared when identical)
        public float[] Gains;    // unique gain vectors, Channels values each
        public bool[] Silent;    // per event: target gains all zero

        public int EventCount => Start.Length;

        /// <summary>Index of the event in force at sample n (the first one before any event).</summary>
        public int EventAt(long n)
        {
            int lo = 0, hi = Start.Length - 1, found = 0;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (Start[mid] <= n) { found = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return found;
        }
    }

    public sealed class GainTimeline
    {
        public LayoutPreset Preset { get; }
        public ObjectPanning Panning { get; }
        public int Channels { get; }
        public ElementGains[] Elements { get; }

        /// <summary>DAMF bed channel names to BS.2051 speaker labels, nominal angles, and where
        /// that speaker sits in the room box (BS.2127's allocentric tables, as in the layout files).</summary>
        static readonly Dictionary<string, (string label, double az, double el, double x, double y, double z)> BedMap =
            new Dictionary<string, (string, double, double, double, double, double)>
            {
                { "L", ("M+030", 30, 0, -1, 1, 0) }, { "R", ("M-030", -30, 0, 1, 1, 0) }, { "C", ("M+000", 0, 0, 0, 1, 0) },
                { "LFE", ("LFE1", 45, -30, 0, 1, -1) },   // drawn front centre, like the layouts' sub
                { "Ls", ("M+110", 110, 0, -1, -1, 0) }, { "Rs", ("M-110", -110, 0, 1, -1, 0) },
                { "Lss", ("M+090", 90, 0, -1, 0, 0) }, { "Rss", ("M-090", -90, 0, 1, 0, 0) },
                { "Lrs", ("M+135", 135, 0, -1, -1, 0) }, { "Rrs", ("M-135", -135, 0, 1, -1, 0) },
                { "Lts", ("U+090", 90, 30, -1, 0, 1) }, { "Rts", ("U-090", -90, 30, 1, 0, 1) },
                // DTS:X 7.1.4 (as harletty names it): heights high on the front and back walls,
                // the same speakers as the 11.1.8 layout's Lfh/Rfh and Lrh/Rrh
                { "Lfh", ("U+030", 30, 30, -1, 1, 1) }, { "Rfh", ("U-030", -30, 30, 1, 1, 1) },
                { "Lrh", ("U+150", 150, 30, -1, -1, 1) }, { "Rrh", ("U-150", -150, 30, 1, -1, 1) },
            };

        /// <summary>DTS:X heights, by preference: the layout's height speakers on the same side,
        /// front to back for the front pair and back to front for the rear pair.</summary>
        static readonly Dictionary<string, string[]> HeightLabels = new Dictionary<string, string[]>
        {
            { "Lfh", new[] { "U+030", "U+045", "U+090" } }, { "Rfh", new[] { "U-030", "U-045", "U-090" } },
            { "Lrh", new[] { "U+150", "U+135", "U+110", "U+090" } }, { "Rrh", new[] { "U-150", "U-135", "U-110", "U-090" } },
        };

        /// <summary>
        /// The speaker a DTS:X height channel plays on: the nearest height speaker on its side
        /// that the layout has, as a DTS:X receiver plays its heights on whichever height
        /// speakers it is set up with. Null when there is none (or for any other channel); the
        /// channel is then placed by its direction, BS.2127's rule. This is a routing choice on
        /// top of BS.2127: by direction alone, the front heights (30 degrees up) would land
        /// mostly in L and R on 5.1.2, and spill into the centre on 7.1.4.
        /// </summary>
        static (string label, double az, double el)? HeightSpeaker(string channel, LayoutPreset preset)
        {
            if (!HeightLabels.TryGetValue(channel, out var labels)) return null;
            foreach (var label in labels)
                foreach (var s in preset.Speakers)
                    if (s.Label == label) return (s.Label, s.Azimuth, s.Elevation);
            return null;
        }

        /// <summary>Where a bed channel is drawn: its speaker's place in the room box. Bed
        /// channels never move.</summary>
        public static bool TryGetBedPosition(string channel, out double x, out double y, out double z)
        {
            x = y = z = 0;
            if (channel == null || !BedMap.TryGetValue(channel, out var b)) return false;
            x = b.x; y = b.y; z = b.z;
            return true;
        }

        public GainTimeline(DamfFile damf, LayoutPreset preset, ObjectPanning panning = ObjectPanning.Allocentric)
        {
            Preset = preset;
            Panning = panning;
            Channels = preset.Speakers.Count;
            Elements = new ElementGains[damf.Elements.Count];

            var g = new double[Channels];
            var pans = new Dictionary<(double, double, double, double), double[]>();
            using (var layout = preset.CreateSpeakerLayout())
            using (var allo = panning == ObjectPanning.Allocentric ? new AlloGainCalculator(layout, preset.AllocentricPositions()) : null)
            using (var polar = panning == ObjectPanning.LibearPolar ? new ObjectGainCalculator(layout) : null)
            using (var direct = new DirectGainCalculator(layout))
            {
                foreach (var e in damf.Elements)
                {
                    int n = e.Events.Count;
                    var eg = new ElementGains { Start = new long[n], RampEnd = new long[n], Offset = new int[n], Silent = new bool[n] };
                    var unique = new List<float>();
                    var seen = new Dictionary<(double, double, double, double, double), int>();
                    for (int i = 0; i < n; i++)
                    {
                        var ev = e.Events[i];
                        eg.Start[i] = ev.SamplePos;
                        long ramp = i == 0 ? 0 : ev.RampLength;
                        if (i + 1 < n) ramp = Math.Min(ramp, e.Events[i + 1].SamplePos - ev.SamplePos);
                        eg.RampEnd[i] = ev.SamplePos + Math.Max(0, ramp);

                        double lin = ev.LinearGain;
                        var key = lin == 0 ? (0.0, 0.0, 0.0, 0.0, 0.0) : (ev.X, ev.Y, ev.Z, ev.Size, lin);
                        if (seen.TryGetValue(key, out int offset))
                        {
                            eg.Offset[i] = offset;
                        }
                        else
                        {
                            if (lin == 0)
                            {
                                Array.Clear(g, 0, g.Length);
                            }
                            else if (e.IsBed)
                            {
                                if (!BedMap.TryGetValue(e.BedChannel, out var b))
                                    throw new NotSupportedException("bed channel '" + e.BedChannel + "' isn't mapped yet");
                                var (label, az, el) = HeightSpeaker(e.BedChannel, preset) ?? (b.label, b.az, b.el);
                                direct.Gains(label, az, el, label.StartsWith("LFE") ? 120.0 : 0.0, g);
                                for (int k = 0; k < Channels; k++) g[k] *= lin;
                            }
                            else
                            {
                                // both panners scale their result by the gain, so pan once at unit gain
                                var pk = (ev.X, ev.Y, ev.Z, ev.Size);
                                if (!pans.TryGetValue(pk, out var pan))
                                {
                                    pan = new double[Channels];
                                    if (allo != null) allo.Gains(ev.X, ev.Y, ev.Z, ev.Size, 1.0, pan);
                                    else polar.CartesianViaPolar(ev.X, ev.Y, ev.Z, ev.Size, 1.0, pan);
                                    pans[pk] = pan;
                                }
                                for (int k = 0; k < Channels; k++) g[k] = pan[k] * lin;
                            }
                            offset = unique.Count;
                            for (int k = 0; k < Channels; k++) unique.Add((float)g[k]);
                            seen[key] = offset;
                            eg.Offset[i] = offset;
                        }
                        bool silent = true;
                        for (int k = 0; k < Channels; k++) if (unique[eg.Offset[i] + k] != 0) { silent = false; break; }
                        eg.Silent[i] = silent;
                    }
                    eg.Gains = unique.ToArray();
                    Elements[e.Index] = eg;
                }
            }
        }

        /// <summary>Gains of one element at sample n, written to dest[0..Channels).</summary>
        public void GainsAt(int element, long n, float[] dest)
        {
            var eg = Elements[element];
            if (eg.EventCount == 0) { Array.Clear(dest, 0, Channels); return; }
            int i = eg.EventAt(n);
            int o = eg.Offset[i];
            if (i > 0 && n >= eg.Start[i] && n < eg.RampEnd[i])
            {
                float p = (float)((double)(n - eg.Start[i]) / (eg.RampEnd[i] - eg.Start[i]));
                int po = eg.Offset[i - 1];
                for (int k = 0; k < Channels; k++)
                    dest[k] = (1 - p) * eg.Gains[po + k] + p * eg.Gains[o + k];
            }
            else
            {
                Array.Copy(eg.Gains, o, dest, 0, Channels);
            }
        }
    }
}
