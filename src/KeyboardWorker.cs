using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace KeyHoldMacro
{
    internal sealed class WorkerEvent
    {
        public string Kind, Text;
        public int X, Y, Key;
        public int StartVersion;
        public IntPtr Window;
        public WorkerEvent(string kind, string text) { Kind = kind; Text = text; }
    }

    internal sealed class KeyboardWorker : IDisposable
    {
        public readonly ConcurrentQueue<WorkerEvent> Events = new ConcurrentQueue<WorkerEvent>();
        private readonly ConcurrentQueue<Action> commands = new ConcurrentQueue<Action>();
        private readonly ManualResetEvent ready = new ManualResetEvent(false);
        private readonly Thread thread;
        private readonly MacroEngine engine;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private System.Threading.Timer sequenceTimer;
        private readonly HashSet<int> controlDown = new HashSet<int>();
        private readonly HashSet<int> controlSwallowed = new HashSet<int>();
        private readonly Native.HookProc hookProc;
        private uint threadId;
        private IntPtr hook, target;
        private bool quitting, pauseOnFocusLoss = true, captureKey;
        private Exception startupError;
        private int lastActive = -1;
        private int startVersion;
        public KeyboardWorker()
        {
            engine = new MacroEngine(new WindowsMouse(), new WindowsKeyboard(), delegate { return clock.ElapsedMilliseconds; });
            hookProc = Hook;
            thread = new Thread(Run); thread.IsBackground = true;
            thread.Name = "KeyHold keyboard"; thread.Start();
            if (!ready.WaitOne(5000)) throw new InvalidOperationException("键盘监听线程启动超时。");
            if (startupError != null) throw new InvalidOperationException("无法安装全局键盘钩子。", startupError);
        }
        private void Push(string kind, string text) { Events.Enqueue(new WorkerEvent(kind, text)); }
        private void Post(Action action)
        {
            if (quitting) return;
            commands.Enqueue(action);
            if (!Native.PostThreadMessage(threadId, Native.WM_COMMAND, UIntPtr.Zero, IntPtr.Zero))
                throw new InvalidOperationException("后台监听线程已停止，请重新启动程序。");
        }
        // Tickets cross the UI/worker queues. A stop invalidates even requests
        // that the UI has not yet returned to this thread.
        public int NewStartRequest() { return Interlocked.Increment(ref startVersion); }
        private void CancelPendingStart() { Interlocked.Increment(ref startVersion); }
        private bool CurrentStart(int version) { return version == Interlocked.CompareExchange(ref startVersion, 0, 0); }
        public void Start(Profile profile, IntPtr foreground, int version)
        {
            // Snapshot the profile before crossing threads.
            Profile copy = new System.Web.Script.Serialization.JavaScriptSerializer()
                .Deserialize<Profile>(ProfileStore.Serialize(profile));
            Post(delegate
            {
                if (!CurrentStart(version)) return;
                captureKey = false; engine.Configure(copy);
                if (foreground == IntPtr.Zero || Native.OwnWindow(foreground) || Native.GetForegroundWindow() != foreground)
                    throw new InvalidOperationException("请先切换到目标应用，再按 F8 启用。");
                foreach (Macro m in copy.Macros)
                    if (m.Enabled)
                    {
                        WindowsMouse.CheckCoordinates(m.X, m.Y);
                        foreach (MacroAction a in m.BeforeActions)
                            if (a.Kind != "KeyTap") WindowsMouse.CheckCoordinates(a.X, a.Y);
                        foreach (MacroAction a in m.AfterActions)
                            if (a.Kind != "KeyTap") WindowsMouse.CheckCoordinates(a.X, a.Y);
                    }
                // Keys held before enabling must be released and pressed again.
                for (int k = 1; k < 255; k++)
                    if (Native.GetAsyncKeyState(k) < 0) engine.MarkAlreadyDown(k);
                pauseOnFocusLoss = copy.PauseOnFocusLoss;
                if (!CurrentStart(version)) return;
                target = foreground; engine.Start();
                Push("running", "运行中：" + Native.WindowTitle(target));
            });
        }
        public void Stop(string reason)
        {
            CancelPendingStart();
            Post(delegate { captureKey = false; engine.Stop(); Push("stopped", reason); });
        }
        public void CaptureKey()
        {
            CancelPendingStart();
            Post(delegate { engine.Stop(); captureKey = true; Push("stopped", "请按要绑定的单键；F12 取消录入。"); });
        }
        private void Fail(Exception ex)
        {
            CancelPendingStart();
            captureKey = false;
            try { engine.Stop(); } catch { }
            Push("error", ex.Message);
        }
        private void ArmSequenceTimer()
        {
            int? delay = engine.NextActionDelayMs;
            if (sequenceTimer != null) sequenceTimer.Change(delay.HasValue ? Math.Max(1, delay.Value) : Timeout.Infinite, Timeout.Infinite);
        }
        private void CheckFocus()
        {
            if (!engine.Enabled) return;
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero || Native.OwnWindow(fg) || (pauseOnFocusLoss && fg != target))
            { CancelPendingStart(); engine.Stop(); Push("stopped", "已暂停：前台窗口发生变化。回到目标应用后按 F8。"); }
        }
        private void Run()
        {
            UIntPtr timer = UIntPtr.Zero;
            try
            {
                threadId = Native.GetCurrentThreadId();
                Native.MSG msg; Native.PeekMessage(out msg, IntPtr.Zero, 0, 0, 0);
                sequenceTimer = new System.Threading.Timer(delegate
                {
                    Native.PostThreadMessage(threadId, Native.WM_SEQUENCE, UIntPtr.Zero, IntPtr.Zero);
                }, null, Timeout.Infinite, Timeout.Infinite);
                for (int k = 1; k < 255; k++)
                    if (Native.GetAsyncKeyState(k) < 0)
                    { engine.MarkAlreadyDown(k); controlDown.Add(k); }
                hook = Native.SetWindowsHookEx(13, hookProc, Native.GetModuleHandle(null), 0);
                if (hook == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                timer = Native.SetTimer(IntPtr.Zero, new UIntPtr(1), 25, IntPtr.Zero);
                if (timer == UIntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                ready.Set();
                while (!quitting)
                {
                    int result = Native.GetMessage(out msg, IntPtr.Zero, 0, 0);
                    if (result == 0) break;
                    if (result < 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    if (msg.message == Native.WM_COMMAND)
                    {
                        Action action;
                        while (commands.TryDequeue(out action))
                            try { action(); } catch (Exception ex) { Fail(ex); }
                    }
                    else if (msg.message == Native.WM_TIMER)
                    {
                        try
                        {
                            if (!engine.Enabled && engine.NeedsRelease) engine.Stop(); // Retry failed releases.
                            CheckFocus();
                            if (lastActive != engine.ActiveKey)
                            {
                                lastActive = engine.ActiveKey;
                                WorkerEvent e = new WorkerEvent("active", ""); e.Key = lastActive; Events.Enqueue(e);
                            }
                        }
                        catch (Exception ex)
                        {
                            // Avoid repeating a release error every timer tick.
                            bool wasEnabled = engine.Enabled;
                            CancelPendingStart();
                            try { engine.Stop(); } catch { }
                            if (wasEnabled) Push("error", ex.Message);
                        }
                    }
                    else if (msg.message == Native.WM_SEQUENCE)
                    {
                        try { CheckFocus(); engine.Advance(); }
                        catch (Exception ex) { Fail(ex); }
                    }
                    else { Native.TranslateMessage(ref msg); Native.DispatchMessage(ref msg); }
                    ArmSequenceTimer();
                }
            }
            catch (Exception ex)
            {
                startupError = ex; Push("error", "后台线程停止：" + ex.Message);
            }
            finally
            {
                CancelPendingStart();
                try { engine.Stop(); } catch { }
                if (sequenceTimer != null) sequenceTimer.Dispose();
                if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
                if (timer != UIntPtr.Zero) Native.KillTimer(IntPtr.Zero, timer);
                quitting = true; ready.Set();
            }
        }
        private IntPtr Hook(int code, IntPtr wp, IntPtr lp)
        {
            if (code < 0) return Native.CallNextHookEx(hook, code, wp, lp);
            Native.KBD data = (Native.KBD)Marshal.PtrToStructure(lp, typeof(Native.KBD));
            if ((data.flags & 0x10) != 0) return Native.CallNextHookEx(hook, code, wp, lp);
            int message = wp.ToInt32(), vk = (int)data.vk;
            bool isDown = message == 0x100 || message == 0x104;
            bool isUp = message == 0x101 || message == 0x105;
            if (!isDown && !isUp) return Native.CallNextHookEx(hook, code, wp, lp);
            bool block = false;
            bool wasSwallowed = engine.IsSwallowed(vk);
            try
            {
                bool first = isDown && controlDown.Add(vk);
                if (isUp)
                {
                    controlDown.Remove(vk);
                    block = controlSwallowed.Remove(vk);
                }
                if (isDown && controlSwallowed.Contains(vk)) block = true;
                if (vk == 0x75 || vk == 0x77 || vk == 0x7B) // F6, F8, F12.
                {
                    if (isDown) { block = true; controlSwallowed.Add(vk); }
                    if (first)
                    {
                        if (vk == 0x7B)
                        { CancelPendingStart(); captureKey = false; engine.Stop(); Push("stopped", "已紧急停止并释放鼠标（F12）。"); }
                        else if (vk == 0x77)
                        {
                            captureKey = false;
                            if (engine.Enabled) { CancelPendingStart(); engine.Stop(); Push("stopped", "已暂停（F8）。"); }
                            else
                            {
                                WorkerEvent e = new WorkerEvent("start", "");
                                e.StartVersion = NewStartRequest();
                                e.Window = Native.GetForegroundWindow(); Events.Enqueue(e);
                            }
                        }
                        else
                        {
                            CancelPendingStart(); captureKey = false; engine.Stop();
                            Native.POINT p;
                            if (!Native.GetCursorPos(out p)) throw new InvalidOperationException("无法读取当前鼠标坐标。");
                            WorkerEvent e = new WorkerEvent("point", ""); e.X = p.x; e.Y = p.y; Events.Enqueue(e);
                            Push("stopped", "已取点；宏保持暂停。按 F8 启用。");
                        }
                    }
                }
                else if (captureKey && first)
                {
                    // Modifier/Windows keys cannot be bound; let their normal releases through.
                    bool supported = KeyCatalog.Keys.ContainsValue(vk);
                    if (supported)
                    {
                        captureKey = false; controlSwallowed.Add(vk); block = true;
                        WorkerEvent e = new WorkerEvent("key", ""); e.Key = vk; Events.Enqueue(e);
                        engine.MarkAlreadyDown(vk);
                    }
                }
                else
                {
                    if (isDown) CheckFocus();
                    block = engine.KeyEvent(vk, isDown) || block;
                }
            }
            catch (Exception ex) { block = block || wasSwallowed || engine.IsSwallowed(vk); Fail(ex); }
            ArmSequenceTimer();
            return block ? new IntPtr(1) : Native.CallNextHookEx(hook, code, wp, lp);
        }
        public void Dispose()
        {
            CancelPendingStart();
            if (!quitting)
            {
                Post(delegate { try { engine.Stop(); } finally { quitting = true; } });
                if (!thread.Join(3000))
                    Native.PostThreadMessage(threadId, Native.WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
            }
        }
    }
}
