/* 10x experience + 5x item-drop quantity, as two entry-instruction hooks.
 *
 * Hooked
 * ------
 *   Monster.GetExperience(double level, double expMult)      VA 0x2A7A6E0
 *       returns the monster's base XP in d0; the hook multiplies it by XP_MULT.
 *       (The trailing .rodata double at 0x13874D0 would be the obvious knob, but
 *       it is shared by 16 call sites across the binary, so it is hooked instead.)
 *
 *   ItemGenerator.QueueRandomLoot(..., int32 numItems, ...)  VA 0x2C8FE9C
 *       numItems is argument 4 (w3); the hook multiplies it by DROP_MULT.  This
 *       is the base number of items rolled by every loot path: monster deaths
 *       (DeathPayload.Apply), chests (LootChest.OpenChest) and the frost-run /
 *       guild-siege / welcome-back reward generators.
 *
 * Both hooks replay the displaced entry instruction(s) and branch back into the
 * original body, so the retail code runs unmodified.  There is no writable
 * state, hence no .bss.
 *
 * Addresses are linked libil2cpp 1.9.3 arm64-v8a VAs (ASLR-safe).
 */
typedef void *ptr;

#define XP_MULT   10
#define DROP_MULT  5

#define STR_(x) #x
#define STR(x)  STR_(x)

/* ---- Monster.GetExperience -------------------------------------------------
 * prologue (12 bytes):
 *   2a7a6e0  stp d9, d8, [sp, #-0x30]!
 *   2a7a6e4  str x30, [sp, #0x10]
 *   2a7a6e8  stp x20, x19, [sp, #0x20]
 *   2a7a6ec  fmov d8, d1              <- XP_RESUME
 */
__attribute__((naked)) void xp_body_impl(void) {
    __asm__ volatile(
        "stp d9, d8, [sp, #-0x30]!\n"
        "str x30, [sp, #0x10]\n"
        "stp x20, x19, [sp, #0x20]\n"
        "b XP_RESUME\n");
}

/* xp_hook: run the real body, then scale its d0 return value.  x29/x30 are
 * saved so the original's own prologue/epilogue stay balanced. */
__attribute__((naked)) void xp_hook(void) {
    __asm__ volatile(
        "stp x29, x30, [sp, #-0x10]!\n"
        "bl xp_body_impl\n"
        "fmov d1, #" STR(XP_MULT) ".0\n"
        "fmul d0, d0, d1\n"
        "ldp x29, x30, [sp], #0x10\n"
        "ret\n");
}

/* ---- ItemGenerator.QueueRandomLoot -----------------------------------------
 * prologue (4 bytes):
 *   2c8fe9c  sub sp, sp, #0xf0        <- replayed, then DROP_RESUME
 *   2c8fea0  stp d15, d14, [sp, #0x50]
 *
 * w9 is caller-saved (x9 is a temp register in the AArch64 PCS), so scaling w3
 * through it before the prologue cannot clobber anything the callee needs.
 */
__attribute__((naked)) void drop_hook(void) {
    __asm__ volatile(
        "mov w9, #" STR(DROP_MULT) "\n"
        "mul w3, w3, w9\n"
        "sub sp, sp, #0xf0\n"
        "b DROP_RESUME\n");
}
