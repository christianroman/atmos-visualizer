// The film on the virtual screen, decoded live from the original file (no converted copy).
//
// Each frame of the app: take the decoded frame whose timestamp matches the audio clock, upload
// its two YUV planes to the GPU, and convert them to RGB (tonemapping HDR) into a mipmapped
// render texture shown on the screen quad. The audio is the clock, so sync is by construction:
// the picture shown is always the frame for "now" on the audio timeline.

using System;
using System.Diagnostics;
using AtmosEngine;
using UnityEngine;

public sealed class MovieScreen : MonoBehaviour
{
    public Renderer Screen;          // the quad on the front wall
    public Material YuvMaterial;     // Atmos/VideoYuv

    public bool HasVideo => reader != null;
    public MovieInfo Info { get; private set; }
    public string Summary { get; private set; } = "none";
    public double LastFramePts { get; private set; } = double.NaN;   // source timeline
    public long FramesShown { get; private set; }
    public float LastUploadMs { get; private set; }
    public float MaxUploadMs { get; private set; }
    public string Error => reader?.Error;
    public string Decoder => reader?.Decoder ?? "";
    public long FramesDecoded => reader?.FramesDecoded ?? 0;
    public double UploadMsTotal { get; private set; }
    public long Uploads { get; private set; }

    /// <summary>Diagnostics only: take frames on time but don't upload or draw them.</summary>
    public bool SkipUpload;

    /// <summary>Source timestamp = audio time + this (the audio track's start on the file timeline).</summary>
    public double OffsetSeconds;

    FfmpegVideoReader reader;
    Texture2D yTex, uvTex;
    RenderTexture rt;
    Material screenMaterial;
    VideoFrame current;
    float lastRestart = -10;
    bool needFirstFrame;   // after opening or seeking: show the first frame even if it's a hair ahead
    readonly Stopwatch watch = new Stopwatch();

    void Awake()
    {
        screenMaterial = Screen.material;   // own instance
        Screen.enabled = false;
    }

    public void Open(string ffmpeg, MovieInfo info, double atAudioSeconds)
    {
        Close();
        if (info == null || !info.HasVideo) return;
        Info = info;
        OffsetSeconds = info.AudioStart;
        int w = info.Width, h = info.Height;
        bool ten = info.TenBit;
        reader = new FfmpegVideoReader(ffmpeg, info.SourcePath, info.VideoStream, w, h, ten, info.Fps);

        yTex = new Texture2D(w, h, ten ? TextureFormat.R16 : TextureFormat.R8, false, true) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        uvTex = new Texture2D(w / 2, h / 2, ten ? TextureFormat.RG32 : TextureFormat.RG16, false, true) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
        {
            useMipMap = true, autoGenerateMips = true, filterMode = FilterMode.Trilinear, anisoLevel = 8, wrapMode = TextureWrapMode.Clamp,
        };
        rt.Create();
        YuvMaterial.SetTexture("_YTex", yTex);
        YuvMaterial.SetTexture("_UVTex", uvTex);
        YuvMaterial.SetFloat("_TenBit", ten ? 1 : 0);
        YuvMaterial.SetFloat("_Bt2020", info.IsBt2020 ? 1 : 0);
        YuvMaterial.SetFloat("_Pq", info.IsPq ? 1 : 0);
        YuvMaterial.SetFloat("_PeakNits", 1000);   // typical UHD Blu-ray mastering peak
        screenMaterial.mainTexture = rt;
        Summary = System.IO.Path.GetFileName(info.SourcePath) + " · " + info.VideoSummary;
        MaxUploadMs = 0;
        FramesShown = 0;
        LastFramePts = double.NaN;
        try
        {
            needFirstFrame = true;
            reader.Start(atAudioSeconds + OffsetSeconds);
        }
        catch (Exception e)
        {
            Summary = "can't start ffmpeg: " + e.Message;
            Close(keepSummary: true);
        }
    }

    public void Close() => Close(false);

    void Close(bool keepSummary)
    {
        if (reader != null)
        {
            reader.Recycle(current);
            current = null;
            reader.Dispose();
            reader = null;
        }
        if (yTex != null) Destroy(yTex);
        if (uvTex != null) Destroy(uvTex);
        if (rt != null) { rt.Release(); Destroy(rt); }
        yTex = uvTex = null;
        rt = null;
        Info = null;
        Screen.enabled = false;
        if (!keepSummary) Summary = "none";
    }

    /// <summary>Jump: restart decoding at the new audio position.</summary>
    public void Seek(double audioSeconds)
    {
        if (reader == null) return;
        needFirstFrame = true;
        reader.Start(Math.Max(0, audioSeconds + OffsetSeconds));
        lastRestart = Time.unscaledTime;
    }

    /// <summary>Show the frame for this audio time. Call once per rendered frame.</summary>
    public void Present(double audioSeconds, bool playing)
    {
        if (reader == null) return;
        double pts = audioSeconds + OffsetSeconds;
        // the first frame after a seek may start a few ms after the requested time (frame grid);
        // show it rather than waiting - essential when paused, where the clock doesn't move
        var f = reader.TakeFrameFor(needFirstFrame ? pts + 0.25 : pts);
        if (f != null)
        {
            needFirstFrame = false;
            if (!SkipUpload)
            {
                watch.Restart();
                int ySize = yTex.width * yTex.height * (Info.TenBit ? 2 : 1);
                yTex.SetPixelData(f.Data, 0, 0);
                uvTex.SetPixelData(f.Data, 0, ySize);
                yTex.Apply(false);
                uvTex.Apply(false);
                Graphics.Blit(null, rt, YuvMaterial);
                LastUploadMs = (float)watch.Elapsed.TotalMilliseconds;
                UploadMsTotal += LastUploadMs;
                Uploads++;
                if (FramesShown > 5) MaxUploadMs = Mathf.Max(MaxUploadMs, LastUploadMs);
            }
            reader.Recycle(current);
            current = f;
            LastFramePts = f.Pts;
            FramesShown++;
            if (!Screen.enabled) Screen.enabled = true;
        }
        else if (playing && !reader.Ended && Time.unscaledTime - lastRestart > 2f)
        {
            // Fell behind (decoding can't keep up, or a long stall): start again a little ahead.
            double newest = reader.NextPts ?? LastFramePts;
            if (!double.IsNaN(newest) && newest < pts - 1.0) Seek(audioSeconds + 0.3);
        }
    }

    void OnDestroy() => Close();
}
