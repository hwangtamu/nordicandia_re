# Email/password login native stub

Goal: let users sign in with an email/password so the same account works on any
device, without the Google Play Games signature requirement.

## What is proven
- The client APIs exist and their call sites confirm the signatures:
  - `NetClient.SignInWithEmail(this, email, password)` — 0x02DA2584
  - `NetClient.RegisterGameAccount(this, email, password, repeat)` — 0x02DA46E0
  - `NetClient.get_Current()` — 0x02DA1E28
  - `UIWindowManager.ShowSingleInputDialogOkCancel(this,msg,initial,a,b,onOk,onCancel,onValidate)` — 0x0279075C
- `System.Action<T>` layout (verified live):
  `+0x10 method_ptr, +0x18 invoke_impl, +0x20 target, +0x28 MethodInfo, +0x38 interp, +0x40 self`.
- `Action<T>.Invoke` (shared generic, 0x03DF1668) does
  `x0=[+0x40]; x2=[+0x28]; x3=[+0x18]; br x3`.
- A delegate whose `+0x10`/`+0x18` point at a native function **was constructed and
  successfully invoked** (native callback fired).

## Files
- `email_login_stub.c` — the stub (flows + callbacks).
- `build_stub.sh` — compile & link to raw bytes for injection at 0x344EC24.

## Open items before it ships
1. Toolchain: this Mac has no `ld.lld`; either `brew install llvm` or add a small
   Python relocator (`R_AARCH64_{CALL26,JUMP26,ADR_PREL_PG_HI21,ADD_ABS_LO12_NC,
   LDST64_ABS_LO12_NC,LDST32_ABS_LO12_NC}`).
2. Writable state: `.text` is RX, so keep persistent state in a block obtained
   from `il2cpp_alloc` and referenced through the fake delegate's `target`
   field (survives between dialog callbacks), instead of C globals in `.bss`.
3. Seed `g_realAction`: the fake delegate is cloned from a real
   `System.Action<string>`; obtain one by instantiating `System.Action<string>`
   via `il2cpp_class_from_name` or by capturing the game's own (e.g. the
   delete-confirm dialog's callback).
4. UI wiring: patch `WindowSelectGameMode.RefreshSignInButton` to `ret` (keep the
   button) and `OnSignInClicked` (0x026340E0) to `b email_login_entry`; add a
   register entry as a second button.
