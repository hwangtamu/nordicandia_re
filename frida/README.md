# Frida (no-root) toolkit for Nordicandia device-login forcing

## How the no-root hook was achieved (VERIFIED WORKING)
1. patchelf --add-needed libfrida-gadget.so libil2cpp.so   (DT_NEEDED -> linker loads gadget)
2. Package into config.arm64_v8a.apk:
   - lib/arm64-v8a/libil2cpp.so             (patchelf'd)
   - lib/arm64-v8a/libfrida-gadget.so       (frida-gadget-17.18.0-android-arm64.so)
   - lib/arm64-v8a/libfrida-gadget.config.so (JSON config)
3. Re-sign (patched_arm64/nordicandia.keystore, alias nord, pass nordpass); install-multiple.
4. adb forward tcp:27042 tcp:27042 ; gadget logs "Frida: Listening on 127.0.0.1 TCP port 27042".
5. Python: frida.get_device_manager().add_remote_device("127.0.0.1:27042").attach("Gadget")
   Confirmed: module base resolves, Interceptor.attach works, console.log messages arrive.

## arm64 addresses (base + offset), from arm64/out/metadata_arm64.json
- NetClient.get_Current        0x2DA1E28
- NetClient.Initialize         0x2DA1F0C
- NetClient.SignInWithDevice   0x2DA21B8
- NetClient..cctor (STATIC)    0x2DAEA68  <-- creates Current=new NetClient(), always writes static_fields[0]
- NetClient..ctor              0x2DAE914
- NetClient.Destroy            0x2DAE83C
- LoginStateMachineNew.Login   0x26F2434  (instance; w1=LoginAccountTypes)
- UnityGame.SignInNew          0x2701390  (static; w0=LoginAccountTypes)
- LoadGame.SignInNew           0x25B7918 ; state machine MoveNext=0x25BFD80
     calls Login @0x25BFFEC with w1=type ; static patch site 0x25BFFD4 (mov w1,wzr)
- Login impl MoveNext (calls SignInWithDevice): reads Current @0x26F63C8, null-check @0x26F63D0,
     bl @0x26F63DC, error branch @0x26F6708
- "Client not initialized" literal @0x0589F5B0
- il2cpp_raise_exception @0x2318168 ; __cxa_throw @0x23FD894

## Static patches that force Device(1) (validated on-device)
- 0x027013C4: f503002a (mov w21,w0) -> 35008052 (mov w21,#1)  [UnityGame.SignInNew]
- 0x025BFFD4: e1031f2a (mov w1,wzr) -> 21008052 (mov w1,#1)   [LoadGame+<SignInNew>d__33.MoveNext]
File offset = vaddr - 0x4000 (LOAD base). patchelf changes offsets: map via ELF PHDRs.

## Current blocker
Forcing Device reaches the login state machine, which reads NetClient.Current == NULL
(0x26F63D0 -> error 0x26F6708 -> "Client not initialized"). Since ..cctor always sets Current,
NULL implies ..cctor threw (likely resolving the server endpoint/config).
Next: hook il2cpp_raise_exception during ..cctor to capture the managed exception message,
or (on the main thread) call ..ctor + Initialize, then force Login type=1.

## Gadget config notes
- {"interaction":{"type":"listen","address":"127.0.0.1","port":27042,"on_load":"wait"|"resume"}}
- on_load:"wait" pauses at load; attach+load script to resume (flaky crashes observed).
- Inline "code":"<js>" did NOT execute (no log file). Verify key name against Frida docs.
