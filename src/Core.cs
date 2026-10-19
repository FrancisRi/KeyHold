using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace KeyHoldMacro
{
    public sealed class MacroAction
    {
        public const int DefaultTimingMs = 40; // 1000 / 30 = 33.33 ms, rounded up with margin.
        public string Kind { get; set; }
        public string Key { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int DurationMs { get; set; }
        public int DelayAfterMs { get; set; }
        public MacroAction() { Kind = "KeyTap"; Key = "1"; DurationMs = DefaultTimingMs; DelayAfterMs = DefaultTimingMs; }
        public MacroAction Clone() { return (MacroAction)MemberwiseClone(); }
        public void Validate()
        {
            int vk;
            if (Kind != "KeyTap" && Kind != "Click" && Kind != "Move") throw new ArgumentException(L.T("不支持的个性化动作。"));
            if (Kind == "KeyTap" && !KeyCatalog.TryGet(Key, out vk)) throw new ArgumentException(L.T("个性化动作包含不支持或保留的按键。"));
            if (X < -100000 || X > 100000 || Y < -100000 || Y > 100000) throw new ArgumentException(L.T("动作坐标超出支持范围。"));
            if (DurationMs < 1 || DurationMs > 60000 || DelayAfterMs < 0 || DelayAfterMs > 60000)
                throw new ArgumentException(L.T("按住时长须为 1～60000 ms，动作间隔须为 0～60000 ms。"));
        }
    }

    public sealed class Macro
    {
        public bool Enabled { get; set; }
        public string Key { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public string Label { get; set; }
        public string MouseMode { get; set; }
        public int MainDurationMs { get; set; }
        public int AfterMainDelayMs { get; set; }
        public List<MacroAction> BeforeActions { get; set; }
        public List<MacroAction> AfterActions { get; set; }
        public Macro()
        {
            Enabled = true; Label = ""; MouseMode = "Hold";
            MainDurationMs = AfterMainDelayMs = MacroAction.DefaultTimingMs;
            BeforeActions = new List<MacroAction>(); AfterActions = new List<MacroAction>();
        }
        public Macro Clone()
        {
            Macro copy = (Macro)MemberwiseClone();
            copy.BeforeActions = BeforeActions.ConvertAll(a => a.Clone());
            copy.AfterActions = AfterActions.ConvertAll(a => a.Clone());
            return copy;
        }
        public void ValidateActions()
        {
            if (MouseMode != "Hold" && MouseMode != "Click") throw new ArgumentException(L.T("原坐标动作模式无效。"));
            if (MainDurationMs < 1 || MainDurationMs > 60000 || AfterMainDelayMs < 0 || AfterMainDelayMs > 60000)
                throw new ArgumentException(L.T("原坐标按住时长或后置间隔超出范围。"));
            if (BeforeActions == null || AfterActions == null || BeforeActions.Count > 10 || AfterActions.Count > 10)
                throw new ArgumentException(L.T("前置和后置动作分别最多 10 个，不能为空值。"));
            foreach (MacroAction a in BeforeActions) { if (a == null) throw new ArgumentException(L.T("动作不能为空值。")); a.Validate(); }
            foreach (MacroAction a in AfterActions)
            {
                if (a == null) throw new ArgumentException(L.T("动作不能为空值。")); a.Validate();
                if (MouseMode == "Hold" && a.Kind == "Click")
                    throw new ArgumentException(L.T("后置坐标点击需要把原坐标模式改为“单次点击”；持续按住时可使用“移动坐标”。"));
            }
        }
    }

    public sealed class Profile
    {
        public int Version { get; set; }
        public string Name { get; set; }
        public bool SuppressKeys { get; set; }
        public bool PauseOnFocusLoss { get; set; }
        public List<Macro> Macros { get; set; }
        public Profile()
        {
            Version = 2; Name = L.T("默认配置"); SuppressKeys = true;
            PauseOnFocusLoss = true; Macros = new List<Macro>();
        }
        public void Validate()
        {
            if (Version != 1 && Version != 2) throw new ArgumentException(L.T("不支持此配置版本。"));
            if (String.IsNullOrWhiteSpace(Name) || Name.Length > 80)
                throw new ArgumentException(L.T("配置名称须为 1～80 个字符。"));
            if (Macros == null || Macros.Count > 15)
                throw new ArgumentException(L.T("每组配置最多 15 个宏。"));
            HashSet<int> keys = new HashSet<int>();
            foreach (Macro m in Macros)
            {
                int vk;
                if (m == null || !KeyCatalog.TryGet(m.Key, out vk))
                    throw new ArgumentException(L.T("配置包含不支持或保留的按键。"));
                if (!keys.Add(vk)) throw new ArgumentException(L.T("同一配置内的宏按键不能重复。"));
                if (m.Label != null && m.Label.Length > 100)
                    throw new ArgumentException(L.T("宏备注最多 100 个字符。"));
                if (m.X < -100000 || m.X > 100000 || m.Y < -100000 || m.Y > 100000)
                    throw new ArgumentException(L.T("坐标超出支持范围。"));
                m.ValidateActions();
            }
        }
    }

    public static class KeyCatalog
    {
        public static readonly Dictionary<string, int> Keys = Build();
        private static Dictionary<string, int> Build()
        {
            Dictionary<string, int> d = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < 26; i++) d.Add(((char)('A' + i)).ToString(), 65 + i);
            for (int i = 0; i < 10; i++) d.Add(i.ToString(), 48 + i);
            for (int i = 1; i <= 24; i++)
                if (i != 6 && i != 8 && i != 12) d.Add("F" + i, 111 + i);
            for (int i = 0; i < 10; i++) d.Add("Num" + i, 96 + i);
            d.Add("Space", 32); d.Add("Tab", 9); d.Add("Enter", 13);
            d.Add("Escape", 27); d.Add("Backspace", 8);
            d.Add("Left", 37); d.Add("Up", 38); d.Add("Right", 39); d.Add("Down", 40);
            d.Add("Home", 36); d.Add("End", 35); d.Add("PageUp", 33); d.Add("PageDown", 34);
            d.Add("Insert", 45); d.Add("Delete", 46);
            d.Add("NumMultiply", 106); d.Add("NumAdd", 107); d.Add("NumSubtract", 109);
            d.Add("NumDecimal", 110); d.Add("NumDivide", 111);
            d.Add(";", 186); d.Add("=", 187); d.Add(",", 188); d.Add("-", 189);
            d.Add(".", 190); d.Add("/", 191); d.Add("`", 192);
            d.Add("[", 219); d.Add("\\", 220); d.Add("]", 221); d.Add("'", 222);
            return d;
        }
        public static bool TryGet(string key, out int vk)
        {
            vk = 0; return key != null && Keys.TryGetValue(key, out vk);
        }
    }

    public static class ProfileStore
    {
        public static string Serialize(Profile p)
        {
            p.Validate(); p.Version = 2; return new JavaScriptSerializer().Serialize(p);
        }
        public static Profile Read(string path)
        {
            if (new FileInfo(path).Length > 1024 * 1024)
                throw new ArgumentException(L.T("配置文件超过 1 MB。"));
            Profile p = new JavaScriptSerializer().Deserialize<Profile>(File.ReadAllText(path, Encoding.UTF8));
            if (p == null) throw new ArgumentException(L.T("配置文件为空。"));
            p.Validate(); p.Version = 2; return p;
        }
        public static void Write(string path, Profile p)
        {
            string json = Serialize(p);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(json);
                    fs.Write(bytes, 0, bytes.Length); fs.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temp, path, path + ".bak", true);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    public interface IMouseOutput
    {
        void PressAt(int x, int y);
        void MoveTo(int x, int y);
        void Release();
    }

    public interface IKeyboardOutput
    {
        bool IsDown(int vk);
        void Press(int vk);
        void Release(int vk);
    }

    // Called only on the keyboard worker thread. No disk I/O or UI work here.
    public sealed class MacroEngine
    {
        private readonly IMouseOutput mouse;
        private readonly IKeyboardOutput keyboard;
        private readonly Func<long> now;
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private List<MacroAction> sequence;
        private int actionIndex, heldKeyboardKey, gap;
        private long due;
        private bool finishingTap, clickInProgress, triggerReleased;
        private readonly HashSet<int> down = new HashSet<int>();
        private readonly HashSet<int> swallowed = new HashSet<int>();
        private Dictionary<int, Macro> bindings = new Dictionary<int, Macro>();
        public bool Enabled { get; private set; }
        public bool Suppress { get; private set; }
        public int ActiveKey { get; private set; }
        public bool MouseHeld { get; private set; }
        public bool NeedsRelease { get { return MouseHeld || heldKeyboardKey != 0; } }
        public int? NextActionDelayMs
        {
            get { return sequence == null ? (int?)null : (int)Math.Max(0, Math.Min(Int32.MaxValue, due - now())); }
        }
        public MacroEngine(IMouseOutput output) : this(output, null, delegate { return clock.ElapsedMilliseconds; }) { }
        public MacroEngine(IMouseOutput output, IKeyboardOutput keys, Func<long> milliseconds)
        { mouse = output; keyboard = keys; now = milliseconds; }
        public void Configure(Profile profile)
        {
            profile.Validate(); Stop();
            Dictionary<int, Macro> next = new Dictionary<int, Macro>();
            foreach (Macro m in profile.Macros)
                if (m.Enabled) next.Add(KeyCatalog.Keys[m.Key], m.Clone());
            bindings = next; Suppress = profile.SuppressKeys;
        }
        public void Start()
        {
            if (bindings.Count == 0) throw new InvalidOperationException(L.T("至少启用一个宏后才能开始。"));
            if (NeedsRelease) throw new InvalidOperationException(L.T("输入释放尚未成功，请先按 F12 重试。"));
            Enabled = true;
        }
        public void Stop()
        {
            Enabled = false; ActiveKey = 0;
            // Keep down/swallowed until physical key-up, even across pause/reconfigure.
            CancelSequence(false);
        }
        private void ReleaseOutputs(bool keepHold)
        {
            Exception failure = null;
            if (heldKeyboardKey != 0)
            {
                try { keyboard.Release(heldKeyboardKey); heldKeyboardKey = 0; } catch (Exception ex) { failure = ex; }
            }
            if (MouseHeld && (!keepHold || clickInProgress))
            {
                try { mouse.Release(); MouseHeld = false; clickInProgress = false; } catch (Exception ex) { if (failure == null) failure = ex; }
            }
            if (failure != null) throw failure;
        }
        private void CancelSequence(bool keepHold)
        {
            sequence = null; finishingTap = false;
            ReleaseOutputs(keepHold);
        }
        private void FailSequence()
        {
            Enabled = false; ActiveKey = 0;
            try { CancelSequence(false); } catch { }
        }
        public void Advance()
        {
            try { AdvanceCore(); }
            catch { FailSequence(); throw; }
        }
        private void AdvanceCore()
        {
            while (Enabled && ActiveKey != 0 && sequence != null && now() >= due)
            {
                if (finishingTap)
                {
                    ReleaseOutputs(true); finishingTap = false; actionIndex++;
                    due = now() + gap;
                    if (gap > 0) return;
                    continue;
                }
                if (actionIndex >= sequence.Count)
                {
                    sequence = null;
                    if (triggerReleased) { ActiveKey = 0; ReleaseOutputs(false); }
                    return;
                }
                MacroAction action = sequence[actionIndex];
                if (action.Kind == "KeyTap")
                {
                    int vk = KeyCatalog.Keys[action.Key];
                    if (keyboard == null) throw new InvalidOperationException(L.T("当前输入输出不支持键盘动作。"));
                    if (down.Contains(vk) || keyboard.IsDown(vk)) throw new InvalidOperationException(L.F("待点按的 {0} 键正被实体键盘按住，请先松开。", action.Key));
                    heldKeyboardKey = vk; keyboard.Press(vk);
                    finishingTap = true; gap = action.DelayAfterMs; due = now() + action.DurationMs;
                    return;
                }
                // A trigger released during its prefix still completes one timed main click.
                if (action.Kind == "Click" || (action.Kind == "Hold" && triggerReleased))
                {
                    if (MouseHeld) { mouse.Release(); MouseHeld = false; }
                    MouseHeld = true; clickInProgress = true;
                    mouse.PressAt(action.X, action.Y);
                    finishingTap = true; gap = action.DelayAfterMs; due = now() + action.DurationMs;
                    return;
                }
                if (action.Kind == "Hold")
                {
                    if (MouseHeld) mouse.MoveTo(action.X, action.Y);
                    else { MouseHeld = true; mouse.PressAt(action.X, action.Y); }
                }
                else mouse.MoveTo(action.X, action.Y);
                actionIndex++; due = now() + action.DelayAfterMs;
                if (action.DelayAfterMs > 0) return;
            }
        }
        public bool KeyEvent(int vk, bool isDown)
        {
            if (!isDown)
            {
                down.Remove(vk);
                bool block = swallowed.Remove(vk);
                if (ActiveKey == vk)
                {
                    triggerReleased = true;
                    try
                    {
                        // Release a persistent main hold now, but let finite taps and
                        // queued personalized actions complete. New key/Stop still cancel.
                        if (MouseHeld && !clickInProgress) { mouse.Release(); MouseHeld = false; }
                        if (sequence == null) ActiveKey = 0;
                    }
                    catch { FailSequence(); throw; }
                }
                return block;
            }
            if (!down.Add(vk)) return swallowed.Contains(vk); // Ignore OS auto-repeat.
            Macro m;
            if (!Enabled || !bindings.TryGetValue(vk, out m)) return false;
            if (Suppress) swallowed.Add(vk);
            try
            {
                // Cancel the old plan immediately, transferring only a persistent main hold.
                CancelSequence(true);
                ActiveKey = vk; triggerReleased = false;
                sequence = new List<MacroAction>(m.BeforeActions);
                sequence.Add(new MacroAction { Kind = m.MouseMode, X = m.X, Y = m.Y,
                    DurationMs = m.MainDurationMs, DelayAfterMs = m.AfterActions.Count > 0 ? m.AfterMainDelayMs : 0 });
                sequence.AddRange(m.AfterActions);
                actionIndex = 0; due = now();
                AdvanceCore();
            }
            catch
            {
                FailSequence();
                throw;
            }
            return Suppress;
        }
        public bool IsSwallowed(int vk) { return swallowed.Contains(vk); }
        public void MarkAlreadyDown(int vk) { down.Add(vk); }
    }
}
