/* Skill Rank -> server sync (client side, plain HTTP POST).
 *
 * The shipped Android client has no UpgradeCharacterSkillRank DTOs/method, so we
 * hook LivingPowers.RankUp and POST the new rank to the private server's
 *   POST /api/character/skill-rank
 * endpoint (same auth token as gRPC; server does ownership + 1..200 + monotonic
 * validation).  Transport mirrors the leaderboard stub: raw arm64 syscalls, no libc.
 *
 * Hooked: LivingPowers.RankUp(LivingPowers*, int powerHashSafe)  0x2C6C63C
 * Reads:  LivingPowers.GetTrainedRank(LivingPowers*, int)        0x2C6EA0C
 * Auth:   NetSession.Current.Session.AuthToken
 * CharId: NetClient.CurrentCharacterId
 *
 * Report rule: only when after > before && after > 1 (0->1 is created server-side
 * and NewRank <= 1 is rejected).
 *
 * Addresses are linked libil2cpp 1.9.3 arm64 VAs (ASLR-safe).
 */
typedef void *ptr;
typedef unsigned long u64;
typedef unsigned int u32;
typedef int i32;
typedef unsigned short u16;
typedef unsigned char u8;
typedef struct { u64 a, b; } Guid;

extern ptr  il2cpp_domain_get(void);
extern ptr  il2cpp_domain_get_assemblies(ptr, u64 *);
extern ptr  il2cpp_assembly_get_image(ptr);
extern ptr  il2cpp_class_from_name(ptr, const char *, const char *);
extern ptr  il2cpp_class_get_method_from_name(ptr, const char *, int);
extern ptr  il2cpp_runtime_invoke(ptr, ptr, ptr *, ptr *);
extern ptr  il2cpp_value_box(ptr, ptr);

/* game / net (fixed VAs, linked) */
extern int   get_trained_rank(ptr self, int hash, ptr mi);          /* 0x2C6EA0C */
extern ptr   netclient_get_current(ptr mi);                         /* 0x2DA1E28 */
extern Guid  netclient_current_character_id(ptr self, ptr mi);      /* 0x2DA1E90 */
extern ptr   netsession_get_current(ptr mi);                         /* 0x2E42DAC */
extern ptr   netsession_get_session(ptr self, ptr mi);              /* 0x2E42E04 */
extern ptr   sessiondto_get_authtoken(ptr self, ptr mi);            /* 0x0279C8B8 */

/* ---- raw syscalls (no libc, no PLT) ---------------------------------- */
#define SYS_close 57
#define SYS_socket 198
#define SYS_connect 203
#define SYS_sendto 206
#define SYS_recvfrom 207
#define SYS_setsockopt 208

static long sc(long n, long a, long b, long c, long d, long e, long f)
{
    register long r8 __asm__("x8") = n;
    register long r0 __asm__("x0") = a;
    register long r1 __asm__("x1") = b;
    register long r2 __asm__("x2") = c;
    register long r3 __asm__("x3") = d;
    register long r4 __asm__("x4") = e;
    register long r5 __asm__("x5") = f;
    __asm__ volatile("svc #0" : "+r"(r0) : "r"(r8), "r"(r1), "r"(r2), "r"(r3), "r"(r4), "r"(r5) : "memory");
    return r0;
}

/* Private server, plain HTTP/1.0 JSON feed (same as the leaderboard stub). */
#define SRV_IP_WORD 0x88328C03u /* 3.140.50.136 network byte order */
#define SRV_PORT 8081

volatile long g_dbg[8];   /* [0] before [1] after [2] http rc [3] status
                           * [4] fd [5] connect rc [6] token len [7] sent */

static char g_token[512];
static char g_guid[40];

static ptr g_guid_class, g_guid_tostring;
static ptr g_nc_class, g_ns_class, g_nc_current_mi, g_ns_current_mi;
static ptr g_rankup_self;
static int g_rankup_hash, g_rankup_before, g_ready, g_error, g_sent;

static int str_copy_il2cpp(char *out, int cap, ptr s)
{
    if (!s) return 0;
    i32 n = *(i32 *)((u8 *)s + 0x10);        /* Il2CppString.length */
    u16 *ch = (u16 *)((u8 *)s + 0x14);       /* Il2CppString.chars  */
    int j = 0;
    for (i32 i = 0; i < n && j < cap - 1; i++) out[j++] = (char)(ch[i] & 0xff);
    out[j] = 0;
    return j;
}

static ptr find_class(const char *ns, const char *name)
{
    u64 n = 0;
    ptr *a = il2cpp_domain_get_assemblies(il2cpp_domain_get(), &n);
    for (u64 i = 0; i < n; i++) {
        ptr k = il2cpp_class_from_name(il2cpp_assembly_get_image(a[i]), ns, name);
        if (k) return k;
    }
    return 0;
}

static void refresh_auth(void)
{
    if (!g_ns_class) g_ns_class = find_class("Nordicandia.Client.Net", "NetSession");
    if (g_ns_class && !g_ns_current_mi)
        g_ns_current_mi = il2cpp_class_get_method_from_name(g_ns_class, "get_Current", 0);
    ptr ns = g_ns_current_mi ? netsession_get_current(g_ns_current_mi) : 0;
    if (ns) {
        ptr sd = netsession_get_session(ns, 0);
        if (sd) str_copy_il2cpp(g_token, sizeof(g_token), sessiondto_get_authtoken(sd, 0));
    }
    g_dbg[6] = g_token[0] ? 1 : 0;
}

/* .NET Guid layout: _a(4 LE) _b(2 LE) _c(2 LE) _d.._k(8).  Print it the way
 * Guid.ToString("D") does so the server's Guid.Parse accepts it. */
static void guid_to_str(const Guid *g, char *out)
{
    static const char H[] = "0123456789abcdef";
    const u8 *b = (const u8 *)g;
    int n = 0;
    for (int i = 3; i >= 0; i--) { out[n++] = H[b[i] >> 4]; out[n++] = H[b[i] & 15]; }
    out[n++] = '-';
    for (int i = 5; i >= 4; i--) { out[n++] = H[b[i] >> 4]; out[n++] = H[b[i] & 15]; }
    out[n++] = '-';
    for (int i = 7; i >= 6; i--) { out[n++] = H[b[i] >> 4]; out[n++] = H[b[i] & 15]; }
    out[n++] = '-';
    for (int i = 8; i < 10; i++) { out[n++] = H[b[i] >> 4]; out[n++] = H[b[i] & 15]; }
    out[n++] = '-';
    for (int i = 10; i < 16; i++) { out[n++] = H[b[i] >> 4]; out[n++] = H[b[i] & 15]; }
    out[n] = 0;
}

static void refresh_guid(void)
{
    Guid cid;
    if (!g_nc_class) g_nc_class = find_class("Client.Net", "NetClient");
    if (g_nc_class && !g_nc_current_mi)
        g_nc_current_mi = il2cpp_class_get_method_from_name(g_nc_class, "get_Current", 0);
    if (!g_nc_current_mi) { g_guid[0] = 0; return; }
    cid = netclient_current_character_id(netclient_get_current(g_nc_current_mi), 0);
    guid_to_str(&cid, g_guid);
}

/* Blocking HTTP/1.0 POST; returns body length (0 on failure). */
static int http_post(const char *path, const char *body, int blen)
{
    struct { unsigned short family; unsigned short port; unsigned int addr; char pad[8]; } sa;
    long tv[2];
    int fd, got, total = 0;
    char req[1200];
    int rl = 0;
    char resp[512];
    char num[24];

    fd = (int)sc(SYS_socket, 2, 1, 0, 0, 0, 0);
    g_dbg[4] = fd;
    if (fd < 0) return 0;
    tv[0] = 2; tv[1] = 0;
    sc(SYS_setsockopt, fd, 1, 20, (long)tv, sizeof(tv), 0);
    sc(SYS_setsockopt, fd, 1, 21, (long)tv, sizeof(tv), 0);

    sa.family = 2;
    sa.port = (unsigned short)(((SRV_PORT & 0xff) << 8) | (SRV_PORT >> 8));
    sa.addr = SRV_IP_WORD;
    for (int i = 0; i < 8; i++) sa.pad[i] = 0;
    { long rc = sc(SYS_connect, fd, (long)&sa, 16, 0, 0, 0); g_dbg[5] = rc;
      if (rc != 0) { sc(SYS_close, fd, 0, 0, 0, 0, 0); return 0; } }

    #define CAT(s) do { const char *_s = (s); while (*_s && rl < (int)sizeof(req) - 1) req[rl++] = *_s++; } while (0)
    CAT("POST "); CAT(path); CAT(" HTTP/1.0\r\nHost: nordicandia\r\n");
    CAT("authorization: "); CAT(g_token); CAT("\r\n");
    CAT("Content-Type: application/json\r\n");
    { int v = blen, d = 0; char tmp[12]; if (v == 0) tmp[d++] = '0';
      while (v > 0) { tmp[d++] = (char)('0' + v % 10); v /= 10; }
      CAT("Content-Length: "); for (int i = d - 1; i >= 0; i--) req[rl++] = tmp[i]; CAT("\r\n"); }
    CAT("Connection: close\r\n\r\n");
    CAT(body);
    if (sc(SYS_sendto, fd, (long)req, rl, 0, 0, 0) <= 0) { sc(SYS_close, fd, 0, 0, 0, 0, 0); return 0; }

    while (total < (int)sizeof(resp) - 1 &&
           (got = (int)sc(SYS_recvfrom, fd, (long)(resp + total), sizeof(resp) - 1 - total, 0, 0, 0)) > 0)
        total += got;
    sc(SYS_close, fd, 0, 0, 0, 0, 0);
    resp[total] = 0;
    /* parse "HTTP/1.x NNN" */
    g_dbg[3] = 0;
    { int i = 0; while (i < total && resp[i] != ' ') i++; if (i < total) {
        int code = 0; i++; while (i < total && resp[i] >= '0' && resp[i] <= '9') { code = code * 10 + (resp[i] - '0'); i++; }
        g_dbg[3] = code; } }
    (void)num;
    return total;
}

static int report(int hash, int rank)
{
    char body[256];
    int n = 0;
    refresh_auth();
    refresh_guid();
    if (!g_token[0] || !g_guid[0]) { g_error = 20; return 0; }
    #define BCAT(s) do { const char *_s = (s); while (*_s && n < (int)sizeof(body) - 1) body[n++] = *_s++; } while (0)
    BCAT("{\"characterId\":\""); BCAT(g_guid);
    BCAT("\",\"powerHashSafe\":"); { int v = hash, d = 0; char t[12];
        if (v == 0) t[d++] = '0'; while (v > 0) { t[d++] = (char)('0' + v % 10); v /= 10; }
        for (int i = d - 1; i >= 0; i--) body[n++] = t[i]; }
    BCAT(",\"newRank\":"); { int v = rank, d = 0; char t[12];
        if (v == 0) t[d++] = '0'; while (v > 0) { t[d++] = (char)('0' + v % 10); v /= 10; }
        for (int i = d - 1; i >= 0; i--) body[n++] = t[i]; }
    BCAT("}");
    body[n] = 0;
    http_post("/api/character/skill-rank", body, n);
    g_sent++;
    return 1;
}

void rankup_enter(ptr self, int hash)
{
    g_rankup_self = self;
    g_rankup_hash = hash;
    g_rankup_before = get_trained_rank(self, hash, 0);
    g_dbg[0] = g_rankup_before;
}
void rankup_leave(void)
{
    int after = get_trained_rank(g_rankup_self, g_rankup_hash, 0);
    g_dbg[1] = after;
    if (after > g_rankup_before && after > 1) report(g_rankup_hash, after);
}

/* RankUp entry: capture before, run the original body, then report after. */
__attribute__((naked)) void rankup_trampoline(void) {
    __asm__ volatile(
        "stp x0,x1,[sp,#-0x20]!\n"
        "str x30,[sp,#0x10]\n"
        "bl rankup_enter\n"
        "ldr x30,[sp,#0x10]\n"
        "ldp x0,x1,[sp],#0x20\n"
        "bl rankup_body_impl\n"
        "stp x0,x30,[sp,#-0x10]!\n"
        "bl rankup_leave\n"
        "ldp x0,x30,[sp],#0x10\n"
        "ret\n");
}
/* rankup_body_impl: reproduce 0x2C6C63C (sub sp,sp,#0x90) then resume. */
__attribute__((naked)) void rankup_body_impl(void) {
    __asm__ volatile("sub sp,sp,#0x90\nb RANKUP_RESUME\n");
}

int  skill_rank_stub_ready(void) { return g_ready; }
int  skill_rank_stub_error(void) { return g_error; }
int  skill_rank_stub_sent(void)  { return g_sent; }
/* Test entry for Frida: report a rank for the current character. */
void skill_rank_test(int hash, int rank) { g_ready = 1; report(hash, rank); }