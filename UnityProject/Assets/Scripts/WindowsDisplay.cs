// Windows' display scaling (Settings > System > Display > Scale) for the app's own window, read
// from Windows itself. It follows the monitor the window is on, and changes while the app runs:
// a new Scale setting, dragging the window to another monitor, or a Remote Desktop session
// reconnected from a different device. Elsewhere (the editor, other platforms) Unity's
// Screen.dpi is used.

using System;
using System.Runtime.InteropServices;
using UnityEngine;

public static class WindowsDisplay
{
    /// <summary>Dots per inch of the app's window: 96 = 100%, 144 = 150%, 288 = 300%.</summary>
    public static float Dpi
    {
        get
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            uint dpi = 0;
            try
            {
                if (window == IntPtr.Zero) window = FindOwnWindow();
                if (window != IntPtr.Zero) dpi = GetDpiForWindow(window);
                if (dpi == 0) window = IntPtr.Zero;   // window replaced (e.g. fullscreen switch): find it again next time
            }
            catch (Exception) { }   // Windows older than 10 (1607)
            if (dpi > 0) return dpi;
#endif
            return Screen.dpi > 0 ? Screen.dpi : 96f;
        }
    }

    /// <summary>1 at 100% scaling, 3 at 300%.</summary>
    public static float Scale => Dpi / 96f;

    /// <summary>Where Dpi comes from, for test logs.</summary>
    public static string Source
    {
        get
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            float d = Dpi;
            if (window != IntPtr.Zero) return "Windows (window DPI " + d + ")";
#endif
            return "Unity Screen.dpi " + Screen.dpi;
        }
    }

    /// <summary>True when the app is shown through Remote Desktop, which limits the frame rate.</summary>
    public static bool RemoteSession
    {
        get
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            try { return GetSystemMetrics(0x1000) != 0; } catch (Exception) { }   // SM_REMOTESESSION
#endif
            return false;
        }
    }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    static IntPtr window;

    // The player's window has the class "UnityWndClass"; take the one belonging to this process.
    static IntPtr FindOwnWindow()
    {
        uint me = GetCurrentProcessId();
        IntPtr h = IntPtr.Zero;
        while ((h = FindWindowEx(IntPtr.Zero, h, "UnityWndClass", null)) != IntPtr.Zero)
        {
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == me) return h;
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
#endif
}
