// d3d9.dll proxy for Alien Shooter (Sigma Team, Steam 2009 build).
//
// The game draws its maps at 1:1 pixel size, and levels 1 and 11 are only 2000 px wide,
// so a native ultrawide resolution shows a black strip. This proxy lets the game render
// at a small resolution (default 1720x720, half of 3440x1440) into an offscreen render
// target and scales that up into a borderless window covering the monitor.
//
// What it does:
//  - Patches the game's display-mode picker in memory so it uses RenderWidth x RenderHeight.
//  - Turns the game's fullscreen device into a windowed device the size of the monitor.
//  - Hands the game an offscreen render target as its "back buffer" and stretches it on Present.
//  - Maps mouse messages and window-size queries back to the game's resolution.
//
// Settings live in d3d9_upscale.ini next to the game executable.

#define WIN32_LEAN_AND_MEAN
#define CINTERFACE
#define COBJMACROS
#include <windows.h>
#include <d3d9.h>
#include <stdio.h>
#include <stdarg.h>
#include <share.h>

// ---------------------------------------------------------------- config / log

static struct {
    int enabled;
    int renderW, renderH;
    int linear;        // 0 = point (sharp pixels), 1 = bilinear
    int integerScale;  // 1 = only whole-number scale factors
    int clipCursor;    // keep the mouse on the game monitor while it is active
    int testMode;      // off-screen, no focus stealing, no cursor moves (for automated tests)
    int log;
    int outputW, outputH; // 0 = use the desktop resolution (borderless window)
    char monitor[32];     // e.g. "\\.\DISPLAY2"; empty = primary monitor
} cfg;

static char g_dir[MAX_PATH];
static FILE *g_log;

static void Log(const char *fmt, ...)
{
    va_list ap;
    if (!cfg.log) return;
    if (!g_log) {
        char path[MAX_PATH];
        sprintf_s(path, sizeof path, "%sd3d9_upscale.log", g_dir);
        g_log = _fsopen(path, "w", _SH_DENYNO);
        if (!g_log) return;
    }
    va_start(ap, fmt);
    vfprintf(g_log, fmt, ap);
    va_end(ap);
    fputc('\n', g_log);
    fflush(g_log);
}

static void LoadConfig(void)
{
    char ini[MAX_PATH];
    sprintf_s(ini, sizeof ini, "%sd3d9_upscale.ini", g_dir);
    cfg.enabled      = GetPrivateProfileIntA("upscale", "Enabled", 1, ini);
    cfg.renderW      = GetPrivateProfileIntA("upscale", "RenderWidth", 1720, ini);
    cfg.renderH      = GetPrivateProfileIntA("upscale", "RenderHeight", 720, ini);
    cfg.linear       = GetPrivateProfileIntA("upscale", "LinearFilter", 0, ini);
    cfg.integerScale = GetPrivateProfileIntA("upscale", "IntegerScale", 0, ini);
    cfg.clipCursor   = GetPrivateProfileIntA("upscale", "ClipCursor", 1, ini);
    cfg.testMode     = GetPrivateProfileIntA("upscale", "TestMode", 0, ini);
    cfg.log          = GetPrivateProfileIntA("upscale", "Log", 1, ini);
    cfg.outputW      = GetPrivateProfileIntA("upscale", "OutputWidth", 0, ini);
    cfg.outputH      = GetPrivateProfileIntA("upscale", "OutputHeight", 0, ini);
    GetPrivateProfileStringA("upscale", "Monitor", "", cfg.monitor, sizeof cfg.monitor, ini);
    if (cfg.renderW < 320) cfg.renderW = 320;
    if (cfg.renderH < 240) cfg.renderH = 240;
}

// ---------------------------------------------------------------- real d3d9

typedef IDirect3D9 *(WINAPI *PFN_Create9)(UINT);
static HMODULE g_real;
static PFN_Create9 p_Direct3DCreate9;

#define FWD_LIST(X) \
    X(D3DPERF_BeginEvent) X(D3DPERF_EndEvent) X(D3DPERF_GetStatus) X(D3DPERF_QueryRepeatFrame) \
    X(D3DPERF_SetMarker) X(D3DPERF_SetOptions) X(D3DPERF_SetRegion) X(DebugSetLevel) X(DebugSetMute) \
    X(Direct3D9EnableMaximizedWindowedModeShim) X(Direct3DCreate9Ex) X(Direct3DCreate9On12) \
    X(Direct3DCreate9On12Ex) X(Direct3DShaderValidatorCreate9) X(PSGPError) X(PSGPSampleTexture)

#define DECL_FWD(n) static FARPROC p_##n; __declspec(naked) void Stub_##n(void) { __asm { jmp dword ptr [p_##n] } }
FWD_LIST(DECL_FWD)

static void LoadReal(void)
{
    char path[MAX_PATH];
    GetSystemDirectoryA(path, MAX_PATH);   // WOW64 redirects this to SysWOW64
    strcat_s(path, sizeof path, "\\d3d9.dll");
    g_real = LoadLibraryA(path);
    if (!g_real) return;
    p_Direct3DCreate9 = (PFN_Create9)GetProcAddress(g_real, "Direct3DCreate9");
#define RESOLVE(n) p_##n = GetProcAddress(g_real, #n);
    FWD_LIST(RESOLVE)
}

// ---------------------------------------------------------------- geometry

static HWND g_hwnd;
static WNDPROC g_origProc;
static RECT g_target;        // window rect, screen coordinates
static RECT g_dst;           // where the image goes inside the client area
static int g_gameW, g_gameH; // size of the game's back buffer
static int g_exclusive;      // 1 = real fullscreen with a display mode change (OutputWidth/Height)

static HMONITOR g_monitor;

static BOOL CALLBACK FindMonitor(HMONITOR m, HDC dc, LPRECT r, LPARAM found)
{
    MONITORINFOEXA mi;
    mi.cbSize = sizeof mi;
    if (GetMonitorInfoA(m, (MONITORINFO *)&mi) && !_stricmp(mi.szDevice, cfg.monitor)) {
        *(HMONITOR *)found = m;
        return FALSE;
    }
    return TRUE;
}

static void ComputeTarget(void)
{
    POINT origin = {0, 0};
    MONITORINFO mi = {sizeof mi};
    HMONITOR m = NULL;
    if (cfg.monitor[0]) EnumDisplayMonitors(NULL, NULL, FindMonitor, (LPARAM)&m);
    if (!m) m = MonitorFromPoint(origin, MONITOR_DEFAULTTOPRIMARY);
    g_monitor = m;
    GetMonitorInfoA(m, &mi);
    g_target = mi.rcMonitor;
    g_exclusive = 0;
    if (cfg.testMode) {
        OffsetRect(&g_target, 20000 - g_target.left, 0);   // off-screen, positive (game reads x unsigned)
    } else if (cfg.outputW > 0 && cfg.outputH > 0 &&
               (cfg.outputW != g_target.right - g_target.left || cfg.outputH != g_target.bottom - g_target.top)) {
        // A different output resolution needs a real display mode change; the monitor
        // keeps its top-left corner and becomes outputW x outputH.
        g_exclusive = 1;
        g_target.right = g_target.left + cfg.outputW;
        g_target.bottom = g_target.top + cfg.outputH;
    }
}

static void ComputeDst(void)
{
    int cw = g_target.right - g_target.left, ch = g_target.bottom - g_target.top;
    int gw = g_gameW ? g_gameW : cfg.renderW, gh = g_gameH ? g_gameH : cfg.renderH;
    double s = min((double)cw / gw, (double)ch / gh);
    int dw, dh;
    if (cfg.integerScale && s >= 1.0) s = (double)(int)s;
    dw = (int)(gw * s + 0.5); dh = (int)(gh * s + 0.5);
    g_dst.left = (cw - dw) / 2; g_dst.top = (ch - dh) / 2;
    g_dst.right = g_dst.left + dw; g_dst.bottom = g_dst.top + dh;
}

static int Active(void) { return cfg.enabled && g_hwnd != NULL; }

static LPARAM ToGame(LPARAM lp)
{
    int x = (short)LOWORD(lp), y = (short)HIWORD(lp);
    int dw = g_dst.right - g_dst.left, dh = g_dst.bottom - g_dst.top;
    int gw = g_gameW ? g_gameW : cfg.renderW, gh = g_gameH ? g_gameH : cfg.renderH;
    if (dw <= 0 || dh <= 0) return lp;
    x = MulDiv(x - g_dst.left, gw, dw);
    y = MulDiv(y - g_dst.top, gh, dh);
    if (x < 0) x = 0; if (x >= gw) x = gw - 1;
    if (y < 0) y = 0; if (y >= gh) y = gh - 1;
    return MAKELPARAM(x, y);
}

// The game sees a "virtual" screen: a game-sized screen whose window sits at 0,0, whatever
// monitor it is really on (the game reads coordinates as unsigned 16-bit, so this also keeps
// monitors at negative positions working). Real point = target + dst offset + virtual * scale.
static void RealToVirtual(POINT *p)
{
    int gw = g_gameW ? g_gameW : cfg.renderW, gh = g_gameH ? g_gameH : cfg.renderH;
    int dw = g_dst.right - g_dst.left, dh = g_dst.bottom - g_dst.top;
    if (dw <= 0 || dh <= 0) return;
    p->x = MulDiv(p->x - g_target.left - g_dst.left, gw, dw);
    p->y = MulDiv(p->y - g_target.top - g_dst.top, gh, dh);
}

static void VirtualToReal(POINT *p)
{
    int gw = g_gameW ? g_gameW : cfg.renderW, gh = g_gameH ? g_gameH : cfg.renderH;
    p->x = g_target.left + g_dst.left + MulDiv(p->x, g_dst.right - g_dst.left, gw);
    p->y = g_target.top + g_dst.top + MulDiv(p->y, g_dst.bottom - g_dst.top, gh);
}

static int IsMouseMsg(UINT m)
{
    return (m >= WM_MOUSEMOVE && m <= WM_MBUTTONDBLCLK && m != WM_MOUSEWHEEL) ||
           (m >= WM_XBUTTONDOWN && m <= WM_XBUTTONDBLCLK);
}

// ---------------------------------------------------------------- window hooks

static void UpdateClip(int active)
{
    if (!cfg.clipCursor || cfg.testMode) return;
    if (active) ClipCursor(&g_target); else ClipCursor(NULL);
}

static LRESULT CALLBACK HookProc(HWND h, UINT msg, WPARAM wp, LPARAM lp)
{
    static int nlog;
    if (cfg.log >= 2 && nlog < 300 && msg != WM_SETCURSOR && msg != WM_NCHITTEST) {
        nlog++;
        Log("msg %04x wp=%08lx lp=%08lx", msg, (unsigned long)wp, (unsigned long)lp);
    }
    switch (msg) {
    case WM_NCHITTEST: {
        // The game tracks the cursor from WM_NCHITTEST (screen coordinates minus GetWindowRect).
        // It gets virtual coordinates; the hit-test answer is computed from the real point.
        POINT p, real;
        LRESULT r;
        p.x = real.x = (short)LOWORD(lp); p.y = real.y = (short)HIWORD(lp);
        RealToVirtual(&p);
        r = CallWindowProcA(g_origProc, h, msg, wp, MAKELPARAM((WORD)p.x, (WORD)p.y));
        return PtInRect(&g_target, real) ? HTCLIENT : r;
    }
    case WM_SIZE:
        if (wp != SIZE_MINIMIZED)
            lp = MAKELPARAM(g_gameW ? g_gameW : cfg.renderW, g_gameH ? g_gameH : cfg.renderH);
        break;
    case WM_WINDOWPOSCHANGING: {
        WINDOWPOS *p = (WINDOWPOS *)lp;
        if (p->x > -32000 && !IsIconic(h)) {
            p->x = g_target.left; p->y = g_target.top;
            p->cx = g_target.right - g_target.left; p->cy = g_target.bottom - g_target.top;
            p->flags &= ~(SWP_NOMOVE | SWP_NOSIZE);
        }
        break;
    }
    case WM_ACTIVATEAPP:
        if (cfg.testMode) wp = TRUE;
        UpdateClip((BOOL)wp);
        break;
    case WM_ACTIVATE:
        if (cfg.testMode) wp = WA_ACTIVE;
        break;
    case WM_KILLFOCUS:
        if (cfg.testMode) return 0;
        break;
    case WM_DESTROY:
        UpdateClip(0);
        break;
    }
    return CallWindowProcA(g_origProc, h, msg, wp, lp);
}

static void AdoptWindow(HWND h)
{
    if (!h || h == g_hwnd) return;
    g_hwnd = h;
    g_origProc = (WNDPROC)SetWindowLongPtrA(h, GWLP_WNDPROC, (LONG_PTR)HookProc);
    SetWindowLongA(h, GWL_STYLE, (GetWindowLongA(h, GWL_STYLE) & WS_VISIBLE) | WS_POPUP | WS_CLIPCHILDREN);
    if (cfg.testMode)
        SetWindowLongA(h, GWL_EXSTYLE, WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    else
        SetWindowLongA(h, GWL_EXSTYLE, GetWindowLongA(h, GWL_EXSTYLE) & ~(WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_DLGMODALFRAME));
    SetWindowPos(h, NULL, g_target.left, g_target.top, g_target.right - g_target.left,
                 g_target.bottom - g_target.top, SWP_NOZORDER | SWP_FRAMECHANGED | (cfg.testMode ? SWP_NOACTIVATE : 0));
    if (cfg.testMode) {
        PostMessageA(h, WM_ACTIVATEAPP, TRUE, 0);
        PostMessageA(h, WM_ACTIVATE, WA_ACTIVE, 0);
    } else if (GetForegroundWindow() == h) {
        UpdateClip(1);
    }
    Log("window %p adopted, target %ld,%ld %ldx%ld", h, g_target.left, g_target.top,
        g_target.right - g_target.left, g_target.bottom - g_target.top);
}

typedef HWND (WINAPI *PFN_CreateWindowExA)(DWORD, LPCSTR, LPCSTR, DWORD, int, int, int, int, HWND, HMENU, HINSTANCE, LPVOID);
typedef BOOL (WINAPI *PFN_Rect)(HWND, LPRECT);
typedef BOOL (WINAPI *PFN_Pt)(HWND, LPPOINT);
typedef BOOL (WINAPI *PFN_SetCursorPos)(int, int);
typedef HWND (WINAPI *PFN_GetFg)(void);
typedef BOOL (WINAPI *PFN_ShowWindow)(HWND, int);
static PFN_CreateWindowExA o_CreateWindowExA;
static PFN_Rect o_GetClientRect, o_GetWindowRect;
static PFN_Pt o_ClientToScreen;
static PFN_SetCursorPos o_SetCursorPos;
static PFN_GetFg o_GetForegroundWindow;
static PFN_ShowWindow o_ShowWindow;

static HWND WINAPI hk_CreateWindowExA(DWORD ex, LPCSTR cls, LPCSTR name, DWORD style, int x, int y, int w, int h,
                                      HWND parent, HMENU menu, HINSTANCE inst, LPVOID param)
{
    HWND r;
    int adopt = cfg.enabled && !parent && !g_hwnd;
    if (adopt) {
        style = (style & WS_VISIBLE) | WS_POPUP | WS_CLIPCHILDREN;
        ex = cfg.testMode ? (WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW) : 0;
        x = g_target.left; y = g_target.top;
        w = g_target.right - g_target.left; h = g_target.bottom - g_target.top;
    }
    r = o_CreateWindowExA(ex, cls, name, style, x, y, w, h, parent, menu, inst, param);
    if (adopt && r) AdoptWindow(r);
    return r;
}

static BOOL WINAPI hk_GetClientRect(HWND h, LPRECT r)
{
    if (Active() && h == g_hwnd && r) {
        r->left = r->top = 0;
        r->right = g_gameW ? g_gameW : cfg.renderW;
        r->bottom = g_gameH ? g_gameH : cfg.renderH;
        return TRUE;
    }
    return o_GetClientRect(h, r);
}

static BOOL WINAPI hk_GetWindowRect(HWND h, LPRECT r)
{
    if (Active() && h == g_hwnd && r) {
        r->left = r->top = 0;
        r->right = g_gameW ? g_gameW : cfg.renderW;
        r->bottom = g_gameH ? g_gameH : cfg.renderH;
        return TRUE;
    }
    return o_GetWindowRect(h, r);
}

static BOOL WINAPI hk_ClientToScreen(HWND h, LPPOINT p)
{
    if (Active() && h == g_hwnd && p) return TRUE;   // virtual screen: client == screen
    return o_ClientToScreen(h, p);
}

static BOOL WINAPI hk_SetCursorPos(int x, int y)
{
    POINT p;
    if (cfg.testMode) return TRUE;
    if (!Active()) return o_SetCursorPos(x, y);
    p.x = x; p.y = y;
    VirtualToReal(&p);
    return o_SetCursorPos(p.x, p.y);
}

typedef BOOL (WINAPI *PFN_PeekMessageA)(LPMSG, HWND, UINT, UINT, UINT);
static PFN_PeekMessageA o_PeekMessageA;

// The game reads the mouse from the MSG it pulls out of the queue (lParam and pt),
// so translate both before it sees them.
static BOOL WINAPI hk_PeekMessageA(LPMSG m, HWND h, UINT mn, UINT mx, UINT rm)
{
    BOOL r = o_PeekMessageA(m, h, mn, mx, rm);
    if (r && m && Active() && m->hwnd == g_hwnd) {
        if (IsMouseMsg(m->message)) {
            m->lParam = ToGame(m->lParam);
            m->pt.x = (short)LOWORD(m->lParam);
            m->pt.y = (short)HIWORD(m->lParam);
        } else if (!cfg.testMode) {
            RealToVirtual(&m->pt);
        }
    }
    return r;
}

static HWND WINAPI hk_GetForegroundWindow(void)
{
    static int n;
    if (cfg.log >= 2 && (n++ % 500) == 0) Log("GetForegroundWindow called (%d)", n);
    if (cfg.testMode && g_hwnd) return g_hwnd;
    return o_GetForegroundWindow();
}

static BOOL WINAPI hk_ShowWindow(HWND h, int cmd)
{
    if (cfg.testMode && h == g_hwnd && (cmd == SW_SHOW || cmd == SW_SHOWNORMAL || cmd == SW_RESTORE ||
                                        cmd == SW_SHOWMAXIMIZED || cmd == SW_SHOWDEFAULT))
        cmd = SW_SHOWNOACTIVATE;
    return o_ShowWindow(h, cmd);
}

static void HookIAT(const char *dll, const char *fn, void *hook, void **orig)
{
    BYTE *base = (BYTE *)GetModuleHandleA(NULL);
    IMAGE_NT_HEADERS *nt = (IMAGE_NT_HEADERS *)(base + ((IMAGE_DOS_HEADER *)base)->e_lfanew);
    IMAGE_DATA_DIRECTORY dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    IMAGE_IMPORT_DESCRIPTOR *d = (IMAGE_IMPORT_DESCRIPTOR *)(base + dir.VirtualAddress);
    for (; d->Name; d++) {
        IMAGE_THUNK_DATA *names, *addrs;
        if (_stricmp((char *)(base + d->Name), dll)) continue;
        names = (IMAGE_THUNK_DATA *)(base + (d->OriginalFirstThunk ? d->OriginalFirstThunk : d->FirstThunk));
        addrs = (IMAGE_THUNK_DATA *)(base + d->FirstThunk);
        for (; names->u1.AddressOfData; names++, addrs++) {
            DWORD old;
            if (IMAGE_SNAP_BY_ORDINAL(names->u1.Ordinal)) continue;
            if (strcmp((char *)((IMAGE_IMPORT_BY_NAME *)(base + names->u1.AddressOfData))->Name, fn)) continue;
            VirtualProtect(&addrs->u1.Function, sizeof(void *), PAGE_READWRITE, &old);
            *orig = (void *)addrs->u1.Function;
            addrs->u1.Function = (DWORD_PTR)hook;
            VirtualProtect(&addrs->u1.Function, sizeof(void *), old, &old);
            Log("hooked %s!%s", dll, fn);
            return;
        }
    }
    Log("could not hook %s!%s", dll, fn);
}

// ---------------------------------------------------------------- exe patch

// Inside the game's display-mode picker (RVAs of the Steam build, image base 0x400000):
//   0x2DC3B  push 0x500                 -> max mode width; 0 makes every mode fail the filter
//   0x2F879  mov [eax], 999999.0f       -> starting width, kept when no mode passes
//   0x2F882  mov [esi], 1.0f            -> starting height
static void PatchExe(void)
{
    BYTE *base = (BYTE *)GetModuleHandleA(NULL);
    BYTE *cap = base + 0x2DC3B, *pw = base + 0x2F879, *ph = base + 0x2F882;
    float w = (float)cfg.renderW, h = (float)cfg.renderH;
    DWORD zero = 0, old;
    __try {
        if (cap[0] != 0x68 || pw[0] != 0xC7 || pw[1] != 0x00 || ph[0] != 0xC7 || ph[1] != 0x06 ||
            cap[5] != 0xFF || cap[6] != 0xB7) {
            Log("exe signature mismatch, resolution patch skipped");
            return;
        }
        VirtualProtect(cap, 0x1000 + (ph - cap), PAGE_EXECUTE_READWRITE, &old);
        memcpy(cap + 1, &zero, 4);
        memcpy(pw + 2, &w, 4);
        memcpy(ph + 2, &h, 4);
        VirtualProtect(cap, 0x1000 + (ph - cap), old, &old);
        FlushInstructionCache(GetCurrentProcess(), cap, 0x1000 + (ph - cap));
        Log("exe patched: game resolution %dx%d", cfg.renderW, cfg.renderH);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        Log("exe patch failed");
    }
}

// ---------------------------------------------------------------- device wrapper
//
// The game gets a wrapper object instead of the real device. Patching the real device's
// vtable does not work here: another layer (Steam overlay) rewrites it shortly after creation.

typedef HRESULT (WINAPI *PFN_CreateDevice)(IDirect3D9 *, UINT, D3DDEVTYPE, HWND, DWORD, D3DPRESENT_PARAMETERS *, IDirect3DDevice9 **);
static PFN_CreateDevice o_CreateDevice;

typedef struct {
    IDirect3DDevice9Vtbl *lpVtbl;
    IDirect3DDevice9 *real;
} WrapDevice;

#define R(This) (((WrapDevice *)(This))->real)

static IDirect3DSurface9 *g_rt, *g_ds;
static D3DPRESENT_PARAMETERS g_gamePP;
static int g_frames;

static void Patch(void **slot, void *hook, void **orig)
{
    DWORD old;
    if (*slot == hook) return;
    VirtualProtect(slot, sizeof(void *), PAGE_READWRITE, &old);
    *orig = *slot;
    *slot = hook;
    VirtualProtect(slot, sizeof(void *), old, &old);
}

static void BuildRealPP(const D3DPRESENT_PARAMETERS *game, D3DPRESENT_PARAMETERS *real)
{
    *real = *game;
    real->BackBufferWidth = g_target.right - g_target.left;
    real->BackBufferHeight = g_target.bottom - g_target.top;
    real->BackBufferCount = 1;
    real->MultiSampleType = D3DMULTISAMPLE_NONE;
    real->MultiSampleQuality = 0;
    real->SwapEffect = D3DSWAPEFFECT_DISCARD;
    real->hDeviceWindow = g_hwnd;
    real->Windowed = !g_exclusive;
    if (g_exclusive) real->BackBufferFormat = D3DFMT_X8R8G8B8;
    real->EnableAutoDepthStencil = FALSE;
    real->Flags &= ~(D3DPRESENTFLAG_LOCKABLE_BACKBUFFER | D3DPRESENTFLAG_DISCARD_DEPTHSTENCIL);
    real->FullScreen_RefreshRateInHz = 0;
    if (real->PresentationInterval != D3DPRESENT_INTERVAL_IMMEDIATE)
        real->PresentationInterval = D3DPRESENT_INTERVAL_ONE;
}

static void ReleaseTargets(void)
{
    if (g_rt) { IDirect3DSurface9_Release(g_rt); g_rt = NULL; }
    if (g_ds) { IDirect3DSurface9_Release(g_ds); g_ds = NULL; }
}

static HRESULT CreateTargets(IDirect3DDevice9 *dev)
{
    D3DFORMAT fmt = g_gamePP.BackBufferFormat == D3DFMT_UNKNOWN ? D3DFMT_X8R8G8B8 : g_gamePP.BackBufferFormat;
    HRESULT hr;
    g_gameW = g_gamePP.BackBufferWidth ? g_gamePP.BackBufferWidth : cfg.renderW;
    g_gameH = g_gamePP.BackBufferHeight ? g_gamePP.BackBufferHeight : cfg.renderH;
    ComputeDst();
    hr = IDirect3DDevice9_CreateRenderTarget(dev, g_gameW, g_gameH, fmt, D3DMULTISAMPLE_NONE, 0,
            (g_gamePP.Flags & D3DPRESENTFLAG_LOCKABLE_BACKBUFFER) != 0, &g_rt, NULL);
    if (FAILED(hr)) { Log("CreateRenderTarget failed %08lx", hr); return hr; }
    if (g_gamePP.EnableAutoDepthStencil) {
        hr = IDirect3DDevice9_CreateDepthStencilSurface(dev, g_gameW, g_gameH, g_gamePP.AutoDepthStencilFormat,
                D3DMULTISAMPLE_NONE, 0, FALSE, &g_ds, NULL);
        if (FAILED(hr)) { Log("CreateDepthStencilSurface failed %08lx", hr); return hr; }
    }
    IDirect3DDevice9_SetRenderTarget(dev, 0, g_rt);
    IDirect3DDevice9_SetDepthStencilSurface(dev, g_ds);
    Log("render target %dx%d -> output %ldx%ld at %ld,%ld", g_gameW, g_gameH,
        g_dst.right - g_dst.left, g_dst.bottom - g_dst.top, g_dst.left, g_dst.top);
    return D3D_OK;
}

// Test hook: when d3d9_upscale_capture.req exists, the next frame is saved as a BMP.
static void SaveCapture(IDirect3DDevice9 *dev, IDirect3DSurface9 *bb)
{
    char req[MAX_PATH], out[MAX_PATH];
    D3DSURFACE_DESC d;
    IDirect3DSurface9 *sys = NULL;
    D3DLOCKED_RECT lr;
    FILE *f;
    sprintf_s(req, sizeof req, "%sd3d9_upscale_capture.req", g_dir);
    if (GetFileAttributesA(req) == INVALID_FILE_ATTRIBUTES) return;
    DeleteFileA(req);
    sprintf_s(out, sizeof out, "%sd3d9_upscale_capture.bmp", g_dir);
    IDirect3DSurface9_GetDesc(bb, &d);
    if (FAILED(IDirect3DDevice9_CreateOffscreenPlainSurface(dev, d.Width, d.Height, d.Format, D3DPOOL_SYSTEMMEM, &sys, NULL))) {
        Log("capture: CreateOffscreenPlainSurface failed");
        return;
    }
    if (SUCCEEDED(IDirect3DDevice9_GetRenderTargetData(dev, bb, sys)) &&
        SUCCEEDED(IDirect3DSurface9_LockRect(sys, &lr, NULL, D3DLOCK_READONLY))) {
        if (fopen_s(&f, out, "wb") == 0) {
            BITMAPFILEHEADER fh = {0};
            BITMAPINFOHEADER ih = {0};
            UINT y;
            ih.biSize = sizeof ih; ih.biWidth = d.Width; ih.biHeight = -(LONG)d.Height;
            ih.biPlanes = 1; ih.biBitCount = 32; ih.biCompression = BI_RGB;
            fh.bfType = 0x4D42; fh.bfOffBits = sizeof fh + sizeof ih;
            fh.bfSize = fh.bfOffBits + d.Width * 4 * d.Height;
            fwrite(&fh, sizeof fh, 1, f); fwrite(&ih, sizeof ih, 1, f);
            for (y = 0; y < d.Height; y++) fwrite((BYTE *)lr.pBits + y * lr.Pitch, 4, d.Width, f);
            fclose(f);
            Log("capture saved %ux%u", d.Width, d.Height);
        }
        IDirect3DSurface9_UnlockRect(sys);
    } else {
        Log("capture: readback failed");
    }
    IDirect3DSurface9_Release(sys);
}

static HRESULT WINAPI W_QueryInterface(IDirect3DDevice9 *This, REFIID riid, void **out)
{
    if (out && (IsEqualGUID(riid, &IID_IUnknown) || IsEqualGUID(riid, &IID_IDirect3DDevice9))) {
        IDirect3DDevice9_AddRef(R(This));
        *out = This;
        return S_OK;
    }
    return IDirect3DDevice9_QueryInterface(R(This), riid, out);
}

static ULONG WINAPI W_AddRef(IDirect3DDevice9 *This)
{
    return IDirect3DDevice9_AddRef(R(This));
}

static ULONG WINAPI W_Release(IDirect3DDevice9 *This)
{
    IDirect3DDevice9 *real = R(This);
    ULONG n = IDirect3DDevice9_Release(real);
    if (n == 0) HeapFree(GetProcessHeap(), 0, This);
    return n;
}

static HRESULT WINAPI W_Present(IDirect3DDevice9 *This, const RECT *src, const RECT *dst, HWND wnd, const RGNDATA *dirty)
{
    IDirect3DDevice9 *dev = R(This);
    IDirect3DSurface9 *bb = NULL;
    static int count;
    if (++count == 1 || count == 1000) Log("Present #%d", count);
    if (g_rt && SUCCEEDED(IDirect3DDevice9_GetBackBuffer(dev, 0, 0, D3DBACKBUFFER_TYPE_MONO, &bb))) {
        int full = g_dst.left == 0 && g_dst.top == 0 &&
                   g_dst.right == g_target.right - g_target.left && g_dst.bottom == g_target.bottom - g_target.top;
        if (!full) IDirect3DDevice9_ColorFill(dev, bb, NULL, D3DCOLOR_XRGB(0, 0, 0));
        IDirect3DDevice9_StretchRect(dev, g_rt, NULL, bb, &g_dst, cfg.linear ? D3DTEXF_LINEAR : D3DTEXF_POINT);
        if ((++g_frames & 15) == 0) SaveCapture(dev, bb);
        IDirect3DSurface9_Release(bb);
        return IDirect3DDevice9_Present(dev, NULL, NULL, NULL, NULL);
    }
    return IDirect3DDevice9_Present(dev, src, dst, wnd, dirty);
}

static HRESULT WINAPI W_Reset(IDirect3DDevice9 *This, D3DPRESENT_PARAMETERS *pp)
{
    D3DPRESENT_PARAMETERS real;
    HRESULT hr;
    ReleaseTargets();
    g_gamePP = *pp;
    BuildRealPP(pp, &real);
    hr = IDirect3DDevice9_Reset(R(This), &real);
    Log("Reset %ux%u windowed=%d -> %08lx", pp->BackBufferWidth, pp->BackBufferHeight, pp->Windowed, hr);
    if (SUCCEEDED(hr)) CreateTargets(R(This));
    return hr;
}

static HRESULT WINAPI W_GetBackBuffer(IDirect3DDevice9 *This, UINT sc, UINT i, D3DBACKBUFFER_TYPE t, IDirect3DSurface9 **out)
{
    if (g_rt && sc == 0 && i == 0 && out) {
        IDirect3DSurface9_AddRef(g_rt);
        *out = g_rt;
        return D3D_OK;
    }
    return IDirect3DDevice9_GetBackBuffer(R(This), sc, i, t, out);
}

static HRESULT WINAPI W_GetDisplayMode(IDirect3DDevice9 *This, UINT sc, D3DDISPLAYMODE *m)
{
    HRESULT hr = IDirect3DDevice9_GetDisplayMode(R(This), sc, m);
    if (SUCCEEDED(hr) && g_rt) { m->Width = g_gameW; m->Height = g_gameH; }
    return hr;
}

static HRESULT WINAPI W_GetFrontBufferData(IDirect3DDevice9 *This, UINT sc, IDirect3DSurface9 *dest)
{
    if (g_rt) return IDirect3DDevice9_GetRenderTargetData(R(This), g_rt, dest);
    return IDirect3DDevice9_GetFrontBufferData(R(This), sc, dest);
}

static HRESULT WINAPI W_GetSwapChain(IDirect3DDevice9 *This, UINT i, IDirect3DSwapChain9 **out)
{
    static int logged;
    if (!logged++) Log("game called GetSwapChain(%u) - not wrapped", i);
    return IDirect3DDevice9_GetSwapChain(R(This), i, out);
}

#include "devwrap.h"

static HRESULT WINAPI hk_CreateDevice(IDirect3D9 *self, UINT adapter, D3DDEVTYPE type, HWND focus, DWORD flags,
                                      D3DPRESENT_PARAMETERS *pp, IDirect3DDevice9 **out)
{
    D3DPRESENT_PARAMETERS real;
    IDirect3DDevice9 *dev = NULL;
    WrapDevice *w;
    HRESULT hr;
    Log("CreateDevice game wants %ux%u fmt=%d windowed=%d depth=%d/%d interval=%x",
        pp->BackBufferWidth, pp->BackBufferHeight, pp->BackBufferFormat, pp->Windowed,
        pp->EnableAutoDepthStencil, pp->AutoDepthStencilFormat, pp->PresentationInterval);
    if (!cfg.enabled || g_rt) return o_CreateDevice(self, adapter, type, focus, flags, pp, out);
    AdoptWindow(pp->hDeviceWindow ? pp->hDeviceWindow : focus);
    {
        UINT i, n = self->lpVtbl->GetAdapterCount(self);
        for (i = 0; i < n; i++)
            if (self->lpVtbl->GetAdapterMonitor(self, i) == g_monitor) { adapter = i; break; }
        Log("monitor '%s' -> adapter %u", cfg.monitor, adapter);
    }
    g_gamePP = *pp;
    BuildRealPP(pp, &real);
    hr = o_CreateDevice(self, adapter, type, focus, flags, &real, &dev);
    Log("CreateDevice %s %ux%u -> %08lx", g_exclusive ? "fullscreen" : "windowed",
        real.BackBufferWidth, real.BackBufferHeight, hr);
    if (FAILED(hr) && g_exclusive) {
        // Display mode not available (e.g. 4K on a 1440p monitor): fall back to the desktop size.
        cfg.outputW = cfg.outputH = 0;
        ComputeTarget();
        SetWindowPos(g_hwnd, NULL, g_target.left, g_target.top, g_target.right - g_target.left,
                     g_target.bottom - g_target.top, SWP_NOZORDER | SWP_NOACTIVATE);
        BuildRealPP(pp, &real);
        hr = o_CreateDevice(self, adapter, type, focus, flags, &real, &dev);
        Log("fallback CreateDevice windowed %ux%u -> %08lx", real.BackBufferWidth, real.BackBufferHeight, hr);
    }
    if (FAILED(hr)) return o_CreateDevice(self, adapter, type, focus, flags, pp, out);
    if (FAILED(CreateTargets(dev))) {
        ReleaseTargets();
        *out = dev;
        return hr;
    }
    w = (WrapDevice *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof *w);
    w->lpVtbl = &g_wrapVtbl;
    w->real = dev;
    *out = (IDirect3DDevice9 *)w;
    return hr;
}

IDirect3D9 *WINAPI MyDirect3DCreate9(UINT sdk)
{
    IDirect3D9 *d3d;
    if (!p_Direct3DCreate9) return NULL;
    d3d = p_Direct3DCreate9(sdk);
    if (d3d && cfg.enabled)
        Patch((void **)&d3d->lpVtbl->CreateDevice, hk_CreateDevice, (void **)&o_CreateDevice);
    return d3d;
}

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, LPVOID reserved)
{
    if (reason == DLL_PROCESS_ATTACH) {
        char *slash;
        DisableThreadLibraryCalls(inst);
        GetModuleFileNameA(NULL, g_dir, MAX_PATH);
        slash = strrchr(g_dir, '\\');
        if (slash) slash[1] = 0;
        LoadConfig();
        LoadReal();
        Log("d3d9 upscaler loaded (enabled=%d render=%dx%d test=%d)", cfg.enabled, cfg.renderW, cfg.renderH, cfg.testMode);
        if (cfg.enabled) {
            ComputeTarget();
            PatchExe();
            HookIAT("USER32.dll", "CreateWindowExA", hk_CreateWindowExA, (void **)&o_CreateWindowExA);
            HookIAT("USER32.dll", "GetClientRect", hk_GetClientRect, (void **)&o_GetClientRect);
            HookIAT("USER32.dll", "GetWindowRect", hk_GetWindowRect, (void **)&o_GetWindowRect);
            HookIAT("USER32.dll", "ClientToScreen", hk_ClientToScreen, (void **)&o_ClientToScreen);
            HookIAT("USER32.dll", "SetCursorPos", hk_SetCursorPos, (void **)&o_SetCursorPos);
            HookIAT("USER32.dll", "GetForegroundWindow", hk_GetForegroundWindow, (void **)&o_GetForegroundWindow);
            HookIAT("USER32.dll", "ShowWindow", hk_ShowWindow, (void **)&o_ShowWindow);
            HookIAT("USER32.dll", "PeekMessageA", hk_PeekMessageA, (void **)&o_PeekMessageA);
        }
    } else if (reason == DLL_PROCESS_DETACH) {
        if (g_log) fclose(g_log);
    }
    return TRUE;
}
