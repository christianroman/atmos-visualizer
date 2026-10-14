// The viewer's controller: opens files, owns playback, switches speaker layouts live, and feeds
// the 3D view and the UI each frame.
//
// Open... (one button):
//   a movie (.mkv, ...)        audio: the Atmos (TrueHD or DD+) or DTS:X track is extracted and decoded once (cached);
//                              video: the original, decoded live while it plays
//   a .atmos master            its movie is found automatically if it came from the cache; otherwise,
//                              opened while a film of the same length is showing, it keeps that
//                              film's picture (an edited mix of it, e.g. RPO_s030.atmos over RPO)
//   a .atmos AND a movie       that audio with that movie's picture (only possible when both are in
//                              one folder: the Windows dialog can't select across folders)
// Heavy work (parsing metadata, computing gains) runs off the main thread so the window stays
// responsive.
//
// It starts with the room and a welcome card (Open… and the recent files) rather than reopening the
// last film.
//
// Command line (all optional):
//   -open <file> [<file>]          as if chosen in Open...
//   -atmos <NAME.atmos> [-video <movie>]
//   -layout <name>                 start with this speaker layout, e.g. 9.1.6
//   -phase1test <dir> | -movietest <dir> <files...> | -uitest <dir>   scripted checks

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AtmosEngine;
using UnityEngine;

public sealed class AtmosApp : MonoBehaviour
{
    public AtmosAudioSource Audio;
    public MovieScreen Movie;
    public RoomView RoomView;
    public AtmosUI UI;
    public OrbitCamera OrbitCamera;
    public Camera Camera;

    public List<LayoutPreset> Presets { get; private set; } = new List<LayoutPreset>();
    public LayoutPreset CurrentPreset { get; private set; }
    public LayoutPreset MeteredPreset { get; private set; }   // the layout the meters are showing now
    public DamfFile Damf { get; private set; }
    public bool AudioLoading => loadingAudio != null;
    public MoviePreparer Preparer => preparer;

    /// <summary>Where prepared movies are cached (decoded audio, ~100 MB per minute).</summary>
    public static string CacheRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AtmosVisualizer", "Cache");

    /// <summary>How far the audible sound lags the audio clock: Unity's mixer adds one DSP buffer
    /// (measured), the output queue a few more (inferred from the DSP buffer settings).</summary>
    public double OutputLatencySeconds { get; private set; }

    LayoutPreset stereoPreset;
    ExternalTools tools;
    public ExternalTools Tools => tools;
    int meterId;
    Task<GainTimeline> pendingTimeline;
    LayoutPreset pendingPreset;
    Task<(DamfFile damf, GainTimeline stereo)> loadingAudio;
    string loadingPath, loadingTitle;
    MovieInfo loadingVideo;
    readonly double[] speakerMs = new double[StreamingPlayer.MaxMeterChannels];
    double[] elementMs = new double[0];
    float volumeDb = -12f;   // RPO's room-relative stereo render peaks at +10.7 dBFS
    double queuedSeek = -1;
    float lastSeekTime = -10;
    string audioTitle, videoNote;
    MoviePreparer preparer;
    bool prepAudioOpened;
    float prepFinishedAt = -1;
    string[] openedPaths;     // what was chosen in Open…, for the Recent list once it has loaded
    MovieInfo loadingDecodedFrom, audioSource;   // the movie the audio was decoded from (null: a .atmos master)
    int audioBits = 24, audioChannels;
    MovieInfo pictureToKeep;  // the film showing when a .atmos was opened on its own
    bool recordRecent = true;

    // ---- start-up ---------------------------------------------------------------------------

    // Always open maximised. Unity otherwise restores the last window it saw, which can be a
    // useless 640x480 left over from a disconnected Remote Desktop session. Unity's own
    // -screen-width/-screen-height/-window-mode arguments still win.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void OpenMaximised()
    {
        if (Application.isEditor) return;
        var a = Environment.GetCommandLineArgs();
        if (a.Contains("-screen-width") || a.Contains("-screen-height") || a.Contains("-screen-fullscreen") || a.Contains("-window-mode")) return;
        var r = Screen.currentResolution;
        Screen.SetResolution(r.width, r.height, FullScreenMode.MaximizedWindow);
    }

    void Start()
    {
        Application.targetFrameRate = 60;
        AudioSettings.GetDSPBufferSize(out int bufferLength, out int buffers);
        OutputLatencySeconds = (1 + buffers) * (double)bufferLength / AudioSettings.outputSampleRate;
        tools = ExternalTools.Find(Path.Combine(Application.persistentDataPath, "settings.yaml"),
            Path.Combine(Application.streamingAssetsPath, "Tools"));   // harletty comes with the app

        var errors = new List<string>();
        Presets = LayoutPreset.LoadAll(Path.Combine(Application.streamingAssetsPath, "Layouts"), errors);
        stereoPreset = Presets.FirstOrDefault(p => p.Name == "2.0");
        if (errors.Count > 0) UI.SetStatus("Layout files skipped: " + string.Join("; ", errors));
        if (stereoPreset == null)
        {
            UI.SetStatus("Missing StreamingAssets/Layouts/2.0.yaml - needed for the stereo output.");
            return;
        }

        UI.SetPresets(Presets);
        UI.PresetChosen += SelectPreset;
        UI.PlayPauseClicked += TogglePlay;
        UI.SeekRequested += RequestSeek;
        UI.OpenClicked += () =>
        {
            var files = NativeFileDialog.OpenFiles(
                "Open a movie (MKV with a Dolby Atmos or DTS:X track) or a .atmos master - or a .atmos and a movie together",
                Dir(PlayerPrefs.GetString("lastOpenDir", "")),
                "Movies and Atmos masters (*.mkv;*.m2ts;*.mp4;*.ec3;*.atmos)", "*.mkv;*.mka;*.m2ts;*.ts;*.mp4;*.m4v;*.mov;*.ec3;*.eac3;*.dts;*.atmos",
                "All files (*.*)", "*.*");
            if (files.Length > 0) OpenAny(files);
        };
        UI.RecentChosen += item => OpenAny(item.Paths, item.Title);
        UI.CancelPreparationClicked += () => preparer?.Cancel();
        UI.ClosePreparationClicked += () =>
        {
            preparer = null;
            UI.HidePreparation();
            if (Damf == null && !AudioLoading) ShowWelcome();   // nothing open: back to the start
        };
        UI.VolumeChanged += db =>
        {
            volumeDb = db;
            ApplyVolume();
            PlayerPrefs.SetFloat("volume", db);
        };
        UI.ViewChosen += OrbitCamera.SetView;
        UI.RotateChanged += deg => OrbitCamera.TargetYaw = deg;
        UI.ZoomChanged += z => OrbitCamera.Zoom = z;
        UI.SpeakersToggled += on => RoomView.ShowSpeakers = on;
        UI.CouchToggled += on => RoomView.ShowCouch = on;
        UI.ObjectsToggled += on => RoomView.ShowObjects = on;
        UI.LevelColorsToggled += on => { RoomView.LevelColors = on; PlayerPrefs.SetInt("levelColors", on ? 1 : 0); };
        OrbitCamera.IsPointerOverUI = UI.IsOverUI;
        OrbitCamera.SetView("Overview");

        volumeDb = Mathf.Clamp(PlayerPrefs.GetFloat("volume", -12f), AtmosUI.MinVolumeDb, AtmosUI.MaxVolumeDb);
        ApplyVolume();
        UI.SetVolume(volumeDb);
        bool levelColors = PlayerPrefs.GetInt("levelColors", 0) == 1;
        RoomView.LevelColors = levelColors;
        UI.SetLevelColors(levelColors);

        string layout = Arg("-layout") ?? PlayerPrefs.GetString("layout", "7.1.4");
        SelectPreset(Presets.FirstOrDefault(p => p.Name == layout) ?? Presets.FirstOrDefault(p => p.Name == "7.1.4") ?? Presets[0]);

        // scripted test runs are silent and don't touch the Recent list
        bool testRun = Arg("-phase1test") != null || Args("-movietest").Length > 0 || Arg("-uitest") != null;
        if (testRun)
        {
            AudioListener.volume = 0;
            recordRecent = false;
        }

        var open = Args("-open");
        if (open.Length > 0) OpenAny(open);
        else if (Arg("-atmos") != null) OpenAny(new[] { Arg("-atmos"), Arg("-video") }.Where(p => p != null).ToArray());
        else ShowWelcome();   // just the room until a file is chosen
        RefreshFileInfo();

        string test = Arg("-phase1test");
        if (test != null) StartCoroutine(GetComponent<Phase1Test>().Run(this, test));
        var mt = Args("-movietest");
        if (mt.Length > 0) StartCoroutine(GetComponent<Phase1Test>().RunMovieTest(this, mt[0], mt.Skip(1).ToArray()));
        string ui = Arg("-uitest");
        if (ui != null) StartCoroutine(GetComponent<Phase1Test>().RunUiTest(this, ui));
    }

    static string Arg(string name) => Args(name).FirstOrDefault();

    static string[] Args(string name)
    {
        var a = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(a, name);
        return i < 0 ? new string[0] : a.Skip(i + 1).TakeWhile(x => !x.StartsWith("-")).ToArray();
    }

    static string Dir(string path) => string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path);

    // The volume (-40..0 dB) sets the sound, and Level colors follow it like a master volume.
    void ApplyVolume()
    {
        RoomView.VolumeDb = volumeDb;
        if (Audio.Player != null) Audio.Player.OutputGainDb = volumeDb;
    }

    void ShowWelcome() => UI.ShowWelcome(RecentFiles.Load());

    // ---- opening ----------------------------------------------------------------------------

    public void OpenAny(string[] paths, string title = null)
    {
        if (paths.Length == 0) return;
        PlayerPrefs.SetString("lastOpenDir", paths[0]);
        openedPaths = paths;
        pictureToKeep = null;
        UI.HideWelcome();
        string atmos = paths.FirstOrDefault(p => Path.GetExtension(p).Equals(".atmos", StringComparison.OrdinalIgnoreCase));
        string movie = paths.FirstOrDefault(p => !Path.GetExtension(p).Equals(".atmos", StringComparison.OrdinalIgnoreCase));
        if (atmos == null)
        {
            PrepareMovie(movie);
            return;
        }
        // A .atmos: its picture comes from the movie chosen with it, or the movie it was decoded from.
        MovieInfo video = null;
        if (movie != null)
        {
            try { video = MovieInfo.Probe(tools, movie); }
            catch (Exception e) { UI.SetStatus("Couldn't read the video in " + Path.GetFileName(movie) + ": " + e.Message); }
        }
        else
        {
            video = MovieInfo.ForCachedAtmos(atmos);
            if (video == null && Movie.HasVideo) pictureToKeep = Movie.Info;   // decided once its length is known
        }
        if (video != null && !File.Exists(video.SourcePath)) video = null;
        OpenAudio(atmos, title ?? (video != null && movie == null ? Path.GetFileName(video.SourcePath) : Path.GetFileName(atmos)), video,
                  MovieInfo.ForCachedAtmos(atmos));
    }

    public void PrepareMovie(string path)
    {
        preparer?.Cancel();
        Pause();   // the current film stays loaded until the new one's audio is ready
        preparer = new MoviePreparer(path, CacheRoot, tools);
        prepAudioOpened = false;
        prepFinishedAt = -1;
        preparer.Start();
        UI.ShowPreparation(Path.GetFileName(path), CacheRoot);
        UI.SetStatus("");
    }

    void PollPreparer()
    {
        var p = preparer;
        if (p == null) return;
        UI.UpdatePreparation(p);
        if (p.AudioReady && !prepAudioOpened)
        {
            prepAudioOpened = true;
            OpenAudio(p.AtmosPath, Path.GetFileName(p.SourcePath), p.Info, p.Info);
        }
        if (p.Finished && p.Error == null)
        {
            if (prepFinishedAt < 0) prepFinishedAt = Time.unscaledTime;
            if (Time.unscaledTime - prepFinishedAt > 2.5f)
            {
                UI.HidePreparation();
                preparer = null;
            }
        }
    }

    /// <summary>Loads a .atmos master (parsing and stereo gains in the background); the video,
    /// if any, starts once the audio is ready.</summary>
    public void OpenAudio(string path, string title = null, MovieInfo video = null, MovieInfo decodedFrom = null)
    {
        var stereo = stereoPreset;
        loadingPath = path;
        loadingTitle = title ?? Path.GetFileName(path);
        loadingVideo = video;
        loadingDecodedFrom = decodedFrom;
        UI.SetTitle(loadingTitle);
        UI.SetMedia(new MediaSummary().Add("Audio", "loading…"));
        loadingAudio = Task.Run(() =>
        {
            var damf = DamfFile.Load(path);
            return (damf, new GainTimeline(damf, stereo));
        });
    }

    void PollLoadingAudio()
    {
        var task = loadingAudio;
        if (task == null || !task.IsCompleted) return;
        loadingAudio = null;
        if (task.IsFaulted)
        {
            var e = task.Exception?.GetBaseException();
            UI.SetStatus("Couldn't open " + Path.GetFileName(loadingPath) + ": " + e?.Message);
            RefreshFileInfo();
            Debug.LogException(e);
            if (Damf == null) ShowWelcome();
            return;
        }
        var (damf, stereo) = task.Result;
        Audio.Open(damf, stereo);
        Audio.Player.SafetyLimiter = true;
        ApplyVolume();
        Damf = damf;
        audioSource = loadingDecodedFrom;
        try
        {
            using (var caf = damf.OpenAudio())
            {
                audioBits = caf.BitsPerChannel;
                audioChannels = caf.Channels;
            }
        }
        catch (Exception) { audioBits = 24; audioChannels = damf.Elements.Count; }
        elementMs = new double[damf.Elements.Count];
        RoomView.SetDamf(damf);
        MeteredPreset = null;

        double seconds = Audio.Player.FrameCount / Audio.Player.SampleRate;
        audioTitle = loadingTitle;
        UI.SetDuration(seconds);
        UI.SetStatus("");
        // a .atmos opened on its own over a film of the same length: an edited mix of that film,
        // so keep its picture (and remember the pair for Recent)
        var keep = pictureToKeep;
        pictureToKeep = null;
        if ((loadingVideo == null || !loadingVideo.HasVideo) && keep != null && keep.HasVideo
            && Math.Abs(keep.DurationSeconds - seconds) < 1.0 && File.Exists(keep.SourcePath))
        {
            loadingVideo = keep;
            if (openedPaths != null && openedPaths.Length == 1) openedPaths = new[] { openedPaths[0], keep.SourcePath };
        }
        if (recordRecent && openedPaths != null) RecentFiles.Add(loadingTitle, openedPaths);

        if (loadingVideo != null && loadingVideo.HasVideo)
        {
            Movie.Open(tools.Ffmpeg, loadingVideo, 0);
            videoNote = null;
        }
        else
        {
            Movie.Close();
            // decoded from a file without a picture (a DD+ demo clip, an .mka), or a .atmos on its own
            videoNote = audioSource != null ? "none (the file has no picture)"
                                            : "none - to see a .atmos with its film, open the film first, then the .atmos";
        }
        RefreshFileInfo();
        SelectPreset(CurrentPreset);
    }

    /// <summary>What the media card shows now (for tests).</summary>
    public MediaSummary LastMedia { get; private set; }

    void RefreshFileInfo()
    {
        var m = new MediaSummary();
        if (loadingAudio != null)
        {
            UI.SetTitle(loadingTitle);
            m.Add("Audio", "loading…");
        }
        else if (Damf == null)
        {
            UI.SetTitle("No file open");
            m.Add("Audio", "none loaded");
            m.Add("Video", "none");
        }
        else
        {
            UI.SetTitle(audioTitle);
            int objects = Damf.Objects.Count();
            if (Damf.IsDtsX) m.MetadataKey = "DTS:X";
            m.Objects = objects + " dynamic object" + (objects == 1 ? "" : "s");
            var beds = Damf.Beds.Select(b => b.BedChannel).ToList();
            if (beds.Count > 0)
            {
                // overhead: Atmos's top pair, and DTS:X's front and rear heights
                var overhead = new[] { "Lts", "Rts", "Lfh", "Rfh", "Lrh", "Rrh" };
                int lfe = beds.Count(b => b == "LFE"), top = beds.Count(overhead.Contains), ear = beds.Count - lfe - top;
                string shape = beds.Count <= 2 ? string.Join(" ", beds) : ear + "." + lfe + (top > 0 ? "." + top : "");
                m.Beds = beds.Count + " bed channel" + (beds.Count == 1 ? "" : "s") + " (" + shape + ")";
            }

            var c = CultureInfo.InvariantCulture;
            string Mbps(double bitsPerSecond) => (bitsPerSecond / 1e6).ToString("0.00", c) + " Mb/s";
            if (audioSource != null && !string.IsNullOrEmpty(audioSource.AudioProfile))
            {
                m.Add("Format", audioSource.AudioProfile);
                m.Add("Bitrate", audioSource.AudioBitrate > 0 ? Mbps(audioSource.AudioBitrate) + " average" : "not stated in the file");
            }
            else
            {
                m.Add("Format", "Dolby Atmos master (.atmos)");
                m.Add("Bitrate", Mbps((double)audioChannels * audioBits * Damf.SampleRate) + " uncompressed PCM");
            }
            m.Add("Audio", (Damf.SampleRate / 1000).ToString("0.#", c) + " kHz · " + audioBits + "-bit · "
                           + AtmosUI.FormatTime(Audio.Player.FrameCount / Audio.Player.SampleRate, false));

            if (!string.IsNullOrEmpty(Movie.Error)) m.Add("Video", "can't decode: " + Movie.Error);
            else if (Movie.HasVideo)
            {
                m.Add("Video", Movie.Info.VideoSummary + " · " + (Movie.Decoder.StartsWith("GPU", StringComparison.Ordinal) ? "GPU" : "CPU") + " decoding");
                if (audioSource == null || !string.Equals(audioSource.SourcePath, Movie.Info.SourcePath, StringComparison.OrdinalIgnoreCase))
                    m.Add("Picture", "from " + Path.GetFileName(Movie.Info.SourcePath));
            }
            else m.Add("Video", videoNote ?? "none");

            m.Add("Renderer", "ITU-R BS.2127 (libear) · room-relative");
            m.Add("Speakers", (CurrentPreset != null ? CurrentPreset.Name : "?") + " shown");
            m.Add("Output", "Stereo (default output format)");
        }
        LastMedia = m;
        UI.SetMedia(m);
    }

    // ---- layouts ----------------------------------------------------------------------------

    public void SelectPreset(LayoutPreset preset)
    {
        if (preset == null) return;
        CurrentPreset = preset;
        PlayerPrefs.SetString("layout", preset.Name);
        if (Damf == null)
        {
            RoomView.SetPreset(preset);
            MeteredPreset = preset;
            UI.SetSelectedPreset(preset, false);
            return;
        }
        // Gains for a layout are computed once per file, off the main thread; the view switches
        // over when they're ready (instant for a short clip).
        UI.SetSelectedPreset(preset, true);
        var damf = Damf;
        pendingPreset = preset;
        pendingTimeline = Task.Run(() => new GainTimeline(damf, preset));
    }

    void PollPendingLayout()
    {
        if (pendingTimeline == null || !pendingTimeline.IsCompleted) return;
        var task = pendingTimeline;
        var preset = pendingPreset;
        pendingTimeline = null;
        if (task.IsFaulted)
        {
            UI.SetStatus("Couldn't build " + preset.Name + ": " + task.Exception?.GetBaseException().Message);
            UI.SetSelectedPreset(CurrentPreset, false);
            return;
        }
        if (preset != CurrentPreset || Audio.Player == null) return;   // superseded
        meterId = Audio.Player.SetMeterTimeline(task.Result);
        RoomView.SetPreset(preset);
        MeteredPreset = preset;
        UI.SetSelectedPreset(preset, false);
    }

    public bool LayoutReady => pendingTimeline == null && loadingAudio == null && MeteredPreset == CurrentPreset;

    // the latest meter reading, for tests
    public long LastSample { get; private set; }
    public bool LastMetersValid { get; private set; }
    public double[] LastSpeakerMeanSquare => speakerMs;

    // ---- transport --------------------------------------------------------------------------

    public bool Playing => Audio.Player != null && Audio.Player.Playing;

    public void TogglePlay()
    {
        if (Audio.Player == null) return;
        if (Audio.Player.Playing) Pause();
        else Play();
    }

    public void Play()
    {
        if (Audio.Player == null) return;
        if (Audio.PositionAtEnd) Seek(0);
        Audio.Play();
    }

    public void Pause()
    {
        if (Audio.Player != null) Audio.Pause();
    }

    public void Seek(double seconds)
    {
        if (Audio.Player == null) return;
        seconds = Math.Max(0, Math.Min(seconds, Audio.Player.FrameCount / Audio.Player.SampleRate));
        Audio.Seek(seconds);
        Movie.Seek(seconds);
        lastSeekTime = Time.unscaledTime;
        queuedSeek = -1;
    }

    void RequestSeek(double seconds)
    {
        // dragging the timeline: seek at most ~8 times a second, and always to the final position
        if (Time.unscaledTime - lastSeekTime > 0.12f) Seek(seconds);
        else queuedSeek = seconds;
    }

    /// <summary>What the listener is hearing now, in seconds on the audio timeline.</summary>
    public double AudibleSeconds => Audio.Player == null ? 0
        : Math.Max(0, Audio.ClockSeconds - (Playing ? OutputLatencySeconds : 0));

    public long AudibleSample => Audio.Player == null ? 0 : (long)(AudibleSeconds * Audio.Player.SampleRate);

    // ---- per frame ----------------------------------------------------------------------------

    void Update()
    {
        PollPreparer();
        PollLoadingAudio();
        PollPendingLayout();
        if (queuedSeek >= 0 && Time.unscaledTime - lastSeekTime > 0.12f) Seek(queuedSeek);
        HandleKeys();

        double t = AudibleSeconds;
        Movie.Present(t, Playing);

        long sample = AudibleSample;
        bool have = false;
        LastSample = sample;
        if (Audio.Player != null && MeteredPreset == CurrentPreset && meterId != 0)
            have = Audio.Player.ReadMeters(sample - 2048, sample + 1, meterId, speakerMs, elementMs) > 0;
        RoomView.UpdateVisuals(sample, have ? speakerMs : null, have ? elementMs : null, Time.unscaledDeltaTime);
        LastMetersValid = have;

        UI.SetTime(t, Playing);
        UI.SetCamera(OrbitCamera.Yaw, OrbitCamera.Zoom);
        if (Camera != null) UI.UpdateSpeakerLabels(Camera, RoomView.Speakers);
        if (Time.frameCount % 30 == 0) RefreshFileInfo();
    }

    void HandleKeys()
    {
        if (Input.GetKeyDown(KeyCode.Space)) TogglePlay();
        double step = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? 30 : 5;
        if (Input.GetKeyDown(KeyCode.RightArrow)) Seek(AudibleSeconds + step);
        if (Input.GetKeyDown(KeyCode.LeftArrow)) Seek(AudibleSeconds - step);
        if (Input.GetKeyDown(KeyCode.Home)) Seek(0);
        int index = Presets.IndexOf(CurrentPreset);
        if (Presets.Count > 0 && (Input.GetKeyDown(KeyCode.PageDown) || Input.GetKeyDown(KeyCode.RightBracket))) SelectPreset(Presets[(index + 1) % Presets.Count]);
        if (Presets.Count > 0 && (Input.GetKeyDown(KeyCode.PageUp) || Input.GetKeyDown(KeyCode.LeftBracket))) SelectPreset(Presets[(index - 1 + Presets.Count) % Presets.Count]);
    }

    void OnApplicationQuit()
    {
        preparer?.Cancel();
        PlayerPrefs.Save();
    }
}
