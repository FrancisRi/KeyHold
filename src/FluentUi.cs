using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KeyHoldMacro
{
    internal sealed class FluentPalette
    {
        public readonly bool Dark, HighContrast;
        public readonly Color Background, Surface, Subtle, Stroke, Text, Muted, Accent, AccentText, Selection, Danger, Success;
        public FluentPalette(bool dark, bool highContrast)
        {
            Dark = dark; HighContrast = highContrast;
            Background = dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243);
            Surface = dark ? Color.FromArgb(43, 43, 43) : Color.White;
            Subtle = dark ? Color.FromArgb(51, 51, 51) : Color.FromArgb(249, 249, 249);
            Stroke = dark ? Color.FromArgb(66, 66, 66) : Color.FromArgb(229, 229, 229);
            Text = dark ? Color.FromArgb(245, 245, 245) : Color.FromArgb(26, 26, 26);
            Muted = dark ? Color.FromArgb(185, 185, 185) : Color.FromArgb(99, 99, 99);
            Accent = dark ? Color.FromArgb(96, 205, 255) : Color.FromArgb(0, 103, 192);
            AccentText = dark ? Color.FromArgb(0, 51, 72) : Color.White;
            Selection = dark ? Color.FromArgb(38, 65, 78) : Color.FromArgb(230, 242, 252);
            Danger = dark ? Color.FromArgb(255, 153, 164) : Color.FromArgb(181, 32, 48);
            Success = dark ? Color.FromArgb(108, 203, 95) : Color.FromArgb(15, 108, 51);
            if (highContrast)
            {
                Background = Surface = Subtle = SystemColors.Window;
                Text = SystemColors.WindowText; Muted = SystemColors.GrayText; Stroke = Text;
                Accent = SystemColors.Highlight; AccentText = SystemColors.HighlightText;
                Selection = SystemColors.Highlight; Danger = Success = Text;
            }
        }
        public static FluentPalette SystemPalette()
        {
            bool dark = false;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    dark = key != null && Convert.ToInt32(key.GetValue("AppsUseLightTheme", 1)) == 0;
            }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
            return new FluentPalette(dark, SystemInformation.HighContrast);
        }
    }

    internal class FluentWindow : Form
    {
        private readonly Icon applicationIcon;
        public FluentPalette Palette { get; private set; }
        public bool NativeCornersApplied { get; private set; }
        public bool NativeBackdropApplied { get; private set; }
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        protected FluentWindow()
        {
            Palette = FluentPalette.SystemPalette();
            Font = FluentUi.Font(10F, FontStyle.Regular);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Palette.Background; ForeColor = Palette.Text;
            DoubleBuffered = true;
            using (System.IO.Stream resource = typeof(FluentWindow).Assembly.GetManifestResourceStream("KeyHold.AppIcon"))
            {
                if (resource != null) { applicationIcon = new Icon(resource); Icon = applicationIcon; }
            }
            SystemEvents.UserPreferenceChanged += PreferenceChanged;
        }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                // Form.DoubleBuffered does not buffer the child HWNDs. Compose
                // the whole tree so their initial clears are never presented
                // as a top-to-bottom white sweep on the desktop.
                parameters.ExStyle |= 0x02000000; // WS_EX_COMPOSITED
                return parameters;
            }
        }
        protected override void OnHandleCreated(EventArgs e)
        { base.OnHandleCreated(e); ApplyNativeFrame(); }
        protected override void OnShown(EventArgs e)
        { FitWorkingArea(); RefreshAppearance(); base.OnShown(e); }
        protected override void OnDpiChanged(DpiChangedEventArgs e)
        { base.OnDpiChanged(e); FitWorkingArea(); ApplyNativeFrame(); }
        private void FitWorkingArea()
        {
            if (WindowState != FormWindowState.Normal) return;
            Rectangle work = Screen.FromControl(this).WorkingArea;
            int width = Math.Max(320, work.Width - 32), height = Math.Max(320, work.Height - 32);
            if (Width <= width && Height <= height) return;
            MinimumSize = new Size(Math.Min(MinimumSize.Width, width), Math.Min(MinimumSize.Height, height));
            Size = new Size(Math.Min(Width, width), Math.Min(Height, height));
        }
        private void PreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (!IsHandleCreated || IsDisposed || Disposing) return;
            try { BeginInvoke((MethodInvoker)delegate { if (!IsDisposed) { Palette = FluentPalette.SystemPalette(); RefreshAppearance(); } }); }
            catch (InvalidOperationException) { }
        }
        internal void PreviewTheme(bool dark)
        { Palette = new FluentPalette(dark, false); RefreshAppearance(); }
        internal void RefreshAppearance()
        {
            BackColor = Palette.Background; ForeColor = Palette.Text;
            FluentUi.Apply(this, Palette); ApplyNativeFrame(); Invalidate(true);
        }
        private void ApplyNativeFrame()
        {
            if (!IsHandleCreated) return;
            try
            {
                int dark = Palette.Dark && !Palette.HighContrast ? 1 : 0;
                DwmSetWindowAttribute(Handle, 20, ref dark, 4);
                int corner = Palette.HighContrast ? 0 : 2;
                NativeCornersApplied = DwmSetWindowAttribute(Handle, 33, ref corner, 4) == 0;
                int backdrop = Palette.HighContrast ? 1 : 2;
                NativeBackdropApplied = DwmSetWindowAttribute(Handle, 38, ref backdrop, 4) == 0;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) SystemEvents.UserPreferenceChanged -= PreferenceChanged;
            base.Dispose(disposing);
            if (disposing && applicationIcon != null) applicationIcon.Dispose();
        }
    }

    internal enum FluentButtonTone { Standard, Primary, Subtle, Danger, Selected }

    internal sealed class FluentButton : Button
    {
        public FluentButtonTone Tone { get; set; }
        private bool hover, pressed;
        public FluentButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false; BackColor = Color.Transparent; Height = 36; Cursor = Cursors.Hand;
            Margin = new Padding(0, 3, 8, 3);
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) pressed = true; Invalidate(); base.OnKeyDown(e); }
        protected override void OnKeyUp(KeyEventArgs e) { pressed = false; Invalidate(); base.OnKeyUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Fill in control coordinates: Graphics.Clear ignores the translated
            // bounds when WinForms reuses a buffered parent/WM_PRINT surface.
            using (Brush brush = new SolidBrush(FluentUi.ParentColor(this))) e.Graphics.FillRectangle(brush, ClientRectangle);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            // ButtonBase can skip the background pass for an opaque paint.
            // Cover the entire buffer before drawing the rounded shape.
            OnPaintBackground(e);
            FluentPalette p = FluentUi.PaletteFor(this);
            Color fill = Tone == FluentButtonTone.Primary ? p.Accent : Tone == FluentButtonTone.Subtle ? FluentUi.ParentColor(this) : p.Surface;
            Color ink = Tone == FluentButtonTone.Primary ? p.AccentText : Tone == FluentButtonTone.Danger ? p.Danger : Tone == FluentButtonTone.Selected ? p.Accent : p.Text;
            if (hover) fill = FluentUi.Blend(fill, p.Text, pressed ? 0.10 : 0.04);
            if (!Enabled) { fill = p.Subtle; ink = p.Muted; }
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = FluentUi.Round(new RectangleF(0.5F, 0.5F, Width - 1, Height - 1), 4F * e.Graphics.DpiX / 96F))
            {
                using (Brush brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, path);
                if (Tone != FluentButtonTone.Subtle && Tone != FluentButtonTone.Primary)
                    using (Pen pen = new Pen(p.Stroke)) e.Graphics.DrawPath(pen, path);
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (Focused && ShowFocusCues)
                using (GraphicsPath focus = FluentUi.Round(Rectangle.Inflate(ClientRectangle, -4, -4), 3))
                using (Pen pen = new Pen(p.Accent)) e.Graphics.DrawPath(pen, focus);
        }
    }

    internal sealed class FluentCard : Panel
    {
        public int CornerRadius { get; set; }
        public FluentCard()
        {
            CornerRadius = 8; Padding = new Padding(16); Margin = new Padding(0, 0, 0, 12);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            FluentPalette p = FluentUi.PaletteFor(this);
            using (Brush background = new SolidBrush(FluentUi.ParentColor(this))) e.Graphics.FillRectangle(background, ClientRectangle);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = FluentUi.Round(new RectangleF(0.5F, 0.5F, Width - 1, Height - 1), CornerRadius * e.Graphics.DpiX / 96F))
            {
                using (Brush brush = new SolidBrush(BackColor)) e.Graphics.FillPath(brush, path);
                using (Pen pen = new Pen(p.Stroke)) e.Graphics.DrawPath(pen, path);
            }
        }
    }

    internal sealed class FluentToggle : CheckBox
    {
        public FluentToggle() { AutoSize = false; Size = new Size(310, 32); Cursor = Cursors.Hand; DoubleBuffered = true; }
        protected override void OnPaint(PaintEventArgs e)
        {
            FluentPalette p = FluentUi.PaletteFor(this);
            if (p.HighContrast) { base.OnPaint(e); return; }
            using (Brush background = new SolidBrush(FluentUi.ParentColor(this))) e.Graphics.FillRectangle(background, ClientRectangle);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = e.Graphics.DpiX / 96F, w = 40 * scale, h = 20 * scale, y = (Height - h) / 2;
            using (GraphicsPath path = FluentUi.Round(new RectangleF(1, y, w, h), h / 2))
            {
                using (Brush fill = new SolidBrush(Checked && Enabled ? p.Accent : p.Subtle)) e.Graphics.FillPath(fill, path);
                using (Pen pen = new Pen(Checked ? p.Accent : p.Muted)) e.Graphics.DrawPath(pen, path);
            }
            float dot = 12 * scale, x = Checked ? w - dot - 3 * scale : 5 * scale;
            using (Brush brush = new SolidBrush(Checked ? p.AccentText : p.Muted)) e.Graphics.FillEllipse(brush, x, y + 4 * scale, dot, dot);
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle((int)(52 * scale), 0, Width - (int)(52 * scale), Height), Enabled ? p.Text : p.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -1, -1));
        }
    }

    internal sealed class FluentTextField : Panel
    {
        private readonly TextBox editor;
        private bool hover;
        internal FluentTextField(TextBox input)
        {
            editor = input; Height = 36; TabStop = false; AccessibleRole = AccessibleRole.Grouping; Cursor = Cursors.IBeam;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            editor.BorderStyle = BorderStyle.None; editor.AutoSize = true;
            editor.Margin = new Padding(0); Controls.Add(editor);
            editor.GotFocus += delegate { Invalidate(); };
            editor.LostFocus += delegate { Invalidate(); };
            editor.MouseEnter += delegate { hover = true; Invalidate(); };
            editor.MouseLeave += delegate { hover = false; Invalidate(); };
        }
        internal void RefreshAppearance()
        {
            FluentPalette p = FluentUi.PaletteFor(this);
            editor.BackColor = Enabled ? p.Surface : p.Subtle;
            editor.ForeColor = Enabled ? p.Text : p.Muted;
            Invalidate();
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e); if (editor == null) return;
            int inset = (int)Math.Round(10F * DeviceDpi / 96F);
            editor.SetBounds(inset, Math.Max(0, (ClientSize.Height - editor.PreferredHeight) / 2),
                Math.Max(1, ClientSize.Width - inset * 2), editor.PreferredHeight);
        }
        protected override void OnEnabledChanged(EventArgs e)
        { base.OnEnabledChanged(e); if (editor != null) RefreshAppearance(); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) editor.Focus(); }
        protected override void OnPaintBackground(PaintEventArgs e)
        { using (Brush background = new SolidBrush(FluentUi.ParentColor(this))) e.Graphics.FillRectangle(background, ClientRectangle); }
        protected override void OnPaint(PaintEventArgs e)
        {
            OnPaintBackground(e);
            FluentPalette p = FluentUi.PaletteFor(this);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = FluentUi.Round(new RectangleF(.5F, .5F, Width - 1, Height - 1), 4F * e.Graphics.DpiX / 96F))
            {
                using (Brush fill = new SolidBrush(Enabled ? p.Surface : p.Subtle)) e.Graphics.FillPath(fill, path);
                Color stroke = editor.Focused && Enabled ? p.Accent : p.HighContrast ? p.Text : hover && Enabled ? p.Muted : p.Stroke;
                using (Pen pen = new Pen(stroke)) e.Graphics.DrawPath(pen, path);
            }
        }
    }

    internal sealed class FluentComboBox : ComboBox
    {
        public FluentComboBox()
        {
            FluentComboSupport.Attach(this); ItemHeight = 26;
        }
        protected override void OnFontChanged(EventArgs e)
        { base.OnFontChanged(e); ItemHeight = Math.Max(22, Font.Height + 6); }
        internal static void PaintFace(ComboBox combo, Graphics g)
        {
            FluentPalette p = FluentUi.PaletteFor(combo);
            int width = combo.ClientSize.Width, height = combo.ClientSize.Height;
            using (Brush background = new SolidBrush(FluentUi.ParentColor(combo))) g.FillRectangle(background, combo.ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = FluentUi.Round(new RectangleF(0.5F, 0.5F, width - 1, height - 1), 4))
            {
                using (Brush fill = new SolidBrush(p.Surface)) g.FillPath(fill, path);
                using (Pen pen = new Pen(combo.Focused ? p.Accent : p.Stroke)) g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, combo.Text, combo.Font, new Rectangle(10, 0, Math.Max(0, width - 40), height), combo.Enabled ? p.Text : p.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            using (Pen pen = new Pen(p.Muted, 1.4F))
            {
                float x = width - 17, y = height / 2F;
                g.DrawLines(pen, new PointF[] { new PointF(x - 4, y - 2), new PointF(x, y + 2), new PointF(x + 4, y - 2) });
            }
        }
    }

    // Keep native selection, scrolling, keyboard navigation and accessibility;
    // only replace painting. The popup and grid editors have separate HWNDs.
    internal sealed class FluentComboSupport : NativeWindow
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ComboBox, FluentComboSupport> instances =
            new System.Runtime.CompilerServices.ConditionalWeakTable<ComboBox, FluentComboSupport>();
        private readonly ComboBox combo;
        private readonly FluentNativeChrome popup;
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ComboInfo
        { public int Size; public NativeRect Item, Button; public int State; public IntPtr Combo, Edit, List; }
        [DllImport("user32.dll")]
        private static extern bool GetComboBoxInfo(IntPtr window, ref ComboInfo info);
        private FluentComboSupport(ComboBox control)
        {
            combo = control; popup = new FluentNativeChrome(control, true);
            combo.DrawMode = DrawMode.OwnerDrawFixed; combo.FlatStyle = FlatStyle.Flat;
            combo.MaxDropDownItems = 12; combo.IntegralHeight = false;
            combo.HandleCreated += delegate { AssignHandle(combo.Handle); };
            combo.HandleDestroyed += delegate { ReleaseHandle(); popup.Detach(); };
            combo.Disposed += delegate { popup.Detach(); ReleaseHandle(); };
            combo.DrawItem += DrawItem;
            combo.DropDown += delegate
            {
                combo.DropDownHeight = combo.ItemHeight * Math.Min(12, Math.Max(1, combo.Items.Count)) + 2;
                ComboInfo info = new ComboInfo(); info.Size = Marshal.SizeOf(typeof(ComboInfo));
                if (GetComboBoxInfo(combo.Handle, ref info) && info.List != IntPtr.Zero) popup.Attach(info.List);
            };
            if (combo.IsHandleCreated) AssignHandle(combo.Handle);
        }
        internal static void Attach(ComboBox combo)
        { instances.GetValue(combo, delegate(ComboBox control) { return new FluentComboSupport(control); }); }
        internal static void Refresh(ComboBox combo)
        {
            FluentComboSupport support;
            if (instances.TryGetValue(combo, out support)) support.popup.ApplyTheme();
        }
        private void DrawItem(object sender, DrawItemEventArgs e)
        {
            FluentPalette p = FluentUi.PaletteFor(combo);
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (Brush fill = new SolidBrush(selected ? p.Selection : p.Surface)) e.Graphics.FillRectangle(fill, e.Bounds);
            string value = e.Index < 0 ? combo.Text : combo.GetItemText(combo.Items[e.Index]);
            Rectangle text = e.Bounds; text.Inflate(-8, 0);
            TextRenderer.DrawText(e.Graphics, value, combo.Font, text, combo.Enabled ? (selected && p.HighContrast ? p.AccentText : p.Text) : p.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (combo.DropDownStyle != ComboBoxStyle.DropDownList || !combo.IsHandleCreated) return;
            if (m.Msg == 0xF)
                using (Graphics g = Graphics.FromHwnd(Handle)) FluentComboBox.PaintFace(combo, g);
            else if ((m.Msg == 0x318 || m.Msg == 0x317) && m.WParam != IntPtr.Zero)
                using (Graphics g = Graphics.FromHdc(m.WParam)) FluentComboBox.PaintFace(combo, g);
        }
    }

    internal sealed class FluentNativeChrome : NativeWindow
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, FluentNativeChrome> instances =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Control, FluentNativeChrome>();
        private readonly Control owner;
        private readonly bool popup;
        private readonly bool spin;
        private bool? nativeDark;
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr window, string application, string classes);
        [DllImport("uxtheme.dll")] private static extern int BufferedPaintStopAllAnimations(IntPtr window);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
        [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        internal FluentNativeChrome(Control control, bool isPopup)
        { owner = control; popup = isPopup; spin = control.Parent is NumericUpDown; }
        internal static void Watch(Control control)
        {
            FluentNativeChrome instance = instances.GetValue(control, delegate(Control item)
            {
                FluentNativeChrome chrome = new FluentNativeChrome(item, false);
                item.HandleCreated += delegate { chrome.Attach(item.Handle); };
                item.HandleDestroyed += delegate { chrome.Detach(); };
                item.Disposed += delegate { chrome.Detach(); };
                if (item.IsHandleCreated) chrome.Attach(item.Handle);
                return chrome;
            });
            instance.ApplyTheme();
        }
        internal void Attach(IntPtr handle)
        { if (Handle == handle) { ApplyTheme(); return; } Detach(); AssignHandle(handle); ApplyTheme(); }
        internal void Detach() { nativeDark = null; if (Handle != IntPtr.Zero) ReleaseHandle(); }
        internal void ApplyTheme()
        {
            if (Handle == IntPtr.Zero || spin || owner.IsDisposed) return;
            FluentPalette p = FluentUi.PaletteFor(owner);
            bool dark = p.Dark && !p.HighContrast;
            if (nativeDark == dark) return;
            nativeDark = dark; // SetWindowTheme synchronously sends WM_THEMECHANGED.
            BufferedPaintStopAllAnimations(Handle);
            // Windows 11's theme identifier is also used by modern Windows Forms.
            // Theme the native renderer itself: hover animations and the native
            // scrollbar tracking loop can paint without another WM_PAINT call.
            SetWindowTheme(Handle, dark ? "DarkMode_Explorer" : null, null);
            BufferedPaintStopAllAnimations(Handle);
        }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (Handle == IntPtr.Zero || owner.IsDisposed || FluentUi.PaletteFor(owner).HighContrast) return;
            if (!spin && !popup) return; // Native scrollbars paint every state themselves.
            if (m.Msg == 0x317 || m.Msg == 0x318)
            {
                if (m.WParam != IntPtr.Zero)
                    using (Graphics g = Graphics.FromHdc(m.WParam)) Paint(g);
            }
            else if (m.Msg == 0xF || m.Msg == 0x85 || m.Msg == 0xA0 || m.Msg == 0x200 ||
                m.Msg == 0x201 || m.Msg == 0x202 || m.Msg == 0xA1 || m.Msg == 0xA2 ||
                m.Msg == 0x113 || m.Msg == 0x114 || m.Msg == 0x115 || m.Msg == 0x20A || m.Msg == 0x215)
            {
                IntPtr dc = GetWindowDC(Handle);
                if (dc == IntPtr.Zero) return;
                try { using (Graphics g = Graphics.FromHdc(dc)) Paint(g); }
                finally { ReleaseDC(Handle, dc); }
            }
        }
        private void Paint(Graphics g)
        {
            NativeRect window; if (!GetWindowRect(Handle, out window)) return;
            FluentPalette p = FluentUi.PaletteFor(owner);
            int width = window.Right - window.Left, height = window.Bottom - window.Top;
            if (width < 1 || height < 1) return;
            if (spin)
            {
                using (Brush fill = new SolidBrush(p.Subtle)) g.FillRectangle(fill, 0, 0, width, height);
                using (Pen border = new Pen(p.Stroke)) g.DrawLine(border, 0, height / 2, width, height / 2);
                using (Pen pen = new Pen(owner.Enabled ? p.Text : p.Muted, 1.3F))
                {
                    float x = width / 2F;
                    for (int i = 0; i < 2; i++)
                    {
                        float y = height * (i == 0 ? .25F : .75F), direction = i == 0 ? 1 : -1;
                        g.DrawLines(pen, new PointF[] { new PointF(x - 3, y + direction), new PointF(x, y - direction * 2), new PointF(x + 3, y + direction) });
                    }
                }
                return;
            }
            if (popup)
                using (Pen border = new Pen(p.Stroke)) g.DrawRectangle(border, 0, 0, width - 1, height - 1);
        }
    }

    internal sealed class FluentTabs : Panel
    {
        private readonly System.Collections.Generic.List<Control> pages = new System.Collections.Generic.List<Control>();
        private readonly System.Collections.Generic.List<FluentButton> buttons = new System.Collections.Generic.List<FluentButton>();
        private readonly FlowLayoutPanel navigation = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
        private readonly FluentCard content = new FluentCard { Dock = DockStyle.Fill, Padding = new Padding(1), Margin = new Padding(0) };
        private int selectedIndex = -1;
        public event EventHandler SelectedIndexChanged;
        public FluentTabs()
        {
            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0) };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.Controls.Add(navigation, 0, 0); root.Controls.Add(content, 0, 1); Controls.Add(root);
        }
        public void AddPage(Control page, string title)
        {
            int index = pages.Count; pages.Add(page); page.Dock = DockStyle.Fill; page.Visible = false; content.Controls.Add(page);
            FluentButton button = new FluentButton { Text = title, Width = 240, Height = 38 };
            button.Click += delegate { SelectedIndex = index; };
            button.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right)
                { SelectedIndex = (selectedIndex + (e.KeyCode == Keys.Right ? 1 : pages.Count - 1)) % pages.Count; buttons[selectedIndex].Focus(); e.Handled = true; }
            };
            buttons.Add(button); navigation.Controls.Add(button);
            if (selectedIndex < 0) SelectedIndex = 0;
        }
        public int SelectedIndex
        {
            get { return selectedIndex; }
            set
            {
                if (value < 0 || value >= pages.Count || value == selectedIndex) return;
                selectedIndex = value;
                for (int i = 0; i < pages.Count; i++)
                { pages[i].Visible = i == value; buttons[i].Tone = i == value ? FluentButtonTone.Selected : FluentButtonTone.Subtle; buttons[i].AccessibleDescription = i == value ? L.T("已选择") : L.T("未选择"); buttons[i].Invalidate(); }
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if ((keyData == (Keys.Control | Keys.Tab) || keyData == (Keys.Control | Keys.Shift | Keys.Tab)) && pages.Count > 0)
            { SelectedIndex = (selectedIndex + ((keyData & Keys.Shift) != 0 ? pages.Count - 1 : 1)) % pages.Count; return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    internal static class FluentUi
    {
        private static readonly FluentPalette fallback = new FluentPalette(false, false);
        public static Font Font(float size, FontStyle style) { return new Font(L.Language == "en" ? "Segoe UI" : "Microsoft YaHei UI", size, style); }
        public static Label Text(string value, float size, bool muted)
        { return new Label { Text = value, AutoSize = true, Font = Font(size, FontStyle.Regular), Tag = muted ? "muted" : null, BackColor = Color.Transparent, Margin = new Padding(0) }; }
        public static FluentPalette PaletteFor(Control control)
        { Form form = control.FindForm(); FluentWindow window = form as FluentWindow; return window == null ? fallback : window.Palette; }
        public static Color ParentColor(Control control)
        {
            Control parent = control.Parent;
            while (parent != null) { if (parent.BackColor.A == 255) return parent.BackColor; parent = parent.Parent; }
            return PaletteFor(control).Background;
        }
        public static Color Blend(Color a, Color b, double amount)
        { return Color.FromArgb((int)(a.R * (1 - amount) + b.R * amount), (int)(a.G * (1 - amount) + b.G * amount), (int)(a.B * (1 - amount) + b.B * amount)); }
        public static GraphicsPath Round(RectangleF r, float radius)
        {
            GraphicsPath path = new GraphicsPath(); float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { path.AddRectangle(r); return path; }
            path.AddArc(r.Left, r.Top, d, d, 180, 90); path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90); path.CloseFigure(); return path;
        }
        public static void Apply(Control control, FluentPalette p)
        {
            if (control is FluentCard || control is FluentTextField) control.BackColor = p.Surface;
            else if (control is Label) control.ForeColor = Convert.ToString(control.Tag) == "muted" ? p.Muted : p.Text;
            else if (control is TextBox || control is ComboBox || control is NumericUpDown)
            {
                control.BackColor = p.Surface; control.ForeColor = p.Text;
                if (control is ComboBox) FluentComboSupport.Refresh((ComboBox)control);
                if (control is NumericUpDown)
                    foreach (Control child in control.Controls)
                        if (!(child is TextBox)) FluentNativeChrome.Watch(child);
            }
            else if (control is ScrollBar) FluentNativeChrome.Watch(control);
            else if (control is TabPage) { control.BackColor = p.Surface; control.ForeColor = p.Text; }
            else if (control is DataGridView) StyleGrid((DataGridView)control, p);
            else if (control is Panel) control.BackColor = Color.Transparent;
            foreach (Control child in control.Controls) Apply(child, p);
            FluentTextField field = control as FluentTextField;
            if (field != null) field.RefreshAppearance();
        }
        public static void ConfigureGrid(DataGridView grid)
        {
            grid.BorderStyle = BorderStyle.None; grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            grid.EnableHeadersVisualStyles = false; grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.ColumnHeadersHeight = 40; grid.RowTemplate.Height = 36;
            foreach (Control child in grid.Controls)
            {
                ScrollBar bar = child as ScrollBar; if (bar == null) continue;
                FluentNativeChrome.Watch(bar);
            }
            grid.EditingControlShowing += delegate(object sender, DataGridViewEditingControlShowingEventArgs e)
            {
                FluentPalette p = PaletteFor(grid); e.Control.BackColor = p.Surface; e.Control.ForeColor = p.Text;
                ComboBox combo = e.Control as ComboBox;
                if (combo != null) { FluentComboSupport.Attach(combo); combo.ItemHeight = Math.Max(22, combo.Font.Height + 6); }
            };
            grid.CellPainting += delegate(object sender, DataGridViewCellPaintingEventArgs e)
            {
                if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
                if (grid.Columns[e.ColumnIndex] is DataGridViewCheckBoxColumn && !PaletteFor(grid).HighContrast)
                {
                    FluentPalette checkPalette = PaletteFor(grid); e.PaintBackground(e.ClipBounds, true);
                    int size = (int)(16 * e.Graphics.DpiX / 96F);
                    Rectangle box = new Rectangle(e.CellBounds.Left + (e.CellBounds.Width - size) / 2,
                        e.CellBounds.Top + (e.CellBounds.Height - size) / 2, size, size);
                    bool value = Object.Equals(e.FormattedValue, true);
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    using (GraphicsPath path = Round(box, 3))
                    {
                        using (Brush fill = new SolidBrush(value && grid.Enabled ? checkPalette.Accent : checkPalette.Subtle)) e.Graphics.FillPath(fill, path);
                        using (Pen pen = new Pen(value ? checkPalette.Accent : checkPalette.Muted)) e.Graphics.DrawPath(pen, path);
                    }
                    if (value)
                        using (Pen pen = new Pen(grid.Enabled ? checkPalette.AccentText : checkPalette.Muted, 2))
                            e.Graphics.DrawLines(pen, new PointF[] { new PointF(box.Left + size * .23F, box.Top + size * .5F),
                                new PointF(box.Left + size * .43F, box.Top + size * .7F), new PointF(box.Left + size * .78F, box.Top + size * .3F) });
                    using (Pen pen = new Pen(checkPalette.Stroke)) e.Graphics.DrawLine(pen, e.CellBounds.Left, e.CellBounds.Bottom - 1, e.CellBounds.Right, e.CellBounds.Bottom - 1);
                    if ((e.State & DataGridViewElementStates.Selected) != 0 && grid.Focused && grid.CurrentCell == grid.Rows[e.RowIndex].Cells[e.ColumnIndex])
                        e.Paint(e.ClipBounds, DataGridViewPaintParts.Focus);
                    e.Handled = true; return;
                }
                if (grid.Columns[e.ColumnIndex] is DataGridViewComboBoxColumn)
                {
                    FluentPalette comboPalette = PaletteFor(grid); e.PaintBackground(e.ClipBounds, true);
                    Rectangle text = e.CellBounds; text.Inflate(-8, 0); text.Width -= 18;
                    TextRenderer.DrawText(e.Graphics, Convert.ToString(e.FormattedValue), grid.Font, text, grid.Enabled ? comboPalette.Text : comboPalette.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    DataGridViewComboBoxCell cell = grid.Rows[e.RowIndex].Cells[e.ColumnIndex] as DataGridViewComboBoxCell;
                    if (cell != null && !cell.ReadOnly && cell.DisplayStyle != DataGridViewComboBoxDisplayStyle.Nothing)
                    {
                        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                        using (Pen pen = new Pen(comboPalette.Muted, 1.3F))
                        {
                            float x = e.CellBounds.Right - 15, y = e.CellBounds.Top + e.CellBounds.Height / 2F;
                            e.Graphics.DrawLines(pen, new PointF[] { new PointF(x - 3, y - 1), new PointF(x, y + 2), new PointF(x + 3, y - 1) });
                        }
                    }
                    using (Pen pen = new Pen(comboPalette.Stroke)) e.Graphics.DrawLine(pen, e.CellBounds.Left, e.CellBounds.Bottom - 1, e.CellBounds.Right, e.CellBounds.Bottom - 1);
                    e.Handled = true; return;
                }
                if (!(grid.Columns[e.ColumnIndex] is DataGridViewButtonColumn)) return;
                FluentPalette p = PaletteFor(grid); e.PaintBackground(e.ClipBounds, true);
                Rectangle r = Rectangle.Inflate(e.CellBounds, -8, -5);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath path = Round(r, 4))
                {
                    using (Brush brush = new SolidBrush(p.Subtle)) e.Graphics.FillPath(brush, path);
                    using (Pen pen = new Pen(p.Stroke)) e.Graphics.DrawPath(pen, path);
                }
                TextRenderer.DrawText(e.Graphics, Convert.ToString(e.FormattedValue), grid.Font, r, grid.Enabled ? p.Accent : p.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                e.Handled = true;
            };
        }
        private static void StyleGrid(DataGridView grid, FluentPalette p)
        {
            grid.BackgroundColor = p.Surface; grid.GridColor = p.Stroke;
            grid.DefaultCellStyle.BackColor = p.Surface; grid.DefaultCellStyle.ForeColor = p.Text;
            grid.DefaultCellStyle.SelectionBackColor = p.Selection; grid.DefaultCellStyle.SelectionForeColor = p.HighContrast ? p.AccentText : p.Text;
            grid.DefaultCellStyle.Padding = new Padding(8, 0, 8, 0);
            grid.ColumnHeadersDefaultCellStyle.BackColor = p.Surface; grid.ColumnHeadersDefaultCellStyle.ForeColor = p.Muted;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = p.Surface; grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(8, 0, 8, 0);
            grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
            grid.AlternatingRowsDefaultCellStyle.BackColor = p.Surface;
            foreach (DataGridViewRow row in grid.Rows)
                foreach (DataGridViewCell cell in row.Cells) cell.Style.BackColor = cell.ReadOnly ? p.Subtle : p.Surface;
        }
    }
}
