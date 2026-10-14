// Reader for the CAF (Core Audio Format) files the decoder writes as NAME.atmos.audio.
//
// CAF is Apple's container: an 8-byte file header, then chunks of [4-char type][int64 size].
// 'desc' describes the PCM format, 'data' holds the interleaved samples (after a 4-byte edit
// count). harletty (like truehdd) writes 24-bit big-endian integer PCM, one channel per DAMF
// element. For DD+ it also writes the stream's 5.1 core ahead of the elements, so the reader can
// be told to use only the last channels of each frame (see DamfFile.OpenAudio).
// All CAF integers are big-endian.
//
// Reads are positioned by frame index, so the caller can stream sequentially or seek.
// A "frame" is one sample for every channel.

using System;
using System.IO;
using System.Text;

namespace AtmosEngine
{
    public sealed class CafReader : IDisposable
    {
        public string Path { get; }
        public double SampleRate { get; }
        public int Channels { get; }       // channels read: the last Channels of FileChannels
        public int FileChannels { get; }   // channels stored in the file
        public int BitsPerChannel { get; }
        public bool IsFloat { get; }
        public bool IsLittleEndian { get; }
        public int BytesPerFrame { get; }  // in the file (all FileChannels)
        public long DataOffset { get; }   // file offset of frame 0
        public long FrameCount { get; }

        readonly FileStream stream;
        byte[] buffer = new byte[0];

        // kCAFLinearPCMFormatFlagIsFloat / IsLittleEndian
        const uint FlagIsFloat = 1, FlagIsLittleEndian = 2;

        /// <param name="lastChannels">read only the last this many channels of each frame (0 = all)</param>
        public CafReader(string path, int lastChannels = 0)
        {
            Path = path;
            // A large buffer suits sequential streaming of a multi-GB file.
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            try
            {
                var head = ReadExactly(8);
                if (Encoding.ASCII.GetString(head, 0, 4) != "caff")
                    throw new InvalidDataException("not a CAF file (no 'caff' header): " + path);

                bool haveDesc = false;
                long dataSize = -1;
                while (true)
                {
                    if (stream.Position + 12 > stream.Length)
                        throw new InvalidDataException("CAF file has no 'data' chunk");
                    var ch = ReadExactly(12);
                    string type = Encoding.ASCII.GetString(ch, 0, 4);
                    long size = BigEndianInt64(ch, 4);

                    if (type == "desc")
                    {
                        var d = ReadExactly(32);
                        SampleRate = BitConverter.Int64BitsToDouble(BigEndianInt64(d, 0));
                        string formatId = Encoding.ASCII.GetString(d, 8, 4);
                        uint flags = BigEndianUInt32(d, 12);
                        uint bytesPerPacket = BigEndianUInt32(d, 16);
                        uint framesPerPacket = BigEndianUInt32(d, 20);
                        FileChannels = (int)BigEndianUInt32(d, 24);
                        BitsPerChannel = (int)BigEndianUInt32(d, 28);
                        IsFloat = (flags & FlagIsFloat) != 0;
                        IsLittleEndian = (flags & FlagIsLittleEndian) != 0;
                        BytesPerFrame = (int)bytesPerPacket;

                        if (formatId != "lpcm") throw new InvalidDataException("CAF format is '" + formatId + "', expected 'lpcm'");
                        if (framesPerPacket != 1) throw new InvalidDataException("CAF framesPerPacket is " + framesPerPacket);
                        if (FileChannels <= 0 || BytesPerFrame != FileChannels * (BitsPerChannel / 8))
                            throw new InvalidDataException("CAF bytesPerPacket doesn't match channels x bits");
                        bool ok = IsFloat ? (BitsPerChannel == 32 || BitsPerChannel == 64)
                                          : (BitsPerChannel == 16 || BitsPerChannel == 24 || BitsPerChannel == 32);
                        if (!ok) throw new InvalidDataException("unsupported CAF sample format: " + BitsPerChannel + " bits, float=" + IsFloat);
                        haveDesc = true;
                        if (size > 32) stream.Seek(size - 32, SeekOrigin.Current);
                    }
                    else if (type == "data")
                    {
                        if (!haveDesc) throw new InvalidDataException("CAF 'data' chunk before 'desc'");
                        ReadExactly(4); // edit count
                        DataOffset = stream.Position;
                        // size -1 means "data runs to the end of the file"
                        dataSize = size < 0 ? stream.Length - DataOffset : size - 4;
                        break;
                    }
                    else
                    {
                        stream.Seek(size, SeekOrigin.Current);
                    }
                }
                FrameCount = dataSize / BytesPerFrame;
                if (lastChannels > FileChannels)
                    throw new InvalidDataException("CAF has " + FileChannels + " channels, fewer than the " + lastChannels + " asked for");
                Channels = lastChannels > 0 ? lastChannels : FileChannels;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        public double DurationSeconds => FrameCount / SampleRate;

        /// <summary>Reads up to frameCount frames starting at startFrame as interleaved floats in
        /// [-1, 1). Returns the number of frames read (fewer at the end of the file).</summary>
        public int ReadFloat(long startFrame, float[] dest, int frameCount)
        {
            int frames = FillBuffer(startFrame, frameCount);
            int skip = FileChannels - Channels;   // channels ahead of the ones read, in each frame
            var b = buffer;
            if (!IsFloat && BitsPerChannel == 24 && !IsLittleEndian)
            {
                const float scale = 1f / 8388608f; // 2^23
                // Shift the 3 bytes into the top of an int, then arithmetic-shift back down so
                // the sign bit is extended.
                for (int f = 0, i = 0, j = skip * 3; f < frames; f++, j += skip * 3)
                    for (int c = 0; c < Channels; c++, i++, j += 3)
                        dest[i] = (((b[j] << 24) | (b[j + 1] << 16) | (b[j + 2] << 8)) >> 8) * scale;
            }
            else
            {
                for (int f = 0, i = 0; f < frames; f++)
                    for (int c = 0; c < Channels; c++, i++)
                        dest[i] = (float)SampleAsDouble(b, f * FileChannels + skip + c);
            }
            return frames;
        }

        /// <summary>Reads up to frameCount frames as interleaved doubles in [-1, 1), exactly
        /// (no float rounding). For measurement.</summary>
        public int ReadDouble(long startFrame, double[] dest, int frameCount)
        {
            int frames = FillBuffer(startFrame, frameCount);
            int skip = FileChannels - Channels;
            for (int f = 0, i = 0; f < frames; f++)
                for (int c = 0; c < Channels; c++, i++)
                    dest[i] = SampleAsDouble(buffer, f * FileChannels + skip + c);
            return frames;
        }

        int FillBuffer(long startFrame, int frameCount)
        {
            if (startFrame < 0 || startFrame >= FrameCount || frameCount <= 0) return 0;
            int frames = (int)Math.Min(frameCount, FrameCount - startFrame);
            int bytes = frames * BytesPerFrame;
            if (buffer.Length < bytes) buffer = new byte[bytes];
            long pos = DataOffset + startFrame * BytesPerFrame;
            if (stream.Position != pos) stream.Seek(pos, SeekOrigin.Begin);
            int got = 0;
            while (got < bytes)
            {
                int r = stream.Read(buffer, got, bytes - got);
                if (r <= 0) throw new EndOfStreamException("CAF data ended early at frame " + (startFrame + got / BytesPerFrame));
                got += r;
            }
            return frames;
        }

        double SampleAsDouble(byte[] b, int index)
        {
            int bytesPer = BitsPerChannel / 8;
            int j = index * bytesPer;
            if (IsFloat)
            {
                if (BitsPerChannel == 32)
                {
                    int bits = IsLittleEndian ? BitConverter.ToInt32(b, j) : (int)BigEndianUInt32(b, j);
                    return BitConverter.Int32BitsToSingle(bits);
                }
                long bits64 = IsLittleEndian ? BitConverter.ToInt64(b, j) : BigEndianInt64(b, j);
                return BitConverter.Int64BitsToDouble(bits64);
            }
            int v;
            switch (BitsPerChannel)
            {
                case 16:
                    v = IsLittleEndian ? (short)(b[j] | (b[j + 1] << 8)) : (short)((b[j] << 8) | b[j + 1]);
                    return v / 32768.0;
                case 24:
                    v = IsLittleEndian ? ((b[j + 2] << 24) | (b[j + 1] << 16) | (b[j] << 8)) >> 8
                                       : ((b[j] << 24) | (b[j + 1] << 16) | (b[j + 2] << 8)) >> 8;
                    return v / 8388608.0;
                default:
                    v = IsLittleEndian ? BitConverter.ToInt32(b, j) : (int)BigEndianUInt32(b, j);
                    return v / 2147483648.0;
            }
        }

        byte[] ReadExactly(int n)
        {
            var b = new byte[n];
            int got = 0;
            while (got < n)
            {
                int r = stream.Read(b, got, n - got);
                if (r <= 0) throw new EndOfStreamException("unexpected end of CAF header");
                got += r;
            }
            return b;
        }

        static uint BigEndianUInt32(byte[] b, int o) =>
            (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);

        static long BigEndianInt64(byte[] b, int o) =>
            ((long)BigEndianUInt32(b, o) << 32) | BigEndianUInt32(b, o + 4);

        public void Dispose() => stream.Dispose();
    }
}
