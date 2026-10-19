using System;
using System.Collections.Generic;
using System.IO;
using KeyHoldMacro;

internal sealed class FakeMouse : IMouseOutput
{
    public readonly List<string> Events = new List<string>();
    public bool FailPress, FailMove, FailRelease;
    public void PressAt(int x, int y)
    { Events.Add("down:" + x + "," + y); if (FailPress) throw new Exception("press failed"); }
    public void MoveTo(int x, int y)
    { Events.Add("move:" + x + "," + y); if (FailMove) throw new Exception("move failed"); }
    public void Release()
    { Events.Add("up"); if (FailRelease) throw new Exception("release failed"); }
}

internal static class CoreTests
{
    private static int count;
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception("FAIL: " + message); count++; }
    private static void Throws(Action f, string message)
    {
        bool thrown = false; try { f(); } catch { thrown = true; }
        Check(thrown, message);
    }
    private static Profile MakeProfile(bool suppress)
    {
        Profile p = new Profile { Name = "测试配置", SuppressKeys = suppress };
        p.Macros.Add(new Macro { Key = "A", X = 100, Y = 200, Label = "第一个" });
        p.Macros.Add(new Macro { Key = "B", X = -300, Y = 400 });
        return p;
    }
    public static int Main()
    {
        try { Run(); return 0; }
        catch (Exception ex) { Console.WriteLine(ex.Message); return 1; }
    }
    private static void Run()
    {
        FakeMouse m = new FakeMouse(); MacroEngine e = new MacroEngine(m);
        e.Configure(MakeProfile(true)); e.Start();
        Check(e.KeyEvent(65, true), "down is intercepted");
        Check(e.MouseHeld && e.ActiveKey == 65, "press owns left button");
        for (int i = 0; i < 50; i++) e.KeyEvent(65, true);
        Check(m.Events.Count == 1, "repeat does not click again");
        Check(e.KeyEvent(65, false), "up is intercepted");
        Check(!e.MouseHeld && m.Events[1] == "up", "release immediately");
        Check(!e.KeyEvent(67, true) && !e.KeyEvent(67, false), "unbound key passes through");

        m.Events.Clear(); e.KeyEvent(65, true); e.KeyEvent(66, true);
        Check(String.Join("|", m.Events.ToArray()) == "down:100,200|move:-300,400", "new press moves while keeping the original button hold");
        e.KeyEvent(65, false);
        Check(e.ActiveKey == 66 && e.MouseHeld && m.Events.Count == 2, "old key-up does not release newer hold");
        e.KeyEvent(66, false); Check(!e.MouseHeld, "new key-up releases");

        m.Events.Clear(); e.KeyEvent(65, true); e.KeyEvent(66, true); e.KeyEvent(66, false);
        Check(!e.MouseHeld && e.ActiveKey == 0, "does not fall back to old held key");
        e.KeyEvent(65, true); Check(m.Events.Count == 3, "auto-repeat of superseded held key does not reactivate");
        e.KeyEvent(65, false); e.KeyEvent(65, true);
        Check(e.ActiveKey == 65 && m.Events.Count == 4, "release and re-press reactivates");
        e.Stop(); Check(!e.Enabled && !e.MouseHeld, "pause releases");
        Check(e.KeyEvent(65, false), "paired key-up stays intercepted after pause");
        Check(!e.KeyEvent(65, true), "new press while paused passes through");
        e.Start(); e.KeyEvent(65, true);
        Check(!e.MouseHeld, "enabling while already held does not fire");
        Check(!e.KeyEvent(65, false), "key-up of native down passes through");
        e.KeyEvent(65, true); e.Stop();
        e.Configure(MakeProfile(false));
        Check(e.KeyEvent(65, false), "reconfigure preserves old suppression pair");
        e.Start(); Check(!e.KeyEvent(65, true) && e.MouseHeld, "optional pass-through still fires macro");
        Check(!e.KeyEvent(65, false), "pass-through up");
        e.Stop(); e.MarkAlreadyDown(66); e.Start(); e.KeyEvent(66, true);
        Check(!e.MouseHeld, "physical held startup key ignored"); e.KeyEvent(66, false);

        Profile disabled = MakeProfile(true); disabled.Macros[0].Enabled = false;
        e.Configure(disabled); e.Start(); Check(!e.KeyEvent(65, true) && !e.MouseHeld, "disabled macro ignored"); e.KeyEvent(65, false); e.Stop();
        Profile empty = new Profile(); e.Configure(empty); Throws(delegate { e.Start(); }, "empty profile cannot start");
        Profile dup = MakeProfile(true); dup.Macros[1].Key = "A"; Throws(delegate { dup.Validate(); }, "duplicate key rejected");
        Profile reserved = MakeProfile(true); reserved.Macros[0].Key = "F12"; Throws(delegate { reserved.Validate(); }, "emergency key reserved");
        reserved.Macros[0].Key = "F6"; Throws(delegate { reserved.Validate(); }, "capture key reserved");
        reserved.Macros[0].Key = "F8"; Throws(delegate { reserved.Validate(); }, "toggle key reserved");
        reserved.Macros[0].Key = "LControlKey"; Throws(delegate { reserved.Validate(); }, "modifier is not a macro key");
        Profile max = new Profile();
        for (int i = 0; i < 15; i++) max.Macros.Add(new Macro { Key = ((char)('A' + i)).ToString() });
        max.Validate(); Check(max.Macros.Count == 15, "15 macros accepted");
        max.Macros.Add(new Macro { Key = "P" }); Throws(delegate { max.Validate(); }, "16 macros rejected");
        Profile badVersion = MakeProfile(true); badVersion.Version = 3; Throws(delegate { badVersion.Validate(); }, "unsupported schema rejected");
        Profile badName = MakeProfile(true); badName.Name = " "; Throws(delegate { badName.Validate(); }, "empty name rejected");

        e.Configure(MakeProfile(true)); e.Start(); m.FailPress = true;
        Throws(delegate { e.KeyEvent(65, true); }, "press error surfaced");
        Check(!e.Enabled && !e.MouseHeld && m.Events[m.Events.Count - 1] == "up", "partial press failure attempts release and disables");
        Check(e.KeyEvent(65, false), "failed press retains suppression pair");
        m.FailPress = false; e.Start(); e.KeyEvent(65, true); m.FailRelease = true;
        Throws(delegate { e.Stop(); }, "release error surfaced");
        Check(e.MouseHeld && !e.Enabled, "failed release keeps retry latch");
        Throws(delegate { e.Start(); }, "cannot restart with unresolved release");
        m.FailRelease = false; e.Stop(); Check(!e.MouseHeld, "subsequent release retry succeeds");
        e.KeyEvent(65, false);

        ContinuousHoldTests();
        SequenceTests.Run(Check, Throws);

        string dir = Path.Combine(Path.GetTempPath(), "KeyHoldMacroTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "profile.json");
            Profile p = MakeProfile(true); p.Name = "中文配置 测试";
            ProfileStore.Write(path, p); Profile loaded = ProfileStore.Read(path);
            Check(loaded.Name == p.Name && loaded.Macros[0].Label == "第一个", "Unicode roundtrip");
            Check(loaded.Macros[1].X == -300 && loaded.SuppressKeys && loaded.PauseOnFocusLoss, "coordinates and options preserved");
            p.Name = "第二版"; ProfileStore.Write(path, p);
            Check(ProfileStore.Read(path).Name == "第二版", "atomic replacement saves new revision");
            Check(ProfileStore.Read(path + ".bak").Name == "中文配置 测试", "previous revision backup retained");
            File.WriteAllText(path, "{invalid"); Throws(delegate { ProfileStore.Read(path); }, "corrupt JSON rejected");
            File.WriteAllText(path, "null"); Throws(delegate { ProfileStore.Read(path); }, "null JSON rejected");
            File.WriteAllText(path, new string('x', 1024 * 1024 + 1)); Throws(delegate { ProfileStore.Read(path); }, "oversized JSON rejected");
        }
        finally { Directory.Delete(dir, true); }
        Console.WriteLine("PASS: " + count + " checks.");
    }

    private static void ContinuousHoldTests()
    {
        Profile p = new Profile();
        p.Macros.Add(new Macro { Key = "E", X = 100, Y = 200 });
        p.Macros.Add(new Macro { Key = "F", X = -300, Y = 400 });
        p.Macros.Add(new Macro { Key = "G", X = 500, Y = 600 });
        FakeMouse m = new FakeMouse(); MacroEngine e = new MacroEngine(m);
        e.Configure(p); e.Start();
        Check(e.KeyEvent(69, true) && e.KeyEvent(70, true), "E and F down are intercepted");
        for (int i = 0; i < 50; i++) { e.KeyEvent(69, true); e.KeyEvent(70, true); }
        Check(String.Join("|", m.Events.ToArray()) == "down:100,200|move:-300,400", "E to F has exactly one down and one move, including repeats");
        Check(e.KeyEvent(69, false) && e.MouseHeld && e.ActiveKey == 70 && m.Events.Count == 2, "releasing E keeps F held without output");
        Check(e.KeyEvent(70, false) && !e.MouseHeld && String.Join("|", m.Events.ToArray()) == "down:100,200|move:-300,400|up", "E down, F down, E up, F up releases only at the end");

        m.Events.Clear();
        e.KeyEvent(69, true); e.KeyEvent(70, true); e.KeyEvent(71, true);
        e.KeyEvent(70, false); e.KeyEvent(69, false);
        Check(e.ActiveKey == 71 && e.MouseHeld && m.Events.Count == 3, "three-key chain ignores superseded releases");
        e.KeyEvent(71, false);
        Check(String.Join("|", m.Events.ToArray()) == "down:100,200|move:-300,400|move:500,600|up", "three targets share a single button hold");

        m.Events.Clear();
        e.KeyEvent(69, true); e.KeyEvent(70, true); e.KeyEvent(69, false); e.KeyEvent(69, true);
        e.KeyEvent(70, false); e.KeyEvent(69, false);
        Check(String.Join("|", m.Events.ToArray()) == "down:100,200|move:-300,400|move:100,200|up", "re-pressing a superseded key moves back without an extra click");

        m.Events.Clear();
        e.KeyEvent(69, true); e.KeyEvent(70, true); e.Stop();
        Check(String.Join("|", m.Events.ToArray()) == "down:100,200|move:-300,400|up" && !e.MouseHeld, "pause releases a transferred hold");
        Check(e.KeyEvent(69, false) && e.KeyEvent(70, false), "pause retains both intercepted release pairs");

        p.SuppressKeys = false; e.Configure(p); e.Start(); m.Events.Clear();
        bool passed = !e.KeyEvent(69, true) && !e.KeyEvent(70, true) && !e.KeyEvent(69, false) && !e.KeyEvent(70, false);
        Check(passed && String.Join("|", m.Events.ToArray()) == "down:100,200|move:-300,400|up", "pass-through mode uses the same continuous hold");

        p.SuppressKeys = true; e.Configure(p); e.Start(); m.Events.Clear();
        e.KeyEvent(69, true); m.FailMove = true;
        Throws(delegate { e.KeyEvent(70, true); }, "override movement failure is surfaced");
        Check(!e.Enabled && !e.MouseHeld && e.ActiveKey == 0 && String.Join("|", m.Events.ToArray()) == "down:100,200|move:-300,400|up", "failed override releases the original hold and disables macros");
        Check(e.KeyEvent(69, false) && e.KeyEvent(70, false), "failed override preserves both intercepted release pairs");

        m.FailMove = false; e.Start(); e.KeyEvent(69, true); m.FailMove = true; m.FailRelease = true;
        Throws(delegate { e.KeyEvent(70, true); }, "override failure with release failure is surfaced");
        Check(!e.Enabled && e.MouseHeld, "failed override cleanup keeps the retry latch");
        Throws(delegate { e.Start(); }, "unresolved override cleanup prevents restart");
        m.FailMove = false; m.FailRelease = false; e.Stop();
        Check(!e.MouseHeld, "override cleanup can be retried");
        e.KeyEvent(69, false); e.KeyEvent(70, false);

        // Every supported macro key can take ownership; no E/F-specific branch.
        foreach (KeyValuePair<string, int> key in KeyCatalog.Keys)
        {
            string older = key.Key == "E" ? "F" : "E";
            Profile all = new Profile();
            all.Macros.Add(new Macro { Key = older, X = 100, Y = 200 });
            all.Macros.Add(new Macro { Key = key.Key, X = -300, Y = 400 });
            e.Configure(all); e.Start(); m.Events.Clear();
            e.KeyEvent(KeyCatalog.Keys[older], true); e.KeyEvent(key.Value, true);
            e.KeyEvent(KeyCatalog.Keys[older], false); e.KeyEvent(key.Value, false);
            Check(!e.MouseHeld && e.ActiveKey == 0 && String.Join("|", m.Events.ToArray()) == "down:100,200|move:-300,400|up", "continuous override for " + key.Key);
        }
        e.Stop();
    }
}
