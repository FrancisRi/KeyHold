using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using KeyHoldMacro;

internal static class UiTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ComboInfo
    { public int Size; public NativeRect Item, Button; public int State; public IntPtr Combo, Edit, List; }
    [DllImport("user32.dll")]
    private static extern bool GetComboBoxInfo(IntPtr window, ref ComboInfo info);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenThemeData(IntPtr window, string classes);
    [DllImport("uxtheme.dll")]
    private static extern int CloseThemeData(IntPtr theme);
    [DllImport("uxtheme.dll")]
    private static extern int DrawThemeBackground(IntPtr theme, IntPtr dc, int part, int state, ref NativeRect bounds, IntPtr clip);
    private static int checks;
    private static void Check(bool value, string message)
    { if (!value) throw new Exception("FAIL: " + message); checks++; }
    private static T Field<T>(object target, string name)
    { return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }
    private static void Call(object target, string name, params object[] args)
    { target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args); }
    private static void Render(Form form, string path)
    {
        form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-3000, -3000);
        form.Show(); Application.DoEvents(); form.Refresh();
        using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
        { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png); }
    }

    private static void CheckHeadings(Control root)
    {
        foreach (Control child in root.Controls)
        {
            if (child is Label && child.Font.Size >= 20)
            {
                Check(child.Height >= child.PreferredSize.Height, "heading fits actual font/DPI: " + child.Text);
                Font original = child.Font;
                using (Font larger = new Font(original.FontFamily, original.Size * 1.2F, original.Style))
                {
                    child.Font = larger; child.Parent.PerformLayout(); root.PerformLayout();
                    Check(child.Height >= child.PreferredSize.Height, "heading grows when font metrics increase: " + child.Text);
                    child.Font = original;
                }
            }
            CheckHeadings(child);
        }
    }

    private static void ShowPreview(Form form)
    {
        form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-3000, -3000); form.Show(); Application.DoEvents();
    }

    private static void CheckPixel(Control control, Point point, Color expected, string message)
    {
        using (Bitmap bitmap = new Bitmap(control.Width, control.Height))
        {
            control.DrawToBitmap(bitmap, control.ClientRectangle);
            Check(bitmap.GetPixel(point.X, point.Y).ToArgb() == expected.ToArgb(), message);
        }
    }

    private static void CheckThemes(MainForm main, DataGridView grid)
    {
        foreach (bool dark in new bool[] { true, false })
        {
            main.PreviewTheme(dark); main.Refresh(); Application.DoEvents();
            CheckButtonBackground(main);
            Rectangle cell = grid.GetCellDisplayRectangle(grid.Columns["enabled"].Index, 0, false);
            Point center = new Point(cell.Left + cell.Width / 2, cell.Top + cell.Height / 2);
            grid.Rows[0].Cells["enabled"].Value = false;
            CheckPixel(grid, center, main.Palette.Subtle, "unchecked macro box uses current theme rather than white");
            grid.Rows[0].Cells["enabled"].Value = true;
            CheckPixel(grid, new Point(center.X - 5, center.Y - 5), main.Palette.Accent, "enabled macro box uses current accent");
            foreach (Control child in grid.Controls)
                if (child is ScrollBar)
                {
                    CheckNativeScrollbar(child.Handle, dark, child is HScrollBar);
                    if (dark)
                    {
                        typeof(Control).GetMethod("RecreateHandle", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(child, null);
                        CheckNativeScrollbar(child.Handle, dark, child is HScrollBar);
                    }
                }
            grid.CurrentCell = grid.Rows[0].Cells["key"]; grid.BeginEdit(true);
            ComboBox combo = (ComboBox)grid.EditingControl;
            Check(combo.DrawMode == DrawMode.OwnerDrawFixed, "actual grid dropdown editor uses themed item painting");
            CheckPixel(combo, new Point(combo.Width / 2, 4), main.Palette.Surface, "actual grid dropdown face follows theme");
            combo.DroppedDown = true; Application.DoEvents();
            ComboInfo info = new ComboInfo(); info.Size = Marshal.SizeOf(typeof(ComboInfo));
            Check(GetComboBoxInfo(combo.Handle, ref info) && info.List != IntPtr.Zero, "actual dropdown exposes its native list");
            CheckNativeScrollbar(info.List, dark, false);
            main.PreviewTheme(!dark);
            CheckNativeScrollbar(info.List, !dark, false);
            main.PreviewTheme(dark); combo.DroppedDown = false;
            combo.SelectedItem = "F"; grid.CommitEdit(DataGridViewDataErrorContexts.Commit); grid.EndEdit();
            Check(Convert.ToString(grid.Rows[0].Cells["key"].Value) == "F", "themed dropdown retains native selection and commit behavior");
            grid.FirstDisplayedScrollingRowIndex = 8;
            Check(grid.FirstDisplayedScrollingRowIndex == 8, "themed scrollbar preserves native grid scrolling");
            grid.FirstDisplayedScrollingRowIndex = 0;
        }
    }

    private static void CheckNativeScrollbar(IntPtr handle, bool dark, bool horizontal)
    {
        // Ask the underlying Windows renderer directly. A WM_PRINT snapshot of
        // our overlay hid the light native renderer and missed hover animations.
        IntPtr theme = OpenThemeData(handle, "ScrollBar");
        Check(theme != IntPtr.Zero, "native scrollbar has a visual style");
        try
        {
            foreach (int state in new int[] { 1, 2, 3, 5 }) // normal, hot, pressed, hover
            using (Bitmap bitmap = new Bitmap(20, 60))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                NativeRect bounds = new NativeRect { Right = 20, Bottom = 60 };
                IntPtr dc = g.GetHdc(); int result;
                try { result = DrawThemeBackground(theme, dc, horizontal ? 4 : 6, state, ref bounds, IntPtr.Zero); }
                finally { g.ReleaseHdc(dc); }
                Check(result == 0, "Windows renders scrollbar state " + state);
                Color pixel = bitmap.GetPixel(1, 30);
                Check(dark ? pixel.R < 100 && pixel.G < 100 && pixel.B < 100 : pixel.R > 180 && pixel.G > 180 && pixel.B > 180,
                    "underlying native scrollbar state follows theme: " + state);
            }
        }
        finally { CloseThemeData(theme); }
    }

    private static void CheckNumberThemes(MacroEditor editor)
    {
        NumericUpDown number = Field<NumericUpDown>(editor, "mainDuration");
        foreach (bool dark in new bool[] { true, false })
        {
            editor.PreviewTheme(dark); editor.Refresh(); Application.DoEvents();
            foreach (Control child in number.Controls)
                if (!(child is TextBox))
                    CheckPixel(child, new Point(1, 1), editor.Palette.Subtle, "numeric arrows follow the current theme");
        }
        decimal original = number.Value; number.UpButton();
        Check(number.Value == original + 1, "themed numeric arrows retain native increment behavior");
        number.Value = original;
    }

    private static void CheckButtonBackground(FluentWindow form)
    {
        using (FluentButton button = new FluentButton { Size = new Size(170, 36), Tone = FluentButtonTone.Danger })
        {
            form.Controls.Add(button);
            using (Bitmap bitmap = new Bitmap(button.Width, button.Height))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                // Windows may reuse a buffer containing pixels from the preceding
                // primary button. Opaque Button painting must cover its corners too.
                g.Clear(Color.Cyan);
                typeof(FluentButton).GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(button, new object[] { new PaintEventArgs(g, button.ClientRectangle) });
                Check(bitmap.GetPixel(button.Width - 1, 0).ToArgb() == form.Palette.Background.ToArgb(),
                    "rounded button corners overwrite pixels left by another control");
            }
            form.Controls.Remove(button);
        }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Run(args); Console.WriteLine("PASS: " + checks + " editor checks (no input injection)."); return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex.GetBaseException().Message); return 1; }
    }
    private static void Run(string[] args)
    {
        Macro source = new Macro { Key = "X", X = 800, Y = 600 };
        source.BeforeActions.Add(new MacroAction { Key = "1" });
        Action<int, int> point = null;
        using (MacroEditor dialog = new MacroEditor(source, delegate(Action<int, int> receive) { point = receive; }, delegate { point = null; }))
        {
            ShowPreview(dialog); CheckHeadings(dialog); CheckNumberThemes(dialog);
            DataGridView before = Field<DataGridView>(dialog, "before"), after = Field<DataGridView>(dialog, "after");
            ComboBox mode = Field<ComboBox>(dialog, "mode");
            Check(before.Rows.Count == 1 && after.Rows.Count == 0 && mode.SelectedIndex == 0, "editor loads per-key actions and hold mode");
            Check(Field<NumericUpDown>(dialog, "mainDuration").Enabled, "hold mode allows setting the deferred short-tap click duration");
            Check(Field<Label>(dialog, "durationCaption").Text == "短按点击时长 ms", "hold mode names the duration for deferred short taps");
            Field<NumericUpDown>(dialog, "mainDuration").Value = 25;
            before.Rows[0].Cells["duration"].Value = 12; before.Rows[0].Cells["gap"].Value = 10;
            Check(source.BeforeActions[0].DurationMs == 40 && source.BeforeActions[0].DelayAfterMs == 40, "unsaved dialog edits do not mutate profile data");
            mode.SelectedIndex = 1;
            Check(Field<NumericUpDown>(dialog, "mainDuration").Enabled, "click mode enables main click duration");
            Check(Field<Label>(dialog, "durationCaption").Text == "点击时长 ms", "click mode names its ordinary click duration");
            Call(dialog, "Add", after, "点击坐标");
            Check(String.IsNullOrEmpty(Convert.ToString(after.Rows[0].Cells["key"].Value)) && after.Rows[0].Cells["key"].ReadOnly,
                "new coordinate click does not display a keyboard binding");
            after.Rows[0].Cells["kind"].Value = "按键点按";
            after.Rows[0].Cells["key"].Value = "Q";
            after.Rows[0].Cells["kind"].Value = "点击坐标";
            Check(Convert.ToString(after.Rows[0].Cells["key"].Value) == "" &&
                ((DataGridViewComboBoxCell)after.Rows[0].Cells["key"]).DisplayStyle == DataGridViewComboBoxDisplayStyle.Nothing,
                "changing a keyboard action to a click removes its key text and dropdown");
            after.Rows[0].Cells["kind"].Value = "按键点按";
            Check(Convert.ToString(after.Rows[0].Cells["key"].Value) == "Q", "changing back to a keyboard action restores its previous key");
            after.Rows[0].Cells["kind"].Value = "点击坐标";
            after.CurrentCell = after.Rows[0].Cells["kind"];
            Call(dialog, "PrepareCapture", after);
            Check(point != null, "coordinate action can request non-focusing F6 capture");
            point(-555, 666);
            Check(Convert.ToInt32(after.Rows[0].Cells["x"].Value) == -555 && Convert.ToInt32(after.Rows[0].Cells["y"].Value) == 666, "F6 receiver fills the selected action coordinates");
            FluentTabs tabs = Field<FluentTabs>(dialog, "tabs");
            tabs.SelectedIndex = 1;
            Check(point == null, "switching action pages cancels stale coordinate capture");
            Message keyMessage = new Message();
            object[] keyArgs = new object[] { keyMessage, Keys.Control | Keys.Tab };
            bool handled = (bool)typeof(FluentTabs).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(tabs, keyArgs);
            Check(handled && tabs.SelectedIndex == 0, "Fluent action navigation supports Ctrl+Tab without injecting keyboard input");
            keyArgs[1] = Keys.Control | Keys.Shift | Keys.Tab;
            typeof(FluentTabs).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(tabs, keyArgs);
            Check(tabs.SelectedIndex == 1, "Fluent action navigation supports reverse keyboard switching");
            Call(dialog, "Add", after, "移动坐标");
            Check(Convert.ToString(after.Rows[1].Cells["key"].Value) == "" && after.Rows[1].Cells["key"].ReadOnly, "move action also has no displayed keyboard binding");
            after.Rows[1].Cells["x"].Value = 321; after.Rows[1].Cells["y"].Value = 123;
            Call(dialog, "Swap", after, -1);
            Check(Convert.ToString(after.Rows[0].Cells["kind"].Value) == "移动坐标", "action reorder preserves selected action values");
            Check(point == null, "reorder cancels stale coordinate receiver");
            if (args.Length > 0)
            {
                dialog.PreviewTheme(false); Render(dialog, args[0]);
                mode.SelectedIndex = 0; Application.DoEvents(); dialog.Refresh();
                Render(dialog, System.IO.Path.ChangeExtension(args[0], ".hold.png"));
                mode.SelectedIndex = 1;
                Field<FluentTabs>(dialog, "tabs").SelectedIndex = 1; Application.DoEvents(); dialog.Refresh();
                Render(dialog, System.IO.Path.ChangeExtension(args[0], ".coordinates.png"));
                dialog.PreviewTheme(true); Render(dialog, System.IO.Path.ChangeExtension(args[0], ".dark.png"));
                dialog.Size = dialog.MinimumSize; Render(dialog, System.IO.Path.ChangeExtension(args[0], ".minimum.png"));
                dialog.Hide();
            }
            Call(dialog, "Apply");
            Check(dialog.Result != null && dialog.Result.MouseMode == "Click" && dialog.Result.MainDurationMs == 25, "apply returns updated macro settings and click duration");
            Check(dialog.Result.BeforeActions[0].DurationMs == 12 && dialog.Result.BeforeActions[0].DelayAfterMs == 10, "custom tap duration and gap are retained");
            Check(dialog.Result.AfterActions.Count == 2 && dialog.Result.AfterActions[0].Kind == "Move" && dialog.Result.AfterActions[1].X == -555, "ordered extra coordinates are retained");
            Check(dialog.Result.AfterActions.TrueForAll(a => a.Key == ""), "saved coordinate actions contain no irrelevant keyboard binding");
        }
        using (MacroEditor plain = new MacroEditor(new Macro { Key = "X" }, delegate(Action<int, int> receive) { }, delegate { }))
        {
            ComboBox mode = Field<ComboBox>(plain, "mode");
            NumericUpDown duration = Field<NumericUpDown>(plain, "mainDuration");
            DataGridView before = Field<DataGridView>(plain, "before");
            Check(!duration.Enabled, "plain continuous hold disables the unused short-tap duration");
            mode.SelectedIndex = 1;
            Check(duration.Enabled, "single-click duration is editable without prefix actions");
            mode.SelectedIndex = 0;
            Call(plain, "Add", before, "移动坐标");
            Check(duration.Enabled, "delayed prefix move enables short-tap click duration");
            before.Rows[0].Cells["gap"].Value = 0;
            Check(!duration.Enabled, "instantaneous prefix move does not enable an irrelevant duration");
            Call(plain, "Add", before, "按键点按");
            Check(duration.Enabled, "prefix key tap enables short-tap duration immediately");
            before.Rows.RemoveAt(1);
            Check(!duration.Enabled, "removing the last deferred prefix disables the unused duration");
        }
        Macro legacy = new Macro { Key = "X", MouseMode = "Click" };
        legacy.AfterActions.Add(new MacroAction { Kind = "Click", Key = "Q" });
        using (MacroEditor loaded = new MacroEditor(legacy, delegate(Action<int, int> receive) { }, delegate { }))
        {
            DataGridView after = Field<DataGridView>(loaded, "after");
            Check(Convert.ToString(after.Rows[0].Cells["key"].Value) == "", "loaded legacy coordinate action hides stale key values");
            Check(legacy.AfterActions[0].Key == "Q", "loading the editor does not mutate existing profile data");
        }
        using (MacroEditor cancel = new MacroEditor(source, delegate(Action<int, int> receive) { }, delegate { }))
        {
            DataGridView before = Field<DataGridView>(cancel, "before");
            before.Rows[0].Cells["key"].Value = "Q";
            Call(cancel, "Add", before, "点击坐标");
            Call(cancel, "Swap", before, -1);
            Check(Convert.ToString(before.Rows[0].Cells["key"].Value) == "" && Convert.ToString(before.Rows[1].Cells["key"].Value) == "Q",
                "reordering mixed actions preserves the actual keyboard binding and hides coordinate key text");
            Call(cancel, "Swap", before, 1);
            Check(Convert.ToString(before.Rows[0].Cells["key"].Value) == "Q" && Convert.ToString(before.Rows[1].Cells["key"].Value) == "",
                "reordering mixed actions back does not leak cached keyboard text into coordinate rows");
            Call(cancel, "Add", Field<DataGridView>(cancel, "before"), "按键点按");
            cancel.Close();
            Check(cancel.Result == null && source.BeforeActions.Count == 1, "cancel leaves existing personalization untouched");
        }
        using (MainForm main = new MainForm(true))
        {
            ShowPreview(main);
            CheckHeadings(main);
            DataGridView grid = Field<DataGridView>(main, "grid");
            CheckThemes(main, grid);
            Check(grid.Rows.Count == 15 && Field<KeyboardWorker>(main, "worker") == null, "main-window preview has 15 rows without installing an input hook");
            Check(Field<Button>(main, "start").Enabled && Field<Label>(main, "status").Text.StartsWith("已暂停"), "redesigned main view starts paused with configuration available");
            Profile p = new Profile { Name = "我的工作配置" };
            Macro first = new Macro { Key = "X", X = 800, Y = 600, Label = "先点按 1，再执行原坐标" };
            first.BeforeActions.Add(new MacroAction { Key = "1", DelayAfterMs = 10 }); p.Macros.Add(first);
            Macro second = new Macro { Key = "C", X = 1024, Y = 720, MouseMode = "Click", Label = "两个独立坐标点击" };
            second.AfterActions.Add(new MacroAction { Kind = "Click", X = 1200, Y = 760 }); p.Macros.Add(second);
            p.Macros.Add(new Macro { Key = "E", X = 680, Y = 480, Label = "连续按住与新键接管" });
            Call(main, "LoadProfile", p, null);
            Profile read = (Profile)typeof(MainForm).GetMethod("GetProfile", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(main, null);
            Check(read.Macros.Count == 3 && read.Macros[0].BeforeActions[0].Key == "1" && read.Macros[1].AfterActions[0].X == 1200, "new main layout preserves per-row customization through profile load and readback");
            main.PreviewTheme(true);
            Check(!Field<Button>(main, "start").IsDisposed && !Field<bool>(main, "dirty"), "theme changes do not alter configuration or dirty state");
            Call(main, "SetRunning", true);
            Check(!Field<TableLayoutPanel>(main, "settings").Enabled && !Field<Button>(main, "start").Enabled && Field<Label>(main, "runState").Text.Contains("运行中"), "running state disables configuration while exposing its status");
            Call(main, "SetRunning", false);
            Check(Field<TableLayoutPanel>(main, "settings").Enabled && Field<Button>(main, "start").Enabled, "pause restores configuration in the new main layout");
            if (args.Length > 0)
            {
                main.PreviewTheme(false); Render(main, System.IO.Path.ChangeExtension(args[0], ".main.png"));
                main.PreviewTheme(true); Render(main, System.IO.Path.ChangeExtension(args[0], ".main-dark.png"));
                main.Size = main.MinimumSize; Render(main, System.IO.Path.ChangeExtension(args[0], ".main-minimum.png"));
                main.Hide();
                Console.WriteLine("Native frame: corners=" + main.NativeCornersApplied + ", backdrop=" + main.NativeBackdropApplied);
            }
        }
    }
}
