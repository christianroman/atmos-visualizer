// Records Unity's final audio mix (what goes to the sound card), for measurement.
//
// Attach next to the AudioListener: a filter there sees the whole mix after every AudioSource.
// It also notes which file frame the AtmosAudioSource rendered for each buffer, so the recording
// can be lined up exactly with an offline render.

using System;
using System.IO;
using UnityEngine;

public sealed class ListenerCapture : MonoBehaviour
{
    public AtmosAudioSource Source;

    float[] buffer;
    int channels;
    volatile int framesWritten;
    volatile bool capturing;
    long firstFileFrame = -1;
    int discontinuities;
    long expectedNextFileFrame = -1;
    int callbackFrames;

    public bool Done => !capturing && buffer != null;
    public long FirstFileFrame => firstFileFrame;
    public int Discontinuities => discontinuities;
    public int CallbackFrames => callbackFrames;

    long previousSourceFrame = -1;

    public void StartCapture(double seconds, int sampleRate, int outputChannels)
    {
        buffer = new float[(int)(seconds * sampleRate) * outputChannels];
        framesWritten = 0;
        channels = 0;
        firstFileFrame = -1;
        expectedNextFileFrame = -1;
        discontinuities = 0;
        capturing = true;
    }

    void OnAudioFilterRead(float[] data, int ch)
    {
        // Unity's final mix carries each AudioSource buffer one callback later (measured: the
        // listener output equals the source output delayed by exactly one 1024-frame buffer).
        long sourceFrameNow = Source != null && Source.Player != null ? Source.Player.LastBlockStartFrame : -1;
        long fileFrame = previousSourceFrame;
        previousSourceFrame = sourceFrameNow;

        if (!capturing) return;
        int frames = data.Length / ch;
        if (fileFrame < 0 && framesWritten == 0) return;    // wait for the first audible block
        if (channels == 0) channels = ch;
        callbackFrames = frames;
        if (firstFileFrame < 0) firstFileFrame = fileFrame;
        if (expectedNextFileFrame >= 0 && fileFrame != expectedNextFileFrame) discontinuities++;
        expectedNextFileFrame = fileFrame + frames;

        int capacity = buffer.Length / ch;
        int n = Math.Min(frames, capacity - framesWritten);
        Array.Copy(data, 0, buffer, framesWritten * ch, n * ch);
        framesWritten += n;
        if (framesWritten >= capacity) capturing = false;
    }

    public void Stop() => capturing = false;

    public void SaveWav(string path, int sampleRate)
    {
        using (var w = new BinaryWriter(File.Create(path)))
        {
            int dataBytes = framesWritten * channels * 4;
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + dataBytes);
            w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            w.Write(System.Text.Encoding.ASCII.GetBytes("fmt ")); w.Write(16);
            w.Write((short)3); w.Write((short)channels); w.Write(sampleRate);
            w.Write(sampleRate * channels * 4); w.Write((short)(channels * 4)); w.Write((short)32);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(dataBytes);
            for (int i = 0; i < framesWritten * channels; i++) w.Write(buffer[i]);
        }
    }

    public int FramesWritten => framesWritten;
    public int Channels => channels;
}
