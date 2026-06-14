// Faolan client hook DLL (32-bit, injected into AgeOfConan.exe under Wine/Proton).
// In-client verification without human eyes. Three mechanisms:
//
//  1) ENABLE THE CLIENT'S OWN DEBUG LOGGING (broad, bulletproof — no code patching). The client has extensive
//     internal logging (LogBase/_dbShouldOutput) gated off by default: the gate is
//     `DAT_021a2c29 != 0 && (DAT_029864f8 == 0 || _dbShouldOutput(channel))`. We set DAT_021a2c29=1 and
//     DAT_029864f8=0 (the OR short-circuits, bypassing the per-channel check), so the client writes its full
//     debug trace to AgeOfConan.log — quest activates ("got quest [id]"), IIR version errors, etc.
//
//  2) DISPATCHER HOOKS — a full per-interface server->client PACKET FEED. Every `*InterfaceDispatch_t::
//     DispatchMethod(opcode, ...)` (thiscall; opcode = first stack arg) is hooked; we log `disp <iface> <op>`.
//     Covers GameChar (snapshot/quest/inventory/combat), Tradepost (AH/mail), CSPlayer (guild/team),
//     Player/Universe/Game (login flow). This is "see exactly which packets the client received + dispatched".
//
//  3) FIELD HOOKS — e.g. QuestFullUpdateIIR::Activate logs the activated quest id.
//
// Inline hooks are PROLOGUE-VERIFIED: a hook is installed only if the target's current bytes match the
// prologue recovered from Ghidra, so a stale/wrong address is skipped (never patched) — it can't corrupt the
// client. Addresses are absolute (exe is NOT ASLR, ImageBase 0x400000). Built with pure LLVM (clang
// -fasm-blocks naked detours + lld-link), freestanding/no-CRT; output: faolan_hook.log (client CWD).

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
__declspec(dllimport) HANDLE WINAPI GetModuleHandleA(LPCSTR);
__declspec(dllimport) DWORD  WINAPI GetCurrentProcessId(void);
__declspec(dllimport) BOOL   WINAPI VirtualProtect(LPVOID, DWORD, DWORD, DWORD *);
__declspec(dllimport) LPVOID WINAPI VirtualAlloc(LPVOID, DWORD, DWORD, DWORD);

// ---------------------------------------------------------------------------- log helpers

static HANDLE g_log = INVALID_HANDLE_VALUE;

static unsigned slen(const char *s) { unsigned n = 0; if (s) while (s[n]) n++; return n; }
static void raw_write(const char *p, unsigned n) {
    if (g_log == INVALID_HANDLE_VALUE) return;
    DWORD w = 0; WriteFile(g_log, p, n, &w, 0);
}
static void logs(const char *s) { raw_write(s, slen(s)); }
static unsigned put_str(char *b, unsigned p, const char *s) { for (unsigned i = 0; s[i]; i++) b[p++] = s[i]; return p; }
static unsigned put_hex(char *b, unsigned p, DWORD v) {
    const char *h = "0123456789abcdef";
    b[p++] = '0'; b[p++] = 'x';
    for (int sh = 28; sh >= 0; sh -= 4) b[p++] = h[(v >> sh) & 0xF];
    return p;
}
static void log_kv(const char *label, DWORD v) {
    char b[64]; unsigned p = 0;
    p = put_str(b, p, label); b[p++] = '='; p = put_hex(b, p, v); b[p++] = '\n';
    raw_write(b, p);
}
// "disp <iface> <op>\n"
static void log_disp(const char *iface, DWORD op) {
    char b[64]; unsigned p = 0;
    p = put_str(b, p, "disp "); p = put_str(b, p, iface); b[p++] = ' '; p = put_hex(b, p, op); b[p++] = '\n';
    raw_write(b, p);
}

// ---------------------------------------------------------------------------- memory helpers

static void *mem_alloc_rwx(DWORD n) { return VirtualAlloc(0, n, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE); }
static int bytes_match(const BYTE *a, const BYTE *b, unsigned n) { for (unsigned i = 0; i < n; i++) if (a[i] != b[i]) return 0; return 1; }
static void mem_copy(BYTE *d, const BYTE *s, unsigned n) { for (unsigned i = 0; i < n; i++) d[i] = s[i]; }

// ---------------------------------------------------------------------------- 1) enable debug logging

static void enable_debug_logging(void) {
    DWORD old;
    if (VirtualProtect((LPVOID)0x021A2C29, 1, PAGE_READWRITE, &old)) { *(volatile BYTE *)0x021A2C29 = 1; VirtualProtect((LPVOID)0x021A2C29, 1, old, &old); }
    if (VirtualProtect((LPVOID)0x029864F8, 4, PAGE_READWRITE, &old)) { *(volatile DWORD *)0x029864F8 = 0; VirtualProtect((LPVOID)0x029864F8, 4, old, &old); }
    logs("enabled client debug logging (DAT_021a2c29=1, DAT_029864f8=0) -> AgeOfConan.log\n");
}

// ---------------------------------------------------------------------------- 2) inline-hook framework

// 5-byte rel32 jmp at `target` -> `detour`, installed ONLY if the first `steal` bytes equal `expected`. The
// stolen (relocatable, position-independent) bytes go into a fresh RWX trampoline ending in a jmp back to
// target+steal; *tramp_out receives it. Returns 1 installed, 0 skipped.
static int install_hook(DWORD target, const BYTE *expected, unsigned steal, void *detour, void **tramp_out) {
    BYTE *t = (BYTE *)target;
    if (!bytes_match(t, expected, steal)) return 0;
    BYTE *tramp = (BYTE *)mem_alloc_rwx(steal + 5);
    if (!tramp) return 0;
    mem_copy(tramp, t, steal);
    tramp[steal] = 0xE9;
    *(int *)(tramp + steal + 1) = (int)(target + steal) - (int)(tramp + steal + 5);
    *tramp_out = tramp;
    DWORD old;
    if (!VirtualProtect(t, steal, PAGE_EXECUTE_READWRITE, &old)) return 0;
    t[0] = 0xE9;
    *(int *)(t + 1) = (int)detour - (int)(target + 5);
    for (unsigned i = 5; i < steal; i++) t[i] = 0x90;
    VirtualProtect(t, steal, old, &old);
    return 1;
}

// All curated targets begin with this prologue (push ebp; mov ebp,esp; and esp,-8) = 6 relocatable bytes.
static const BYTE PROLOGUE_STD[6] = { 0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF8 };

// ---- dispatcher hooks: *InterfaceDispatch_t::DispatchMethod(opcode, param2) — thiscall (this=ECX), opcode is
// the first STACK arg. After pushad(32)+pushfd(4), the original [esp+4] (opcode) sits at [esp+40]. We pass
// (iface_name_ptr, opcode) to on_dispatch (cdecl), then run the original via the trampoline.
static void __cdecl on_dispatch(const char *iface, DWORD opcode) { log_disp(iface, opcode); }

// One naked detour per dispatcher (clang's -fasm-blocks doesn't expand __asm{} inside a macro). Each: save all,
// read the opcode (original [esp+4] -> [esp+40] after pushad+pushfd), call on_dispatch(name, opcode), restore,
// tail-jmp to its trampoline.
static void *g_tramp_player = 0; static const char player_name[] = "Player";
__declspec(naked) static void detour_player(void) { __asm {
    pushad
    pushfd
    mov eax, dword ptr [esp + 40]
    push eax
    lea eax, player_name
    push eax
    call on_dispatch
    add esp, 8
    popfd
    popad
    jmp dword ptr [g_tramp_player]
} }

static void *g_tramp_universe = 0; static const char universe_name[] = "Universe";
__declspec(naked) static void detour_universe(void) { __asm {
    pushad
    pushfd
    mov eax, dword ptr [esp + 40]
    push eax
    lea eax, universe_name
    push eax
    call on_dispatch
    add esp, 8
    popfd
    popad
    jmp dword ptr [g_tramp_universe]
} }

static void *g_tramp_game = 0; static const char game_name[] = "Game";
__declspec(naked) static void detour_game(void) { __asm {
    pushad
    pushfd
    mov eax, dword ptr [esp + 40]
    push eax
    lea eax, game_name
    push eax
    call on_dispatch
    add esp, 8
    popfd
    popad
    jmp dword ptr [g_tramp_game]
} }

static void *g_tramp_gamechar = 0; static const char gamechar_name[] = "GameChar";
__declspec(naked) static void detour_gamechar(void) { __asm {
    pushad
    pushfd
    mov eax, dword ptr [esp + 40]
    push eax
    lea eax, gamechar_name
    push eax
    call on_dispatch
    add esp, 8
    popfd
    popad
    jmp dword ptr [g_tramp_gamechar]
} }

static void *g_tramp_gamecharpj = 0; static const char gamecharpj_name[] = "GameCharProj";
__declspec(naked) static void detour_gamecharpj(void) { __asm {
    pushad
    pushfd
    mov eax, dword ptr [esp + 40]
    push eax
    lea eax, gamecharpj_name
    push eax
    call on_dispatch
    add esp, 8
    popfd
    popad
    jmp dword ptr [g_tramp_gamecharpj]
} }

static void *g_tramp_csplayer = 0; static const char csplayer_name[] = "CSPlayer";
__declspec(naked) static void detour_csplayer(void) { __asm {
    pushad
    pushfd
    mov eax, dword ptr [esp + 40]
    push eax
    lea eax, csplayer_name
    push eax
    call on_dispatch
    add esp, 8
    popfd
    popad
    jmp dword ptr [g_tramp_csplayer]
} }

static void *g_tramp_tradepost = 0; static const char tradepost_name[] = "Tradepost";
__declspec(naked) static void detour_tradepost(void) { __asm {
    pushad
    pushfd
    mov eax, dword ptr [esp + 40]
    push eax
    lea eax, tradepost_name
    push eax
    call on_dispatch
    add esp, 8
    popfd
    popad
    jmp dword ptr [g_tramp_tradepost]
} }

// ---- field hook: QuestFullUpdateIIR::Activate @0x00918080 (thiscall, this=ECX). quest id = *(*(this+0x2C)+0x0C).
static void *g_tramp_quest_activate = 0;
static void __cdecl on_quest_activate(DWORD id) { log_kv("quest_activate", id); }
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

// ---------------------------------------------------------------------------- hook table + load

typedef struct { DWORD addr; const BYTE *prologue; unsigned steal; void *detour; void **tramp; const char *name; } HookSpec;

static const HookSpec g_hooks[] = {
    { 0x00484270, PROLOGUE_STD, 6, (void *)detour_player,        &g_tramp_player,        "PlayerInterfaceDispatch" },
    { 0x00490970, PROLOGUE_STD, 6, (void *)detour_universe,      &g_tramp_universe,      "UniverseInterfaceDispatch" },
    { 0x00491F30, PROLOGUE_STD, 6, (void *)detour_game,          &g_tramp_game,          "GameInterfaceDispatch" },
    { 0x004BC7B0, PROLOGUE_STD, 6, (void *)detour_gamechar,      &g_tramp_gamechar,      "GameCharInterfaceDispatch" },
    { 0x0115E430, PROLOGUE_STD, 6, (void *)detour_gamecharpj,    &g_tramp_gamecharpj,    "GameCharInterfaceProjectDispatch" },
    { 0x0133A6A0, PROLOGUE_STD, 6, (void *)detour_csplayer,      &g_tramp_csplayer,      "CSPlayerInterfaceDispatch" },
    { 0x01357130, PROLOGUE_STD, 6, (void *)detour_tradepost,     &g_tramp_tradepost,     "TradepostInterfaceDispatch" },
    { 0x00918080, PROLOGUE_STD, 6, (void *)detour_quest_activate,&g_tramp_quest_activate,"QuestFullUpdateIIR::Activate" },
};

static void on_attach(void) {
    g_log = CreateFileA("faolan_hook.log", GENERIC_WRITE, 0, 0, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, 0);
    logs("faolan_hook loaded\n");
    log_kv("exe_base", (DWORD)GetModuleHandleA(0));
    log_kv("pid", GetCurrentProcessId());

    enable_debug_logging();

    unsigned ok = 0, total = (unsigned)(sizeof(g_hooks) / sizeof(g_hooks[0]));
    for (unsigned i = 0; i < total; i++) {
        const HookSpec *h = &g_hooks[i];
        int done = install_hook(h->addr, h->prologue, h->steal, h->detour, h->tramp);
        char b[96]; unsigned p = 0;
        p = put_str(b, p, done ? "hook OK   " : "hook SKIP ");
        p = put_str(b, p, h->name); b[p++] = '\n';
        raw_write(b, p);
        if (done) ok++;
    }
    log_kv("hooks_installed", ok);
    logs("ready.\n");
}

BOOL WINAPI DllMain(HANDLE hinst, DWORD reason, void *reserved) {
    (void)hinst; (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) on_attach();
    return 1;
}
