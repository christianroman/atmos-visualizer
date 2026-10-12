// Phase 0 runtime test, run inside the Unity player:
//  - P/Invoke into ear_shim.dll from Unity's Mono runtime
//  - stream RPO from disk, render to stereo with libear gains, play it, and record what Unity
//    actually outputs (checked offline against an independent render)
//  - play the proxy video slaved to the audio clock and log the drift between them
//  - seek and pause, and time how long audio and video take to resume
//
// Command line (all optional): -atmos <NAME.atmos> -video <proxy.mp4> -out <results dir>
// Results: <out>/unity_phase0.txt, unity_capture.wav, unity_drift.csv. Quits when finished.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AtmosEngine;
using UnityEngine;
using UnityEngine.Video;

public sealed class Phase0Harness : MonoBehaviour
{
    public AtmosAudioSource Audio;
    public ListenerCapture Capture;
    public VideoPlayer Video;
    public VideoSync Sync;

    // empty: taken from ATMOS_TEST_RPO_ATMOS (the .atmos), ATMOS_TEST_RPO_PROXY (a video proxy of
    // its film) and ATMOS_TEST_OUT_DIR (where results go) when the harness starts
    public string AtmosPath, VideoPath, OutDir;

    const double CaptureStart = 20.0, CaptureSeconds = 20.0, SteadySeconds = 60.0;

    readonly StringBuilder log = new StringBuilder();
    readonly StringBuilder drift = new StringBuilder("phase,realtime,audio_clock,video_target,video_time,video_frame,drift_s,speed\n");
    string status = "starting";
    bool finished;

    void Log(string line)
    {
        log.AppendLine(line);
        Debug.Log("[Phase0] " + line);
    }

    static string F(double v, string fmt = "F4") => v.ToString(fmt, CultureInfo.InvariantCulture);

    void ParseArgs()
    {
        var a = Environment.GetCommandLineArgs();
        for (int i = 0; i < a.Length - 1; i++)
        {
            if (a[i] == "-atmos") AtmosPath = a[i + 1];
            if (a[i] == "-video") VideoPath = a[i + 1];
            if (a[i] == "-out") OutDir = a[i + 1];
        }
        if (string.IsNullOrEmpty(AtmosPath)) AtmosPath = Environment.GetEnvironmentVariable("ATMOS_TEST_RPO_ATMOS") ?? "";
        if (string.IsNullOrEmpty(VideoPath)) VideoPath = Environment.GetEnvironmentVariable("ATMOS_TEST_RPO_PROXY") ?? "";
        if (string.IsNullOrEmpty(OutDir)) OutDir = Environment.GetEnvironmentVariable("ATMOS_TEST_OUT_DIR") ?? "Phase0Results";
    }

    static int ChannelsFor(AudioSpeakerMode mode)
    {
        switch (mode)
        {
            case AudioSpeakerMode.Mono: return 1;
            case AudioSpeakerMode.Quad: return 4;
            case AudioSpeakerMode.Surround: return 5;
            case AudioSpeakerMode.Mode5point1: return 6;
            case AudioSpeakerMode.Mode7point1: return 8;
            default: return 2;
        }
    }

    IEnumerator Start()
    {
        ParseArgs();
        Directory.CreateDirectory(OutDir);

        // ---- environment -------------------------------------------------------------------
        Log("unity: " + Application.unityVersion + ", platform " + Application.platform + ", scripting "
            + (Type.GetType("Mono.Runtime") != null ? "Mono" : "other"));
        var cfg = AudioSettings.GetConfiguration();
        AudioSettings.GetDSPBufferSize(out int bufLen, out int numBufs);
        Log("audio: outputSampleRate " + AudioSettings.outputSampleRate + " Hz, speakerMode " + cfg.speakerMode
            + ", driverCaps " + AudioSettings.driverCapabilities + ", dspBuffer " + bufLen + " x " + numBufs
            + "; video sync assumes output latency " + F(Sync.OutputLatencySeconds * 1000, "F1") + " ms (inferred)");

        // ---- P/Invoke from Mono ------------------------------------------------------------
        Log("ear_shim via P/Invoke: " + EarShim.Version);
        using (var l = SpeakerLayout.Bs2051("4+7+0"))
        using (var calc = new ObjectGainCalculator(l))
        {
            var g = new double[l.ChannelCount];
            foreach (var (name, x, y, z) in new[] { ("centre [0,1,0]", 0.0, 1.0, 0.0), ("front left [-1,1,0]", -1.0, 1.0, 0.0), ("front right [1,1,0]", 1.0, 1.0, 0.0) })
            {
                calc.CartesianViaPolar(x, y, z, 0, 1, g);
                var parts = new List<string>();
                for (int k = 0; k < g.Length; k++) if (g[k] > 1e-6) parts.Add(l.ChannelNames[k] + " " + F(g[k]));
                Log("  4+7+0 " + name + " -> " + string.Join(", ", parts));
            }
        }

        // ---- open audio and video --------------------------------------------------------------
        status = "opening";
        Audio.Open(AtmosPath, LayoutPreset.Load(Path.Combine(Application.streamingAssetsPath, "Layouts", "2.0.yaml")));
        Log("damf: " + AtmosPath + ": " + Audio.Damf.Elements.Count + " elements; gain timeline built in "
            + F(Audio.GainSetupSeconds, "F3") + " s; " + Audio.Player.FrameCount + " frames");

        Video.url = VideoPath;
        Video.Prepare();
        float prepDeadline = Time.realtimeSinceStartup + 30f;
        while (!Video.isPrepared && Time.realtimeSinceStartup < prepDeadline) yield return null;
        Log("video: " + VideoPath + " prepared=" + Video.isPrepared + ", " + Video.width + "x" + Video.height
            + ", " + F(Video.frameRate, "F3") + " fps, " + Video.frameCount + " frames, canSetPlaybackSpeed " + Video.canSetPlaybackSpeed);

        // ---- play from CaptureStart, recording the output, then keep playing for steady-state drift
        Capture.StartCapture(CaptureSeconds, AudioSettings.outputSampleRate, ChannelsFor(cfg.speakerMode));
        yield return SeekBoth(CaptureStart, "start at 20 s");
        status = "playing + capturing";
        double t0 = Time.realtimeSinceStartupAsDouble;
        while (Time.realtimeSinceStartupAsDouble - t0 < SteadySeconds)
        {
            Sample("steady");
            yield return null;
        }
        Log("capture: " + Capture.FramesWritten + " frames x " + Capture.Channels + " ch, first file frame "
            + Capture.FirstFileFrame + " (" + F(Capture.FirstFileFrame / 48000.0) + " s), callback size "
            + Capture.CallbackFrames + " frames, discontinuities " + Capture.Discontinuities);
        Capture.SaveWav(Path.Combine(OutDir, "unity_capture.wav"), AudioSettings.outputSampleRate);

        // ---- seeks: forward, then back over the 29.6 s ceiling event ------------------------------
        yield return SeekBoth(200.0, "seek to 200 s");
        yield return PlayFor(8, "after seek 200");
        yield return SeekBoth(28.0, "seek to 28 s");
        yield return PlayFor(8, "after seek 28");

        // ---- pause / resume --------------------------------------------------------------------
        Audio.Pause();
        Video.Pause();
        double pausedAt = Audio.ClockSeconds;
        yield return new WaitForSecondsRealtime(2f);
        Log("pause: audio clock moved " + F((Audio.ClockSeconds - pausedAt) * 1000, "F1") + " ms while paused for 2 s");
        Audio.Play();
        Video.Play();
        yield return PlayFor(6, "after resume");

        Audio.Pause();
        Video.Pause();
        Log("player: callbacks " + Audio.Player.CallbackCount + ", underruns " + Audio.Player.Underruns
            + ", max render time per callback " + F(Audio.Player.MaxRenderMs, "F3") + " ms; video hard resyncs " + Sync.HardResyncs);
        Finish();
    }

    IEnumerator PlayFor(double seconds, string phase)
    {
        status = phase;
        double t0 = Time.realtimeSinceStartupAsDouble;
        while (Time.realtimeSinceStartupAsDouble - t0 < seconds)
        {
            Sample(phase);
            yield return null;
        }
    }

    IEnumerator SeekBoth(double seconds, string label)
    {
        status = label;
        double t0 = Time.realtimeSinceStartupAsDouble;
        long target = (long)Math.Round(seconds * 48000);
        Audio.Seek(seconds);
        Audio.Play();
        Sync.SeekTo(seconds);
        Video.Play();
        double audioBack = -1, videoBack = -1;
        while (Time.realtimeSinceStartupAsDouble - t0 < 10)
        {
            double now = Time.realtimeSinceStartupAsDouble - t0;
            if (audioBack < 0 && Audio.Player.PositionFrames > target) audioBack = now;
            if (videoBack < 0 && Math.Abs(Video.time - seconds) < 0.5 && Video.frame >= 0) videoBack = now;
            Sample(label);
            if (audioBack >= 0 && videoBack >= 0) break;
            yield return null;
        }
        Log(label + ": audio playing again after " + F(audioBack, "F3") + " s, video at the new position after "
            + F(videoBack, "F3") + " s (frame " + Video.frame + ", " + F(Video.time, "F3") + " s)");
    }

    void Sample(string phase)
    {
        double a = Audio.ClockSeconds;
        double target = Sync.TargetTime;
        double v = Video.time;
        drift.Append(phase).Append(',')
             .Append(F(Time.realtimeSinceStartupAsDouble)).Append(',')
             .Append(F(a)).Append(',').Append(F(target)).Append(',').Append(F(v)).Append(',')
             .Append(Video.frame).Append(',').Append(F(v - target)).Append(',')
             .Append(F(Video.playbackSpeed, "F4")).Append('\n');
    }

    void Finish()
    {
        if (finished) return;
        finished = true;
        status = "done";
        File.WriteAllText(Path.Combine(OutDir, "unity_phase0.txt"), log.ToString());
        File.WriteAllText(Path.Combine(OutDir, "unity_drift.csv"), drift.ToString());
        Debug.Log("[Phase0] results written to " + OutDir);
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    void OnGUI()
    {
        double a = Audio != null && Audio.Player != null ? Audio.ClockSeconds : 0;
        var ts = TimeSpan.FromSeconds(Math.Max(0, a));
        GUI.Label(new Rect(10, 10, 900, 25), "Phase 0 - " + status);
        GUI.Label(new Rect(10, 30, 900, 25), string.Format(CultureInfo.InvariantCulture,
            "audio {0:mm\\:ss\\.fff}   video {1:F3} s   video - target {2:+0.000;-0.000} s",
            ts, Video != null ? Video.time : 0, Sync != null ? Sync.LastDriftSeconds : 0));
    }
}
