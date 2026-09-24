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
/* (this, title, message, initialText, placeholder, Action<string> onOk,
 *  Action onCancel, Action<WindowDialogSingleInputOkCancel,string> onValidate,
 *  bool, bool, int) — ten parameters after `this`. */
extern void UIWindowManager_ShowSingleInputDialogOkCancel(
        ptr self, ptr title, ptr message, ptr initial, ptr placeholder,
        ptr onOk, ptr onCancel, ptr onValidate, int a, int b, int c);
/* The official entry point used by WindowLogin.OnLoginClicked:
 *   UnityGame.SignInNew(LoginAccountTypes type, bool createAccountIfPossible,
 *                       bool isSilentLogin, string email, string password)
 * WindowLogin passes type = 2 (username/email) with both bools false, which drives
 * LoginStateMachineNew -> GotoUsernameFlow -> LoginWithUsername --LoggedInOnline-->
 * FetchAdditionalData -> Finished. That path yields an *online* PlayerAccount
 * (AccountType in {1,2,3}) and never touches SynchronizeProfileStateMachine. */
extern void UnityGame_SignInNew(int acctType, int createIfPossible, int silent, ptr email, ptr pw);
/* LoginStateMachineNew.Initialize() resets/re-creates the login state machine. The
 * machine is single-shot: once it has reached Finished, calling Login() again is a
 * silent no-op, which is why an automatic sign-in has to reset it first (the UI path
 * gets this for free because WindowSelectGameMode re-opens the machine). */
extern ptr NetClient_get_Current(void);
/* Explicit server-side email login. SignInNew alone short-circuits onto the existing
 * startup session (the machine fires LoggedInOnline without any network call), which
 * leaves the realtime socket's first await parked forever. This call establishes the
 * session/token the socket needs. */
extern ptr NetClient_SignInWithEmail(ptr self, ptr email, ptr pw);
extern ptr il2cpp_class_get_method_from_name(ptr klass, const char* name, int argc);
extern ptr il2cpp_method_get_param(ptr method, unsigned int index);
extern ptr il2cpp_class_from_type(ptr type);
extern ptr il2cpp_object_new(ptr klass);

/* ---- credential file --------------------------------------------------
 * The dialog path proved unusable (ShowSingleInputDialogOkCancel depends on UI
 * state that is not present on the game-mode screen), so credentials are read from
 * a file and handed straight to the official LoginStateMachineNew entry point.
 * Tried in order; the first readable file wins:
 *   1. app external files dir (reachable from a PC over USB)
 *   2. /data/local/tmp (adb push, handy for testing)
 * Format: first line = email, second line = password.
 * ---------------------------------------------------------------------- */
#define SYS_close   57
#define SYS_openat  56
#define SYS_read    63
#define AT_FDCWD    (-100)
#define O_RDONLY    0

static long sc2(long n, long a, long b, long c, long d, long e)
{
    register long r8 __asm__("x8") = n;
    register long r0 __asm__("x0") = a;
    register long r1 __asm__("x1") = b;
    register long r2 __asm__("x2") = c;
    register long r3 __asm__("x3") = d;
    register long r4 __asm__("x4") = e;
    __asm__ volatile("svc #0" : "+r"(r0)
                     : "r"(r8), "r"(r1), "r"(r2), "r"(r3), "r"(r4) : "memory");
    return r0;
}

static const char* const g_paths[] = {
    "/storage/emulated/0/Android/data/com.IterativeStudios.Nordicandia/files/nord-login.txt",
    "/data/local/tmp/nord-login.txt",
};

static int read_credentials(char* buf, int cap, char** email, char** pw)
{
    int i;
    for (i = 0; i < 2; i++) {
        long fd = sc2(SYS_openat, AT_FDCWD, (long)g_paths[i], O_RDONLY, 0, 0);
        long n;
        int j;
        if (fd < 0) continue;
        n = sc2(SYS_read, fd, (long)buf, cap - 1, 0, 0);
        sc2(SYS_close, fd, 0, 0, 0, 0);
        if (n <= 0) continue;
        buf[n] = 0;
        *email = buf;
        *pw = 0;
        for (j = 0; j < (int)n; j++) {
            if (buf[j] == '\r') { buf[j] = 0; continue; }
            if (buf[j] == '\n' && !*pw) { buf[j] = 0; *pw = &buf[j + 1]; }
        }
        if (*pw && **pw) return 1;
    }
    return 0;
}

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
static ptr make_delegate_for(int paramIndex, int invokeArity, void (*cb)(ptr, ptr, ptr)) {
    ptr klass, method, type, aklass, obj;
    g_dbg[0] = (long)(u64)g_windowMgr;
    if (!g_windowMgr) return 0;
    klass = *(ptr*)g_windowMgr;
    g_dbg[1] = (long)(u64)klass;
    if (!klass) return 0;
    method = il2cpp_class_get_method_from_name(klass, "ShowSingleInputDialogOkCancel", 10);
    g_dbg[2] = (long)(u64)method;
    if (!method) return 0;
    type = il2cpp_method_get_param(method, (unsigned)paramIndex);
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
    /* The dialog inspects Delegate.Method, and a null there faults inside it, so
     * point it at the target delegate type's own Invoke MethodInfo. */
    *(ptr*)((u8*)obj + 0x28) = il2cpp_class_get_method_from_name(aklass, "Invoke", invokeArity);
    *(ptr*)((u8*)obj + 0x40) = obj;       /* self        */
    g_realAction = obj;                   /* keep a ref for debugging */
    return obj;
}

static ptr make_delegate(void (*cb)(ptr, ptr, ptr)) { return make_delegate_for(4, 1, cb); }

/* No-op used for the dialog's optional callbacks; the framework always supplies the
 * delegate itself plus its arguments, which are ignored here. */
static void on_noop(ptr self, ptr arg, ptr method) { (void)self; (void)arg; (void)method; }

static void show(const char* title, void (*cb)(ptr, ptr, ptr)) {
    ptr dlg, cancel, validate, msg, empty;
    g_dbg[6] = 0xA1;                       /* show() entered */
    dlg = make_delegate_for(4, 1, cb);     /* Action<string>  (ok)     */
    g_dbg[6] = 0xA2;
    cancel = make_delegate_for(5, 0, on_noop); /* Action      (cancel) */
    validate = make_delegate_for(6, 2, on_noop); /* Action<..,string>   */
    g_dbg[5] = (long)(u64)cancel;
    g_dbg[3] = (long)(u64)validate;
    msg = il2cpp_string_new(title);
    empty = il2cpp_string_new("");
    g_dbg[4] = (long)(u64)msg;
    g_dbg[6] = 0xA3;                       /* strings built */
    UIWindowManager_ShowSingleInputDialogOkCancel(
        g_windowMgr,
        msg,            /* title        */
        empty,          /* message      */
        empty,          /* initial text */
        empty,          /* placeholder  */
        dlg,            /* onOk   Action<string>                   */
        cancel,         /* onCancel Action                         */
        validate,       /* validate Action<WindowDialog..,string>  */
        0, 0, 0);       /* bool, bool, int                         */
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

/* ---- startup login override -------------------------------------------
 * LoginStateMachineNew.Login(LoginAccountTypes, bool createIfPossible, bool silent,
 *                            string a, string b, bool c)
 * is the single funnel every sign-in goes through (startup device login and the UI).
 * Replacing its arguments makes the credential-file email login the default, so the
 * account is online from the first frame - which is what sets PlayerAccount.IsOnline
 * and therefore opens the realtime /ws channel that persists experience.
 * The entry is tail-called (not called), so the first instruction is replicated here
 * and control resumes at the following one.
 * ---------------------------------------------------------------------- */
extern char g_filebuf[256];          /* defined with the credential helpers below */
static int read_credentials(char* buf, int cap, char** email, char** pw);

long g_fa[8];

void login_force_helper(long x0, long x1, long x2, long x3, long x4, long x5, long x6)
{
    char* e = 0;
    char* p = 0;
    g_fa[0] = x0; g_fa[1] = x1; g_fa[2] = x2; g_fa[3] = x3;
    g_fa[4] = x4; g_fa[5] = x5; g_fa[6] = x6;
    if (!read_credentials(g_filebuf, sizeof(g_filebuf), &e, &p)) { g_dbg[7] = 0x8888; return; }
    g_dbg[7] = 0x7777;                 /* startup login overridden to the email flow */
    g_fa[1] = 2;                       /* LoginAccountTypes: username/email -> online */
    g_fa[2] = 1;                       /* createAccountIfPossible */
    g_fa[3] = 0;                       /* isSilentLogin */
    g_fa[4] = (long)(u64)il2cpp_string_new(e);
    g_fa[5] = (long)(u64)il2cpp_string_new(p);
}

__attribute__((naked)) void login_force_trampoline(void)
{
    __asm__ volatile(
        "sub sp, sp, #0x50\n"
        "stp x0, x1, [sp, #0x00]\n"
        "stp x2, x3, [sp, #0x10]\n"
        "stp x4, x5, [sp, #0x20]\n"
        "str x6, [sp, #0x30]\n"
        "str x30, [sp, #0x40]\n"          /* keep the caller's return address */
        "ldp x0, x1, [sp, #0x00]\n"       /* pass the original args through */
        "ldp x2, x3, [sp, #0x10]\n"
        "ldp x4, x5, [sp, #0x20]\n"
        "ldr x6, [sp, #0x30]\n"
        "bl  login_force_helper\n"
        "ldr x30, [sp, #0x40]\n"          /* restore it for the target function */
        "adrp x9, g_fa\n"
        "add  x9, x9, :lo12:g_fa\n"
        "ldp x0, x1, [x9, #0x00]\n"
        "ldp x2, x3, [x9, #0x10]\n"
        "ldp x4, x5, [x9, #0x20]\n"
        "ldr x6, [x9, #0x30]\n"
        "add sp, sp, #0x50\n"
        "sub sp, sp, #0xd0\n"             /* displaced original instruction */
        "b   login_resume\n"
    );
}

/* ---- entry points ----------------------------------------------------- */
/* Called (branch patch) from WindowSelectGameMode.OnSignInClicked. */
char g_filebuf[256];

void email_login_entry(ptr self) {
    char* email = 0;
    char* pw = 0;
    (void)self;                       /* real UIWindowManager comes from the capture trampoline */
    g_dbg[7] = 0x1111;                /* stub entered */
    g_stage = STAGE_LOGIN_EMAIL;
    g_dbg[7] = 0x2222;
    if (read_credentials(g_filebuf, sizeof(g_filebuf), &email, &pw)) {
        g_dbg[7] = 0x4444;            /* credentials loaded from file */
        UnityGame_SignInNew(2, 1, 0, il2cpp_string_new(email), il2cpp_string_new(pw));
        g_dbg[7] = 0x5555;            /* SignInNew returned */
        return;
    }
    g_dbg[7] = 0x6666;                /* no credential file found -> fall back to the dialog */
    show("Email", on_input);
    g_dbg[7] = 0x3333;
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
/* Per-frame hook: once the client has finished booting (the startup device login has
 * initialised NetClient), perform the email sign-in a single time. Doing it here
 * instead of inside LoginStateMachineNew.Login avoids the "Client not initialized"
 * failure that overriding the startup login causes, and it is what flips the account
 * to an online one - opening the realtime /ws channel that persists experience. */
/* Bump on every change. The per-frame hook publishes it into g_dbg[0] so the build
 * actually running on the device can be confirmed before trusting any test result
 * (silently testing a stale lib wasted a full iteration once). */
#define STUB_VERSION 0x0002

static int g_autologin_tries;

/* Credential-file sign-in. It MUST run from the Game Mode window's own callback:
 * LoginStateMachineNew is (re)created with that window, so calling SignInNew from an
 * unrelated context (e.g. the per-frame UIWindowManager update) reaches the method but
 * silently returns without starting the flow. */
void auto_email_signin(void)
{
    char* e = 0;
    char* p = 0;
    if (g_autologin_tries >= 3) return;
    g_autologin_tries++;
    if (!read_credentials(g_filebuf, sizeof(g_filebuf), &e, &p)) { g_dbg[6] = 0x9999; return; }
    g_dbg[6] = 0xA000 + g_autologin_tries;
    {
        ptr es = il2cpp_string_new(e);
        ptr ps = il2cpp_string_new(p);
        NetClient_SignInWithEmail(NetClient_get_Current(), es, ps);   /* fresh session */
        g_dbg[6] = 0xA800 + g_autologin_tries;
        UnityGame_SignInNew(2, 1, 0, es, ps);                         /* account online */
    }
    g_dbg[6] = 0xB000 + g_autologin_tries;
}

/* WindowSelectGameMode.OnEnable: keep the original behaviour and trigger the sign-in
 * inside the window's UI context. Displaces `stp x30, x23, [sp, #-0x30]!`. */
__attribute__((naked)) void game_mode_onenable_trampoline(void)
{
    __asm__ volatile(
        "stp x0, x30, [sp, #-0x10]!\n"
        "bl  auto_email_signin\n"
        "ldp x0, x30, [sp], #0x10\n"
        "stp x30, x23, [sp, #-0x30]!\n"
        "b   GM_ONENABLE_RESUME\n"
    );
}

/* WindowSelectGameMode.Start runs after OnEnable and after the window has wired its
 * buttons - the point where the login machine is actually usable. Displaces
 * `sub sp, sp, #0x40`. */
__attribute__((naked)) void game_mode_start_trampoline(void)
{
    __asm__ volatile(
        "stp x0, x30, [sp, #-0x10]!\n"
        "bl  auto_email_signin\n"
        "ldp x0, x30, [sp], #0x10\n"
        "sub sp, sp, #0x40\n"
        "b   GM_START_RESUME\n"
    );
}

__attribute__((naked)) void email_capture_wm_trampoline(void) {
    __asm__ volatile(
        "stp x9, x10, [sp, #-0x20]!\n"
        "stp x0, x30, [sp, #0x10]\n"
        "adrp x9, g_windowMgr\n"
        "add  x9, x9, :lo12:g_windowMgr\n"
        "ldr  x10, [x9]\n"
        "cbnz x10, 1f\n"
        "str  x0, [x9]\n"
        "1:\n"
        "mov  w10, #0x0002\n"
        "str  w10, [x9, #-0x58]\n"   /* g_dbg[0] = STUB_VERSION */
        "ldp x0, x30, [sp, #0x10]\n"
        "ldp x9, x10, [sp], #0x20\n"
        "sub sp, sp, #0xb0\n"
        "adrp x9, WMGR_RESUME\n"
        "add  x9, x9, :lo12:WMGR_RESUME\n"
        "br   x9\n"
    );
}
