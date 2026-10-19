// Optional desktop verification. Unlike UiTests, this moves the real cursor and
// drags only scrollbars in MainForm(true), which has no input hook or macro worker.
using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using KeyHoldMacro;

internal static class ScrollInteractionTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ComboInfo
    { public int Size; public Rect Item, Button; public int State; public IntPtr Combo, Edit, List; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ScrollInfo
    {
        public int Size; public Rect Bounds; public int Arrow, ThumbTop, ThumbBottom, Reserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public int[] States;
    }
    [DllImport("user32.dll")] private static extern bool GetComboBoxInfo(IntPtr window, ref ComboInfo info);
    [DllImport("user32.dll")] private static extern bool GetScrollBarInfo(IntPtr window, int id, ref ScrollInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect bounds);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wp, IntPtr lp);
    private static int samples;

    private static void Pump(int milliseconds)
    {
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(10); }
    }
    private static void Sample(IntPtr window, Point point, bool dark, string phase)
    {
        IntPtr dc = GetWindowDC(window);
        uint pixel;
        try { pixel = GetPixel(dc, point.X, point.Y); }
        finally { ReleaseDC(window, dc); }
        int r = (int)(pixel & 255), g = (int)((pixel >> 8) & 255), b = (int)((pixel >> 16) & 255);
        if (pixel == UInt32.MaxValue || (dark ? r > 100 || g > 100 || b > 100 : r < 180 || g < 180 || b < 180))
            throw new Exception(phase + " unexpected track color: " + pixel.ToString("X6"));
        Interlocked.Increment(ref samples);
    }
    private static void Exercise(MainForm form, IntPtr window, bool popup, bool dark)
    {
        ScrollInfo info = new ScrollInfo(); info.Size = Marshal.SizeOf(typeof(ScrollInfo));
        Rect bounds;
        if (!GetScrollBarInfo(window, popup ? -5 : -4, ref info) || !GetWindowRect(window, out bounds))
            throw new Exception("Cannot read scrollbar geometry");
        Point pixel = new Point(info.Bounds.Left - bounds.Left + 1, (info.Bounds.Top + info.Bounds.Bottom) / 2 - bounds.Top);
        Point start = new Point((info.Bounds.Left + info.Bounds.Right) / 2,
            info.Bounds.Top + (info.ThumbTop + info.ThumbBottom) / 2);
        Cursor.Position = start;
        for (int i = 0; i < 15; i++) { Pump(30); Sample(window, pixel, dark, "hover"); }
        Cursor.Position = form.PointToScreen(new Point(10, 10));
        for (int i = 0; i < 10; i++) { Pump(30); Sample(window, pixel, dark, "leave"); }
        Cursor.Position = start; Pump(40); SetForegroundWindow(form.Handle); Pump(40);
        if (Native.GetForegroundWindow() != form.Handle || Native.GetAsyncKeyState(1) < 0 || WindowFromPoint(start) != window)
            throw new Exception("Preview must be foreground and its scrollbar unobstructed; no drag was sent");
        WindowsMouse mouse = new WindowsMouse(); Exception failure = null;
        int distance = Math.Min(100, info.Bounds.Bottom - info.Arrow - start.Y - 2);
        if (distance < 20) throw new Exception("Not enough thumb travel to test a drag");
        Thread drag = new Thread(delegate()
        {
            try
            {
                Thread.Sleep(80);
                for (int i = 0; i < 20; i++)
                {
                    Cursor.Position = new Point(start.X, start.Y + distance * i / 19);
                    Thread.Sleep(20); Sample(window, pixel, dark, "drag");
                }
            }
            catch (Exception ex) { failure = ex; }
            finally { mouse.Release(); }
        });
        drag.IsBackground = true;
        try
        {
            mouse.PressAt(start.X, start.Y); drag.Start();
            while (drag.IsAlive) { Application.DoEvents(); Thread.Sleep(10); }
        }
        finally { mouse.Release(); }
        Pump(100); if (failure != null) throw failure;
        Sample(window, pixel, dark, "release");
    }

    [STAThread]
    public static int Main()
    {
        Point original = Cursor.Position; IntPtr previous = Native.GetForegroundWindow();
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using (MainForm form = new MainForm(true))
            {
                form.Text = "KeyHold · 滚动条交互验证（无宏监听）";
                form.Show(); SetForegroundWindow(form.Handle); Pump(100);
                DataGridView grid = (DataGridView)typeof(MainForm).GetField("grid", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
                foreach (bool dark in new bool[] { true, false })
                {
                    form.PreviewTheme(dark); grid.FirstDisplayedScrollingRowIndex = 0; Pump(100);
                    VScrollBar bar = null;
                    foreach (Control child in grid.Controls) if (child is VScrollBar) bar = (VScrollBar)child;
                    if (bar == null) throw new Exception("Grid scrollbar not found");
                    Exercise(form, bar.Handle, false, dark);
                    if (grid.FirstDisplayedScrollingRowIndex == 0) throw new Exception("Grid did not scroll during drag");
                    grid.FirstDisplayedScrollingRowIndex = 0;
                    grid.CurrentCell = grid.Rows[0].Cells["key"]; grid.BeginEdit(true);
                    ComboBox combo = (ComboBox)grid.EditingControl;
                    combo.DroppedDown = true; Pump(100);
                    ComboInfo info = new ComboInfo(); info.Size = Marshal.SizeOf(typeof(ComboInfo));
                    if (!GetComboBoxInfo(combo.Handle, ref info)) throw new Exception("Dropdown list not found");
                    Exercise(form, info.List, true, dark);
                    if (SendMessage(info.List, 0x18E, IntPtr.Zero, IntPtr.Zero).ToInt32() == 0)
                        throw new Exception("Dropdown did not scroll during drag");
                    combo.DroppedDown = false; grid.EndEdit();
                    Console.WriteLine("PASS: " + (dark ? "dark" : "light") + " grid and dropdown hover/leave/drag/release");
                }
            }
            Console.WriteLine("PASS: " + samples + " live desktop color samples; no macro worker or keyboard hook.");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL: " + ex.Message); return 1; }
        finally { Cursor.Position = original; if (previous != IntPtr.Zero) SetForegroundWindow(previous); }
    }
}
