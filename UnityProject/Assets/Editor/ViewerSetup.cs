// Builds the viewer scene, its materials and UI assets, and a Windows player.
// Menu: Atmos > Viewer > ..., or headless:
//   Unity.exe -batchmode -quit -projectPath UnityProject -executeMethod ViewerSetup.All

using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.Video;

public static class ViewerSetup
{
    const string ScenePath = "Assets/Scenes/Viewer.unity";
    public const string PlayerPath = "Builds/Viewer/AtmosVisualizer.exe";

    static Material MakeMaterial(string path, string shader, Color color)
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(Shader.Find(shader));
            AssetDatabase.CreateAsset(mat, path);
        }
        mat.shader = Shader.Find(shader);
        mat.color = color;
        EditorUtility.SetDirty(mat);
        return mat;
    }

    [MenuItem("Atmos/Viewer/Build Scene")]
    public static void BuildScene()
    {
        Directory.CreateDirectory("Assets/Scenes");
        Directory.CreateDirectory("Assets/Materials");
        Directory.CreateDirectory("Assets/UI");

        var lineMat = MakeMaterial("Assets/Materials/Line.mat", "Sprites/Default", Color.white);
        var speakerMat = MakeMaterial("Assets/Materials/Speaker.mat", "Unlit/Color", Color.gray);
        var objectMat = MakeMaterial("Assets/Materials/Object.mat", "Sprites/Default", Color.yellow);
        var screenMat = MakeMaterial("Assets/Materials/VideoScreen.mat", "Unlit/Texture", Color.white);
        var yuvMat = MakeMaterial("Assets/Materials/VideoYuv.mat", "Atmos/VideoYuv", Color.white);
        var furnitureMat = MakeMaterial("Assets/Materials/Furniture.mat", "Atmos/SoftLit", Color.gray);

        // UI Toolkit needs a theme and panel settings; the theme just imports Unity's default one.
        const string themePath = "Assets/UI/AtmosTheme.tss";
        if (!File.Exists(themePath)) File.WriteAllText(themePath, "@import url(\"unity-theme://default\");\n");
        AssetDatabase.ImportAsset(themePath);
        var theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(themePath);
        const string panelPath = "Assets/UI/AtmosPanelSettings.asset";
        var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(panelPath);
        if (panel == null)
        {
            panel = ScriptableObject.CreateInstance<PanelSettings>();
            AssetDatabase.CreateAsset(panel, panelPath);
        }
        panel.themeStyleSheet = theme;
        // Like a Windows app: sizes follow Windows' display scaling. AtmosUI sets the scale every
        // frame from the window's DPI (WindowsDisplay), so the panel itself uses plain pixels.
        panel.scaleMode = PanelScaleMode.ConstantPixelSize;
        panel.scale = 1;
        EditorUtility.SetDirty(panel);
        var style = AssetDatabase.LoadAssetAtPath<StyleSheet>("Assets/UI/Atmos.uss");
        if (style == null) throw new Exception("Assets/UI/Atmos.uss missing");

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.035f, 0.04f, 0.055f);
        cam.fieldOfView = 50;
        cam.nearClipPlane = 0.05f;
        camGo.AddComponent<AudioListener>();
        var orbit = camGo.AddComponent<OrbitCamera>();

        // the screen on the front wall; a Quad faces -Z, i.e. towards the seat
        var screen = GameObject.CreatePrimitive(PrimitiveType.Quad);
        screen.name = "Screen";
        UnityEngine.Object.DestroyImmediate(screen.GetComponent<Collider>());
        screen.transform.position = new Vector3(0, 1.66f, Room.Depth / 2 - 0.03f);
        screen.transform.localScale = new Vector3(3.6f, 2.025f, 1);
        var screenRenderer = screen.GetComponent<MeshRenderer>();
        screenRenderer.sharedMaterial = screenMat;
        var movie = screen.AddComponent<MovieScreen>();   // decodes the original video live with ffmpeg
        movie.Screen = screenRenderer;
        movie.YuvMaterial = yuvMat;

        // a dark frame behind the picture, so the screen is still there when no video is loaded
        var frame = GameObject.CreatePrimitive(PrimitiveType.Quad);
        frame.name = "Screen Frame";
        UnityEngine.Object.DestroyImmediate(frame.GetComponent<Collider>());
        frame.transform.position = new Vector3(0, 1.66f, Room.Depth / 2 - 0.015f);
        frame.transform.localScale = new Vector3(3.72f, 2.145f, 1);
        frame.GetComponent<MeshRenderer>().sharedMaterial = MakeMaterial("Assets/Materials/ScreenFrame.mat", "Unlit/Color", new Color(0.06f, 0.065f, 0.08f));

        var audioGo = new GameObject("Atmos Audio");
        var src = audioGo.AddComponent<AudioSource>();
        src.playOnAwake = false;
        src.spatialBlend = 0;
        var atmos = audioGo.AddComponent<AtmosAudioSource>();

        var roomGo = new GameObject("Room");
        var room = roomGo.AddComponent<RoomView>();
        room.LineMaterial = lineMat;
        room.SpeakerMaterial = speakerMat;
        room.ObjectMaterial = objectMat;
        room.FurnitureMaterial = furnitureMat;

        var uiGo = new GameObject("UI");
        var doc = uiGo.AddComponent<UIDocument>();
        doc.panelSettings = panel;
        var ui = uiGo.AddComponent<AtmosUI>();
        ui.Style = style;

        var appGo = new GameObject("App");
        var app = appGo.AddComponent<AtmosApp>();
        appGo.AddComponent<Phase1Test>();
        app.Audio = atmos;
        app.Movie = movie;
        app.RoomView = room;
        app.UI = ui;
        app.OrbitCamera = orbit;
        app.Camera = cam;

        EditorSceneManager.SaveScene(scene, ScenePath);
        EditorBuildSettings.scenes = new[]
        {
            new EditorBuildSettingsScene(ScenePath, true),
            new EditorBuildSettingsScene("Assets/Scenes/Phase0.unity", true),
        };
        AssetDatabase.SaveAssets();
        Debug.Log("[ViewerSetup] scene saved to " + ScenePath);
    }

    [MenuItem("Atmos/Viewer/Build Player")]
    public static void BuildPlayer()
    {
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = PlayerPath,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None,
        });
        Debug.Log("[ViewerSetup] build " + report.summary.result + ": " + report.summary.outputPath
                  + ", " + report.summary.totalErrors + " errors, " + report.summary.totalWarnings + " warnings");
        if (report.summary.result != BuildResult.Succeeded) throw new Exception("player build failed");
    }

    public static void All()
    {
        try
        {
            Phase0Setup.ConfigureProject();
            PlayerSettings.fullScreenMode = FullScreenMode.MaximizedWindow;   // the viewer opens maximised
            PlayerSettings.enableFrameTimingStats = true;   // lets the movie test see where each frame's time goes
            BuildScene();
            BuildPlayer();
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Debug.LogError("[ViewerSetup] FAILED: " + e);
            EditorApplication.Exit(1);
        }
    }
}
