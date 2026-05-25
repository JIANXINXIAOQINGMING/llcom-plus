//! serial_monitor_hook — Injected DLL that hooks ReadFile/WriteFile in the target process
//! and forwards COM port data back to the host (serial_monitor.dll) via a named pipe.
//!
//! The host writes a UTF-16 pipe name to named shared memory
//! (`Local\llcom_smv2_session`) before injecting this DLL.
//! DllMain reads that name, connects to the pipe, and installs the hooks.
//!
//! Supports both x86_64 (via `retour` inline hooks) and x86 (via manual 5-byte
//! JMP patching, because `retour` does not compile for i686 targets).

#![allow(non_snake_case)]
#![allow(clippy::missing_safety_doc)]

use std::cell::Cell;
use std::collections::HashMap;
use std::ffi::c_void;
use std::sync::{Mutex, OnceLock, RwLock};

use windows::Win32::Foundation::{CloseHandle, HANDLE, INVALID_HANDLE_VALUE};
use windows::Win32::System::LibraryLoader::{GetModuleHandleW, GetProcAddress};
use windows::Win32::System::Memory::{
    MapViewOfFile, OpenFileMappingW, UnmapViewOfFile, FILE_MAP_READ,
};
use windows::Win32::System::SystemServices::{DLL_PROCESS_ATTACH, DLL_PROCESS_DETACH};
use windows::core::{w, PCWSTR};

// ── Constants ────────────────────────────────────────────────────────────────

const MAX_DATA: usize = 8192;

const STATE_DISCONNECT: u8 = 2;
const STATE_RECEIVE: u8    = 3;
const STATE_SEND: u8       = 4;

const SHMEM_NAME: PCWSTR = w!("Local\\llcom_smv2_session");

// ── Wire structure (must match C# Udata, Pack=1) ─────────────────────────────

#[repr(C, packed(1))]
#[derive(Clone, Copy)]
struct Udata {
    com_port:    u8,
    comm_state:  u8,
    file_handle: i32,
    data_size:   i32,
    data: [u8; MAX_DATA],
}

impl Udata {
    fn new(com_port: u8, comm_state: u8, file_handle: i32) -> Self {
        Udata { com_port, comm_state, file_handle, data_size: 0, data: [0u8; MAX_DATA] }
    }
}

// ── Global state ─────────────────────────────────────────────────────────────

/// HANDLE → COM port number
static COM_HANDLES: OnceLock<RwLock<HashMap<isize, u8>>>             = OnceLock::new();
/// Pending overlapped reads: OVERLAPPED ptr → (com_port, handle, buffer_ptr)
static PENDING_READS: OnceLock<RwLock<HashMap<usize, (u8, i32, usize)>>> = OnceLock::new();

struct SafeHandle(isize);
unsafe impl Send for SafeHandle {}
static PIPE: OnceLock<Mutex<SafeHandle>> = OnceLock::new();

fn com_map()      -> &'static RwLock<HashMap<isize, u8>>              { COM_HANDLES.get_or_init(|| RwLock::new(HashMap::new())) }
fn pending_reads()-> &'static RwLock<HashMap<usize,(u8,i32,usize)>>   { PENDING_READS.get_or_init(|| RwLock::new(HashMap::new())) }

fn pipe_handle() -> isize {
    PIPE.get().and_then(|m| m.lock().ok()).map(|g| g.0).unwrap_or(-1)
}

// ── Reentrancy guard ──────────────────────────────────────────────────────────

thread_local! { static IN_HOOK: Cell<bool> = Cell::new(false); }

// ── Hook function-pointer types ───────────────────────────────────────────────

type FnCreateFileW = unsafe extern "system" fn(*const u16,u32,u32,*mut c_void,u32,u32,isize)->isize;
type FnCreateFileA = unsafe extern "system" fn(*const u8, u32,u32,*mut c_void,u32,u32,isize)->isize;
type FnReadFile    = unsafe extern "system" fn(isize,*mut c_void,u32,*mut u32,*mut c_void)->i32;
type FnWriteFile   = unsafe extern "system" fn(isize,*const c_void,u32,*mut u32,*mut c_void)->i32;
type FnCloseHandle = unsafe extern "system" fn(isize)->i32;
type FnGetOverlappedResult = unsafe extern "system" fn(isize,*mut c_void,*mut u32,i32)->i32;

// ── Architecture-specific hook back-end ──────────────────────────────────────
//
// arch_hooks::call_original_*  — call the original (pre-hook) function
// arch_hooks::install()        — patch all functions
// arch_hooks::uninstall()      — restore all functions

// ── x86_64 back-end: retour static detours ───────────────────────────────────
#[cfg(target_arch = "x86_64")]
mod arch_hooks {
    use super::*;
    use retour::static_detour;

    static_detour! {
        pub(super) static CreateFileWDetour: unsafe extern "system" fn(
            *const u16,u32,u32,*mut c_void,u32,u32,isize)->isize;
        pub(super) static CreateFileADetour: unsafe extern "system" fn(
            *const u8,u32,u32,*mut c_void,u32,u32,isize)->isize;
        pub(super) static ReadFileDetour: unsafe extern "system" fn(
            isize,*mut c_void,u32,*mut u32,*mut c_void)->i32;
        pub(super) static WriteFileDetour: unsafe extern "system" fn(
            isize,*const c_void,u32,*mut u32,*mut c_void)->i32;
        pub(super) static CloseHandleDetour: unsafe extern "system" fn(isize)->i32;
        pub(super) static GetOverlappedResultDetour: unsafe extern "system" fn(
            isize,*mut c_void,*mut u32,i32)->i32;
    }

    pub(super) unsafe fn call_original_create_file_w(n:*const u16,a:u32,s:u32,sec:*mut c_void,d:u32,f:u32,t:isize)->isize { CreateFileWDetour.call(n,a,s,sec,d,f,t) }
    pub(super) unsafe fn call_original_create_file_a(n:*const u8, a:u32,s:u32,sec:*mut c_void,d:u32,f:u32,t:isize)->isize { CreateFileADetour.call(n,a,s,sec,d,f,t) }
    pub(super) unsafe fn call_original_read_file(h:isize,b:*mut c_void,n:u32,r:*mut u32,o:*mut c_void)->i32 { ReadFileDetour.call(h,b,n,r,o) }
    pub(super) unsafe fn call_original_write_file(h:isize,b:*const c_void,n:u32,w:*mut u32,o:*mut c_void)->i32 { WriteFileDetour.call(h,b,n,w,o) }
    pub(super) unsafe fn call_original_close_handle(h:isize)->i32 { CloseHandleDetour.call(h) }
    pub(super) unsafe fn call_original_get_overlapped_result(h:isize,o:*mut c_void,b:*mut u32,w:i32)->i32 { GetOverlappedResultDetour.call(h,o,b,w) }

    pub(super) unsafe fn install() -> Result<(), Box<dyn std::error::Error>> {
        macro_rules! hook {
            ($detour:ident, $proc:literal, $closure:expr, $fn_type:ty) => {{
                let addr = super::get_proc("kernelbase.dll", $proc)
                    .or_else(|| super::get_proc("kernel32.dll", $proc))
                    .ok_or_else(|| format!("GetProcAddress failed for {}", $proc))?;
                let target: $fn_type = std::mem::transmute(addr);
                $detour.initialize(target, $closure)?;
                $detour.enable()?;
            }};
        }
        // retour expects safe Fn closures; wrap the unsafe extern "system" hooks.
        hook!(CreateFileWDetour,         "CreateFileW",
            |n,a,s,sec,d,f,t| unsafe { super::hook_create_file_w(n,a,s,sec,d,f,t) }, FnCreateFileW);
        hook!(CreateFileADetour,         "CreateFileA",
            |n,a,s,sec,d,f,t| unsafe { super::hook_create_file_a(n,a,s,sec,d,f,t) }, FnCreateFileA);
        hook!(ReadFileDetour,            "ReadFile",
            |h,b,n,r,o| unsafe { super::hook_read_file(h,b,n,r,o) }, FnReadFile);
        hook!(WriteFileDetour,           "WriteFile",
            |h,b,n,w,o| unsafe { super::hook_write_file(h,b,n,w,o) }, FnWriteFile);
        hook!(CloseHandleDetour,         "CloseHandle",
            |h| unsafe { super::hook_close_handle(h) }, FnCloseHandle);
        hook!(GetOverlappedResultDetour, "GetOverlappedResult",
            |h,o,b,w| unsafe { super::hook_get_overlapped_result(h,o,b,w) }, FnGetOverlappedResult);
        Ok(())
    }

    pub(super) unsafe fn uninstall() {
        let _ = GetOverlappedResultDetour.disable();
        let _ = CloseHandleDetour.disable();
        let _ = WriteFileDetour.disable();
        let _ = ReadFileDetour.disable();
        let _ = CreateFileADetour.disable();
        let _ = CreateFileWDetour.disable();
    }
}

// ── x86 back-end: manual 5-byte JMP inline hooks ─────────────────────────────
//
// On x86 (i686) `retour` does not compile because its `impl_hookable!` macro
// generates `extern "win64"` code which is not valid on 32-bit targets.
// We fall back to a simple manual inline-hook that:
//   1. Allocates executable trampoline memory via VirtualAlloc.
//   2. Copies the original 5 bytes then appends a JMP back to original+5.
//   3. Overwrites the target function with a 5-byte JMP to our hook.
// The trampoline pointer is stored in a static AtomicUsize so the hook
// function can "call original" without recursing.
#[cfg(target_arch = "x86")]
mod arch_hooks {
    use super::*;
    use std::sync::atomic::{AtomicUsize, Ordering::SeqCst};
    use windows::Win32::System::Memory::{
        VirtualAlloc, VirtualProtect, PAGE_EXECUTE_READWRITE, PAGE_PROTECTION_FLAGS,
        MEM_COMMIT, MEM_RESERVE,
    };

    const JMP_SZ: usize = 5; // E9 <rel32>

    // Trampoline pointers for each hooked function.
    static T_CFW: AtomicUsize = AtomicUsize::new(0); // CreateFileW
    static T_CFA: AtomicUsize = AtomicUsize::new(0); // CreateFileA
    static T_RF:  AtomicUsize = AtomicUsize::new(0); // ReadFile
    static T_WF:  AtomicUsize = AtomicUsize::new(0); // WriteFile
    static T_CH:  AtomicUsize = AtomicUsize::new(0); // CloseHandle
    static T_GOR: AtomicUsize = AtomicUsize::new(0); // GetOverlappedResult

    // Per-hook saved state for uninstall: (target_addr, original_bytes).
    struct Saved { target: usize, orig: [u8; JMP_SZ] }
    static SAVED: OnceLock<Mutex<Vec<Saved>>> = OnceLock::new();
    fn saved() -> &'static Mutex<Vec<Saved>> { SAVED.get_or_init(|| Mutex::new(Vec::new())) }

    /// Patch `target` with a JMP to `hook_fn`, store trampoline in `tramp_slot`.
    /// Returns false on failure.
    unsafe fn patch(target: *mut u8, hook_fn: *const u8, tramp_slot: &AtomicUsize) -> bool {
        // Allocate 10-byte executable trampoline: [5 original bytes] + [JMP back]
        let tramp = VirtualAlloc(
            None,
            (JMP_SZ * 2) as _,
            MEM_COMMIT | MEM_RESERVE,
            PAGE_EXECUTE_READWRITE,
        ) as *mut u8;
        if tramp.is_null() { return false; }

        // Copy original bytes.
        let orig = *(target as *const [u8; JMP_SZ]);
        std::ptr::copy_nonoverlapping(orig.as_ptr(), tramp, JMP_SZ);

        // Append JMP back to target+JMP_SZ.
        let jmp_back_from = tramp.add(JMP_SZ);
        let jmp_back_to   = target.add(JMP_SZ);
        let rel_back = (jmp_back_to as isize) - (jmp_back_from as isize) - 5;
        *jmp_back_from = 0xE9;
        *(jmp_back_from.add(1) as *mut i32) = rel_back as i32;

        // Patch target: write JMP to hook_fn.
        let mut old = PAGE_PROTECTION_FLAGS(0);
        if VirtualProtect(target as _, JMP_SZ, PAGE_EXECUTE_READWRITE, &mut old).is_err() {
            return false;
        }
        let rel_hook = (hook_fn as isize) - (target as isize) - 5;
        *target = 0xE9;
        *(target.add(1) as *mut i32) = rel_hook as i32;
        let _ = VirtualProtect(target as _, JMP_SZ, old, &mut old);

        // Save trampoline and original bytes.
        tramp_slot.store(tramp as usize, SeqCst);
        if let Ok(mut sv) = saved().lock() {
            sv.push(Saved { target: target as usize, orig });
        }
        true
    }

    pub(super) unsafe fn call_original_create_file_w(n:*const u16,a:u32,s:u32,sec:*mut c_void,d:u32,f:u32,t:isize)->isize {
        let fp: FnCreateFileW = std::mem::transmute(T_CFW.load(SeqCst)); fp(n,a,s,sec,d,f,t)
    }
    pub(super) unsafe fn call_original_create_file_a(n:*const u8, a:u32,s:u32,sec:*mut c_void,d:u32,f:u32,t:isize)->isize {
        let fp: FnCreateFileA = std::mem::transmute(T_CFA.load(SeqCst)); fp(n,a,s,sec,d,f,t)
    }
    pub(super) unsafe fn call_original_read_file(h:isize,b:*mut c_void,n:u32,r:*mut u32,o:*mut c_void)->i32 {
        let fp: FnReadFile = std::mem::transmute(T_RF.load(SeqCst)); fp(h,b,n,r,o)
    }
    pub(super) unsafe fn call_original_write_file(h:isize,b:*const c_void,n:u32,w:*mut u32,o:*mut c_void)->i32 {
        let fp: FnWriteFile = std::mem::transmute(T_WF.load(SeqCst)); fp(h,b,n,w,o)
    }
    pub(super) unsafe fn call_original_close_handle(h:isize)->i32 {
        let fp: FnCloseHandle = std::mem::transmute(T_CH.load(SeqCst)); fp(h)
    }
    pub(super) unsafe fn call_original_get_overlapped_result(h:isize,o:*mut c_void,b:*mut u32,w:i32)->i32 {
        let fp: FnGetOverlappedResult = std::mem::transmute(T_GOR.load(SeqCst)); fp(h,o,b,w)
    }

    pub(super) unsafe fn install() -> Result<(), Box<dyn std::error::Error>> {
        macro_rules! hook {
            ($slot:ident, $proc:literal, $hook_fn:expr) => {{
                let addr = super::get_proc("kernelbase.dll", $proc)
                    .or_else(|| super::get_proc("kernel32.dll", $proc))
                    .ok_or_else(|| format!("GetProcAddress failed for {}", $proc))?;
                if !patch(addr as *mut u8, $hook_fn as *const u8, &$slot) {
                    return Err(format!("patch failed for {}", $proc).into());
                }
            }};
        }
        hook!(T_CFW, "CreateFileW",         hook_create_file_w as unsafe extern "system" fn(*const u16,u32,u32,*mut c_void,u32,u32,isize)->isize);
        hook!(T_CFA, "CreateFileA",         hook_create_file_a as unsafe extern "system" fn(*const u8, u32,u32,*mut c_void,u32,u32,isize)->isize);
        hook!(T_RF,  "ReadFile",            hook_read_file     as unsafe extern "system" fn(isize,*mut c_void,u32,*mut u32,*mut c_void)->i32);
        hook!(T_WF,  "WriteFile",           hook_write_file    as unsafe extern "system" fn(isize,*const c_void,u32,*mut u32,*mut c_void)->i32);
        hook!(T_CH,  "CloseHandle",         hook_close_handle  as unsafe extern "system" fn(isize)->i32);
        hook!(T_GOR, "GetOverlappedResult", hook_get_overlapped_result as unsafe extern "system" fn(isize,*mut c_void,*mut u32,i32)->i32);
        Ok(())
    }

    pub(super) unsafe fn uninstall() {
        use windows::Win32::System::Memory::PAGE_PROTECTION_FLAGS;
        if let Ok(mut sv) = saved().lock() {
            for s in sv.drain(..) {
                let ptr = s.target as *mut u8;
                let mut old = PAGE_PROTECTION_FLAGS(0);
                if VirtualProtect(ptr as _, JMP_SZ, PAGE_EXECUTE_READWRITE, &mut old).is_ok() {
                    std::ptr::copy_nonoverlapping(s.orig.as_ptr(), ptr, JMP_SZ);
                    let _ = VirtualProtect(ptr as _, JMP_SZ, old, &mut old);
                }
            }
        }
    }
}

// ── Named-pipe write helper ───────────────────────────────────────────────────

fn send_udata(ud: &Udata) {
    let pipe = pipe_handle();
    if pipe == -1 || pipe == 0 { return; }
    let bytes = unsafe {
        std::slice::from_raw_parts(ud as *const Udata as *const u8, std::mem::size_of::<Udata>())
    };
    let mut written: u32 = 0;
    unsafe {
        arch_hooks::call_original_write_file(
            pipe, bytes.as_ptr() as *const c_void, bytes.len() as u32,
            &mut written, std::ptr::null_mut(),
        );
    }
}

// ── COM port path detection ───────────────────────────────────────────────────

fn parse_com_w(path: *const u16) -> Option<u8> {
    if path.is_null() { return None; }
    let mut buf = [0u16; 64];
    let mut len = 0usize;
    unsafe {
        while len < buf.len() {
            let c = *path.add(len);
            if c == 0 { break; }
            buf[len] = c; len += 1;
        }
    }
    parse_com_str(&String::from_utf16_lossy(&buf[..len]).to_uppercase())
}

fn parse_com_a(path: *const u8) -> Option<u8> {
    if path.is_null() { return None; }
    let mut buf = [0u8; 64];
    let mut len = 0usize;
    unsafe {
        while len < buf.len() {
            let c = *path.add(len);
            if c == 0 { break; }
            buf[len] = c; len += 1;
        }
    }
    parse_com_str(&String::from_utf8_lossy(&buf[..len]).to_uppercase())
}

fn parse_com_str(s: &str) -> Option<u8> {
    let stripped = s.trim_start_matches('\\').trim_start_matches('.').trim_start_matches('\\').trim_start_matches('/');
    if let Some(rest) = stripped.strip_prefix("COM") {
        if let Ok(n) = rest.trim_end_matches('\0').parse::<u8>() { return Some(n); }
    }
    let dev = s.trim_start_matches('\\');
    if let Some(rest) = dev.strip_prefix("DEVICE\\SERIAL") {
        if let Ok(n) = rest.trim_end_matches('\0').parse::<u8>() { return Some(n); }
    }
    None
}

// ── Hook implementations (shared between arches) ─────────────────────────────

unsafe extern "system" fn hook_create_file_w(
    lp_file_name: *const u16, dw_desired_access: u32, dw_share_mode: u32,
    lp_security_attributes: *mut c_void, dw_creation_disposition: u32,
    dw_flags_and_attributes: u32, h_template_file: isize,
) -> isize {
    let handle = arch_hooks::call_original_create_file_w(
        lp_file_name, dw_desired_access, dw_share_mode,
        lp_security_attributes, dw_creation_disposition,
        dw_flags_and_attributes, h_template_file,
    );
    if handle != -1 {
        if let Some(port) = parse_com_w(lp_file_name) {
            if let Ok(mut map) = com_map().write() { map.insert(handle, port); }
        }
    }
    handle
}

unsafe extern "system" fn hook_create_file_a(
    lp_file_name: *const u8, dw_desired_access: u32, dw_share_mode: u32,
    lp_security_attributes: *mut c_void, dw_creation_disposition: u32,
    dw_flags_and_attributes: u32, h_template_file: isize,
) -> isize {
    let handle = arch_hooks::call_original_create_file_a(
        lp_file_name, dw_desired_access, dw_share_mode,
        lp_security_attributes, dw_creation_disposition,
        dw_flags_and_attributes, h_template_file,
    );
    if handle != -1 {
        if let Some(port) = parse_com_a(lp_file_name) {
            if let Ok(mut map) = com_map().write() { map.insert(handle, port); }
        }
    }
    handle
}

unsafe extern "system" fn hook_read_file(
    h_file: isize, lp_buffer: *mut c_void, n_to_read: u32,
    lp_bytes_read: *mut u32, lp_overlapped: *mut c_void,
) -> i32 {
    let result = arch_hooks::call_original_read_file(h_file, lp_buffer, n_to_read, lp_bytes_read, lp_overlapped);

    if !IN_HOOK.with(|f| f.get()) {
        let com_port = com_map().read().ok().and_then(|m| m.get(&h_file).copied());
        if let Some(port) = com_port {
            if !lp_overlapped.is_null() {
                // Async read: register for GetOverlappedResult callback.
                if let Ok(mut pending) = pending_reads().write() {
                    pending.insert(lp_overlapped as usize, (port, h_file as i32, lp_buffer as usize));
                }
            } else if result != 0 && !lp_bytes_read.is_null() {
                let bytes_read = *lp_bytes_read as usize;
                if bytes_read > 0 {
                    IN_HOOK.with(|f| f.set(true));
                    let mut ud = Udata::new(port, STATE_RECEIVE, h_file as i32);
                    let len = bytes_read.min(MAX_DATA);
                    ud.data_size = len as i32;
                    std::ptr::copy_nonoverlapping(lp_buffer as *const u8, ud.data.as_mut_ptr(), len);
                    send_udata(&ud);
                    IN_HOOK.with(|f| f.set(false));
                }
            }
        }
    }
    result
}

unsafe extern "system" fn hook_write_file(
    h_file: isize, lp_buffer: *const c_void, n_to_write: u32,
    lp_bytes_written: *mut u32, lp_overlapped: *mut c_void,
) -> i32 {
    // Capture data BEFORE calling original: buffer is always valid here.
    if !IN_HOOK.with(|f| f.get()) && n_to_write > 0 && !lp_buffer.is_null() {
        if let Some(port) = com_map().read().ok().and_then(|m| m.get(&h_file).copied()) {
            IN_HOOK.with(|f| f.set(true));
            let mut ud = Udata::new(port, STATE_SEND, h_file as i32);
            let len = (n_to_write as usize).min(MAX_DATA);
            ud.data_size = len as i32;
            std::ptr::copy_nonoverlapping(lp_buffer as *const u8, ud.data.as_mut_ptr(), len);
            send_udata(&ud);
            IN_HOOK.with(|f| f.set(false));
        }
    }
    arch_hooks::call_original_write_file(h_file, lp_buffer, n_to_write, lp_bytes_written, lp_overlapped)
}

unsafe extern "system" fn hook_close_handle(h_object: isize) -> i32 {
    let com_port = com_map().write().ok().and_then(|mut m| m.remove(&h_object));
    if let Some(port) = com_port {
        if let Ok(mut pending) = pending_reads().write() {
            pending.retain(|_, v| v.1 != h_object as i32);
        }
        if !IN_HOOK.with(|f| f.get()) {
            IN_HOOK.with(|f| f.set(true));
            let ud = Udata::new(port, STATE_DISCONNECT, h_object as i32);
            send_udata(&ud);
            IN_HOOK.with(|f| f.set(false));
        }
    }
    arch_hooks::call_original_close_handle(h_object)
}

unsafe extern "system" fn hook_get_overlapped_result(
    h_file: isize, lp_overlapped: *mut c_void, lp_bytes: *mut u32, b_wait: i32,
) -> i32 {
    let result = arch_hooks::call_original_get_overlapped_result(h_file, lp_overlapped, lp_bytes, b_wait);
    if result != 0 && !IN_HOOK.with(|f| f.get()) && !lp_overlapped.is_null() && !lp_bytes.is_null() {
        let entry = pending_reads().write().ok().and_then(|mut m| m.remove(&(lp_overlapped as usize)));
        if let Some((port, fhandle, buf_ptr)) = entry {
            let bytes = *lp_bytes as usize;
            if bytes > 0 && buf_ptr != 0 {
                IN_HOOK.with(|f| f.set(true));
                let mut ud = Udata::new(port, STATE_RECEIVE, fhandle);
                let len = bytes.min(MAX_DATA);
                ud.data_size = len as i32;
                std::ptr::copy_nonoverlapping(buf_ptr as *const u8, ud.data.as_mut_ptr(), len);
                send_udata(&ud);
                IN_HOOK.with(|f| f.set(false));
            }
        }
    }
    result
}

// ── GetProcAddress helper ─────────────────────────────────────────────────────

unsafe fn get_proc(module: &str, proc: &str) -> Option<*const c_void> {
    let module_w: Vec<u16> = module.encode_utf16().chain(std::iter::once(0u16)).collect();
    let hmod = GetModuleHandleW(PCWSTR(module_w.as_ptr())).ok()?;
    let proc_cstr = std::ffi::CString::new(proc).ok()?;
    let addr = GetProcAddress(hmod, windows::core::PCSTR(proc_cstr.as_ptr() as _))?;
    Some(addr as *const c_void)
}

// ── Install / uninstall (delegates to arch_hooks) ────────────────────────────

unsafe fn install_hooks() -> Result<(), Box<dyn std::error::Error>> { arch_hooks::install() }
unsafe fn uninstall_hooks() { arch_hooks::uninstall(); }

// ── Shared-memory config reader ───────────────────────────────────────────────

fn read_pipe_name_from_shmem() -> Option<String> {
    unsafe {
        let hmap = OpenFileMappingW(FILE_MAP_READ.0, false, SHMEM_NAME).ok()?;
        let view = MapViewOfFile(hmap, FILE_MAP_READ, 0, 0, 512);
        if view.Value.is_null() { let _ = CloseHandle(hmap); return None; }
        let ptr = view.Value as *const u16;
        let mut chars = Vec::with_capacity(128);
        let mut i = 0usize;
        while i < 256 { let c = *ptr.add(i); if c == 0 { break; } chars.push(c); i += 1; }
        let _ = UnmapViewOfFile(view);
        let _ = CloseHandle(hmap);
        if chars.is_empty() { None } else { Some(String::from_utf16_lossy(&chars)) }
    }
}

// ── Pipe client connection ────────────────────────────────────────────────────

fn connect_pipe(pipe_name: &str) -> bool {
    use windows::Win32::Storage::FileSystem::{
        CreateFileW, FILE_FLAGS_AND_ATTRIBUTES, FILE_SHARE_MODE, OPEN_EXISTING,
    };
    let w: Vec<u16> = pipe_name.encode_utf16().chain(std::iter::once(0u16)).collect();
    let handle = unsafe {
        CreateFileW(
            PCWSTR(w.as_ptr()),
            0x40000000u32, FILE_SHARE_MODE(0), None,
            OPEN_EXISTING, FILE_FLAGS_AND_ATTRIBUTES(0), HANDLE::default(),
        )
    };
    match handle {
        Ok(h) if h != INVALID_HANDLE_VALUE && !h.0.is_null() => {
            let raw = h.0 as isize;
            let _ = PIPE.set(Mutex::new(SafeHandle(raw)));
            let _ = h;
            true
        }
        _ => false,
    }
}

fn disconnect_pipe() {
    if let Some(m) = PIPE.get() {
        if let Ok(g) = m.lock() {
            let h = HANDLE(g.0 as *mut c_void);
            if !h.0.is_null() && h.0 as isize != -1 {
                unsafe { let _ = CloseHandle(h); }
            }
        }
    }
}

// ── DllMain ───────────────────────────────────────────────────────────────────

#[no_mangle]
pub unsafe extern "system" fn DllMain(
    _hinst: *mut c_void, fdw_reason: u32, _lpv_reserved: *mut c_void,
) -> i32 {
    match fdw_reason {
        DLL_PROCESS_ATTACH => {
            if let Some(pipe_name) = read_pipe_name_from_shmem() {
                if connect_pipe(&pipe_name) {
                    if install_hooks().is_err() { disconnect_pipe(); }
                }
            }
        }
        DLL_PROCESS_DETACH => {
            uninstall_hooks();
            disconnect_pipe();
        }
        _ => {}
    }
    1
}