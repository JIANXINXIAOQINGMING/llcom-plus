//! serial_monitor_hook — injected x64 capture hook.
//!
//! Safety policy:
//! * x86 injection is explicitly unsupported; there is no fixed-length patch.
//! * DllMain never connects IPC or installs/removes hooks.
//! * x64 detours are initialized first, enabled with reverse rollback, and are
//!   never removed from a live process.
//! * capture deactivation closes IPC only after new capture calls are gated and
//!   all admitted calls have drained.

#![allow(non_snake_case)]
#![allow(clippy::missing_safety_doc)]

use std::ffi::c_void;

use windows::Win32::System::SystemServices::DLL_PROCESS_ATTACH;

const HOOK_INIT_OK: u32 = 1;
#[cfg(target_arch = "x86_64")]
const HOOK_DEACTIVATE_OK: u32 = 1;

#[cfg(target_arch = "x86_64")]
const HOOK_ERROR_BAD_CONFIG: u32 = 101;
#[cfg(target_arch = "x86_64")]
const HOOK_ERROR_PIPE: u32 = 102;
#[cfg(target_arch = "x86_64")]
const HOOK_ERROR_INSTALL: u32 = 103;
#[cfg(target_arch = "x86")]
const HOOK_ERROR_X86_DISABLED: u32 = 104;
#[cfg(target_arch = "x86_64")]
const HOOK_ERROR_BUSY: u32 = 105;
#[cfg(target_arch = "x86_64")]
const HOOK_ERROR_DRAIN_TIMEOUT: u32 = 106;

#[link(name = "kernel32")]
extern "system" {
    fn DisableThreadLibraryCalls(module: *mut c_void) -> i32;
}

#[cfg(target_arch = "x86")]
#[no_mangle]
pub unsafe extern "system" fn SerialMonitorInitialize(_parameter: *mut c_void) -> u32 {
    HOOK_ERROR_X86_DISABLED
}

#[cfg(target_arch = "x86")]
#[no_mangle]
pub unsafe extern "system" fn SerialMonitorDeactivate(_parameter: *mut c_void) -> u32 {
    HOOK_INIT_OK
}

#[cfg(target_arch = "x86_64")]
mod x64 {
    use super::*;
    use retour::GenericDetour;
    use std::cell::Cell;
    use std::collections::HashMap;
    use std::ffi::CString;
    use std::sync::atomic::{
        AtomicBool, AtomicIsize, AtomicU32, AtomicU64, AtomicU8, AtomicUsize, Ordering,
    };
    use std::sync::{Mutex, MutexGuard, OnceLock, RwLock};
    use std::time::{Duration, Instant};
    use windows::core::{PCSTR, PCWSTR};
    use windows::Win32::Foundation::{
        CloseHandle, GetLastError, SetLastError, HANDLE, INVALID_HANDLE_VALUE,
    };
    use windows::Win32::Storage::FileSystem::{
        CreateFileW, FILE_FLAGS_AND_ATTRIBUTES, FILE_SHARE_MODE, OPEN_EXISTING,
    };
    use windows::Win32::System::LibraryLoader::{GetModuleHandleW, GetProcAddress};
    use windows::Win32::System::Memory::{
        MapViewOfFile, OpenFileMappingW, UnmapViewOfFile, FILE_MAP_READ,
    };
    use windows::Win32::System::Threading::{GetCurrentProcessId, GetCurrentThreadId, Sleep};

    const WIRE_MAGIC: u32 = 0x334D_534C;
    const ABI_VERSION: u16 = 3;
    const MAX_FRAGMENT_DATA: usize = 8192;
    const MAX_CAPTURE_BYTES_PER_TRANSFER: usize = 1024 * 1024;
    const WIRE_HEADER_SIZE: usize = 64;
    const WIRE_PACKET_SIZE: usize = WIRE_HEADER_SIZE + MAX_FRAGMENT_DATA;

    const CONFIG_MAGIC: u32 = 0x3343_4D53;
    const CONFIG_PIPE_CHARS: usize = 240;
    const CONFIG_SIZE: usize = 512;

    const STATE_DISCONNECT: u32 = 2;
    const STATE_RECEIVE: u32 = 3;
    const STATE_SEND: u32 = 4;

    const FLAG_FIRST: u32 = 0x0001;
    const FLAG_LAST: u32 = 0x0002;
    const FLAG_TRUNCATED: u32 = 0x0004;

    const ERROR_IO_INCOMPLETE_RAW: u32 = 996;
    const ERROR_IO_PENDING_RAW: u32 = 997;
    const WAIT_TIMEOUT_RAW: u32 = 258;
    const PIPE_NOWAIT_RAW: u32 = 0x0000_0001;
    const MAX_PENDING_OPERATIONS: usize = 4096;
    const INVALID_PIPE: isize = -1;

    #[link(name = "kernel32")]
    extern "system" {
        fn SetNamedPipeHandleState(
            pipe: *mut c_void,
            mode: *const u32,
            max_collection_count: *const u32,
            collect_data_timeout: *const u32,
        ) -> i32;
    }

    #[repr(C, packed(1))]
    #[derive(Clone, Copy)]
    struct MonitorPacket {
        magic: u32,
        abi_version: u16,
        header_size: u16,
        generation: u64,
        sequence: u64,
        com_port: u32,
        comm_state: u32,
        file_handle: u64,
        total_length: u32,
        fragment_offset: u32,
        data_length: u32,
        flags: u32,
        native_dropped_packets: u64,
        data: [u8; MAX_FRAGMENT_DATA],
    }

    const _: [(); WIRE_PACKET_SIZE] = [(); std::mem::size_of::<MonitorPacket>()];
    const _: [(); WIRE_HEADER_SIZE] = [(); std::mem::offset_of!(MonitorPacket, data)];

    #[repr(C)]
    #[derive(Clone, Copy)]
    struct SharedConfig {
        magic: u32,
        abi_version: u16,
        struct_size: u16,
        generation: u64,
        selected_com: u32,
        host_process_id: u32,
        pipe_name_length: u16,
        reserved: u16,
        pipe_name: [u16; CONFIG_PIPE_CHARS],
    }

    const _: [(); CONFIG_SIZE] = [(); std::mem::size_of::<SharedConfig>()];

    #[repr(C)]
    struct OverlappedEntry {
        completion_key: usize,
        overlapped: *mut c_void,
        internal: usize,
        bytes_transferred: u32,
    }

    type FnCreateFileW =
        unsafe extern "system" fn(*const u16, u32, u32, *mut c_void, u32, u32, isize) -> isize;
    type FnCreateFileA =
        unsafe extern "system" fn(*const u8, u32, u32, *mut c_void, u32, u32, isize) -> isize;
    type FnReadFile =
        unsafe extern "system" fn(isize, *mut c_void, u32, *mut u32, *mut c_void) -> i32;
    type FnWriteFile =
        unsafe extern "system" fn(isize, *const c_void, u32, *mut u32, *mut c_void) -> i32;
    type FnCloseHandle = unsafe extern "system" fn(isize) -> i32;
    type FnGetOverlappedResult =
        unsafe extern "system" fn(isize, *mut c_void, *mut u32, i32) -> i32;
    type FnGetOverlappedResultEx =
        unsafe extern "system" fn(isize, *mut c_void, *mut u32, u32, i32) -> i32;
    type FnCancelIo = unsafe extern "system" fn(isize) -> i32;
    type FnCancelIoEx = unsafe extern "system" fn(isize, *mut c_void) -> i32;
    type FnGetQueuedCompletionStatus =
        unsafe extern "system" fn(isize, *mut u32, *mut usize, *mut *mut c_void, u32) -> i32;
    type FnGetQueuedCompletionStatusEx =
        unsafe extern "system" fn(isize, *mut OverlappedEntry, u32, *mut u32, u32, i32) -> i32;

    struct HookSet {
        create_file_w: GenericDetour<FnCreateFileW>,
        create_file_a: GenericDetour<FnCreateFileA>,
        read_file: GenericDetour<FnReadFile>,
        write_file: GenericDetour<FnWriteFile>,
        close_handle: GenericDetour<FnCloseHandle>,
        get_overlapped_result: GenericDetour<FnGetOverlappedResult>,
        get_overlapped_result_ex: GenericDetour<FnGetOverlappedResultEx>,
        cancel_io: GenericDetour<FnCancelIo>,
        cancel_io_ex: GenericDetour<FnCancelIoEx>,
        get_queued_completion_status: GenericDetour<FnGetQueuedCompletionStatus>,
        get_queued_completion_status_ex: GenericDetour<FnGetQueuedCompletionStatusEx>,
    }

    static HOOKS: OnceLock<HookSet> = OnceLock::new();

    fn hooks() -> &'static HookSet {
        HOOKS
            .get()
            .expect("serial monitor detours must be constructed before enable")
    }

    #[derive(Clone, Copy, Debug, Eq, PartialEq)]
    struct HandleInfo {
        port: u32,
        generation: u64,
    }

    #[derive(Clone, Copy, Debug, Eq, PartialEq)]
    enum PendingKind {
        Read,
        Write,
    }

    #[derive(Clone, Copy)]
    struct PendingOperation {
        kind: PendingKind,
        port: u32,
        handle: isize,
        buffer: usize,
        requested_length: u32,
        handle_generation: u64,
        issuer_thread_id: u32,
    }

    static COM_HANDLES: OnceLock<RwLock<HashMap<isize, HandleInfo>>> = OnceLock::new();
    static PENDING: OnceLock<RwLock<HashMap<usize, PendingOperation>>> = OnceLock::new();
    static LIFECYCLE_GATE: OnceLock<Mutex<()>> = OnceLock::new();

    static PIPE_HANDLE: AtomicIsize = AtomicIsize::new(INVALID_PIPE);
    static SELECTED_COM: AtomicU32 = AtomicU32::new(0);
    static SESSION_GENERATION: AtomicU64 = AtomicU64::new(0);
    static NEXT_HANDLE_GENERATION: AtomicU64 = AtomicU64::new(1);
    static NEXT_SEQUENCE: AtomicU64 = AtomicU64::new(1);
    static NATIVE_DROPPED: AtomicU64 = AtomicU64::new(0);
    static ACCEPTING_EVENTS: AtomicBool = AtomicBool::new(false);
    static ACTIVE_CAPTURE_CALLS: AtomicUsize = AtomicUsize::new(0);
    // 0 = uninitialized, 1 = installing, 2 = installed, 3 = failed.
    static HOOK_STATE: AtomicU8 = AtomicU8::new(0);

    thread_local! {
        static IN_HOOK: Cell<bool> = const { Cell::new(false) };
    }

    fn com_handles() -> &'static RwLock<HashMap<isize, HandleInfo>> {
        COM_HANDLES.get_or_init(|| RwLock::new(HashMap::new()))
    }

    fn pending_operations() -> &'static RwLock<HashMap<usize, PendingOperation>> {
        PENDING.get_or_init(|| RwLock::new(HashMap::new()))
    }

    fn lifecycle_gate() -> &'static Mutex<()> {
        LIFECYCLE_GATE.get_or_init(|| Mutex::new(()))
    }

    fn lock_recover<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
        match mutex.lock() {
            Ok(guard) => guard,
            Err(poisoned) => poisoned.into_inner(),
        }
    }

    struct CaptureCallGuard;

    impl CaptureCallGuard {
        fn enter() -> Option<Self> {
            if !ACCEPTING_EVENTS.load(Ordering::Acquire) {
                return None;
            }
            ACTIVE_CAPTURE_CALLS.fetch_add(1, Ordering::AcqRel);
            if !ACCEPTING_EVENTS.load(Ordering::Acquire) {
                ACTIVE_CAPTURE_CALLS.fetch_sub(1, Ordering::AcqRel);
                return None;
            }
            Some(Self)
        }
    }

    impl Drop for CaptureCallGuard {
        fn drop(&mut self) {
            ACTIVE_CAPTURE_CALLS.fetch_sub(1, Ordering::Release);
        }
    }

    fn add_native_drops(count: u64) {
        let _ = NATIVE_DROPPED.fetch_update(Ordering::AcqRel, Ordering::Acquire, |current| {
            Some(current.saturating_add(count))
        });
    }

    unsafe fn call_original_create_file_w(
        name: *const u16,
        access: u32,
        share: u32,
        security: *mut c_void,
        disposition: u32,
        flags: u32,
        template: isize,
    ) -> isize {
        hooks()
            .create_file_w
            .call(name, access, share, security, disposition, flags, template)
    }

    unsafe fn call_original_create_file_a(
        name: *const u8,
        access: u32,
        share: u32,
        security: *mut c_void,
        disposition: u32,
        flags: u32,
        template: isize,
    ) -> isize {
        hooks()
            .create_file_a
            .call(name, access, share, security, disposition, flags, template)
    }

    unsafe fn call_original_read_file(
        handle: isize,
        buffer: *mut c_void,
        length: u32,
        transferred: *mut u32,
        overlapped: *mut c_void,
    ) -> i32 {
        hooks()
            .read_file
            .call(handle, buffer, length, transferred, overlapped)
    }

    unsafe fn call_original_write_file(
        handle: isize,
        buffer: *const c_void,
        length: u32,
        transferred: *mut u32,
        overlapped: *mut c_void,
    ) -> i32 {
        hooks()
            .write_file
            .call(handle, buffer, length, transferred, overlapped)
    }

    unsafe fn call_original_close_handle(handle: isize) -> i32 {
        hooks().close_handle.call(handle)
    }

    unsafe fn call_original_get_overlapped_result(
        handle: isize,
        overlapped: *mut c_void,
        transferred: *mut u32,
        wait: i32,
    ) -> i32 {
        hooks()
            .get_overlapped_result
            .call(handle, overlapped, transferred, wait)
    }

    unsafe fn call_original_get_overlapped_result_ex(
        handle: isize,
        overlapped: *mut c_void,
        transferred: *mut u32,
        timeout: u32,
        alertable: i32,
    ) -> i32 {
        hooks()
            .get_overlapped_result_ex
            .call(handle, overlapped, transferred, timeout, alertable)
    }

    unsafe fn get_proc(module: &str, proc: &str) -> Option<*const c_void> {
        let module_wide: Vec<u16> = module.encode_utf16().chain(std::iter::once(0)).collect();
        let module = GetModuleHandleW(PCWSTR(module_wide.as_ptr())).ok()?;
        let proc = CString::new(proc).ok()?;
        let address = GetProcAddress(module, PCSTR(proc.as_ptr() as *const u8))?;
        Some(address as *const c_void)
    }

    unsafe fn resolve_proc(proc: &str) -> Result<*const c_void, String> {
        get_proc("kernelbase.dll", proc)
            .or_else(|| get_proc("kernel32.dll", proc))
            .ok_or_else(|| format!("GetProcAddress failed for {proc}"))
    }

    struct HookTargets {
        create_file_w: FnCreateFileW,
        create_file_a: FnCreateFileA,
        read_file: FnReadFile,
        write_file: FnWriteFile,
        close_handle: FnCloseHandle,
        get_overlapped_result: FnGetOverlappedResult,
        get_overlapped_result_ex: FnGetOverlappedResultEx,
        cancel_io: FnCancelIo,
        cancel_io_ex: FnCancelIoEx,
        get_queued_completion_status: FnGetQueuedCompletionStatus,
        get_queued_completion_status_ex: FnGetQueuedCompletionStatusEx,
    }

    unsafe fn resolve_targets() -> Result<HookTargets, String> {
        Ok(HookTargets {
            create_file_w: std::mem::transmute::<*const c_void, FnCreateFileW>(resolve_proc(
                "CreateFileW",
            )?),
            create_file_a: std::mem::transmute::<*const c_void, FnCreateFileA>(resolve_proc(
                "CreateFileA",
            )?),
            read_file: std::mem::transmute::<*const c_void, FnReadFile>(resolve_proc("ReadFile")?),
            write_file: std::mem::transmute::<*const c_void, FnWriteFile>(resolve_proc(
                "WriteFile",
            )?),
            close_handle: std::mem::transmute::<*const c_void, FnCloseHandle>(resolve_proc(
                "CloseHandle",
            )?),
            get_overlapped_result: std::mem::transmute::<*const c_void, FnGetOverlappedResult>(
                resolve_proc("GetOverlappedResult")?,
            ),
            get_overlapped_result_ex: std::mem::transmute::<*const c_void, FnGetOverlappedResultEx>(
                resolve_proc("GetOverlappedResultEx")?,
            ),
            cancel_io: std::mem::transmute::<*const c_void, FnCancelIo>(resolve_proc("CancelIo")?),
            cancel_io_ex: std::mem::transmute::<*const c_void, FnCancelIoEx>(resolve_proc(
                "CancelIoEx",
            )?),
            get_queued_completion_status: std::mem::transmute::<
                *const c_void,
                FnGetQueuedCompletionStatus,
            >(resolve_proc("GetQueuedCompletionStatus")?),
            get_queued_completion_status_ex: std::mem::transmute::<
                *const c_void,
                FnGetQueuedCompletionStatusEx,
            >(resolve_proc(
                "GetQueuedCompletionStatusEx",
            )?),
        })
    }

    unsafe fn rollback_enabled_hooks() {
        let hook_set = hooks();
        let _ = hook_set.get_queued_completion_status_ex.disable();
        let _ = hook_set.get_queued_completion_status.disable();
        let _ = hook_set.cancel_io_ex.disable();
        let _ = hook_set.cancel_io.disable();
        let _ = hook_set.get_overlapped_result_ex.disable();
        let _ = hook_set.get_overlapped_result.disable();
        let _ = hook_set.close_handle.disable();
        let _ = hook_set.write_file.disable();
        let _ = hook_set.read_file.disable();
        let _ = hook_set.create_file_a.disable();
        let _ = hook_set.create_file_w.disable();
    }

    unsafe fn construct_hooks(targets: HookTargets) -> Result<HookSet, retour::Error> {
        Ok(HookSet {
            create_file_w: GenericDetour::new(
                targets.create_file_w,
                hook_create_file_w as FnCreateFileW,
            )?,
            create_file_a: GenericDetour::new(
                targets.create_file_a,
                hook_create_file_a as FnCreateFileA,
            )?,
            read_file: GenericDetour::new(targets.read_file, hook_read_file as FnReadFile)?,
            write_file: GenericDetour::new(targets.write_file, hook_write_file as FnWriteFile)?,
            close_handle: GenericDetour::new(
                targets.close_handle,
                hook_close_handle as FnCloseHandle,
            )?,
            get_overlapped_result: GenericDetour::new(
                targets.get_overlapped_result,
                hook_get_overlapped_result as FnGetOverlappedResult,
            )?,
            get_overlapped_result_ex: GenericDetour::new(
                targets.get_overlapped_result_ex,
                hook_get_overlapped_result_ex as FnGetOverlappedResultEx,
            )?,
            cancel_io: GenericDetour::new(targets.cancel_io, hook_cancel_io as FnCancelIo)?,
            cancel_io_ex: GenericDetour::new(
                targets.cancel_io_ex,
                hook_cancel_io_ex as FnCancelIoEx,
            )?,
            get_queued_completion_status: GenericDetour::new(
                targets.get_queued_completion_status,
                hook_get_queued_completion_status as FnGetQueuedCompletionStatus,
            )?,
            get_queued_completion_status_ex: GenericDetour::new(
                targets.get_queued_completion_status_ex,
                hook_get_queued_completion_status_ex as FnGetQueuedCompletionStatusEx,
            )?,
        })
    }

    unsafe fn install_hooks_transactional() -> Result<(), String> {
        if HOOK_STATE.load(Ordering::Acquire) == 2 {
            return Ok(());
        }
        if HOOK_STATE
            .compare_exchange(0, 1, Ordering::AcqRel, Ordering::Acquire)
            .is_err()
        {
            return Err("hooks are unavailable after a prior initialization failure".into());
        }
        ACCEPTING_EVENTS.store(false, Ordering::Release);
        let targets = match resolve_targets() {
            Ok(targets) => targets,
            Err(error) => {
                HOOK_STATE.store(3, Ordering::Release);
                return Err(error);
            }
        };

        // GenericDetour is stable-Rust compatible and constructs every decoded,
        // relocated trampoline before any entry point is patched.
        let hook_set = match construct_hooks(targets) {
            Ok(hook_set) => hook_set,
            Err(error) => {
                HOOK_STATE.store(3, Ordering::Release);
                return Err(error.to_string());
            }
        };
        if HOOKS.set(hook_set).is_err() {
            HOOK_STATE.store(3, Ordering::Release);
            return Err("detour set was already initialized".into());
        }

        // During this sequence every installed hook is capture-gated and only
        // dispatches to its retour trampoline. Any failure is rolled back in
        // reverse order before capture can be enabled.
        let hook_set = hooks();
        let enabled = (|| -> Result<(), retour::Error> {
            hook_set.create_file_w.enable()?;
            hook_set.create_file_a.enable()?;
            hook_set.read_file.enable()?;
            hook_set.write_file.enable()?;
            hook_set.close_handle.enable()?;
            hook_set.get_overlapped_result.enable()?;
            hook_set.get_overlapped_result_ex.enable()?;
            hook_set.cancel_io.enable()?;
            hook_set.cancel_io_ex.enable()?;
            hook_set.get_queued_completion_status.enable()?;
            hook_set.get_queued_completion_status_ex.enable()?;
            Ok(())
        })();
        if let Err(error) = enabled {
            rollback_enabled_hooks();
            HOOK_STATE.store(3, Ordering::Release);
            return Err(error.to_string());
        }
        HOOK_STATE.store(2, Ordering::Release);
        Ok(())
    }

    fn parse_com_str(value: &str) -> Option<u32> {
        let normalized = value.trim().replace('/', "\\").to_ascii_uppercase();
        let path = normalized
            .strip_prefix("\\\\.\\")
            .or_else(|| normalized.strip_prefix("\\\\?\\"))
            .unwrap_or(&normalized);
        let digits = path.strip_prefix("COM")?;
        if digits.is_empty() || !digits.bytes().all(|value| value.is_ascii_digit()) {
            return None;
        }
        let port = digits.parse::<u32>().ok()?;
        (port > 0 && port <= 4096).then_some(port)
    }

    unsafe fn parse_com_w(path: *const u16) -> Option<u32> {
        if path.is_null() {
            return None;
        }
        let mut buffer = [0u16; 260];
        let mut length = 0usize;
        while length < buffer.len() {
            let value = *path.add(length);
            if value == 0 {
                break;
            }
            buffer[length] = value;
            length += 1;
        }
        if length == buffer.len() {
            return None;
        }
        parse_com_str(&String::from_utf16_lossy(&buffer[..length]))
    }

    unsafe fn parse_com_a(path: *const u8) -> Option<u32> {
        if path.is_null() {
            return None;
        }
        let mut buffer = [0u8; 260];
        let mut length = 0usize;
        while length < buffer.len() {
            let value = *path.add(length);
            if value == 0 {
                break;
            }
            buffer[length] = value;
            length += 1;
        }
        if length == buffer.len() {
            return None;
        }
        parse_com_str(&String::from_utf8_lossy(&buffer[..length]))
    }

    fn current_handle_info(handle: isize) -> Option<HandleInfo> {
        com_handles()
            .read()
            .ok()
            .and_then(|map| map.get(&handle).copied())
    }

    fn register_handle(handle: isize, port: u32) {
        if port != SELECTED_COM.load(Ordering::Acquire) {
            return;
        }
        let info = HandleInfo {
            port,
            generation: NEXT_HANDLE_GENERATION.fetch_add(1, Ordering::Relaxed),
        };
        if let Ok(mut map) = com_handles().write() {
            map.insert(handle, info);
        }
    }

    fn remove_handle_if_current(handle: isize, expected: HandleInfo) -> bool {
        let Ok(mut map) = com_handles().write() else {
            return false;
        };
        if map.get(&handle).copied() != Some(expected) {
            return false;
        }
        map.remove(&handle);
        true
    }

    fn clear_pending_for_handle(handle: isize, generation: u64) {
        if let Ok(mut pending) = pending_operations().write() {
            pending.retain(|_, operation| {
                operation.handle != handle || operation.handle_generation != generation
            });
        }
    }

    fn clear_pending_for_cancel_io(handle: isize, generation: u64, thread_id: u32) {
        if let Ok(mut pending) = pending_operations().write() {
            pending.retain(|_, operation| {
                operation.handle != handle
                    || operation.handle_generation != generation
                    || operation.issuer_thread_id != thread_id
            });
        }
    }

    fn clear_pending_for_cancel_io_ex(handle: isize, generation: u64, overlapped: *mut c_void) {
        if overlapped.is_null() {
            clear_pending_for_handle(handle, generation);
            return;
        }
        let key = overlapped as usize;
        if let Ok(mut pending) = pending_operations().write() {
            if pending
                .get(&key)
                .map(|operation| {
                    operation.handle == handle && operation.handle_generation == generation
                })
                .unwrap_or(false)
            {
                pending.remove(&key);
            }
        }
    }

    fn clear_pending_overlap(overlapped: *mut c_void) {
        if overlapped.is_null() {
            return;
        }
        if let Ok(mut pending) = pending_operations().write() {
            pending.remove(&(overlapped as usize));
        }
    }

    fn register_pending(overlapped: *mut c_void, operation: PendingOperation) {
        if overlapped.is_null() || operation.buffer == 0 {
            return;
        }
        let Ok(mut pending) = pending_operations().write() else {
            add_native_drops(1);
            return;
        };
        let key = overlapped as usize;
        if !pending.contains_key(&key) && pending.len() >= MAX_PENDING_OPERATIONS {
            add_native_drops(1);
            return;
        }
        pending.insert(key, operation);
    }

    fn take_pending(overlapped: *mut c_void) -> Option<PendingOperation> {
        if overlapped.is_null() {
            return None;
        }
        pending_operations()
            .write()
            .ok()
            .and_then(|mut pending| pending.remove(&(overlapped as usize)))
    }

    fn packet_template(info: HandleInfo, state: u32, sequence: u64) -> MonitorPacket {
        MonitorPacket {
            magic: WIRE_MAGIC,
            abi_version: ABI_VERSION,
            header_size: WIRE_HEADER_SIZE as u16,
            generation: SESSION_GENERATION.load(Ordering::Acquire),
            sequence,
            com_port: info.port,
            comm_state: state,
            file_handle: 0,
            total_length: 0,
            fragment_offset: 0,
            data_length: 0,
            flags: 0,
            native_dropped_packets: 0,
            data: [0; MAX_FRAGMENT_DATA],
        }
    }

    unsafe fn send_packet(packet: &mut MonitorPacket) -> bool {
        let pipe = PIPE_HANDLE.load(Ordering::Acquire);
        if pipe == INVALID_PIPE || pipe == 0 {
            add_native_drops(1);
            return false;
        }
        let prior_drops = NATIVE_DROPPED.swap(0, Ordering::AcqRel);
        packet.native_dropped_packets = prior_drops;
        let bytes = std::slice::from_raw_parts(
            packet as *const MonitorPacket as *const u8,
            WIRE_PACKET_SIZE,
        );
        let mut written = 0u32;
        let result = call_original_write_file(
            pipe,
            bytes.as_ptr() as *const c_void,
            bytes.len() as u32,
            &mut written,
            std::ptr::null_mut(),
        );
        if result == 0 || written as usize != WIRE_PACKET_SIZE {
            add_native_drops(prior_drops.saturating_add(1));
            return false;
        }
        true
    }

    unsafe fn emit_disconnect(handle: isize, info: HandleInfo) {
        let sequence = NEXT_SEQUENCE.fetch_add(1, Ordering::Relaxed);
        let mut packet = packet_template(info, STATE_DISCONNECT, sequence);
        packet.file_handle = handle as usize as u64;
        packet.flags = FLAG_FIRST | FLAG_LAST;
        let _ = send_packet(&mut packet);
    }

    unsafe fn emit_transfer(operation: PendingOperation, transferred_length: u32) {
        let Some(current) = current_handle_info(operation.handle) else {
            return;
        };
        if current.port != operation.port
            || current.generation != operation.handle_generation
            || operation.buffer == 0
        {
            return;
        }

        let completed_length = transferred_length.min(operation.requested_length) as usize;
        let capture_length = completed_length.min(MAX_CAPTURE_BYTES_PER_TRANSFER);
        let truncated = transferred_length > operation.requested_length
            || capture_length < transferred_length as usize;
        if capture_length == 0 && transferred_length == 0 {
            return;
        }
        let state = match operation.kind {
            PendingKind::Read => STATE_RECEIVE,
            PendingKind::Write => STATE_SEND,
        };
        let sequence = NEXT_SEQUENCE.fetch_add(1, Ordering::Relaxed);
        if capture_length == 0 {
            let mut packet = packet_template(current, state, sequence);
            packet.file_handle = operation.handle as usize as u64;
            packet.total_length = transferred_length;
            packet.flags = FLAG_FIRST | FLAG_LAST | FLAG_TRUNCATED;
            let _ = send_packet(&mut packet);
            return;
        }

        let mut offset = 0usize;
        while offset < capture_length {
            let length = (capture_length - offset).min(MAX_FRAGMENT_DATA);
            let mut packet = packet_template(current, state, sequence);
            packet.file_handle = operation.handle as usize as u64;
            packet.total_length = transferred_length;
            packet.fragment_offset = offset as u32;
            packet.data_length = length as u32;
            packet.flags = if offset == 0 { FLAG_FIRST } else { 0 };
            if offset + length == capture_length {
                packet.flags |= FLAG_LAST;
            }
            if truncated {
                packet.flags |= FLAG_TRUNCATED;
            }
            std::ptr::copy_nonoverlapping(
                (operation.buffer as *const u8).add(offset),
                packet.data.as_mut_ptr(),
                length,
            );
            if !send_packet(&mut packet) {
                break;
            }
            offset += length;
        }
    }

    unsafe fn complete_pending(handle: isize, overlapped: *mut c_void, transferred: u32) {
        let Some(operation) = take_pending(overlapped) else {
            return;
        };
        if operation.handle != handle {
            return;
        }
        emit_transfer(operation, transferred);
    }

    unsafe fn immediate_overlapped_count(handle: isize, overlapped: *mut c_void) -> Option<u32> {
        let mut transferred = 0u32;
        (call_original_get_overlapped_result(handle, overlapped, &mut transferred, 0) != 0)
            .then_some(transferred)
    }

    unsafe extern "system" fn hook_create_file_w(
        file_name: *const u16,
        desired_access: u32,
        share_mode: u32,
        security_attributes: *mut c_void,
        creation_disposition: u32,
        flags_and_attributes: u32,
        template_file: isize,
    ) -> isize {
        let parsed_port = parse_com_w(file_name);
        let handle = call_original_create_file_w(
            file_name,
            desired_access,
            share_mode,
            security_attributes,
            creation_disposition,
            flags_and_attributes,
            template_file,
        );
        let original_error = GetLastError();
        if handle != -1 {
            if let (Some(port), Some(_guard)) = (parsed_port, CaptureCallGuard::enter()) {
                register_handle(handle, port);
            }
        }
        SetLastError(original_error);
        handle
    }

    unsafe extern "system" fn hook_create_file_a(
        file_name: *const u8,
        desired_access: u32,
        share_mode: u32,
        security_attributes: *mut c_void,
        creation_disposition: u32,
        flags_and_attributes: u32,
        template_file: isize,
    ) -> isize {
        let parsed_port = parse_com_a(file_name);
        let handle = call_original_create_file_a(
            file_name,
            desired_access,
            share_mode,
            security_attributes,
            creation_disposition,
            flags_and_attributes,
            template_file,
        );
        let original_error = GetLastError();
        if handle != -1 {
            if let (Some(port), Some(_guard)) = (parsed_port, CaptureCallGuard::enter()) {
                register_handle(handle, port);
            }
        }
        SetLastError(original_error);
        handle
    }

    unsafe extern "system" fn hook_read_file(
        handle: isize,
        buffer: *mut c_void,
        requested: u32,
        bytes_read: *mut u32,
        overlapped: *mut c_void,
    ) -> i32 {
        let result = call_original_read_file(handle, buffer, requested, bytes_read, overlapped);
        let original_error = GetLastError();
        if let (Some(info), Some(_guard)) = (current_handle_info(handle), CaptureCallGuard::enter())
        {
            let operation = PendingOperation {
                kind: PendingKind::Read,
                port: info.port,
                handle,
                buffer: buffer as usize,
                requested_length: requested,
                handle_generation: info.generation,
                issuer_thread_id: GetCurrentThreadId(),
            };
            if overlapped.is_null() {
                if result != 0 && !bytes_read.is_null() {
                    emit_transfer(operation, *bytes_read);
                }
            } else if result == 0 && original_error.0 == ERROR_IO_PENDING_RAW {
                register_pending(overlapped, operation);
            } else {
                // Reusing an OVERLAPPED for a non-pending operation invalidates
                // any older observation associated with the same address.
                clear_pending_overlap(overlapped);
                if result != 0 {
                    if let Some(transferred) = immediate_overlapped_count(handle, overlapped) {
                        emit_transfer(operation, transferred);
                    }
                }
            }
        }
        SetLastError(original_error);
        result
    }

    unsafe extern "system" fn hook_write_file(
        handle: isize,
        buffer: *const c_void,
        requested: u32,
        bytes_written: *mut u32,
        overlapped: *mut c_void,
    ) -> i32 {
        // Never report requested bytes before the real WriteFile has committed.
        let result = call_original_write_file(handle, buffer, requested, bytes_written, overlapped);
        let original_error = GetLastError();
        if !IN_HOOK.with(|flag| flag.get()) {
            if let (Some(info), Some(_guard)) =
                (current_handle_info(handle), CaptureCallGuard::enter())
            {
                let operation = PendingOperation {
                    kind: PendingKind::Write,
                    port: info.port,
                    handle,
                    buffer: buffer as usize,
                    requested_length: requested,
                    handle_generation: info.generation,
                    issuer_thread_id: GetCurrentThreadId(),
                };
                if overlapped.is_null() {
                    if result != 0 && !bytes_written.is_null() {
                        IN_HOOK.with(|flag| flag.set(true));
                        emit_transfer(operation, *bytes_written);
                        IN_HOOK.with(|flag| flag.set(false));
                    }
                } else if result == 0 && original_error.0 == ERROR_IO_PENDING_RAW {
                    register_pending(overlapped, operation);
                } else {
                    clear_pending_overlap(overlapped);
                    if result != 0 {
                        if let Some(transferred) = immediate_overlapped_count(handle, overlapped) {
                            IN_HOOK.with(|flag| flag.set(true));
                            emit_transfer(operation, transferred);
                            IN_HOOK.with(|flag| flag.set(false));
                        }
                    }
                }
            }
        }
        SetLastError(original_error);
        result
    }

    unsafe extern "system" fn hook_close_handle(handle: isize) -> i32 {
        let expected = current_handle_info(handle);
        let result = call_original_close_handle(handle);
        let original_error = GetLastError();
        if result != 0 {
            if let (Some(info), Some(_guard)) = (expected, CaptureCallGuard::enter()) {
                if remove_handle_if_current(handle, info) {
                    clear_pending_for_handle(handle, info.generation);
                    IN_HOOK.with(|flag| flag.set(true));
                    emit_disconnect(handle, info);
                    IN_HOOK.with(|flag| flag.set(false));
                }
            }
        }
        SetLastError(original_error);
        result
    }

    fn completion_is_still_pending(error: u32) -> bool {
        error == ERROR_IO_INCOMPLETE_RAW || error == WAIT_TIMEOUT_RAW
    }

    unsafe extern "system" fn hook_get_overlapped_result(
        handle: isize,
        overlapped: *mut c_void,
        bytes_transferred: *mut u32,
        wait: i32,
    ) -> i32 {
        let result =
            call_original_get_overlapped_result(handle, overlapped, bytes_transferred, wait);
        let original_error = GetLastError();
        if let Some(_guard) = CaptureCallGuard::enter() {
            if result != 0 && !bytes_transferred.is_null() {
                IN_HOOK.with(|flag| flag.set(true));
                complete_pending(handle, overlapped, *bytes_transferred);
                IN_HOOK.with(|flag| flag.set(false));
            } else if result == 0 && !completion_is_still_pending(original_error.0) {
                clear_pending_overlap(overlapped);
            }
        }
        SetLastError(original_error);
        result
    }

    unsafe extern "system" fn hook_get_overlapped_result_ex(
        handle: isize,
        overlapped: *mut c_void,
        bytes_transferred: *mut u32,
        timeout: u32,
        alertable: i32,
    ) -> i32 {
        let result = call_original_get_overlapped_result_ex(
            handle,
            overlapped,
            bytes_transferred,
            timeout,
            alertable,
        );
        let original_error = GetLastError();
        if let Some(_guard) = CaptureCallGuard::enter() {
            if result != 0 && !bytes_transferred.is_null() {
                IN_HOOK.with(|flag| flag.set(true));
                complete_pending(handle, overlapped, *bytes_transferred);
                IN_HOOK.with(|flag| flag.set(false));
            } else if result == 0 && !completion_is_still_pending(original_error.0) {
                clear_pending_overlap(overlapped);
            }
        }
        SetLastError(original_error);
        result
    }

    unsafe extern "system" fn hook_cancel_io(handle: isize) -> i32 {
        let result = hooks().cancel_io.call(handle);
        let original_error = GetLastError();
        if result != 0 {
            if let (Some(info), Some(_guard)) =
                (current_handle_info(handle), CaptureCallGuard::enter())
            {
                clear_pending_for_cancel_io(handle, info.generation, GetCurrentThreadId());
            }
        }
        SetLastError(original_error);
        result
    }

    unsafe extern "system" fn hook_cancel_io_ex(handle: isize, overlapped: *mut c_void) -> i32 {
        let result = hooks().cancel_io_ex.call(handle, overlapped);
        let original_error = GetLastError();
        if result != 0 {
            if let (Some(info), Some(_guard)) =
                (current_handle_info(handle), CaptureCallGuard::enter())
            {
                clear_pending_for_cancel_io_ex(handle, info.generation, overlapped);
            }
        }
        SetLastError(original_error);
        result
    }

    unsafe extern "system" fn hook_get_queued_completion_status(
        completion_port: isize,
        bytes_transferred: *mut u32,
        completion_key: *mut usize,
        overlapped_out: *mut *mut c_void,
        timeout: u32,
    ) -> i32 {
        let result = hooks().get_queued_completion_status.call(
            completion_port,
            bytes_transferred,
            completion_key,
            overlapped_out,
            timeout,
        );
        let original_error = GetLastError();
        if let Some(_guard) = CaptureCallGuard::enter() {
            if !overlapped_out.is_null() {
                let overlapped = *overlapped_out;
                if !overlapped.is_null() {
                    // IOCP does not provide the originating file handle. Drop
                    // tracking without dereferencing its buffer rather than
                    // guessing across handle generations.
                    clear_pending_overlap(overlapped);
                }
            }
        }
        SetLastError(original_error);
        result
    }

    unsafe extern "system" fn hook_get_queued_completion_status_ex(
        completion_port: isize,
        entries: *mut OverlappedEntry,
        entry_count: u32,
        removed_count: *mut u32,
        timeout: u32,
        alertable: i32,
    ) -> i32 {
        let result = hooks().get_queued_completion_status_ex.call(
            completion_port,
            entries,
            entry_count,
            removed_count,
            timeout,
            alertable,
        );
        let original_error = GetLastError();
        if result != 0 {
            if let Some(_guard) = CaptureCallGuard::enter() {
                if !entries.is_null() && !removed_count.is_null() {
                    let count = (*removed_count).min(entry_count) as usize;
                    for index in 0..count {
                        clear_pending_overlap((*entries.add(index)).overlapped);
                    }
                }
            }
        }
        SetLastError(original_error);
        result
    }

    fn mapping_name() -> String {
        format!("Local\\llcom_plus_serial_monitor_v3_{}", unsafe {
            GetCurrentProcessId()
        })
    }

    fn read_config() -> Option<SharedConfig> {
        let name: Vec<u16> = mapping_name()
            .encode_utf16()
            .chain(std::iter::once(0))
            .collect();
        unsafe {
            let mapping = OpenFileMappingW(FILE_MAP_READ.0, false, PCWSTR(name.as_ptr())).ok()?;
            let view = MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, CONFIG_SIZE);
            if view.Value.is_null() {
                let _ = CloseHandle(mapping);
                return None;
            }
            let config = std::ptr::read_unaligned(view.Value as *const SharedConfig);
            let _ = UnmapViewOfFile(view);
            let _ = CloseHandle(mapping);
            if config.magic != CONFIG_MAGIC
                || config.abi_version != ABI_VERSION
                || config.struct_size as usize != CONFIG_SIZE
                || config.selected_com == 0
                || config.selected_com > 4096
                || config.generation == 0
                || config.pipe_name_length == 0
                || config.pipe_name_length as usize >= CONFIG_PIPE_CHARS
            {
                return None;
            }
            Some(config)
        }
    }

    unsafe fn close_pipe_after_drain() {
        let pipe = PIPE_HANDLE.swap(INVALID_PIPE, Ordering::AcqRel);
        if pipe == INVALID_PIPE || pipe == 0 {
            return;
        }
        if HOOK_STATE.load(Ordering::Acquire) == 2 {
            let _ = call_original_close_handle(pipe);
        } else {
            let _ = CloseHandle(HANDLE(pipe as *mut c_void));
        }
    }

    unsafe fn connect_pipe(config: &SharedConfig) -> bool {
        let length = config.pipe_name_length as usize;
        let mut pipe_name = Vec::with_capacity(length + 1);
        pipe_name.extend_from_slice(&config.pipe_name[..length]);
        pipe_name.push(0);
        let handle = CreateFileW(
            PCWSTR(pipe_name.as_ptr()),
            0x4000_0000,
            FILE_SHARE_MODE(0),
            None,
            OPEN_EXISTING,
            FILE_FLAGS_AND_ATTRIBUTES(0),
            HANDLE::default(),
        );
        let Ok(handle) = handle else {
            return false;
        };
        if handle == INVALID_HANDLE_VALUE || handle.0.is_null() {
            return false;
        }
        let mode = PIPE_NOWAIT_RAW;
        if SetNamedPipeHandleState(handle.0, &mode, std::ptr::null(), std::ptr::null()) == 0 {
            let _ = CloseHandle(handle);
            return false;
        }
        let raw = handle.0 as isize;
        if PIPE_HANDLE
            .compare_exchange(INVALID_PIPE, raw, Ordering::AcqRel, Ordering::Acquire)
            .is_err()
        {
            let _ = CloseHandle(handle);
            return false;
        }
        true
    }

    fn clear_tracking() {
        if let Ok(mut handles) = com_handles().write() {
            handles.clear();
        }
        if let Ok(mut pending) = pending_operations().write() {
            pending.clear();
        }
    }

    fn wait_for_capture_drain(timeout: Duration) -> bool {
        let deadline = Instant::now() + timeout;
        while ACTIVE_CAPTURE_CALLS.load(Ordering::Acquire) != 0 {
            if Instant::now() >= deadline {
                return false;
            }
            unsafe { Sleep(1) };
        }
        true
    }

    pub(super) unsafe fn initialize() -> u32 {
        let _lifecycle = lock_recover(lifecycle_gate());
        if ACCEPTING_EVENTS.load(Ordering::Acquire) {
            return HOOK_ERROR_BUSY;
        }
        if !wait_for_capture_drain(Duration::from_secs(5)) {
            return HOOK_ERROR_DRAIN_TIMEOUT;
        }
        close_pipe_after_drain();
        clear_tracking();

        let Some(config) = read_config() else {
            return HOOK_ERROR_BAD_CONFIG;
        };
        if !connect_pipe(&config) {
            return HOOK_ERROR_PIPE;
        }
        if install_hooks_transactional().is_err() {
            close_pipe_after_drain();
            return HOOK_ERROR_INSTALL;
        }

        SELECTED_COM.store(config.selected_com, Ordering::Release);
        SESSION_GENERATION.store(config.generation, Ordering::Release);
        NATIVE_DROPPED.store(0, Ordering::Release);
        ACCEPTING_EVENTS.store(true, Ordering::Release);
        HOOK_INIT_OK
    }

    pub(super) unsafe fn deactivate() -> u32 {
        let _lifecycle = lock_recover(lifecycle_gate());
        ACCEPTING_EVENTS.store(false, Ordering::Release);
        if !wait_for_capture_drain(Duration::from_secs(5)) {
            // Keep the pipe valid; the host retries this export asynchronously.
            return HOOK_ERROR_DRAIN_TIMEOUT;
        }
        clear_tracking();
        SELECTED_COM.store(0, Ordering::Release);
        SESSION_GENERATION.store(0, Ordering::Release);
        close_pipe_after_drain();
        HOOK_DEACTIVATE_OK
    }
}

#[cfg(target_arch = "x86_64")]
#[no_mangle]
pub unsafe extern "system" fn SerialMonitorInitialize(_parameter: *mut c_void) -> u32 {
    x64::initialize()
}

#[cfg(target_arch = "x86_64")]
#[no_mangle]
pub unsafe extern "system" fn SerialMonitorDeactivate(_parameter: *mut c_void) -> u32 {
    x64::deactivate()
}

#[no_mangle]
pub unsafe extern "system" fn DllMain(
    module: *mut c_void,
    reason: u32,
    _reserved: *mut c_void,
) -> i32 {
    if reason == DLL_PROCESS_ATTACH {
        // This is the only loader-lock action. Initialization is an explicit
        // host handshake executed by a separate remote thread after LoadLibrary.
        let _ = DisableThreadLibraryCalls(module);
    }
    1
}
