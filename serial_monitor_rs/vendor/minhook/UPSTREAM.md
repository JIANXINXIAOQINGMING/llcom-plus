# MinHook 1.3.4

Source: https://github.com/TsudaKageyu/minhook/tree/v1.3.4

Archive: https://github.com/TsudaKageyu/minhook/archive/refs/tags/v1.3.4.zip

Archive SHA-256: `172708123DAA0C98D20D3A980B16A50BE14AF243DC95DEE6F79C24193AD010E4`

Only `include/`, `src/` and `LICENSE.txt` are vendored. `src/hook.c` and
`include/MinHook.h` have a local strict-thread patch: freeze rejects an
inaccessible or unsuspendable enumerated thread, captures every original
context before EIP migration, restores prior migrations on context failure,
and resumes via retained thread handles before returning an error. No
entry-point patch is attempted after a failed freeze. Threads created during
the snapshot/patch interval remain a general user-mode instrumentation limit.
The x86 capture hook links this code statically; x64 retains its
existing pinned retour implementation. MinHook provides instruction decoding,
trampoline relocation and suspended-thread instruction-pointer adjustment.
Protected processes or inaccessible threads remain a Windows permission and
compatibility limitation; this is not a promise that arbitrary targets can be
instrumented safely or successfully.

The license text includes MinHook and the HDE decoder notices. Keep it with
redistributed source and binary distributions.
