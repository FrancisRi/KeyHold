use std::collections::HashMap;
use std::time::Duration;

pub(crate) type Key = usize;
pub(crate) type Point = (i32, i32);
pub(crate) type Result<T> = std::result::Result<T, Error>;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum Error {
    Empty,
    NeedsRelease,
    PhysicalKey(Key),
    Mouse(u32),
    Keyboard(u32),
    DisplayChanged,
    OutsideDisplay(Point),
    Foreground,
    Cursor(u32),
}

pub(crate) trait Output {
    fn now(&self) -> Duration;
    fn is_down(&self, key: Key) -> bool;
    fn press_at(&mut self, point: Point) -> Result<()>;
    fn move_to(&mut self, point: Point) -> Result<()>;
    fn release_mouse(&mut self) -> Result<()>;
    fn press_key(&mut self, key: Key) -> Result<()>;
    fn release_key(&mut self, key: Key) -> Result<()>;
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum Kind {
    Tap(Key),
    Click(Point),
    Move(Point),
    Hold(Point),
}

#[derive(Clone, Copy)]
pub(crate) struct Action {
    pub kind: Kind,
    pub duration: u64,
    pub gap: u64,
}

struct Sequence {
    index: usize,
    due: Duration,
    finishing_gap: Option<u64>,
}

#[derive(Clone, Copy, Eq, PartialEq)]
enum MouseHold {
    None,
    Persistent,
    Click,
}

pub(crate) struct Engine {
    pub enabled: bool,
    pub active_key: Option<Key>,
    bindings: HashMap<Key, Vec<Action>>,
    suppress: bool,
    down: [bool; 256],
    swallowed: [bool; 256],
    mouse: MouseHold,
    held_key: Option<Key>,
    sequence: Option<Sequence>,
    trigger_released: bool,
}

impl Engine {
    pub fn new() -> Self {
        Self {
            enabled: false,
            active_key: None,
            bindings: HashMap::new(),
            suppress: false,
            down: [false; 256],
            swallowed: [false; 256],
            mouse: MouseHold::None,
            held_key: None,
            sequence: None,
            trigger_released: false,
        }
    }

    pub fn configure(
        &mut self,
        out: &mut impl Output,
        bindings: HashMap<Key, Vec<Action>>,
        suppress: bool,
    ) -> Result<()> {
        self.stop(out)?;
        self.bindings = bindings;
        self.suppress = suppress;
        Ok(())
    }

    pub fn start(&mut self) -> Result<()> {
        if self.bindings.is_empty() {
            return Err(Error::Empty);
        }
        if self.needs_release() {
            return Err(Error::NeedsRelease);
        }
        self.enabled = true;
        Ok(())
    }

    pub fn stop(&mut self, out: &mut impl Output) -> Result<()> {
        self.enabled = false;
        self.active_key = None;
        self.sequence = None;
        // Retain physical down and interception bookkeeping until key-up.
        self.release_outputs(out, false)
    }

    pub fn needs_release(&self) -> bool {
        self.mouse != MouseHold::None || self.held_key.is_some()
    }

    pub fn mark_down(&mut self, key: Key) {
        self.down[key] = true;
    }

    pub fn swallowed(&self, key: Key) -> bool {
        self.swallowed[key]
    }

    pub fn deadline(&self) -> Option<Duration> {
        self.sequence.as_ref().map(|s| s.due)
    }

    fn release_mouse(&mut self, out: &mut impl Output) -> Result<()> {
        out.release_mouse()?;
        self.mouse = MouseHold::None;
        Ok(())
    }

    fn release_outputs(&mut self, out: &mut impl Output, keep_hold: bool) -> Result<()> {
        let keyboard = if let Some(key) = self.held_key {
            out.release_key(key).map(|()| self.held_key = None)
        } else {
            Ok(())
        };
        let mouse =
            if self.mouse != MouseHold::None && (!keep_hold || self.mouse == MouseHold::Click) {
                self.release_mouse(out)
            } else {
                Ok(())
            };
        // Both are attempted even if the first release fails.
        keyboard.and(mouse)
    }

    pub fn advance(&mut self, out: &mut impl Output) -> Result<()> {
        let result = self.advance_core(out);
        if result.is_err() {
            let _ = self.stop(out);
        }
        result
    }

    fn advance_core(&mut self, out: &mut impl Output) -> Result<()> {
        while let (true, Some(key), Some(s)) =
            (self.enabled, self.active_key, self.sequence.as_ref())
        {
            if out.now() < s.due {
                break;
            }
            let index = s.index;
            if let Some(gap) = s.finishing_gap {
                self.release_outputs(out, true)?;
                self.sequence = Some(Sequence {
                    index: index + 1,
                    due: out.now() + Duration::from_millis(gap),
                    finishing_gap: None,
                });
                if gap != 0 {
                    break;
                }
                continue;
            }
            let Some(action) = self.bindings[&key].get(index).copied() else {
                self.sequence = None;
                if self.trigger_released {
                    self.active_key = None;
                    self.release_outputs(out, false)?;
                }
                break;
            };
            let finite = match action.kind {
                Kind::Tap(vk) => {
                    if self.down[vk] || out.is_down(vk) {
                        return Err(Error::PhysicalKey(vk));
                    }
                    // Track the output before calling the OS: it can partially succeed.
                    self.held_key = Some(vk);
                    out.press_key(vk)?;
                    true
                }
                Kind::Click(point) | Kind::Hold(point)
                    if self.trigger_released || matches!(action.kind, Kind::Click(_)) =>
                {
                    if self.mouse != MouseHold::None {
                        self.release_mouse(out)?;
                    }
                    self.mouse = MouseHold::Click;
                    out.press_at(point)?;
                    true
                }
                Kind::Hold(point) => {
                    if self.mouse == MouseHold::None {
                        self.mouse = MouseHold::Persistent;
                        out.press_at(point)?;
                    } else {
                        out.move_to(point)?;
                    }
                    false
                }
                Kind::Move(point) => {
                    out.move_to(point)?;
                    false
                }
                Kind::Click(_) => unreachable!("click is always finite"),
            };
            self.sequence = Some(if finite {
                Sequence {
                    index,
                    due: out.now() + Duration::from_millis(action.duration),
                    finishing_gap: Some(action.gap),
                }
            } else {
                Sequence {
                    index: index + 1,
                    due: out.now() + Duration::from_millis(action.gap),
                    finishing_gap: None,
                }
            });
            if finite || action.gap != 0 {
                break;
            }
        }
        Ok(())
    }

    pub fn key_event(&mut self, out: &mut impl Output, key: Key, down: bool) -> Result<bool> {
        let result = self.key_core(out, key, down);
        if result.is_err() {
            let _ = self.stop(out);
        }
        result
    }

    fn key_core(&mut self, out: &mut impl Output, key: Key, down: bool) -> Result<bool> {
        if !down {
            self.down[key] = false;
            let block = std::mem::take(&mut self.swallowed[key]);
            if self.active_key == Some(key) {
                self.trigger_released = true;
                if self.mouse == MouseHold::Persistent {
                    self.release_mouse(out)?;
                }
                if self.sequence.is_none() {
                    self.active_key = None;
                }
            }
            return Ok(block);
        }
        if std::mem::replace(&mut self.down[key], true) {
            return Ok(self.swallowed[key]);
        }
        if !self.enabled || !self.bindings.contains_key(&key) {
            return Ok(false);
        }
        if self.suppress {
            self.swallowed[key] = true;
        }
        self.sequence = None;
        self.release_outputs(out, true)?;
        self.active_key = Some(key);
        self.trigger_released = false;
        self.sequence = Some(Sequence {
            index: 0,
            due: out.now(),
            finishing_gap: None,
        });
        self.advance_core(out)?;
        Ok(self.suppress)
    }
}
