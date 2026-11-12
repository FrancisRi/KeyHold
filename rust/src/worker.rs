use crate::engine::{Action, Engine, Error, Key, Kind, Output, Point, Result};
use std::collections::{HashMap, VecDeque};
use std::sync::atomic::{AtomicBool, AtomicI32, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

pub(crate) const START: i32 = 1;
pub(crate) const RUNNING: i32 = 2;
pub(crate) const STOPPED: i32 = 3;
pub(crate) const ERROR: i32 = 4;
pub(crate) const POINT: i32 = 5;
pub(crate) const KEY: i32 = 6;
const MAINTENANCE: Duration = Duration::from_millis(25);
const IDLE_FOCUS_WATCHDOG: Duration = Duration::from_millis(250);

#[repr(i32)]
#[derive(Clone, Copy)]
pub(crate) enum Reason {
    KeyCapture = 8,
    Focus = 9,
    F8 = 10,
    F12 = 11,
    Point = 12,
    Empty = 20,
    NeedsRelease = 21,
    PhysicalKey = 22,
    Mouse = 23,
    Keyboard = 24,
    DisplayChanged = 25,
    OutsideDisplay = 26,
    Foreground = 27,
    Cursor = 28,
    Thread = 29,
}

// HWND is pointer-sized; all other fields have explicit widths.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct Event {
    pub window: isize,
    pub kind: i32,
    pub reason: i32,
    pub cue: i32,
    pub ticket: i32,
    pub x: i32,
    pub y: i32,
    pub key: i32,
    pub os_error: u32,
}

impl Event {
    fn error(error: Error) -> Self {
        let mut event = Self {
            kind: ERROR,
            ..Self::default()
        };
        event.reason = match error {
            Error::Empty => Reason::Empty,
            Error::NeedsRelease => Reason::NeedsRelease,
            Error::PhysicalKey(key) => {
                event.key = key as i32;
                Reason::PhysicalKey
            }
            Error::Mouse(code) => {
                event.os_error = code;
                Reason::Mouse
            }
            Error::Keyboard(code) => {
                event.os_error = code;
                Reason::Keyboard
            }
            Error::DisplayChanged => Reason::DisplayChanged,
            Error::OutsideDisplay((x, y)) => {
                event.x = x;
                event.y = y;
                Reason::OutsideDisplay
            }
            Error::Foreground => Reason::Foreground,
            Error::Cursor(code) => {
                event.os_error = code;
                Reason::Cursor
            }
        } as i32;
        event
    }
}

pub(crate) struct Profile {
    pub bindings: HashMap<Key, Vec<Action>>,
    pub suppress: bool,
    pub pause_on_focus_loss: bool,
}

pub(crate) enum Command {
    Start(Profile, isize, i32),
    Stop(i32, bool),
    CaptureKey,
}

#[derive(Default)]
struct EventQueue {
    events: VecDeque<Event>,
    window: isize,
    posted: bool,
}

#[derive(Default)]
pub(crate) struct Shared {
    events: Mutex<EventQueue>,
    pub commands: Mutex<VecDeque<Command>>,
    pub quit: AtomicBool,
    pub dead: AtomicBool,
    ticket: AtomicI32,
}

impl Shared {
    pub fn ticket(&self) -> i32 {
        self.ticket.load(Ordering::SeqCst)
    }
    pub fn invalidate(&self) -> i32 {
        self.ticket.fetch_add(1, Ordering::SeqCst).wrapping_add(1)
    }
    fn push(&self, event: Event) {
        self.events.lock().unwrap().events.push_back(event);
    }
    pub fn poll(&self) -> Option<Event> {
        let mut queue = self.events.lock().unwrap();
        let event = queue.events.pop_front();
        if queue.events.is_empty() {
            queue.posted = false;
        }
        event
    }
    pub fn set_notification_window(&self, window: isize) {
        let mut queue = self.events.lock().unwrap();
        queue.window = window;
        queue.posted = false;
    }
    // Return true only if a post failed and should be retried. Holding the queue
    // lock across the asynchronous post keeps the notification and drain ordered.
    pub fn notify(&self, post: impl FnOnce(isize) -> bool) -> bool {
        let mut queue = self.events.lock().unwrap();
        if queue.window == 0 || queue.posted || queue.events.is_empty() {
            return false;
        }
        queue.posted = post(queue.window);
        !queue.posted
    }
}

pub(crate) trait Platform: Output {
    fn foreground(&self) -> isize;
    fn own_window(&self, window: isize) -> bool;
    fn cursor(&self) -> Result<Point>;
    fn validate_point(&self, point: Point) -> Result<()>;
}

pub(crate) struct Worker<P> {
    pub platform: P,
    pub shared: Arc<Shared>,
    engine: Engine,
    supported: [bool; 256],
    control_down: [bool; 256],
    control_swallowed: [bool; 256],
    target: isize,
    pause_on_focus_loss: bool,
    capture_key: bool,
    reported_running: bool,
    foreground_notifications: bool,
    last_maintenance: Option<Duration>,
}

impl<P: Platform> Worker<P> {
    pub fn new(
        platform: P,
        supported: [bool; 256],
        shared: Arc<Shared>,
        foreground_notifications: bool,
    ) -> Self {
        let mut engine = Engine::new();
        let mut control_down = [false; 256];
        for (key, down) in control_down.iter_mut().enumerate().skip(1) {
            *down = platform.is_down(key);
            if *down {
                engine.mark_down(key);
            }
        }
        Self {
            platform,
            shared,
            engine,
            supported,
            control_down,
            control_swallowed: [false; 256],
            target: 0,
            pause_on_focus_loss: true,
            capture_key: false,
            reported_running: false,
            foreground_notifications,
            last_maintenance: None,
        }
    }

    fn report(&mut self, mut event: Event, emergency: bool) {
        event.cue = match event.kind {
            RUNNING if !std::mem::replace(&mut self.reported_running, true) => 1,
            STOPPED => {
                let was_running = std::mem::take(&mut self.reported_running);
                if emergency {
                    3
                } else if was_running {
                    2
                } else {
                    0
                }
            }
            ERROR => {
                self.reported_running = false;
                4
            }
            _ => 0,
        };
        self.shared.push(event);
    }

    fn stop(&mut self, reason: i32, emergency: bool) -> Result<()> {
        self.engine.stop(&mut self.platform)?;
        self.report(
            Event {
                kind: STOPPED,
                reason,
                ticket: self.shared.ticket(),
                ..Event::default()
            },
            emergency,
        );
        Ok(())
    }

    fn fail(&mut self, error: Error) {
        self.shared.invalidate();
        self.capture_key = false;
        let _ = self.engine.stop(&mut self.platform);
        let mut event = Event::error(error);
        event.ticket = self.shared.ticket();
        self.report(event, false);
    }

    pub fn thread_failed(&mut self, os_error: u32) {
        self.shared.invalidate();
        self.capture_key = false;
        let _ = self.engine.stop(&mut self.platform);
        self.report(
            Event {
                kind: ERROR,
                reason: Reason::Thread as i32,
                ticket: self.shared.ticket(),
                os_error,
                ..Event::default()
            },
            false,
        );
    }

    fn start(&mut self, profile: Profile, target: isize, ticket: i32) -> Result<()> {
        if self.shared.ticket() != ticket {
            return Ok(());
        }
        self.capture_key = false;
        self.engine.stop(&mut self.platform)?;
        if target == 0 || self.platform.own_window(target) || self.platform.foreground() != target {
            return Err(Error::Foreground);
        }
        for actions in profile.bindings.values() {
            for action in actions {
                match action.kind {
                    Kind::Hold(p) | Kind::Click(p) | Kind::Move(p) => {
                        self.platform.validate_point(p)?
                    }
                    Kind::Tap(_) => {}
                }
            }
        }
        self.engine
            .configure(&mut self.platform, profile.bindings, profile.suppress)?;
        for key in 1..256 {
            if self.platform.is_down(key) {
                self.engine.mark_down(key);
            }
        }
        self.pause_on_focus_loss = profile.pause_on_focus_loss;
        if self.shared.ticket() != ticket {
            return Ok(());
        }
        self.target = target;
        self.engine.start()?;
        self.report(
            Event {
                kind: RUNNING,
                window: target,
                ticket,
                ..Event::default()
            },
            false,
        );
        Ok(())
    }

    pub fn command(&mut self, command: Command) {
        let result = match command {
            Command::Start(profile, target, ticket) => self.start(profile, target, ticket),
            Command::Stop(reason, confirm) => {
                self.capture_key = false;
                self.stop(reason, confirm)
            }
            Command::CaptureKey => self
                .stop(Reason::KeyCapture as i32, false)
                .map(|()| self.capture_key = true),
        };
        if let Err(error) = result {
            self.fail(error);
        }
    }

    fn check_focus(&mut self) -> Result<()> {
        if self.engine.enabled {
            let fg = self.platform.foreground();
            if fg == 0
                || self.platform.own_window(fg)
                || (self.pause_on_focus_loss && fg != self.target)
            {
                self.shared.invalidate();
                self.stop(Reason::Focus as i32, false)?;
            }
        }
        Ok(())
    }

    pub fn foreground_changed(&mut self) {
        if let Err(error) = self.check_focus() {
            self.fail(error);
        }
    }

    fn maintenance_deadline(&self) -> Option<Duration> {
        let needs_release = self.engine.needs_release();
        if !self.engine.enabled && !needs_release {
            return None;
        }
        // Keep the faster fallback while a sequence or owned input is active.
        let interval = if self.foreground_notifications
            && !needs_release
            && self.engine.deadline().is_none()
        {
            IDLE_FOCUS_WATCHDOG
        } else {
            MAINTENANCE
        };
        Some(
            self.last_maintenance
                .map_or(Duration::ZERO, |at| at + interval),
        )
    }

    // Return the next deadline after processing any work that is already due.
    pub fn service(&mut self) -> Option<Duration> {
        let now = self.platform.now();
        let was_enabled = self.engine.enabled;
        let maintenance = self.maintenance_deadline().is_some_and(|at| now >= at);
        let due = self.engine.deadline().is_some_and(|at| now >= at);
        if maintenance {
            self.last_maintenance = Some(now);
        }
        if maintenance || due {
            if was_enabled {
                if let Err(error) = self
                    .check_focus()
                    .and_then(|()| self.engine.advance(&mut self.platform))
                {
                    self.fail(error);
                }
            } else {
                // Keep retrying failed releases without repeating the error event.
                let _ = self.engine.stop(&mut self.platform);
            }
        }
        let maintenance = self.maintenance_deadline();
        if maintenance.is_none() {
            self.last_maintenance = None;
        }
        self.engine.deadline().into_iter().chain(maintenance).min()
    }

    pub fn action_deadline(&self) -> Option<Duration> {
        self.engine.deadline()
    }

    pub fn key(&mut self, key: Key, down: bool) -> bool {
        if key >= 256 {
            return false;
        }
        let first = down && !std::mem::replace(&mut self.control_down[key], true);
        let mut block = if down {
            self.control_swallowed[key]
        } else {
            self.control_down[key] = false;
            std::mem::take(&mut self.control_swallowed[key])
        };
        let swallowed = self.engine.swallowed(key);
        let result = if matches!(key, 0x75 | 0x77 | 0x7b) {
            if down {
                block = true;
                self.control_swallowed[key] = true;
            }
            if first {
                self.control(key)
            } else {
                Ok(())
            }
        } else if self.capture_key && first && self.supported[key] {
            self.capture_key = false;
            self.control_swallowed[key] = true;
            self.engine.mark_down(key);
            self.shared.push(Event {
                kind: KEY,
                key: key as i32,
                ..Event::default()
            });
            block = true;
            Ok(())
        } else {
            (if down { self.check_focus() } else { Ok(()) })
                .and_then(|()| self.engine.key_event(&mut self.platform, key, down))
                .map(|swallow| block |= swallow)
        };
        if let Err(error) = result {
            block |= swallowed || self.engine.swallowed(key);
            self.fail(error);
        }
        block
    }

    fn control(&mut self, key: Key) -> Result<()> {
        self.capture_key = false;
        match key {
            0x77 if !self.engine.enabled => {
                let ticket = self.shared.invalidate();
                self.shared.push(Event {
                    kind: START,
                    window: self.platform.foreground(),
                    ticket,
                    ..Event::default()
                });
                Ok(())
            }
            0x77 => {
                self.shared.invalidate();
                self.stop(Reason::F8 as i32, false)
            }
            0x7b => {
                self.shared.invalidate();
                self.stop(Reason::F12 as i32, true)
            }
            _ => {
                self.shared.invalidate();
                self.engine.stop(&mut self.platform)?;
                let (x, y) = self.platform.cursor()?;
                self.shared.push(Event {
                    kind: POINT,
                    x,
                    y,
                    ..Event::default()
                });
                self.stop(Reason::Point as i32, false)
            }
        }
    }

    pub fn shutdown(&mut self) {
        if self.engine.stop(&mut self.platform).is_err() {
            let _ = self.engine.stop(&mut self.platform);
        }
    }
}
