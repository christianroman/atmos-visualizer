// Applies a GainTimeline to a block of element audio, producing speaker feeds.
//
// output[frame][speaker] = sum over elements of input[frame][element] * gain(element, speaker, frame)
//
// Allocation-free, so it is safe to call from the audio thread.

using System;

namespace AtmosEngine
{
    public static class Mixer
    {
        /// <param name="timeline">gains for the output layout</param>
        /// <param name="startFrame">file position (in frames) of input[0]</param>
        /// <param name="input">interleaved element audio, inChannels per frame</param>
        /// <param name="output">interleaved speaker feeds, timeline.Channels per frame (overwritten)</param>
        public static void Render(GainTimeline timeline, long startFrame, float[] input, int inChannels, int frames, float[] output)
        {
            int outCh = timeline.Channels;
            Array.Clear(output, 0, frames * outCh);
            long end = startFrame + frames;
            int elements = Math.Min(timeline.Elements.Length, inChannels);

            for (int e = 0; e < elements; e++)
            {
                var eg = timeline.Elements[e];
                if (eg.EventCount == 0) continue;
                float[] gains = eg.Gains;
                long n = startFrame;
                int i = eg.EventAt(n);

                while (n < end)
                {
                    long segEnd = i + 1 < eg.EventCount ? Math.Min(end, eg.Start[i + 1]) : end;
                    int o = eg.Offset[i];

                    // Part of the segment inside this event's ramp: blend previous and new gains.
                    if (i > 0 && n >= eg.Start[i] && n < eg.RampEnd[i])
                    {
                        long rampStop = Math.Min(segEnd, eg.RampEnd[i]);
                        double rampLen = eg.RampEnd[i] - eg.Start[i];
                        int po = eg.Offset[i - 1];
                        for (long m = n; m < rampStop; m++)
                        {
                            int f = (int)(m - startFrame);
                            float x = input[f * inChannels + e];
                            if (x == 0f) continue;
                            float p = (float)((m - eg.Start[i]) / rampLen);
                            float a = x * (1f - p), b = x * p;
                            int ob = f * outCh;
                            for (int k = 0; k < outCh; k++)
                                output[ob + k] += a * gains[po + k] + b * gains[o + k];
                        }
                        n = rampStop;
                    }

                    // Rest of the segment: constant gains.
                    if (n < segEnd)
                    {
                        if (!eg.Silent[i])
                        {
                            for (long m = n; m < segEnd; m++)
                            {
                                int f = (int)(m - startFrame);
                                float x = input[f * inChannels + e];
                                if (x == 0f) continue;
                                int ob = f * outCh;
                                for (int k = 0; k < outCh; k++)
                                    output[ob + k] += x * gains[o + k];
                            }
                        }
                        n = segEnd;
                    }

                    if (i + 1 < eg.EventCount && n >= eg.Start[i + 1]) i++;
                }
            }
        }
    }
}
