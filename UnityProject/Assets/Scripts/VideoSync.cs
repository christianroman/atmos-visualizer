// Slaves a VideoPlayer to the Atmos audio clock. The audio is the master.
//
// Unity's own drift correction (VideoTimeReference.ExternalTime) was measured to let the video
// wander by -60..+150 ms and correct in jumps, so this does it actively instead, like a PLL:
// every frame it compares the video's time with where the audio is, and nudges the video's
// playback speed (by at most +-3%) to close the gap. A gap over 250 ms (e.g. after a seek) is
// fixed with a hard seek instead.

using System;
using UnityEngine;
using UnityEngine.Video;

[RequireComponent(typeof(VideoPlayer))]
public sealed class VideoSync : MonoBehaviour
{
    public AtmosAudioSource Audio;

    /// <summary>How far the audible sound lags the audio clock: Unity's mixer adds one DSP
    /// buffer (measured), then the output queue holds a few more (inferred from the DSP buffer
    /// settings). Set in Awake; adjust by eye if lips look early or late.</summary>
    public double OutputLatencySeconds = -1;

    /// <summary>Extra offset added to the video target, for calibration.</summary>
    public double OffsetSeconds;

    public double LastDriftSeconds { get; private set; }
    public int HardResyncs { get; private set; }

    const double MaxSpeedChange = 0.03, MaxCatchUpChange = 0.10, Gain = 0.5, HardResyncThreshold = 0.25;

    /// <summary>A video seek takes ~0.2 s to land (measured 0.18-0.28 s) while the audio keeps
    /// going, so seeks made during playback aim this far ahead.</summary>
    public double SeekLeadSeconds = 0.2;
    VideoPlayer video;
    double smoothedDrift;
    float lastResync = -10;

    void Awake()
    {
        video = GetComponent<VideoPlayer>();
        video.timeReference = VideoTimeReference.Freerun;   // we correct drift ourselves
        video.timeUpdateMode = VideoTimeUpdateMode.DSPTime;  // advance with the audio hardware clock
        if (OutputLatencySeconds < 0)
        {
            AudioSettings.GetDSPBufferSize(out int length, out int count);
            OutputLatencySeconds = (1 + count) * (double)length / AudioSettings.outputSampleRate;
        }
    }

    /// <summary>Where the video should be right now, in seconds.</summary>
    public double TargetTime => Audio.ClockSeconds - OutputLatencySeconds + OffsetSeconds;

    /// <summary>Jump the video straight to the audio position.</summary>
    public void Resync() => SeekTo(Audio.ClockSeconds);

    /// <summary>Seek the video to match an audio seek to audioSeconds. The audio clock only
    /// reflects the seek once the audio thread has picked it up, so steering pauses briefly.</summary>
    public void SeekTo(double audioSeconds)
    {
        double lead = Audio != null && Audio.Player != null && Audio.Player.Playing ? SeekLeadSeconds : 0;
        video.time = Math.Max(0, audioSeconds - OutputLatencySeconds + OffsetSeconds + lead);
        smoothedDrift = 0;
        lastResync = Time.unscaledTime;
    }

    void Update()
    {
        if (Audio == null || Audio.Player == null || !Audio.Player.Playing || !video.isPlaying) return;
        if (Time.unscaledTime - lastResync < 0.5f) return;   // let a seek settle first

        double drift = video.time - TargetTime;            // positive: video is ahead
        LastDriftSeconds = drift;
        if (Math.Abs(drift) > HardResyncThreshold)
        {
            if (Time.unscaledTime - lastResync > 1f)
            {
                Resync();
                HardResyncs++;
            }
            return;
        }
        // video.time moves in whole frames (42 ms at 23.976 fps), so smooth before steering
        smoothedDrift += 0.1 * (drift - smoothedDrift);
        double speed = 1.0 - Gain * smoothedDrift;
        double limit = Math.Abs(smoothedDrift) > 0.06 ? MaxCatchUpChange : MaxSpeedChange;   // catch up faster when far off
        speed = Math.Max(1 - limit, Math.Min(1 + limit, speed));
        if (video.canSetPlaybackSpeed) video.playbackSpeed = (float)speed;
    }
}
