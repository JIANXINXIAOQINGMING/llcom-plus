//! Build script for serial_monitor.dll.
//!
//! The hook is built first by ../build.ps1. This script verifies that the
//! resulting PE machine matches Cargo's target before embedding it; it never
//! recursively invokes Cargo (which would deadlock on Cargo's target lock).

use std::path::{Path, PathBuf};

fn read_u16(bytes: &[u8], offset: usize) -> Option<u16> {
    let data = bytes.get(offset..offset.checked_add(2)?)?;
    Some(u16::from_le_bytes([data[0], data[1]]))
}

fn read_u32(bytes: &[u8], offset: usize) -> Option<u32> {
    let data = bytes.get(offset..offset.checked_add(4)?)?;
    Some(u32::from_le_bytes([data[0], data[1], data[2], data[3]]))
}

fn pe_machine(path: &Path) -> Option<u16> {
    let bytes = std::fs::read(path).ok()?;
    if bytes.get(0..2)? != b"MZ" {
        return None;
    }
    let pe = read_u32(&bytes, 0x3c)? as usize;
    if bytes.get(pe..pe.checked_add(4)?)? != b"PE\0\0" {
        return None;
    }
    read_u16(&bytes, pe.checked_add(4)?)
}

fn main() {
    const IMAGE_FILE_MACHINE_I386: u16 = 0x014c;
    const IMAGE_FILE_MACHINE_AMD64: u16 = 0x8664;

    let manifest_dir =
        PathBuf::from(std::env::var("CARGO_MANIFEST_DIR").expect("CARGO_MANIFEST_DIR not set"));
    let workspace_root = manifest_dir
        .parent()
        .expect("serial_monitor crate has no parent directory");
    let target = std::env::var("TARGET").expect("TARGET not set");
    let profile = std::env::var("PROFILE").unwrap_or_else(|_| "release".into());
    let expected_machine = match target.as_str() {
        "i686-pc-windows-msvc" => IMAGE_FILE_MACHINE_I386,
        "x86_64-pc-windows-msvc" => IMAGE_FILE_MACHINE_AMD64,
        _ => panic!("unsupported serial-monitor target: {target}"),
    };

    let hook_dll = workspace_root
        .join("target")
        .join(&target)
        .join(&profile)
        .join("serial_monitor_hook.dll");
    if !hook_dll.exists() {
        panic!(
            "serial_monitor_hook.dll not found at {}. Run .\\build.ps1 so the hook is built first.",
            hook_dll.display()
        );
    }
    let actual_machine = pe_machine(&hook_dll)
        .unwrap_or_else(|| panic!("hook is not a valid PE DLL: {}", hook_dll.display()));
    if actual_machine != expected_machine {
        panic!(
            "hook architecture mismatch at {}: expected PE machine 0x{expected_machine:04x}, got 0x{actual_machine:04x}",
            hook_dll.display()
        );
    }

    let out_dir = PathBuf::from(std::env::var("OUT_DIR").expect("OUT_DIR not set"));
    let destination = out_dir.join("serial_monitor_hook.dll");
    std::fs::copy(&hook_dll, &destination)
        .expect("failed to copy serial_monitor_hook.dll into OUT_DIR");

    println!("cargo:rerun-if-changed={}", hook_dll.display());
    for relative in [
        "serial_monitor_hook/src/lib.rs",
        "serial_monitor_hook/Cargo.toml",
        "serial_monitor/src/lib.rs",
        "serial_monitor/Cargo.toml",
    ] {
        println!(
            "cargo:rerun-if-changed={}",
            workspace_root.join(relative).display()
        );
    }
}
