// Leaderboard online patch for the Android client.
//
// The shipped Android build never asks the server for leaderboards: its whole
// leaderboard window is wired to Nordicandia.Client.Offline.OfflineFakeLeaderboard,
// every MagicOnion leaderboard API is dead code, and the fake rows are synthesised
// locally. The three Build* entry points are therefore replaced here with a version
// that fetches the real cross-platform standings (Steam included) from the small
// plain-HTTP JSON feed added to the private server and materialises them as
// System.FakeLeaderboardRow[] (an array satisfies the IReadOnlyList<T> return type).
//
// Hooked (entry instruction replaced with a plain B, uuid-style tail call):
//   0x02E3FABC  OfflineFakeLeaderboard.BuildOverallBoard(int seed)
//   0x02E40264  OfflineFakeLeaderboard.BuildClassBoard(int seed, int classId)
//   0x02E404F0  OfflineFakeLeaderboard.BuildHelheimBoard(int seed)
//
// FakeLeaderboardRow layout (from its getters):
//   +0x00 int  Rank   +0x08 string Name   +0x10 int ClassId
//   +0x18 long Value  +0x20 bool IsPlayer

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
extern ptr il2cpp_object_new(ptr klass);
extern void il2cpp_runtime_class_init(ptr klass);
extern ptr il2cpp_array_new(ptr elementKlass, u64 length);
extern ptr il2cpp_string_new(const char *str);
extern ptr il2cpp_alloc(unsigned long size);

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

static ptr g_rowKlass;
static char *g_body;
volatile long g_dbg[8];

static void mem_copy(char *d, const char *s, int n)
{
    int i = 0;
    while (i < n) { d[i] = s[i]; i++; }
}

static void str_cat(char *dst, int *len, const char *s)
{
    while (*s) { dst[(*len)++] = *s++; }
}

static void itoa10(long v, char *out)
{
    char tmp[24];
    int n = 0, neg = 0;
    unsigned long u;
    if (v < 0) { neg = 1; u = (unsigned long)(-v); } else u = (unsigned long)v;
    if (!u) tmp[n++] = '0';
    while (u) { tmp[n++] = (char)('0' + (u % 10)); u /= 10; }
    if (neg) tmp[n++] = '-';
    for (int i = 0; i < n; i++) out[i] = tmp[n - 1 - i];
    out[n] = 0;
}

static int str_ends_with(const char *s, const char *suf)
{
    int a = 0, b = 0;
    while (s[a]) a++;
    while (suf[b]) b++;
    if (b > a) return 0;
    for (int i = 0; i < b; i++) if (s[a - b + i] != suf[i]) return 0;
    return 1;
}

/* Blocking HTTP/1.0 GET of `path`, returns body length (0 on failure). */
static int http_get(const char *path, char *out, int cap)
{
    struct { unsigned short family; unsigned short port; unsigned int addr; char pad[8]; } sa;
    int tv[2];
    int fd, body = 0, got;
    char req[256];
    int rl = 0;
    int in_body = 0, scan = 0;

    fd = (int)sc(SYS_socket, 2 /*AF_INET*/, 1 /*SOCK_STREAM*/, 0, 0, 0, 0);
    g_dbg[4] = fd;
    if (fd < 0) return 0;

    /* Bound the whole exchange so a blocked network can never freeze the UI. */
    tv[0] = 2; tv[1] = 0; /* 2s */
    sc(SYS_setsockopt, fd, 1 /*SOL_SOCKET*/, 20 /*SO_RCVTIMEO*/, (long)tv, sizeof(tv), 0);
    sc(SYS_setsockopt, fd, 1, 21 /*SO_SNDTIMEO*/, (long)tv, sizeof(tv), 0);

    sa.family = 2;
    sa.port = (unsigned short)(((LB_PORT & 0xff) << 8) | (LB_PORT >> 8)); /* htons */
    sa.addr = LB_IP_WORD;
    for (int i = 0; i < 8; i++) sa.pad[i] = 0;

    { long rc = sc(SYS_connect, fd, (long)&sa, 16, 0, 0, 0); g_dbg[5] = rc;
      if (rc != 0) { sc(SYS_close, fd, 0, 0, 0, 0, 0); return 0; } }

    str_cat(req, &rl, "GET ");
    str_cat(req, &rl, path);
    str_cat(req, &rl, " HTTP/1.0\r\nHost: nordicandia-lb\r\nConnection: close\r\n\r\n");
    if (sc(SYS_sendto, fd, (long)req, rl, 0, 0, 0) <= 0) { sc(SYS_close, fd, 0, 0, 0, 0, 0); return 0; }

    while ((got = (int)sc(SYS_recvfrom, fd, (long)(out + body), cap - 1 - body, 0, 0, 0)) > 0) {
        int start = body;
        body += got;
        if (body >= cap - 1) break;
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
        (void)start;
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
            /* il2cpp_array_new/il2cpp_object_new dereference class metadata: an
             * uninitialised class made them fault at null+0x28 (SIGSEGV) when the
             * leaderboard window opened.  Force the cctor/type init first. */
            il2cpp_runtime_class_init(k);
            g_rowKlass = k; g_dbg[0] = (long)k; return k;
        }
    }
    return 0;
}

/* Fills the managed FakeLeaderboardRow at `obj`. */
static void fill_row(ptr obj, int rank, const char *name, int nameLen, long value, int classId, int isPlayer)
{
    char buf[64];
    int i;
    for (i = 0; i < nameLen && i < 63; i++) buf[i] = name[i];
    buf[i] = 0;

    /* Field offsets measured from the real class (il2cpp_field_get_offset):
     *   <Rank> 0x10, <Name> 0x18, <ClassId> 0x20, <Value> 0x28, <IsPlayer> 0x30
     * The previous 0x00/0x08/... base overwrote the object header (klass at +0x00,
     * monitor at +0x08) with the rank/name, which crashed IL2CPP later (SIGSEGV at
     * null+0x28 with a bogus klass) and made the UI show an unnamed row with a
     * garbage level. */
    *(i32 *)((u8 *)obj + 0x10) = rank;
    *(ptr *)((u8 *)obj + 0x18) = il2cpp_string_new(buf);
    *(i32 *)((u8 *)obj + 0x20) = classId;
    *(i64 *)((u8 *)obj + 0x28) = value;
    *(u8 *)((u8 *)obj + 0x30) = (u8)(isPlayer ? 1 : 0);
}

static long atoi_span(const char *s, int n)
{
    long v = 0;
    int i = 0, neg = 0;
    if (i < n && s[i] == '-') { neg = 1; i++; }
    for (; i < n && s[i] >= '0' && s[i] <= '9'; i++) v = v * 10 + (s[i] - '0');
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

/*
 * Builds a managed FakeLeaderboardRow[] from the private server's JSON feed.
 * `category` is "overall", "class" or "helheim"; `cls` is used for "class".
 * Returns 0 when the feed is unreachable, so the caller can fall back.
 */
/* An empty result that still satisfies the List<FakeLeaderboardRow> layout the
 * window reads (see the note in build_rows).  Never null: the window dereferences
 * it unconditionally. */
static ptr empty_result(ptr klass)
{
    ptr arr = il2cpp_array_new(klass, 0);
    if (arr) { *(ptr *)((u8 *)arr + 0x10) = arr; *(i64 *)((u8 *)arr + 0x18) = 0; }
    return arr;
}

static ptr build_rows(const char *category, const char *cls)
{
    char *body = g_body;
    char path[192];
    char num[24];
    ptr klass, arr;
    const char *p, *end;
    int pl = 0, count = 0, i = 0;

    klass = resolve_row_klass();
    if (!klass) return 0;
    if (!body) { body = (char *)il2cpp_alloc(32768); g_body = body; }
    g_dbg[1] = (long)body;
    if (!body) return 0;

    str_cat(path, &pl, "/api/leaderboards/");
    str_cat(path, &pl, LB_MODE ? "season" : "normal");
    str_cat(path, &pl, "/");
    str_cat(path, &pl, category);
    str_cat(path, &pl, "?limit=50");
    if (cls) { str_cat(path, &pl, "&cls="); str_cat(path, &pl, cls); }
    path[pl] = 0;

    /* Never hand back a null array: the leaderboard window dereferences the result
     * unconditionally, so a null return (empty class board, or an unreachable feed)
     * crashed the client the moment such a tab was opened.  An empty typed array is
     * the safe equivalent of "no rows". */
    { int hl = http_get(path, body, 32768); g_dbg[2] = hl;
      if (hl <= 0) return empty_result(klass); }

    end = body;
    while (*end) end++;

    /* First pass: how many rows? */
    for (p = body; (p = find_str(p, end, "\"rank\":")) != 0; p += 7) count++;
    g_dbg[3] = count;
    if (count <= 0) return empty_result(klass);

    arr = il2cpp_array_new(klass, (u64)count);
    g_dbg[6] = (long)arr;
    if (!arr) return 0;

    p = body;
    while ((p = find_str(p, end, "\"rank\":")) != 0 && i < count) {
        const char *q;
        int rank, clsId = 0, isPlayer = 0, nameLen = 0;
        long value = 0;
        const char *name = 0;

        q = p + 7;
        rank = (int)atoi_span(q, 12);

        name = find_str(q, end, "\"name\":\"");
        if (!name) break;
        name += 8;
        {
            const char *e = name;
            while (e < end && *e != '"') e++;
            nameLen = (int)(e - name);
        }

        {
            const char *lv = find_str(q, end, "\"level\":");
            if (lv) value = atoi_span(lv + 8, 16);
        }
        {
            const char *ci = find_str(q, end, "\"classId\":");
            if (ci) clsId = (int)atoi_span(ci + 10, 8);
        }
        {
            const char *ip = find_str(q, end, "\"isPlayer\":");
            if (ip) isPlayer = (ip[11] == 't') ? 1 : 0;
        }
        (void)num;

        {
            ptr row = il2cpp_object_new(klass);
            if (i == 0) g_dbg[7] = (long)row;
            if (!row) break;
            fill_row(row, rank ? rank : (i + 1), name, nameLen, value, clsId, isPlayer);
            *(ptr *)((u8 *)arr + 0x20 + 8 * (u64)i) = row;
        }
        i++;
        p += 7;
    }

    /* The leaderboard window holds this result as a concrete List<FakeLeaderboardRow>
     * and reads it by object layout (IL2CPP field access does not go through the
     * IReadOnlyList<T> interface): List._items sits at +0x10 and List._size at +0x18,
     * whereas an array has ArrayBounds* at +0x10 and max_length at +0x18.  Reading an
     * array that way gave _size == max_length (which is why exactly 5 rows appeared)
     * and _items == the bounds pointer, i.e. garbage rows.
     *
     * Point _items back at this very array so the same object satisfies both layouts:
     *   List._items[i] -> array->vector[i] -> the FakeLeaderboardRow we filled in.
     * The array's own element access (vector at +0x20) is untouched, and max_length is
     * already the row count, so it doubles as List._size. */
    *(ptr *)((u8 *)arr + 0x10) = arr;
    *(i64 *)((u8 *)arr + 0x18) = (i64)count;

    /* Trailing entries keep their default (zeroed) values only if the feed was
     * shorter than the announced count; the server never does that. */
    return arr;
}

ptr lb_overall(int seed)
{
    (void)seed;
    ptr r = build_rows("overall", 0);
    return r;
}

ptr lb_class(int seed, int classId)
{
    static const char *names[8] = { "warrior", "paladin", "assassin", "barbarian",
                                    "hunter", "mage", "necromancer", "priest" };
    (void)seed;
    if (classId < 0 || classId > 7) return 0;
    return build_rows("class", names[classId]);
}

ptr lb_helheim(int seed)
{
    (void)seed;
    return build_rows("helheim", 0);
}
