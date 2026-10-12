// The app's on-screen interface, built with UI Toolkit (Unity's retained-mode UI, styled with a
// CSS-like stylesheet, Atmos.uss) - closer to UIKit views than to Unity's immediate-mode GUI.
//
// It is sized like a Windows app: the panel scales with Windows' display scaling for the app's
// window (WindowsDisplay: 100% = 1x, 300% = 3x, updated live), uses the Windows UI font (Segoe
// UI) and the Windows 11 type sizes (Caption 12, Body 14, Subtitle 20).
//
// Layout: top bar (title, file, views), info card (top left), display controls (bottom left),
// speaker layout picker (right), transport (bottom), labels that follow the 3D speakers.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AtmosEngine;
using UnityEngine;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

[RequireComponent(typeof(UIDocument))]
public sealed class AtmosUI : MonoBehaviour
{
    public StyleSheet Style;

    public event Action<LayoutPreset> PresetChosen;
    public event Action<RecentItem> RecentChosen;
    public event Action PlayPauseClicked, OpenClicked, CancelPreparationClicked, ClosePreparationClicked;
    public event Action<double> SeekRequested;
    public event Action<float> VolumeChanged, RotateChanged, ZoomChanged;
    public event Action<string> ViewChosen;
    public event Action<bool> SpeakersToggled, ObjectsToggled, LabelsToggled, LevelsToggled, LevelColorsToggled, CouchToggled;

    public bool ShowSpeakers { get; private set; } = true;
    public bool ShowLabels { get; private set; } = true;
    public bool ShowLevels { get; private set; }
    public bool Scrubbing { get; private set; }

    VisualElement root, presetList, labelLayer;
    ScrollView presetScroll;
    Label fileLabel, statusLabel, timeLabel, durationLabel, volumeLabel;
    Slider timeline, volume, rotate, zoom;
    Button levelColorsButton;
    PlayIcon playIcon;
    bool cameraDrag;
    readonly Dictionary<LayoutPreset, VisualElement> cards = new Dictionary<LayoutPreset, VisualElement>();
    readonly Dictionary<string, Button> viewButtons = new Dictionary<string, Button>();
    readonly List<Label> speakerLabels = new List<Label>();
    IPanel panel;
    UIDocument document;

    /// <summary>The layout needs about this much room (Windows effective pixels). In a smaller
    /// window the whole UI shrinks to fit rather than overlapping.</summary>
    const float MinWidth = 1120f, MinHeight = 640f;

    void Update()
    {
        if (document == null || document.panelSettings == null) return;
        // the panel uses constant pixel size, so this scale is the whole story: Windows' scaling,
        // times a shrink factor when the window is too small for the layout at that scaling
        float windows = WindowsDisplay.Scale;
        float fit = Mathf.Min(1f, Screen.width / windows / MinWidth, Screen.height / windows / MinHeight);
        float scale = windows * Mathf.Max(0.5f, fit);
        if (Mathf.Abs(document.panelSettings.scale - scale) > 0.005f) document.panelSettings.scale = scale;
    }

    void OnDisable()
    {
        if (document != null && document.panelSettings != null) document.panelSettings.scale = 1f;
    }

    void OnEnable()
    {
        var doc = GetComponent<UIDocument>();
        document = doc;
        root = doc.rootVisualElement;
        root.Clear();
        if (Style != null) root.styleSheets.Add(Style);
        root.AddToClassList("atmos-root");
        root.pickingMode = PickingMode.Ignore;
        UseWindowsFont(root);

        labelLayer = new VisualElement { name = "labels", pickingMode = PickingMode.Ignore };
        labelLayer.AddToClassList("label-layer");
        root.Add(labelLayer);

        BuildTopBar();
        BuildInfoCard();
        BuildDisplayPanel();
        BuildLayoutPanel();
        BuildTransport();
        BuildPreparationPanel();
        BuildWelcome();
    }

    /// <summary>Segoe UI (Variable on Windows 11), with Semibold for emphasis as Windows does;
    /// Unity's default font elsewhere.</summary>
    static void UseWindowsFont(VisualElement root)
    {
        FontAsset regular = null, semibold = null;
        foreach (var family in new[] { "Segoe UI Variable Text", "Segoe UI" })
        {
            try
            {
                regular = FontAsset.CreateFontAsset(family, "Regular");
                semibold = FontAsset.CreateFontAsset(family, "Semibold");
            }
            catch (Exception) { regular = null; }
            if (regular != null) break;
        }
        if (regular == null) return;
        if (semibold != null && regular.fontWeightTable != null && regular.fontWeightTable.Length > 7)
        {
            regular.fontWeightTable[6] = new FontWeightPair { regularTypeface = semibold };
            regular.fontWeightTable[7] = new FontWeightPair { regularTypeface = semibold };   // "bold" in the stylesheet
        }
        root.style.unityFontDefinition = new StyleFontDefinition(FontDefinition.FromSDFFont(regular));
    }

    // ---- building -------------------------------------------------------------------------

    void BuildTopBar()
    {
        var bar = Panel("topbar");
        var left = new VisualElement().WithClass("topbar-left");
        left.Add(new Label("Atmos Visualizer").WithClass("app-title"));
        fileLabel = new Label("No file open").WithClass("file");
        left.Add(fileLabel);
        bar.Add(left);

        var views = new VisualElement().WithClass("topbar-right");
        views.Add(new Label("View").WithClass("caption"));
        foreach (var v in new[] { "Overview", "Seat", "Top", "Front", "Side" })
        {
            string view = v;
            var b = new Button(() => { SelectView(view); ViewChosen?.Invoke(view); }) { text = v };
            b.AddToClassList("btn");
            b.AddToClassList("btn--segment");
            viewButtons[v] = b;
            views.Add(b);
        }
        bar.Add(views);
        root.Add(bar);
        SelectView("Overview");
    }

    // The media card: label/value rows. The first, "Atmos metadata", counts the objects and bed
    // channels next to dots in their 3D-view colours (white, purple), so it doubles as the legend.
    VisualElement metadataRow, bedPart, mediaRows;
    Label objectsText, bedsText;
    readonly List<(VisualElement row, Label key, Label value)> mediaRowViews = new List<(VisualElement, Label, Label)>();

    void BuildInfoCard()
    {
        var card = Panel("info");
        metadataRow = new VisualElement().WithClass("media-row");
        metadataRow.Add(new Label("Atmos metadata").WithClass("media-key"));
        var value = new VisualElement().WithClass("media-value-row");
        value.Add(Dot("legend-dot--object"));
        objectsText = new Label("").WithClass("media-value");
        objectsText.AddToClassList("media-value--inline");
        value.Add(objectsText);
        bedPart = new VisualElement().WithClass("media-value-row");
        bedPart.Add(new Label("·").WithClass("media-sep"));
        bedPart.Add(Dot("legend-dot--bed"));
        bedsText = new Label("").WithClass("media-value");
        bedsText.AddToClassList("media-value--inline");
        bedPart.Add(bedsText);
        value.Add(bedPart);
        metadataRow.Add(value);
        card.Add(metadataRow);
        mediaRows = new VisualElement().WithClass("media-rows");
        card.Add(mediaRows);
        statusLabel = new Label("").WithClass("status");
        card.Add(statusLabel);
        root.Add(card);
    }

    static VisualElement Dot(string colourClass)
    {
        var dot = new VisualElement().WithClass("legend-dot");
        dot.AddToClassList(colourClass);
        return dot;
    }

    void BuildDisplayPanel()
    {
        var p = Panel("display");
        p.Add(new Label("Display").WithClass("caption-strong"));

        rotate = CameraSlider(p, "Rotate", -180, 180, v => RotateChanged?.Invoke(v));
        zoom = CameraSlider(p, "Zoom", 0, 1, v => ZoomChanged?.Invoke(v));

        // two labelled rows, lined up with Rotate and Zoom: what to show, and how to show levels
        var show = ToggleLine(p, "Show");
        show.Add(Toggle("Speakers", true, on => { ShowSpeakers = on; SpeakersToggled?.Invoke(on); }, "Show the speakers and their names"));
        show.Add(Toggle("Objects", true, on => ObjectsToggled?.Invoke(on), "Show the Atmos objects (white dots, moving) and bed channels (purple dots, fixed)"));
        show.Add(Toggle("Labels", true, on => { ShowLabels = on; LabelsToggled?.Invoke(on); }, "Show speaker names"));
        show.Add(Toggle("Couch", true, on => CouchToggled?.Invoke(on), "A couch with a seated person at the listening position (off: just a head marker)"));
        var levels = ToggleLine(p, "Levels");
        levels.Add(Toggle("dB", false, on => { ShowLevels = on; LevelsToggled?.Invoke(on); }, "Add each speaker's level in dB to its name"));
        levelColorsButton = Toggle("Colors", false, on => LevelColorsToggled?.Invoke(on),
            "Speakers from green (quiet) to red (loud), like a Trinnov speaker display. Follows the volume setting.");
        levels.Add(levelColorsButton);
        root.Add(p);
    }

    static VisualElement ToggleLine(VisualElement parent, string name)
    {
        var row = new VisualElement().WithClass("toggle-line");
        row.Add(new Label(name).WithClass("slider-label"));
        parent.Add(row);
        return row;
    }

    Slider CameraSlider(VisualElement parent, string name, float lo, float hi, Action<float> changed)
    {
        var row = new VisualElement().WithClass("slider-row");
        row.Add(new Label(name).WithClass("slider-label"));
        var s = new Slider(lo, hi).WithClass("camera-slider");
        s.RegisterValueChangedCallback(e => changed(e.newValue));
        s.RegisterCallback<PointerDownEvent>(_ => cameraDrag = true, TrickleDown.TrickleDown);
        s.RegisterCallback<PointerUpEvent>(_ => cameraDrag = false, TrickleDown.TrickleDown);
        s.RegisterCallback<PointerCaptureOutEvent>(_ => cameraDrag = false, TrickleDown.TrickleDown);
        row.Add(s);
        parent.Add(row);
        return s;
    }

    void BuildLayoutPanel()
    {
        var p = Panel("layouts");
        p.Add(new Label("Speaker layout").WithClass("subtitle"));
        p.Add(new Label("How BS.2127 spreads the sound over your speakers. Click a layout, or press [ and ]. What you hear is always the stereo render.").WithClass("hint"));
        presetScroll = new ScrollView(ScrollViewMode.Vertical);
        presetScroll.AddToClassList("preset-scroll");
        presetList = presetScroll.contentContainer;
        p.Add(presetScroll);
        root.Add(p);
    }

    void BuildTransport()
    {
        var t = Panel("transport");
        var open = Button("Open…", () => OpenClicked?.Invoke());
        open.tooltip = "A movie (MKV with a TrueHD Atmos track) or a .atmos master. A .atmos opened while a film of the same length is showing keeps the film's picture.";
        open.AddToClassList("btn--primary");
        t.Add(open);
        t.Add(new VisualElement().WithClass("divider"));

        var play = new Button(() => PlayPauseClicked?.Invoke());
        play.AddToClassList("btn");
        play.AddToClassList("play-button");
        play.tooltip = "Play / pause (Space)";
        playIcon = new PlayIcon();
        play.Add(playIcon);
        t.Add(play);

        timeLabel = new Label("00:00.000").WithClass("time");
        t.Add(timeLabel);
        timeline = new Slider(0, 1) { name = "timeline" };
        timeline.AddToClassList("timeline");
        timeline.RegisterValueChangedCallback(e => SeekRequested?.Invoke(e.newValue));
        timeline.RegisterCallback<PointerDownEvent>(_ => Scrubbing = true, TrickleDown.TrickleDown);
        timeline.RegisterCallback<PointerUpEvent>(_ => Scrubbing = false, TrickleDown.TrickleDown);
        timeline.RegisterCallback<PointerCaptureOutEvent>(_ => Scrubbing = false, TrickleDown.TrickleDown);
        t.Add(timeline);
        durationLabel = new Label("00:00").WithClass("duration");
        t.Add(durationLabel);
        t.Add(new VisualElement().WithClass("divider"));

        volumeLabel = new Label("Volume").WithClass("volume-label");
        t.Add(volumeLabel);
        volume = new Slider(MinVolumeDb, MaxVolumeDb) { name = "volume" };
        volume.AddToClassList("volume");
        volume.tooltip = "How loud the sound is. Level colors follow it, like a processor's master volume.";
        volume.RegisterValueChangedCallback(e =>
        {
            volumeLabel.text = VolumeText(e.newValue);
            VolumeChanged?.Invoke(e.newValue);
        });
        t.Add(volume);
        root.Add(t);
    }

    VisualElement Panel(string name)
    {
        var p = new VisualElement { name = name };
        p.AddToClassList("panel");
        p.AddToClassList(name);
        return p;
    }

    Button Button(string text, Action onClick)
    {
        var b = new Button(onClick) { text = text };
        b.AddToClassList("btn");
        return b;
    }

    Button Toggle(string text, bool initial, Action<bool> changed, string tooltip)
    {
        var b = new Button { text = text, tooltip = tooltip };
        b.AddToClassList("btn");
        b.userData = initial;
        b.EnableInClassList("btn--on", initial);
        b.clicked += () =>
        {
            bool state = !(bool)b.userData;
            b.userData = state;
            b.EnableInClassList("btn--on", state);
            changed(state);
        };
        return b;
    }

    void SelectView(string view)
    {
        foreach (var kv in viewButtons) kv.Value.EnableInClassList("btn--on", kv.Key == view);
    }

    // ---- movie preparation panel ------------------------------------------------------------

    VisualElement prepPanel;
    Label prepTitle, prepNote, prepError;
    readonly List<(Label name, VisualElement fill, Label state)> prepRows = new List<(Label, VisualElement, Label)>();
    Button prepCancel, prepClose;

    void BuildPreparationPanel()
    {
        prepPanel = Panel("prep");
        prepTitle = new Label("").WithClass("prep-title");
        prepPanel.Add(prepTitle);
        for (int i = 0; i < 3; i++)
        {
            var row = new VisualElement().WithClass("prep-row");
            var name = new Label("").WithClass("prep-step");
            var bar = new VisualElement().WithClass("prep-bar");
            var fill = new VisualElement().WithClass("prep-fill");
            bar.Add(fill);
            var state = new Label("").WithClass("prep-state");
            row.Add(name);
            row.Add(bar);
            row.Add(state);
            prepPanel.Add(row);
            prepRows.Add((name, fill, state));
        }
        prepNote = new Label("").WithClass("prep-note");
        prepError = new Label("").WithClass("prep-error");
        prepPanel.Add(prepNote);
        prepPanel.Add(prepError);
        var buttons = new VisualElement().WithClass("prep-buttons");
        prepCancel = Button("Cancel", () => CancelPreparationClicked?.Invoke());
        prepClose = Button("Close", () => ClosePreparationClicked?.Invoke());
        buttons.Add(prepCancel);
        buttons.Add(prepClose);
        prepPanel.Add(buttons);
        prepPanel.style.display = DisplayStyle.None;
        root.Add(prepPanel);
    }

    public void ShowPreparation(string fileName, string cacheRoot)
    {
        prepTitle.text = "Preparing " + fileName;
        prepNote.text = "One-time step: the decoded Atmos audio is cached in " + cacheRoot
                        + ". The picture is decoded live from the original file - nothing is converted.";
        prepError.text = "";
        prepCancel.style.display = DisplayStyle.Flex;
        prepClose.style.display = DisplayStyle.None;
        prepPanel.style.display = DisplayStyle.Flex;
    }

    public void UpdatePreparation(MoviePreparer p)
    {
        var steps = p.Steps;
        for (int i = 0; i < prepRows.Count; i++)
        {
            var (name, fill, state) = prepRows[i];
            if (i >= steps.Count) { name.parent.style.display = DisplayStyle.None; continue; }
            var s = steps[i];
            name.text = (i + 1) + ". " + s.Name;
            float pct = s.State == PrepStep.Done || s.State == PrepStep.Skipped ? 100f
                      : s.State == PrepStep.Running ? (float)(s.Progress * 100) : 0f;
            fill.style.width = new Length(pct, LengthUnit.Percent);
            fill.EnableInClassList("prep-fill--failed", s.State == PrepStep.Failed);
            switch (s.State)
            {
                case PrepStep.Running:
                    var left = s.Remaining();
                    state.text = (s.Progress * 100).ToString("0") + "%" + (left.HasValue ? " · " + Short(left.Value) + " left" : "");
                    break;
                case PrepStep.Done: state.text = i == 0 || string.IsNullOrEmpty(s.Detail) ? "done" : s.Detail; break;   // step 1's detail is in the title
                case PrepStep.Skipped: state.text = string.IsNullOrEmpty(s.Detail) ? "not needed" : s.Detail; break;
                case PrepStep.Failed: state.text = "failed"; break;
                default: state.text = "waiting"; break;
            }
        }
        if (p.Probe.State == PrepStep.Done && !string.IsNullOrEmpty(p.Probe.Detail))
            prepTitle.text = "Preparing " + System.IO.Path.GetFileName(p.SourcePath) + "\n" + p.Probe.Detail;
        prepError.text = p.Error ?? "";
        bool finished = p.Finished;
        prepCancel.style.display = finished ? DisplayStyle.None : DisplayStyle.Flex;
        prepClose.style.display = finished && p.Error != null ? DisplayStyle.Flex : DisplayStyle.None;
        if (finished && p.Error == null) prepNote.text = p.FromCache ? "Ready (from the cache)." : "Ready.";
    }

    public void HidePreparation() => prepPanel.style.display = DisplayStyle.None;

    static string Short(TimeSpan t) => t.TotalMinutes >= 1 ? Math.Ceiling(t.TotalMinutes) + " min" : Math.Ceiling(t.TotalSeconds) + " s";

    // ---- welcome card (nothing open yet) ------------------------------------------------------

    VisualElement welcomePanel, recentList;
    Label recentCaption;

    void BuildWelcome()
    {
        welcomePanel = Panel("welcome");
        welcomePanel.Add(new Label("Open a film to start").WithClass("subtitle"));
        welcomePanel.Add(new Label("A movie with a Dolby TrueHD Atmos track (MKV), or a .atmos master. To see a .atmos mix with its film, open the film first, then the .atmos.").WithClass("hint"));
        var open = Button("Open…", () => OpenClicked?.Invoke());
        open.AddToClassList("btn--primary");
        open.AddToClassList("welcome-open");
        welcomePanel.Add(open);
        recentCaption = new Label("Recent").WithClass("caption-strong");
        recentCaption.AddToClassList("welcome-recent-caption");
        welcomePanel.Add(recentCaption);
        recentList = new VisualElement().WithClass("welcome-recent");
        welcomePanel.Add(recentList);
        welcomePanel.style.display = DisplayStyle.None;
        root.Add(welcomePanel);
    }

    public void ShowWelcome(IList<RecentItem> recent)
    {
        recentList.Clear();
        foreach (var item in recent)
        {
            var it = item;
            var b = Button(it.Title, () => RecentChosen?.Invoke(it));
            b.AddToClassList("recent-item");
            b.tooltip = string.Join("\n", it.Paths);
            recentList.Add(b);
        }
        var shown = recent.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        recentCaption.style.display = shown;
        recentList.style.display = shown;
        welcomePanel.style.display = DisplayStyle.Flex;
    }

    public void HideWelcome() => welcomePanel.style.display = DisplayStyle.None;

    public bool WelcomeVisible => welcomePanel.resolvedStyle.display == DisplayStyle.Flex;

    // ---- presets ----------------------------------------------------------------------------

    public void SetPresets(IList<LayoutPreset> presets)
    {
        presetList.Clear();
        cards.Clear();
        foreach (var preset in presets)
        {
            var p = preset;
            var card = new VisualElement();
            card.AddToClassList("preset-card");
            card.Add(new PresetDiagram(p));
            var text = new VisualElement();
            text.AddToClassList("preset-text");
            text.Add(new Label(p.Name).WithClass("preset-name"));
            text.Add(new Label(p.Description).WithClass("preset-desc"));
            text.Add(new Label(Counts(p)).WithClass("preset-counts"));
            card.Add(text);
            card.RegisterCallback<ClickEvent>(_ => PresetChosen?.Invoke(p));
            card.tooltip = p.Description;
            presetList.Add(card);
            cards[p] = card;
        }
    }

    static string Counts(LayoutPreset p)
    {
        var parts = new List<string> { p.EarLevelCount + " ear level" };
        if (p.LfeCount > 0) parts.Add(p.LfeCount + " sub");
        if (p.OverheadCount > 0) parts.Add(p.OverheadCount + " overhead");
        return string.Join(" · ", parts);
    }

    public void SetSelectedPreset(LayoutPreset preset, bool busy)
    {
        foreach (var kv in cards)
        {
            kv.Value.EnableInClassList("preset-card--selected", kv.Key == preset);
            kv.Value.EnableInClassList("preset-card--busy", kv.Key == preset && busy);
        }
        if (preset != null && cards.TryGetValue(preset, out var card))
            card.schedule.Execute(() => presetScroll.ScrollTo(card));   // after layout, so it lands
    }

    // ---- state from the app -----------------------------------------------------------------

    public void SetTitle(string title) => fileLabel.text = title;

    public void SetMedia(MediaSummary m)
    {
        metadataRow.style.display = m.Objects != null ? DisplayStyle.Flex : DisplayStyle.None;
        bedPart.style.display = m.Beds != null ? DisplayStyle.Flex : DisplayStyle.None;
        if (m.Objects != null && objectsText.text != m.Objects) objectsText.text = m.Objects;
        if (m.Beds != null && bedsText.text != m.Beds) bedsText.text = m.Beds;
        while (mediaRowViews.Count < m.Rows.Count)
        {
            var row = new VisualElement().WithClass("media-row");
            var key = new Label("").WithClass("media-key");
            var value = new Label("").WithClass("media-value");
            row.Add(key);
            row.Add(value);
            mediaRows.Add(row);
            mediaRowViews.Add((row, key, value));
        }
        for (int i = 0; i < mediaRowViews.Count; i++)
        {
            var (row, key, value) = mediaRowViews[i];
            bool used = i < m.Rows.Count;
            row.style.display = used ? DisplayStyle.Flex : DisplayStyle.None;
            if (!used) continue;
            if (key.text != m.Rows[i].Key) key.text = m.Rows[i].Key;
            if (value.text != m.Rows[i].Value) value.text = m.Rows[i].Value;
        }
    }


    public void SetStatus(string text)
    {
        statusLabel.text = text ?? "";
        statusLabel.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
    }

    public void SetDuration(double seconds)
    {
        timeline.lowValue = 0;
        timeline.highValue = (float)Math.Max(0.001, seconds);
        durationLabel.text = FormatTime(seconds, false);
    }

    public void SetTime(double seconds, bool playing)
    {
        timeLabel.text = FormatTime(seconds, true);
        if (!Scrubbing) timeline.SetValueWithoutNotify((float)seconds);
        playIcon.Playing = playing;
    }

    // 0 dB at most: what the 3D view shows always matches what you hear
    public const float MinVolumeDb = -40f, MaxVolumeDb = 0f;

    static string VolumeText(float db) =>
        string.Format(CultureInfo.InvariantCulture, "Volume {0:0} dB", db).Replace("-", "\u2212");

    public void SetVolume(float db)
    {
        volume.SetValueWithoutNotify(db);
        volumeLabel.text = VolumeText(db);
    }

    /// <summary>Keep the Rotate/Zoom sliders in step with the camera (dragging, scrolling, views).</summary>
    public void SetCamera(float yaw, float zoomValue)
    {
        if (cameraDrag) return;
        rotate.SetValueWithoutNotify(Mathf.DeltaAngle(0, yaw));
        zoom.SetValueWithoutNotify(zoomValue);
    }

    public void SetLevelColors(bool on)
    {
        levelColorsButton.userData = on;
        levelColorsButton.EnableInClassList("btn--on", on);
    }

    public static string FormatTime(double s, bool millis)
    {
        if (s < 0) s = 0;
        var t = TimeSpan.FromSeconds(s);
        string mmss = (t.Hours > 0 ? t.Hours + ":" : "") + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00");
        return millis ? mmss + "." + t.Milliseconds.ToString("000") : mmss;
    }

    // ---- speaker labels that follow the 3D view -------------------------------------------------

    public void UpdateSpeakerLabels(Camera cam, IList<RoomView.SpeakerVisual> speakers)
    {
        if (panel == null) panel = root?.panel;   // attached once the UIDocument's panel is live
        while (speakerLabels.Count < speakers.Count)
        {
            var l = new Label { pickingMode = PickingMode.Ignore };
            l.AddToClassList("speaker-label");
            labelLayer.Add(l);
            speakerLabels.Add(l);
        }
        for (int i = 0; i < speakerLabels.Count; i++)
        {
            var l = speakerLabels[i];
            if (i >= speakers.Count || !ShowLabels || !ShowSpeakers || panel == null)
            {
                l.style.display = DisplayStyle.None;
                continue;
            }
            var s = speakers[i];
            Vector3 world = s.Transform.position + Vector3.up * (s.Speaker.Z >= 0.5 ? -0.18f : 0.32f);
            Vector3 viewport = cam.WorldToViewportPoint(world);
            if (viewport.z <= 0)
            {
                l.style.display = DisplayStyle.None;
                continue;
            }
            Vector2 p = RuntimePanelUtils.CameraTransformWorldToPanel(panel, world, cam);
            l.style.display = DisplayStyle.Flex;
            // the level in the accent colour, so it reads apart from the speaker's name
            l.text = ShowLevels
                ? s.Speaker.Name + "  <color=#60CDFF>" + (s.DisplayDb > -120 ? s.DisplayDb.ToString("0", CultureInfo.InvariantCulture).Replace("-", "\u2212") : "–") + "</color>"
                : s.Speaker.Name;
            l.style.left = p.x - 16;
            l.style.top = p.y - 9;
        }
    }

    /// <summary>True if a screen position (origin bottom-left, as Input.mousePosition) is over a panel.</summary>
    public bool IsOverUI(Vector2 screen)
    {
        if (panel == null) panel = root?.panel;
        if (panel == null) return false;
        var p = RuntimePanelUtils.ScreenToPanel(panel, new Vector2(screen.x, Screen.height - screen.y));
        var picked = panel.Pick(p);
        return picked != null && picked != root;
    }

    // ---- small drawn elements ---------------------------------------------------------------------

    /// <summary>Top-down sketch of a preset: ear-level speakers on the room outline, overhead
    /// speakers drawn inside it, the sub as a small square.</summary>
    sealed class PresetDiagram : VisualElement
    {
        readonly LayoutPreset preset;

        public PresetDiagram(LayoutPreset preset)
        {
            this.preset = preset;
            AddToClassList("preset-diagram");
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
        }

        void Draw(MeshGenerationContext ctx)
        {
            var r = contentRect;
            if (r.width < 4) return;
            var p = ctx.painter2D;
            float pad = 5, w = r.width - 2 * pad, h = r.height - 2 * pad;
            Vector2 Map(double x, double y, double inset) =>
                new Vector2(pad + (float)((x * inset + 1) / 2 * w), pad + (float)((1 - y * inset) / 2 * h));

            p.strokeColor = new Color(1, 1, 1, 0.22f);
            p.lineWidth = 1f;
            p.BeginPath();
            p.MoveTo(new Vector2(pad, pad));
            p.LineTo(new Vector2(pad + w, pad));
            p.LineTo(new Vector2(pad + w, pad + h));
            p.LineTo(new Vector2(pad, pad + h));
            p.ClosePath();
            p.Stroke();

            // screen along the front wall
            p.strokeColor = new Color(1, 1, 1, 0.45f);
            p.lineWidth = 2f;
            p.BeginPath();
            p.MoveTo(new Vector2(pad + w * 0.3f, pad - 2));
            p.LineTo(new Vector2(pad + w * 0.7f, pad - 2));
            p.Stroke();

            foreach (var s in preset.Speakers)
            {
                if (s.IsLfe)
                {
                    var c = Map(s.X, s.Y, 0.62);
                    p.fillColor = new Color(0.68f, 0.38f, 1f, 0.9f);
                    p.BeginPath();
                    p.MoveTo(c + new Vector2(-2.5f, -2.5f)); p.LineTo(c + new Vector2(2.5f, -2.5f)); p.LineTo(c + new Vector2(2.5f, 2.5f)); p.LineTo(c + new Vector2(-2.5f, 2.5f));
                    p.ClosePath();
                    p.Fill();
                    continue;
                }
                bool top = s.Z >= 0.5;
                var pos = Map(s.X, s.Y, !top ? 1.0 : s.Mount == "wall" ? 0.8 : 0.5);
                p.fillColor = top ? new Color(0.25f, 0.85f, 1f, 1f) : new Color(0.92f, 0.94f, 1f, 1f);
                p.BeginPath();
                p.Arc(pos, top ? 2.2f : 2.7f, 0, 360);
                p.Fill();
            }
        }
    }

    /// <summary>Play / pause icon drawn as vector shapes (no font glyphs needed).</summary>
    sealed class PlayIcon : VisualElement
    {
        bool playing;

        public bool Playing
        {
            get => playing;
            set { if (playing != value) { playing = value; MarkDirtyRepaint(); } }
        }

        public PlayIcon()
        {
            AddToClassList("play-icon");
            pickingMode = PickingMode.Ignore;
            generateVisualContent += ctx =>
            {
                var r = contentRect;
                var p = ctx.painter2D;
                p.fillColor = Color.white;
                float s = Mathf.Min(r.width, r.height), x0 = (r.width - s) / 2, y0 = (r.height - s) / 2;
                if (playing)
                {
                    foreach (float bx in new[] { 0.18f, 0.58f })
                    {
                        p.BeginPath();
                        p.MoveTo(new Vector2(x0 + s * bx, y0 + s * 0.15f));
                        p.LineTo(new Vector2(x0 + s * (bx + 0.24f), y0 + s * 0.15f));
                        p.LineTo(new Vector2(x0 + s * (bx + 0.24f), y0 + s * 0.85f));
                        p.LineTo(new Vector2(x0 + s * bx, y0 + s * 0.85f));
                        p.ClosePath();
                        p.Fill();
                    }
                }
                else
                {
                    p.BeginPath();
                    p.MoveTo(new Vector2(x0 + s * 0.22f, y0 + s * 0.12f));
                    p.LineTo(new Vector2(x0 + s * 0.88f, y0 + s * 0.5f));
                    p.LineTo(new Vector2(x0 + s * 0.22f, y0 + s * 0.88f));
                    p.ClosePath();
                    p.Fill();
                }
            };
        }
    }
}

static class UiExtensions
{
    public static T WithClass<T>(this T e, string cls) where T : VisualElement
    {
        e.AddToClassList(cls);
        return e;
    }
}

/// <summary>What the media card shows: legend lines (null hides them) and label/value rows.</summary>
public sealed class MediaSummary
{
    public string Objects, Beds;
    public readonly List<KeyValuePair<string, string>> Rows = new List<KeyValuePair<string, string>>();

    public MediaSummary Add(string key, string value)
    {
        Rows.Add(new KeyValuePair<string, string>(key, value));
        return this;
    }

    public override string ToString() =>
        string.Join(" | ", new[] { Objects, Beds }.Where(x => x != null).Concat(Rows.Select(r => r.Key + ": " + r.Value)));
}
