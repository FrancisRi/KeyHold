using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using KeyHoldMacro;

internal sealed class FakeKeys : IKeyboardOutput
{
    private readonly List<string> events;
    public bool PhysicalDown, FailPress, FailRelease;
    public FakeKeys(List<string> output) { events = output; }
    public bool IsDown(int vk) { return PhysicalDown; }
    public void Press(int vk) { events.Add("key-down:" + vk); if (FailPress) throw new Exception("key press failed"); }
    public void Release(int vk) { events.Add("key-up:" + vk); if (FailRelease) throw new Exception("key release failed"); }
}

internal sealed class SequenceFixture
{
    public long Time;
    public readonly FakeMouse Mouse = new FakeMouse();
    public readonly FakeKeys Keys;
    public readonly MacroEngine Engine;
    public SequenceFixture(Profile p)
    {
        Keys = new FakeKeys(Mouse.Events);
        Engine = new MacroEngine(Mouse, Keys, delegate { return Time; });
        Engine.Configure(p); Engine.Start();
    }
    public string Output { get { return String.Join("|", Mouse.Events.ToArray()); } }
    public void Tick(int ms) { Time += ms; Engine.Advance(); }
}

internal static class SequenceTests
{
    private static Profile Profile()
    {
        Profile p = new Profile();
        p.Macros.Add(new Macro { Key = "E", X = 100, Y = 200 });
        p.Macros.Add(new Macro { Key = "F", X = -300, Y = 400 });
        return p;
    }
    public static void Run(Action<bool, string> check, Action<Action, string> throws)
    {
        check(MacroAction.DefaultTimingMs >= Math.Ceiling(1000.0 / 30) && MacroAction.DefaultTimingMs == 40, "default timing includes a 30 Hz logic-frame margin");
        Profile p = Profile();
        p.Macros[0].BeforeActions.Add(new MacroAction { Key = "1", DurationMs = 40, DelayAfterMs = 10 });
        SequenceFixture f = new SequenceFixture(p);
        f.Engine.KeyEvent(69, true);
        check(f.Output == "key-down:49" && f.Engine.NextActionDelayMs == 40, "prefix key starts before the original coordinate without blocking");
        f.Tick(39); check(f.Output == "key-down:49", "key tap retains its configured duration");
        f.Tick(1); check(f.Output == "key-down:49|key-up:49", "prefix key releases at its deadline");
        f.Tick(9); check(f.Mouse.Events.Count == 2, "10 ms gap is not shortened");
        f.Tick(1); check(f.Output == "key-down:49|key-up:49|down:100,200", "original coordinate starts after tap completion plus gap");
        f.Engine.KeyEvent(69, false);
        check(f.Output.EndsWith("down:100,200|up") && !f.Engine.NeedsRelease, "release ends the main hold after a prefix");

        f = new SequenceFixture(p); f.Engine.KeyEvent(69, true); f.Tick(1000);
        check(f.Output == "key-down:49|key-up:49" && f.Engine.NextActionDelayMs == 10, "late scheduling preserves the following gap instead of bursting overdue actions");
        f.Tick(10); check(f.Output.EndsWith("down:100,200"), "late sequence resumes after the full gap"); f.Engine.Stop();

        f = new SequenceFixture(p); f.Engine.KeyEvent(69, true); f.Engine.KeyEvent(69, false);
        f.Tick(40); f.Tick(10);
        check(f.Output == "key-down:49|key-up:49|down:100,200", "quick tap still reaches the original coordinate after its prefix");
        f.Tick(39); check(f.Engine.MouseHeld, "deferred short-tap click keeps its configured duration");
        f.Tick(1);
        check(f.Output == "key-down:49|key-up:49|down:100,200|up" && !f.Engine.NeedsRelease && f.Engine.ActiveKey == 0 && !f.Engine.NextActionDelayMs.HasValue,
            "quick tap finishes one mouse click and leaves no held input or pending sequence");
        f = new SequenceFixture(p); f.Engine.KeyEvent(69, true); f.Tick(40); f.Engine.KeyEvent(69, false); f.Tick(10); f.Tick(40);
        check(f.Output == "key-down:49|key-up:49|down:100,200|up", "release during the prefix gap also completes the mouse click");
        f = new SequenceFixture(p); f.Engine.KeyEvent(69, true); f.Engine.KeyEvent(69, false); f.Engine.Stop(); f.Tick(1000);
        check(f.Output == "key-down:49|key-up:49" && !f.Engine.NextActionDelayMs.HasValue, "stop still cancels a released trigger's pending main action");
        f = new SequenceFixture(p); f.Engine.KeyEvent(69, true); f.Engine.KeyEvent(69, false); f.Engine.KeyEvent(70, true); f.Tick(1000);
        check(f.Output == "key-down:49|key-up:49|down:-300,400" && f.Engine.ActiveKey == 70, "new macro still cancels the short tap's deferred coordinate");
        f.Engine.KeyEvent(70, false);
        f = new SequenceFixture(p); f.Engine.KeyEvent(69, true); f.Engine.KeyEvent(69, false); f.Engine.KeyEvent(69, true); f.Engine.KeyEvent(69, false);
        f.Tick(40); f.Tick(10); f.Tick(40);
        check(f.Output == "key-down:49|key-up:49|key-down:49|key-up:49|down:100,200|up", "rapid re-press replaces the old sequence instead of duplicating deferred clicks");
        f = new SequenceFixture(p); f.Engine.KeyEvent(69, true);
        for (int i = 0; i < 30; i++) f.Engine.KeyEvent(69, true);
        check(f.Output == "key-down:49", "auto-repeat does not restart a sequence");
        f.Engine.KeyEvent(70, true); f.Engine.KeyEvent(69, false); f.Tick(1000);
        check(f.Output == "key-down:49|key-up:49|down:-300,400" && f.Engine.ActiveKey == 70, "new key cancels prefix and takes ownership immediately");
        f.Engine.KeyEvent(70, false); check(f.Output.EndsWith("|up"), "new owner release still works after cancellation");

        p = Profile(); p.Macros[1].BeforeActions.Add(new MacroAction { Key = "1" });
        f = new SequenceFixture(p); f.Engine.KeyEvent(69, true); f.Engine.KeyEvent(70, true); f.Engine.KeyEvent(69, false);
        check(f.Output == "down:100,200|key-down:49" && f.Engine.MouseHeld, "new owner's prefix preserves the inherited main mouse hold");
        f.Tick(40); f.Tick(40); f.Engine.KeyEvent(70, false);
        check(f.Output == "down:100,200|key-down:49|key-up:49|move:-300,400|up", "delayed takeover moves without an intermediate mouse-up or second mouse-down");

        p = Profile(); p.Macros[0].AfterMainDelayMs = 5;
        p.Macros[0].AfterActions.Add(new MacroAction { Key = "2", DurationMs = 7, DelayAfterMs = 0 });
        f = new SequenceFixture(p); f.Engine.KeyEvent(69, true); f.Tick(4);
        check(f.Output == "down:100,200", "postfix honors the main-to-postfix gap");
        f.Tick(1); check(f.Output == "down:100,200|key-down:50", "postfix executes after main action while trigger is held");
        f.Engine.KeyEvent(69, false);
        check(f.Output == "down:100,200|key-down:50|up" && !f.Engine.MouseHeld, "physical release still releases the active main hold immediately");
        f.Tick(100);
        check(f.Output == "down:100,200|key-down:50|up|key-up:50" && !f.Engine.NeedsRelease, "postfix tap finishes its duration after physical release");

        p.Macros[0].AfterMainDelayMs = 0;
        f = new SequenceFixture(p); f.Engine.KeyEvent(69, true); f.Engine.KeyEvent(70, true);
        check(f.Output == "down:100,200|key-down:50|key-up:50|move:-300,400", "takeover cancels postfix without releasing a persistent mouse hold");
        f.Engine.KeyEvent(69, false); f.Engine.KeyEvent(70, false); f.Tick(100);
        check(f.Output.EndsWith("|move:-300,400|up"), "superseded postfix cannot resume later");

        p = Profile(); p.Macros[0].MouseMode = "Click"; p.Macros[0].MainDurationMs = 10; p.Macros[0].AfterMainDelayMs = 20;
        p.Macros[0].AfterActions.Add(new MacroAction { Kind = "Click", X = 500, Y = 600, DurationMs = 15, DelayAfterMs = 0 });
        f = new SequenceFixture(p); f.Engine.KeyEvent(69, true); f.Tick(10);
        check(f.Output == "down:100,200|up", "main click releases after its duration");
        f.Tick(19); check(f.Mouse.Events.Count == 2, "second coordinate waits for its configured gap");
        f.Tick(1); f.Tick(15); f.Engine.KeyEvent(69, false);
        check(f.Output == "down:100,200|up|down:500,600|up" && !f.Engine.NeedsRelease, "two independent coordinate clicks execute in order");
        f = new SequenceFixture(p); f.Engine.KeyEvent(69, true); f.Engine.KeyEvent(69, false);
        f.Tick(10); f.Tick(20); f.Tick(15);
        check(f.Output == "down:100,200|up|down:500,600|up" && f.Engine.ActiveKey == 0, "quick tap completes both independent coordinate clicks");

        f = new SequenceFixture(p); f.Engine.KeyEvent(69, true); f.Engine.KeyEvent(70, true); f.Tick(100);
        check(f.Output == "down:100,200|up|down:-300,400", "new key cancels an in-progress click and does not inherit its tap"); f.Engine.Stop();

        p = Profile(); p.Macros[0].BeforeActions.Add(new MacroAction { Kind = "Click", X = 500, Y = 600, DurationMs = 10, DelayAfterMs = 5 });
        f = new SequenceFixture(p); f.Engine.KeyEvent(69, true); f.Tick(10); f.Tick(5);
        check(f.Output == "down:500,600|up|down:100,200" && f.Engine.MouseHeld, "prefix click finishes before original hold"); f.Engine.Stop();
        f = new SequenceFixture(p); f.Engine.KeyEvent(69, true); f.Engine.KeyEvent(69, false); f.Tick(10); f.Tick(5); f.Tick(40);
        check(f.Output == "down:500,600|up|down:100,200|up" && !f.Engine.NeedsRelease, "release during a prefix click completes it and auto-releases the deferred main click");

        p = Profile(); p.Macros[1].BeforeActions.Add(new MacroAction { Key = "1", DurationMs = 40, DelayAfterMs = 10 });
        f = new SequenceFixture(p); f.Engine.KeyEvent(69, true); f.Engine.KeyEvent(70, true); f.Engine.KeyEvent(70, false);
        check(f.Output == "down:100,200|key-down:49|up" && !f.Engine.MouseHeld, "short-released new owner immediately releases the inherited persistent hold");
        f.Engine.KeyEvent(69, false); f.Tick(40); f.Tick(10); f.Tick(40);
        check(f.Output == "down:100,200|key-down:49|up|key-up:49|down:-300,400|up" && !f.Engine.NeedsRelease, "short-released takeover finishes only its own coordinate without reviving the old key");

        p = Profile(); p.Macros[0].AfterMainDelayMs = 0;
        p.Macros[0].AfterActions.Add(new MacroAction { Kind = "Move", X = 500, Y = 600, DelayAfterMs = 0 });
        f = new SequenceFixture(p); f.Engine.KeyEvent(69, true); f.Engine.KeyEvent(69, false);
        check(f.Output == "down:100,200|move:500,600|up", "postfix move preserves continuous hold");

        p = Profile(); p.Macros[0].BeforeActions.Add(new MacroAction { Key = "1" });
        f = new SequenceFixture(p); f.Engine.KeyEvent(69, true); f.Engine.Stop(); f.Tick(1000);
        check(f.Output == "key-down:49|key-up:49" && !f.Engine.Enabled && !f.Engine.NeedsRelease, "stop cancels pending actions and releases keyboard tap");
        check(f.Engine.KeyEvent(69, false), "stop during sequence retains trigger interception pair");
        f = new SequenceFixture(p); f.Engine.KeyEvent(69, true); f.Engine.Configure(Profile()); f.Tick(1000);
        check(f.Output == "key-down:49|key-up:49" && !f.Engine.Enabled, "reconfiguration cancels the sequence");

        f = new SequenceFixture(p); f.Keys.FailPress = true;
        throws(delegate { f.Engine.KeyEvent(69, true); }, "partial keyboard down failure is surfaced");
        check(f.Output == "key-down:49|key-up:49" && !f.Engine.Enabled && !f.Engine.NeedsRelease, "partial keyboard down is paired by cleanup");

        f = new SequenceFixture(p); f.Engine.KeyEvent(69, true); f.Keys.FailRelease = true;
        throws(delegate { f.Tick(40); }, "keyboard release failure is surfaced");
        check(!f.Engine.Enabled && f.Engine.NeedsRelease, "keyboard failure retains retry latch");
        throws(delegate { f.Engine.Start(); }, "keyboard cleanup failure prevents restart");
        f.Keys.FailRelease = false; f.Engine.Stop(); check(!f.Engine.NeedsRelease, "keyboard release cleanup can be retried");

        f = new SequenceFixture(p); f.Engine.KeyEvent(49, true);
        throws(delegate { f.Engine.KeyEvent(69, true); }, "a physically held target key is not synthetically tapped");
        check(f.Mouse.Events.Count == 0, "physical key ownership is preserved without synthetic key-up");
        f = new SequenceFixture(p); f.Keys.PhysicalDown = true;
        throws(delegate { f.Engine.KeyEvent(69, true); }, "native keyboard-state collision is rejected");
        check(f.Mouse.Events.Count == 0, "native key collision does not create an owned key latch");

        p = Profile(); p.Macros[0].AfterMainDelayMs = 0; p.Macros[0].AfterActions.Add(new MacroAction { Key = "1" });
        f = new SequenceFixture(p); f.Engine.KeyEvent(69, true); f.Keys.FailRelease = true;
        throws(delegate { f.Engine.Stop(); }, "keyboard cleanup failure on stop is surfaced");
        check(!f.Engine.MouseHeld && f.Engine.NeedsRelease && f.Output.EndsWith("key-up:49|up"), "mouse release is attempted even if keyboard cleanup fails");
        f.Keys.FailRelease = false; f.Engine.Stop();

        p = Profile(); p.Macros[0].BeforeActions.Add(new MacroAction { Key = "F12" });
        throws(delegate { p.Validate(); }, "reserved keys rejected in actions");
        p.Macros[0].BeforeActions[0].Key = "1"; p.Macros[0].BeforeActions[0].DelayAfterMs = -1;
        throws(delegate { p.Validate(); }, "negative action gaps rejected");
        p.Macros[0].BeforeActions[0].DelayAfterMs = 0; p.Macros[0].BeforeActions[0].DurationMs = 0;
        throws(delegate { p.Validate(); }, "zero tap duration rejected");
        p.Macros[0].BeforeActions.Clear(); p.Macros[0].AfterActions.Add(new MacroAction { Kind = "Click" });
        throws(delegate { p.Validate(); }, "ambiguous postfix click while main remains held is rejected");
        p.Macros[0].MouseMode = "Click"; p.Validate(); check(true, "postfix click accepted in click mode");
        p.Macros[0].BeforeActions = null; throws(delegate { p.Validate(); }, "null action lists rejected");
        p = Profile(); for (int i = 0; i < 11; i++) p.Macros[0].BeforeActions.Add(new MacroAction());
        throws(delegate { p.Validate(); }, "excessive action counts rejected");
        p = Profile(); p.Macros[0].BeforeActions.Add(new MacroAction { Kind = "Unknown" });
        throws(delegate { p.Validate(); }, "unknown action kind rejected");
        p.Macros[0].BeforeActions[0] = new MacroAction { Kind = "Click", X = 100001 };
        throws(delegate { p.Validate(); }, "extra coordinate outside supported range rejected");
        p.Macros[0].BeforeActions[0] = new MacroAction { DurationMs = 60001 };
        throws(delegate { p.Validate(); }, "excessive tap duration rejected");
        p.Macros[0].BeforeActions[0] = null;
        throws(delegate { p.Validate(); }, "null action rejected");

        string dir = Path.Combine(Path.GetTempPath(), "KeyHoldSequenceTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "old.json");
            File.WriteAllText(path, "{\"Version\":1,\"Name\":\"旧配置\",\"Macros\":[{\"Key\":\"E\",\"X\":100,\"Y\":200}]}");
            Profile old = ProfileStore.Read(path);
            check(old.Version == 2 && old.Macros[0].MouseMode == "Hold" && old.Macros[0].BeforeActions.Count == 0 && old.Macros[0].AfterActions.Count == 0, "v1 configurations load with their original continuous hold behavior");
            p = Profile(); p.Macros[0].MouseMode = "Click"; p.Macros[0].MainDurationMs = 12; p.Macros[0].AfterMainDelayMs = 10;
            p.Macros[0].BeforeActions.Add(new MacroAction { Key = "1", DurationMs = 17, DelayAfterMs = 10 });
            p.Macros[0].AfterActions.Add(new MacroAction { Kind = "Click", X = -50, Y = 20, DurationMs = 25, DelayAfterMs = 70 });
            ProfileStore.Write(path, p); Profile loaded = ProfileStore.Read(path);
            check(loaded.Version == 2 && loaded.Macros[0].MouseMode == "Click" && loaded.Macros[0].MainDurationMs == 12 && loaded.Macros[0].AfterMainDelayMs == 10, "main click settings survive JSON persistence");
            check(loaded.Macros[0].BeforeActions[0].Key == "1" && loaded.Macros[0].BeforeActions[0].DurationMs == 17 && loaded.Macros[0].AfterActions[0].X == -50 && loaded.Macros[0].AfterActions[0].DelayAfterMs == 70, "ordered actions and custom timings survive JSON persistence");
            Macro copy = p.Macros[0].Clone(); copy.BeforeActions[0].Key = "2";
            check(p.Macros[0].BeforeActions[0].Key == "1", "editor clone does not mutate the original actions");
            p = Profile(); p.Macros[0].BeforeActions.Add(new MacroAction { Key = "1" });
            f = new SequenceFixture(p); p.Macros[0].BeforeActions[0].Key = "2"; f.Engine.KeyEvent(69, true);
            check(f.Output == "key-down:49", "configured engine owns a snapshot of action settings"); f.Engine.Stop();
        }
        finally { Directory.Delete(dir, true); }
    }
}
