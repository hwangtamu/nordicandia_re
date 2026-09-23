/*
 * email_login_stub.c — AArch64 native stub for the Nordicandia Android client.
 *
 * Injected into the libil2cpp code cave at 0x344EC24 (inside the unused
 * BestHTTP.Examples.TestHubSample.Hub_OnConnected, 5928 bytes available).
 *
 * The stub drives the client's own single-input dialog
 * (UIWindowManager.ShowSingleInputDialogOkCancel) to collect email/password and
 * calls the client's own login APIs:
 *
 *   login:    email -> password -> NetClient.SignInWithEmail(current, email, pw)
 *   register: email -> password -> repeat -> NetClient.RegisterGameAccount(current, email, pw, repeat)
 *
 * All calls are PC-relative to other functions inside the same shared object, so
 * the raw code is position independent (works at any ASLR base) as long as it is
 * linked at its real virtual address 0x344EC24.
 *
 * Build with device/stub/build_stub.sh.
 */

typedef unsigned long u64;
typedef unsigned int  u32;
typedef unsigned char u8;
typedef void*         ptr;

/* ---- client functions (absolute VAs; resolved with --defsym) ---------- */
extern ptr il2cpp_string_new(const char* utf8);
extern ptr NetClient_get_Current(void);
extern void UIWindowManager_ShowSingleInputDialogOkCancel(
        ptr self, ptr msg, ptr initial, ptr a, ptr b, ptr onOk, ptr onCancel, ptr onValidate);
extern ptr NetClient_SignInWithEmail(ptr self, ptr email, ptr pw);
extern ptr NetClient_RegisterGameAccount(ptr self, ptr email, ptr pw, ptr repeat);

/* ---- state ------------------------------------------------------------ */
#define STAGE_LOGIN_EMAIL    1
#define STAGE_LOGIN_PW       2
#define STAGE_REG_EMAIL      3
#define STAGE_REG_PW         4
#define STAGE_REG_REPEAT     5

ptr g_realAction;   /* a real System.Action<string> to clone (captured) */
ptr g_windowMgr;    /* UIWindowManager instance                        */
ptr g_email;
ptr g_pw;
int g_stage;

/* ---- helpers ---------------------------------------------------------- */
static void memcpy8(ptr dst, ptr src, u64 n) {
    u8* d = (u8*)dst; u8* s = (u8*)src;
    for (u64 i = 0; i < n; i++) d[i] = s[i];
}

/*
 * Clone g_realAction and point its callback at `cb`.
 * Action<T> layout (verified at runtime):
 *   +0x00 klass  +0x08 monitor  +0x10 method_ptr  +0x18 invoke_impl
 *   +0x20 target +0x28 MethodInfo +0x38 interp   +0x40 self
 * Action<T>.Invoke does: x0=[+0x40], x2=[+0x28], x3=[+0x18]; br x3.
 * So we set +0x10 and +0x18 to `cb` (then Invoke tail-calls cb(delegate, arg, method)).
 * The fake object is a static buffer (Boehm GC is non-moving and never scans it
 * as long as it is only reachable from managed code transiently).
 */
static u8 g_fake[0x48] __attribute__((aligned(16)));

static ptr make_delegate(void (*cb)(ptr, ptr, ptr)) {
    if (!g_realAction) return 0;
    memcpy8(g_fake, g_realAction, 0x48);
    *(ptr*)(g_fake + 0x10) = (ptr)cb;
    *(ptr*)(g_fake + 0x18) = (ptr)cb;
    *(ptr*)(g_fake + 0x20) = 0;
    *(ptr*)(g_fake + 0x40) = (ptr)g_fake;
    return (ptr)g_fake;
}

static void show(const char* title, void (*cb)(ptr, ptr, ptr)) {
    ptr dlg = make_delegate(cb);
    UIWindowManager_ShowSingleInputDialogOkCancel(
        g_windowMgr,
        il2cpp_string_new(title),
        il2cpp_string_new(""), 0, 0,
        dlg, 0, 0);
}

/* ---- callbacks -------------------------------------------------------- */
static void on_input(ptr self, ptr arg, ptr method) {
    (void)self; (void)method;
    switch (g_stage) {
    case STAGE_LOGIN_EMAIL: g_email = arg; g_stage = STAGE_LOGIN_PW;
        show("Password", on_input); break;
    case STAGE_LOGIN_PW:    g_pw = arg;
        NetClient_SignInWithEmail(NetClient_get_Current(), g_email, g_pw); break;

    case STAGE_REG_EMAIL:   g_email = arg; g_stage = STAGE_REG_PW;
        show("Password", on_input); break;
    case STAGE_REG_PW:      g_pw = arg; g_stage = STAGE_REG_REPEAT;
        show("Repeat password", on_input); break;
    case STAGE_REG_REPEAT:
        NetClient_RegisterGameAccount(NetClient_get_Current(), g_email, g_pw, arg); break;
    }
}

/* ---- entry points ----------------------------------------------------- */
/* Called (branch patch) from WindowSelectGameMode.OnSignInClicked. */
void email_login_entry(ptr self) {
    (void)self;                       /* real UIWindowManager comes from the capture trampoline */
    g_stage = STAGE_LOGIN_EMAIL;
    show("Email", on_input);
}

/* A second entry for a Register button. */
void email_register_entry(ptr self) {
    (void)self;
    g_stage = STAGE_REG_EMAIL;
    show("Email (register)", on_input);
}

/*
 * Capture hook: call once from any place that hands an Action<string> to a
 * dialog, to seed g_realAction. (Wired by a separate small patch, or set from
 * a Frida script while developing.)
 */
void email_stub_capture(ptr action) { g_realAction = action; }
/* ------------------------------------------------------------------ *
 * Capture trampoline (step 3):  patch ShowSingleInputDialogOkCancel's
 * first instruction (sub sp,sp,#0x70) with `b email_capture_trampoline`.
 *
 * The trampoline seeds g_realAction with the first real System.Action<string>
 * the game passes to the dialog, then re-executes the displaced instruction and
 * resumes the original function at 0x02790764.
 *
 * Register use: only x9/x10 (volatile) are touched, and they are saved/restored
 * so the original function sees the entry register state it expects.
 * ------------------------------------------------------------------ */
__attribute__((naked)) void email_capture_trampoline(void) {
    __asm__ volatile(
        "stp x9, x10, [sp, #-16]!\n"
        "adrp x9, g_realAction\n"
        "add  x9, x9, :lo12:g_realAction\n"
        "ldr  x10, [x9]\n"
        "cbnz x10, 1f\n"
        "str  x5, [x9]\n"
        "1:\n"
        "adrp x9, g_windowMgr\n"
        "add  x9, x9, :lo12:g_windowMgr\n"
        "ldr  x10, [x9]\n"
        "cbnz x10, 2f\n"
        "str  x0, [x9]\n"
        "2:\n"
        "ldp x9, x10, [sp], #16\n"
        "sub sp, sp, #0x70\n"          /* the displaced original instruction */
        "adrp x9, SHOWDLG_RESUME\n"
        "add  x9, x9, :lo12:SHOWDLG_RESUME\n"
        "br   x9\n"
    );
}

/* Seed g_windowMgr from UIWindowManager.Update (called every frame, x0 = instance).
 * Displaces `sub sp,sp,#0xb0` and resumes at 0x0278D73C. Cheap: one load + test. */
__attribute__((naked)) void email_capture_wm_trampoline(void) {
    __asm__ volatile(
        "stp x9, x10, [sp, #-16]!\n"
        "adrp x9, g_windowMgr\n"
        "add  x9, x9, :lo12:g_windowMgr\n"
        "ldr  x10, [x9]\n"
        "cbnz x10, 1f\n"
        "str  x0, [x9]\n"
        "1:\n"
        "ldp x9, x10, [sp], #16\n"
        "sub sp, sp, #0xb0\n"
        "adrp x9, WMGR_RESUME\n"
        "add  x9, x9, :lo12:WMGR_RESUME\n"
        "br   x9\n"
    );
}
