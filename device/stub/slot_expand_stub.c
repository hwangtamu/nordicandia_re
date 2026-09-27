/* Skill/Potion Slot expansion -> server sync (client side, plain HTTP POST).
 *
 * The shipped Android client only has *Offline variants:
 *   SkillGrid.ExpandSkillSlotOffline(SkillSlotType, int cost)      0x25263C8
 *   InventoryGrid.ExpandPotionSlotOffline(int cost)                0x250AA90
 * These update local state without calling the server, so purchased slots
 * vanish on next login (server re-sends old GameModeAccount).
 *
 * We hook both and POST the purchase to the private server's:
 *   POST /api/character/expand-skill-slots  {characterId, expandType, opalCost}
 *   POST /api/character/expand-potion-slots {characterId, opalCost}
 * (same auth token as gRPC; server does ownership + balance validation).
 * Transport mirrors the skill-rank stub: raw arm64 syscalls, no libc.
 *
 * Auth:   NetSession.Current.Session.AuthToken (dynamic via metadata)
 * CharId: NetClient.CurrentCharacterId (dynamic via metadata)
 *
 * Addresses are linked libil2cpp 1.9.3 arm64 VAs (ASLR-safe).
 * Il2CppInspectorRedux VAs are +0x4000; real VA = inspector - 0x4000.
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
extern ptr  il2cpp_object_get_class(ptr);

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

#define SRV_IP_WORD 0x88328C03u /* 3.140.50.136 network byte order */
#define SRV_PORT 8081

static char g_token[512];
static char g_guid[40];

static ptr g_guid_class, g_guid_tostring;
static ptr g_nc_class, g_ns_class, g_nc_current_mi, g_ns_current_mi;
static int g_ready, g_error;

static int str_copy_il2cpp(char *out, int cap, ptr s)
{
    if (!s) return 0;
    i32 n = *(i32 *)((u8 *)s + 0x10);
    u16 *ch = (u16 *)((u8 *)s + 0x14);
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
    /* Dynamic via metadata (same as skill-rank stub v9) */
    if (g_ready) return;
    g_ns_class = find_class("Nordicandia.Client.Net", "NetSession");
    g_nc_class = find_class("Client.Net", "NetClient");
    if (!g_ns_class || !g_nc_class) { g_error = 1; return; }
    g_ns_current_mi = il2cpp_class_get_method_from_name(g_ns_class, "get_Current", 0);
    g_nc_current_mi = il2cpp_class_get_method_from_name(g_nc_class, "get_Current", 0);
    ptr get_session = il2cpp_class_get_method_from_name(g_ns_class, "get_Session", 0);
    ptr get_token = 0, get_charid = 0;
    /* Find get_AuthToken on SessionDto and get_CurrentCharacterId on NetClient */
    {
        u64 n = 0;
        ptr *a = il2cpp_domain_get_assemblies(il2cpp_domain_get(), &n);
        for (u64 i = 0; i < n && (!get_token || !get_charid); i++) {
            ptr img = il2cpp_assembly_get_image(a[i]);
            ptr dto = il2cpp_class_from_name(img, "SharedNet.Dto", "SessionDto");
            if (dto && !get_token) get_token = il2cpp_class_get_method_from_name(dto, "get_AuthToken", 0);
            if (!get_charid) get_charid = il2cpp_class_get_method_from_name(g_nc_class, "get_CurrentCharacterId", 0);
        }
    }
    if (!g_ns_current_mi || !g_nc_current_mi || !get_session || !get_token || !get_charid) { g_error = 2; return; }

    ptr exc = 0;
    ptr ns = il2cpp_runtime_invoke(g_ns_current_mi, 0, 0, &exc);
    if (exc || !ns) { g_error = 3; return; }
    ptr sess = il2cpp_runtime_invoke(get_session, ns, 0, &exc);
    if (exc || !sess) { g_error = 4; return; }
    ptr tok = il2cpp_runtime_invoke(get_token, sess, 0, &exc);
    if (exc || !tok) { g_error = 5; return; }
    if (!str_copy_il2cpp(g_token, sizeof(g_token), tok)) { g_error = 6; return; }

    ptr nc = il2cpp_runtime_invoke(g_nc_current_mi, 0, 0, &exc);
    if (exc || !nc) { g_error = 7; return; }
    /* get_CurrentCharacterId returns Guid (value type, boxed) */
    ptr gid = il2cpp_runtime_invoke(get_charid, nc, 0, &exc);
    if (exc || !gid) { g_error = 8; return; }
    /* Unbox Guid: Il2CppObject + 0x10 is the value */
    Guid g = *(Guid *)((u8 *)gid + 0x10);
    /* Format as hex string */
    static const char *hexd = "0123456789abcdef";
    u8 *b = (u8 *)&g;
    /* Guid layout: Data1(4) Data2(2) Data3(2) Data4(8), need standard format */
    int p = 0;
    /* Simplified: just hex encode raw 16 bytes with dashes at 4-2-2-2-6 */
    int idx[16] = {3,2,1,0, 5,4, 7,6, 8,9, 10,11,12,13,14,15};
    for (int i = 0; i < 16; i++) {
        if (i == 4 || i == 6 || i == 8 || i == 10) g_guid[p++] = '-';
        u8 v = b[idx[i]];
        g_guid[p++] = hexd[v >> 4];
        g_guid[p++] = hexd[v & 15];
    }
    g_guid[p] = 0;
    g_ready = 1;
}

/* Minimal HTTP POST, returns HTTP status code or -1 */
static int http_post(const char *path, const char *json, int json_len)
{
    refresh_auth();
    if (!g_ready) return -1;

    long fd = sc(SYS_socket, 2, 1, 0, 0, 0, 0); /* AF_INET, SOCK_STREAM */
    if (fd < 0) return -1;

    u8 addr[16] = {0};
    *(u16 *)addr = 2; /* AF_INET */
    *(u16 *)(addr + 2) = __builtin_bswap16(SRV_PORT);
    *(u32 *)(addr + 4) = SRV_IP_WORD;
    if (sc(SYS_connect, fd, (long)addr, 16, 0, 0, 0) < 0) { sc(SYS_close, fd, 0,0,0,0,0); return -1; }

    /* Build request */
    static char req[2048];
    int tl = 0; while (g_token[tl]) tl++;
    int pl = 0; while (path[pl]) pl++;
    int n = 0;
    const char *h1 = "POST ";
    while (*h1) req[n++] = *h1++;
    for (int i = 0; i < pl; i++) req[n++] = path[i];
    const char *h2 = " HTTP/1.0\r\nHost: 3.140.50.136:8081\r\nauthorization: ";
    while (*h2) req[n++] = *h2++;
    for (int i = 0; i < tl; i++) req[n++] = g_token[i];
    const char *h3 = "\r\nContent-Type: application/json\r\nContent-Length: ";
    while (*h3) req[n++] = *h3++;
    /* json_len as decimal */
    char cl[16]; int cln = 0;
    int tmp = json_len; if (tmp == 0) cl[cln++] = '0';
    char rev[16]; int rn = 0;
    while (tmp > 0) { rev[rn++] = '0' + tmp % 10; tmp /= 10; }
    while (rn > 0) cl[cln++] = rev[--rn];
    for (int i = 0; i < cln; i++) req[n++] = cl[i];
    const char *h4 = "\r\nConnection: close\r\n\r\n";
    while (*h4) req[n++] = *h4++;
    for (int i = 0; i < json_len; i++) req[n++] = json[i];

    long sent = 0;
    while (sent < n) {
        long r = sc(SYS_sendto, fd, (long)(req + sent), n - sent, 0, 0, 0);
        if (r <= 0) break;
        sent += r;
    }
    /* Read status line */
    static char resp[512];
    long r = sc(SYS_recvfrom, fd, (long)resp, sizeof(resp)-1, 0, 0, 0);
    sc(SYS_close, fd, 0,0,0,0,0);
    if (r <= 0) return -1;
    resp[r] = 0;
    /* Parse "HTTP/1.0 200" */
    int code = -1;
    for (int i = 0; i < r - 11; i++) {
        if (resp[i]=='H' && resp[i+1]=='T' && resp[i+2]=='T' && resp[i+3]=='P') {
            /* Find first space, then digits */
            int j = i;
            while (j < r && resp[j] != ' ') j++;
            j++;
            code = 0;
            while (j < r && resp[j] >= '0' && resp[j] <= '9') { code = code*10 + (resp[j]-'0'); j++; }
            break;
        }
    }
    return code;
}

/* Hook entry for ExpandSkillSlotOffline(SkillGrid*, SkillSlotType, int cost)
 * ARM64: x0=this, w1=type, w2=cost. Called at method entry (original first
 * instruction replaced by branch here). We POST then jump to RESUME.
 * The stub is position-independent; RESUME addr is patched in by the patcher.
 *
 * IMPORTANT: The skipped first instruction is `stp x19, x20, [sp, #64]`.
 * We must save x19/x20 at entry and execute the stp before RESUME,
 * otherwise the function's epilogue will load garbage.
 */
__attribute__((naked)) void hook_expand_skill_slot(void)
{
    __asm__ volatile(
        "stp x29, x30, [sp, #-16]!\n"
        "stp x0, x1, [sp, #-16]!\n"
        "stp x2, x3, [sp, #-16]!\n"
        "mov w0, w1\n"              /* skillSlotType */
        "mov w1, w2\n"              /* cost */
        "bl hook_expand_skill_slot_c\n"
        "ldp x2, x3, [sp], #16\n"
        "ldp x0, x1, [sp], #16\n"
        "ldp x29, x30, [sp], #16\n"
        "str x30, [sp, #-0x50]!\n"  /* execute skipped instruction */
        "b EXPAND_SKILL_SLOT_RESUME\n"  /* PC-relative: ASLR-safe */
    );
}

void hook_expand_skill_slot_c(int skillSlotType, int cost)
{
    /* Build JSON: {"characterId":"...","expandType":N,"opalCost":M} */
    static char json[256];
    int n = 0;
    const char *p1 = "{\"characterId\":\"";
    while (*p1) json[n++] = *p1++;
    for (int i = 0; g_guid[i]; i++) json[n++] = g_guid[i];
    const char *p2 = "\",\"expandType\":";
    while (*p2) json[n++] = *p2++;
    /* expandType decimal */
    char nb[12]; int nn = 0;
    int t = skillSlotType; if (t==0) nb[nn++]='0';
    char rb[12]; int rn=0;
    while (t>0) { rb[rn++]='0'+t%10; t/=10; }
    while (rn>0) nb[nn++]=rb[--rn];
    for (int i=0;i<nn;i++) json[n++]=nb[i];
    const char *p3 = ",\"opalCost\":";
    while (*p3) json[n++] = *p3++;
    nn=0; rn=0; t=cost; if(t==0) nb[nn++]='0';
    while (t>0) { rb[rn++]='0'+t%10; t/=10; }
    while (rn>0) nb[nn++]=rb[--rn];
    for (int i=0;i<nn;i++) json[n++]=nb[i];
    json[n++]='}'; json[n]=0;
    http_post("/api/character/expand-skill-slots", json, n);
}

/* Hook entry for ExpandPotionSlotOffline(InventoryGrid*, int cost)
 * ARM64: x0=this, w1=cost.
 *
 * IMPORTANT: The skipped first instruction is `adrp x21, #13983`
 * (x21 = 0x5BA9000). We must set x21 correctly before RESUME,
 * otherwise the function will use a wrong pointer.
 */
__attribute__((naked)) void hook_expand_potion_slot(void)
{
    __asm__ volatile(
        "stp x29, x30, [sp, #-16]!\n"
        "stp x0, x1, [sp, #-16]!\n"
        "mov w0, w1\n"              /* cost */
        "bl hook_expand_potion_slot_c\n"
        "ldp x0, x1, [sp], #16\n"
        "ldp x29, x30, [sp], #16\n"
        "sub sp, sp, #0x90\n"  /* execute skipped instruction */
        "b EXPAND_POTION_SLOT_RESUME\n"  /* PC-relative: ASLR-safe */
    );
}

void hook_expand_potion_slot_c(int cost)
{
    static char json[256];
    int n = 0;
    const char *p1 = "{\"characterId\":\"";
    while (*p1) json[n++] = *p1++;
    for (int i = 0; g_guid[i]; i++) json[n++] = g_guid[i];
    const char *p2 = "\",\"opalCost\":";
    while (*p2) json[n++] = *p2++;
    char nb[12]; int nn=0; char rb[12]; int rn=0;
    int t=cost; if(t==0) nb[nn++]='0';
    while (t>0) { rb[rn++]='0'+t%10; t/=10; }
    while (rn>0) nb[nn++]=rb[--rn];
    for (int i=0;i<nn;i++) json[n++]=nb[i];
    json[n++]='}'; json[n]=0;
    http_post("/api/character/expand-potion-slots", json, n);
}

/* Hook entry for OfflineCombatPets_Unlock(int petDefinitionIntegerId, bool payWithOpals, int cost, int* newCurrencyValue)
 * ARM64: w0=petId, w1=payWithOpals, w2=cost, x3=newCurrencyValue. Static method.
 * Real VA 0x02E3DF40 (inspector VA, no -0x4000 adjustment for this range).
 *
 * IMPORTANT: The skipped first instruction is `stp x25, x30, [sp, #-64]!`.
 * We must save x25 at entry and execute the stp before RESUME.
 */
__attribute__((naked)) void hook_unlock_combat_pet(void)
{
    __asm__ volatile(
        "stp x29, x30, [sp, #-16]!\n"
        "stp x25, x26, [sp, #-16]!\n"  /* save x25 (skipped stp needs it) */
        "stp x0, x1, [sp, #-16]!\n"
        "stp x2, x3, [sp, #-16]!\n"
        "str w0, [sp, #-16]!\n"      /* petDefinitionIntegerId */
        "str w1, [sp, #-16]!\n"      /* payWithOpals (bool) */
        "str w2, [sp, #-16]!\n"      /* cost */
        "bl hook_unlock_combat_pet_c\n"
        "add sp, sp, #48\n"
        "ldp x2, x3, [sp], #16\n"
        "ldp x0, x1, [sp], #16\n"
        "ldp x25, x26, [sp], #16\n"  /* restore x25 */
        "ldp x29, x30, [sp], #16\n"
        "stp x30, x25, [sp, #-64]!\n"  /* execute skipped instruction (x30 first!) */
        "b UNLOCK_COMBAT_PET_RESUME\n"  /* PC-relative: ASLR-safe */
    );
}

void hook_unlock_combat_pet_c(int petDefinitionIntegerId, int payWithOpals, int cost)
{
    /* Build JSON: {"characterId":"...","payWithOpals":true,"combatPetDefinitionIntegerId":N,"cost":M} */
    static char json[256];
    int n = 0;
    const char *p1 = "{\"characterId\":\"";
    while (*p1) json[n++] = *p1++;
    for (int i = 0; g_guid[i]; i++) json[n++] = g_guid[i];
    const char *p2 = "\",\"payWithOpals\":";
    while (*p2) json[n++] = *p2++;
    const char *pb = payWithOpals ? "true" : "false";
    while (*pb) json[n++] = *pb++;
    const char *p3 = ",\"combatPetDefinitionIntegerId\":";
    while (*p3) json[n++] = *p3++;
    char nb[12]; int nn = 0;
    int t = petDefinitionIntegerId; if (t==0) nb[nn++]='0';
    char rb[12]; int rn=0;
    while (t>0) { rb[rn++]='0'+t%10; t/=10; }
    while (rn>0) nb[nn++]=rb[--rn];
    for (int i=0;i<nn;i++) json[n++]=nb[i];
    const char *p4 = ",\"cost\":";
    while (*p4) json[n++] = *p4++;
    nn=0; rn=0; t=cost; if(t==0) nb[nn++]='0';
    while (t>0) { rb[rn++]='0'+t%10; t/=10; }
    while (rn>0) nb[nn++]=rb[--rn];
    for (int i=0;i<nn;i++) json[n++]=nb[i];
    json[n++]='}'; json[n]=0;
    http_post("/api/character/unlock-combat-pet", json, n);
}
