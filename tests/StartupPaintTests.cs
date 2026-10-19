// Optional desktop capture; displays preview windows but installs no hooks and
// sends no keyboard/mouse input. Run on an unobstructed desktop.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using KeyHoldMacro;

internal static class StartupPaintTests
{
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, int flags);
    private sealed class Frame
    { public Bitmap Image; public long Milliseconds; public double WhiteFraction; }
    private static int framesChecked;

    private static void Export(List<Frame> frames, string path)
    {
        if (frames.Count == 0) return;
        using (Bitmap sheet = new Bitmap(1280, 720))
        using (Graphics g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.Black);
            int count = Math.Min(9, frames.Count);
            for (int i = 0; i < count; i++)
            {
                Frame frame = frames[i * (frames.Count - 1) / Math.Max(1, count - 1)];
                int x = i % 3 * 426, y = i / 3 * 240;
                g.DrawImage(frame.Image, new Rectangle(x, y, 426, 240));
                g.FillRectangle(Brushes.Black, x, y, 160, 20);
                g.DrawString(frame.Milliseconds + "ms · " + frame.WhiteFraction.ToString("P1"),
                    SystemFonts.DefaultFont, Brushes.Yellow, x + 3, y + 2);
            }
            sheet.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
    }
    private static void RunCase(FluentWindow form, bool dark, bool restore, string exportPath)
    {
        using (form)
        using (Form background = new Form())
        {
            form.PreviewTheme(dark); form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(Screen.PrimaryScreen.WorkingArea.Left + 40, Screen.PrimaryScreen.WorkingArea.Top + 40);
            form.TopMost = true;
            IntPtr handle = form.Handle;
            Rectangle area = form.Bounds;
            // A solid test backdrop keeps wallpaper/other apps from being
            // mistaken for a white flash during Windows' native show animation.
            background.FormBorderStyle = FormBorderStyle.None; background.ShowInTaskbar = false;
            background.TopMost = true;
            background.StartPosition = FormStartPosition.Manual; background.Bounds = area;
            background.BackColor = form.Palette.Background;
            background.Show(); SetForegroundWindow(background.Handle); background.Refresh();
            Application.DoEvents(); Thread.Sleep(80);
            if (restore)
            {
                form.Show(); Application.DoEvents();
                MainForm main = form as MainForm;
                if (main != null)
                {
                    DataGridView grid = (DataGridView)typeof(MainForm).GetField("grid", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(main);
                    grid.CurrentCell = grid.Rows[0].Cells["key"]; grid.BeginEdit(true);
                    ((ComboBox)grid.EditingControl).SelectedItem = "X";
                    grid.CommitEdit(DataGridViewDataErrorContexts.Commit); grid.EndEdit();
                }
                form.Enabled = false; form.WindowState = FormWindowState.Minimized;
                // Exercise occlusion/restore with a borderless fullscreen cover.
                // This does not claim to simulate an exclusive display-mode switch.
                background.Bounds = Screen.FromControl(form).Bounds;
                background.BringToFront(); SetForegroundWindow(background.Handle); background.Refresh();
                Application.DoEvents(); Thread.Sleep(100);
            }
            List<FluentButton> buttons = new List<FluentButton>(); CollectButtons(form, buttons);
            List<Frame> frames = new List<Frame>(); Exception captureFailure = null;
            ManualResetEvent stop = new ManualResetEvent(false);
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            Thread capture = new Thread(delegate()
            {
                try
                {
                    while (!stop.WaitOne(8))
                    {
                        if (!IsWindowVisible(handle)) continue;
                        if (GetAncestor(WindowFromPoint(new Point(area.Left + area.Width / 2, area.Top + area.Height / 2)), 2) != handle)
                            continue; // Never pass by sampling an unrelated app in front.
                        Bitmap bitmap = new Bitmap(area.Width, area.Height);
                        try
                        {
                            using (Graphics g = Graphics.FromImage(bitmap)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
                            int white = 0, total = 0;
                            for (int y = 60; y < bitmap.Height - 30; y += 12)
                                for (int x = 30; x < bitmap.Width - 30; x += 12)
                                {
                                    Color color = bitmap.GetPixel(x, y);
                                    if (color.R > 190 && color.G > 190 && color.B > 190) white++;
                                    total++;
                                }
                            frames.Add(new Frame { Image = bitmap, Milliseconds = clock.ElapsedMilliseconds,
                                WhiteFraction = (double)white / total });
                            bitmap = null;
                        }
                        finally { if (bitmap != null) bitmap.Dispose(); }
                    }
                }
                catch (Exception ex) { captureFailure = ex; }
            });
            capture.IsBackground = true; capture.Start();
            try
            {
                form.Enabled = true; form.WindowState = FormWindowState.Normal;
                form.Show(); form.BringToFront(); SetForegroundWindow(handle);
                int step = -1;
                while (clock.ElapsedMilliseconds < (restore ? 1000 : 650))
                {
                    int next = (int)(clock.ElapsedMilliseconds / 40);
                    if (restore && next != step && buttons.Count > 0)
                    {
                        if (step >= 0) Hover(buttons[step % buttons.Count], false);
                        Hover(buttons[next % buttons.Count], true); step = next;
                    }
                    Application.DoEvents(); Thread.Sleep(5);
                }
            }
            finally { stop.Set(); capture.Join(); stop.Dispose(); }
            try
            {
                if (captureFailure != null) throw captureFailure;
                if (frames.Count < 5) throw new Exception("Too few desktop frames to verify startup");
                double peak = 0;
                foreach (Frame frame in frames) peak = Math.Max(peak, frame.WhiteFraction);
                if (exportPath != null) Export(frames, exportPath);
                Console.WriteLine(form.GetType().Name + " " + (dark ? "dark" : "light") + (restore ? " restore/hover" : " startup") + ": " + frames.Count + " frames, peak white=" + peak.ToString("P1"));
                if (dark && peak > .05) throw new Exception("Opening/restoring exposed a partially painted white surface");
                framesChecked += frames.Count;
            }
            finally { foreach (Frame frame in frames) frame.Image.Dispose(); }
        }
    }

    private static void CollectButtons(Control control, List<FluentButton> buttons)
    {
        foreach (Control child in control.Controls)
        {
            if (child is FluentButton) buttons.Add((FluentButton)child);
            CollectButtons(child, buttons);
        }
    }
    private static void Hover(FluentButton button, bool enter)
    {
        typeof(FluentButton).GetMethod(enter ? "OnMouseEnter" : "OnMouseLeave", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(button, new object[] { EventArgs.Empty });
    }

    [STAThread]
    public static int Main(string[] args)
    {
        IntPtr previous = Native.GetForegroundWindow();
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string folder = args.Length > 0 ? Path.GetFullPath(args[0]) : null;
            if (folder != null) Directory.CreateDirectory(folder);
            foreach (bool restore in new bool[] { false, true })
            foreach (bool dark in new bool[] { true, false })
                for (int run = 0; run < 3; run++)
                {
                    string suffix = (restore ? "restore-" : "startup-") + (dark ? "dark" : "light") + "-" + run + ".png";
                    RunCase(new MainForm(true), dark, restore, folder == null ? null : Path.Combine(folder, "main-" + suffix));
                    Macro macro = new Macro { Key = "X" }; macro.BeforeActions.Add(new MacroAction { Key = "1" });
                    RunCase(new MacroEditor(macro, delegate(Action<int, int> point) { }, delegate { }), dark, restore,
                        folder == null ? null : Path.Combine(folder, "editor-" + suffix));
                }
            Console.WriteLine("PASS: 24 preview openings/restores, " + framesChecked + " desktop frames; no hooks or input injection.");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL: " + ex.Message); return 1; }
        finally { if (previous != IntPtr.Zero) SetForegroundWindow(previous); }
    }
}
