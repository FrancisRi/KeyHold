use crate::engine::{Error, Key, Output, Point, Result};
use crate::worker::{Command, Platform, Shared, Worker};
use std::cell::{Cell, RefCell};
use std::ffi::c_void;
use std::mem::size_of;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::ptr;
use std::sync::atomic::Ordering;
use std::sync::{mpsc, Arc};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

type Handle = isize;
type HookProc = unsafe extern "system" fn(i32, usize, isize) -> isize;
type WinEventProc = unsafe extern "system" fn(Handle, u32, Handle, i32, i32, u32, u32);

#[repr(C)]
#[derive(Clone, Copy, Default)]
struct WinPoint {
    x: i32,
    y: i32,
}

#[repr(C)]
#[derive(Default)]
struct Msg {
    hwnd: Handle,
    message: u32,
    wp: usize,
    lp: isize,
    time: u32,
    point: WinPoint,
    private: u32,
}

#[repr(C)]
struct KeyboardHook {
    vk: u32,
    scan: u32,
    flags: u32,
    time: u32,
    extra: usize,
}

#[repr(C)]
#[derive(Clone, Copy)]
struct MouseInput {
    x: i32,
    y: i32,
    data: u32,
    flags: u32,
    time: u32,
    extra: usize,
}

#[repr(C)]
#[derive(Clone, Copy)]
struct KeyInput {
    vk: u16,
    scan: u16,
    flags: u32,
    time: u32,
    extra: usize,
}

#[repr(C)]
union InputData {
    mouse: MouseInput,
    keyboard: KeyInput,
}

#[repr(C)]
struct Input {
    kind: u32,
    data: InputData,
}

#[link(name = "user32")]
extern "system" {
    fn SetWindowsHookExW(kind: i32, proc: Option<HookProc>, module: Handle, thread: u32) -> Handle;
    fn UnhookWindowsHookEx(hook: Handle) -> i32;
    fn CallNextHookEx(hook: Handle, code: i32, wp: usize, lp: isize) -> isize;
    fn SetWinEventHook(
        event_min: u32,
        event_max: u32,
        module: Handle,
        callback: WinEventProc,
        process: u32,
        thread: u32,
        flags: u32,
    ) -> Handle;
    fn UnhookWinEvent(hook: Handle) -> i32;
    fn PeekMessageW(msg: *mut Msg, hwnd: Handle, first: u32, last: u32, remove: u32) -> i32;
    fn TranslateMessage(msg: *const Msg) -> i32;
    fn DispatchMessageW(msg: *const Msg) -> isize;
    fn PostMessageW(hwnd: Handle, message: u32, wp: usize, lp: isize) -> i32;
    fn MsgWaitForMultipleObjectsEx(
        count: u32,
        handles: *const Handle,
        ms: u32,
        wake_mask: u32,
        flags: u32,
    ) -> u32;
    fn GetForegroundWindow() -> Handle;
    fn GetWindowThreadProcessId(hwnd: Handle, pid: *mut u32) -> u32;
    fn GetCursorPos(point: *mut WinPoint) -> i32;
    fn GetAsyncKeyState(key: i32) -> i16;
    fn GetSystemMetrics(index: i32) -> i32;
    fn MonitorFromPoint(point: WinPoint, flags: u32) -> Handle;
    fn SendInput(count: u32, inputs: *const Input, size: i32) -> u32;
}

#[link(name = "kernel32")]
extern "system" {
    fn GetModuleHandleExW(flags: u32, address: *const u16, module: *mut Handle) -> i32;
    fn GetCurrentProcessId() -> u32;
    fn GetLastError() -> u32;
    fn CreateEventW(
        attributes: *const c_void,
        manual_reset: i32,
        initial: i32,
        name: *const u16,
    ) -> Handle;
    fn SetEvent(event: Handle) -> i32;
    fn CreateWaitableTimerExW(
        attributes: *const c_void,
        name: *const u16,
        flags: u32,
        access: u32,
    ) -> Handle;
    fn SetWaitableTimer(
        timer: Handle,
        due: *const i64,
        period: i32,
        completion: *const c_void,
        argument: *const c_void,
        resume: i32,
    ) -> i32;
    fn CancelWaitableTimer(timer: Handle) -> i32;
    fn WaitForSingleObject(handle: Handle, ms: u32) -> u32;
    fn CloseHandle(handle: Handle) -> i32;
}

const EXTRA: usize = 0x4b48_4d31;
const INFINITE: u32 = u32::MAX;
const EVENTS_READY: u32 = 0x8001; // WM_APP + 1, on the WinForms receiver only.
const NOTIFY_RETRY_MS: u32 = 25;

fn notify_ui(shared: &Shared) -> bool {
    shared.notify(|window| unsafe { PostMessageW(window, EVENTS_READY, 0, 0) != 0 })
}

fn mouse_input(x: i32, y: i32, flags: u32) -> Input {
    Input {
        kind: 0,
        data: InputData {
            mouse: MouseInput {
                x,
                y,
                data: 0,
                flags,
                time: 0,
                extra: EXTRA,
            },
        },
    }
}

fn mouse_move((x, y): Point) -> Result<Input> {
    let (left, top, width, height) = unsafe {
        (
            GetSystemMetrics(76),
            GetSystemMetrics(77),
            GetSystemMetrics(78),
            GetSystemMetrics(79),
        )
    };
    let dx = i64::from(x) - i64::from(left);
    let dy = i64::from(y) - i64::from(top);
    if dx < 0 || dy < 0 || dx >= i64::from(width) || dy >= i64::from(height) {
        return Err(Error::DisplayChanged);
    }
    let center =
        |offset: i64, size: i32| ((offset * 65536 + 32768) / i64::from(size)).min(65535) as i32;
    Ok(mouse_input(
        center(dx, width),
        center(dy, height),
        0x8000 | 0x4000 | 0x2000 | 1,
    ))
}

fn send(inputs: &[Input], failure: fn(u32) -> Error) -> Result<()> {
    if unsafe {
        SendInput(
            inputs.len() as u32,
            inputs.as_ptr(),
            size_of::<Input>() as i32,
        )
    } == inputs.len() as u32
    {
        Ok(())
    } else {
        Err(failure(unsafe { GetLastError() }))
    }
}

fn send_key(key: Key, up: bool) -> Result<()> {
    let extended = matches!(key, 33..=40 | 45 | 46 | 111);
    let flags = (u32::from(up) << 1) | u32::from(extended);
    send(
        &[Input {
            kind: 1,
            data: InputData {
                keyboard: KeyInput {
                    vk: key as u16,
                    scan: 0,
                    flags,
                    time: 0,
                    extra: EXTRA,
                },
            },
        }],
        Error::Keyboard,
    )
}

#[derive(Clone, Copy)]
struct Windows(Instant);

impl Output for Windows {
    fn now(&self) -> Duration {
        self.0.elapsed()
    }
    fn is_down(&self, key: Key) -> bool {
        unsafe { GetAsyncKeyState(key as i32) < 0 }
    }
    fn press_at(&mut self, point: Point) -> Result<()> {
        send(&[mouse_move(point)?, mouse_input(0, 0, 2)], Error::Mouse)
    }
    fn move_to(&mut self, point: Point) -> Result<()> {
        send(&[mouse_move(point)?], Error::Mouse)
    }
    fn release_mouse(&mut self) -> Result<()> {
        send(&[mouse_input(0, 0, 4)], Error::Mouse)
    }
    fn press_key(&mut self, key: Key) -> Result<()> {
        send_key(key, false)
    }
    fn release_key(&mut self, key: Key) -> Result<()> {
        send_key(key, true)
    }
}

impl Platform for Windows {
    fn foreground(&self) -> isize {
        unsafe { GetForegroundWindow() }
    }
    fn own_window(&self, window: isize) -> bool {
        let mut pid = 0;
        unsafe {
            GetWindowThreadProcessId(window, &mut pid);
            pid == GetCurrentProcessId()
        }
    }
    fn cursor(&self) -> Result<Point> {
        let mut point = WinPoint::default();
        if unsafe { GetCursorPos(&mut point) } != 0 {
            Ok((point.x, point.y))
        } else {
            Err(Error::Cursor(unsafe { GetLastError() }))
        }
    }
    fn validate_point(&self, (x, y): Point) -> Result<()> {
        if unsafe { MonitorFromPoint(WinPoint { x, y }, 0) } != 0 {
            Ok(())
        } else {
            Err(Error::OutsideDisplay((x, y)))
        }
    }
}

thread_local! {
    static STATE: RefCell<Option<Worker<Windows>>> = const { RefCell::new(None) };
    static FOREGROUND_CHANGED: Cell<bool> = const { Cell::new(false) };
    static FOREGROUND_WAKE: Cell<Handle> = const { Cell::new(0) };
}

unsafe extern "system" fn foreground_proc(
    _: Handle,
    _: u32,
    _: Handle,
    _: i32,
    _: i32,
    _: u32,
    _: u32,
) {
    // WinEvent callbacks may run during other Win32 calls on the pump thread.
    // Only record a pending check and signal; the pump owns the worker state.
    FOREGROUND_CHANGED.with(|changed| {
        if !changed.replace(true) {
            FOREGROUND_WAKE.with(|wake| {
                if wake.get() != 0 {
                    SetEvent(wake.get());
                }
            });
        }
    });
}

unsafe extern "system" fn hook_proc(code: i32, wp: usize, lp: isize) -> isize {
    if code == 0 && lp != 0 && matches!(wp, 0x100 | 0x101 | 0x104 | 0x105) {
        let event = &*(lp as *const KeyboardHook);
        if event.flags & 0x10 == 0 {
            // Ignore injected events before borrowing thread-local state.
            let result = catch_unwind(AssertUnwindSafe(|| {
                STATE.with(|state| {
                    if let Ok(mut state) = state.try_borrow_mut() {
                        if let Some(worker) = state.as_mut() {
                            if !worker.shared.quit.load(Ordering::Acquire) {
                                return worker.key(event.vk as usize, matches!(wp, 0x100 | 0x104));
                            }
                        }
                    }
                    false
                })
            }));
            match result {
                Ok(true) => return 1,
                Ok(false) => {}
                Err(_) => {
                    // Release program-owned inputs and stop at the pump boundary.
                    // Even failure reporting cannot unwind through a Win32 callback.
                    let _ = catch_unwind(AssertUnwindSafe(|| {
                        STATE.with(|state| {
                            if let Ok(mut state) = state.try_borrow_mut() {
                                if let Some(worker) = state.as_mut() {
                                    worker.shared.quit.store(true, Ordering::Release);
                                    worker.thread_failed(0);
                                }
                            }
                        })
                    }));
                }
            }
        }
    }
    CallNextHookEx(0, code, wp, lp)
}

struct Wake(Handle);
impl Wake {
    fn signal(&self) {
        unsafe {
            SetEvent(self.0);
        }
    }
}
impl Drop for Wake {
    fn drop(&mut self) {
        unsafe {
            CloseHandle(self.0);
        }
    }
}

struct ForegroundWatch(Handle);

impl ForegroundWatch {
    fn new(wake: &Wake) -> Option<Self> {
        const EVENT_SYSTEM_FOREGROUND: u32 = 3;
        const WINEVENT_OUTOFCONTEXT: u32 = 0;
        FOREGROUND_WAKE.with(|slot| slot.set(wake.0));
        let hook = unsafe {
            SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND,
                EVENT_SYSTEM_FOREGROUND,
                0,
                foreground_proc,
                0,
                0,
                WINEVENT_OUTOFCONTEXT,
            )
        };
        if hook == 0 {
            FOREGROUND_WAKE.with(|slot| slot.set(0));
            return None;
        }
        Some(Self(hook))
    }
}

impl Drop for ForegroundWatch {
    fn drop(&mut self) {
        unsafe { UnhookWinEvent(self.0) };
        FOREGROUND_WAKE.with(|slot| slot.set(0));
        FOREGROUND_CHANGED.with(|changed| changed.set(false));
    }
}

struct ActionTimer {
    handle: Handle,
    armed: bool,
}

impl ActionTimer {
    fn new() -> Option<Self> {
        const HIGH_RESOLUTION: u32 = 0x0000_0002;
        const SYNCHRONIZE_AND_MODIFY_STATE: u32 = 0x0010_0002;
        let handle = unsafe {
            CreateWaitableTimerExW(
                ptr::null(),
                ptr::null(),
                HIGH_RESOLUTION,
                SYNCHRONIZE_AND_MODIFY_STATE,
            )
        };
        (handle != 0).then(|| Self {
            handle,
            armed: false,
        })
    }

    fn arm(&mut self, delay: Duration) -> bool {
        // Action delays are bounded by profile validation. A negative value is
        // relative; round up to the next 100 ns.
        let due = -(delay.as_nanos().div_ceil(100) as i64);
        if unsafe { SetWaitableTimer(self.handle, &due, 0, ptr::null(), ptr::null(), 0) } == 0 {
            return false;
        }
        self.armed = true;
        true
    }

    fn cancel(&mut self) {
        if self.armed {
            unsafe { CancelWaitableTimer(self.handle) };
            self.armed = false;
        }
    }
}

impl Drop for ActionTimer {
    fn drop(&mut self) {
        unsafe { CloseHandle(self.handle) };
    }
}

fn message_timeout(delay: Option<Duration>) -> u32 {
    delay.map_or(INFINITE, |delay| {
        delay
            .as_nanos()
            .div_ceil(1_000_000)
            .min((INFINITE - 1) as u128) as u32
    })
}

pub struct Runtime {
    pub(crate) shared: Arc<Shared>,
    wake: Arc<Wake>,
    thread: Option<JoinHandle<()>>,
}

impl Runtime {
    pub(crate) fn spawn(supported: [bool; 256]) -> std::result::Result<Self, u32> {
        let event = unsafe { CreateEventW(ptr::null(), 0, 0, ptr::null()) };
        if event == 0 {
            return Err(unsafe { GetLastError() });
        }
        let wake = Arc::new(Wake(event));
        let shared = Arc::new(Shared::default());
        let (ready, wait) = mpsc::sync_channel(1);
        let worker_shared = Arc::clone(&shared);
        let worker_wake = Arc::clone(&wake);
        let thread = thread::Builder::new()
            .name("KeyHold keyboard (Rust)".into())
            .spawn(move || run(supported, worker_shared, worker_wake, ready))
            .map_err(|e| e.raw_os_error().unwrap_or(0) as u32)?;
        match wait.recv_timeout(Duration::from_secs(5)) {
            Ok(Ok(())) => Ok(Self {
                shared,
                wake,
                thread: Some(thread),
            }),
            Ok(Err(error)) => {
                let _ = thread.join();
                Err(error)
            }
            Err(_) => {
                shared.quit.store(true, Ordering::Release);
                wake.signal();
                Err(1460)
            }
        }
    }

    pub(crate) fn post(&self, command: Command) -> bool {
        if self.shared.dead.load(Ordering::Acquire) {
            return false;
        }
        self.shared.commands.lock().unwrap().push_back(command);
        self.wake.signal();
        true
    }

    pub(crate) fn set_notification_window(&self, window: Handle) {
        self.shared.set_notification_window(window);
        if notify_ui(&self.shared) {
            self.wake.signal();
        }
    }
}

impl Drop for Runtime {
    fn drop(&mut self) {
        self.shared.invalidate();
        self.shared.quit.store(true, Ordering::Release);
        self.wake.signal();
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

fn run(
    supported: [bool; 256],
    shared: Arc<Shared>,
    wake: Arc<Wake>,
    ready: mpsc::SyncSender<std::result::Result<(), u32>>,
) {
    let mut msg = Msg::default();
    // Force creation of the owning thread's message queue before installing the hook.
    unsafe {
        PeekMessageW(&mut msg, 0, 0, 0, 0);
    }
    let mut module = 0;
    let hook = unsafe {
        if GetModuleHandleExW(0x4 | 0x2, hook_proc as *const () as *const u16, &mut module) == 0 {
            0
        } else {
            SetWindowsHookExW(13, Some(hook_proc), module, 0)
        }
    };
    if hook == 0 {
        let _ = ready.send(Err(unsafe { GetLastError() }));
        return;
    }
    let foreground_watch = ForegroundWatch::new(&wake);
    STATE.with(|state| {
        *state.borrow_mut() = Some(Worker::new(
            Windows(Instant::now()),
            supported,
            Arc::clone(&shared),
            foreground_watch.is_some(),
        ))
    });
    if ready.send(Ok(())).is_ok() {
        let result = catch_unwind(AssertUnwindSafe(|| pump(&shared, &wake, &mut msg)));
        if !matches!(result, Ok(Ok(()))) {
            let code = match result {
                Ok(Err(code)) => code,
                _ => 0,
            };
            STATE.with(|state| state.borrow_mut().as_mut().unwrap().thread_failed(code));
        }
    }
    drop(foreground_watch);
    STATE.with(|state| {
        if let Some(mut worker) = state.borrow_mut().take() {
            worker.shutdown();
        }
    });
    unsafe {
        UnhookWindowsHookEx(hook);
    }
    shared.dead.store(true, Ordering::Release);
    // A terminal error can be queued after the last iteration of the pump.
    while notify_ui(&shared) && !shared.quit.load(Ordering::Acquire) {
        if unsafe { WaitForSingleObject(wake.0, NOTIFY_RETRY_MS) } == u32::MAX {
            break;
        }
    }
}

fn pump(shared: &Shared, wake: &Wake, msg: &mut Msg) -> std::result::Result<(), u32> {
    let mut action_timer = ActionTimer::new();
    let mut handles = [wake.0, 0];
    while !shared.quit.load(Ordering::Acquire) {
        // Win32 invokes WH_KEYBOARD_LL while pumping messages. Do not hold a
        // thread-local borrow across the message pump or wait.
        for _ in 0..64 {
            if shared.quit.load(Ordering::Acquire) || unsafe { PeekMessageW(msg, 0, 0, 0, 1) } == 0
            {
                break;
            }
            if msg.message == 0x12 {
                return Err(0);
            }
            unsafe {
                TranslateMessage(msg);
                DispatchMessageW(msg);
            }
        }
        if shared.quit.load(Ordering::Acquire) {
            break;
        }
        let commands = std::mem::take(&mut *shared.commands.lock().unwrap());
        let (deadline, action_deadline, clock) = STATE.with(|state| {
            let mut state = state.borrow_mut();
            let worker = state.as_mut().unwrap();
            if FOREGROUND_CHANGED.with(|changed| changed.replace(false)) {
                worker.foreground_changed();
            }
            for command in commands {
                if shared.quit.load(Ordering::Acquire) {
                    break;
                }
                worker.command(command);
            }
            (worker.service(), worker.action_deadline(), worker.platform)
        });
        if shared.quit.load(Ordering::Acquire) {
            break;
        }
        let retry_notification = notify_ui(shared);
        let now = clock.now();
        let mut delay = deadline.map(|at| at.saturating_sub(now));
        if retry_notification {
            let retry = Duration::from_millis(NOTIFY_RETRY_MS.into());
            delay = Some(delay.map_or(retry, |delay| delay.min(retry)));
        }
        let mut count = 1;
        if let Some(timer) = action_timer.as_mut() {
            // The timer can wake the thread at the action deadline even if the
            // ordinary maintenance timeout would return late.
            let action_delay = action_deadline.map(|at| at.saturating_sub(now));
            if action_delay.is_some_and(|delay| !delay.is_zero() && timer.arm(delay)) {
                handles[1] = timer.handle;
                count = 2;
            } else {
                timer.cancel();
            }
        }
        let wait = message_timeout(delay);
        // QS_ALLINPUT | MWMO_INPUTAVAILABLE; wait for the next macro deadline,
        // maintenance tick, posted command, or incoming Win32 message.
        if unsafe { MsgWaitForMultipleObjectsEx(count, handles.as_ptr(), wait, 0x04ff, 0x0004) }
            == u32::MAX
        {
            return Err(unsafe { GetLastError() });
        }
    }
    Ok(())
}

#[cfg(test)]
#[test]
fn native_x64_layout() {
    assert_eq!(
        (
            size_of::<Input>(),
            size_of::<Msg>(),
            size_of::<KeyboardHook>()
        ),
        (40, 48, 24)
    );
}

#[cfg(test)]
#[test]
fn fallback_timeout_rounds_up_without_turning_finite_waits_into_infinite_waits() {
    assert_eq!(message_timeout(None), INFINITE);
    assert_eq!(message_timeout(Some(Duration::ZERO)), 0);
    assert_eq!(message_timeout(Some(Duration::from_nanos(1))), 1);
    assert_eq!(message_timeout(Some(Duration::from_micros(10_001))), 11);
    assert_eq!(message_timeout(Some(Duration::MAX)), INFINITE - 1);
}

#[cfg(test)]
#[test]
fn action_timer_shares_the_message_wait_with_the_command_event() {
    let Some(mut timer) = ActionTimer::new() else {
        return; // This platform uses the tested fallback timeout.
    };
    let event = unsafe { CreateEventW(ptr::null(), 0, 0, ptr::null()) };
    assert_ne!(event, 0);
    let wake = Wake(event);
    let handles = [wake.0, timer.handle];
    assert!(timer.arm(Duration::from_secs(1)));
    wake.signal();
    let wait = || unsafe { MsgWaitForMultipleObjectsEx(2, handles.as_ptr(), 2000, 0x04ff, 4) };
    assert_eq!(wait(), 0);
    assert!(timer.arm(Duration::from_millis(10)));
    assert_eq!(wait(), 1);
}

#[cfg(test)]
#[test]
fn foreground_callback_coalesces_wake_signals_until_serviced() {
    let event = unsafe { CreateEventW(ptr::null(), 0, 0, ptr::null()) };
    assert_ne!(event, 0);
    let wake = Wake(event);
    FOREGROUND_WAKE.with(|slot| slot.set(wake.0));
    let notify = || unsafe { foreground_proc(0, 3, 0, 0, 0, 0, 0) };
    let poll = || unsafe { WaitForSingleObject(wake.0, 0) };
    notify();
    assert_eq!(poll(), 0);
    notify();
    assert_eq!(poll(), 0x102);
    assert!(FOREGROUND_CHANGED.with(|changed| changed.replace(false)));
    notify();
    assert_eq!(poll(), 0);
    FOREGROUND_WAKE.with(|slot| slot.set(0));
    FOREGROUND_CHANGED.with(|changed| changed.set(false));
}
