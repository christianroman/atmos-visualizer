// Scripted acceptance checks, run inside the real viewer.
//
// -phase1test <dir>:
//  1. sync: the RPO ceiling objects should appear at 29.558 s, with the picture at the same time
//  2. live layout switching while playing
//  3. which speakers light, per layout, while the ceiling objects play (screenshots + levels)
//  4. unused object slots stay dark
//  5. the RPO_s030 size edit and the ceiling8_decorr file on 7.1.4 and 9.1.6
// -movietest <dir> <files...>: open files through the app's own Open... path and record what
//  happens, including live video decoding, picture/sound sync and where each frame's time goes.
// -uitest <dir>: click the on-screen controls the way a user does (Rotate, Zoom, the views,
//  Level colors, Volume) and record what each did, with screenshots.
// The test files come from environment variables (read-only; the app only reads them):
//   ATMOS_TEST_RPO_DIR    folder with RPO.atmos and its edited variants (RPO_s030.atmos, ...)
//   ATMOS_TEST_RPO_MOVIE  the Ready Player One MKV those were decoded from
//   ATMOS_TEST_MEDIA_DIR  folder with the ceil8d_e16 re-encode used by the Phase 1 checks

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AtmosEngine;
using UnityEngine;
using UnityEngine.UIElements;

public sealed class Phase1Test : MonoBehaviour
{
    public string RpoDir, MediaDir, RpoMovie;   // empty: taken from the environment variables above

    void Awake()
    {
        if (string.IsNullOrEmpty(RpoDir)) RpoDir = Environment.GetEnvironmentVariable("ATMOS_TEST_RPO_DIR") ?? "";
        if (string.IsNullOrEmpty(RpoMovie)) RpoMovie = Environment.GetEnvironmentVariable("ATMOS_TEST_RPO_MOVIE") ?? "";
        if (string.IsNullOrEmpty(MediaDir)) MediaDir = Environment.GetEnvironmentVariable("ATMOS_TEST_MEDIA_DIR") ?? "";
    }

    // the scripted tests need the RPO test files; without them they say so and stop
    bool HaveTestFiles()
    {
        if (File.Exists(Path.Combine(RpoDir, "RPO.atmos")) && File.Exists(RpoMovie)) return true;
        Log("test files not found: set ATMOS_TEST_RPO_DIR (folder with RPO.atmos) and ATMOS_TEST_RPO_MOVIE (the RPO MKV)");
        return false;
    }

    AtmosApp app;
    string outDir;
    readonly StringBuilder log = new StringBuilder();
    readonly HashSet<int> everShownIds = new HashSet<int>();
    bool trackSlots = true;   // only while the original RPO is open

    void Log(string s)
    {
        log.AppendLine(s);
        Debug.Log("[Phase1] " + s);
    }

    static string F(double v, string fmt = "F3") => v.ToString(fmt, CultureInfo.InvariantCulture);

    void LogEnvironment()
    {
        var doc = FindFirstObjectByType<UnityEngine.UIElements.UIDocument>();
        Log("unity " + Application.unityVersion + ", window " + Screen.width + "x" + Screen.height + ", Windows scaling "
            + F(WindowsDisplay.Scale * 100, "F0") + "% (from " + WindowsDisplay.Source + "; Unity Screen.dpi " + Screen.dpi + "), UI scale "
            + (doc != null && doc.panelSettings != null ? doc.panelSettings.scaleMode + " x" + F(doc.panelSettings.scale, "F2") : "?"));
        Log("display " + Screen.currentResolution.width + "x" + Screen.currentResolution.height + " at " + F(Screen.currentResolution.refreshRateRatio.value, "F1")
            + " Hz, vsync " + QualitySettings.vSyncCount + ", target frame rate " + Application.targetFrameRate + ", graphics "
            + SystemInfo.graphicsDeviceType + " on " + SystemInfo.graphicsDeviceName + ", Remote Desktop " + (WindowsDisplay.RemoteSession ? "yes" : "no")
            + ", frame timing stats " + (FrameTimingManager.IsFeatureEnabled() ? "on" : "off"));
    }

    public IEnumerator Run(AtmosApp app, string outDir)
    {
        this.app = app;
        this.outDir = outDir;
        Directory.CreateDirectory(outDir);
        if (!HaveTestFiles()) { File.WriteAllText(Path.Combine(outDir, "phase1_results.txt"), log.ToString()); Application.Quit(); yield break; }
        yield return new WaitForSecondsRealtime(0.5f);   // the window settles at its final size first
        LogEnvironment();

        // ---- 1. sync check on the original (RPO.atmos with the RPO movie's picture, decoded live) ---
        app.OpenAny(new[] { Path.Combine(RpoDir, "RPO.atmos"), RpoMovie });
        yield return Select("7.1.4");
        yield return WaitFor(() => app.Movie.FramesShown > 0, 20);
        app.OrbitCamera.SetView("Overview");
        app.Seek(27.0);
        app.Play();
        yield return WaitFor(() => Math.Abs(app.LastSample / 48000.0 - 27.0) < 0.5, 5);
        double appearAudio = -1, appearVideo = -1;
        float deadline = Time.realtimeSinceStartup + 10;
        while (Time.realtimeSinceStartup < deadline)
        {
            Track();
            if (CeilingVisible() && appearAudio < 0)
            {
                appearAudio = app.LastSample / 48000.0;
                appearVideo = app.Movie.LastFramePts - app.Movie.OffsetSeconds;
            }
            if (appearAudio >= 0 && app.LastSample / 48000.0 > 30.0) break;
            yield return null;
        }
        Log("1. ceiling objects (IDs 10, 20) first drawn at audible time " + F(appearAudio) + " s (metadata: 29.558 s); "
            + "picture on screen at that moment: frame at " + F(appearVideo) + " s");
        yield return Shot("rpo_7.1.4_29.9s");

        // ---- 2. live switch while playing --------------------------------------------------------
        float t0 = Time.realtimeSinceStartup;
        app.SelectPreset(app.Presets.First(p => p.Name == "9.1.6"));
        yield return WaitFor(() => app.LayoutReady, 5);
        Log("2. switched 7.1.4 -> 9.1.6 while playing in " + F((Time.realtimeSinceStartup - t0) * 1000, "F0")
            + " ms; still playing: " + app.Playing);
        app.Pause();

        // ---- 3. per-layout levels while the ceiling objects play (53-57 s) -------------------------
        foreach (var name in new[] { "7.1.4", "9.1.6", "5.1.2", "5.1.2 front height", "5.1.4", "2.0" })
            yield return MeasureLayout("RPO", name, 53.0, 2.0);

        // ---- 4. unused slots (original RPO only) ------------------------------------------------------
        trackSlots = false;
        Log("4. object IDs drawn at any point during the RPO.atmos runs: " + string.Join(", ", everShownIds.OrderBy(i => i))
            + " (RPO's unused slots are IDs 12 and 18)");

        // ---- 5. size edit and ceiling8 decorrelated ------------------------------------------------
        app.OpenAny(new[] { Path.Combine(RpoDir, "RPO_s030.atmos"), RpoMovie });
        foreach (var name in new[] { "7.1.4", "9.1.6" })
            yield return MeasureLayout("RPO_s030", name, 53.0, 2.0);

        string ceil8 = Path.Combine(MediaDir, "ceil8d_e16", "RPO_ceil8d_e16.atmos");
        if (File.Exists(ceil8))
        {
            app.OpenAny(new[] { ceil8, RpoMovie });
            foreach (var name in new[] { "7.1.4", "9.1.6" })
                yield return MeasureLayout("RPO_ceiling8_decorr", name, 53.0, 2.0);
        }
        else Log("5. ceiling8_decorr DAMF not found at " + ceil8);

        app.Pause();
        File.WriteAllText(Path.Combine(outDir, "phase1_results.txt"), log.ToString());
        Debug.Log("[Phase1] done");
        Application.Quit();
    }

    public IEnumerator RunMovieTest(AtmosApp app, string outDir, string[] movies)
    {
        this.app = app;
        this.outDir = outDir;
        Directory.CreateDirectory(outDir);
        yield return new WaitForSecondsRealtime(0.5f);   // the window settles at its final size first
        LogEnvironment();
        foreach (var movie in movies)
        {
            string tag = Path.GetFileNameWithoutExtension(movie).Replace(' ', '_');
            Log("open " + movie);
            float t0 = Time.realtimeSinceStartup;
            app.OpenAny(new[] { movie });
            bool midShot = false;
            float deadline = t0 + 900;
            MoviePreparer p = null;
            while (Time.realtimeSinceStartup < deadline)
            {
                p = app.Preparer ?? p;
                if (p != null && !midShot && p.Decode.State == PrepStep.Running && p.Decode.Progress > 0.3)
                {
                    midShot = true;
                    yield return Shot(tag + "_preparing");
                }
                if (p != null && p.Finished) break;
                yield return null;
            }
            float prepSeconds = Time.realtimeSinceStartup - t0;
            if (p == null) { Log("  no preparer started"); continue; }
            foreach (var s in p.Steps)
                Log("  step '" + s.Name + "': " + new[] { "waiting", "running", "done", "failed", "skipped" }[s.State] + " " + s.Detail);
            Log("  finished in " + F(prepSeconds, "F1") + " s" + (p.FromCache ? " (from cache)" : "") + (p.Error != null ? "; error: " + p.Error : ""));
            if (p.Error != null)
            {
                yield return Shot(tag + "_error");
                Log("  after the error, still loaded: audio " + (app.Damf != null ? Path.GetFileName(app.Damf.AtmosPath) : "none")
                    + ", video " + (app.Movie.HasVideo ? Path.GetFileName(app.Movie.Info.SourcePath) : "none"));
                continue;
            }
            yield return WaitFor(() => !app.AudioLoading && app.Damf != null && app.Movie.FramesShown > 0 && app.LayoutReady, 60);
            Log("  media card: " + app.LastMedia);
            Log("  audio loaded: " + app.Damf.Objects.Count() + " objects, " + app.Damf.Elements.Sum(e => e.Events.Count) + " events; video: "
                + (app.Movie.HasVideo ? app.Movie.Summary + ", decoder " + app.Movie.Decoder : "none"));
            app.Seek(60);
            app.Play();
            yield return WaitFor(() => Math.Abs(app.LastSample / 48000.0 - 60) < 0.5 && app.Movie.LastFramePts > 59, 5);
            yield return new WaitForSecondsRealtime(1f);   // let the first frames settle

            float until = Time.realtimeSinceStartup + 8;
            int visibleMax = 0, renderFrames = 0, samples = 0;
            long shown0 = app.Movie.FramesShown;
            double a0 = app.LastSample / 48000.0, worst = 0, sum = 0;
            while (Time.realtimeSinceStartup < until)
            {
                visibleMax = Math.Max(visibleMax, app.RoomView.Objects.Count(o => o.Transform.gameObject.activeSelf));
                // how far the picture on screen trails the sound: 0..1 frame is expected (frames are held until the next)
                double lag = app.LastSample / 48000.0 + app.Movie.OffsetSeconds - app.Movie.LastFramePts;
                worst = Math.Max(worst, Math.Abs(lag));
                sum += lag;
                samples++;
                renderFrames++;
                yield return null;
            }
            double played = app.LastSample / 48000.0 - a0;
            long shown = app.Movie.FramesShown - shown0;
            double fps = app.Movie.Info != null ? app.Movie.Info.Fps : 24;
            Log("  played " + F(played, "F1") + " s from 61 s: " + renderFrames + " rendered frames (" + F(renderFrames / 8.0, "F0") + " fps); film frames shown "
                + shown + " of " + F(played * fps, "F0") + "; picture behind sound: mean " + F(1000 * sum / Math.Max(1, samples), "F0")
                + " ms, worst " + F(1000 * worst, "F0") + " ms (frames are " + F(1000 / fps, "F0") + " ms apart); frame upload "
                + F(app.Movie.LastUploadMs, "F1") + " ms, worst " + F(app.Movie.MaxUploadMs, "F1") + " ms; up to " + visibleMax + " objects drawn");
            yield return Shot(tag + "_playing");

            // seek while playing: how long until the picture is at the new position
            float s0 = Time.realtimeSinceStartup;
            app.Seek(120);
            yield return WaitFor(() => app.Movie.LastFramePts - app.Movie.OffsetSeconds >= 119.9, 5);
            Log("  seek to 2:00 while playing: picture there after " + F((Time.realtimeSinceStartup - s0) * 1000, "F0") + " ms");

            // where each rendered frame's time goes, with all of the video work, without the
            // upload, and with no video at all
            yield return new WaitForSecondsRealtime(1f);
            yield return MeasureFrames("film playing", 5);
            app.Movie.SkipUpload = true;
            yield return MeasureFrames("film decoded, not uploaded", 5);
            app.Movie.SkipUpload = false;
            var info = app.Movie.Info;
            app.Movie.Close();
            yield return MeasureFrames("no film", 5);
            app.Movie.Open(app.Tools.Ffmpeg, info, app.LastSample / 48000.0);
            app.Pause();
        }
        File.WriteAllText(Path.Combine(outDir, "movie_test.txt"), log.ToString());
        Application.Quit();
    }

    public IEnumerator RunUiTest(AtmosApp app, string outDir)
    {
        this.app = app;
        this.outDir = outDir;
        Directory.CreateDirectory(outDir);
        if (!HaveTestFiles()) { File.WriteAllText(Path.Combine(outDir, "ui_test.txt"), log.ToString()); Application.Quit(); yield break; }
        yield return new WaitForSecondsRealtime(0.5f);   // the window settles at its final size first
        LogEnvironment();
        var root = app.UI.GetComponent<UIDocument>().rootVisualElement;
        bool levelColorsBefore = app.RoomView.LevelColors;
        float volumeBefore = PlayerPrefs.GetFloat("volume", -12f);
        string layoutBefore = PlayerPrefs.GetString("layout", "7.1.4");

        // started without a file: just the room and the welcome card
        yield return new WaitForSecondsRealtime(1.5f);
        Log("start-up: " + (app.Damf == null ? "no file open" : "a file is open") + ", welcome card " + (app.UI.WelcomeVisible ? "shown" : "hidden")
            + " with " + RecentFiles.Load().Count + " recent files");
        LogCamera("start-up camera");
        yield return Shot("ui_00_welcome");

        // RPO with its film on 7.1.4, paused just after the ceiling objects come in (29.6 s)
        app.OpenAny(new[] { Path.Combine(RpoDir, "RPO.atmos"), RpoMovie });
        yield return Select("7.1.4");
        yield return WaitFor(() => app.Movie.FramesShown > 0, 20);
        if (app.RoomView.LevelColors) yield return Click(root, "Colors");   // start from the normal look
        yield return SetSlider(root, "volume", -12f);
        app.Seek(29.0);
        app.Play();
        yield return WaitFor(() => Math.Abs(app.LastSample / 48000.0 - 29.0) < 0.5, 5);
        yield return WaitFor(() => app.AudibleSeconds > 31.0, 10);
        app.Pause();
        yield return new WaitForSecondsRealtime(1.5f);
        LogCamera("start");
        Log("media card: " + app.LastMedia);
        Log("welcome card after opening: " + (app.UI.WelcomeVisible ? "still shown" : "hidden") + "; bed dots: "
            + string.Join(", ", app.RoomView.Beds.Select(b => b.Element.BedChannel + " at " + b.Transform.position.ToString("F2"))));
        yield return Shot("ui_0_start");

        var sliders = root.Q(className: "display").Query<Slider>().ToList();
        // Rotate to three quarters of the way = +90 degrees, then Zoom to 0.8
        yield return SetSlider(sliders[0], 0.75f);
        yield return new WaitForSecondsRealtime(1.5f);
        LogCamera("Rotate set to 3/4 (slider now " + F(sliders[0].value, "F0") + ")");
        yield return Shot("ui_1_rotate");
        yield return SetSlider(sliders[1], 0.8f);
        yield return new WaitForSecondsRealtime(1.5f);
        LogCamera("Zoom set to 0.8 (slider now " + F(sliders[1].value, "F2") + ")");
        yield return Shot("ui_2_zoom");
        foreach (var v in new[] { "Top", "Front", "Side", "Seat", "Overview" })
        {
            yield return Click(root, v);
            yield return new WaitForSecondsRealtime(1.5f);
            LogCamera("view " + v + " (sliders show rotate " + F(sliders[0].value, "F0") + ", zoom " + F(sliders[1].value, "F2") + ")");
            yield return Shot("ui_3_view_" + v);
        }

        // Speakers off: just the objects (and the speaker names go with them); then on again
        yield return Click(root, "Speakers");
        yield return null;
        Log("Speakers clicked: speakers " + (app.RoomView.ShowSpeakers ? "shown" : "hidden") + ", labels shown "
            + root.Query<Label>(className: "speaker-label").ToList().Count(l => l.resolvedStyle.display == DisplayStyle.Flex));
        yield return Shot("ui_3_speakers_off");
        yield return Click(root, "Speakers");
        yield return null;
        yield return null;
        Log("Speakers clicked again: speakers " + (app.RoomView.ShowSpeakers ? "shown" : "hidden") + ", labels shown "
            + root.Query<Label>(className: "speaker-label").ToList().Count(l => l.resolvedStyle.display == DisplayStyle.Flex));

        // Couch off: just the head marker, for comparison; then on again
        yield return Click(root, "Couch");
        yield return null;
        Log("Couch clicked: " + (app.RoomView.ShowCouch ? "couch shown" : "head marker only"));
        yield return Shot("ui_3_couch_off");
        yield return Click(root, "Couch");
        yield return null;

        // Side clicked twice: the second click shows the other side
        yield return Click(root, "Side");
        yield return new WaitForSecondsRealtime(1.5f);
        LogCamera("Side, first click");
        yield return Click(root, "Side");
        yield return new WaitForSecondsRealtime(1.5f);
        LogCamera("Side, clicked again");
        yield return Shot("ui_3_view_Side_other");
        yield return Click(root, "Overview");
        yield return new WaitForSecondsRealtime(1.5f);

        // Level colors: speakers green (quiet) to red (loud), after the volume setting
        yield return Click(root, "Colors");
        Log("Levels: Colors clicked: now " + (app.RoomView.LevelColors ? "on" : "off"));
        foreach (float vol in new[] { -12f, -30f, 0f })
        {
            yield return SetSlider(root, "volume", vol);
            app.Seek(29.0);
            app.Play();
            // let the seek land first (the clock still shows the old position on the next frame)
            yield return WaitFor(() => Math.Abs(app.LastSample / 48000.0 - 29.0) < 0.5, 5);
            yield return WaitFor(() => app.AudibleSeconds > 30.5, 10);
            var parts = new List<string>();
            foreach (var sp in app.RoomView.Speakers)
            {
                float db = sp.DisplayDb + app.RoomView.VolumeDb;
                parts.Add(sp.Speaker.Name + " " + (db > -100 ? F(db, "F0") : "off") + " " + ColorName(app.RoomView.LevelColor(db)));
            }
            Log("Level colors at volume " + F(vol, "F0") + " dB (sound gain " + F(app.Audio.Player.OutputGainDb, "F0") + " dB), "
                + F(app.AudibleSeconds, "F1") + " s (speaker dBFS after volume, colour): " + string.Join(", ", parts));
            yield return Shot("ui_4_level_colors_volume" + F(vol, "F0"));
            app.Pause();
        }

        // a .atmos master opened on its own while the film shows: an edited mix of the same
        // length keeps the film's picture
        string film = app.Movie.HasVideo ? Path.GetFileName(app.Movie.Info.SourcePath) : "none";
        app.OpenAny(new[] { Path.Combine(RpoDir, "RPO_s030.atmos") });
        yield return WaitFor(() => !app.AudioLoading, 30);
        yield return WaitFor(() => app.Movie.FramesShown > 0, 10);
        Log("opened RPO_s030.atmos on its own while showing " + film + ": now playing " + Path.GetFileName(app.Damf?.AtmosPath ?? "nothing")
            + ", picture " + (app.Movie.HasVideo ? "kept: " + Path.GetFileName(app.Movie.Info.SourcePath) : "gone"));
        yield return Shot("ui_5_atmos_over_film");

        // the 11.1.8 layout at the ceiling-objects moment, from the Overview and from the side
        yield return Select("11.1.8");
        app.Seek(29.0);
        app.Play();
        yield return WaitFor(() => Math.Abs(app.LastSample / 48000.0 - 29.0) < 0.5, 5);
        yield return WaitFor(() => app.AudibleSeconds > 31.0, 10);
        app.Pause();
        Log("11.1.8 at " + F(app.AudibleSeconds, "F1") + " s: " + string.Join(", ", app.RoomView.Speakers.Select(sp =>
            sp.Speaker.Name + " " + (sp.DisplayDb > -100 ? F(sp.DisplayDb, "F0") : "off"))));
        yield return Click(root, "dB");   // the speaker labels with their levels
        yield return new WaitForSecondsRealtime(0.3f);
        yield return Shot("ui_6_11.1.8_overview");
        yield return Click(root, "Side");
        yield return new WaitForSecondsRealtime(1.5f);
        yield return Shot("ui_6_11.1.8_side");
        yield return Click(root, "dB");
        yield return Click(root, "Overview");

        // put the user's settings back
        if (app.RoomView.LevelColors != levelColorsBefore) yield return Click(root, "Colors");
        yield return SetSlider(root, "volume", volumeBefore);
        app.SelectPreset(app.Presets.FirstOrDefault(pr => pr.Name == layoutBefore) ?? app.CurrentPreset);
        File.WriteAllText(Path.Combine(outDir, "ui_test.txt"), log.ToString());
        Application.Quit();
    }

    void LogCamera(string what)
    {
        var c = app.OrbitCamera;
        Log(what + ": camera yaw " + F(Mathf.DeltaAngle(0, c.Yaw), "F1") + ", pitch " + F(c.Pitch, "F1") + ", distance " + F(c.Distance, "F2") + " m");
    }

    static string ColorName(Color c)
    {
        Color.RGBToHSV(c, out float h, out float sat, out float v);
        if (v < 0.25f) return "dark";
        string hue = h < 0.04f || h > 0.95f ? "red" : h < 0.11f ? "orange" : h < 0.19f ? "yellow" : h < 0.45f ? "green" : "blue";
        return (v < 0.6f ? "dim " : "") + hue;
    }

    // ---- driving the UI. A synthetic mouse click doesn't trigger UI Toolkit's buttons, so: check
    // that the control is what a pointer at its centre would hit (nothing covering it), then press
    // it like the keyboard's Enter does, or set a slider's value, which fires the same change
    // event as dragging it ----

    bool Reachable(VisualElement control)
    {
        var at = control.worldBound.center;
        var hit = control.panel.Pick(at);
        for (var e = hit; e != null; e = e.parent)
            if (e == control) return true;
        Log("  covered: at the centre of " + control.GetType().Name + " '" + ((control as TextElement)?.text ?? control.name) + "' a pointer would hit "
            + (hit == null ? "nothing" : hit.GetType().Name + " " + string.Join(".", hit.GetClasses())));
        return false;
    }

    IEnumerator Click(VisualElement root, string buttonText)
    {
        var b = root.Query<Button>().Where(x => x.text == buttonText).First();
        if (b == null) { Log("  no button '" + buttonText + "'"); yield break; }
        Reachable(b);
        using (var e = NavigationSubmitEvent.GetPooled()) { e.target = b; b.SendEvent(e); }
        yield return null;
    }

    IEnumerator SetSlider(Slider s, float fraction)
    {
        Reachable(s);
        s.value = Mathf.Lerp(s.lowValue, s.highValue, fraction);
        yield return null;
    }

    IEnumerator SetSlider(VisualElement root, string name, float value)
    {
        var s = root.Q<Slider>(name);
        s.value = value;
        yield return null;
    }

    // Unity's FrameTimingManager (needs "frame timing stats" in the player settings): per frame,
    // the main thread's work, the render thread's, the GPU's, and the time spent waiting for the
    // display to take the frame (vsync, or Remote Desktop's frame rate).
    IEnumerator MeasureFrames(string label, float seconds)
    {
        var timing = new FrameTiming[1];
        double main = 0, render = 0, gpu = 0, wait = 0;
        int n = 0, frames = 0;
        long shown0 = app.Movie.FramesShown, decoded0 = app.Movie.FramesDecoded, uploads0 = app.Movie.Uploads;
        double uploadMs0 = app.Movie.UploadMsTotal;
        float t0 = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup < t0 + seconds)
        {
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, timing) == 1)
            {
                main += timing[0].cpuMainThreadFrameTime;
                render += timing[0].cpuRenderThreadFrameTime;
                gpu += timing[0].gpuFrameTime;
                wait += timing[0].cpuMainThreadPresentWaitTime;
                n++;
            }
            frames++;
            yield return null;
        }
        float dt = Time.realtimeSinceStartup - t0;
        long uploads = app.Movie.Uploads - uploads0;
        Log("  frame timing, " + label + ": " + F(frames / dt, "F1") + " fps; per frame (ms) main thread " + F(main / Math.Max(1, n), "F1")
            + " of which waiting for the display " + F(wait / Math.Max(1, n), "F1") + ", render thread " + F(render / Math.Max(1, n), "F1")
            + ", GPU " + F(gpu / Math.Max(1, n), "F1") + (n == 0 ? " (no timings: frame timing stats off?)" : "")
            + "; film frames decoded " + F((app.Movie.FramesDecoded - decoded0) / dt, "F1") + "/s, shown " + F((app.Movie.FramesShown - shown0) / dt, "F1")
            + "/s" + (uploads > 0 ? ", upload " + F((app.Movie.UploadMsTotal - uploadMs0) / uploads, "F1") + " ms each" : ""));
    }

    IEnumerator Select(string name)
    {
        yield return WaitFor(() => !app.AudioLoading, 30);   // opening is asynchronous
        app.SelectPreset(app.Presets.First(p => p.Name == name));
        yield return WaitFor(() => app.LayoutReady, 10);
    }

    IEnumerator MeasureLayout(string file, string layout, double from, double seconds)
    {
        yield return Select(layout);
        app.Seek(from);
        app.Play();
        // let the seek land (the view still shows the old position on this frame)
        yield return WaitFor(() => Math.Abs(app.LastSample / 48000.0 - from) < 0.5, 5);
        // average the meter readings over the window, once the audible time is inside it
        var sum = new double[app.CurrentPreset.Speakers.Count];
        int n = 0;
        float deadline = Time.realtimeSinceStartup + (float)seconds + 5;
        while (Time.realtimeSinceStartup < deadline)
        {
            Track();
            double t = app.LastSample / 48000.0;
            if (t >= from + 0.3 && app.LastMetersValid)
            {
                for (int k = 0; k < sum.Length; k++) sum[k] += app.LastSpeakerMeanSquare[k];
                n++;
            }
            if (t >= from + 0.3 + seconds) break;
            yield return null;
        }
        app.OrbitCamera.SetView("Overview");
        yield return Shot(file + "_" + layout.Replace(' ', '-'));
        app.Pause();

        var parts = new List<string>();
        for (int k = 0; k < sum.Length; k++)
        {
            var sp = app.CurrentPreset.Speakers[k];
            double ms = n > 0 ? sum[k] / n : 0;
            parts.Add(sp.Name + " " + (ms > 1e-14 ? F(10 * Math.Log10(ms), "F1") : "off"));
        }
        Log("3. " + file + " on " + layout + ", meters averaged over " + n + " frames from " + F(from + 0.3, "F1")
            + " s: " + string.Join(", ", parts));
    }

    void Track()
    {
        if (!trackSlots) return;
        foreach (var o in app.RoomView.Objects)
            if (o.Transform.gameObject.activeSelf) everShownIds.Add(o.Element.Id);
    }

    bool CeilingVisible() =>
        app.RoomView.Objects.Where(o => o.Element.Id == 10 || o.Element.Id == 20).All(o => o.Transform.gameObject.activeSelf);

    IEnumerator Shot(string name)
    {
        yield return null;
        yield return new WaitForEndOfFrame();
        ScreenCapture.CaptureScreenshot(Path.Combine(outDir, name + ".png"));
        yield return null;
        yield return null;
    }

    static IEnumerator WaitFor(Func<bool> condition, float seconds)
    {
        float end = Time.realtimeSinceStartup + seconds;
        while (!condition() && Time.realtimeSinceStartup < end) yield return null;
    }
}
