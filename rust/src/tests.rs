use crate::engine::{Action, Engine, Error, Key, Kind, Output, Point, Result};
use crate::worker::{
    Command, Event, Platform, Profile, Reason, Shared, Worker, ERROR, KEY, POINT, RUNNING, START,
    STOPPED,
};
use std::collections::HashMap;
use std::sync::Arc;
use std::time::Duration;

#[derive(Default)]
struct Fake {
    time: Duration,
    foreground: isize,
    events: Vec<String>,
    fail_press: bool,
    fail_mouse_up: bool,
    fail_key_up: bool,
    physical: Option<Key>,
    release_cost: Duration,
}

impl Output for Fake {
    fn now(&self) -> Duration {
        self.time
    }
    fn is_down(&self, key: Key) -> bool {
        self.physical == Some(key)
    }
    fn press_at(&mut self, (x, y): Point) -> Result<()> {
        self.events.push(format!("down:{x},{y}"));
        if self.fail_press {
            Err(Error::Mouse(1))
        } else {
            Ok(())
        }
    }
    fn move_to(&mut self, (x, y): Point) -> Result<()> {
        self.events.push(format!("move:{x},{y}"));
        Ok(())
    }
    fn release_mouse(&mut self) -> Result<()> {
        self.events.push("up".into());
        self.time += self.release_cost;
        if self.fail_mouse_up {
            Err(Error::Mouse(2))
        } else {
            Ok(())
        }
    }
    fn press_key(&mut self, key: Key) -> Result<()> {
        self.events.push(format!("key-down:{key}"));
        Ok(())
    }
    fn release_key(&mut self, key: Key) -> Result<()> {
        self.events.push(format!("key-up:{key}"));
        self.time += self.release_cost;
        if self.fail_key_up {
            Err(Error::Keyboard(3))
        } else {
            Ok(())
        }
    }
}

impl Platform for Fake {
    fn foreground(&self) -> isize {
        self.foreground
    }
    fn own_window(&self, window: isize) -> bool {
        window == 99
    }
    fn cursor(&self) -> Result<Point> {
        Ok((-100, 75))
    }
    fn validate_point(&self, point: Point) -> Result<()> {
        if point.0 == 10000 {
            Err(Error::OutsideDisplay(point))
        } else {
            Ok(())
        }
    }
}

fn action(kind: Kind, duration: u64, gap: u64) -> Action {
    Action {
        kind,
        duration,
        gap,
    }
}
fn hold(x: i32, y: i32) -> Action {
    action(Kind::Hold((x, y)), 40, 0)
}
fn tap() -> Action {
    action(Kind::Tap(49), 40, 10)
}
fn simple() -> HashMap<Key, Vec<Action>> {
    HashMap::from([(69, vec![hold(100, 200)]), (70, vec![hold(-300, 400)])])
}
fn engine(bindings: HashMap<Key, Vec<Action>>) -> (Engine, Fake) {
    let (mut engine, mut out) = (Engine::new(), Fake::default());
    engine.configure(&mut out, bindings, true).unwrap();
    engine.start().unwrap();
    (engine, out)
}
fn advance(engine: &mut Engine, out: &mut Fake, time: u64) {
    out.time = ms(time);
    engine.advance(out).unwrap();
}

fn ms(time: u64) -> Duration {
    Duration::from_millis(time)
}

#[test]
fn continuous_takeover_matches_reference_trace() {
    let (mut engine, mut out) = engine(simple());
    for (key, down) in [(69, true), (70, true), (70, true), (69, false), (70, false)] {
        assert!(engine.key_event(&mut out, key, down).unwrap());
    }
    assert_eq!(out.events, ["down:100,200", "move:-300,400", "up"]);
}

#[test]
fn no_fallback_and_intercept_pairs_survive_reconfiguration() {
    let (mut engine, mut out) = engine(simple());
    for (key, down) in [(69, true), (70, true), (70, false), (69, true)] {
        assert!(engine.key_event(&mut out, key, down).unwrap());
    }
    assert_eq!(out.events, ["down:100,200", "move:-300,400", "up"]);
    engine.configure(&mut out, simple(), false).unwrap();
    assert!(engine.key_event(&mut out, 69, false).unwrap());
    engine.start().unwrap();
    assert!(!engine.key_event(&mut out, 69, true).unwrap());
    assert_eq!(out.events.last().unwrap(), "down:100,200");
}

#[test]
fn short_tap_completes_prefix_main_click_and_postfix() {
    let (mut engine, mut out) = engine(HashMap::from([(
        88,
        vec![
            tap(),
            action(Kind::Hold((500, 400)), 40, 10),
            action(Kind::Move((800, 400)), 40, 0),
        ],
    )]));
    engine.key_event(&mut out, 88, true).unwrap();
    engine.key_event(&mut out, 88, false).unwrap();
    for time in [39, 40, 49, 50, 89, 90, 99, 100] {
        advance(&mut engine, &mut out, time);
    }
    assert_eq!(
        out.events,
        [
            "key-down:49",
            "key-up:49",
            "down:500,400",
            "up",
            "move:800,400"
        ]
    );
    assert_eq!(engine.active_key, None);
    assert!(!engine.needs_release());
}

#[test]
fn an_existing_hold_releases_before_delayed_postfix() {
    let (mut engine, mut out) = engine(HashMap::from([(
        88,
        vec![action(Kind::Hold((500, 400)), 40, 40), tap()],
    )]));
    engine.key_event(&mut out, 88, true).unwrap();
    engine.key_event(&mut out, 88, false).unwrap();
    assert_eq!(out.events, ["down:500,400", "up"]);
    for time in [40, 80, 90] {
        advance(&mut engine, &mut out, time);
    }
    assert_eq!(
        out.events,
        ["down:500,400", "up", "key-down:49", "key-up:49"]
    );
}

#[test]
fn only_persistent_hold_is_inherited() {
    let (mut engine, mut out) = engine(HashMap::from([
        (69, vec![hold(100, 200)]),
        (70, vec![tap(), hold(-300, 400)]),
        (88, vec![action(Kind::Click((500, 400)), 40, 0)]),
    ]));
    for key in [69, 70, 88] {
        engine.key_event(&mut out, key, true).unwrap();
    }
    assert_eq!(
        out.events,
        [
            "down:100,200",
            "key-down:49",
            "key-up:49",
            "up",
            "down:500,400"
        ]
    );
    engine.key_event(&mut out, 88, false).unwrap();
    engine.key_event(&mut out, 69, false).unwrap();
    engine.key_event(&mut out, 69, true).unwrap();
    assert_eq!(out.events[5..], ["up", "down:100,200"]);
}

#[test]
fn repressed_trigger_replaces_pending_short_tap() {
    let (mut engine, mut out) = engine(HashMap::from([(88, vec![tap(), hold(500, 400)])]));
    for down in [true, false, true] {
        engine.key_event(&mut out, 88, down).unwrap();
    }
    advance(&mut engine, &mut out, 40);
    advance(&mut engine, &mut out, 50);
    engine.key_event(&mut out, 88, false).unwrap();
    assert_eq!(
        out.events,
        [
            "key-down:49",
            "key-up:49",
            "key-down:49",
            "key-up:49",
            "down:500,400",
            "up"
        ]
    );
}

#[test]
fn gap_starts_after_actual_release() {
    let (mut engine, mut out) = engine(HashMap::from([(88, vec![tap(), hold(500, 400)])]));
    engine.key_event(&mut out, 88, true).unwrap();
    out.release_cost = Duration::from_micros(7750);
    advance(&mut engine, &mut out, 54);
    let deadline = Duration::from_micros(71_750);
    assert_eq!(engine.deadline(), Some(deadline));
    out.time = deadline - Duration::from_nanos(100);
    engine.advance(&mut out).unwrap();
    assert_eq!(out.events, ["key-down:49", "key-up:49"]);
    out.time = deadline;
    engine.advance(&mut out).unwrap();
    assert_eq!(out.events.last().unwrap(), "down:500,400");
}

#[test]
fn release_latches_and_physical_keys_survive_failures() {
    let (mut engine, mut out) = engine(HashMap::from([(69, vec![hold(100, 200), tap()])]));
    engine.key_event(&mut out, 69, true).unwrap();
    out.fail_key_up = true;
    out.fail_mouse_up = true;
    assert_eq!(engine.stop(&mut out), Err(Error::Keyboard(3)));
    assert_eq!(
        out.events,
        ["down:100,200", "key-down:49", "key-up:49", "up"]
    );
    assert_eq!(engine.start(), Err(Error::NeedsRelease));
    assert!(engine.key_event(&mut out, 69, false).unwrap());
    out.fail_key_up = false;
    out.fail_mouse_up = false;
    engine.stop(&mut out).unwrap();
    assert!(!engine.needs_release());
    assert_eq!(out.events[4..], ["key-up:49", "up"]);
    engine.start().unwrap();
    out.fail_press = true;
    assert_eq!(engine.key_event(&mut out, 69, true), Err(Error::Mouse(1)));
    assert!(!engine.enabled);
    assert_eq!(out.events[6..], ["down:100,200", "up"]);
    assert!(engine.swallowed(69));
}

#[test]
fn physically_held_tap_key_is_not_synthesized() {
    let (mut engine, mut out) = engine(HashMap::from([(88, vec![tap(), hold(500, 400)])]));
    out.physical = Some(49);
    assert_eq!(
        engine.key_event(&mut out, 88, true),
        Err(Error::PhysicalKey(49))
    );
    assert!(out.events.is_empty());
    assert!(!engine.enabled);
}

fn worker() -> Worker<Fake> {
    worker_with_foreground_notifications(false)
}
fn worker_with_foreground_notifications(foreground_notifications: bool) -> Worker<Fake> {
    let mut supported = [false; 256];
    supported[69] = true;
    supported[70] = true;
    Worker::new(
        Fake {
            foreground: 42,
            ..Fake::default()
        },
        supported,
        Arc::new(Shared::default()),
        foreground_notifications,
    )
}
fn profile(bindings: HashMap<Key, Vec<Action>>) -> Profile {
    Profile {
        bindings,
        suppress: true,
        pause_on_focus_loss: true,
    }
}
fn events(w: &Worker<Fake>) -> Vec<Event> {
    std::iter::from_fn(|| w.shared.poll()).collect()
}
fn press(w: &mut Worker<Fake>, key: Key) {
    assert!(w.key(key, true));
    assert!(w.key(key, false));
}
fn enable(w: &mut Worker<Fake>, bindings: HashMap<Key, Vec<Action>>) {
    press(w, 0x77);
    let request = events(w).pop().unwrap();
    assert_eq!((request.kind, request.cue, request.window), (START, 0, 42));
    w.command(Command::Start(
        profile(bindings),
        request.window,
        request.ticket,
    ));
    let running = events(w).pop().unwrap();
    assert_eq!(
        (running.kind, running.cue, running.ticket),
        (RUNNING, 1, request.ticket)
    );
}

#[test]
fn event_notifications_coalesce_and_rearm_after_the_queue_drains() {
    let mut w = worker();
    press(&mut w, 0x77);
    assert!(!w.shared.notify(|_| panic!("no destination yet")));
    w.shared.set_notification_window(12);
    assert!(!w.shared.notify(|window| {
        assert_eq!(window, 12);
        true
    }));
    press(&mut w, 0x7b);
    assert!(!w.shared.notify(|_| panic!("notification already posted")));
    let batch = events(&w);
    assert_eq!((batch[0].kind, batch[1].kind), (START, STOPPED));
    assert!(!w.shared.notify(|_| panic!("queue is empty")));

    press(&mut w, 0x75);
    assert!(w.shared.notify(|_| false));
    assert!(!w.shared.notify(|window| {
        assert_eq!(window, 12);
        true
    }));
    w.shared.set_notification_window(24);
    assert!(!w.shared.notify(|window| {
        assert_eq!(window, 24);
        true
    }));
    let batch = events(&w);
    assert_eq!((batch[0].kind, batch[1].kind), (POINT, STOPPED));
}

#[test]
fn f12_invalidates_delayed_ui_start_and_does_not_repeat() {
    let mut w = worker();
    press(&mut w, 0x77);
    let ticket = events(&w)[0].ticket;
    assert!(w.key(0x7b, true));
    assert!(w.key(0x7b, true));
    assert!(w.key(0x7b, false));
    w.command(Command::Start(profile(simple()), 42, ticket));
    let events = events(&w);
    assert_eq!(events.len(), 1);
    assert_eq!(
        (events[0].kind, events[0].reason, events[0].cue),
        (STOPPED, Reason::F12 as i32, 3)
    );
    assert!(!w.key(69, true));
    assert!(w.platform.events.is_empty());
}

#[test]
fn failed_start_never_announces_enabled() {
    let mut w = worker();
    let ticket = w.shared.invalidate();
    w.command(Command::Start(profile(simple()), 99, ticket));
    let event = events(&w)[0];
    assert_eq!(
        (event.kind, event.reason, event.cue),
        (ERROR, Reason::Foreground as i32, 4)
    );
    let ticket = w.shared.invalidate();
    w.command(Command::Start(
        profile(HashMap::from([(69, vec![hold(10000, -25)])])),
        42,
        ticket,
    ));
    let event = events(&w)[0];
    assert_eq!(
        (event.kind, event.reason, event.x, event.y),
        (ERROR, Reason::OutsideDisplay as i32, 10000, -25)
    );
}

#[test]
fn focus_is_checked_before_delayed_actions_and_pause_releases() {
    let mut w = worker();
    enable(
        &mut w,
        HashMap::from([(88, vec![action(Kind::Tap(49), 10, 10), hold(500, 400)])]),
    );
    w.key(88, true);
    assert_eq!(w.service(), Some(ms(10)));
    w.platform.time = ms(9);
    assert_eq!(w.service(), Some(ms(10)));
    assert_eq!(w.platform.events, ["key-down:49"]);
    w.platform.time = ms(10);
    assert_eq!(w.service(), Some(ms(20)));
    assert_eq!(w.platform.events, ["key-down:49", "key-up:49"]);
    w.platform.foreground = 52;
    w.platform.time = ms(20);
    assert_eq!(w.service(), None);
    let paused = events(&w)[0];
    assert_eq!(
        (paused.kind, paused.reason, paused.cue),
        (STOPPED, Reason::Focus as i32, 2)
    );
    assert!(w.key(88, false));
    assert_eq!(w.platform.events, ["key-down:49", "key-up:49"]);
}

#[test]
fn paused_worker_has_no_deadline_and_enabled_worker_still_checks_focus() {
    let mut w = worker();
    assert_eq!(w.service(), None);
    w.platform.time = ms(5000);
    assert_eq!(w.service(), None);
    enable(&mut w, simple());
    assert_eq!(w.service(), Some(ms(5025)));
    assert!(w.key(69, true));

    w.platform.foreground = 52;
    w.platform.time = ms(5024);
    assert_eq!(w.service(), Some(ms(5025)));
    assert!(events(&w).is_empty());
    w.platform.time = ms(5025);
    assert_eq!(w.service(), None);
    assert_eq!(events(&w)[0].reason, Reason::Focus as i32);
    assert_eq!(w.platform.events, ["down:100,200", "up"]);
    assert!(w.key(69, false));

    press(&mut w, 0x75);
    press(&mut w, 0x7b);
    let paused = events(&w);
    assert_eq!((paused[0].kind, paused[2].cue), (POINT, 3));
    assert_eq!(w.service(), None);
}

#[test]
fn foreground_notifications_extend_only_idle_enabled_maintenance() {
    let mut w = worker_with_foreground_notifications(true);
    enable(&mut w, simple());
    assert_eq!(w.service(), Some(ms(250)));
    w.platform.time = ms(25);
    assert_eq!(w.service(), Some(ms(250)));

    assert!(w.key(69, true));
    assert_eq!(w.service(), Some(ms(50)));
    w.platform.time = ms(30);
    w.platform.foreground = 52;
    w.foreground_changed();
    assert_eq!(events(&w)[0].reason, Reason::Focus as i32);
    assert_eq!(w.platform.events, ["down:100,200", "up"]);
    assert!(w.key(69, false));
    assert_eq!(w.service(), None);
}

#[test]
fn foreground_notification_pauses_an_idle_worker_before_its_watchdog() {
    let mut w = worker_with_foreground_notifications(true);
    enable(&mut w, simple());
    assert_eq!(w.service(), Some(ms(250)));
    w.platform.time = ms(10);
    w.platform.foreground = 52;
    w.foreground_changed();
    assert_eq!(events(&w)[0].reason, Reason::Focus as i32);
    assert_eq!(w.service(), None);
}

#[test]
fn paused_worker_retries_owned_inputs_until_both_are_released() {
    let mut w = worker_with_foreground_notifications(true);
    enable(&mut w, HashMap::from([(69, vec![hold(100, 200), tap()])]));
    assert_eq!(w.service(), Some(ms(250)));
    assert!(w.key(69, true));
    w.platform.fail_key_up = true;
    w.platform.fail_mouse_up = true;
    w.command(Command::Stop(Reason::F12 as i32, true));
    assert_eq!(events(&w)[0].kind, ERROR);
    w.platform.events.clear();

    w.platform.time = ms(24);
    assert_eq!(w.service(), Some(ms(25)));
    assert!(w.platform.events.is_empty());
    w.platform.time = ms(25);
    w.platform.fail_mouse_up = false;
    assert_eq!(w.service(), Some(ms(50)));
    assert_eq!(w.platform.events, ["key-up:49", "up"]);
    w.platform.time = ms(50);
    w.platform.fail_key_up = false;
    assert_eq!(w.service(), None);
    assert_eq!(w.platform.events, ["key-up:49", "up", "key-up:49"]);
    assert!(events(&w).is_empty());
}

#[test]
fn f6_and_key_capture_are_paused_and_keep_pairs() {
    let mut w = worker();
    enable(&mut w, simple());
    assert!(w.key(69, true));
    press(&mut w, 0x75);
    let captured = events(&w);
    assert_eq!(
        (
            captured[0].kind,
            captured[0].x,
            captured[0].y,
            captured[1].cue
        ),
        (POINT, -100, 75, 2)
    );
    assert_eq!(w.platform.events, ["down:100,200", "up"]);
    assert!(w.key(69, false));
    w.command(Command::CaptureKey);
    assert_eq!(events(&w)[0].cue, 0);
    assert!(!w.key(16, true));
    assert!(!w.key(16, false));
    press(&mut w, 70);
    assert_eq!((events(&w)[0].kind, w.platform.events.len()), (KEY, 2));
}

#[test]
fn f8_pause_and_shutdown_clean_up_without_replaying() {
    let mut w = worker();
    enable(&mut w, simple());
    w.key(69, true);
    w.key(70, true);
    w.key(69, false);
    assert!(w.key(0x77, true));
    assert!(w.key(0x77, true));
    assert!(w.key(0x77, false));
    let pause = events(&w);
    assert_eq!(pause.len(), 1);
    assert_eq!((pause[0].reason, pause[0].cue), (Reason::F8 as i32, 2));
    w.shutdown();
    assert_eq!(w.platform.events, ["down:100,200", "move:-300,400", "up"]);
}
