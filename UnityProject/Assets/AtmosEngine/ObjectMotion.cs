// Where to draw an object at a given moment, from its DAMF events.
//
// Positions move linearly over each event's rampLength, like the audio gains. When an object
// switches off, DAMF also sets its position to [0, 0, 0]; drawing that literally would show the
// dot flying to the middle of the room as it fades. So switching off fades the dot out where it
// was, and switching on fades it in where it appears.

using System;

namespace AtmosEngine
{
    public struct ObjectVisual
    {
        public bool Visible;
        public double X, Y, Z;
        public double Size;
        public double Alpha;        // 0..1, for fading in and out
        public double LinearGain;   // metadata gain (not the audio level)
    }

    public static class ObjectMotion
    {
        public static ObjectVisual At(DamfElement e, long sample)
        {
            var v = new ObjectVisual();
            if (e.IsBed || e.Events.Count == 0) return v;
            int i = Math.Max(0, DamfFile.EventIndexAt(e, sample));
            var ev = e.Events[i];
            bool on = ev.Active && ev.LinearGain > 0;

            bool inRamp = false;
            double p = 1;
            if (i > 0 && sample >= ev.SamplePos)
            {
                long ramp = ev.RampLength;
                if (i + 1 < e.Events.Count) ramp = Math.Min(ramp, e.Events[i + 1].SamplePos - ev.SamplePos);
                if (ramp > 0 && sample < ev.SamplePos + ramp)
                {
                    inRamp = true;
                    p = (sample - ev.SamplePos) / (double)ramp;
                }
            }
            var prev = i > 0 ? e.Events[i - 1] : ev;
            bool prevOn = i > 0 && prev.Active && prev.LinearGain > 0;

            if (on && prevOn && inRamp)
            {
                Set(ref v, Lerp(prev.X, ev.X, p), Lerp(prev.Y, ev.Y, p), Lerp(prev.Z, ev.Z, p),
                    Lerp(prev.Size, ev.Size, p), 1, Lerp(prev.LinearGain, ev.LinearGain, p));
            }
            else if (on)
            {
                Set(ref v, ev.X, ev.Y, ev.Z, ev.Size, inRamp && !prevOn ? p : 1, ev.LinearGain);
            }
            else if (prevOn && inRamp)
            {
                Set(ref v, prev.X, prev.Y, prev.Z, prev.Size, 1 - p, prev.LinearGain * (1 - p));
            }
            return v;
        }

        static void Set(ref ObjectVisual v, double x, double y, double z, double size, double alpha, double gain)
        {
            v.Visible = alpha > 0;
            v.X = x; v.Y = y; v.Z = z;
            v.Size = size;
            v.Alpha = alpha;
            v.LinearGain = gain;
        }

        static double Lerp(double a, double b, double t) => a + (b - a) * t;
    }
}
