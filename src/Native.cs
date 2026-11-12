using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace KeyHoldMacro
{
    // UI-only Win32 calls. The Rust DLL owns the hook and all input output.
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int x, y; }

        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int max);

        public static Rectangle Desktop
        { get { return new Rectangle(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79)); } }

        public static string WindowTitle(IntPtr window)
        {
            StringBuilder text = new StringBuilder(256);
            GetWindowText(window, text, text.Capacity);
            return text.ToString();
        }
    }
}
