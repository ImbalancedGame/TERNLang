// A fake user32.dll / gdi32.dll / kernel32.dll, for testing d3d12.window on a
// machine that has no Windows and no display.
//
// The three Windows libraries are one shared object here; point all three
// variables at it:
//
//   TERN_USER32_LIBRARY=$PWD/libuser32.so
//   TERN_GDI32_LIBRARY=$PWD/libuser32.so
//   TERN_KERNEL32_LIBRARY=$PWD/libuser32.so
//   TERN_D3D12_WINDOW=1
//
// Opening a window is opt-in (`TERN_D3D12_WINDOW=1`) so that a program which
// tries a window and falls back to text does not open one during an ordinary
// test run: on a real machine that would leave a window on someone's screen.
//
// What it checks, beyond "the call arrived": the bitmap handed to StretchDIBits
// has to be a 32-bit top-down DIB whose every pixel is opaque, and its contents
// are hashed so the interpreter and the compiled binary can be compared pixel
// for pixel. The window closes itself after TERN_WINDOW_FRAMES frames (default
// 3), which is what lets the cube's loop terminate in a test.
//
// Build: cc -shared -fPIC -o libuser32.so tools/d3d12/mock_user32.c

#define _GNU_SOURCE
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef struct {
    uint32_t cbSize, style;
    void *proc;
    int32_t clsExtra, wndExtra;
    void *instance, *icon, *cursor, *background;
    const uint16_t *menu, *className;
    void *iconSmall;
} WndClassEx;

typedef struct {
    void *hwnd;
    uint32_t message, pad;
    uintptr_t wparam;
    intptr_t lparam;
    uint32_t time;
    int32_t x, y;
} Msg;

typedef struct {
    int32_t left, top, right, bottom;
} Rect;

typedef struct {
    uint32_t size;
    int32_t width, height;
    uint16_t planes, bits;
    uint32_t compression, sizeImage;
    int32_t xPpm, yPpm;
    uint32_t coloursUsed, coloursImportant;
} BitmapHeader;

typedef struct {
    BitmapHeader header;
    uint32_t colour0;
} BitmapInfo;

#define WM_CLOSE_ 0x0010u
#define WM_QUIT_ 0x0012u
#define MSG_NONE 0

static int frames_presented;
static int frames_to_run = -1;
static int window_open;
static int client_width, client_height;

static int configured_frames(void) {
    if (frames_to_run >= 0) {
        return frames_to_run;
    }
    const char *value = getenv("TERN_WINDOW_FRAMES");
    frames_to_run = value && value[0] ? atoi(value) : 3;
    return frames_to_run;
}

static int windowing_enabled(void) {
    const char *value = getenv("TERN_D3D12_WINDOW");
    return value && value[0] && strcmp(value, "0") != 0;
}

// --- user32 ----------------------------------------------------------------

uint16_t RegisterClassExW(const WndClassEx *windowClass) {
    if (!windowClass || windowClass->cbSize != sizeof(WndClassEx) || !windowClass->proc) {
        fprintf(stderr, "mock_user32: RegisterClassExW got a %u byte class (expected %zu)\n",
                windowClass ? windowClass->cbSize : 0, sizeof(WndClassEx));
        return 0;
    }
    if (!windowClass->className) {
        fprintf(stderr, "mock_user32: RegisterClassExW got no class name\n");
        return 0;
    }
    return 1;
}

void *CreateWindowExW(uint32_t exStyle, const uint16_t *className, const uint16_t *title,
                      uint32_t style, int x, int y, int width, int height,
                      void *parent, void *menu, void *instance, void *parameter) {
    (void)exStyle;
    (void)style;
    (void)x;
    (void)y;
    (void)parent;
    (void)menu;
    (void)instance;
    (void)parameter;
    if (!className || !title) {
        fprintf(stderr, "mock_user32: CreateWindowExW got no class name or title\n");
        return NULL;
    }
    if (!windowing_enabled()) {
        // No display here, exactly like a service or a headless machine.
        return NULL;
    }
    if (width <= 0 || height <= 0) {
        fprintf(stderr, "mock_user32: CreateWindowExW got %dx%d\n", width, height);
        return NULL;
    }
    client_width = width;
    client_height = height;
    window_open = 1;
    return (void *)0x1000;
}

intptr_t DefWindowProcW(void *hwnd, uint32_t message, uintptr_t wparam, intptr_t lparam) {
    (void)hwnd;
    (void)message;
    (void)wparam;
    (void)lparam;
    return 0;
}

int ShowWindow(void *hwnd, int command) {
    (void)hwnd;
    (void)command;
    return 1;
}

int UpdateWindow(void *hwnd) {
    (void)hwnd;
    return 1;
}

int PeekMessageW(Msg *message, void *hwnd, uint32_t min, uint32_t max, uint32_t remove) {
    (void)hwnd;
    (void)min;
    (void)max;
    (void)remove;
    if (!message) {
        return 0;
    }
    if (window_open && frames_presented >= configured_frames()) {
        // The user closed it: this is what PostQuitMessage leaves in the queue.
        memset(message, 0, sizeof *message);
        message->message = WM_QUIT_;
        window_open = 0;
        return 1;
    }
    return 0;
}

int TranslateMessage(const Msg *message) {
    (void)message;
    return 0;
}

intptr_t DispatchMessageW(const Msg *message) {
    (void)message;
    return 0;
}

int DestroyWindow(void *hwnd) {
    (void)hwnd;
    window_open = 0;
    return 1;
}

void *GetDC(void *hwnd) {
    (void)hwnd;
    return (void *)0x2000;
}

int ReleaseDC(void *hwnd, void *dc) {
    (void)hwnd;
    (void)dc;
    return 1;
}

void *GetModuleHandleW(const uint16_t *name) {
    (void)name;
    return (void *)0x3000;
}

void *LoadCursorW(void *instance, void *name) {
    (void)instance;
    (void)name;
    return (void *)0x4000;
}

int GetClientRect(void *hwnd, Rect *rect) {
    (void)hwnd;
    if (!rect) {
        return 0;
    }
    rect->left = 0;
    rect->top = 0;
    rect->right = client_width;
    rect->bottom = client_height;
    return 1;
}

int AdjustWindowRect(Rect *rect, uint32_t style, int menu) {
    (void)style;
    (void)menu;
    return rect != NULL;
}

// --- gdi32 -----------------------------------------------------------------

unsigned StretchDIBits(void *dc, int xDest, int yDest, int destWidth, int destHeight,
                       int xSrc, int ySrc, int srcWidth, int srcHeight,
                       const void *bits, const BitmapInfo *info, unsigned usage, unsigned rop) {
    (void)dc;
    (void)xDest;
    (void)yDest;
    (void)destWidth;
    (void)destHeight;
    (void)xSrc;
    (void)ySrc;
    (void)usage;
    (void)rop;

    if (!bits || !info) {
        fprintf(stderr, "mock_user32: StretchDIBits with no pixels\n");
        return 0;
    }
    if (info->header.size != sizeof(BitmapHeader)) {
        fprintf(stderr, "mock_user32: StretchDIBits header is %u bytes, expected %zu\n",
                info->header.size, sizeof(BitmapHeader));
        return 0;
    }
    if (info->header.bits != 32 || info->header.compression != 0) {
        fprintf(stderr, "mock_user32: StretchDIBits wants a 32-bit BI_RGB bitmap, got %u bits "
                        "compression %u\n", info->header.bits, info->header.compression);
        return 0;
    }
    if (info->header.height >= 0) {
        fprintf(stderr, "mock_user32: StretchDIBits wants a top-down bitmap, height is %d\n",
                info->header.height);
        return 0;
    }
    if (info->header.width != srcWidth || -info->header.height != srcHeight) {
        fprintf(stderr, "mock_user32: StretchDIBits bitmap is %dx%d but the call says %dx%d\n",
                info->header.width, info->header.height, srcWidth, srcHeight);
        return 0;
    }

    const unsigned char *pixels = (const unsigned char *)bits;
    size_t total = (size_t)srcWidth * (size_t)srcHeight * 4;
    // The alpha byte is set by the runtime's own fill loop, so if it is wrong
    // the window is showing garbage that a screenshot would not explain.
    for (size_t at = 3; at < total; at += 4) {
        if (pixels[at] != 255) {
            fprintf(stderr, "mock_user32: pixel %zu is not opaque (alpha %u)\n", at / 4, pixels[at]);
            return 0;
        }
    }

    uint32_t hash = 2166136261u;
    for (size_t at = 0; at < total; at++) {
        hash ^= pixels[at];
        hash *= 16777619u;
    }

    frames_presented++;
    // stderr, not stdout: the probe's own output is diffed between the two back
    // ends, and a library has no business interleaving with it.
    fprintf(stderr, "mock_user32: frame %d %dx%d checksum 0x%08x\n",
            frames_presented, srcWidth, srcHeight, hash);
    fflush(stderr);
    return srcWidth;
}

// --- kernel32 --------------------------------------------------------------

void Sleep(uint32_t milliseconds) {
    (void)milliseconds;   // a test should not wait 60 seconds for 3600 frames
}

int MultiByteToWideChar(uint32_t codePage, uint32_t flags, const char *text, int count,
                        uint16_t *out, int outCount) {
    (void)codePage;
    (void)flags;
    int written = 0;
    if (!out || outCount <= 0) {
        return 0;
    }
    if (count < 0) {
        count = text ? (int)strlen(text) + 1 : 0;
    }
    for (int i = 0; i < count && written < outCount - 1; i++) {
        out[written++] = text[i] ? (uint16_t)(unsigned char)text[i] : 0;
        if (!text[i]) {
            return written;
        }
    }
    out[written] = 0;
    return written;
}
