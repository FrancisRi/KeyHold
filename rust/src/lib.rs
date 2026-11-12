mod engine;
#[cfg(test)]
mod tests;
#[cfg(windows)]
mod windows;
mod worker;

#[cfg(windows)]
mod abi {
    use super::engine::{Action, Kind};
    use super::windows::Runtime;
    use super::worker::{Command, Event, Profile};
    use std::collections::HashMap;
    use std::{ptr, slice};

    #[repr(C)]
    pub struct Binding {
        key: i32,
        first: i32,
        count: i32,
    }

    #[repr(C)]
    pub struct Step {
        kind: i32,
        key: i32,
        x: i32,
        y: i32,
        duration: i32,
        gap: i32,
    }

    // Callers own input buffers only for the duration of the call. The worker
    // receives an owned snapshot and never calls back into managed code.
    unsafe fn input<'a, T>(p: *const T, count: i32, max: i32) -> Option<&'a [T]> {
        if count < 0 || count > max || (count != 0 && p.is_null()) {
            return None;
        }
        Some(if count == 0 {
            &[]
        } else {
            slice::from_raw_parts(p, count as usize)
        })
    }

    fn key(value: i32) -> Option<usize> {
        (1..256).contains(&value).then_some(value as usize)
    }

    fn profile(
        bindings: &[Binding],
        steps: &[Step],
        suppress: bool,
        pause_on_focus_loss: bool,
    ) -> Option<Profile> {
        let mut result = HashMap::with_capacity(bindings.len());
        for binding in bindings {
            let start = usize::try_from(binding.first).ok()?;
            let count = usize::try_from(binding.count).ok()?;
            if !(1..=21).contains(&count) {
                return None;
            }
            let mut actions = Vec::with_capacity(count);
            for step in steps.get(start..start.checked_add(count)?)? {
                let p = (step.x, step.y);
                let kind = match step.kind {
                    1 => Kind::Tap(key(step.key)?),
                    2 => Kind::Click(p),
                    3 => Kind::Move(p),
                    4 => Kind::Hold(p),
                    _ => return None,
                };
                if !(1..=60_000).contains(&step.duration) || !(0..=60_000).contains(&step.gap) {
                    return None;
                }
                actions.push(Action {
                    kind,
                    duration: step.duration as u64,
                    gap: step.gap as u64,
                });
            }
            if result.insert(key(binding.key)?, actions).is_some() {
                return None;
            }
        }
        Some(Profile {
            bindings: result,
            suppress,
            pause_on_focus_loss,
        })
    }

    #[no_mangle]
    pub extern "C" fn kh_abi_version() -> u32 {
        2
    }

    #[no_mangle]
    pub unsafe extern "C" fn kh_create(
        keys: *const i32,
        count: i32,
        error: *mut u32,
    ) -> *mut Runtime {
        let mut supported = [false; 256];
        let Some(keys) = input(keys, count, 255) else {
            return ptr::null_mut();
        };
        for &value in keys {
            let Some(key) = key(value) else {
                return ptr::null_mut();
            };
            supported[key] = true;
        }
        match Runtime::spawn(supported) {
            Ok(runtime) => Box::into_raw(Box::new(runtime)),
            Err(code) => {
                if let Some(error) = error.as_mut() {
                    *error = code;
                }
                ptr::null_mut()
            }
        }
    }

    #[no_mangle]
    pub unsafe extern "C" fn kh_destroy(handle: *mut Runtime) {
        if !handle.is_null() {
            drop(Box::from_raw(handle));
        }
    }

    #[no_mangle]
    pub unsafe extern "C" fn kh_ticket(handle: *const Runtime) -> i32 {
        handle.as_ref().map_or(0, |h| h.shared.ticket())
    }

    #[no_mangle]
    pub unsafe extern "C" fn kh_request_start(handle: *const Runtime) -> i32 {
        handle.as_ref().map_or(0, |h| h.shared.invalidate())
    }

    #[no_mangle]
    pub unsafe extern "C" fn kh_start(
        handle: *const Runtime,
        target: isize,
        ticket: i32,
        bindings: *const Binding,
        binding_count: i32,
        steps: *const Step,
        step_count: i32,
        suppress: i32,
        pause: i32,
    ) -> i32 {
        let Some(h) = handle.as_ref() else {
            return 0;
        };
        if h.shared.ticket() != ticket {
            return 1;
        }
        let (Some(bindings), Some(steps)) = (
            input(bindings, binding_count, 15),
            input(steps, step_count, 315),
        ) else {
            return -1;
        };
        let Some(profile) = profile(bindings, steps, suppress != 0, pause != 0) else {
            return -1;
        };
        i32::from(h.post(Command::Start(profile, target, ticket)))
    }

    #[no_mangle]
    pub unsafe extern "C" fn kh_stop(handle: *const Runtime, reason: i32, confirm: i32) {
        if let Some(h) = handle.as_ref() {
            h.shared.invalidate();
            h.post(Command::Stop(reason, confirm != 0));
        }
    }

    #[no_mangle]
    pub unsafe extern "C" fn kh_capture_key(handle: *const Runtime) -> i32 {
        let Some(h) = handle.as_ref() else {
            return 0;
        };
        h.shared.invalidate();
        i32::from(h.post(Command::CaptureKey))
    }

    #[no_mangle]
    pub unsafe extern "C" fn kh_set_notification_window(handle: *const Runtime, window: isize) {
        if let Some(h) = handle.as_ref() {
            h.set_notification_window(window);
        }
    }

    #[no_mangle]
    pub unsafe extern "C" fn kh_poll(handle: *const Runtime, event: *mut Event) -> i32 {
        let (Some(h), Some(event)) = (handle.as_ref(), event.as_mut()) else {
            return 0;
        };
        if let Some(next) = h.shared.poll() {
            *event = next;
            1
        } else {
            0
        }
    }

    #[cfg(test)]
    #[test]
    fn bridge_layout_and_snapshot_bounds() {
        use std::mem::size_of;
        assert_eq!(
            (size_of::<Event>(), size_of::<Binding>(), size_of::<Step>()),
            (40, 12, 24)
        );
        let steps = [Step {
            kind: 4,
            key: 0,
            x: -1920,
            y: 5,
            duration: 40,
            gap: 0,
        }];
        let valid = [Binding {
            key: 69,
            first: 0,
            count: 1,
        }];
        assert!(profile(&valid, &steps, true, true).is_some());
        let invalid = [Binding {
            key: 69,
            first: 1,
            count: 1,
        }];
        assert!(profile(&invalid, &steps, true, true).is_none());
    }
}
