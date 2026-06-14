// Faolan client hook DLL (32-bit, injected into AgeOfConan.exe under Wine/Proton).
// Stage 1 (this file): prove injection + file output works — on load, write the process module base
// address + a marker to faolan_hook.log (in the client's working dir). No game hooks yet (added once the
// injection/output pipeline is confirmed in-client). Built with pure LLVM (clang + lld-link), no CRT.
//
// We declare the few kernel32 imports ourselves (no windows.h / no Windows SDK).

typedef unsigned int   DWORD;
typedef unsigned short WORD;
typedef int            BOOL;
typedef void          *HANDLE;
typedef const char    *LPCSTR;

#define WINAPI __stdcall
#define DLL_PROCESS_ATTACH 1
#define GENERIC_WRITE 0x40000000u
#define CREATE_ALWAYS 2
#define FILE_ATTRIBUTE_NORMAL 0x80
#define INVALID_HANDLE_VALUE ((HANDLE)-1)

__declspec(dllimport) HANDLE WINAPI CreateFileA(LPCSTR, DWORD, DWORD, void *, DWORD, DWORD, HANDLE);
__declspec(dllimport) BOOL   WINAPI WriteFile(HANDLE, const void *, DWORD, DWORD *, void *);
__declspec(dllimport) BOOL   WINAPI CloseHandle(HANDLE);
__declspec(dllimport) HANDLE WINAPI GetModuleHandleA(LPCSTR);
__declspec(dllimport) DWORD  WINAPI GetCurrentProcessId(void);

static unsigned slen(const char *s) { unsigned n = 0; while (s[n]) n++; return n; }

// Append "<label>=0x<hex8>\n" into buf at *pos.
static void put_hex(char *buf, unsigned *pos, const char *label, DWORD v) {
    const char *hex = "0123456789abcdef";
    for (unsigned i = 0; label[i]; i++) buf[(*pos)++] = label[i];
    buf[(*pos)++] = '=';
    buf[(*pos)++] = '0'; buf[(*pos)++] = 'x';
    for (int sh = 28; sh >= 0; sh -= 4) buf[(*pos)++] = hex[(v >> sh) & 0xF];
    buf[(*pos)++] = '\n';
}

static void log_load(void) {
    char buf[128];
    unsigned pos = 0;
    const char *hdr = "faolan_hook loaded\n";
    for (unsigned i = 0; i < slen(hdr); i++) buf[pos++] = hdr[i];
    put_hex(buf, &pos, "exe_base", (DWORD)GetModuleHandleA(0));
    put_hex(buf, &pos, "pid", GetCurrentProcessId());

    HANDLE h = CreateFileA("faolan_hook.log", GENERIC_WRITE, 0, 0, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, 0);
    if (h == INVALID_HANDLE_VALUE) return;
    DWORD written = 0;
    WriteFile(h, buf, pos, &written, 0);
    CloseHandle(h);
}

BOOL WINAPI DllMain(HANDLE hinst, DWORD reason, void *reserved) {
    (void)hinst; (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) log_load();
    return 1;
}
