using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace KeyHoldMacro;

public sealed partial class MainWindow : Window, IDisposable
{
    private readonly IntPtr hwnd;
    private readonly DispatcherQueueTimer cursorTimer;
    private readonly DispatcherQueueTimer countdownTimer;
    private readonly Stopwatch countdown = new();
    private readonly List<MappingRow> rows = new();
    private readonly Forms.NotifyIcon tray = new();
    private readonly Forms.ContextMenuStrip trayMenu = new();
    private readonly System.Drawing.Icon icon;
    private readonly FeedbackAudio audio = new();
    private readonly KeyboardWorker worker;
    private readonly WorkerNotifications notifications;
    private MacroEditor editor;
    private Action<int, int> actionPoint;
    private int selectedRow, recordingRow, countdownVersion, previewSoundIndex;
    private int? pendingPointRow;
    private bool loading, dirty, busy, running, disposed, allowClose;
    private bool shown = true, soundOn;
    private System.Drawing.Point? cursorPoint;
    private System.Drawing.Rectangle cursorDesktop;

    internal MainWindow()
    {
        InitializeComponent();
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        cursorTimer = DispatcherQueue.CreateTimer();
        cursorTimer.Interval = TimeSpan.FromMilliseconds(40);
        cursorTimer.Tick += (_, _) => RefreshCursor();
        countdownTimer = DispatcherQueue.CreateTimer();
        countdownTimer.Tick += (_, _) => TickCountdown();

        try
        {
            ConfigureWindow();
            using var iconStream = typeof(MainWindow).Assembly.GetManifestResourceStream("KeyHold.AppIcon");
            icon = new System.Drawing.Icon(iconStream);
            tray.Icon = icon;
            tray.ContextMenuStrip = trayMenu;
            tray.MouseDoubleClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) OpenFromTray(); };

            soundOn = FeedbackPreference.Read(FeedbackPreference.PathName);
            SoundEnabled.IsChecked = soundOn;
            Localize();
            LoadProfile(new Profile(), null);
            Directory.CreateDirectory(profileFolder);
            RefreshProfiles();
            TryLoadLast();
            WireCommands();

            worker = new KeyboardWorker();
            notifications = new WorkerNotifications(DrainWorkerEvents);
            worker.SetNotificationWindow(notifications.Handle);
            SystemEvents.SessionSwitch += SessionChanged;
            SystemEvents.PowerModeChanged += PowerChanged;
            tray.Visible = true;
            RefreshCursor();
            cursorTimer.Start();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    private void ConfigureWindow()
    {
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "assets", "KeyHold.ico"));
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        double scale = GetDpiForWindow(hwnd) / 96.0;
        int width = Math.Min((int)(1180 * scale), work.Width - 24);
        int height = Math.Min((int)(900 * scale), work.Height - 24);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = Math.Min((int)(850 * scale), width);
            presenter.PreferredMinimumHeight = Math.Min((int)(560 * scale), height);
        }
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));
        AppWindow.Changed += (_, _) =>
        {
            if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
                HideToTray();
        };
        AppWindow.Closing += (_, e) =>
        {
            if (allowClose) return;
            e.Cancel = true;
            EmergencyStop(false);
            DispatcherQueue.TryEnqueue(RequestClose);
        };
        Closed += (_, _) => Dispose();
    }

    private void Localize()
    {
        Shell.Language = L.Language;
        Heading.Text = L.T("宏配置");
        Shortcuts.Text = L.T("F6 取点   ·   F8 启停   ·   F12 急停");
        LanguageChoice.SelectedIndex = L.Language == "en" ? 1 : 0;
        ProfilesHeading.Text = L.T("配置文件");
        SavedProfiles.Header = L.T("已存配置");
        ProfileName.Header = L.T("配置名称");
        SaveHint.Text = L.T("改名后保存会新建一份，原配置保留。");
        LoadButton.Content = L.T("读取");
        SaveButton.Content = L.T("保存配置");
        NewButton.Content = L.T("新建");
        ImportButton.Content = L.T("导入 JSON");
        ExportButton.Content = L.T("导出 JSON");
        FolderButton.Content = L.T("配置目录");
        SuppressKeys.Content = L.T("拦截宏按键（不传给目标应用）");
        PauseOnFocus.Content = L.T("切换前台窗口时自动暂停");
        SoundEnabled.Content = L.T("状态提示音");
        TestSoundButton.Content = L.T("试听提示音");
        MappingHeading.Text = L.T("键位映射");
        MappingHint.Text = L.T("新键接管只移动、不松左键；短按完成个性化序列。每组最多 15 个宏，坐标为桌面物理像素。");
        RecordButton.Content = L.T("录入选中行按键");
        CaptureButton.Content = L.T("准备取点（F6）");
        CustomizeButton.Content = L.T("个性化设置");
        ClearButton.Content = L.T("清空选中行");
        StopButton.Content = L.T("立即停止 · F12");
        TrayButton.Content = L.T("最小化到托盘");
        Picked.Text = "";
        Status.Text = L.T("已暂停。先配置按键和坐标，再切到目标应用按 F8。");
        cursorPoint = null;
        RefreshCursor();
        CancelCountdown();

        if (rows.Count == 0)
        {
            Mappings.Children.Add(MappingRow.Header());
            for (int i = 0; i < 15; i++)
            {
                var row = new MappingRow(i, SelectRow, MarkDirty, QueueEditMacro, (Style)Root.Resources["CommandButton"]);
                rows.Add(row);
                Mappings.Children.Add(row);
            }
            SelectRow(0);
        }
        MappingRow.LocalizeHeader((Grid)Mappings.Children[0]);
        AutomationProperties.SetName(Mappings, L.T("键位映射"));
        foreach (var row in rows) row.Localize();

        trayMenu.Items.Clear();
        trayMenu.Items.Add(L.T("打开配置（自动暂停）"), null, (_, _) => OpenFromTray());
        trayMenu.Items.Add(L.T("紧急停止 F12"), null, (_, _) => EmergencyStop(true));
        trayMenu.Items.Add(L.T("退出"), null, (_, _) => RequestClose());
        SetRunning(false);
    }

    private void WireCommands()
    {
        LoadButton.Click += (_, _) => RunCommand(ReadSelected);
        SaveButton.Click += (_, _) => RunCommand(async () => { await SaveProfile(); });
        NewButton.Click += (_, _) => RunCommand(async () =>
        {
            if (await DiscardOrSave()) LoadProfile(new Profile { Name = L.T("新配置") }, null);
        });
        ImportButton.Click += (_, _) => RunCommand(Import);
        ExportButton.Click += (_, _) => RunCommand(Export);
        FolderButton.Click += (_, _) => RunCommand(() =>
        {
            Process.Start(new ProcessStartInfo(profileFolder) { UseShellExecute = true });
            return Task.CompletedTask;
        });
        LanguageChoice.SelectionChanged += (_, _) =>
        {
            if (!loading) DispatcherQueue.TryEnqueue(() => RunCommand(ChangeLanguage));
        };
        ProfileName.TextChanging += (_, _) => MarkDirty();
        SuppressKeys.Checked += (_, _) => MarkDirty();
        SuppressKeys.Unchecked += (_, _) => MarkDirty();
        PauseOnFocus.Checked += (_, _) => MarkDirty();
        PauseOnFocus.Unchecked += (_, _) => MarkDirty();
        SoundEnabled.Checked += (_, _) => ChangeSound();
        SoundEnabled.Unchecked += (_, _) => ChangeSound();
        TestSoundButton.Click += (_, _) => PreviewSound();
        CustomizeButton.Click += (_, _) => QueueEditMacro(selectedRow);
        ClearButton.Click += (_, _) => { rows[selectedRow].Load(null); MarkDirty(); };
        CaptureButton.Click += (_, _) =>
        {
            CancelCountdown();
            pendingPointRow = selectedRow;
            worker.Stop(WorkerReason.PreparePoint);
            Picked.Text = L.F("待取点：第 {0} 行；切换到目标应用后按 F6。", selectedRow + 1);
        };
        RecordButton.Click += (_, _) =>
        {
            CancelCountdown();
            recordingRow = selectedRow;
            try { worker.CaptureKey(); }
            catch (Exception ex) { ReportError(ex.Message); }
        };
        StartButton.Click += (_, _) => StartCountdown();
        StopButton.Click += (_, _) => EmergencyStop(true);
        TrayButton.Click += (_, _) => HideToTray();
    }

    private void SetRunning(bool value)
    {
        running = value;
        RunState.Text = value ? L.T("●  运行中") : L.T("●  已暂停");
        tray.Text = value ? L.T("KeyHold：运行中（F12 停止）") : L.T("KeyHold：已暂停");
        UpdateCommandAvailability();
    }

    private void UpdateCommandAvailability()
    {
        bool editable = !running && !busy;
        foreach (Control control in new Control[] { SavedProfiles, ProfileName, LoadButton, SaveButton, NewButton,
            ImportButton, ExportButton, FolderButton, SuppressKeys, PauseOnFocus, SoundEnabled, TestSoundButton,
            RecordButton, CaptureButton, CustomizeButton, ClearButton, LanguageChoice, StartButton })
            control.IsEnabled = editable;
        foreach (var row in rows) row.SetEditable(editable);
    }

    private async void RunCommand(Func<Task> command)
    {
        if (disposed || busy || running || editor != null) return;
        busy = true;
        if (countdownTimer.IsRunning)
        {
            CancelCountdown();
            worker.Stop(WorkerReason.CancelCountdown);
        }
        UpdateCommandAvailability();
        try { await command(); }
        catch (Exception ex) { ReportError(ex.Message); }
        finally
        {
            busy = false;
            if (!disposed) UpdateCommandAvailability();
        }
    }

    private void HideToTray()
    {
        if (!shown || busy || editor != null) return;
        shown = false;
        cursorTimer.Stop();
        AppWindow.Hide();
    }

    private void OpenFromTray()
    {
        EmergencyStop(false);
        if (editor != null) { editor.Activate(); return; }
        RestoreWindow();
    }

    private void RestoreWindow()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        shown = true;
        AppWindow.Show();
        Activate();
        RefreshCursor();
        cursorTimer.Start();
    }

    private async void RequestClose()
    {
        if (disposed || busy || editor != null) return;
        EmergencyStop(false);
        if (dirty && !shown) RestoreWindow();
        busy = true;
        UpdateCommandAvailability();
        try
        {
            if (!await DiscardOrSave()) return;
            allowClose = true;
            Close();
        }
        catch (Exception ex) { ReportError(ex.Message); }
        finally
        {
            busy = false;
            if (!disposed) UpdateCommandAvailability();
        }
    }

    private void RefreshCursor()
    {
        if (!shown || !Native.GetCursorPos(out var point)) return;
        var current = new System.Drawing.Point(point.x, point.y);
        var desktop = Native.Desktop;
        if (current == cursorPoint && desktop == cursorDesktop) return;
        cursorPoint = current;
        cursorDesktop = desktop;
        CursorPosition.Text = L.F("当前鼠标坐标：X = {0}    Y = {1}    |    当前桌面：{2}", current.X, current.Y, desktop);
    }

    private void SelectRow(int index)
    {
        if (pendingPointRow is int pending && pending != index)
        {
            pendingPointRow = null;
            Picked.Text = "";
        }
        selectedRow = index;
        rows[index].Select();
    }

    // Return from the WinUI button callback before entering the editor's modal loop.
    private void QueueEditMacro(int index) => DispatcherQueue.TryEnqueue(() => EditMacro(index));

    private void EditMacro(int index)
    {
        if (running || busy || editor != null) return;
        try
        {
            EmergencyStop(false);
            SelectRow(index);
            var macro = rows[index].Read(forEditor: true);
            if (macro == null) throw new ArgumentException(L.T("请先为该行选择宏按键。"));
            using var dialog = new MacroEditor(macro, receive =>
            {
                worker.Stop(WorkerReason.ActionPoint);
                actionPoint = receive;
            }, () => actionPoint = null);
            editor = dialog;
            // Keep the independent editor modal, with the actual WinUI HWND as owner.
            if (dialog.ShowDialog(new WindowOwner(hwnd)) == Forms.DialogResult.OK)
            {
                rows[index].Load(dialog.Result);
                MarkDirty();
            }
        }
        catch (Exception ex) { ReportError(ex.Message); }
        finally { editor = null; actionPoint = null; }
    }

    private sealed record WindowOwner(IntPtr Handle) : Forms.IWin32Window;

    private void StartCountdown()
    {
        if (countdownTimer.IsRunning)
        {
            CancelCountdown();
            worker.Stop(WorkerReason.CancelCountdown);
            return;
        }
        try
        {
            GetProfile();
            pendingPointRow = null;
            countdownVersion = worker.NewStartRequest();
            countdown.Restart();
            countdownTimer.Interval = TimeSpan.FromSeconds(1);
            countdownTimer.Start();
            TickCountdown();
            Status.Text = L.T("请切换到目标应用，3 秒后启用……");
        }
        catch (Exception ex) { ReportError(ex.Message); }
    }

    private void TickCountdown()
    {
        if (!countdownTimer.IsRunning) return;
        long remaining = 3000 - countdown.ElapsedMilliseconds;
        if (remaining > 0)
        {
            int seconds = (int)((remaining + 999) / 1000);
            StartButton.Content = L.F("倒计时 {0} 秒（点击取消）", seconds);
            countdownTimer.Interval = TimeSpan.FromMilliseconds(remaining - (seconds - 1) * 1000);
            return;
        }
        CancelCountdown();
        if (countdownVersion != worker.StartVersion) return;
        try
        {
            worker.Start(GetProfile(), Native.GetForegroundWindow(), countdownVersion);
            HideToTray();
        }
        catch (Exception ex) { ReportError(ex.Message); }
    }

    private void CancelCountdown()
    {
        countdownTimer.Stop();
        countdown.Reset();
        StartButton.Content = L.T("3 秒后启用并最小化");
    }

    internal void EmergencyStop(bool confirm)
    {
        if (disposed) return;
        CancelCountdown();
        actionPoint = null;
        pendingPointRow = null;
        worker?.Stop(WorkerReason.Requested, confirm);
    }

    private void DrainWorkerEvents()
    {
        if (disposed) return;
        var feedback = new FeedbackBatch();
        while (worker.TryDequeue(out WorkerEvent e))
        {
            feedback.Add(e.Cue, e.StartVersion);
            switch (e.Kind)
            {
                case "start":
                    if (e.StartVersion != worker.StartVersion) break;
                    CancelCountdown();
                    if (editor != null || busy) { worker.Stop(WorkerReason.CloseEditor); break; }
                    try { worker.Start(GetProfile(), e.Window, e.StartVersion); }
                    catch (Exception ex) { ReportError(ex.Message); }
                    break;
                case "running":
                    if (e.StartVersion != worker.StartVersion) break;
                    SetRunning(true);
                    Status.Text = e.Text;
                    break;
                case "stopped":
                    CancelCountdown();
                    SetRunning(false);
                    Status.Text = e.Text;
                    if (e.Reason == WorkerReason.F12) { actionPoint = null; pendingPointRow = null; }
                    break;
                case "point":
                    SetRunning(false);
                    if (actionPoint != null)
                    {
                        var receive = actionPoint;
                        actionPoint = null;
                        receive(e.X, e.Y);
                        break;
                    }
                    if (editor != null || busy) break;
                    int index = pendingPointRow ?? selectedRow;
                    pendingPointRow = null;
                    rows[index].SetPoint(e.X, e.Y);
                    Picked.Text = L.F("第 {0} 行已取点：({1}, {2})", index + 1, e.X, e.Y);
                    MarkDirty();
                    tray.ShowBalloonTip(1500, L.T("已读取鼠标坐标"), Picked.Text, Forms.ToolTipIcon.Info);
                    break;
                case "key":
                    if (editor != null || busy) break;
                    string key = KeyCatalog.Keys.FirstOrDefault(k => k.Value == e.Key).Key;
                    if (key == null) break;
                    rows[recordingRow].RecordKey(key);
                    Status.Text = L.F("第 {0} 行按键已录入。", recordingRow + 1);
                    break;
                case "error":
                    feedback.Clear();
                    CancelCountdown();
                    actionPoint = null;
                    pendingPointRow = null;
                    SetRunning(false);
                    ReportError(e.Text);
                    break;
            }
        }
        PlayFeedback(feedback.Take(worker.StartVersion), false);
    }

    private void SessionChanged(object sender, SessionSwitchEventArgs e) => worker.Stop(WorkerReason.SessionChanged);
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) worker.Stop(WorkerReason.Suspend);
    }

    private void ChangeSound()
    {
        if (loading) return;
        bool requested = SoundEnabled.IsChecked == true;
        try
        {
            FeedbackPreference.Write(FeedbackPreference.PathName, requested);
            soundOn = requested;
            if (!requested) audio.Stop();
        }
        catch (Exception ex)
        {
            loading = true;
            SoundEnabled.IsChecked = soundOn;
            loading = false;
            ReportError(L.T("提示音设置无法保存：") + ex.Message);
        }
    }

    private void PreviewSound()
    {
        string[] names = { "已启用", "暂停", "紧急停止", "错误" };
        FeedbackCue[] cues = { FeedbackCue.Enabled, FeedbackCue.Paused, FeedbackCue.Emergency, FeedbackCue.Error };
        int index = previewSoundIndex++ % cues.Length;
        Status.Text = L.F("试听：{0}（不会启用宏）。", L.T(names[index]));
        PlayFeedback(cues[index], true);
    }

    private void PlayFeedback(FeedbackCue cue, bool preview)
    {
        if (cue != FeedbackCue.None && (soundOn || preview) && !audio.Play(cue))
            Status.Text = L.T("提示音无法播放，请检查 Windows 音量和输出设备。");
    }

    private void ReportError(string message)
    {
        PlayFeedback(FeedbackCue.Error, false);
        Status.Text = message;
        tray.ShowBalloonTip(4000, L.T("KeyHold：需要处理"), message, Forms.ToolTipIcon.Warning);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        cursorTimer.Stop();
        countdownTimer.Stop();
        SystemEvents.SessionSwitch -= SessionChanged;
        SystemEvents.PowerModeChanged -= PowerChanged;
        worker?.Dispose();
        notifications?.Dispose();
        audio.Dispose();
        tray.Dispose();
        trayMenu.Dispose();
        icon?.Dispose();
    }
}
