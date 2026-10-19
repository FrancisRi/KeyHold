using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("KeyHold")]
[assembly: System.Reflection.AssemblyProduct("KeyHold")]
[assembly: System.Reflection.AssemblyVersion("0.2.1.0")]
[assembly: System.Reflection.AssemblyFileVersion("0.2.1.0")]

namespace KeyHoldMacro
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            L.Initialize(L.PreferencePath, System.Globalization.CultureInfo.CurrentUICulture.Name);
            bool created;
            using (Mutex mutex = new Mutex(true, "Local\\KeyHoldMacro-76D6DF29", out created))
            {
                if (!created) { MessageBox.Show(L.T("程序已在运行，请双击托盘图标打开。"), "KeyHold"); return; }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                KeyHoldContext context = null;
                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
                {
                    if (context != null && context.Window != null) context.Window.EmergencyStop();
                    MessageBox.Show(L.T("已请求停止宏。\r\n") + e.Exception.Message, L.T("程序错误"));
                };
                try { using (context = new KeyHoldContext()) Application.Run(context); }
                catch (Exception ex) { MessageBox.Show(ex.ToString(), L.T("KeyHold 启动失败")); }
            }
        }
    }

    // Keep one native UI/message-loop lifetime while language changes replace
    // the main form, without exiting and restarting the application.
    internal sealed class KeyHoldContext : ApplicationContext
    {
        private readonly Func<MainForm> createForm;
        internal MainForm Window { get { return MainForm as MainForm; } }
        internal KeyHoldContext() : this(delegate { return new MainForm(); }) { }
        internal KeyHoldContext(Func<MainForm> factory)
        { createForm = factory; MainForm = createForm(); }
        protected override void OnMainFormClosed(object sender, EventArgs e)
        {
            MainForm previous = (MainForm)sender;
            if (previous.NextLanguage == null) { base.OnMainFormClosed(sender, e); return; }
            L.Language = previous.NextLanguage;
            MainForm = createForm();
            previous.Dispose();
            MainForm.Show();
        }
    }

    internal sealed class ProfileItem
    {
        public string Name, Path;
        public override string ToString() { return Name; }
    }

    internal sealed class MainForm : FluentWindow
    {
        private readonly string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeyHoldMacro", "Profiles");
        private readonly KeyboardWorker worker;
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly DataGridView grid = new FluentGrid();
        private readonly ComboBox profiles = new FluentComboBox();
        private readonly TextBox name = new TextBox();
        private readonly CheckBox suppress = new FluentToggle(), pauseFocus = new FluentToggle();
        private readonly Label status = new Label(), cursor = new Label(), picked = new Label();
        private readonly Button start = new FluentButton { Tone = FluentButtonTone.Primary };
        private readonly Label runState = new Label();
        private readonly TableLayoutPanel settings = new FluentTableLayoutPanel();
        private readonly ComboBox language = new FluentComboBox();
        private readonly bool previewOnly;
        internal string NextLanguage { get; private set; }
        private bool loading, dirty, closing;
        private string currentPath, loadedName;
        private int captureRow, countdown, countdownVersion;
        private DateTime startAt;
        private Point? lastCursor;
        private Rectangle lastDesktop;
        private MacroEditor editing;
        private Action<int, int> actionPoint;
        public MainForm() : this(false) { }
        internal MainForm(bool previewOnly)
        {
            this.previewOnly = previewOnly;
            Text = "KeyHold";
            ClientSize = new Size(1240, 920); MinimumSize = new Size(1080, 820);
            StartPosition = FormStartPosition.CenterScreen;
            if (!previewOnly) Directory.CreateDirectory(folder);
            BuildUI();
            if (previewOnly)
            {
                LoadProfile(new Profile(), null); RefreshAppearance();
                Disposed += delegate { tray.Dispose(); timer.Dispose(); };
                return;
            }
            worker = new KeyboardWorker();
            ConfigureTray();
            LoadProfile(new Profile(), null);
            RefreshProfiles();
            TryLoadLast();
            RefreshAppearance();
            timer.Interval = 40; timer.Tick += Tick; timer.Start();
            Resize += delegate { if (WindowState == FormWindowState.Minimized) Hide(); };
            FormClosing += OnClosing;
            SystemEvents.SessionSwitch += SessionChanged;
            SystemEvents.PowerModeChanged += PowerChanged;
            Shown += delegate { grid.ClearSelection(); grid.Rows[0].Selected = true; };
        }

        private Button CreateButton(string text, EventHandler action, int width)
        {
            using (Font font = FluentUi.Font(10F, FontStyle.Regular))
                width = Math.Max(width, TextRenderer.MeasureText(text, font).Width + 16);
            Button b = new FluentButton { Text = text, Width = width, Height = 36 };
            b.Click += action; return b;
        }
        private FlowLayoutPanel Flow()
        {
            FlowLayoutPanel p = new FluentFlowLayoutPanel(); p.Dock = DockStyle.Fill;
            p.WrapContents = false; p.Padding = new Padding(0); p.Margin = new Padding(0); p.BackColor = Color.Transparent; return p;
        }
        private void BuildUI()
        {
            TableLayoutPanel root = new FluentTableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28, 20, 28, 20), ColumnCount = 1, RowCount = 5 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            Controls.Add(root);

            TableLayoutPanel heading = new FluentTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 3, Margin = new Padding(0, 0, 0, 18) };
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));
            heading.RowStyles.Add(new RowStyle(SizeType.AutoSize)); heading.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label title = FluentUi.Text(L.T("宏配置"), 25F, false); title.Font = FluentUi.Font(25F, FontStyle.Bold);
            heading.Controls.Add(title, 0, 0);
            heading.Controls.Add(FluentUi.Text(L.T("让每个按键，按你的方式执行。"), 10F, true), 0, 1);
            runState.Text = L.T("●  已暂停"); runState.AutoSize = false; runState.TextAlign = ContentAlignment.MiddleRight;
            runState.Dock = DockStyle.Fill; runState.Font = FluentUi.Font(11F, FontStyle.Bold);
            heading.Controls.Add(runState, 1, 0);
            Label shortcuts = FluentUi.Text(L.T("F6 取点   ·   F8 启停   ·   F12 急停"), 9F, true);
            shortcuts.Anchor = AnchorStyles.Top | AnchorStyles.Right; shortcuts.TextAlign = ContentAlignment.TopRight;
            heading.Controls.Add(shortcuts, 1, 1);
            heading.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            language.DropDownStyle = ComboBoxStyle.DropDownList; language.Width = 180;
            language.Items.AddRange(new object[] { "简体中文", "English" });
            language.SelectedIndex = L.Language == "en" ? 1 : 0;
            language.AccessibleName = "Language / 语言";
            // Leave bottom space for the native combo's non-client border;
            // its auto-size measurement can omit two physical border pixels.
            language.Anchor = AnchorStyles.Right; language.Margin = new Padding(0, 8, 0, 8);
            heading.Controls.Add(language, 1, 2);
            Label languageHint = FluentUi.Text(L.T("语言切换后将重新打开配置窗口。"), 9F, true);
            languageHint.Margin = new Padding(0, 12, 0, 0); heading.Controls.Add(languageHint, 0, 2);
            // Native combo selection (including UI Automation) must return
            // before its owning window is disposed and reconstructed.
            language.SelectedIndexChanged += delegate
            {
                if (IsHandleCreated) BeginInvoke((MethodInvoker)delegate { if (!IsDisposed && !closing) ChangeLanguage(); });
            };
            root.Controls.Add(heading, 0, 0);

            settings.Dock = DockStyle.Fill; settings.Margin = new Padding(0); settings.ColumnCount = 1; settings.RowCount = 4;
            settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 160));
            settings.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
            root.Controls.Add(settings, 0, 1);

            FluentCard profileCard = new FluentCard { Dock = DockStyle.Fill };
            TableLayoutPanel profileBody = new FluentTableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Margin = new Padding(0) };
            profileBody.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            profileBody.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
            profileBody.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
            profileBody.Controls.Add(FluentUi.Text(L.T("配置文件"), 11F, false), 0, 0);
            FlowLayoutPanel p = Flow();
            Label label = FluentUi.Text(L.T("已存配置"), 9.5F, true); label.Padding = new Padding(0, 9, 12, 0); p.Controls.Add(label);
            profiles.DropDownStyle = ComboBoxStyle.DropDownList; profiles.FlatStyle = FlatStyle.Flat; profiles.Width = 255;
            profiles.Margin = new Padding(0, 6, 12, 0); p.Controls.Add(profiles);
            p.Controls.Add(CreateButton(L.T("读取"), delegate { ReadSelected(); }, 66));
            p.Controls.Add(CreateButton(L.T("新建"), delegate { if (DiscardOrSave()) LoadProfile(new Profile { Name = L.T("新配置") }, null); }, 66));
            p.Controls.Add(CreateButton(L.T("导入 JSON"), delegate { Import(); }, 106));
            p.Controls.Add(CreateButton(L.T("导出 JSON"), delegate { Export(); }, 106));
            p.Controls.Add(CreateButton(L.T("配置目录"), delegate { System.Diagnostics.Process.Start("explorer.exe", "\"" + folder + "\""); }, 100));
            profileBody.Controls.Add(p, 0, 1);
            p = Flow(); label = FluentUi.Text(L.T("配置名称"), 9.5F, true); label.Padding = new Padding(0, 9, 12, 0); p.Controls.Add(label);
            name.MaxLength = 80; name.AccessibleName = L.T("配置名称");
            FluentTextField nameField = new FluentTextField(name) { Width = 255, Height = 36, Margin = new Padding(0, 3, 12, 0) };
            name.TextChanged += Changed; p.Controls.Add(nameField);
            p.Controls.Add(CreateButton(L.T("保存配置"), delegate { SaveProfile(); }, 106));
            label = FluentUi.Text(L.T("改名后保存会新建一份，原配置保留。"), 9F, true); label.Padding = new Padding(8, 10, 0, 0); p.Controls.Add(label);
            profileBody.Controls.Add(p, 0, 2); profileCard.Controls.Add(profileBody); settings.Controls.Add(profileCard, 0, 0);

            FluentCard mappingCard = new FluentCard { Dock = DockStyle.Fill, Padding = new Padding(12, 10, 12, 8), Margin = new Padding(0, 0, 0, 4) };
            TableLayoutPanel mapping = new FluentTableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = new Padding(0) };
            mapping.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); mapping.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Label mappingTitle = FluentUi.Text(L.T("键位映射"), 12F, false); mappingTitle.Padding = new Padding(4, 0, 0, 0); mapping.Controls.Add(mappingTitle, 0, 0);
            grid.Dock = DockStyle.Fill; grid.Margin = new Padding(0); grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false; grid.MultiSelect = false; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.RowHeadersVisible = false; grid.AutoGenerateColumns = false; grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            FluentUi.ConfigureGrid(grid);
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "number", HeaderText = L.T("序号"), ReadOnly = true, FillWeight = 42 });
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "enabled", HeaderText = L.T("启用"), FillWeight = 48 });
            DataGridViewComboBoxColumn key = new DataGridViewComboBoxColumn { Name = "key", HeaderText = L.T("触发按键"), FillWeight = 105, FlatStyle = FlatStyle.Flat };
            key.Items.Add(""); foreach (string k in KeyCatalog.Keys.Keys) key.Items.Add(k); grid.Columns.Add(key);
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "x", HeaderText = L.T("屏幕 X"), FillWeight = 80 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "y", HeaderText = L.T("屏幕 Y"), FillWeight = 80 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "label", HeaderText = L.T("备注"), FillWeight = 205 });
            grid.Columns.Add(new DataGridViewButtonColumn { Name = "custom", HeaderText = L.T("个性化"), FillWeight = 115 });
            for (int i = 0; i < 15; i++) grid.Rows.Add(i + 1, false, "", 0, 0, "");
            grid.CellContentClick += delegate(object sender, DataGridViewCellEventArgs e)
            { if (e.RowIndex >= 0 && e.ColumnIndex == grid.Columns["custom"].Index) EditMacro(e.RowIndex); };
            grid.CellValueChanged += Changed;
            grid.CurrentCellDirtyStateChanged += delegate { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            grid.DataError += delegate(object sender, DataGridViewDataErrorEventArgs e) { e.ThrowException = false; status.Text = L.T("表格值无效，请重新输入。"); };
            mapping.Controls.Add(grid, 0, 1); mappingCard.Controls.Add(mapping); settings.Controls.Add(mappingCard, 0, 1);

            p = Flow(); p.Padding = new Padding(0, 5, 0, 0);
            p.Controls.Add(CreateButton(L.T("录入选中行按键"), delegate { captureRow = SelectedRow(); worker.CaptureKey(); }, 152));
            p.Controls.Add(CreateButton(L.T("准备取点（F6）"), delegate
            {
                captureRow = SelectedRow(); worker.Stop(L.T("移到目标应用，把鼠标放到目标位置，按 F6 取点。"));
                picked.Text = L.F("待取点：第 {0} 行；切换到目标应用后按 F6。", captureRow + 1);
            }, 148));
            p.Controls.Add(CreateButton(L.T("个性化设置"), delegate { EditMacro(SelectedRow()); }, 118));
            p.Controls.Add(CreateButton(L.T("清空选中行"), delegate
            {
                int i = SelectedRow(); grid.Rows[i].SetValues(i + 1, false, "", 0, 0, ""); grid.Rows[i].Tag = null; UpdateCustom(grid.Rows[i]); Changed(this, EventArgs.Empty);
            }, 118));
            picked.AutoSize = true; picked.Padding = new Padding(4, 11, 0, 0); picked.Font = FluentUi.Font(9F, FontStyle.Regular); picked.Tag = "muted"; p.Controls.Add(picked);
            settings.Controls.Add(p, 0, 2);

            FluentCard behaviorCard = new FluentCard { Dock = DockStyle.Fill, Padding = new Padding(16, 10, 16, 8), Margin = new Padding(0, 0, 0, 6) };
            TableLayoutPanel behavior = new FluentTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0) };
            behavior.RowStyles.Add(new RowStyle(SizeType.Absolute, 35)); behavior.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            FlowLayoutPanel toggles = Flow(); suppress.Text = L.T("拦截宏按键（不传给目标应用）"); suppress.Width = 340;
            pauseFocus.Text = L.T("切换前台窗口时自动暂停"); pauseFocus.Width = L.Language == "en" ? 380 : 310;
            suppress.Margin = pauseFocus.Margin = new Padding(0, 0, 24, 0);
            suppress.CheckedChanged += Changed; pauseFocus.CheckedChanged += Changed;
            toggles.Controls.Add(suppress); toggles.Controls.Add(pauseFocus); behavior.Controls.Add(toggles, 0, 0);
            Label behaviorHint = FluentUi.Text(L.T("新键接管只移动、不松左键；短按完成个性化序列。每组最多 15 个宏，坐标为桌面物理像素。"), 9F, true);
            behaviorHint.AutoSize = false; behaviorHint.Dock = DockStyle.Fill;
            behavior.Controls.Add(behaviorHint, 0, 1);
            behaviorCard.Controls.Add(behavior); settings.Controls.Add(behaviorCard, 0, 3);

            p = Flow(); p.Padding = new Padding(0, 10, 0, 0);
            start.Text = L.T("3 秒后启用并最小化"); start.Width = 218; start.Height = 38; start.Margin = new Padding(0, 3, 10, 3);
            start.Click += delegate
            {
                if (countdown > 0) { CancelCountdown(); worker.Stop(L.T("已取消启用倒计时。")); return; }
                try { GetProfile(); countdownVersion = worker.NewStartRequest(); countdown = 3; startAt = DateTime.UtcNow.AddSeconds(3); status.Text = L.T("请切换到目标应用，3 秒后启用……"); }
                catch (Exception ex) { Error(ex.Message); }
            };
            p.Controls.Add(start);
            FluentButton stop = (FluentButton)CreateButton(L.T("立即停止 · F12"), delegate { EmergencyStop(); }, 170); stop.Tone = FluentButtonTone.Danger; p.Controls.Add(stop);
            p.Controls.Add(CreateButton(L.T("最小化到托盘"), delegate { WindowState = FormWindowState.Minimized; }, 152));
            label = FluentUi.Text(L.T("切换到目标应用后，也可按 F8 启用。"), 9F, true); label.Padding = new Padding(8, 13, 0, 0); p.Controls.Add(label); root.Controls.Add(p, 0, 2);
            cursor.Dock = DockStyle.Fill; cursor.Padding = new Padding(0, 3, 0, 0); cursor.Font = FluentUi.Font(9F, FontStyle.Regular); cursor.Tag = "muted";
            root.Controls.Add(cursor, 0, 3);
            FluentCard statusCard = new FluentCard { Dock = DockStyle.Fill, Padding = new Padding(12, 2, 12, 2), CornerRadius = 6, Margin = new Padding(0) };
            status.Dock = DockStyle.Fill; status.TextAlign = ContentAlignment.MiddleLeft; status.BackColor = Color.Transparent; status.Font = FluentUi.Font(9F, FontStyle.Regular);
            status.Text = L.T("已暂停。先配置按键和坐标，再切到目标应用按 F8。");
            statusCard.Controls.Add(status); root.Controls.Add(statusCard, 0, 4);
        }
        private void ConfigureTray()
        {
            tray.Icon = Icon; tray.Text = L.T("KeyHold：已暂停"); tray.Visible = true;
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add(L.T("打开配置（自动暂停）"), null, delegate { ShowSettings(); });
            menu.Items.Add(L.T("紧急停止 F12"), null, delegate { EmergencyStop(); });
            menu.Items.Add(L.T("退出"), null, delegate { Close(); });
            tray.ContextMenuStrip = menu; tray.DoubleClick += delegate { ShowSettings(); };
        }
        private void ShowSettings()
        {
            EmergencyStop(); Show(); WindowState = FormWindowState.Normal; Activate();
        }
        private void ChangeLanguage()
        {
            string requested = language.SelectedIndex == 1 ? "en" : "zh-CN";
            if (requested == L.Language) return;
            if (previewOnly) { NextLanguage = requested; return; }
            EmergencyStop();
            if (!DiscardOrSave()) { language.SelectedIndex = L.Language == "en" ? 1 : 0; return; }
            try { L.SavePreference(L.PreferencePath, requested); }
            catch (Exception ex)
            {
                language.SelectedIndex = L.Language == "en" ? 1 : 0;
                Error(L.T("语言设置无法保存：") + ex.Message); return;
            }
            NextLanguage = requested;
            Close();
        }
        private void SetRunning(bool value)
        {
            settings.Enabled = !value; start.Enabled = !value;
            tray.Text = value ? L.T("KeyHold：运行中（F12 停止）") : L.T("KeyHold：已暂停");
            runState.Text = value ? L.T("●  运行中") : L.T("●  已暂停");
            runState.ForeColor = value ? Palette.Success : Palette.Muted;
            status.ForeColor = value ? Palette.Success : Palette.Text;
        }
        private void Tick(object sender, EventArgs args)
        {
            Native.POINT p;
            // Keep processing worker events while hidden, but do not repaint an
            // invisible window or repeatedly format an unchanged cursor label.
            if (Visible && WindowState != FormWindowState.Minimized && Native.GetCursorPos(out p))
                UpdateCursor(p.x, p.y, Native.Desktop);
            WorkerEvent e;
            while (worker.Events.TryDequeue(out e))
            {
                if (e.Kind == "start")
                {
                    CancelCountdown();
                    if (editing != null) { worker.Stop(L.T("请先关闭个性化设置窗口，再按 F8 启用。")); continue; }
                    try { worker.Start(GetProfile(), e.Window, e.StartVersion); }
                    catch (Exception ex) { Error(ex.Message); }
                }
                else if (e.Kind == "running") { SetRunning(true); status.Text = e.Text; }
                else if (e.Kind == "stopped") { CancelCountdown(); SetRunning(false); status.Text = e.Text; if (e.Text.Contains("F12")) actionPoint = null; }
                else if (e.Kind == "error") { actionPoint = null; CancelCountdown(); SetRunning(false); Error(e.Text); }
                else if (e.Kind == "point")
                {
                    if (actionPoint != null)
                    {
                        Action<int, int> receive = actionPoint; actionPoint = null;
                        receive(e.X, e.Y); SetRunning(false); continue;
                    }
                    SetRunning(false); int row = SelectedRow();
                    grid.Rows[row].Cells["x"].Value = e.X; grid.Rows[row].Cells["y"].Value = e.Y;
                    picked.Text = L.F("第 {0} 行已取点：({1}, {2})", row + 1, e.X, e.Y);
                    Changed(this, EventArgs.Empty);
                    tray.ShowBalloonTip(1500, L.T("已读取鼠标坐标"), picked.Text, ToolTipIcon.Info);
                }
                else if (e.Kind == "key")
                {
                    foreach (KeyValuePair<string, int> k in KeyCatalog.Keys)
                        if (k.Value == e.Key) { grid.Rows[captureRow].Cells["key"].Value = k.Key; break; }
                    grid.Rows[captureRow].Cells["enabled"].Value = true;
                    status.Text = L.F("第 {0} 行按键已录入。", captureRow + 1);
                }
            }
            if (countdown > 0)
            {
                int seconds = Math.Max(0, (int)Math.Ceiling((startAt - DateTime.UtcNow).TotalSeconds));
                start.Text = L.F("倒计时 {0} 秒（点击取消）", seconds);
                if (seconds == 0)
                {
                    CancelCountdown();
                    try
                    {
                        IntPtr target = Native.GetForegroundWindow();
                        worker.Start(GetProfile(), target, countdownVersion);
                        WindowState = FormWindowState.Minimized;
                    }
                    catch (Exception ex) { Error(ex.Message); }
                }
            }
        }
        private void UpdateCursor(int x, int y, Rectangle desktop)
        {
            Point point = new Point(x, y);
            if (lastCursor == point && lastDesktop == desktop) return;
            lastCursor = point; lastDesktop = desktop;
            cursor.Text = L.F("当前鼠标坐标：X = {0}    Y = {1}    |    当前桌面：{2}", x, y, desktop);
        }
        private void CancelCountdown() { countdown = 0; start.Text = L.T("3 秒后启用并最小化"); }
        public void EmergencyStop()
        {
            CancelCountdown(); if (worker != null) worker.Stop(L.T("已停止并请求释放鼠标。"));
        }
        private void SessionChanged(object sender, SessionSwitchEventArgs e) { worker.Stop(L.T("会话发生变化，已暂停。")); }
        private void PowerChanged(object sender, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Suspend) worker.Stop(L.T("系统休眠，已暂停。")); }
        private int SelectedRow() { return grid.CurrentCell == null ? 0 : grid.CurrentCell.RowIndex; }
        private static void UpdateCustom(DataGridViewRow row)
        {
            Macro m = row.Tag as Macro;
            row.Cells["custom"].Value = m == null || (m.MouseMode == "Hold" && m.BeforeActions.Count + m.AfterActions.Count == 0)
                ? L.T("设置") : (m.MouseMode == "Click" ? L.T("点击") : L.T("按住")) + " · " + m.BeforeActions.Count + "/" + m.AfterActions.Count;
        }
        private void EditMacro(int rowIndex)
        {
            try
            {
                EmergencyStop(); grid.EndEdit();
                DataGridViewRow row = grid.Rows[rowIndex];
                string key = Convert.ToString(row.Cells["key"].Value); int vk, x, y;
                if (!KeyCatalog.TryGet(key, out vk)) throw new ArgumentException(L.T("请先为该行选择宏按键。"));
                if (!Int32.TryParse(Convert.ToString(row.Cells["x"].Value), out x) || !Int32.TryParse(Convert.ToString(row.Cells["y"].Value), out y))
                    throw new ArgumentException(L.T("请先填写有效的原坐标。"));
                Macro current = row.Tag as Macro; current = current == null ? new Macro() : current.Clone();
                current.Key = key; current.X = x; current.Y = y;
                using (MacroEditor dialog = new MacroEditor(current, delegate(Action<int, int> receive)
                {
                    worker.Stop(L.T("请切换到目标应用，按 F6 为个性化动作取点。")); actionPoint = receive;
                }, delegate { actionPoint = null; }))
                {
                    editing = dialog;
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    { row.Tag = dialog.Result; UpdateCustom(row); Changed(this, EventArgs.Empty); }
                }
            }
            catch (Exception ex) { Error(ex.Message); }
            finally { editing = null; actionPoint = null; }
        }
        private void Changed(object sender, EventArgs e)
        {
            if (loading) return; dirty = true;
            Text = "KeyHold · " + name.Text + " *";
        }
        private Profile GetProfile()
        {
            grid.EndEdit(); Validate();
            Profile p = new Profile { Name = name.Text.Trim(), SuppressKeys = suppress.Checked, PauseOnFocusLoss = pauseFocus.Checked };
            for (int i = 0; i < 15; i++)
            {
                DataGridViewRow r = grid.Rows[i]; string key = Convert.ToString(r.Cells["key"].Value);
                bool enabled = Convert.ToBoolean(r.Cells["enabled"].Value);
                if (String.IsNullOrEmpty(key))
                {
                    if (enabled) throw new ArgumentException(L.F("第 {0} 行已启用，请先选择宏按键。", i + 1));
                    continue;
                }
                int x, y;
                if (!Int32.TryParse(Convert.ToString(r.Cells["x"].Value), out x) || !Int32.TryParse(Convert.ToString(r.Cells["y"].Value), out y))
                    throw new ArgumentException(L.F("第 {0} 行坐标必须是整数。", i + 1));
                Macro m = r.Tag as Macro; m = m == null ? new Macro() : m.Clone();
                m.Enabled = enabled; m.Key = key; m.X = x; m.Y = y; m.Label = Convert.ToString(r.Cells["label"].Value);
                p.Macros.Add(m);
            }
            p.Validate(); return p;
        }
        private void LoadProfile(Profile p, string path)
        {
            p.Validate(); loading = true;
            try
            {
                name.Text = p.Name; suppress.Checked = p.SuppressKeys; pauseFocus.Checked = p.PauseOnFocusLoss;
                for (int i = 0; i < 15; i++)
                {
                    if (i < p.Macros.Count)
                    { Macro m = p.Macros[i]; grid.Rows[i].SetValues(i + 1, m.Enabled, m.Key, m.X, m.Y, m.Label); grid.Rows[i].Tag = m.Clone(); }
                    else { grid.Rows[i].SetValues(i + 1, false, "", 0, 0, ""); grid.Rows[i].Tag = null; }
                    UpdateCustom(grid.Rows[i]);
                }
                currentPath = path; loadedName = p.Name; dirty = false;
                Text = "KeyHold · " + p.Name;
                if (path != null) Remember(path);
            }
            finally { loading = false; }
        }
        private void RefreshProfiles()
        {
            profiles.Items.Clear();
            foreach (string path in Directory.GetFiles(folder, "*.json"))
            {
                try
                {
                    Profile p = ProfileStore.Read(path);
                    int index = profiles.Items.Add(new ProfileItem { Name = p.Name, Path = path });
                    if (String.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase)) profiles.SelectedIndex = index;
                }
                catch { /* A damaged file remains on disk and is not silently overwritten. */ }
            }
            if (profiles.SelectedIndex < 0 && profiles.Items.Count > 0) profiles.SelectedIndex = 0;
        }
        private void ReadSelected()
        {
            ProfileItem item = profiles.SelectedItem as ProfileItem;
            if (item == null || !DiscardOrSave()) return;
            try { LoadProfile(ProfileStore.Read(item.Path), item.Path); status.Text = L.T("已读取配置：") + name.Text; }
            catch (Exception ex) { Error(ex.Message); }
        }
        private bool SaveProfile()
        {
            try
            {
                Profile p = GetProfile(); string path;
                if (currentPath != null && p.Name == loadedName) path = currentPath;
                else
                {
                    string filename = p.Name;
                    foreach (char c in Path.GetInvalidFileNameChars()) filename = filename.Replace(c, '_');
                    filename = filename.Trim().TrimEnd('.');
                    // Prefix prevents reserved DOS device names such as CON or NUL.
                    path = Path.Combine(folder, "profile_" + filename + ".json");
                    if (File.Exists(path) && MessageBox.Show(L.T("该名称对应的配置已存在，是否覆盖？"), L.T("保存配置"), MessageBoxButtons.YesNo) != DialogResult.Yes) return false;
                }
                ProfileStore.Write(path, p); currentPath = path; loadedName = p.Name; dirty = false;
                Remember(path); RefreshProfiles(); Text = "KeyHold · " + p.Name;
                status.Text = L.T("配置已保存：") + p.Name; return true;
            }
            catch (Exception ex) { Error(ex.Message); return false; }
        }
        private bool DiscardOrSave()
        {
            grid.EndEdit(); if (!dirty) return true;
            DialogResult r = MessageBox.Show(L.T("当前配置有修改，是否先保存？"), L.T("未保存的配置"), MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return false;
            return r == DialogResult.No || SaveProfile();
        }
        private void Import()
        {
            if (!DiscardOrSave()) return;
            using (OpenFileDialog d = new OpenFileDialog { Filter = L.T("JSON 配置 (*.json)|*.json"), Title = L.T("导入配置") })
                if (d.ShowDialog(this) == DialogResult.OK)
                    try { LoadProfile(ProfileStore.Read(d.FileName), null); dirty = true; status.Text = L.T("已导入；点击保存配置后可反复读取。"); }
                    catch (Exception ex) { Error(ex.Message); }
        }
        private void Export()
        {
            try
            {
                Profile p = GetProfile();
                using (SaveFileDialog d = new SaveFileDialog { Filter = L.T("JSON 配置 (*.json)|*.json"), FileName = "macro-profile.json", Title = L.T("导出配置") })
                    if (d.ShowDialog(this) == DialogResult.OK) { ProfileStore.Write(d.FileName, p); status.Text = L.T("配置已导出。"); }
            }
            catch (Exception ex) { Error(ex.Message); }
        }
        private void Remember(string path)
        {
            try { File.WriteAllText(Path.Combine(folder, "last-profile.txt"), Path.GetFileName(path)); } catch { }
        }
        private void TryLoadLast()
        {
            try
            {
                string marker = Path.Combine(folder, "last-profile.txt");
                if (!File.Exists(marker)) return;
                string file = File.ReadAllText(marker).Trim();
                if (Path.GetFileName(file) != file) return;
                string path = Path.Combine(folder, file);
                if (File.Exists(path)) { LoadProfile(ProfileStore.Read(path), path); RefreshProfiles(); }
            }
            catch (Exception ex) { status.Text = L.T("上次配置无法读取，请重新选择或导入：") + ex.Message; }
        }
        private void Error(string message)
        {
            status.Text = message; status.ForeColor = Palette.Danger;
            tray.ShowBalloonTip(4000, L.T("KeyHold：需要处理"), message, ToolTipIcon.Warning);
            if (Visible && WindowState != FormWindowState.Minimized)
                MessageBox.Show(this, message, "KeyHold", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (closing) return;
            EmergencyStop();
            if (NextLanguage == null && e.CloseReason == CloseReason.UserClosing && !DiscardOrSave()) { e.Cancel = true; return; }
            closing = true; timer.Stop();
            SystemEvents.SessionSwitch -= SessionChanged; SystemEvents.PowerModeChanged -= PowerChanged;
            worker.Dispose(); tray.Visible = false; tray.Dispose(); timer.Dispose();
        }
    }
}
