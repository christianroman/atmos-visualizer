// "Open file" dialog. Unity has no runtime file picker, so on Windows this calls the system's
// own (comdlg32 GetOpenFileNameW), with multi-select; in the editor it uses the editor's.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class NativeFileDialog
{
    /// <param name="filter">pairs of description and pattern, e.g. { "Atmos master", "*.atmos" }</param>
    /// <returns>the chosen paths (empty if cancelled)</returns>
    public static string[] OpenFiles(string title, string initialDir, params string[] filter)
    {
#if UNITY_EDITOR
        string ext = filter.Length >= 2 ? filter[1].Replace("*.", "").Replace(";", ",") : "";
        string path = UnityEditor.EditorUtility.OpenFilePanel(title, initialDir ?? "", ext);
        return string.IsNullOrEmpty(path) ? new string[0] : new[] { path };
#elif UNITY_STANDALONE_WIN
        const int chars = 32768;
        IntPtr buffer = Marshal.AllocHGlobal(chars * 2);
        try
        {
            Marshal.Copy(new byte[chars * 2], 0, buffer, chars * 2);
            var ofn = new OpenFileName();
            ofn.lStructSize = Marshal.SizeOf(ofn);
            ofn.hwndOwner = GetActiveWindow();
            ofn.lpstrFilter = string.Join("\0", filter) + "\0\0";
            ofn.lpstrFile = buffer;
            ofn.nMaxFile = chars;
            ofn.lpstrInitialDir = initialDir;
            ofn.lpstrTitle = title;
            // explorer style, multi-select, file and path must exist, don't change the working dir
            ofn.Flags = 0x00080000 | 0x00000200 | 0x00001000 | 0x00000800 | 0x00000008;
            if (!GetOpenFileNameW(ofn)) return new string[0];

            // single: "C:\dir\file\0\0"; several: "C:\dir\0file1\0file2\0\0"
            var parts = new List<string>();
            int offset = 0;
            while (true)
            {
                string s = Marshal.PtrToStringUni(IntPtr.Add(buffer, offset * 2));
                if (string.IsNullOrEmpty(s)) break;
                parts.Add(s);
                offset += s.Length + 1;
            }
            if (parts.Count <= 1) return parts.ToArray();
            var result = new string[parts.Count - 1];
            for (int i = 1; i < parts.Count; i++) result[i - 1] = System.IO.Path.Combine(parts[0], parts[i]);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
#else
        return new string[0];
#endif
    }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    class OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public string lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;          // our own buffer: multi-select results contain NULs
        public int nMaxFile;
        public string lpstrFileTitle;
        public int nMaxFileTitle;
        public string lpstrInitialDir;
        public string lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetOpenFileNameW([In, Out] OpenFileName ofn);

    [DllImport("user32.dll")]
    static extern IntPtr GetActiveWindow();
#endif
}
