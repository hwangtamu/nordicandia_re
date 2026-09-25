// Leaderboard online patch for the Android client.
//
// The shipped Android build never asks the server for leaderboards: its whole
// leaderboard window is wired to Nordicandia.Client.Offline.OfflineFakeLeaderboard,
// every MagicOnion leaderboard API is dead code, and the fake rows are synthesised
// locally. The three Build* entry points are therefore replaced here with a version
// that fetches the real cross-platform standings (Steam included) from the small
// plain-HTTP JSON feed added to the private server.
//
// Hooked (entry instruction replaced with a plain B, i.e. a tail call):
//   0x02E3FABC  OfflineFakeLeaderboard.BuildOverallBoard(int size)
//   0x02E40264  OfflineFakeLeaderboard.BuildClassBoard(int classId, int size)
//   0x02E404F0  OfflineFakeLeaderboard.BuildHelheimBoard(int size)
// (WindowLeaderboard.<>c.<Awake>b__28_* call them as (50) / (classId, 50) / (50):
//  classId comes FIRST - the earlier (seed, classId) reading used size=50 as the
//  class id, returned null and made ShowBoard throw a NullReferenceException.)
//
// ---------------------------------------------------------------------------
// FakeLeaderboardRow is a *value type* (readonly struct), not a class:
//   get_Rank  = ldr w0,[x0]        get_Name     = ldr x0,[x0,#8]
//   get_ClassId = ldr w0,[x0,#0x10] get_Value   = ldr x0,[x0,#0x18]
//   get_IsPlayer = ldrb w0,[x0,#0x20]          (sizeof = 0x28)
// il2cpp_field_get_offset reports 0x10..0x30 because it includes the boxed object
// header.  A FakeLeaderboardRow[] therefore stores the structs INLINE at
// vector + i*0x28; it is not an array of pointers.  Earlier versions boxed each row
// with il2cpp_object_new and stored the box pointers at an 8-byte stride, which is
// why the window showed one row with a garbage value followed by ??<Unknown>?? rows.
//
// ShowBoard (0x025F6AF0) only reads the result through the IReadOnlyCollection<T>.Count
// and IReadOnlyList<T>.get_Item interfaces (interface dispatch, slow path for arrays),
// there is no castclass to List<T> - a correctly laid out T[] is a perfectly valid
// result (the retail code itself returns Array.Empty<FakeLeaderboardRow>() for empty
// boards).  The "array + List hybrid" experiment made the board empty because it
// wrote a non-null ArrayBounds* at +0x10, turning Count into garbage.
//
// Writable state lives at 0x5DE6000 (see lbstub.ld): the old 0x5DDE100 is INSIDE the
// retail .bss (0x5BA8C50..0x5DDE840), so il2cpp itself overwrote the cached row class
// and il2cpp_array_new crashed with a bad klass.

typedef unsigned char u8;
typedef unsigned int u32;
typedef int i32;
typedef long long i64;
typedef unsigned long long u64;
typedef void *ptr;

extern ptr il2cpp_class_from_name(ptr image, const char *ns, const char *name);
extern ptr il2cpp_domain_get(void);
extern ptr il2cpp_domain_get_assemblies(ptr domain, u64 *size);
extern ptr il2cpp_assembly_get_image(ptr assembly);
extern void il2cpp_runtime_class_init(ptr klass);
extern ptr il2cpp_array_new(ptr elementKlass, u64 length);
extern ptr il2cpp_string_new(const char *str);
extern ptr il2cpp_alloc(unsigned long size);
extern int il2cpp_class_is_valuetype(ptr klass);
extern i32 il2cpp_class_value_size(ptr klass, u32 *align);
extern void il2cpp_gc_wbarrier_set_field(ptr obj, ptr *targetAddress, ptr object);
extern ptr il2cpp_class_get_method_from_name(ptr klass, const char *name, int argsCount);
extern ptr il2cpp_runtime_invoke(ptr method, ptr object, ptr *params, ptr *exception);
extern ptr il2cpp_object_get_class(ptr object);
extern ptr il2cpp_class_get_type(ptr klass);
extern ptr il2cpp_type_get_object(ptr type);

/* Retail entry points used by the click trampoline and inspect-window flow. */
extern void retail_apply_rank(void);
extern void retail_create_row_resume(void);
extern void retail_button_press(void);
extern ptr retail_get_window(i32 id, ptr method);
extern void retail_bring_front(ptr window, ptr method);
extern ptr retail_instantiate_9(ptr original, ptr parent, i32 worldPositionStays, ptr method);
extern ptr retail_instantiate_method_slot;
extern ptr retail_component_get_transform(ptr component, ptr method);
typedef struct { u64 low, high; } UniTask16;
extern UniTask16 retail_inspect_player(ptr inspect, u64 guidLow, u64 guidHigh, ptr method);
extern void retail_forget(u64 taskLow, u64 taskHigh, ptr method);

/* libc is not linkable from a freestanding blob and its addresses are ASLR'd, so the
 * handful of socket calls are issued as raw arm64 Linux syscalls (allowed by the app
 * seccomp policy: socket/connect/sendto/recvfrom/setsockopt/close). */
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

/* ---- cloud leaderboard feed ------------------------------------------------- */
/* Baked-in private server address (plain HTTP/1.1 JSON feed, port 8081). */
#define LB_IP_WORD 0x88328C03u /* 3.140.50.136 network byte order */
#define LB_PORT 8081
#define LB_MODE 1 /* 0 = normal, 1 = season */
#define LB_BODY_CAP 32768

/* FakeLeaderboardRow (value type) field offsets inside the unboxed struct. */
#define ROW_RANK 0x00
#define ROW_NAME 0x08
#define ROW_CLASS 0x10
#define ROW_VALUE 0x18
#define ROW_PLAYER 0x20
#define ROW_SIZE 0x28
#define ARRAY_VECTOR 0x20 /* Il2CppArray: klass, monitor, bounds, max_length, vector[] */

static ptr g_rowKlass;
static char *g_body;
static i32 g_rowSize;
/* These are small fixed rings because one feed can contain at most 50 rows. */
#define LB_MAP_CAP 64
struct guid_pair { ptr key; u64 low, high; };
static struct guid_pair g_nameGuids[LB_MAP_CAP];
static struct guid_pair g_buttonGuids[LB_MAP_CAP];
static u32 g_nameNext, g_buttonNext;
/* Only class/method metadata is cached here. Managed objects are never cached:
 * this .bss is not a GC root, so a stale window/component pointer could be
 * collected and reused after the player closes the inspect window. */
static ptr g_prefabDbClass, g_gameObjectClass;
static ptr g_inspectClass, g_uiWindowClass, g_componentClass;
static ptr g_getPrefabDb, g_getComponent, g_componentGetComponent;
static volatile i32 g_openingInspect;
/* g_dbg: [0] row klass [1] body buf [2] http body len [3] rows parsed
 *        [4] socket fd [5] connect rc [6] result array [7] last category */
volatile long g_dbg[8];

static void str_cat(char *dst, int *len, int cap, const char *s)
{
    while (*s && *len < cap - 1) { dst[(*len)++] = *s++; }
}

/* Blocking HTTP/1.0 GET of `path`, returns body length (0 on failure). */
static int http_get(const char *path, char *out, int cap)
{
    struct { unsigned short family; unsigned short port; unsigned int addr; char pad[8]; } sa;
    long tv[2];
    int fd, body = 0, got;
    char req[320];
    int rl = 0;
    int in_body = 0, scan = 0;

    fd = (int)sc(SYS_socket, 2 /*AF_INET*/, 1 /*SOCK_STREAM*/, 0, 0, 0, 0);
    g_dbg[4] = fd;
    if (fd < 0) return 0;

    /* Bound the whole exchange so a blocked network can never freeze the UI.
     * struct timeval is {long tv_sec; long tv_usec} on arm64. */
    tv[0] = 2; tv[1] = 0;
    sc(SYS_setsockopt, fd, 1 /*SOL_SOCKET*/, 20 /*SO_RCVTIMEO*/, (long)tv, sizeof(tv), 0);
    sc(SYS_setsockopt, fd, 1, 21 /*SO_SNDTIMEO*/, (long)tv, sizeof(tv), 0);

    sa.family = 2;
    sa.port = (unsigned short)(((LB_PORT & 0xff) << 8) | (LB_PORT >> 8)); /* htons */
    sa.addr = LB_IP_WORD;
    for (int i = 0; i < 8; i++) sa.pad[i] = 0;

    { long rc = sc(SYS_connect, fd, (long)&sa, 16, 0, 0, 0); g_dbg[5] = rc;
      if (rc != 0) { sc(SYS_close, fd, 0, 0, 0, 0, 0); return 0; } }

    str_cat(req, &rl, sizeof(req), "GET ");
    str_cat(req, &rl, sizeof(req), path);
    str_cat(req, &rl, sizeof(req), " HTTP/1.0\r\nHost: nordicandia-lb\r\nConnection: close\r\n\r\n");
    if (sc(SYS_sendto, fd, (long)req, rl, 0, 0, 0) <= 0) { sc(SYS_close, fd, 0, 0, 0, 0, 0); return 0; }

    while (body < cap - 1 &&
           (got = (int)sc(SYS_recvfrom, fd, (long)(out + body), cap - 1 - body, 0, 0, 0)) > 0) {
        body += got;
        if (!in_body) {
            for (int i = scan; i + 3 < body; i++) {
                if (out[i] == '\r' && out[i + 1] == '\n' && out[i + 2] == '\r' && out[i + 3] == '\n') {
                    int tail = body - (i + 4);
                    for (int j = 0; j < tail; j++) out[j] = out[i + 4 + j];
                    body = tail;
                    in_body = 1;
                    break;
                }
            }
            if (!in_body) scan = body > 3 ? body - 3 : 0;
        }
    }
    sc(SYS_close, fd, 0, 0, 0, 0, 0);
    out[body] = 0;
    return in_body ? body : 0;
}

static ptr resolve_row_klass(void)
{
    u64 n = 0;
    ptr *assemblies;
    if (g_rowKlass) return g_rowKlass;
    assemblies = (ptr *)il2cpp_domain_get_assemblies(il2cpp_domain_get(), &n);
    for (u64 i = 0; i < n; i++) {
        ptr k = il2cpp_class_from_name(il2cpp_assembly_get_image(assemblies[i]),
                                       "Nordicandia.OfflineCore", "FakeLeaderboardRow");
        if (k) {
            u32 align = 0;
            il2cpp_runtime_class_init(k);
            /* Refuse to write anything if the layout is not the one measured above. */
            if (!il2cpp_class_is_valuetype(k)) return 0;
            g_rowSize = il2cpp_class_value_size(k, &align);
            if (g_rowSize != ROW_SIZE) return 0;
            g_rowKlass = k; g_dbg[0] = (long)k;
            return k;
        }
    }
    return 0;
}

static long atoi_span(const char *s, const char *end)
{
    long v = 0;
    int neg = 0;
    while (s < end && (*s == ' ')) s++;
    if (s < end && *s == '-') { neg = 1; s++; }
    for (; s < end && *s >= '0' && *s <= '9'; s++) v = v * 10 + (*s - '0');
    return neg ? -v : v;
}

static const char *find_str(const char *p, const char *end, const char *needle)
{
    int nl = 0;
    while (needle[nl]) nl++;
    while (p + nl <= end) {
        int i = 0;
        while (i < nl && p[i] == needle[i]) i++;
        if (i == nl) return p;
        p++;
    }
    return 0;
}

static int hexval(char c)
{
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

static int put_utf8(char *out, int o, int cap, u32 cp)
{
    if (cp < 0x80) { if (o + 1 < cap) out[o++] = (char)cp; }
    else if (cp < 0x800) { if (o + 2 < cap) { out[o++] = (char)(0xC0 | (cp >> 6)); out[o++] = (char)(0x80 | (cp & 0x3F)); } }
    else if (cp < 0x10000) { if (o + 3 < cap) { out[o++] = (char)(0xE0 | (cp >> 12)); out[o++] = (char)(0x80 | ((cp >> 6) & 0x3F)); out[o++] = (char)(0x80 | (cp & 0x3F)); } }
    else { if (o + 4 < cap) { out[o++] = (char)(0xF0 | (cp >> 18)); out[o++] = (char)(0x80 | ((cp >> 12) & 0x3F)); out[o++] = (char)(0x80 | ((cp >> 6) & 0x3F)); out[o++] = (char)(0x80 | (cp & 0x3F)); } }
    return o;
}

static int read_u16(const char *s, const char *end, u32 *v)
{
    int a, b, c, d;
    if (s + 4 > end) return 0;
    a = hexval(s[0]); b = hexval(s[1]); c = hexval(s[2]); d = hexval(s[3]);
    if ((a | b | c | d) < 0) return 0;
    *v = (u32)((a << 12) | (b << 8) | (c << 4) | d);
    return 1;
}

/* Decodes the JSON string starting right after its opening quote into UTF-8.
 * System.Text.Json escapes every non-ASCII character as \uXXXX by default, so this
 * is required for non-Latin character names. */
static void json_string(const char *s, const char *end, char *out, int cap)
{
    int o = 0;
    while (s < end && *s != '"') {
        if (*s != '\\') { if (o + 1 < cap) out[o++] = *s; s++; continue; }
        s++;
        if (s >= end) break;
        switch (*s) {
        case 'n': o = put_utf8(out, o, cap, '\n'); s++; break;
        case 't': o = put_utf8(out, o, cap, ' '); s++; break;
        case 'r': case 'b': case 'f': s++; break;
        case 'u': {
            u32 cp = 0, lo = 0;
            if (!read_u16(s + 1, end, &cp)) { s++; break; }
            s += 5;
            if (cp >= 0xD800 && cp < 0xDC00 && s + 6 <= end && s[0] == '\\' && s[1] == 'u' &&
                read_u16(s + 2, end, &lo) && lo >= 0xDC00 && lo < 0xE000) {
                cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
                s += 6;
            }
            o = put_utf8(out, o, cap, cp);
            break;
        }
        default: o = put_utf8(out, o, cap, (u8)*s); s++; break; /* \" \\ \/ */
        }
    }
    out[o] = 0;
}

/* Value of "key": within [p, end) - the object of a single row. */
static const char *field(const char *p, const char *end, const char *key)
{
    const char *f = find_str(p, end, key);
    if (!f) return 0;
    while (*key) { key++; f++; }
    return f;
}

static int parse_guid(const char *s, const char *end, u64 *low, u64 *high)
{
    u8 text[16], mem[16];
    int n = 0;
    while (s < end && *s != '"' && n < 16) {
        int a, b;
        if (*s == '-') { s++; continue; }
        a = hexval(*s++);
        if (s >= end || *s == '"') return 0;
        b = hexval(*s++);
        if (a < 0 || b < 0) return 0;
        text[n++] = (u8)((a << 4) | b);
    }
    if (n != 16) return 0;
    /* Guid's first three fields are little-endian in memory on this target. */
    mem[0] = text[3]; mem[1] = text[2]; mem[2] = text[1]; mem[3] = text[0];
    mem[4] = text[5]; mem[5] = text[4];
    mem[6] = text[7]; mem[7] = text[6];
    for (int i = 8; i < 16; i++) mem[i] = text[i];
    *low = 0; *high = 0;
    for (int i = 0; i < 8; i++) *low |= (u64)mem[i] << (i * 8);
    for (int i = 0; i < 8; i++) *high |= (u64)mem[i + 8] << (i * 8);
    return 1;
}

static void remember_pair(struct guid_pair *ring, u32 *next, ptr key, u64 low, u64 high)
{
    if (!key) return;
    for (int i = 0; i < LB_MAP_CAP; i++) {
        if (ring[i].key == key) {
            ring[i].low = low;
            ring[i].high = high;
            return;
        }
    }
    u32 at = (*next)++ & (LB_MAP_CAP - 1);
    ring[at].key = key;
    ring[at].low = low;
    ring[at].high = high;
}

static void forget_pair(struct guid_pair *ring, ptr key)
{
    for (int i = 0; i < LB_MAP_CAP; i++) {
        if (ring[i].key == key) ring[i].key = 0;
    }
}

static int lookup_pair(struct guid_pair *ring, ptr key, u64 *low, u64 *high)
{
    if (!key) return 0;
    for (int i = 0; i < LB_MAP_CAP; i++) {
        if (ring[i].key == key) {
            *low = ring[i].low;
            *high = ring[i].high;
            return 1;
        }
    }
    return 0;
}

static ptr empty_result(ptr klass)
{
    /* A genuine zero-length FakeLeaderboardRow[] - exactly what the retail code
     * returns (Array.Empty<T>) for an empty board.  Never null: ShowBoard
     * dereferences its rows argument unconditionally. */
    return il2cpp_array_new(klass, 0);
}

static ptr build_rows(const char *category, const char *cls)
{
    char *body = g_body;
    char path[192];
    ptr klass, arr;
    const char *p, *end;
    int pl = 0, count = 0, i = 0;
    int helheim = category[0] == 'h';

    g_dbg[7] = (long)category;
    /* A new board destroys the previous rows; forget their buttons so a later
     * Button allocated at a recycled address cannot open a stale profile. */
    for (i = 0; i < LB_MAP_CAP; ++i) g_buttonGuids[i].key = 0;
    g_buttonNext = 0;
    i = 0;
    klass = resolve_row_klass();
    if (!klass) return 0;   /* unknown layout: let the window fail loudly rather than corrupt memory */
    if (!body) { body = (char *)il2cpp_alloc(LB_BODY_CAP); g_body = body; }
    g_dbg[1] = (long)body;
    if (!body) return empty_result(klass);

    str_cat(path, &pl, sizeof(path), "/api/leaderboards/");
    str_cat(path, &pl, sizeof(path), LB_MODE ? "season" : "normal");
    str_cat(path, &pl, sizeof(path), "/");
    str_cat(path, &pl, sizeof(path), category);
    str_cat(path, &pl, sizeof(path), "?limit=50");
    if (cls) { str_cat(path, &pl, sizeof(path), "&cls="); str_cat(path, &pl, sizeof(path), cls); }
    path[pl] = 0;

    { int hl = http_get(path, body, LB_BODY_CAP); g_dbg[2] = hl;
      if (hl <= 0) return empty_result(klass); }

    end = body;
    while (*end) end++;

    /* Rows live in "rows":[{...},{...}]; each row object starts with "rank". */
    p = find_str(body, end, "\"rows\":");
    if (!p) return empty_result(klass);
    for (const char *q = p; (q = find_str(q, end, "\"rank\":")) != 0; q += 7) count++;
    g_dbg[3] = count;
    if (count <= 0) return empty_result(klass);

    arr = il2cpp_array_new(klass, (u64)count);
    g_dbg[6] = (long)arr;
    if (!arr) return empty_result(klass);

    while ((p = find_str(p, end, "\"rank\":")) != 0 && i < count) {
        const char *next = find_str(p + 7, end, "\"rank\":");
        const char *rowEnd = next ? next : end;
        const char *f;
        char name[96];
        long rank, value = 0, clsId = 0, isPlayer = 0;
        u64 guidLow = 0, guidHigh = 0;
        int hasGuid = 0;
        ptr nameString;
        u8 *row;

        rank = atoi_span(p + 7, rowEnd);

        name[0] = 0;
        f = field(p, rowEnd, "\"name\":\"");
        if (f) json_string(f, rowEnd, name, sizeof(name));

        if (helheim) {
            /* LeaderboardQuery.EncodeScore: WorldTier * 1_000_000 + Waypoint * 1_000 */
            f = field(p, rowEnd, "\"score\":");
            if (f) value = atoi_span(f, rowEnd) / 1000000;
        } else {
            f = field(p, rowEnd, "\"level\":");
            if (f) value = atoi_span(f, rowEnd);
        }
        f = field(p, rowEnd, "\"classId\":");
        if (f) clsId = atoi_span(f, rowEnd);
        f = field(p, rowEnd, "\"isPlayer\":");
        if (f) isPlayer = (*f == 't');
        f = field(p, rowEnd, "\"characterId\":\"");
        if (f) hasGuid = parse_guid(f, rowEnd, &guidLow, &guidHigh);

        row = (u8 *)arr + ARRAY_VECTOR + (u64)i * (u64)g_rowSize;
        *(i32 *)(row + ROW_RANK) = (i32)(rank ? rank : (i + 1));
        nameString = il2cpp_string_new(name);
        il2cpp_gc_wbarrier_set_field(arr, (ptr *)(row + ROW_NAME), nameString);
        if (hasGuid) remember_pair(g_nameGuids, &g_nameNext, nameString, guidLow, guidHigh);
        *(i32 *)(row + ROW_CLASS) = (i32)clsId;
        *(i64 *)(row + ROW_VALUE) = (i64)value;
        *(u8 *)(row + ROW_PLAYER) = (u8)(isPlayer ? 1 : 0);

        i++;
        p = rowEnd;
    }

    /* Any trailing, unparsed slots stay zeroed (rank 0, null name).  The server
     * never announces more rows than it sends, but guard the UI anyway. */
    for (; i < count; i++) {
        u8 *row = (u8 *)arr + ARRAY_VECTOR + (u64)i * (u64)g_rowSize;
        *(i32 *)(row + ROW_RANK) = i + 1;
        il2cpp_gc_wbarrier_set_field(arr, (ptr *)(row + ROW_NAME), il2cpp_string_new(""));
    }
    return arr;
}

ptr lb_overall(i32 size, ptr method)
{
    (void)size; (void)method;
    return build_rows("overall", 0);
}

ptr lb_class(i32 classId, i32 size, ptr method)
{
    /* SharedNet.Constants.Game.CharacterClass; the window's tabs pass 0/4/5/6.
     * Same clamp as OfflineFakeLeaderboard.ClampClassId.  A switch (PC-relative
     * adr of each literal) instead of a pointer table: a table would hold absolute
     * link-time addresses, which are wrong once the library is loaded under ASLR. */
    const char *name;
    (void)size; (void)method;
    switch (classId < 0 ? 0 : classId > 7 ? 7 : classId) {
    case 0: name = "warrior"; break;
    case 1: name = "paladin"; break;
    case 2: name = "assassin"; break;
    case 3: name = "barbarian"; break;
    case 4: name = "hunter"; break;
    case 5: name = "mage"; break;
    case 6: name = "necromancer"; break;
    default: name = "priest"; break;
    }
    return build_rows("class", name);
}

ptr lb_helheim(i32 size, ptr method)
{
    (void)size; (void)method;
    return build_rows("helheim", 0);
}

static ptr find_class(const char *ns, const char *name)
{
    u64 count = 0;
    ptr *assemblies = (ptr *)il2cpp_domain_get_assemblies(il2cpp_domain_get(), &count);
    if (!assemblies) return 0;
    for (u64 i = 0; i < count; i++) {
        ptr k = il2cpp_class_from_name(il2cpp_assembly_get_image(assemblies[i]), ns, name);
        if (k) return k;
    }
    return 0;
}

static ptr find_method(ptr klass, const char *name, int argCount)
{
    return klass ? il2cpp_class_get_method_from_name(klass, name, argCount) : 0;
}

static ptr invoke(ptr method, ptr object, ptr *args)
{
    ptr exception = 0;
    ptr result;
    if (!method) return 0;
    result = il2cpp_runtime_invoke(method, object, args, &exception);
    return exception ? 0 : result;
}

static ptr invoke_singleton(ptr method)
{
    return invoke(method, 0, 0);
}

static ptr get_component_with(ptr method, ptr object, ptr componentClass)
{
    ptr type;
    ptr args[1];
    if (!object || !componentClass || !method) return 0;
    type = il2cpp_type_get_object(il2cpp_class_get_type(componentClass));
    if (!type) return 0;
    /* il2cpp_runtime_invoke takes reference-type arguments as the object pointer
     * itself (only value types are passed by address). */
    args[0] = type;
    return invoke(method, object, args);
}

static ptr get_component(ptr gameObject, ptr componentClass)
{
    return get_component_with(g_getComponent, gameObject, componentClass);
}

static int resolve_inspect_api(void)
{
    if (g_getPrefabDb && g_getComponent && g_componentGetComponent &&
        g_inspectClass && g_uiWindowClass)
        return 1;
    g_prefabDbClass = find_class("Nordicandia.UI", "UIPrefabDatabase");
    g_gameObjectClass = find_class("UnityEngine", "GameObject");
    g_componentClass = find_class("UnityEngine", "Component");
    g_inspectClass = find_class("", "WindowInspectPlayer");
    g_uiWindowClass = find_class("DuloGames.UI", "UIWindow");
    if (!g_prefabDbClass || !g_gameObjectClass || !g_componentClass ||
        !g_inspectClass || !g_uiWindowClass)
        return 0;
    g_getPrefabDb = find_method(g_prefabDbClass, "get_Instance", 0);
    g_getComponent = find_method(g_gameObjectClass, "GetComponent", 1);
    g_componentGetComponent = find_method(g_componentClass, "GetComponent", 1);
    return g_getPrefabDb && g_getComponent && g_componentGetComponent;
}

/* Unity objects keep their native object in m_CachedPtr (+0x10); it is cleared
 * when the object is destroyed even while the managed wrapper is still alive. */
static int unity_alive(ptr object)
{
    return object && *(ptr *)((u8 *)object + 0x10) != 0;
}

/* Returns the UIWindow of a live "Window (Inspect Player)", creating it when needed.
 *
 * Retail ShowWindow/ShowInGameWindow deliberately return null for this prefab in the
 * offline build, so it is instantiated here with exactly the parent ShowInGameWindow
 * would have used (GetWindow(InGame=5).transform). No Show() call is needed or safe:
 * a freshly instantiated window is already in the visible state (UIWindow+0x40 == 0),
 * and forcing a transition on it aborts. WindowInspectPlayer(.Skills).Awake, which the
 * offline build turned into Destroy(gameObject), is patched to `ret` by the installer. */
static ptr inspect_window(void)
{
    ptr window, database, prefab, window5, parent, methodCell, instantiateMethod, clone;

    window = retail_get_window(46, 0);
    if (unity_alive(window)) return window;

    database = invoke_singleton(g_getPrefabDb);
    if (!database) return 0;
    prefab = *(ptr *)((u8 *)database + 0x2f0);
    window5 = retail_get_window(5, 0);
    if (!prefab || !unity_alive(window5)) return 0;
    parent = retail_component_get_transform(window5, 0);
    if (!parent) return 0;
    /* The generic Instantiate<GameObject> MethodInfo used by ShowWindow; the slot is
     * a pointer-to-pointer cell filled by IL2CPP's normal method initialization. */
    methodCell = *(ptr *)&retail_instantiate_method_slot;
    instantiateMethod = methodCell ? *(ptr *)methodCell : 0;
    if (!instantiateMethod) return 0;
    clone = retail_instantiate_9(prefab, parent, 0, instantiateMethod);
    if (!clone) return 0;

    window = retail_get_window(46, 0);
    if (unity_alive(window)) return window;
    return get_component(clone, g_uiWindowClass);
}

static void open_inspect(u64 guidLow, u64 guidHigh)
{
    ptr window, inspect;
    UniTask16 task;

    if (g_openingInspect) return;
    g_openingInspect = 1;
    if (!resolve_inspect_api()) goto done;
    window = inspect_window();
    if (!unity_alive(window)) goto done;
    inspect = get_component_with(g_componentGetComponent, window, g_inspectClass);
    if (!unity_alive(inspect)) goto done;
    retail_bring_front(window, 0);
    task = retail_inspect_player(inspect, guidLow, guidHigh, 0);
    retail_forget(task.low, task.high, 0);

done:
    g_openingInspect = 0;
}

/* LeaderboardEntry+0x50 is the row's full-size "Button (Inspect)". Retail wired it to
 * the inspect window; the offline build left its onClick empty. */
#define ENTRY_INSPECT_BUTTON 0x50

void lb_row_register(ptr entry, i32 rank, ptr row)
{
    ptr name, button;
    u64 low, high;
    (void)rank;
    if (!entry || !row) return;
    name = *(ptr *)((u8 *)row + ROW_NAME);
    button = *(ptr *)((u8 *)entry + ENTRY_INSPECT_BUTTON);
    if (!button) return;
    if (lookup_pair(g_nameGuids, name, &low, &high))
        remember_pair(g_buttonGuids, &g_buttonNext, button, low, high);
    else
        forget_pair(g_buttonGuids, button);
}

void lb_click_dispatch(ptr button)
{
    u64 low, high;
    if (!lookup_pair(g_buttonGuids, button, &low, &high)) return;
    open_inspect(low, high);
}

/* Hook the row's ApplyRankDecorations call. Register the Button -> character ID
 * mapping, then run the original call with its untouched arguments. */
__attribute__((naked)) void lb_row_hook(void)
{
    __asm__ volatile(
        "sub sp, sp, #32\n"
        "stp x0, x1, [sp]\n"
        "stp x2, x30, [sp, #16]\n"
        "mov x2, x19\n"
        "bl lb_row_register\n"
        "ldp x2, x30, [sp, #16]\n"
        "ldp x0, x1, [sp]\n"
        "add sp, sp, #32\n"
        "bl retail_apply_rank\n"
        "b retail_create_row_resume\n");
}

/* Placed on Button.OnPointerClick's left-button tail call (`b Button.Press`), so only
 * genuine left clicks/taps on a mapped leaderboard row open a profile; every other
 * Button continues straight into the untouched retail Press(). */
__attribute__((naked)) void lb_click_hook(void)
{
    __asm__ volatile(
        "stp x0, x30, [sp, #-16]!\n"
        "bl lb_click_dispatch\n"
        "ldp x0, x30, [sp], #16\n"
        "b retail_button_press\n");
}
