using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace KeyHoldMacro
{
    // Keep these numbers aligned with rust/src/worker.rs.
    internal enum WorkerReason
    {
        None = 0, Requested = 1, PreparePoint = 2, CancelCountdown = 3, CloseEditor = 4,
        SessionChanged = 5, Suspend = 6, ActionPoint = 7, KeyCapture = 8, Focus = 9,
        F8 = 10, F12 = 11, Point = 12, Empty = 20, NeedsRelease = 21, PhysicalKey = 22,
        Mouse = 23, Keyboard = 24, DisplayChanged = 25, OutsideDisplay = 26,
        Foreground = 27, Cursor = 28, Thread = 29
    }

    internal sealed class WorkerEvent
    {
        public string Kind, Text;
        public int X, Y, Key, StartVersion;
        public WorkerReason Reason;
        public FeedbackCue Cue;
        public IntPtr Window;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WorkerNativeEvent
    {
        public IntPtr Window;
        public int Kind, Reason, Cue, Ticket, X, Y, Key;
        public uint OsError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WorkerBinding { public int Key, First, Count; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WorkerStep { public int Kind, Key, X, Y, Duration, Gap; }

    // This message-only HWND lives on the UI thread independently of the form's handle.
    internal sealed class WorkerNotifications : NativeWindow, IDisposable
    {
        internal const int EventsReady = 0x8001; // WM_APP + 1, shared with the DLL.
        private readonly Action drain;

        internal WorkerNotifications(Action drain)
        {
            this.drain = drain;
            CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // HWND_MESSAGE
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == EventsReady) drain();
            else base.WndProc(ref message);
        }

        public void Dispose() { DestroyHandle(); }
    }

    // C# owns JSON and key names. Only validated numeric plans cross into Rust.
    internal sealed class WorkerSnapshot
    {
        public readonly WorkerBinding[] Bindings;
        public readonly WorkerStep[] Steps;
        public readonly int Suppress, PauseOnFocusLoss;
        public WorkerSnapshot(Profile profile)
        {
            profile.Validate();
            List<WorkerBinding> bindings = new List<WorkerBinding>();
            List<WorkerStep> steps = new List<WorkerStep>();
            foreach (Macro macro in profile.Macros)
            {
                if (!macro.Enabled) continue;
                int first = steps.Count;
                foreach (MacroAction action in macro.BeforeActions) steps.Add(Step(action));
                steps.Add(new WorkerStep { Kind = macro.MouseMode == "Click" ? 2 : 4, X = macro.X, Y = macro.Y,
                    Duration = macro.MainDurationMs, Gap = macro.AfterActions.Count == 0 ? 0 : macro.AfterMainDelayMs });
                foreach (MacroAction action in macro.AfterActions) steps.Add(Step(action));
                bindings.Add(new WorkerBinding { Key = KeyCatalog.Keys[macro.Key], First = first, Count = steps.Count - first });
            }
            Bindings = bindings.ToArray(); Steps = steps.ToArray();
            Suppress = profile.SuppressKeys ? 1 : 0; PauseOnFocusLoss = profile.PauseOnFocusLoss ? 1 : 0;
        }
        private static WorkerStep Step(MacroAction action)
        {
            return new WorkerStep { Kind = action.Kind == "KeyTap" ? 1 : action.Kind == "Click" ? 2 : 3,
                Key = action.Kind == "KeyTap" ? KeyCatalog.Keys[action.Key] : 0, X = action.X, Y = action.Y,
                Duration = action.DurationMs, Gap = action.DelayAfterMs };
        }
    }

    internal sealed class KeyboardWorker : IDisposable
    {
        private readonly object gate = new object();
        private readonly WorkerHandle handle;

        public KeyboardWorker()
        {
            try
            {
                if (Api.kh_abi_version() != 2) throw ComponentError(null);
                int[] keys = new int[KeyCatalog.Keys.Count]; KeyCatalog.Keys.Values.CopyTo(keys, 0);
                uint error;
                IntPtr value = Api.kh_create(keys, keys.Length, out error);
                if (value == IntPtr.Zero)
                {
                    if (error == 1460) throw new InvalidOperationException(L.T("键盘监听线程启动超时。"));
                    throw new InvalidOperationException(L.T("无法安装全局键盘钩子。"), new Win32Exception(unchecked((int)error)));
                }
                handle = new WorkerHandle(value);
            }
            catch (DllNotFoundException ex) { throw ComponentError(ex); }
            catch (BadImageFormatException ex) { throw ComponentError(ex); }
            catch (EntryPointNotFoundException ex) { throw ComponentError(ex); }
        }

        private static InvalidOperationException ComponentError(Exception inner)
        { return new InvalidOperationException(L.T("无法加载 Rust 后台组件。请完整解压同一版本的 x64 发行包。"), inner); }

        internal int StartVersion { get { lock (gate) return handle.IsClosed ? 0 : Api.kh_ticket(handle); } }
        public int NewStartRequest() { lock (gate) return handle.IsClosed ? 0 : Api.kh_request_start(handle); }
        internal void SetNotificationWindow(IntPtr window)
        { lock (gate) if (!handle.IsClosed) Api.kh_set_notification_window(handle, window); }

        public void Start(Profile profile, IntPtr foreground, int version)
        {
            WorkerSnapshot snapshot = new WorkerSnapshot(profile);
            lock (gate)
            {
                if (handle.IsClosed) return;
                int result = Api.kh_start(handle, foreground, version, snapshot.Bindings, snapshot.Bindings.Length,
                    snapshot.Steps, snapshot.Steps.Length, snapshot.Suppress, snapshot.PauseOnFocusLoss);
                if (result == 0) throw new InvalidOperationException(L.T("后台监听线程已停止，请重新启动程序。"));
                if (result < 0) throw new InvalidOperationException(L.T("后台组件无法读取宏配置。"));
            }
        }

        public void Stop(WorkerReason reason, bool confirmStop = false)
        { lock (gate) if (!handle.IsClosed) Api.kh_stop(handle, (int)reason, confirmStop ? 1 : 0); }

        public void CaptureKey()
        {
            lock (gate)
                if (!handle.IsClosed && Api.kh_capture_key(handle) == 0)
                    throw new InvalidOperationException(L.T("后台监听线程已停止，请重新启动程序。"));
        }

        public bool TryDequeue(out WorkerEvent result)
        {
            WorkerNativeEvent native;
            lock (gate)
                if (handle.IsClosed || Api.kh_poll(handle, out native) == 0) { result = null; return false; }
            result = Decode(native);
            return true;
        }

        internal static WorkerEvent Decode(WorkerNativeEvent e)
        {
            string kind;
            switch (e.Kind)
            {
                case 1: kind = "start"; break;
                case 2: kind = "running"; break;
                case 3: kind = "stopped"; break;
                case 4: kind = "error"; break;
                case 5: kind = "point"; break;
                case 6: kind = "key"; break;
                default: throw ComponentError(null);
            }
            return new WorkerEvent { Kind = kind, Reason = (WorkerReason)e.Reason,
                Text = e.Kind == 2 ? L.T("运行中：") + Native.WindowTitle(e.Window) : Message(e),
                Window = e.Window, X = e.X, Y = e.Y, Key = e.Key, StartVersion = e.Ticket, Cue = (FeedbackCue)e.Cue };
        }

        private static string Message(WorkerNativeEvent e)
        {
            switch ((WorkerReason)e.Reason)
            {
                case WorkerReason.Requested: return L.T("已停止并请求释放鼠标。");
                case WorkerReason.PreparePoint: return L.T("移到目标应用，把鼠标放到目标位置，按 F6 取点。");
                case WorkerReason.CancelCountdown: return L.T("已取消启用倒计时。");
                case WorkerReason.CloseEditor: return L.T("请先关闭个性化设置窗口，再按 F8 启用。");
                case WorkerReason.SessionChanged: return L.T("会话发生变化，已暂停。");
                case WorkerReason.Suspend: return L.T("系统休眠，已暂停。");
                case WorkerReason.ActionPoint: return L.T("请切换到目标应用，按 F6 为个性化动作取点。");
                case WorkerReason.KeyCapture: return L.T("请按要绑定的单键；F12 取消录入。");
                case WorkerReason.Focus: return L.T("已暂停：前台窗口发生变化。回到目标应用后按 F8。");
                case WorkerReason.F8: return L.T("已暂停（F8）。");
                case WorkerReason.F12: return L.T("已紧急停止并释放鼠标（F12）。");
                case WorkerReason.Point: return L.T("已取点；宏保持暂停。按 F8 启用。");
                case WorkerReason.Empty: return L.T("至少启用一个宏后才能开始。");
                case WorkerReason.NeedsRelease: return L.T("输入释放尚未成功，请先按 F12 重试。");
                case WorkerReason.PhysicalKey:
                    foreach (KeyValuePair<string, int> k in KeyCatalog.Keys)
                        if (k.Value == e.Key) return L.F("待点按的 {0} 键正被实体键盘按住，请先松开。", k.Key);
                    return L.F("待点按的 {0} 键正被实体键盘按住，请先松开。", e.Key);
                case WorkerReason.Mouse: return L.T("Windows 未完整接受鼠标输入。若目标程序以管理员运行，请以相同权限启动本程序。");
                case WorkerReason.Keyboard: return L.T("Windows 未接受键盘输入，请检查目标程序权限。");
                case WorkerReason.DisplayChanged: return L.T("显示器布局已改变，请重新取点。");
                case WorkerReason.OutsideDisplay: return L.F("目标坐标 ({0}, {1}) 不在当前任何显示器内，请重新取点。", e.X, e.Y);
                case WorkerReason.Foreground: return L.T("请先切换到目标应用，再按 F8 启用。");
                case WorkerReason.Cursor: return L.T("无法读取当前鼠标坐标。");
                case WorkerReason.Thread:
                    return e.OsError == 0 ? L.T("后台监听线程已停止，请重新启动程序。") :
                        L.T("后台线程停止：") + new Win32Exception(unchecked((int)e.OsError)).Message;
                default: return "";
            }
        }

        public void Dispose() { lock (gate) handle.Dispose(); }

        private sealed class WorkerHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public WorkerHandle(IntPtr value) : base(true) { SetHandle(value); }
            protected override bool ReleaseHandle() { Api.kh_destroy(handle); return true; }
        }

        private static class Api
        {
            private const string Dll = "keyhold_worker.dll";
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
            public static extern uint kh_abi_version();
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            public static extern IntPtr kh_create([In] int[] keys, int count, out uint error);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            public static extern void kh_destroy(IntPtr handle);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            public static extern int kh_ticket(WorkerHandle handle);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            public static extern int kh_request_start(WorkerHandle handle);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            public static extern int kh_start(WorkerHandle handle, IntPtr foreground, int version,
                [In] WorkerBinding[] bindings, int count, [In] WorkerStep[] steps, int stepCount, int suppress, int pause);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            public static extern void kh_stop(WorkerHandle handle, int reason, int confirm);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            public static extern int kh_capture_key(WorkerHandle handle);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            public static extern void kh_set_notification_window(WorkerHandle handle, IntPtr window);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            public static extern int kh_poll(WorkerHandle handle, out WorkerNativeEvent native);
        }
    }
}
