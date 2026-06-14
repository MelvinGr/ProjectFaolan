// Faolan client hook DLL (32-bit, injected into AgeOfConan.exe under Wine/Proton).
// In-client verification without human eyes. Two mechanisms:
//
//  1) ENABLE THE CLIENT'S OWN DEBUG LOGGING (the broad, bulletproof one — no code patching). The client has
//     extensive internal logging (LogBase/_dbShouldOutput) gated off by default: the gate is
//     `DAT_021a2c29 != 0 && (DAT_029864f8 == 0 || _dbShouldOutput(channel))`. We set the master flag
//     DAT_021a2c29=1 and DAT_029864f8=0 (the OR short-circuits, bypassing the per-channel check), so the
//     client writes its full debug trace to AgeOfConan.log — quest activates ("got quest [id]"), IIR version
//     errors ("Wrong IIR_t version"), dispatch, inventory, combat, etc. "Test whatever we want" via the
//     client's own instrumentation. (Globals verified in writable .data; addresses are absolute — the exe is
//     NOT ASLR, ImageBase 0x400000.)
//
//  2) INLINE HOOKS (a prologue-verified framework). Each hook is installed ONLY if the target's current bytes
//     match the prologue we recovered from Ghidra (so a stale address can't corrupt anything — it just skips).
//     The detours read a simple field and log it, then run the original via a relocated trampoline.
//
// Built with pure LLVM (clang + lld-link, -fasm-blocks for the naked detours); freestanding, no CRT, kernel32
// prototypes declared inline (no windows.h). Output: faolan_hook.log in the client's working dir.

typedef unsigned int   DWORD;
typedef unsigned char  BYTE;
typedef int            BOOL;
typedef void          *HANDLE;
typedef const char    *LPCSTR;
typedef void          *LPVOID;

#define WINAPI __stdcall
#define DLL_PROCESS_ATTACH 1
#define GENERIC_WRITE 0x40000000u
#define CREATE_ALWAYS 2
#define FILE_ATTRIBUTE_NORMAL 0x80
#define INVALID_HANDLE_VALUE ((HANDLE)-1)
#define PAGE_EXECUTE_READWRITE 0x40
#define PAGE_READWRITE 0x04
#define MEM_COMMIT 0x1000
#define MEM_RESERVE 0x2000

__declspec(dllimport) HANDLE WINAPI CreateFileA(LPCSTR, DWORD, DWORD, void *, DWORD, DWORD, HANDLE);
__declspec(dllimport) BOOL   WINAPI WriteFile(HANDLE, const void *, DWORD, DWORD *, void *);
__declspec(dllimport) BOOL   WINAPI CloseHandle(HANDLE);
__declspec(dllimport) HANDLE WINAPI GetModuleHandleA(LPCSTR);
__declspec(dllimport) DWORD  WINAPI GetCurrentProcessId(void);
__declspec(dllimport) BOOL   WINAPI VirtualProtect(LPVOID, DWORD, DWORD, DWORD *);
__declspec(dllimport) LPVOID WINAPI VirtualAlloc(LPVOID, DWORD, DWORD, DWORD);

// ---------------------------------------------------------------------------- log helpers

static HANDLE g_log = INVALID_HANDLE_VALUE;

static unsigned slen(const char *s) { unsigned n = 0; if (s) while (s[n]) n++; return n; }

static void raw_write(const char *p, unsigned n) {
    if (g_log == INVALID_HANDLE_VALUE) return;
    DWORD w = 0;
    WriteFile(g_log, p, n, &w, 0);
}
static void logs(const char *s) { raw_write(s, slen(s)); }
static void log_hex(const char *label, DWORD v) {
    const char *hex = "0123456789abcdef";
    char b[40]; unsigned p = 0;
    for (unsigned i = 0; label[i]; i++) b[p++] = label[i];
    b[p++] = '='; b[p++] = '0'; b[p++] = 'x';
    for (int sh = 28; sh >= 0; sh -= 4) b[p++] = hex[(v >> sh) & 0xF];
    b[p++] = '\n';
    raw_write(b, p);
}

// ---------------------------------------------------------------------------- memory helpers

static void *mem_alloc_rwx(DWORD n) {
    return VirtualAlloc(0, n, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
}
static int bytes_match(const BYTE *a, const BYTE *b, unsigned n) {
    for (unsigned i = 0; i < n; i++) if (a[i] != b[i]) return 0;
    return 1;
}
static void mem_copy(BYTE *d, const BYTE *s, unsigned n) { for (unsigned i = 0; i < n; i++) d[i] = s[i]; }

// ---------------------------------------------------------------------------- 1) enable debug logging

static void enable_debug_logging(void) {
    DWORD old;
    if (VirtualProtect((LPVOID)0x021A2C29, 1, PAGE_READWRITE, &old)) {
        *(volatile BYTE *)0x021A2C29 = 1;   // DAT_021a2c29: master "debug logging on"
        VirtualProtect((LPVOID)0x021A2C29, 1, old, &old);
    }
    if (VirtualProtect((LPVOID)0x029864F8, 4, PAGE_READWRITE, &old)) {
        *(volatile DWORD *)0x029864F8 = 0;  // DAT_029864f8: 0 = bypass per-channel gate + vfprintf to log
        VirtualProtect((LPVOID)0x029864F8, 4, old, &old);
    }
    logs("enabled client debug logging (DAT_021a2c29=1, DAT_029864f8=0) -> AgeOfConan.log\n");
}

// ---------------------------------------------------------------------------- 2) inline-hook framework

// Install a 5-byte rel32 jmp at `target` to `detour`, after verifying the first `steal` bytes equal
// `expected` (so we never patch a stale/wrong address). The stolen bytes are relocated into a fresh RWX
// trampoline that ends with a jmp back to target+steal; *tramp_out receives it (the detour tail-jmps to it).
// All curated targets here begin with relocatable, position-independent prologue bytes (push ebp; mov ebp,esp;
// and esp,-8) so a straight copy is safe. Returns 1 on success, 0 if skipped (prologue mismatch / alloc fail).
static int install_hook(DWORD target, const BYTE *expected, unsigned steal, void *detour, void **tramp_out) {
    BYTE *t = (BYTE *)target;
    if (!bytes_match(t, expected, steal)) return 0; // address moved / wrong build -> skip, do not corrupt

    BYTE *tramp = (BYTE *)mem_alloc_rwx(steal + 5);
    if (!tramp) return 0;
    mem_copy(tramp, t, steal);
    tramp[steal] = 0xE9; // jmp rel32 back to target+steal
    *(int *)(tramp + steal + 1) = (int)(target + steal) - (int)(tramp + steal + 5);
    *tramp_out = tramp;

    DWORD old;
    if (!VirtualProtect(t, steal, PAGE_EXECUTE_READWRITE, &old)) return 0;
    t[0] = 0xE9; // jmp rel32 to detour
    *(int *)(t + 1) = (int)detour - (int)(target + 5);
    for (unsigned i = 5; i < steal; i++) t[i] = 0x90; // NOP any leftover stolen byte (never executed)
    VirtualProtect(t, steal, old, &old);
    return 1;
}

// ---- curated hook: QuestFullUpdateIIR::Activate @0x00918080 (thiscall, this=ECX). The activated quest id is
// *(*(this+0x2C)+0x0C). Fires when the client activates a quest from a QuestFullUpdate -> logs the id.
static const BYTE PROLOGUE_QUEST_ACTIVATE[6] = { 0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF8 };
static void *g_tramp_quest_activate = 0;

static void __cdecl on_quest_activate(DWORD quest_id) { log_hex("quest_activate", quest_id); }

__declspec(naked) static void detour_quest_activate(void) {
    __asm {
        pushad
        pushfd
        mov eax, dword ptr [ecx + 0x2C]
        test eax, eax
        jz   skip
        mov eax, dword ptr [eax + 0x0C]
        push eax
        call on_quest_activate
        add  esp, 4
    skip:
        popfd
        popad
        jmp  dword ptr [g_tramp_quest_activate]
    }
}

// ---------------------------------------------------------------------------- load

static void on_attach(void) {
    g_log = CreateFileA("faolan_hook.log", GENERIC_WRITE, 0, 0, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, 0);

    logs("faolan_hook loaded\n");
    log_hex("exe_base", (DWORD)GetModuleHandleA(0));
    log_hex("pid", GetCurrentProcessId());

    enable_debug_logging();

    if (install_hook(0x00918080, PROLOGUE_QUEST_ACTIVATE, 6, (void *)detour_quest_activate, &g_tramp_quest_activate))
        logs("hook installed: QuestFullUpdateIIR::Activate (quest_activate <id>)\n");
    else
        logs("hook SKIPPED: QuestFullUpdateIIR::Activate (prologue mismatch)\n");

    logs("ready.\n");
}

BOOL WINAPI DllMain(HANDLE hinst, DWORD reason, void *reserved) {
    (void)hinst; (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) on_attach();
    return 1;
}
