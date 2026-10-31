// A real window, for putting a rendered frame on screen.
//
// Direct3D 12 renders offscreen here: a texture, a readback buffer, and a PNG
// on disk. That is enough to prove the renderer works, and it is not what a
// person means when they run a cube demo - they mean a window with a cube
// turning in it.
//
// This is that window. It is Win32 (`user32`, `gdi32`, `kernel32`), loaded at
// run time the same way the Direct3D 12 binding loads `d3d12.dll`: nothing is
// linked, so this header compiles on any system and a test can hand it a fake
// user32. The pixels come from the existing Direct3D 12 readback path and go to
// the window with `StretchDIBits`; there is no swap chain, which is the one
// thing a windowed renderer should eventually add (see docs/status.md).
//
// Everything here is deliberately small: one window class, up to four windows,
// no menus, no DPI scaling, no resizing logic beyond letting the picture scale
// to the client area.

#ifndef TERN_WINDOW_H
#define TERN_WINDOW_H

#include <stddef.h>
#include <stdint.h>

#define TERN_MAX_WINDOWS 4

// Win32 constants, spelled out rather than pulled from windows.h so the same
// numbers reach a fake user32 on a machine that has no windows.h at all.
#define TERN_WS_OVERLAPPEDWINDOW_ 0x00CF0000u
#define TERN_SW_SHOW_ 5
#define TERN_PM_REMOVE_ 0x0001u
#define TERN_WM_KEYDOWN_ 0x0100u
#define TERN_WM_CLOSE_ 0x0010u
#define TERN_WM_DESTROY_ 0x0002u
#define TERN_WM_QUIT_ 0x0012u
#define TERN_VK_ESCAPE_ 0x1Bu
#define TERN_SRCCOPY_ 0x00CC0020u
#define TERN_DIB_RGB_COLORS_ 0
#define TERN_BI_RGB_ 0
#define TERN_CP_UTF8_ 65001u
#define TERN_IDC_ARROW_ ((void *)(uintptr_t)32512)

// --- Win32 structures ------------------------------------------------------
//
// x86-64 layouts. The sizes are asserted at run time (see
// tools/d3d12/window_layout_check.py, which compares them against windows.h via
// the Zig cross compiler), because getting one wrong is a crash on the first
// frame rather than an error message.

typedef struct {
    int32_t left, top, right, bottom;
} TernWinRect;

typedef struct {
    uint32_t cb_size;
    uint32_t style;
    intptr_t (*proc)(void *, uint32_t, uintptr_t, intptr_t);
    int32_t cls_extra;
    int32_t wnd_extra;
    void *instance;
    void *icon;
    void *cursor;
    void *background;
    const uint16_t *menu;
    const uint16_t *class_name;
    void *icon_small;
} TernWinClass;   // WNDCLASSEXW, 80 bytes

typedef struct {
    void *hwnd;
    uint32_t message;
    uint32_t pad;       // the alignment windows.h inserts before wParam
    uintptr_t wparam;
    intptr_t lparam;
    uint32_t time;
    int32_t x;
    int32_t y;
} TernWinMsg;   // MSG, 48 bytes

typedef struct {
    uint32_t size;
    int32_t width;
    int32_t height;
    uint16_t planes;
    uint16_t bits;
    uint32_t compression;
    uint32_t size_image;
    int32_t x_ppm;
    int32_t y_ppm;
    uint32_t colours_used;
    uint32_t colours_important;
} TernBitmapHeader;   // BITMAPINFOHEADER, 40 bytes

typedef struct {
    TernBitmapHeader header;
    uint32_t colour0;   // BITMAPINFO's RGBQUAD[1]; unused for 32bpp BI_RGB
} TernBitmapInfo;   // 44 bytes

// The sizes and offsets windows.h gives these, written down so the Windows
// build can check them: tools/d3d12/gen_window_layout_check.py turns each line
// below into the same assertion against the SDK header, so a wrong number fails
// a compile instead of corrupting the heap on the first frame.

_Static_assert(sizeof(TernWinRect) == 16, "TernWinRect size");
_Static_assert(offsetof(TernWinRect, right) == 8, "TernWinRect.right offset");

_Static_assert(sizeof(TernWinClass) == 80, "TernWinClass size");
_Static_assert(offsetof(TernWinClass, proc) == 8, "TernWinClass.proc offset");
_Static_assert(offsetof(TernWinClass, cls_extra) == 16, "TernWinClass.cls_extra offset");
_Static_assert(offsetof(TernWinClass, instance) == 24, "TernWinClass.instance offset");
_Static_assert(offsetof(TernWinClass, background) == 48, "TernWinClass.background offset");
_Static_assert(offsetof(TernWinClass, class_name) == 64, "TernWinClass.class_name offset");
_Static_assert(offsetof(TernWinClass, icon_small) == 72, "TernWinClass.icon_small offset");

_Static_assert(sizeof(TernWinMsg) == 48, "TernWinMsg size");
_Static_assert(offsetof(TernWinMsg, message) == 8, "TernWinMsg.message offset");
_Static_assert(offsetof(TernWinMsg, wparam) == 16, "TernWinMsg.wparam offset");
_Static_assert(offsetof(TernWinMsg, lparam) == 24, "TernWinMsg.lparam offset");
_Static_assert(offsetof(TernWinMsg, time) == 32, "TernWinMsg.time offset");
_Static_assert(offsetof(TernWinMsg, y) == 40, "TernWinMsg.y offset");

_Static_assert(sizeof(TernBitmapHeader) == 40, "TernBitmapHeader size");
_Static_assert(offsetof(TernBitmapHeader, height) == 8, "TernBitmapHeader.height offset");
_Static_assert(offsetof(TernBitmapHeader, bits) == 14, "TernBitmapHeader.bits offset");
_Static_assert(offsetof(TernBitmapHeader, size_image) == 20, "TernBitmapHeader.size_image offset");
_Static_assert(offsetof(TernBitmapHeader, colours_important) == 36,
               "TernBitmapHeader.colours_important offset");
_Static_assert(sizeof(TernBitmapInfo) == 44, "TernBitmapInfo size");
_Static_assert(offsetof(TernBitmapInfo, colour0) == 40, "TernBitmapInfo.colour0 offset");

// --- entry points ----------------------------------------------------------

typedef struct {
    void *user32;
    void *gdi32;
    void *kernel32;
    int tried;
    int ready;

    void *(*register_class)(const TernWinClass *);
    void *(*create_window)(uint32_t, const uint16_t *, const uint16_t *, uint32_t,
                           int, int, int, int, void *, void *, void *, void *);
    intptr_t (*def_window_proc)(void *, uint32_t, uintptr_t, intptr_t);
    int (*show_window)(void *, int);
    int (*update_window)(void *);
    int (*peek_message)(TernWinMsg *, void *, uint32_t, uint32_t, uint32_t);
    int (*translate_message)(const TernWinMsg *);
    intptr_t (*dispatch_message)(const TernWinMsg *);
    int (*destroy_window)(void *);
    void *(*get_dc)(void *);
    int (*release_dc)(void *, void *);
    void *(*module_handle)(const uint16_t *);
    void *(*load_cursor)(void *, void *);
    int (*get_client_rect)(void *, TernWinRect *);
    int (*adjust_window_rect)(TernWinRect *, uint32_t, int);

    int (*stretch_dib)(void *, int, int, int, int, int, int, int, int,
                       const void *, const TernBitmapInfo *, uint32_t, uint32_t);
    void (*sleep)(uint32_t);
    int (*widen)(uint32_t, uint32_t, const char *, int, uint16_t *, int);
} TernWin32;

static TernWin32 tern_win32;

typedef struct {
    void *hwnd;
    int width;
    int height;
    int live;   // the slot is in use, whether or not the window is still up
    int open;
    unsigned char *dib;   // BGRA, top row first
    int dib_width;
    int dib_height;
} TernWindow;

static TernWindow tern_windows[TERN_MAX_WINDOWS];
static int tern_window_class_done;
static const uint16_t tern_window_class_name[] = {
    'T', 'E', 'R', 'N', 'L', 'a', 'n', 'g', 'W', 'i', 'n', 'd', 'o', 'w', 0
};

static void *tern_win32_need(void *library, const char *name) {
    void *symbol = tern_lib_symbol(library, name);
    if (!symbol) {
        return NULL;
    }
    return symbol;
}

/// Loads user32, gdi32 and kernel32. Fails quietly: a machine without them
/// cannot show a window, which is a fact about the machine, not an error in the
/// program calling d3d12.window().
static TernWin32 *tern_win32_load(void) {
    if (tern_win32.tried) {
        return tern_win32.ready ? &tern_win32 : NULL;
    }
    tern_win32.tried = 1;

    const char *user32_path = getenv("TERN_USER32_LIBRARY");
    const char *gdi32_path = getenv("TERN_GDI32_LIBRARY");
    const char *kernel32_path = getenv("TERN_KERNEL32_LIBRARY");
#ifdef _WIN32
    const char *user32_name = "user32.dll";
    const char *gdi32_name = "gdi32.dll";
    const char *kernel32_name = "kernel32.dll";
#else
    // Windows has no equivalent on Linux, so by default there is no window -
    // unless a test points these at a stand-in.
    const char *user32_name = "libuser32.so";
    const char *gdi32_name = "libgdi32.so";
    const char *kernel32_name = "libkernel32.so";
#endif

    tern_win32.user32 = tern_lib_open(user32_path && user32_path[0] ? user32_path : user32_name);
    if (!tern_win32.user32) {
        return NULL;
    }
    tern_win32.gdi32 = tern_lib_open(gdi32_path && gdi32_path[0] ? gdi32_path : gdi32_name);
    if (!tern_win32.gdi32) {
        return NULL;
    }
    tern_win32.kernel32 = tern_lib_open(kernel32_path && kernel32_path[0] ? kernel32_path
                                                                        : kernel32_name);

    tern_win32.register_class = (void *(*)(const TernWinClass *))
        tern_win32_need(tern_win32.user32, "RegisterClassExW");
    tern_win32.create_window = (void *(*)(uint32_t, const uint16_t *, const uint16_t *, uint32_t,
                                          int, int, int, int, void *, void *, void *, void *))
        tern_win32_need(tern_win32.user32, "CreateWindowExW");
    tern_win32.def_window_proc = (intptr_t (*)(void *, uint32_t, uintptr_t, intptr_t))
        tern_win32_need(tern_win32.user32, "DefWindowProcW");
    tern_win32.show_window = (int (*)(void *, int))
        tern_win32_need(tern_win32.user32, "ShowWindow");
    tern_win32.update_window = (int (*)(void *))
        tern_win32_need(tern_win32.user32, "UpdateWindow");
    tern_win32.peek_message = (int (*)(TernWinMsg *, void *, uint32_t, uint32_t, uint32_t))
        tern_win32_need(tern_win32.user32, "PeekMessageW");
    tern_win32.translate_message = (int (*)(const TernWinMsg *))
        tern_win32_need(tern_win32.user32, "TranslateMessage");
    tern_win32.dispatch_message = (intptr_t (*)(const TernWinMsg *))
        tern_win32_need(tern_win32.user32, "DispatchMessageW");
    tern_win32.destroy_window = (int (*)(void *))
        tern_win32_need(tern_win32.user32, "DestroyWindow");
    tern_win32.get_dc = (void *(*)(void *))
        tern_win32_need(tern_win32.user32, "GetDC");
    tern_win32.release_dc = (int (*)(void *, void *))
        tern_win32_need(tern_win32.user32, "ReleaseDC");
    tern_win32.module_handle = (void *(*)(const uint16_t *))
        tern_win32_need(tern_win32.user32, "GetModuleHandleW");
    tern_win32.load_cursor = (void *(*)(void *, void *))
        tern_win32_need(tern_win32.user32, "LoadCursorW");
    tern_win32.get_client_rect = (int (*)(void *, TernWinRect *))
        tern_win32_need(tern_win32.user32, "GetClientRect");
    tern_win32.adjust_window_rect = (int (*)(TernWinRect *, uint32_t, int))
        tern_win32_need(tern_win32.user32, "AdjustWindowRect");

    tern_win32.stretch_dib = (int (*)(void *, int, int, int, int, int, int, int, int,
                                      const void *, const TernBitmapInfo *, uint32_t, uint32_t))
        tern_win32_need(tern_win32.gdi32, "StretchDIBits");

    if (tern_win32.kernel32) {
        tern_win32.sleep = (void (*)(uint32_t))
            tern_win32_need(tern_win32.kernel32, "Sleep");
        tern_win32.widen = (int (*)(uint32_t, uint32_t, const char *, int, uint16_t *, int))
            tern_win32_need(tern_win32.kernel32, "MultiByteToWideChar");
    }

    if (!tern_win32.register_class || !tern_win32.create_window ||
            !tern_win32.def_window_proc || !tern_win32.peek_message ||
            !tern_win32.dispatch_message || !tern_win32.get_dc || !tern_win32.stretch_dib) {
        return NULL;
    }

    tern_win32.ready = 1;
    return &tern_win32;
}

/// UTF-8 to the UTF-16 the W functions want, without assuming `wchar_t` is two
/// bytes - it is four on every system this file is compiled on except the one
/// it ultimately runs on.
static void tern_window_widen(const char *utf8, uint16_t *out, size_t out_length) {
    size_t at = 0;
    const unsigned char *p = (const unsigned char *)utf8;
    if (out_length == 0) {
        return;
    }
    if (tern_win32.widen &&
            tern_win32.widen(TERN_CP_UTF8_, 0, utf8, -1, out, (int)out_length) > 0) {
        return;
    }
    // No MultiByteToWideChar: keep ASCII and drop the rest rather than fail.
    while (*p && at + 1 < out_length) {
        out[at++] = *p < 0x80 ? (uint16_t)*p : (uint16_t)'?';
        p++;
    }
    out[at] = 0;
}

static TernWindow *tern_window_from(void *hwnd) {
    for (int i = 0; i < TERN_MAX_WINDOWS; i++) {
        if (tern_windows[i].live && tern_windows[i].hwnd == hwnd) {
            return &tern_windows[i];
        }
    }
    return NULL;
}

static intptr_t tern_window_proc(void *hwnd, uint32_t message, uintptr_t wparam, intptr_t lparam) {
    TernWindow *window = tern_window_from(hwnd);
    if (window) {
        if (message == TERN_WM_CLOSE_) {
            window->open = 0;
            if (tern_win32.destroy_window) {
                tern_win32.destroy_window(hwnd);
            }
            return 0;
        }
        if (message == TERN_WM_DESTROY_) {
            window->open = 0;
            return 0;
        }
        if (message == TERN_WM_KEYDOWN_ && wparam == TERN_VK_ESCAPE_) {
            window->open = 0;
            if (tern_win32.destroy_window) {
                tern_win32.destroy_window(hwnd);
            }
            return 0;
        }
    }
    return tern_win32.def_window_proc ? tern_win32.def_window_proc(hwnd, message, wparam, lparam) : 0;
}

/// Opens a window whose client area is as close to width x height as Win32
/// allows, and returns it, or NULL when this machine has no window layer.
static TernWindow *tern_window_open(int width, int height, const char *title) {
    TernWin32 *api = tern_win32_load();
    if (!api) {
        return NULL;
    }

    void *instance = api->module_handle ? api->module_handle(NULL) : NULL;
    if (!tern_window_class_done) {
        TernWinClass window_class;
        memset(&window_class, 0, sizeof window_class);
        window_class.cb_size = (uint32_t)sizeof window_class;
        window_class.proc = tern_window_proc;
        window_class.instance = instance;
        window_class.cursor = api->load_cursor ? api->load_cursor(NULL, TERN_IDC_ARROW_) : NULL;
        window_class.class_name = tern_window_class_name;
        if (api->register_class(&window_class) == NULL) {
            return NULL;
        }
        tern_window_class_done = 1;
    }

    TernWindow *window = NULL;
    for (int i = 0; i < TERN_MAX_WINDOWS; i++) {
        if (!tern_windows[i].live) {
            window = &tern_windows[i];
            break;
        }
    }
    if (!window) {
        return NULL;
    }
    memset(window, 0, sizeof *window);

    // The window is the frame; the client area is what the picture fills. Ask
    // for the frame that leaves width x height of client space, so a 640x480
    // image is not cropped by the title bar.
    TernWinRect frame;
    frame.left = 0;
    frame.top = 0;
    frame.right = width;
    frame.bottom = height;
    if (api->adjust_window_rect) {
        api->adjust_window_rect(&frame, TERN_WS_OVERLAPPEDWINDOW_, 0);
    }

    uint16_t wide_title[256];
    tern_window_widen(title ? title : "TERN", wide_title, 256);

    void *hwnd = api->create_window(0, tern_window_class_name, wide_title,
                                    TERN_WS_OVERLAPPEDWINDOW_,
                                    0x80000000, 0x80000000,   // CW_USEDEFAULT
                                    frame.right - frame.left, frame.bottom - frame.top,
                                    NULL, NULL, instance, NULL);
    if (!hwnd) {
        return NULL;
    }

    window->hwnd = hwnd;
    window->width = width;
    window->height = height;
    window->live = 1;
    window->open = 1;

    if (api->show_window) {
        api->show_window(hwnd, TERN_SW_SHOW_);
    }
    if (api->update_window) {
        api->update_window(hwnd);
    }
    return window;
}

static void tern_window_release_dib(TernWindow *window) {
    if (window->dib) {
        free(window->dib);
        window->dib = NULL;
        window->dib_width = 0;
        window->dib_height = 0;
    }
}

/// Turns off-screen messages into a closed window: WM_QUIT is what PostQuitMessage
/// leaves behind, and WM_CLOSE/WM_DESTROY are handled by the window procedure.
static void tern_window_pump(TernWindow *window) {
    if (!tern_win32.peek_message) {
        return;
    }
    TernWinMsg message;
    while (tern_win32.peek_message(&message, NULL, 0, 0, TERN_PM_REMOVE_)) {
        if (message.message == TERN_WM_QUIT_) {
            window->open = 0;
            break;
        }
        if (tern_win32.translate_message) {
            tern_win32.translate_message(&message);
        }
        if (tern_win32.dispatch_message) {
            tern_win32.dispatch_message(&message);
        }
    }
}

/// Shows one frame. `rgba` is width x height RGBA with the top row first, the
/// same layout d3d12.pixels() returns and d3d12.save() writes.
///
/// Returns 1 while the window is still open and 0 once the user has closed it.
/// The frame is paced at roughly 60 per second, because an uncapped loop simply
/// spins a core for no more animation.
static int tern_window_present(TernWindow *window, const unsigned char *rgba,
                               int width, int height) {
    TernWin32 *api = &tern_win32;
    if (!window || !window->open) {
        return 0;
    }

    if (window->dib_width != width || window->dib_height != height ||
            (!window->dib && width > 0 && height > 0)) {
        tern_window_release_dib(window);
        window->dib = (unsigned char *)calloc((size_t)width * (size_t)height, 4);
        if (!window->dib) {
            tern_panic("D3D12Error", "cannot open a window: out of memory");
        }
        window->dib_width = width;
        window->dib_height = height;
    }

    // A 32-bit BI_RGB DIB is BGRA in memory, and row 0 is the top because the
    // height below is negative. The readback is RGBA with row 0 at the bottom,
    // which is the same order d3d12.save() flips when it writes a PNG.
    unsigned char *target = window->dib;
    size_t row_bytes = (size_t)width * 4;
    for (int y = 0; y < height; y++) {
        const unsigned char *source = rgba + (size_t)(height - 1 - y) * row_bytes;
        unsigned char *line = target + (size_t)y * row_bytes;
        for (int x = 0; x < width; x++) {
            line[x * 4 + 0] = source[x * 4 + 2];
            line[x * 4 + 1] = source[x * 4 + 1];
            line[x * 4 + 2] = source[x * 4 + 0];
            line[x * 4 + 3] = 255;
        }
    }

    TernBitmapInfo info;
    memset(&info, 0, sizeof info);
    info.header.size = (uint32_t)sizeof(TernBitmapHeader);
    info.header.width = width;
    info.header.height = -height;
    info.header.planes = 1;
    info.header.bits = 32;
    info.header.compression = TERN_BI_RGB_;

    // Filling the client area rather than the requested size means a resized
    // window shows a bigger cube instead of a cropped one.
    int client_width = width;
    int client_height = height;
    TernWinRect client;
    if (api->get_client_rect && api->get_client_rect(window->hwnd, &client)) {
        if (client.right - client.left > 0 && client.bottom - client.top > 0) {
            client_width = client.right - client.left;
            client_height = client.bottom - client.top;
        }
    }

    void *dc = api->get_dc(window->hwnd);
    if (dc) {
        api->stretch_dib(dc, 0, 0, client_width, client_height, 0, 0, width, height,
                         window->dib, &info, TERN_DIB_RGB_COLORS_, TERN_SRCCOPY_);
        if (api->release_dc) {
            api->release_dc(window->hwnd, dc);
        }
    }

    tern_window_pump(window);
    if (api->sleep) {
        api->sleep(16);
    }
    return window->open;
}

static void tern_window_close(TernWindow *window) {
    if (!window) {
        return;
    }
    if (window->open && window->hwnd && tern_win32.destroy_window) {
        tern_win32.destroy_window(window->hwnd);
    }
    tern_window_release_dib(window);
    memset(window, 0, sizeof *window);
}

/// A stable 1-based handle for a window, or 0 when there is no window layer.
static int tern_window_handle(TernWindow *window) {
    return window ? (int)(window - tern_windows) + 1 : 0;
}

static TernWindow *tern_window_at(int handle) {
    if (handle < 1 || handle > TERN_MAX_WINDOWS || !tern_windows[handle - 1].live) {
        return NULL;
    }
    return &tern_windows[handle - 1];
}

#endif  // TERN_WINDOW_H
