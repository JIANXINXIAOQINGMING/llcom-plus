//! Typed stdcall trampolines backed by vendored MinHook 1.3.4.
//! MinHook decodes/relocates whole instructions and adjusts suspended thread
//! EIP values while changing entry points. Trampolines are retained for the
//! module lifetime, matching the capture hook's no-live-unload policy.

use std::ffi::c_void;
use std::sync::OnceLock;

#[derive(Clone, Copy, Debug)]
pub(crate) struct Error(i32);

impl std::fmt::Display for Error {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(formatter, "MinHook status {}", self.0)
    }
}

impl std::error::Error for Error {}

extern "system" {
    fn MH_Initialize() -> i32;
    fn MH_CreateHook(target: *mut c_void, detour: *mut c_void, original: *mut *mut c_void) -> i32;
    fn MH_EnableHook(target: *mut c_void) -> i32;
    fn MH_DisableHook(target: *mut c_void) -> i32;
}

static INITIALIZED: OnceLock<Result<(), Error>> = OnceLock::new();

pub(crate) struct GenericDetour<T> {
    target: *mut c_void,
    original: T,
}

// These fields are immutable function addresses. MinHook synchronizes patching;
// the capture lifecycle owns their validity until the target process exits.
unsafe impl<T: Send> Send for GenericDetour<T> {}
unsafe impl<T: Sync> Sync for GenericDetour<T> {}

impl<T: Copy> GenericDetour<T> {
    pub(crate) unsafe fn new(target: T, detour: T) -> Result<Self, Error> {
        if std::mem::size_of::<T>() != std::mem::size_of::<*mut c_void>() {
            return Err(Error(-1000));
        }
        (*INITIALIZED.get_or_init(|| match MH_Initialize() {
            0 | 1 => Ok(()), // MH_OK or already initialized by this module
            status => Err(Error(status)),
        }))?;
        let target = std::mem::transmute_copy::<T, *mut c_void>(&target);
        let detour = std::mem::transmute_copy::<T, *mut c_void>(&detour);
        let mut original = std::ptr::null_mut();
        let status = MH_CreateHook(target, detour, &mut original);
        if status != 0 || original.is_null() {
            return Err(Error(status));
        }
        Ok(Self {
            target,
            original: std::mem::transmute_copy(&original),
        })
    }

    pub(crate) unsafe fn enable(&self) -> Result<(), Error> {
        match MH_EnableHook(self.target) {
            0 | 5 => Ok(()), // MH_OK or MH_ERROR_ENABLED
            status => Err(Error(status)),
        }
    }

    pub(crate) unsafe fn disable(&self) -> Result<(), Error> {
        match MH_DisableHook(self.target) {
            0 | 6 => Ok(()), // MH_OK or MH_ERROR_DISABLED
            status => Err(Error(status)),
        }
    }
}

macro_rules! trampoline_call {
    ($($argument:ident),*) => {
        impl<R, $($argument,)*> GenericDetour<unsafe extern "system" fn($($argument),*) -> R> {
            #[allow(non_snake_case, dead_code)]
            pub(crate) unsafe fn call(&self, $($argument: $argument),*) -> R {
                (self.original)($($argument),*)
            }
        }
    };
}
trampoline_call!(A);
trampoline_call!(A, B);
trampoline_call!(A, B, C);
trampoline_call!(A, B, C, D);
trampoline_call!(A, B, C, D, E);
trampoline_call!(A, B, C, D, E, F);
trampoline_call!(A, B, C, D, E, F, G);
