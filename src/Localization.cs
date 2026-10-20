using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace KeyHoldMacro
{
    // Language is changed only between main-window lifetimes, after the worker
    // has been disposed. Translation never performs IO in input callbacks.
    internal static class L
    {
        private static string language = "zh-CN";
        internal static string Language
        {
            get { return language; }
            set
            {
                if (value != "zh-CN" && value != "en") throw new ArgumentException("Unsupported language.");
                language = value;
            }
        }
        internal static string PreferencePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeyHoldMacro", "language.txt"); }
        }
        internal static string ResolveLanguage(string culture, string preference)
        {
            if (preference == "zh-CN" || preference == "en") return preference;
            return culture != null && culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en";
        }
        internal static void Initialize(string path, string culture)
        {
            string preference = null;
            try { if (File.Exists(path)) preference = File.ReadAllText(path, Encoding.UTF8).Trim(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            Language = ResolveLanguage(culture, preference);
        }
        internal static void SavePreference(string path, string value)
        {
            if (value != "zh-CN" && value != "en") throw new ArgumentException("Unsupported language.");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            // This recoverable preference is only a few bytes. File.Replace
            // can be denied on EFS-managed user folders even when ordinary
            // content writes succeed. A partial file falls back to OS culture.
            File.WriteAllText(path, value, new UTF8Encoding(false));
        }
        internal static string T(string text)
        {
            string translated;
            return language == "en" && English.TryGetValue(text, out translated) ? translated : text;
        }
        internal static string F(string text, params object[] args)
        { return String.Format(CultureInfo.CurrentCulture, T(text), args); }

        private static readonly Dictionary<string, string> English = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "状态提示音", "Sound cues" },
            { "试听提示音", "Test sound" },
            { "提示音设置无法保存：", "Could not save sound preferences: " },
            { "提示音无法播放，请检查 Windows 音量和输出设备。", "Sound could not be played. Check the Windows volume and output device." },
            { "试听：{0}（不会启用宏）。", "Preview: {0} (macros stay paused)." },
            { "已启用", "Enabled" },
            { "暂停", "Paused" },
            { "紧急停止", "Emergency stop" },
            { "错误", "Error" },
            { "程序已在运行，请双击托盘图标打开。", "KeyHold is already running. Double-click its tray icon to open settings." },
            { "已请求停止宏。\r\n", "A macro stop has been requested.\r\n" },
            { "程序错误", "Application error" },
            { "KeyHold 启动失败", "KeyHold startup failed" },
            { "宏配置", "Macro settings" },
            { "让每个按键，按你的方式执行。", "Make every key work your way." },
            { "●  已暂停", "●  Paused" },
            { "F6 取点   ·   F8 启停   ·   F12 急停", "F6 Capture  ·  F8 Toggle  ·  F12 Stop" },
            { "配置文件", "Profiles" },
            { "已存配置", "Saved profile" },
            { "读取", "Load" },
            { "新建", "New" },
            { "新配置", "New profile" },
            { "导入 JSON", "Import JSON" },
            { "导出 JSON", "Export JSON" },
            { "配置目录", "Open folder" },
            { "配置名称", "Profile name" },
            { "保存配置", "Save profile" },
            { "改名后保存会新建一份，原配置保留。", "Saving a new name keeps the original profile." },
            { "键位映射", "Key mappings" },
            { "序号", "#" },
            { "启用", "On" },
            { "触发按键", "Trigger key" },
            { "屏幕 X", "Screen X" },
            { "屏幕 Y", "Screen Y" },
            { "备注", "Notes" },
            { "个性化", "Customize" },
            { "表格值无效，请重新输入。", "Invalid table value. Please enter it again." },
            { "录入选中行按键", "Record key" },
            { "准备取点（F6）", "Capture point (F6)" },
            { "移到目标应用，把鼠标放到目标位置，按 F6 取点。", "Switch to the target app, position the cursor, then press F6 to capture." },
            { "待取点：第 {0} 行；切换到目标应用后按 F6。", "Row {0}: switch to the target app and press F6." },
            { "个性化设置", "Customize key" },
            { "清空选中行", "Clear row" },
            { "拦截宏按键（不传给目标应用）", "Block macro keys in the target app" },
            { "切换前台窗口时自动暂停", "Pause on foreground app change" },
            { "新键接管只移动、不松左键；短按完成个性化序列。每组最多 15 个宏，坐标为桌面物理像素。", "New keys take over without releasing the mouse; short taps finish the sequence. Up to 15 macros. Coordinates: physical pixels." },
            { "3 秒后启用并最小化", "Start in 3s and minimize" },
            { "已取消启用倒计时。", "Start countdown canceled." },
            { "请切换到目标应用，3 秒后启用……", "Switch to the target app. Starting in 3 seconds..." },
            { "立即停止 · F12", "Stop now · F12" },
            { "最小化到托盘", "Minimize to tray" },
            { "切换到目标应用后，也可按 F8 启用。", "You can also switch to the target app and press F8." },
            { "已暂停。先配置按键和坐标，再切到目标应用按 F8。", "Paused. Set up keys and coordinates, then switch to the target app and press F8." },
            { "KeyHold：已暂停", "KeyHold: Paused" },
            { "打开配置（自动暂停）", "Open settings (pauses macros)" },
            { "紧急停止 F12", "Emergency stop F12" },
            { "退出", "Exit" },
            { "KeyHold：运行中（F12 停止）", "KeyHold: Running (F12 to stop)" },
            { "●  运行中", "●  Running" },
            { "当前鼠标坐标：X = {0}    Y = {1}    |    当前桌面：{2}", "Cursor: X = {0}    Y = {1}    |    Desktop: {2}" },
            { "请先关闭个性化设置窗口，再按 F8 启用。", "Close the customization window before pressing F8 to start." },
            { "第 {0} 行已取点：({1}, {2})", "Row {0} captured: ({1}, {2})" },
            { "已读取鼠标坐标", "Cursor coordinates captured" },
            { "第 {0} 行按键已录入。", "Key recorded for row {0}." },
            { "倒计时 {0} 秒（点击取消）", "Starting in {0}s (click to cancel)" },
            { "已停止并请求释放鼠标。", "Stopped; mouse release requested." },
            { "会话发生变化，已暂停。", "Paused because the Windows session changed." },
            { "系统休眠，已暂停。", "Paused because Windows is suspending." },
            { "设置", "Edit" },
            { "点击", "Click" },
            { "按住", "Hold" },
            { "请先为该行选择宏按键。", "Choose a macro key for this row first." },
            { "请先填写有效的原坐标。", "Enter valid main coordinates first." },
            { "请切换到目标应用，按 F6 为个性化动作取点。", "Switch to the target app and press F6 to capture this action point." },
            { "第 {0} 行已启用，请先选择宏按键。", "Row {0} is enabled. Choose a macro key first." },
            { "第 {0} 行坐标必须是整数。", "Coordinates in row {0} must be whole numbers." },
            { "已读取配置：", "Profile loaded: " },
            { "该名称对应的配置已存在，是否覆盖？", "A profile with this name already exists. Overwrite it?" },
            { "配置已保存：", "Profile saved: " },
            { "当前配置有修改，是否先保存？", "The current profile has unsaved changes. Save them first?" },
            { "未保存的配置", "Unsaved profile" },
            { "JSON 配置 (*.json)|*.json", "JSON profiles (*.json)|*.json" },
            { "导入配置", "Import profile" },
            { "已导入；点击保存配置后可反复读取。", "Imported. Save the profile to load it again later." },
            { "导出配置", "Export profile" },
            { "配置已导出。", "Profile exported." },
            { "上次配置无法读取，请重新选择或导入：", "The last profile could not be loaded. Select or import it again: " },
            { "KeyHold：需要处理", "KeyHold: Attention needed" },
            { "Windows 未接受键盘输入，请检查目标程序权限。", "Windows did not accept keyboard input. Check the target app permissions." },
            { "Windows 未完整接受鼠标输入。若目标程序以管理员运行，请以相同权限启动本程序。", "Windows did not accept all mouse input. If the target app runs as administrator, run KeyHold with the same privileges." },
            { "目标坐标 ({0}, {1}) 不在当前任何显示器内，请重新取点。", "Target ({0}, {1}) is outside all current displays. Capture the point again." },
            { "显示器布局已改变，请重新取点。", "The display layout has changed. Capture the point again." },
            { "KeyHold · {0} 个性化设置", "KeyHold · Customize {0}" },
            { "个性化 · {0}", "Customize · {0}" },
            { "为这个按键编排动作与节奏。", "Arrange the actions and timing for this key." },
            { "原坐标动作  ({0}, {1})", "Main action  ({0}, {1})" },
            { "到后置动作间隔 ms", "Gap before after-actions (ms)" },
            { "持续按住，随松键释放", "Hold until key release" },
            { "单次点击", "Single click" },
            { "前置动作  →  原坐标动作  →  后置动作\r\n短按会完成序列；新键、暂停或 F12 可取消。默认 40ms；30 帧约 33.33ms，10ms 需实测。", "Before-actions  →  Main action  →  After-actions\r\nShort taps finish; another key, pause or F12 cancels. Default: 40ms (30fps = 33.33ms/frame). Test 10ms gaps in-app." },
            { "前置动作（原坐标之前）", "Before-actions" },
            { "后置动作（原坐标之后）", "After-actions" },
            { "每组最多 10 个动作。选中坐标动作后可按 F6 在目标应用中取点，不会抢回焦点。", "Up to 10 actions per group. Select a coordinate action, then press F6 in the target app to capture without taking focus." },
            { "应用到该键位", "Apply to key" },
            { "取消", "Cancel" },
            { "按键点按", "Key tap" },
            { "点击坐标", "Click point" },
            { "移动坐标", "Move to point" },
            { "点击时长 ms", "Click duration (ms)" },
            { "短按点击时长 ms", "Short-tap duration (ms)" },
            { "按指定时长完成一次点击，再执行后置动作。间隔从动作结束开始计算。", "Click for the specified duration, then run after-actions. Gaps begin when each action finishes." },
            { "按住时随松键释放；若在前置延迟中已松键，按“短按点击时长”完成一次点击。后置独立点击请切换为单次点击。", "A hold releases on physical key-up. If released during before-actions, it becomes one timed short-tap click. Choose Single click for independent after-clicks." },
            { "没有前置延迟，无需设置短按点击时长。实际按住多久由实体松键决定。", "No before-action delay: short-tap duration is unused. The physical key release ends the hold." },
            { "动作", "Action" },
            { "按键", "Key" },
            { "按住时长 ms", "Duration (ms)" },
            { "结束后间隔 ms", "Gap after (ms)" },
            { "＋按键点按", "+ Key tap" },
            { "＋坐标点击", "+ Click point" },
            { "＋移动坐标", "+ Move point" },
            { "删除", "Delete" },
            { "上移", "Up" },
            { "下移", "Down" },
            { "为选中动作取点 F6", "Capture action point F6" },
            { "前置和后置分别最多 10 个动作。", "Before-actions and after-actions each allow up to 10 actions." },
            { "请先添加并选中一个坐标动作。", "Add and select a coordinate action first." },
            { "取点适用于坐标点击或移动动作。", "Point capture is available for click and move actions." },
            { "已取点：({0}, {1})；宏仍暂停。", "Captured: ({0}, {1}); macros remain paused." },
            { "待取点：切换到目标应用，把鼠标放到目标位置，按 F6。", "Switch to the target app, position the cursor, then press F6." },
            { "第 {0} 个动作的坐标、时长和间隔必须是整数。", "Coordinates, duration and gap for action {0} must be whole numbers." },
            { "请选择有效的动作类型。", "Select a valid action type." },
            { "KeyHold 个性化设置", "KeyHold customization" },
            { "键盘监听线程启动超时。", "The keyboard listener timed out during startup." },
            { "无法安装全局键盘钩子。", "Could not install the global keyboard hook." },
            { "后台监听线程已停止，请重新启动程序。", "The background listener has stopped. Restart KeyHold." },
            { "请先切换到目标应用，再按 F8 启用。", "Switch to the target app before pressing F8 to start." },
            { "运行中：", "Running: " },
            { "请按要绑定的单键；F12 取消录入。", "Press the single key to bind; F12 cancels recording." },
            { "已暂停：前台窗口发生变化。回到目标应用后按 F8。", "Paused: the foreground window changed. Return to the target app and press F8." },
            { "后台线程停止：", "Background listener stopped: " },
            { "已紧急停止并释放鼠标（F12）。", "Emergency stop: mouse released (F12)." },
            { "已暂停（F8）。", "Paused (F8)." },
            { "无法读取当前鼠标坐标。", "Could not read the current cursor coordinates." },
            { "已取点；宏保持暂停。按 F8 启用。", "Point captured; macros remain paused. Press F8 to start." },
            { "不支持的个性化动作。", "Unsupported custom action." },
            { "个性化动作包含不支持或保留的按键。", "A custom action uses an unsupported or reserved key." },
            { "动作坐标超出支持范围。", "Action coordinates are outside the supported range." },
            { "按住时长须为 1～60000 ms，动作间隔须为 0～60000 ms。", "Duration must be 1-60000ms; action gaps must be 0-60000ms." },
            { "原坐标动作模式无效。", "Invalid main action mode." },
            { "原坐标按住时长或后置间隔超出范围。", "The main action duration or following gap is out of range." },
            { "前置和后置动作分别最多 10 个，不能为空值。", "Before-actions and after-actions must each contain at most 10 non-null actions." },
            { "动作不能为空值。", "An action cannot be null." },
            { "后置坐标点击需要把原坐标模式改为“单次点击”；持续按住时可使用“移动坐标”。", "Independent after-clicks require Single click mode for the main action. Use Move to point with Hold mode." },
            { "默认配置", "Default profile" },
            { "不支持此配置版本。", "Unsupported profile version." },
            { "配置名称须为 1～80 个字符。", "Profile names must contain 1-80 characters." },
            { "每组配置最多 15 个宏。", "Each profile supports up to 15 macros." },
            { "配置包含不支持或保留的按键。", "The profile uses an unsupported or reserved key." },
            { "同一配置内的宏按键不能重复。", "Macro keys must be unique within a profile." },
            { "宏备注最多 100 个字符。", "Macro notes support up to 100 characters." },
            { "坐标超出支持范围。", "Coordinates are outside the supported range." },
            { "配置文件超过 1 MB。", "The profile file exceeds 1MB." },
            { "配置文件为空。", "The profile file is empty." },
            { "至少启用一个宏后才能开始。", "Enable at least one macro before starting." },
            { "输入释放尚未成功，请先按 F12 重试。", "Input release has not succeeded. Press F12 to retry first." },
            { "当前输入输出不支持键盘动作。", "The current input/output provider does not support keyboard actions." },
            { "待点按的 {0} 键正被实体键盘按住，请先松开。", "The {0} key is physically held down. Release it before a key tap." },
            { "已选择", "Selected" },
            { "未选择", "Not selected" },
            { "语言切换后将重新打开配置窗口。", "Changing language reopens the settings window." },
            { "语言设置无法保存：", "Could not save the language preference: " },
        };
    }
}
