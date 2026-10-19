using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;

namespace KeyHoldMacro
{
    internal static class Native
    {
        public const uint WM_QUIT = 0x12, WM_TIMER = 0x113, WM_COMMAND = 0x8001, WM_SEQUENCE = 0x8002;
        public delegate IntPtr HookProc(int code, IntPtr wp, IntPtr lp);
        [StructLayout(LayoutKind.Sequential)]
        public struct KBD { public uint vk, scan, flags, time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd; public uint message; public UIntPtr wParam;
            public IntPtr lParam; public uint time; public POINT pt; public uint lPrivate;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx, dy; public uint data, flags, time; public UIntPtr extra;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort vk, scan; public uint flags, time; public UIntPtr extra;
        }
        [StructLayout(LayoutKind.Explicit)]
        public struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT { public uint type; public INPUTUNION u; }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int kind, HookProc proc, IntPtr mod, uint tid);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wp, IntPtr lp);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", SetLastError = true)] public static extern int GetMessage(out MSG msg, IntPtr hwnd, uint first, uint last);
        [DllImport("user32.dll")] public static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint first, uint last, uint remove);
        [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG msg);
        [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool PostThreadMessage(uint id, uint msg, UIntPtr wp, IntPtr lp);
        [DllImport("user32.dll", SetLastError = true)] public static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint ms, IntPtr proc);
        [DllImport("user32.dll")] public static extern bool KillTimer(IntPtr hwnd, UIntPtr id);
        [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint count, INPUT[] input, int size);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int max);
        public static Rectangle Desktop
        {
            get { return new Rectangle(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79)); }
        }
        public static bool OwnWindow(IntPtr hwnd)
        {
            uint pid; GetWindowThreadProcessId(hwnd, out pid);
            return pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        }
        public static string WindowTitle(IntPtr hwnd)
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder(256);
            GetWindowText(hwnd, text, text.Capacity); return text.ToString();
        }
    }

    internal sealed class WindowsKeyboard : IKeyboardOutput
    {
        public bool IsDown(int vk) { return Native.GetAsyncKeyState(vk) < 0; }
        private static void Send(int vk, bool up)
        {
            Native.INPUT input = new Native.INPUT(); input.type = 1;
            input.u.ki.vk = (ushort)vk;
            bool extended = (vk >= 33 && vk <= 40) || vk == 45 || vk == 46 || vk == 111;
            input.u.ki.flags = (up ? 0x0002u : 0u) | (extended ? 0x0001u : 0u);
            input.u.ki.extra = new UIntPtr(0x4B484D31);
            if (Native.SendInput(1, new Native.INPUT[] { input }, Marshal.SizeOf(typeof(Native.INPUT))) != 1)
                throw new Win32Exception(Marshal.GetLastWin32Error(), L.T("Windows 未接受键盘输入，请检查目标程序权限。"));
        }
        public void Press(int vk) { Send(vk, false); }
        public void Release(int vk) { Send(vk, true); }
    }

    internal sealed class WindowsMouse : IMouseOutput
    {
        private static Native.INPUT Mouse(int x, int y, uint flags)
        {
            Native.INPUT i = new Native.INPUT(); i.type = 0;
            i.u.mi.dx = x; i.u.mi.dy = y; i.u.mi.flags = flags;
            i.u.mi.extra = new UIntPtr(0x4B484D31); return i;
        }
        private static void Send(params Native.INPUT[] inputs)
        {
            if (Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Native.INPUT))) != inputs.Length)
                throw new Win32Exception(Marshal.GetLastWin32Error(), L.T("Windows 未完整接受鼠标输入。若目标程序以管理员运行，请以相同权限启动本程序。"));
        }
        public static void CheckCoordinates(int x, int y)
        {
            bool inside = false;
            foreach (System.Windows.Forms.Screen s in System.Windows.Forms.Screen.AllScreens)
                if (s.Bounds.Contains(x, y)) { inside = true; break; }
            if (!inside) throw new ArgumentException(L.F("目标坐标 ({0}, {1}) 不在当前任何显示器内，请重新取点。", x, y));
        }
        private static Native.INPUT MoveInputAt(int x, int y)
        {
            // Pixel-center mapping avoids rounding to the adjacent physical pixel.
            Rectangle r = Native.Desktop;
            if (!r.Contains(x, y)) throw new ArgumentException(L.T("显示器布局已改变，请重新取点。"));
            int nx = Math.Min(65535, (int)(((long)(x - r.Left) * 65536 + 32768) / r.Width));
            int ny = Math.Min(65535, (int)(((long)(y - r.Top) * 65536 + 32768) / r.Height));
            return Mouse(nx, ny, 0x8000 | 0x4000 | 0x0001 | 0x2000);
        }
        public void PressAt(int x, int y) { Send(MoveInputAt(x, y), Mouse(0, 0, 0x0002)); }
        public void MoveTo(int x, int y) { Send(MoveInputAt(x, y)); }
        public void Release() { Send(Mouse(0, 0, 0x0004)); }
    }
}
