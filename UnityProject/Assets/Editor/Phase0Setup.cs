// Editor automation for Phase 0: configures the project, builds the test scene and a Windows
// player. Runs from the menu (Atmos > Phase 0) or headless:
//   Unity.exe -batchmode -quit -projectPath UnityProject -executeMethod Phase0Setup.All

using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Video;

public static class Phase0Setup
{
    const string ScenePath = "Assets/Scenes/Phase0.unity";
    const string MaterialPath = "Assets/Materials/Screen.mat";
    const string PluginPath = "Assets/Plugins/x86_64/ear_shim.dll";
    public const string PlayerPath = "Builds/Phase0/AtmosVisualizer.exe";

    [MenuItem("Atmos/Phase 0/Configure Project")]
    public static void ConfigureProject()
    {
        // Audio: 48 kHz so Unity doesn't resample the DAMF audio; stereo output.
        var audioManager = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/AudioManager.asset")[0];
        var so = new SerializedObject(audioManager);
        so.FindProperty("m_SampleRate").intValue = 48000;
        so.FindProperty("Default Speaker Mode").intValue = (int)AudioSpeakerMode.Stereo;
        so.FindProperty("m_RequestedDSPBufferSize").intValue = 1024;
        so.ApplyModifiedPropertiesWithoutUndo();

        PlayerSettings.companyName = "AtmosVisualizer";
        PlayerSettings.productName = "Atmos Visualizer";
        PlayerSettings.runInBackground = true;          // keep playing when the window loses focus
        PlayerSettings.visibleInBackground = true;
        PlayerSettings.SplashScreen.show = false;
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.defaultScreenWidth = 1280;
        PlayerSettings.defaultScreenHeight = 720;
        PlayerSettings.resizableWindow = true;
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);

        // The native plugin: 64-bit Windows, editor and player.
        var plugin = (PluginImporter)AssetImporter.GetAtPath(PluginPath);
        if (plugin == null) throw new Exception("plugin not found at " + PluginPath);
        plugin.SetCompatibleWithAnyPlatform(false);
        plugin.SetCompatibleWithEditor(true);
        plugin.SetEditorData("CPU", "x86_64");
        plugin.SetEditorData("OS", "Windows");
        plugin.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows64, true);
        plugin.SetPlatformData(BuildTarget.StandaloneWindows64, "CPU", "x86_64");
        plugin.SaveAndReimport();

        AssetDatabase.SaveAssets();
        Debug.Log("[Phase0Setup] project configured");
    }

    [MenuItem("Atmos/Phase 0/Build Scene")]
    public static void BuildScene()
    {
        Directory.CreateDirectory("Assets/Scenes");
        Directory.CreateDirectory("Assets/Materials");
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var camGo = new GameObject("Main Camera");
        camGo.tag = "MainCamera";
        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.08f, 0.08f, 0.1f);
        camGo.transform.position = new Vector3(0, 1.5f, -4.2f);
        camGo.AddComponent<AudioListener>();
        var capture = camGo.AddComponent<ListenerCapture>();

        // A 16:9 screen. Unity is Y-up and left-handed: +Z points away from the camera here.
        var screen = GameObject.CreatePrimitive(PrimitiveType.Quad);
        screen.name = "Screen";
        UnityEngine.Object.DestroyImmediate(screen.GetComponent<Collider>());
        screen.transform.position = new Vector3(0, 1.5f, 0);
        screen.transform.localScale = new Vector3(4f, 2.25f, 1f);
        var mat = new Material(Shader.Find("Unlit/Texture"));
        AssetDatabase.CreateAsset(mat, MaterialPath);
        var renderer = screen.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = mat;
        var video = screen.AddComponent<VideoPlayer>();
        video.playOnAwake = false;
        video.source = VideoSource.Url;
        video.renderMode = VideoRenderMode.MaterialOverride;
        video.targetMaterialRenderer = renderer;
        video.targetMaterialProperty = "_MainTex";
        video.audioOutputMode = VideoAudioOutputMode.None;
        video.waitForFirstFrame = true;
        video.skipOnDrop = true;
        var sync = screen.AddComponent<VideoSync>();

        var audioGo = new GameObject("Atmos Audio");
        var src = audioGo.AddComponent<AudioSource>();
        src.playOnAwake = false;
        src.spatialBlend = 0;
        var atmos = audioGo.AddComponent<AtmosAudioSource>();
        sync.Audio = atmos;
        capture.Source = atmos;

        var harnessGo = new GameObject("Phase 0 Harness");
        var harness = harnessGo.AddComponent<Phase0Harness>();
        harness.Audio = atmos;
        harness.Capture = capture;
        harness.Video = video;
        harness.Sync = sync;

        EditorSceneManager.SaveScene(scene, ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        AssetDatabase.SaveAssets();
        Debug.Log("[Phase0Setup] scene saved to " + ScenePath);
    }

    [MenuItem("Atmos/Phase 0/Build Player")]
    public static void BuildPlayer()
    {
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = PlayerPath,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None,
        });
        Debug.Log("[Phase0Setup] build " + report.summary.result + ": " + report.summary.outputPath
                  + ", " + report.summary.totalErrors + " errors, " + report.summary.totalWarnings + " warnings");
        if (report.summary.result != BuildResult.Succeeded) throw new Exception("player build failed");
    }

    /// <summary>Entry point for -executeMethod.</summary>
    public static void All()
    {
        try
        {
            ConfigureProject();
            BuildScene();
            BuildPlayer();
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Debug.LogError("[Phase0Setup] FAILED: " + e);
            EditorApplication.Exit(1);
        }
    }
}
