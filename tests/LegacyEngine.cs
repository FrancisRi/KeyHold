// C# v0.3.0 reference engine; built only by the regression tests.
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace KeyHoldMacro
{
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
