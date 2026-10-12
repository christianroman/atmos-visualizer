// P/Invoke bindings for ear_shim.dll (native/include/ear_shim.h), the C wrapper over libear.
//
// P/Invoke is C#'s way of calling a plain C function in a DLL - roughly what Swift does when
// it calls a C function through a bridging header. The [DllImport] declarations below must
// match the C signatures exactly; the wrapper classes turn handles and error codes into
// ordinary objects and exceptions.

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace AtmosEngine
{
    public sealed class EarShimException : Exception
    {
        public EarShimException(string message) : base(message) { }
    }

    internal static class EarShimNative
    {
        const string Dll = "ear_shim";

        [DllImport(Dll)] internal static extern IntPtr es_version();
        [DllImport(Dll)] internal static extern IntPtr es_last_error();

        [DllImport(Dll)] internal static extern IntPtr es_layout_bs2051([MarshalAs(UnmanagedType.LPStr)] string name);

        [DllImport(Dll)]
        internal static extern IntPtr es_layout_custom(
            [MarshalAs(UnmanagedType.LPStr)] string layoutName, int count,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string[] names,
            double[] azimuths, double[] elevations, int[] isLfe);

        [DllImport(Dll)] internal static extern void es_layout_free(IntPtr layout);
        [DllImport(Dll)] internal static extern int es_layout_channel_count(IntPtr layout);
        [DllImport(Dll)] internal static extern int es_layout_channel_name(IntPtr layout, int index, byte[] buf, int bufSize);
        [DllImport(Dll)] internal static extern int es_layout_channel_polar(IntPtr layout, int index, out double azimuth, out double elevation, out double distance);
        [DllImport(Dll)] internal static extern int es_layout_channel_is_lfe(IntPtr layout, int index);

        [DllImport(Dll)] internal static extern IntPtr es_objects_new(IntPtr layout);
        [DllImport(Dll)] internal static extern void es_objects_free(IntPtr calc);

        [DllImport(Dll)]
        internal static extern int es_objects_gains_polar(IntPtr calc, double azimuth, double elevation, double distance,
            double width, double height, double depth, double gain, [In, Out] double[] outGains);

        [DllImport(Dll)]
        internal static extern int es_objects_gains_cart_via_polar(IntPtr calc, double x, double y, double z,
            double width, double height, double depth, double gain, [In, Out] double[] outGains);

        [DllImport(Dll)] internal static extern int es_point_cart_to_polar(double x, double y, double z, out double azimuth, out double elevation, out double distance);

        [DllImport(Dll)] internal static extern IntPtr es_allo_new(IntPtr layout, double[] positions);
        [DllImport(Dll)] internal static extern void es_allo_free(IntPtr calc);

        [DllImport(Dll)]
        internal static extern int es_allo_gains(IntPtr calc, double x, double y, double z,
            double width, double height, double depth, double gain, [In, Out] double[] outGains);

        [DllImport(Dll)] internal static extern IntPtr es_direct_new(IntPtr layout);
        [DllImport(Dll)] internal static extern void es_direct_free(IntPtr calc);

        [DllImport(Dll)]
        internal static extern int es_direct_gains(IntPtr calc, [MarshalAs(UnmanagedType.LPStr)] string speakerLabel,
            double azimuth, double elevation, double distance, double lfeLowPassHz, [In, Out] double[] outGains);

        internal static string LastError() => Marshal.PtrToStringAnsi(es_last_error()) ?? "";

        internal static int Check(int rc)
        {
            if (rc < 0) throw new EarShimException(LastError());
            return rc;
        }

        internal static IntPtr Check(IntPtr handle)
        {
            if (handle == IntPtr.Zero) throw new EarShimException(LastError());
            return handle;
        }
    }

    public static class EarShim
    {
        public static string Version => Marshal.PtrToStringAnsi(EarShimNative.es_version());

        /// <summary>BS.2127 section 10 conversion of a Cartesian position to polar
        /// (azimuth anticlockwise, positive = left).</summary>
        public static void CartToPolar(double x, double y, double z, out double azimuth, out double elevation, out double distance)
        {
            EarShimNative.Check(EarShimNative.es_point_cart_to_polar(x, y, z, out azimuth, out elevation, out distance));
        }
    }

    /// <summary>A loudspeaker layout: a BS.2051 one by name, or a custom one.</summary>
    public sealed class SpeakerLayout : IDisposable
    {
        internal IntPtr Handle { get; private set; }
        public string Name { get; }
        public string[] ChannelNames { get; }
        public double[] Azimuths { get; }
        public double[] Elevations { get; }
        public bool[] IsLfe { get; }
        public int ChannelCount => ChannelNames.Length;

        SpeakerLayout(string name, IntPtr handle)
        {
            Name = name;
            Handle = handle;
            int n = EarShimNative.Check(EarShimNative.es_layout_channel_count(handle));
            ChannelNames = new string[n];
            Azimuths = new double[n];
            Elevations = new double[n];
            IsLfe = new bool[n];
            var buf = new byte[64];
            for (int i = 0; i < n; i++)
            {
                EarShimNative.Check(EarShimNative.es_layout_channel_name(handle, i, buf, buf.Length));
                ChannelNames[i] = Encoding.UTF8.GetString(buf, 0, Array.IndexOf(buf, (byte)0));
                EarShimNative.Check(EarShimNative.es_layout_channel_polar(handle, i, out Azimuths[i], out Elevations[i], out _));
                IsLfe[i] = EarShimNative.Check(EarShimNative.es_layout_channel_is_lfe(handle, i)) == 1;
            }
        }

        public static SpeakerLayout Bs2051(string name) =>
            new SpeakerLayout(name, EarShimNative.Check(EarShimNative.es_layout_bs2051(name)));

        public static SpeakerLayout Custom(string name, string[] names, double[] azimuths, double[] elevations, bool[] isLfe)
        {
            var lfe = new int[names.Length];
            for (int i = 0; i < lfe.Length; i++) lfe[i] = isLfe[i] ? 1 : 0;
            return new SpeakerLayout(name, EarShimNative.Check(
                EarShimNative.es_layout_custom(name, names.Length, names, azimuths, elevations, lfe)));
        }

        public int IndexOf(string channelName) => Array.IndexOf(ChannelNames, channelName);

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) EarShimNative.es_layout_free(Handle);
            Handle = IntPtr.Zero;
        }
    }

    /// <summary>libear's object gain calculator for one layout. Not thread-safe.</summary>
    public sealed class ObjectGainCalculator : IDisposable
    {
        IntPtr handle;
        public int ChannelCount { get; }

        public ObjectGainCalculator(SpeakerLayout layout)
        {
            handle = EarShimNative.Check(EarShimNative.es_objects_new(layout.Handle));
            ChannelCount = layout.ChannelCount;
        }

        /// <summary>Gains for a DAMF/ADM Cartesian position, via BS.2127 section 10 conversion to
        /// polar and libear's polar panner. size is applied as width = height = depth.</summary>
        public void CartesianViaPolar(double x, double y, double z, double size, double linearGain, double[] outGains)
        {
            EarShimNative.Check(EarShimNative.es_objects_gains_cart_via_polar(handle, x, y, z, size, size, size, linearGain, outGains));
        }

        public void Polar(double azimuth, double elevation, double distance, double width, double height, double depth,
            double linearGain, double[] outGains)
        {
            EarShimNative.Check(EarShimNative.es_objects_gains_polar(handle, azimuth, elevation, distance, width, height, depth, linearGain, outGains));
        }

        public void Dispose()
        {
            if (handle != IntPtr.Zero) EarShimNative.es_objects_free(handle);
            handle = IntPtr.Zero;
        }
    }

    /// <summary>BS.2127's room-relative ("allocentric") panner for Cartesian objects such as
    /// DAMF's - the path libear lacks, ported from the EBU reference renderer into ear_shim.
    /// Not thread-safe.</summary>
    public sealed class AlloGainCalculator : IDisposable
    {
        IntPtr handle;
        public int ChannelCount { get; }

        /// <param name="positions">x, y, z room-box position for every layout channel (LFE ignored)</param>
        public AlloGainCalculator(SpeakerLayout layout, double[] positions)
        {
            if (positions.Length != layout.ChannelCount * 3)
                throw new ArgumentException("need 3 coordinates per layout channel");
            handle = EarShimNative.Check(EarShimNative.es_allo_new(layout.Handle, positions));
            ChannelCount = layout.ChannelCount;
        }

        /// <summary>Gains for an object at room position x, y, z with the given size (applied as
        /// width = height = depth; 0 = point source) and linear gain.</summary>
        public void Gains(double x, double y, double z, double size, double linearGain, double[] outGains)
        {
            EarShimNative.Check(EarShimNative.es_allo_gains(handle, x, y, z, size, size, size, linearGain, outGains));
        }

        public void Dispose()
        {
            if (handle != IntPtr.Zero) EarShimNative.es_allo_free(handle);
            handle = IntPtr.Zero;
        }
    }

    /// <summary>libear's DirectSpeakers gain calculator (bed channels such as the LFE).</summary>
    public sealed class DirectGainCalculator : IDisposable
    {
        IntPtr handle;

        public DirectGainCalculator(SpeakerLayout layout)
        {
            handle = EarShimNative.Check(EarShimNative.es_direct_new(layout.Handle));
        }

        public void Gains(string speakerLabel, double azimuth, double elevation, double lfeLowPassHz, double[] outGains)
        {
            EarShimNative.Check(EarShimNative.es_direct_gains(handle, speakerLabel, azimuth, elevation, 1.0, lfeLowPassHz, outGains));
        }

        public void Dispose()
        {
            if (handle != IntPtr.Zero) EarShimNative.es_direct_free(handle);
            handle = IntPtr.Zero;
        }
    }
}
