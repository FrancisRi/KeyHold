using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace KeyHoldMacro
{
    public sealed class MacroAction
    {
        public const int DefaultDurationMs = 40;
        public const int DefaultGapMs = 40;
        public string Kind { get; set; }
        public string Key { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int DurationMs { get; set; }
        public int DelayAfterMs { get; set; }
        public MacroAction() { Kind = "KeyTap"; Key = "1"; DurationMs = DefaultDurationMs; DelayAfterMs = DefaultGapMs; }
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
            MainDurationMs = MacroAction.DefaultDurationMs; AfterMainDelayMs = MacroAction.DefaultGapMs;
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
}
