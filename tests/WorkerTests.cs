using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using KeyHoldMacro;

// Compile instead of src/Native.cs: deterministic message loop, no hooks or real input.
namespace KeyHoldMacro
{
    internal static class Native
    {
        public const uint WM_QUIT = 0x12, WM_TIMER = 0x113, WM_COMMAND = 0x8001, WM_SEQUENCE = 0x8002;
        public delegate IntPtr HookProc(int code, IntPtr wp, IntPtr lp);
        public struct MSG { public uint message; }
        [StructLayout(LayoutKind.Sequential)]
        public struct KBD { public uint vk, scan, flags, time; public UIntPtr extra; }
        public struct POINT { public int x, y; }
        private static readonly BlockingCollection<MSG> messages = new BlockingCollection<MSG>();
        public static HookProc Callback;
        public static IntPtr Foreground = new IntPtr(123);
        public static uint GetCurrentThreadId() { return 1; }
        public static bool PeekMessage(out MSG msg, IntPtr w, uint a, uint b, uint c) { msg = new MSG(); return false; }
        public static int GetMessage(out MSG msg, IntPtr w, uint a, uint b)
        { msg = messages.Take(); return msg.message == WM_QUIT ? 0 : 1; }
        public static bool PostThreadMessage(uint id, uint message, UIntPtr wp, IntPtr lp)
        { messages.Add(new MSG { message = message }); return true; }
        public static bool TranslateMessage(ref MSG msg) { return true; }
        public static IntPtr DispatchMessage(ref MSG msg) { return IntPtr.Zero; }
        public static IntPtr GetModuleHandle(string name) { return new IntPtr(1); }
        public static IntPtr SetWindowsHookEx(int kind, HookProc proc, IntPtr mod, uint tid)
        { Callback = proc; return new IntPtr(1); }
        public static bool UnhookWindowsHookEx(IntPtr hook) { return true; }
        public static IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wp, IntPtr lp) { return IntPtr.Zero; }
        public static UIntPtr SetTimer(IntPtr w, UIntPtr id, uint ms, IntPtr proc) { return id; }
        public static bool KillTimer(IntPtr w, UIntPtr id) { return true; }
        public static short GetAsyncKeyState(int key) { return 0; }
        public static IntPtr GetForegroundWindow() { return Foreground; }
        public static bool OwnWindow(IntPtr window) { return false; }
        public static string WindowTitle(IntPtr window) { return "Test target"; }
        public static bool GetCursorPos(out POINT p) { p = new POINT { x = 10, y = 20 }; return true; }
    }

    internal sealed class WindowsMouse : IMouseOutput
    {
        public static readonly List<string> Events = new List<string>();
        public static void CheckCoordinates(int x, int y) { }
        public void PressAt(int x, int y) { Events.Add("down"); }
        public void MoveTo(int x, int y) { Events.Add("move:" + x + "," + y); }
        public void Release() { Events.Add("up"); }
    }
    internal sealed class WindowsKeyboard : IKeyboardOutput
    {
        public bool IsDown(int vk) { return false; }
        public void Press(int vk) { WindowsMouse.Events.Add("key-down:" + vk); }
        public void Release(int vk) { WindowsMouse.Events.Add("key-up:" + vk); }
    }
}

internal static class WorkerTests
{
    private static int checks;
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception("FAIL: " + message); checks++; }

    // Execute at a real worker-thread boundary, while all native dependencies are fakes.
    private static void Sync(KeyboardWorker worker, Action action)
    {
        Exception error = null;
        using (ManualResetEvent done = new ManualResetEvent(false))
        {
            Action command = delegate
            {
                try { action(); } catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            };
            typeof(KeyboardWorker).GetMethod("Post", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(worker, new object[] { command });
            if (!done.WaitOne(5000)) throw new Exception("Worker test timed out");
        }
        if (error != null) throw error;
    }

    private static void Key(KeyboardWorker worker, int vk, bool down)
    {
        Sync(worker, delegate { DeliverKey(vk, down); });
    }
    private static void DeliverKey(int vk, bool down)
    {
            IntPtr data = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.KBD)));
            try
            {
                Marshal.StructureToPtr(new Native.KBD { vk = (uint)vk }, data, false);
                Native.Callback(0, new IntPtr(down ? 0x100 : 0x101), data);
            }
            finally { Marshal.FreeHGlobal(data); }
    }

    private static WorkerEvent RequestStart(KeyboardWorker worker)
    {
        Key(worker, 0x77, true); Key(worker, 0x77, false);
        WorkerEvent e;
        while (worker.Events.TryDequeue(out e)) if (e.Kind == "start") return e;
        throw new Exception("F8 did not request start");
    }
    private static void WaitForOutput(KeyboardWorker worker, string expected)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        string actual = "";
        while (timeout.ElapsedMilliseconds < 3000)
        {
            Sync(worker, delegate { actual = String.Join("|", WindowsMouse.Events.ToArray()); });
            if (actual == expected) return;
            Thread.Sleep(2);
        }
        throw new Exception("Timed sequence expected " + expected + ", got " + actual);
    }

    public static int Main()
    {
        try { Run(); return 0; }
        catch (Exception ex) { Console.WriteLine(ex.Message); return 1; }
    }

    private static void Run()
    {
        Profile profile = new Profile();
        profile.Macros.Add(new Macro { Key = "A", X = 100, Y = 200 });
        profile.Macros.Add(new Macro { Key = "B", X = -300, Y = 400 });
        using (KeyboardWorker worker = new KeyboardWorker())
        {
            WorkerEvent pending = RequestStart(worker);
            Key(worker, 0x7B, true); Key(worker, 0x7B, false);
            // Replay a delayed UI response after the hook has already handled F12.
            worker.Start(profile, pending.Window, pending.StartVersion);
            Sync(worker, delegate { });
            Key(worker, 65, true); Key(worker, 65, false);
            Check(WindowsMouse.Events.Count == 0, "F12 cancels an F8 request waiting for the UI");

            pending = RequestStart(worker);
            Key(worker, 0x75, true); Key(worker, 0x75, false);
            worker.Start(profile, pending.Window, pending.StartVersion);
            Sync(worker, delegate { });
            Key(worker, 65, true); Key(worker, 65, false);
            Check(WindowsMouse.Events.Count == 0, "F6 cancels an F8 request waiting for the UI");

            pending = RequestStart(worker);
            worker.Stop("UI/session stop");
            worker.Start(profile, pending.Window, pending.StartVersion);
            Sync(worker, delegate { });
            Key(worker, 65, true); Key(worker, 65, false);
            Check(WindowsMouse.Events.Count == 0, "UI/session stop cancels a delayed F8 request");

            int countdown = worker.NewStartRequest();
            Key(worker, 0x7B, true); Key(worker, 0x7B, false);
            worker.Start(profile, Native.Foreground, countdown);
            Sync(worker, delegate { });
            Key(worker, 65, true); Key(worker, 65, false);
            Check(WindowsMouse.Events.Count == 0, "F12 cancels a countdown before its queued start runs");

            pending = RequestStart(worker);
            // Queue Start, then deliver F12 before the worker dequeues that command.
            Sync(worker, delegate
            {
                worker.Start(profile, pending.Window, pending.StartVersion);
                IntPtr data = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.KBD)));
                try
                {
                    Marshal.StructureToPtr(new Native.KBD { vk = 0x7B }, data, false);
                    Native.Callback(0, new IntPtr(0x100), data);
                    Native.Callback(0, new IntPtr(0x101), data);
                }
                finally { Marshal.FreeHGlobal(data); }
            });
            Sync(worker, delegate { });
            Key(worker, 65, true); Key(worker, 65, false);
            Check(WindowsMouse.Events.Count == 0, "F12 cancels a start already in the worker command queue");

            pending = RequestStart(worker);
            worker.Start(profile, pending.Window, pending.StartVersion);
            Sync(worker, delegate { });
            Key(worker, 65, true);
            Check(WindowsMouse.Events.Count == 1 && WindowsMouse.Events[0] == "down", "a new F8 after cancellation can enable macros");
            Key(worker, 66, true); Key(worker, 65, false);
            Check(String.Join("|", WindowsMouse.Events.ToArray()) == "down|move:-300,400", "hook transfers the hold and ignores the old key release");
            Key(worker, 66, false);
            Check(String.Join("|", WindowsMouse.Events.ToArray()) == "down|move:-300,400|up", "hook releases only when the current macro key is released");
            WindowsMouse.Events.Clear();
            Key(worker, 65, true); Key(worker, 66, true);
            Key(worker, 0x7B, true); Key(worker, 0x7B, false);
            Check(String.Join("|", WindowsMouse.Events.ToArray()) == "down|move:-300,400|up", "F12 releases a transferred fake hold");
            Key(worker, 65, false); Key(worker, 66, false);

            profile.Macros[0].BeforeActions.Add(new MacroAction { Key = "1", DurationMs = 15, DelayAfterMs = 10 });
            worker.Stop("prepare timed sequence"); Sync(worker, delegate { WindowsMouse.Events.Clear(); });
            pending = RequestStart(worker); worker.Start(profile, pending.Window, pending.StartVersion);
            Sync(worker, delegate { }); Key(worker, 65, true);
            WaitForOutput(worker, "key-down:49|key-up:49|down");
            Check(true, "one-shot timer resumes the real worker message loop in order");
            Key(worker, 65, false);
            Check(String.Join("|", WindowsMouse.Events.ToArray()) == "key-down:49|key-up:49|down|up", "physical release ends a timed sequence hold");

            Sync(worker, delegate { WindowsMouse.Events.Clear(); DeliverKey(65, true); DeliverKey(65, false); });
            WaitForOutput(worker, "key-down:49|key-up:49|down|up");
            Check(true, "quick physical tap completes prefix and one timed original-coordinate click through the worker timer");
            Native.PostThreadMessage(1, Native.WM_SEQUENCE, UIntPtr.Zero, IntPtr.Zero); Sync(worker, delegate { });
            Check(WindowsMouse.Events.Count == 4, "completed quick tap cannot leave a held mouse button or repeat its click");

            profile.Macros[0].BeforeActions[0].DurationMs = 200;
            worker.Stop("prepare cancellation"); Sync(worker, delegate { WindowsMouse.Events.Clear(); });
            pending = RequestStart(worker); worker.Start(profile, pending.Window, pending.StartVersion);
            Sync(worker, delegate { }); Key(worker, 65, true);
            Key(worker, 0x7B, true); Key(worker, 0x7B, false);
            Check(String.Join("|", WindowsMouse.Events.ToArray()) == "key-down:49|key-up:49", "F12 releases an in-progress synthetic keyboard tap");
            // Deliver an obsolete wake-up after stop; it must not run the original coordinate.
            Native.PostThreadMessage(1, Native.WM_SEQUENCE, UIntPtr.Zero, IntPtr.Zero); Sync(worker, delegate { });
            Check(WindowsMouse.Events.Count == 2, "obsolete sequence wake-up cannot run actions after F12");
            Key(worker, 65, false);

            worker.Stop("prepare focus test"); Sync(worker, delegate { WindowsMouse.Events.Clear(); });
            pending = RequestStart(worker); worker.Start(profile, pending.Window, pending.StartVersion);
            Sync(worker, delegate { }); Key(worker, 65, true);
            Native.Foreground = new IntPtr(456);
            WaitForOutput(worker, "key-down:49|key-up:49");
            Check(true, "focus is checked again before a delayed continuation");
            Key(worker, 65, false); Native.Foreground = new IntPtr(123);
        }
        L.Language = "en";
        try
        {
            using (KeyboardWorker english = new KeyboardWorker())
            {
                WorkerEvent pending = RequestStart(english);
                english.Start(profile, pending.Window, pending.StartVersion); Sync(english, delegate { });
                WorkerEvent e; string running = null;
                while (english.Events.TryDequeue(out e)) if (e.Kind == "running") running = e.Text;
                Check(running == "Running: Test target", "English worker reports the target without translating its title");
                Key(english, 0x77, true); Key(english, 0x77, false);
                string stopped = null;
                while (english.Events.TryDequeue(out e)) if (e.Kind == "stopped") stopped = e.Text;
                Check(stopped == "Paused (F8).", "F8 pause is localized on the worker thread");
                Key(english, 0x75, true); Key(english, 0x75, false);
                bool captured = false;
                while (english.Events.TryDequeue(out e))
                { if (e.Kind == "point") captured = e.X == 10 && e.Y == 20; if (e.Kind == "stopped") stopped = e.Text; }
                Check(captured && stopped == "Point captured; macros remain paused. Press F8 to start.", "English F6 event preserves coordinates and paused state");
                Key(english, 0x7B, true); Key(english, 0x7B, false);
                while (english.Events.TryDequeue(out e)) if (e.Kind == "stopped") stopped = e.Text;
                Check(stopped == "Emergency stop: mouse released (F12).", "English emergency status keeps the F12 cancellation marker");
            }
        }
        finally { L.Language = "zh-CN"; }
        TestFeedback();
        Console.WriteLine("PASS: " + checks + " worker checks (fake native input).");
    }
    private static List<WorkerEvent> Drain(KeyboardWorker worker)
    {
        Sync(worker, delegate { });
        List<WorkerEvent> events = new List<WorkerEvent>(); WorkerEvent e;
        while (worker.Events.TryDequeue(out e)) events.Add(e);
        return events;
    }
    private static void TestFeedback()
    {
        Profile profile = new Profile(); profile.Macros.Add(new Macro { Key = "A", X = 100, Y = 200 });
        using (KeyboardWorker worker = new KeyboardWorker())
        {
            worker.Stop("already paused");
            Check(Drain(worker).TrueForAll(e => e.Cue == FeedbackCue.None), "ordinary stops during paused startup are silent");
            WorkerEvent request = RequestStart(worker);
            Check(request.Cue == FeedbackCue.None, "F8 request carries no premature enable sound");
            Key(worker, 0x7B, true); Key(worker, 0x7B, false);
            worker.Start(profile, request.Window, request.StartVersion);
            List<WorkerEvent> canceled = Drain(worker);
            Check(!canceled.Exists(e => e.Cue == FeedbackCue.Enabled) && canceled.Exists(e => e.Cue == FeedbackCue.Emergency),
                "an F8 request canceled by F12 cannot emit an enabled cue later");
            request = RequestStart(worker);
            worker.Start(profile, request.Window, request.StartVersion);
            List<WorkerEvent> events = Drain(worker);
            WorkerEvent ack = events.Find(e => e.Kind == "running");
            Check(ack != null && ack.Cue == FeedbackCue.Enabled && ack.StartVersion == request.StartVersion, "worker confirms enabled only after engine start succeeds");
            worker.Stop("UI pause");
            Check(ack.StartVersion != worker.StartVersion, "a later stop immediately invalidates an enable acknowledgement not yet consumed by the UI");
            events = Drain(worker);
            Check(events.Exists(e => e.Cue == FeedbackCue.Paused), "UI pause reports an actual state transition");
            Key(worker, 0x75, true); Key(worker, 0x75, false);
            Check(Drain(worker).TrueForAll(e => e.Cue == FeedbackCue.None), "F6 capture while paused remains silent");
            Key(worker, 0x7B, true); Key(worker, 0x7B, true); Key(worker, 0x7B, false);
            Check(Drain(worker).FindAll(e => e.Cue == FeedbackCue.Emergency).Count == 1, "F12 confirms once even while paused and ignores auto-repeat");
            worker.Stop("UI emergency", true);
            Check(Drain(worker).Exists(e => e.Cue == FeedbackCue.Emergency), "the tray/button emergency path confirms stopping too");
            Native.Foreground = IntPtr.Zero;
            request = RequestStart(worker); worker.Start(profile, request.Window, request.StartVersion);
            events = Drain(worker);
            Check(events.Exists(e => e.Cue == FeedbackCue.Error) && !events.Exists(e => e.Cue == FeedbackCue.Enabled), "invalid target produces an error cue and no false enabled cue");
            Native.Foreground = new IntPtr(123);
            request = RequestStart(worker); worker.Start(profile, request.Window, request.StartVersion); Drain(worker);
            Key(worker, 0x77, true); Key(worker, 0x77, true); Key(worker, 0x77, false);
            Check(Drain(worker).FindAll(e => e.Cue == FeedbackCue.Paused).Count == 1, "F8 pause confirms once and ignores auto-repeat");
            request = RequestStart(worker); worker.Start(profile, request.Window, request.StartVersion); Drain(worker);
            Native.Foreground = new IntPtr(456);
            Native.PostThreadMessage(1, Native.WM_TIMER, UIntPtr.Zero, IntPtr.Zero);
            Check(Drain(worker).Exists(e => e.Cue == FeedbackCue.Paused), "focus-loss automatic pause has the same confirmed pause sound");
            Native.Foreground = new IntPtr(123);
        }
    }
}
