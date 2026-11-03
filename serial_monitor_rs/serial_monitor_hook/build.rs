use std::path::PathBuf;

fn main() {
    let root =
        PathBuf::from(std::env::var("CARGO_MANIFEST_DIR").unwrap()).join("../vendor/minhook");
    if std::env::var("CARGO_CFG_TARGET_ARCH").as_deref() == Ok("x86") {
        cc::Build::new()
            .include(root.join("include"))
            .include(root.join("src"))
            .file(root.join("src/buffer.c"))
            .file(root.join("src/hook.c"))
            .file(root.join("src/trampoline.c"))
            .file(root.join("src/hde/hde32.c"))
            .warnings(false)
            .compile("llcom_minhook_x86");
    }
    println!("cargo:rerun-if-changed={}", root.display());
}
