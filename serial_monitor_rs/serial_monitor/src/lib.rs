//! serial_monitor.dll — safe host for the injected serial monitor hook.
//!
//! The public ABI is consumed by SerialMonitorPage.xaml.cs.  The injected DLL
//! is deliberately never unloaded from a live target: disabling capture is
//! safe, while proving that no target thread can execute an inline-detour
//! trampoline at FreeLibrary time is not possible here.

#![allow(non_snake_case)]

use std::ffi::{c_void, CString};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Mutex, OnceLock};
use std::time::{Duration, Instant};

use windows::core::{PCSTR, PCWSTR};
use windows::Win32::Foundation::{CloseHandle, GetLastError, HANDLE, INVALID_HANDLE_VALUE};
use windows::Win32::Storage::FileSystem::ReadFile;
use windows::Win32::System::Diagnostics::Debug::WriteProcessMemory;
use windows::Win32::System::Diagnostics::ToolHelp::{
    CreateToolhelp32Snapshot, Module32FirstW, Module32NextW, MODULEENTRY32W, TH32CS_SNAPMODULE,
    TH32CS_SNAPMODULE32,
};
use windows::Win32::System::LibraryLoader::{GetModuleHandleW, GetProcAddress};
use windows::Win32::System::Memory::{
    CreateFileMappingW, MapViewOfFile, UnmapViewOfFile, VirtualAllocEx, VirtualFreeEx,
    FILE_MAP_WRITE, MEM_COMMIT, MEM_RELEASE, MEM_RESERVE, PAGE_READWRITE,
};
use windows::Win32::System::Pipes::{
    ConnectNamedPipe, CreateNamedPipeW, PeekNamedPipe, PIPE_NOWAIT, PIPE_READMODE_MESSAGE,
    PIPE_TYPE_MESSAGE,
};
use windows::Win32::System::Threading::{
    CreateEventW, CreateRemoteThread, GetCurrentProcessId, GetExitCodeThread, OpenProcess,
    SetEvent, Sleep, WaitForSingleObject, PROCESS_ALL_ACCESS,
};

static HOOK_DLL_BYTES: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/serial_monitor_hook.dll"));

const WIRE_MAGIC: u32 = 0x334D_534C; // "LSM3"
const ABI_VERSION: u16 = 3;
const MAX_FRAGMENT_DATA: usize = 8192;
const WIRE_HEADER_SIZE: usize = 64;
const WIRE_PACKET_SIZE: usize = WIRE_HEADER_SIZE + MAX_FRAGMENT_DATA;

const CONFIG_MAGIC: u32 = 0x3343_4D53; // "SMC3"
const CONFIG_PIPE_CHARS: usize = 240;

const STATUS_STARTED: i32 = 1;
const STATUS_STOPPED_MODULE_RETAINED: i32 = 2;
const STATUS_STOP_PENDING_MODULE_RETAINED: i32 = 3;
const STATUS_STOPPED_MODULE_STATE_UNKNOWN: i32 = 4;
const STATUS_NO_SESSION: i32 = 0;
const ERROR_INVALID_ARGUMENT: i32 = -1;
const ERROR_X86_DISABLED: i32 = -2;
const ERROR_ARCHITECTURE_UNKNOWN: i32 = -3;
const ERROR_CROSS_BITNESS: i32 = -4;
const ERROR_IPC_SETUP: i32 = -5;
const ERROR_HOOK_EXTRACTION: i32 = -6;
const ERROR_INJECTION_FAILED: i32 = -7;
const ERROR_INJECTION_TIMEOUT: i32 = -8;
const ERROR_HOOK_LOAD_FAILED: i32 = -9;
const ERROR_HOOK_INITIALIZATION: i32 = -10;
const ERROR_HOOK_INITIALIZATION_TIMEOUT: i32 = -11;
const ERROR_WORKER_START: i32 = -12;
const ERROR_HOOK_VERSION_CONFLICT: i32 = -13;

const HOOK_INIT_OK: u32 = 1;
const HOOK_DEACTIVATE_OK: u32 = 1;
const ERROR_ALREADY_EXISTS_RAW: u32 = 183;
const ERROR_PIPE_CONNECTED_RAW: u32 = 535;
const ERROR_PIPE_LISTENING_RAW: u32 = 536;
const WAIT_OBJECT_0_RAW: u32 = 0;
const PIPE_ACCESS_INBOUND_FLAG: u32 = 0x0000_0001;

const IMAGE_FILE_MACHINE_UNKNOWN: u16 = 0;
const IMAGE_FILE_MACHINE_I386: u16 = 0x014c;
const IMAGE_FILE_MACHINE_AMD64: u16 = 0x8664;
const IMAGE_FILE_MACHINE_ARM64: u16 = 0xaa64;

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

const _: [(); 512] = [(); std::mem::size_of::<SharedConfig>()];

type CallbackFn = unsafe extern "system" fn(*const c_void) -> i32;
type RemoteThreadFn = unsafe extern "system" fn(*mut c_void) -> u32;
type IsWow64Process2Fn = unsafe extern "system" fn(HANDLE, *mut u16, *mut u16) -> i32;
type IsWow64ProcessFn = unsafe extern "system" fn(HANDLE, *mut i32) -> i32;

struct OwnedHandle(HANDLE);

unsafe impl Send for OwnedHandle {}
unsafe impl Sync for OwnedHandle {}

impl OwnedHandle {
    fn new(handle: HANDLE) -> Option<Self> {
        if handle == INVALID_HANDLE_VALUE || handle.0.is_null() {
            None
        } else {
            Some(Self(handle))
        }
    }

    fn raw(&self) -> HANDLE {
        self.0
    }
}

impl Drop for OwnedHandle {
    fn drop(&mut self) {
        if self.0 != INVALID_HANDLE_VALUE && !self.0 .0.is_null() {
            unsafe {
                let _ = CloseHandle(self.0);
            }
            self.0 = INVALID_HANDLE_VALUE;
        }
    }
}

struct Session {
    target_pid: u32,
    hook_module_name: String,
    pipe: Option<OwnedHandle>,
    _mapping: Option<OwnedHandle>,
    stop_event: Option<OwnedHandle>,
    worker: Option<std::thread::JoinHandle<()>>,
}

impl Session {
    fn stop_worker(&mut self) {
        if let Some(stop) = self.stop_event.as_ref() {
            unsafe {
                let _ = SetEvent(stop.raw());
            }
        }
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}

impl Drop for Session {
    fn drop(&mut self) {
        self.stop_worker();
    }
}

static SESSION: OnceLock<Mutex<Option<Session>>> = OnceLock::new();
static OPERATION_GATE: OnceLock<Mutex<()>> = OnceLock::new();
static NEXT_GENERATION: AtomicU64 = AtomicU64::new(1);

fn session_mutex() -> &'static Mutex<Option<Session>> {
    SESSION.get_or_init(|| Mutex::new(None))
}

fn operation_gate() -> &'static Mutex<()> {
    OPERATION_GATE.get_or_init(|| Mutex::new(()))
}

fn lock_recover<T>(mutex: &Mutex<T>) -> std::sync::MutexGuard<'_, T> {
    match mutex.lock() {
        Ok(guard) => guard,
        Err(poisoned) => poisoned.into_inner(),
    }
}

fn wide_null(value: &str) -> Vec<u16> {
    value.encode_utf16().chain(std::iter::once(0)).collect()
}

fn mapping_name(target_pid: u32) -> String {
    format!("Local\\llcom_plus_serial_monitor_v3_{target_pid}")
}

fn hook_binary_hash() -> u64 {
    HOOK_DLL_BYTES
        .iter()
        .fold(0xcbf2_9ce4_8422_2325u64, |hash, byte| {
            (hash ^ u64::from(*byte)).wrapping_mul(0x0000_0100_0000_01b3)
        })
}

fn hook_module_name() -> String {
    let architecture = if cfg!(target_arch = "x86_64") {
        "x64"
    } else {
        "x86"
    };
    format!(
        "serial_monitor_hook_v3_{architecture}_{:016x}.dll",
        hook_binary_hash()
    )
}

fn hook_dll_path(module_name: &str) -> PathBuf {
    let mut path = std::env::temp_dir();
    path.push("llcom_plus_serial_monitor_v3");
    path.push(module_name);
    path
}

fn extract_hook_dll(path: &Path) -> bool {
    let Some(parent) = path.parent() else {
        return false;
    };
    if std::fs::create_dir_all(parent).is_err() {
        return false;
    }

    if let Ok(existing) = std::fs::read(path) {
        if existing == HOOK_DLL_BYTES {
            return true;
        }
        // The hash is part of the filename.  Different contents at the same
        // path indicate corruption or an implausible collision; fail closed.
        return false;
    }

    let temporary = path.with_extension(format!("tmp-{}", unsafe { GetCurrentProcessId() }));
    if std::fs::write(&temporary, HOOK_DLL_BYTES).is_err() {
        return false;
    }
    let renamed = std::fs::rename(&temporary, path).is_ok();
    if !renamed {
        let _ = std::fs::remove_file(&temporary);
        return std::fs::read(path)
            .map(|existing| existing == HOOK_DLL_BYTES)
            .unwrap_or(false);
    }
    true
}

fn create_shared_config(
    target_pid: u32,
    generation: u64,
    selected_com: u32,
    pipe_name: &str,
) -> Option<OwnedHandle> {
    let pipe_utf16: Vec<u16> = pipe_name.encode_utf16().collect();
    if pipe_utf16.is_empty() || pipe_utf16.len() >= CONFIG_PIPE_CHARS {
        return None;
    }

    let mut config = SharedConfig {
        magic: CONFIG_MAGIC,
        abi_version: ABI_VERSION,
        struct_size: std::mem::size_of::<SharedConfig>() as u16,
        generation,
        selected_com,
        host_process_id: unsafe { GetCurrentProcessId() },
        pipe_name_length: pipe_utf16.len() as u16,
        reserved: 0,
        pipe_name: [0; CONFIG_PIPE_CHARS],
    };
    config.pipe_name[..pipe_utf16.len()].copy_from_slice(&pipe_utf16);

    let name = wide_null(&mapping_name(target_pid));
    unsafe {
        let mapping = CreateFileMappingW(
            INVALID_HANDLE_VALUE,
            None,
            PAGE_READWRITE,
            0,
            std::mem::size_of::<SharedConfig>() as u32,
            PCWSTR(name.as_ptr()),
        )
        .ok()?;
        let mapping = OwnedHandle::new(mapping)?;
        if GetLastError().0 == ERROR_ALREADY_EXISTS_RAW {
            return None;
        }

        let view = MapViewOfFile(
            mapping.raw(),
            FILE_MAP_WRITE,
            0,
            0,
            std::mem::size_of::<SharedConfig>(),
        );
        if view.Value.is_null() {
            return None;
        }
        std::ptr::copy_nonoverlapping(
            &config as *const SharedConfig as *const u8,
            view.Value as *mut u8,
            std::mem::size_of::<SharedConfig>(),
        );
        let _ = UnmapViewOfFile(view);
        Some(mapping)
    }
}

fn create_pipe_server(pipe_name: &str) -> Option<OwnedHandle> {
    let name = wide_null(pipe_name);
    let packet_capacity = WIRE_PACKET_SIZE.checked_mul(16)? as u32;
    let pipe = unsafe {
        CreateNamedPipeW(
            PCWSTR(name.as_ptr()),
            windows::Win32::Storage::FileSystem::FILE_FLAGS_AND_ATTRIBUTES(
                PIPE_ACCESS_INBOUND_FLAG,
            ),
            PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_NOWAIT,
            1,
            0,
            packet_capacity,
            0,
            None,
        )
    };
    OwnedHandle::new(pipe)
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum ProcessMachine {
    X86,
    X64,
    Arm64,
    Unknown,
}

fn machine_from_code(machine: u16) -> ProcessMachine {
    match machine {
        IMAGE_FILE_MACHINE_I386 => ProcessMachine::X86,
        IMAGE_FILE_MACHINE_AMD64 => ProcessMachine::X64,
        IMAGE_FILE_MACHINE_ARM64 => ProcessMachine::Arm64,
        _ => ProcessMachine::Unknown,
    }
}

fn local_machine() -> ProcessMachine {
    #[cfg(target_arch = "x86_64")]
    {
        ProcessMachine::X64
    }
    #[cfg(target_arch = "x86")]
    {
        ProcessMachine::X86
    }
    #[cfg(not(any(target_arch = "x86", target_arch = "x86_64")))]
    {
        ProcessMachine::Unknown
    }
}

fn query_process_machine(pid: u32) -> Option<ProcessMachine> {
    let process = unsafe { OpenProcess(PROCESS_ALL_ACCESS, false, pid).ok()? };
    let process = OwnedHandle::new(process)?;
    let kernel = unsafe { GetModuleHandleW(windows::core::w!("kernel32.dll")).ok()? };

    unsafe {
        if let Some(proc_address) = GetProcAddress(kernel, windows::core::s!("IsWow64Process2")) {
            let function: IsWow64Process2Fn = std::mem::transmute::<
                unsafe extern "system" fn() -> isize,
                IsWow64Process2Fn,
            >(proc_address);
            let mut process_machine = IMAGE_FILE_MACHINE_UNKNOWN;
            let mut native_machine = IMAGE_FILE_MACHINE_UNKNOWN;
            if function(process.raw(), &mut process_machine, &mut native_machine) != 0 {
                return Some(machine_from_code(
                    if process_machine == IMAGE_FILE_MACHINE_UNKNOWN {
                        native_machine
                    } else {
                        process_machine
                    },
                ));
            }
        }

        let proc_address = GetProcAddress(kernel, windows::core::s!("IsWow64Process"))?;
        let function: IsWow64ProcessFn = std::mem::transmute::<
            unsafe extern "system" fn() -> isize,
            IsWow64ProcessFn,
        >(proc_address);
        let mut is_wow64 = 0i32;
        if function(process.raw(), &mut is_wow64) == 0 {
            return None;
        }
        if local_machine() == ProcessMachine::X64 {
            Some(if is_wow64 != 0 {
                ProcessMachine::X86
            } else {
                ProcessMachine::X64
            })
        } else {
            Some(ProcessMachine::X86)
        }
    }
}

#[derive(Clone)]
struct ModuleInfo {
    base: usize,
    size: usize,
    name: String,
}

fn module_name(entry: &MODULEENTRY32W) -> String {
    let length = entry
        .szModule
        .iter()
        .position(|value| *value == 0)
        .unwrap_or(entry.szModule.len());
    String::from_utf16_lossy(&entry.szModule[..length])
}

fn enumerate_modules(pid: u32) -> Vec<ModuleInfo> {
    let mut modules = Vec::new();
    unsafe {
        let Ok(snapshot) = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid)
        else {
            return modules;
        };
        let Some(snapshot) = OwnedHandle::new(snapshot) else {
            return modules;
        };
        let mut entry = MODULEENTRY32W {
            dwSize: std::mem::size_of::<MODULEENTRY32W>() as u32,
            ..Default::default()
        };
        if Module32FirstW(snapshot.raw(), &mut entry).is_ok() {
            loop {
                modules.push(ModuleInfo {
                    base: entry.modBaseAddr as usize,
                    size: entry.modBaseSize as usize,
                    name: module_name(&entry),
                });
                if Module32NextW(snapshot.raw(), &mut entry).is_err() {
                    break;
                }
            }
        }
    }
    modules
}

fn find_module(pid: u32, expected_name: &str) -> Option<ModuleInfo> {
    enumerate_modules(pid)
        .into_iter()
        .find(|module| module.name.eq_ignore_ascii_case(expected_name))
}

fn find_loaded_hook_conflict(pid: u32, expected_name: &str) -> bool {
    enumerate_modules(pid).into_iter().any(|module| {
        module
            .name
            .to_ascii_lowercase()
            .starts_with("serial_monitor_hook")
            && !module.name.eq_ignore_ascii_case(expected_name)
    })
}

fn resolve_remote_system_proc(pid: u32, module_name: &str, proc_name: &str) -> Option<usize> {
    let local_module_name = wide_null(module_name);
    let local_module = unsafe { GetModuleHandleW(PCWSTR(local_module_name.as_ptr())).ok()? };
    let proc_name = CString::new(proc_name).ok()?;
    let local_proc =
        unsafe { GetProcAddress(local_module, PCSTR(proc_name.as_ptr() as *const u8))? } as usize;

    // GetProcAddress may resolve a forwarded kernel32 export into KernelBase.
    // Locate the module that actually owns the returned address, then apply
    // that RVA to the same module in the target process.
    let local_owner = enumerate_modules(unsafe { GetCurrentProcessId() })
        .into_iter()
        .find(|module| {
            local_proc >= module.base && local_proc < module.base.saturating_add(module.size)
        })?;
    let remote_owner = find_module(pid, &local_owner.name)?;
    remote_owner
        .base
        .checked_add(local_proc.checked_sub(local_owner.base)?)
}

fn read_u16(bytes: &[u8], offset: usize) -> Option<u16> {
    let data = bytes.get(offset..offset.checked_add(2)?)?;
    Some(u16::from_le_bytes([data[0], data[1]]))
}

fn read_u32(bytes: &[u8], offset: usize) -> Option<u32> {
    let data = bytes.get(offset..offset.checked_add(4)?)?;
    Some(u32::from_le_bytes([data[0], data[1], data[2], data[3]]))
}

fn rva_to_file_offset(bytes: &[u8], pe_offset: usize, rva: u32) -> Option<usize> {
    let optional_header = pe_offset.checked_add(24)?;
    let section_count = read_u16(bytes, pe_offset.checked_add(6)?)? as usize;
    let optional_size = read_u16(bytes, pe_offset.checked_add(20)?)? as usize;
    let size_of_headers = read_u32(bytes, optional_header.checked_add(60)?)?;
    if rva < size_of_headers {
        return Some(rva as usize);
    }
    let sections = optional_header.checked_add(optional_size)?;
    for index in 0..section_count {
        let section = sections.checked_add(index.checked_mul(40)?)?;
        let virtual_size = read_u32(bytes, section.checked_add(8)?)?;
        let virtual_address = read_u32(bytes, section.checked_add(12)?)?;
        let raw_size = read_u32(bytes, section.checked_add(16)?)?;
        let raw_offset = read_u32(bytes, section.checked_add(20)?)?;
        let mapped_size = virtual_size.max(raw_size);
        if rva >= virtual_address && rva < virtual_address.checked_add(mapped_size)? {
            let within = rva.checked_sub(virtual_address)?;
            if within >= raw_size {
                return None;
            }
            return Some(raw_offset.checked_add(within)? as usize);
        }
    }
    None
}

fn pe_export_rva(bytes: &[u8], export_name: &str) -> Option<u32> {
    if bytes.get(0..2)? != b"MZ" {
        return None;
    }
    let pe_offset = read_u32(bytes, 0x3c)? as usize;
    if bytes.get(pe_offset..pe_offset.checked_add(4)?)? != b"PE\0\0" {
        return None;
    }
    let optional_header = pe_offset.checked_add(24)?;
    let magic = read_u16(bytes, optional_header)?;
    let data_directory = match magic {
        0x10b => optional_header.checked_add(96)?,
        0x20b => optional_header.checked_add(112)?,
        _ => return None,
    };
    let export_rva = read_u32(bytes, data_directory)?;
    let export_size = read_u32(bytes, data_directory.checked_add(4)?)?;
    let export_offset = rva_to_file_offset(bytes, pe_offset, export_rva)?;
    let number_of_functions = read_u32(bytes, export_offset.checked_add(20)?)?;
    let number_of_names = read_u32(bytes, export_offset.checked_add(24)?)?;
    let functions_rva = read_u32(bytes, export_offset.checked_add(28)?)?;
    let names_rva = read_u32(bytes, export_offset.checked_add(32)?)?;
    let ordinals_rva = read_u32(bytes, export_offset.checked_add(36)?)?;
    let functions = rva_to_file_offset(bytes, pe_offset, functions_rva)?;
    let names = rva_to_file_offset(bytes, pe_offset, names_rva)?;
    let ordinals = rva_to_file_offset(bytes, pe_offset, ordinals_rva)?;

    for index in 0..number_of_names as usize {
        let name_rva = read_u32(bytes, names.checked_add(index.checked_mul(4)?)?)?;
        let name_offset = rva_to_file_offset(bytes, pe_offset, name_rva)?;
        let tail = bytes.get(name_offset..)?;
        let end = tail.iter().position(|value| *value == 0)?;
        if tail.get(..end)? != export_name.as_bytes() {
            continue;
        }
        let ordinal = read_u16(bytes, ordinals.checked_add(index.checked_mul(2)?)?)? as u32;
        if ordinal >= number_of_functions {
            return None;
        }
        let function_rva = read_u32(
            bytes,
            functions.checked_add((ordinal as usize).checked_mul(4)?)?,
        )?;
        // A function RVA inside the export directory is a forwarder string.
        if function_rva >= export_rva && function_rva < export_rva.checked_add(export_size)? {
            return None;
        }
        return Some(function_rva);
    }
    None
}

enum RemoteCallOutcome {
    Complete(u32),
    Pending(OwnedHandle),
    Failed,
}

fn start_remote_call(
    process: &OwnedHandle,
    start_address: usize,
    parameter: *mut c_void,
    timeout_ms: u32,
) -> RemoteCallOutcome {
    if start_address == 0 {
        return RemoteCallOutcome::Failed;
    }
    let start: RemoteThreadFn =
        unsafe { std::mem::transmute::<usize, RemoteThreadFn>(start_address) };
    let thread = unsafe {
        CreateRemoteThread(
            process.raw(),
            None,
            0,
            Some(start),
            Some(parameter),
            0,
            None,
        )
    };
    let Ok(thread) = thread else {
        return RemoteCallOutcome::Failed;
    };
    let Some(thread) = OwnedHandle::new(thread) else {
        return RemoteCallOutcome::Failed;
    };
    let wait = unsafe { WaitForSingleObject(thread.raw(), timeout_ms) }.0;
    if wait == WAIT_OBJECT_0_RAW {
        let mut exit_code = 0u32;
        if unsafe { GetExitCodeThread(thread.raw(), &mut exit_code) }.is_ok() {
            RemoteCallOutcome::Complete(exit_code)
        } else {
            RemoteCallOutcome::Failed
        }
    } else {
        RemoteCallOutcome::Pending(thread)
    }
}

fn wait_thread_or_process_exit(process: &OwnedHandle, thread: &OwnedHandle) -> Option<u32> {
    loop {
        let thread_wait = unsafe { WaitForSingleObject(thread.raw(), 1_000) }.0;
        if thread_wait == WAIT_OBJECT_0_RAW {
            let mut exit_code = 0u32;
            return unsafe { GetExitCodeThread(thread.raw(), &mut exit_code) }
                .ok()
                .map(|_| exit_code);
        }
        if unsafe { WaitForSingleObject(process.raw(), 0) }.0 == WAIT_OBJECT_0_RAW {
            return None;
        }
    }
}

fn spawn_load_timeout_reaper(process: OwnedHandle, thread: OwnedHandle, remote_memory: usize) {
    let task = move || {
        let completed = wait_thread_or_process_exit(&process, &thread).is_some();
        if completed {
            unsafe {
                let _ = VirtualFreeEx(process.raw(), remote_memory as *mut c_void, 0, MEM_RELEASE);
            }
        }
        // If the target exited, its address space (including remote_memory) was
        // reclaimed by the kernel. OwnedHandle then closes both local handles.
    };
    if std::thread::Builder::new()
        .name("serial-monitor-load-reaper".into())
        .spawn(task)
        .is_err()
    {
        // The target kernel object still owns the allocation.  Avoid freeing
        // memory that a running remote thread may dereference; target exit is
        // the final reclamation boundary in this exceptional resource failure.
    }
}

enum EnsureHookResult {
    Loaded {
        process: OwnedHandle,
        module_base: usize,
    },
    TimedOut,
    Failed(i32),
}

fn ensure_hook_loaded(pid: u32, module_name: &str, path: &Path) -> EnsureHookResult {
    if find_loaded_hook_conflict(pid, module_name) {
        return EnsureHookResult::Failed(ERROR_HOOK_VERSION_CONFLICT);
    }
    if let Some(module) = find_module(pid, module_name) {
        let process = unsafe { OpenProcess(PROCESS_ALL_ACCESS, false, pid).ok() };
        return match process.and_then(OwnedHandle::new) {
            Some(process) => EnsureHookResult::Loaded {
                process,
                module_base: module.base,
            },
            None => EnsureHookResult::Failed(ERROR_INJECTION_FAILED),
        };
    }

    let load_library = match resolve_remote_system_proc(pid, "kernel32.dll", "LoadLibraryW") {
        Some(address) => address,
        None => return EnsureHookResult::Failed(ERROR_INJECTION_FAILED),
    };
    let process = match unsafe { OpenProcess(PROCESS_ALL_ACCESS, false, pid).ok() }
        .and_then(OwnedHandle::new)
    {
        Some(process) => process,
        None => return EnsureHookResult::Failed(ERROR_INJECTION_FAILED),
    };
    let Some(path_text) = path.to_str() else {
        return EnsureHookResult::Failed(ERROR_HOOK_EXTRACTION);
    };
    let path_wide = wide_null(path_text);
    let byte_count = path_wide.len().saturating_mul(2);
    let remote = unsafe {
        VirtualAllocEx(
            process.raw(),
            None,
            byte_count,
            MEM_COMMIT | MEM_RESERVE,
            PAGE_READWRITE,
        )
    };
    if remote.is_null() {
        return EnsureHookResult::Failed(ERROR_INJECTION_FAILED);
    }
    if unsafe {
        WriteProcessMemory(
            process.raw(),
            remote,
            path_wide.as_ptr() as *const c_void,
            byte_count,
            None,
        )
    }
    .is_err()
    {
        unsafe {
            let _ = VirtualFreeEx(process.raw(), remote, 0, MEM_RELEASE);
        }
        return EnsureHookResult::Failed(ERROR_INJECTION_FAILED);
    }

    match start_remote_call(&process, load_library, remote, 8_000) {
        RemoteCallOutcome::Complete(exit_code) => {
            unsafe {
                let _ = VirtualFreeEx(process.raw(), remote, 0, MEM_RELEASE);
            }
            // GetExitCodeThread is DWORD-sized and therefore truncates HMODULE
            // on x64. We still inspect it (zero is diagnostic), but module
            // enumeration is the authoritative load result.
            let _load_library_exit_code = exit_code;
            let deadline = Instant::now() + Duration::from_secs(2);
            loop {
                if let Some(module) = find_module(pid, module_name) {
                    return EnsureHookResult::Loaded {
                        process,
                        module_base: module.base,
                    };
                }
                if Instant::now() >= deadline {
                    return EnsureHookResult::Failed(ERROR_HOOK_LOAD_FAILED);
                }
                unsafe { Sleep(10) };
            }
        }
        RemoteCallOutcome::Pending(thread) => {
            spawn_load_timeout_reaper(process, thread, remote as usize);
            EnsureHookResult::TimedOut
        }
        RemoteCallOutcome::Failed => {
            unsafe {
                let _ = VirtualFreeEx(process.raw(), remote, 0, MEM_RELEASE);
            }
            EnsureHookResult::Failed(ERROR_INJECTION_FAILED)
        }
    }
}

fn resolve_hook_export(module_base: usize, export_name: &str) -> Option<usize> {
    let rva = pe_export_rva(HOOK_DLL_BYTES, export_name)? as usize;
    module_base.checked_add(rva)
}

fn spawn_late_initialization_cleanup(
    process: OwnedHandle,
    initialization_thread: OwnedHandle,
    deactivate_address: usize,
    session: Session,
) {
    let task = move || {
        let initialized =
            wait_thread_or_process_exit(&process, &initialization_thread) == Some(HOOK_INIT_OK);
        if !initialized {
            drop(session);
            return;
        }

        let deactivated =
            match start_remote_call(&process, deactivate_address, std::ptr::null_mut(), 5_000) {
                RemoteCallOutcome::Complete(code) => code == HOOK_DEACTIVATE_OK,
                RemoteCallOutcome::Pending(thread) => {
                    match wait_thread_or_process_exit(&process, &thread) {
                        Some(code) => code == HOOK_DEACTIVATE_OK,
                        None => true, // Target exit reclaimed the module and IPC handle.
                    }
                }
                RemoteCallOutcome::Failed => false,
            };
        if deactivated {
            drop(session);
        } else {
            // A late successful initialization must never be abandoned active.
            // Keep IPC alive and retry bounded remote deactivation calls until
            // success or target exit.
            spawn_deactivation_retry(session);
        }
    };
    let _ = std::thread::Builder::new()
        .name("serial-monitor-init-cleanup".into())
        .spawn(task);
}

fn spawn_worker(
    pipe: HANDLE,
    stop: HANDLE,
    generation: u64,
    selected_com: u32,
    callback: CallbackFn,
) -> std::io::Result<std::thread::JoinHandle<()>> {
    let pipe_value = pipe.0 as isize;
    let stop_value = stop.0 as isize;
    std::thread::Builder::new()
        .name("serial-monitor-pipe".into())
        .spawn(move || {
            worker_main(
                HANDLE(pipe_value as *mut c_void),
                HANDLE(stop_value as *mut c_void),
                generation,
                selected_com,
                callback,
            )
        })
}

fn packet_is_valid(packet: &MonitorPacket, generation: u64, selected_com: u32) -> bool {
    packet.magic == WIRE_MAGIC
        && packet.abi_version == ABI_VERSION
        && packet.header_size as usize == WIRE_HEADER_SIZE
        && packet.generation == generation
        && packet.com_port == selected_com
        && packet.data_length as usize <= MAX_FRAGMENT_DATA
        && packet.fragment_offset <= packet.total_length
        && packet
            .fragment_offset
            .checked_add(packet.data_length)
            .map(|end| end <= packet.total_length)
            .unwrap_or(false)
}

fn worker_main(
    pipe: HANDLE,
    stop: HANDLE,
    generation: u64,
    selected_com: u32,
    callback: CallbackFn,
) {
    const CONNECT_TIMEOUT_MS: u64 = 10_000;
    const POLL_MS: u32 = 5;
    unsafe {
        let connect_started = Instant::now();
        loop {
            if WaitForSingleObject(stop, 0).0 == WAIT_OBJECT_0_RAW {
                return;
            }
            match ConnectNamedPipe(pipe, None) {
                Ok(_) => break,
                Err(error) => {
                    let code = (error.code().0 as u32) & 0xffff;
                    match code {
                        ERROR_PIPE_CONNECTED_RAW => break,
                        ERROR_PIPE_LISTENING_RAW => {
                            if connect_started.elapsed()
                                >= Duration::from_millis(CONNECT_TIMEOUT_MS)
                            {
                                return;
                            }
                            Sleep(POLL_MS);
                        }
                        _ => return,
                    }
                }
            }
        }

        loop {
            if WaitForSingleObject(stop, 0).0 == WAIT_OBJECT_0_RAW {
                break;
            }
            let mut available = 0u32;
            if PeekNamedPipe(pipe, None, 0, None, Some(&mut available), None).is_err() {
                break;
            }
            if available as usize >= WIRE_PACKET_SIZE {
                let mut packet: MonitorPacket = std::mem::zeroed();
                let bytes = std::slice::from_raw_parts_mut(
                    &mut packet as *mut MonitorPacket as *mut u8,
                    WIRE_PACKET_SIZE,
                );
                let mut bytes_read = 0u32;
                if ReadFile(pipe, Some(bytes), Some(&mut bytes_read), None).is_err()
                    || bytes_read as usize != WIRE_PACKET_SIZE
                {
                    break;
                }
                if packet_is_valid(&packet, generation, selected_com) {
                    let _ = callback(&packet as *const MonitorPacket as *const c_void);
                }
            } else {
                Sleep(POLL_MS);
            }
        }
    }
}

fn deactivate_export_address(pid: u32, module_name: &str) -> Option<(OwnedHandle, usize)> {
    let module = find_module(pid, module_name)?;
    let process = unsafe { OpenProcess(PROCESS_ALL_ACCESS, false, pid).ok()? };
    let process = OwnedHandle::new(process)?;
    let address = resolve_hook_export(module.base, "SerialMonitorDeactivate")?;
    Some((process, address))
}

fn spawn_deactivation_retry(mut session: Session) {
    session.stop_worker();
    let pid = session.target_pid;
    let module_name = session.hook_module_name.clone();
    let task = move || loop {
        let Some((process, address)) = deactivate_export_address(pid, &module_name) else {
            // Target exit or module disappearance is a safe reclamation boundary.
            drop(session);
            return;
        };
        match start_remote_call(&process, address, std::ptr::null_mut(), 5_000) {
            RemoteCallOutcome::Complete(code) if code == HOOK_DEACTIVATE_OK => {
                drop(session);
                return;
            }
            RemoteCallOutcome::Pending(thread) => {
                if wait_thread_or_process_exit(&process, &thread) == Some(HOOK_DEACTIVATE_OK) {
                    drop(session);
                    return;
                }
            }
            RemoteCallOutcome::Complete(_) | RemoteCallOutcome::Failed => {}
        }
        unsafe { Sleep(250) };
    };
    let _ = std::thread::Builder::new()
        .name("serial-monitor-deactivate-retry".into())
        .spawn(task);
}

fn stop_current_session() -> i32 {
    let mut session = {
        let mut guard = lock_recover(session_mutex());
        guard.take()
    };
    let Some(mut session_value) = session.take() else {
        return STATUS_NO_SESSION;
    };

    // Stop managed callbacks first.  IPC handles remain open until the injected
    // side has stopped admitting capture calls, so it cannot race a CloseHandle
    // against an in-flight pipe write.
    session_value.stop_worker();
    let Some((process, address)) =
        deactivate_export_address(session_value.target_pid, &session_value.hook_module_name)
    else {
        return STATUS_STOPPED_MODULE_RETAINED;
    };

    match start_remote_call(&process, address, std::ptr::null_mut(), 5_000) {
        RemoteCallOutcome::Complete(code) if code == HOOK_DEACTIVATE_OK => {
            STATUS_STOPPED_MODULE_RETAINED
        }
        RemoteCallOutcome::Pending(thread) => {
            let task_session = session_value;
            let task = move || {
                let completed =
                    wait_thread_or_process_exit(&process, &thread) == Some(HOOK_DEACTIVATE_OK);
                if completed {
                    drop(task_session);
                } else {
                    spawn_deactivation_retry(task_session);
                }
            };
            let _ = std::thread::Builder::new()
                .name("serial-monitor-deactivate-wait".into())
                .spawn(task);
            STATUS_STOP_PENDING_MODULE_RETAINED
        }
        RemoteCallOutcome::Complete(_) | RemoteCallOutcome::Failed => {
            spawn_deactivation_retry(session_value);
            STATUS_STOPPED_MODULE_STATE_UNKNOWN
        }
    }
}

/// Start monitoring. Returns a positive status on success and a negative,
/// stable error code on failure. x86 injection is intentionally disabled.
///
/// # Safety
/// `callback` must remain a valid system-ABI function pointer until
/// `UnMonitorComm` reports that callback delivery has stopped. The callback
/// must accept a pointer valid only for the duration of that invocation.
#[no_mangle]
pub unsafe extern "system" fn MonitorComm(
    pid: u32,
    com_index: u32,
    callback: Option<CallbackFn>,
) -> i32 {
    let _operation = lock_recover(operation_gate());
    let _ = stop_current_session();

    let Some(callback) = callback else {
        return ERROR_INVALID_ARGUMENT;
    };
    if pid == 0 || com_index == 0 || com_index > 4096 {
        return ERROR_INVALID_ARGUMENT;
    }
    if cfg!(target_arch = "x86") {
        return ERROR_X86_DISABLED;
    }

    let Some(target_machine) = query_process_machine(pid) else {
        return ERROR_ARCHITECTURE_UNKNOWN;
    };
    if target_machine != local_machine() {
        return ERROR_CROSS_BITNESS;
    }

    let generation = NEXT_GENERATION.fetch_add(1, Ordering::Relaxed).max(1);
    let host_pid = unsafe { GetCurrentProcessId() };
    let pipe_name =
        format!("\\\\.\\pipe\\llcom_plus_serial_monitor_v3_{host_pid}_{pid}_{generation}");
    let Some(pipe) = create_pipe_server(&pipe_name) else {
        return ERROR_IPC_SETUP;
    };
    let Some(mapping) = create_shared_config(pid, generation, com_index, &pipe_name) else {
        return ERROR_IPC_SETUP;
    };
    let Some(stop_event) = CreateEventW(None, true, false, None)
        .ok()
        .and_then(OwnedHandle::new)
    else {
        return ERROR_IPC_SETUP;
    };

    let module_name = hook_module_name();
    let hook_path = hook_dll_path(&module_name);
    if !extract_hook_dll(&hook_path) {
        return ERROR_HOOK_EXTRACTION;
    }

    let mut pending_session = Session {
        target_pid: pid,
        hook_module_name: module_name.clone(),
        pipe: Some(pipe),
        _mapping: Some(mapping),
        stop_event: Some(stop_event),
        worker: None,
    };

    let (process, module_base) = match ensure_hook_loaded(pid, &module_name, &hook_path) {
        EnsureHookResult::Loaded {
            process,
            module_base,
        } => (process, module_base),
        EnsureHookResult::TimedOut => return ERROR_INJECTION_TIMEOUT,
        EnsureHookResult::Failed(code) => return code,
    };
    let Some(initialize_address) = resolve_hook_export(module_base, "SerialMonitorInitialize")
    else {
        return ERROR_HOOK_INITIALIZATION;
    };
    let Some(deactivate_address) = resolve_hook_export(module_base, "SerialMonitorDeactivate")
    else {
        return ERROR_HOOK_INITIALIZATION;
    };

    match start_remote_call(&process, initialize_address, std::ptr::null_mut(), 10_000) {
        RemoteCallOutcome::Complete(code) => {
            if code != HOOK_INIT_OK {
                return ERROR_HOOK_INITIALIZATION - (code.min(999) as i32);
            }
        }
        RemoteCallOutcome::Pending(thread) => {
            spawn_late_initialization_cleanup(process, thread, deactivate_address, pending_session);
            return ERROR_HOOK_INITIALIZATION_TIMEOUT;
        }
        RemoteCallOutcome::Failed => return ERROR_HOOK_INITIALIZATION,
    }

    let pipe_handle = pending_session.pipe.as_ref().unwrap().raw();
    let stop_handle = pending_session.stop_event.as_ref().unwrap().raw();
    let worker = match spawn_worker(pipe_handle, stop_handle, generation, com_index, callback) {
        Ok(worker) => worker,
        Err(_) => {
            // No managed callback worker was published. Keep IPC owned by an
            // asynchronous deactivation retry rather than closing it under an
            // admitted target-side capture call.
            spawn_deactivation_retry(pending_session);
            return ERROR_WORKER_START;
        }
    };
    pending_session.worker = Some(worker);

    let mut guard = lock_recover(session_mutex());
    if guard.is_some() {
        drop(guard);
        spawn_deactivation_retry(pending_session);
        return ERROR_WORKER_START;
    }
    *guard = Some(pending_session);
    STATUS_STARTED
}

/// Side-effect-free ABI probe. Managed code calls this before any lifecycle API
/// so a stale repository DLL cannot be used with a newer packet definition.
#[no_mangle]
pub extern "system" fn SerialMonitorGetAbiVersion() -> u32 {
    u32::from(ABI_VERSION)
}

/// Stop callbacks and deactivate capture. The hook module intentionally stays
/// mapped until the target exits; status 2/3/4 describes the deactivation state.
#[no_mangle]
pub extern "system" fn UnMonitorComm() -> i32 {
    let _operation = lock_recover(operation_gate());
    stop_current_session()
}

/// DllMain performs no injection, pipe, thread, or hook work.
#[no_mangle]
pub extern "system" fn DllMain(_module: *mut c_void, _reason: u32, _reserved: *mut c_void) -> i32 {
    1
}
