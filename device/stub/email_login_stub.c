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
 *   login:    email -> password -> UnityGame.SignInNew(2, 1, 0, email, pw)
 *             (createAccountIfPossible=1: new email registers via GotoCreateAccountFlow)
 *   register: email -> password -> repeat -> UnityGame.SignInNew(2, 1, 0, email, pw)
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
/* The official entry point used by WindowLogin.OnLoginClicked:
 *   UnityGame.SignInNew(LoginAccountTypes type, bool createAccountIfPossible,
 *                       bool isSilentLogin, string email, string password)
 * WindowLogin passes type = 2 (username/email) with both bools false, which drives
 * LoginStateMachineNew -> GotoUsernameFlow -> LoginWithUsername --LoggedInOnline-->
 * FetchAdditionalData -> Finished. That path yields an *online* PlayerAccount
 * (AccountType in {1,2,3}) and never touches SynchronizeProfileStateMachine. */
extern void UnityGame_SignInNew(int acctType, int createIfPossible, int silent, ptr email, ptr pw);
extern ptr il2cpp_class_get_method_from_name(ptr klass, const char* name, int argc);
extern ptr il2cpp_method_get_param(ptr method, unsigned int index);
extern ptr il2cpp_class_from_type(ptr type);
extern ptr il2cpp_object_new(ptr klass);

/* ---- state ------------------------------------------------------------ */
#define STAGE_LOGIN_EMAIL    1
#define STAGE_LOGIN_PW       2
#define STAGE_REG_EMAIL      3
#define STAGE_REG_PW         4
#define STAGE_REG_REPEAT     5

volatile long g_dbg[8];  /* diagnostics: make_delegate step results */
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

/* Build a System.Action<string> at runtime, without a template instance:
 * UIWindowManager klass -> ShowSingleInputDialogOkCancel MethodInfo ->
 * parameter 4 (Action<string>) type -> klass -> il2cpp_object_new.
 *
 * ShowSingleInputDialogOkCancel has TEN parameters:
 *   (string, string, string, string, Action<string>, Action,
 *    Action<WindowDialogSingleInputOkCancel,string>, bool, bool, int)
 * so the lookup must use argc = 10; 7 silently yields a null MethodInfo and the
 * dialog then invokes a null callback. */
static ptr make_delegate(void (*cb)(ptr, ptr, ptr)) {
    ptr klass, method, type, aklass, obj;
    g_dbg[0] = (long)(u64)g_windowMgr;
    if (!g_windowMgr) return 0;
    klass = *(ptr*)g_windowMgr;
    g_dbg[1] = (long)(u64)klass;
    if (!klass) return 0;
    method = il2cpp_class_get_method_from_name(klass, "ShowSingleInputDialogOkCancel", 10);
    g_dbg[2] = (long)(u64)method;
    if (!method) return 0;
    type = il2cpp_method_get_param(method, 4);
    g_dbg[3] = (long)(u64)type;
    if (!type) return 0;
    aklass = il2cpp_class_from_type(type);
    g_dbg[4] = (long)(u64)aklass;
    if (!aklass) return 0;
    obj = il2cpp_object_new(aklass);
    g_dbg[5] = (long)(u64)obj;
    if (!obj) return 0;
    *(ptr*)((u8*)obj + 0x10) = (ptr)cb;   /* method_ptr  */
    *(ptr*)((u8*)obj + 0x18) = (ptr)cb;   /* invoke_impl */
    *(ptr*)((u8*)obj + 0x20) = 0;         /* target      */
    *(ptr*)((u8*)obj + 0x28) = 0;         /* method      */
    *(ptr*)((u8*)obj + 0x40) = obj;       /* self        */
    g_realAction = obj;                   /* keep a ref for debugging */
    return obj;
}

static void show(const char* title, void (*cb)(ptr, ptr, ptr)) {
    ptr dlg, msg;
    g_dbg[6] = 0xA1;                       /* show() entered */
    dlg = make_delegate(cb);
    g_dbg[6] = 0xA2;                       /* delegate built */
    msg = il2cpp_string_new(title);
    g_dbg[4] = (long)(u64)msg;             /* il2cpp_string_new result */
    g_dbg[6] = 0xA3;                       /* string built */
    UIWindowManager_ShowSingleInputDialogOkCancel(
        g_windowMgr,
        msg,
        il2cpp_string_new(""), 0, 0,
        dlg, 0, 0);
    g_dbg[6] = 0xA4;                       /* dialog call returned */
}

/* ---- callbacks -------------------------------------------------------- */
static void on_input(ptr self, ptr arg, ptr method) {
    (void)self; (void)method;
    switch (g_stage) {
    case STAGE_LOGIN_EMAIL: g_email = arg; g_stage = STAGE_LOGIN_PW;
        show("Password", on_input); break;
    case STAGE_LOGIN_PW:    g_pw = arg;
        UnityGame_SignInNew(2, 1, 0, g_email, g_pw); break;

    case STAGE_REG_EMAIL:   g_email = arg; g_stage = STAGE_REG_PW;
        show("Password", on_input); break;
    case STAGE_REG_PW:      g_pw = arg; g_stage = STAGE_REG_REPEAT;
        show("Repeat password", on_input); break;
    case STAGE_REG_REPEAT:
        /* createAccountIfPossible = 1 -> LoginStateMachineNew drives
         * GotoCreateAccountFlow -> RegisterGameAccount when the login is rejected. */
        UnityGame_SignInNew(2, 1, 0, g_email, g_pw); break;
    }
}

/* ---- entry points ----------------------------------------------------- */
/* Called (branch patch) from WindowSelectGameMode.OnSignInClicked. */
void email_login_entry(ptr self) {
    (void)self;                       /* real UIWindowManager comes from the capture trampoline */
    g_dbg[7] = 0x1111;                /* stub entered */
    g_stage = STAGE_LOGIN_EMAIL;
    g_dbg[7] = 0x2222;                /* g_stage written */
    show("Email", on_input);
    g_dbg[7] = 0x3333;                /* show() returned */
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
