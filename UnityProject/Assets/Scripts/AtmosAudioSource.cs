// Plays a DAMF file through Unity's audio output, rendered to stereo by libear.
//
// Unity calls OnAudioFilterRead on its audio thread (not the main thread) every time it needs
// another buffer of samples - like an AVAudioSourceNode render block on iOS. We fill that buffer
// from the StreamingPlayer. No Unity API calls and no allocations are allowed in there.

using System;
using AtmosEngine;
using UnityEngine;

[RequireComponent(typeof(AudioSource))]
public sealed class AtmosAudioSource : MonoBehaviour
{
    public StreamingPlayer Player { get; private set; }
    public DamfFile Damf { get; private set; }
    public double GainSetupSeconds { get; private set; }

    volatile bool ready;
    long lastPos = -1;
    double lastPosTime;

    /// <summary>Loads a DAMF file and starts streaming it (paused). Audible output is rendered
    /// to outputPreset - the 2.0 stereo preset.</summary>
    public void Open(string atmosPath, LayoutPreset outputPreset) => Open(DamfFile.Load(atmosPath), outputPreset);

    public void Open(DamfFile damf, LayoutPreset outputPreset)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var timeline = new GainTimeline(damf, outputPreset);
        GainSetupSeconds = sw.Elapsed.TotalSeconds;
        Open(damf, timeline);
    }

    /// <summary>Starts streaming with a stereo timeline already built (e.g. on a worker thread).</summary>
    public void Open(DamfFile damf, GainTimeline output)
    {
        Close();
        Damf = damf;
        Player = new StreamingPlayer(Damf, output);
        lastPos = -1;

        var src = GetComponent<AudioSource>();
        src.clip = null;
        src.spatialBlend = 0f;      // 2D: Unity mustn't pan or attenuate it
        src.volume = 1f;
        src.bypassEffects = true;
        src.bypassReverbZones = true;
        src.Play();
        ready = true;
    }

    public bool PositionAtEnd => Player != null && Player.PositionFrames >= Player.FrameCount;

    public void Play() => Player.Playing = true;
    public void Pause() => Player.Playing = false;
    public void Seek(double seconds)
    {
        Player.Seek(seconds);
        // The audio thread applies the seek on its next buffer; until then the clock reports
        // the target, so the video and the time display don't briefly see the old position.
        seekTarget = Math.Max(0, Math.Min(seconds, Player.FrameCount / Player.SampleRate));
        seekRequested = Time.realtimeSinceStartupAsDouble;
    }

    double seekTarget = -1, seekRequested;

    /// <summary>Audio clock in seconds: the file position Unity has most recently been given,
    /// advanced by wall-clock time since then (Unity asks for audio in ~21 ms steps).
    /// Main thread only.</summary>
    public double ClockSeconds
    {
        get
        {
            if (Player == null) return 0;
            long pos = Player.PositionFrames;
            double now = Time.realtimeSinceStartupAsDouble;
            if (seekTarget >= 0)
            {
                bool applied = Math.Abs(pos / Player.SampleRate - seekTarget) < 0.25;
                if (!applied && now - seekRequested < 1.0) return seekTarget;
                seekTarget = -1;
            }
            if (pos != lastPos)
            {
                lastPos = pos;
                lastPosTime = now;
            }
            double ahead = Player.Playing ? Math.Min(now - lastPosTime, 0.05) : 0;
            return pos / Player.SampleRate + ahead;
        }
    }

    void OnAudioFilterRead(float[] data, int channels)
    {
        var p = Player;
        if (!ready || p == null) return;
        p.Render(data, data.Length / channels, channels);
    }

    public void Close()
    {
        ready = false;
        var p = Player;
        Player = null;
        p?.Dispose();
    }

    void OnDestroy() => Close();
}
