// std:d3d12 — Direct3D 12 from TERN.
//
// Two things make this testable on a machine with no Windows and no GPU:
//
//   * the library is opened with tern_lib_open, so TERN_D3D12_LIBRARY can point
//     at a mock that implements the same COM vtables, and
//   * every structure below is written out by hand, with the offsets printed
//     from the real d3d12.h (sizeof(D3D12_RESOURCE_DESC) is 56, and so on), so
//     the same code compiles unchanged here and against the Windows SDK.
//
// The vtable indices are the ones in the shipped d3d12.h: ID3D12Device's
// CreateCommandQueue is slot 8, ID3D12GraphicsCommandList's Close is 9 and its
// ClearRenderTargetView is 48, and so on. Getting one of those wrong is a crash
// rather than a compile error, which is why they are listed here against the
// header and exercised by the mock.
#ifndef TERN_D3D12_H
#define TERN_D3D12_H

#include <string.h>

#define D3D12_MAX_DEVICES 4
#define D3D12_MAX_DIMENSION 16384
#define D3D12_WAIT_TIMEOUT_MS 30000

// --- the structures, with the offsets the SDK headers give them -------------

typedef struct {
    uint32_t a;
    uint16_t b, c;
    unsigned char d[8];
} TernGuid;

static const TernGuid TERN_IID_ID3D12Device = {
    0x189819f1u, 0x1b6b, 0x4ec5, { 0xba, 0xaf, 0x95, 0xa3, 0x7f, 0x9e, 0x51, 0x55 }
};
static const TernGuid TERN_IID_IDXGIFactory1 = {
    0x770aae78u, 0xf26f, 0x4dba, { 0xa8, 0x29, 0x25, 0x3c, 0x83, 0xd1, 0xb3, 0x87 }
};

#define D3D12_HEAP_TYPE_DEFAULT_ 1
#define D3D12_HEAP_TYPE_READBACK_ 3
#define D3D12_DESCRIPTOR_HEAP_TYPE_RTV_ 2
#define D3D12_RESOURCE_DIMENSION_BUFFER_ 1
#define D3D12_RESOURCE_DIMENSION_TEXTURE2D_ 3
#define D3D12_RESOURCE_STATE_COMMON_ 0
#define D3D12_RESOURCE_STATE_RENDER_TARGET_ 4
#define D3D12_RESOURCE_STATE_COPY_DEST_ 1024
#define D3D12_RESOURCE_STATE_COPY_SOURCE_ 2048
#define DXGI_FORMAT_R8G8B8A8_UNORM_ 28
#define D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX_ 0
#define D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT_ 1
#define D3D12_COMMAND_LIST_TYPE_DIRECT_ 0
#define D3D_FEATURE_LEVEL_11_0_ 0xB000
#define D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET_ 1

typedef struct {
    int type;
    int cpu_page_property;
    int memory_pool_preference;
    unsigned creation_node_mask;
    unsigned visible_node_mask;
} D3D12_HEAP_PROPERTIES;

typedef struct {
    unsigned count;
    unsigned quality;
} TernSampleDesc;

typedef struct {
    int dimension;              // 0
    unsigned long long alignment;// 8
    unsigned long long width;   // 16
    unsigned height;            // 24
    unsigned short depth_or_array_size; // 28
    unsigned short mip_levels;  // 30
    int format;                 // 32
    TernSampleDesc sample_desc;// 36
    int layout;                 // 44
    int flags;                  // 48
} D3D12_RESOURCE_DESC;          // 56

typedef struct {
    int type;
    unsigned num_descriptors;
    unsigned flags;
    unsigned node_mask;
} D3D12_DESCRIPTOR_HEAP_DESC;   // 16

typedef struct {
    void *resource;
    unsigned subresource;
    unsigned state_before;
    unsigned state_after;
} D3D12_TRANSITION_RESOURCE;

typedef struct {
    int type;
    unsigned flags;
    union {
        D3D12_TRANSITION_RESOURCE transition;
        struct { void *before; void *after; } aliasing;
        struct { void *resource; unsigned subresource; } uav;
    } u;
} D3D12_RESOURCE_BARRIER;       // 32

typedef struct {
    unsigned long long begin;
    unsigned long long end;
} D3D12_RANGE;                  // 16

typedef struct {
    int format;
    unsigned width;
    unsigned height;
    unsigned depth;
    unsigned row_pitch;
} D3D12_SUBRESOURCE_FOOTPRINT;  // 20

typedef struct {
    unsigned long long offset;
    D3D12_SUBRESOURCE_FOOTPRINT footprint;
} D3D12_PLACED_SUBRESOURCE_FOOTPRINT; // 32

typedef struct {
    void *resource;
    int type;
    union {
        D3D12_PLACED_SUBRESOURCE_FOOTPRINT placed_footprint;
        unsigned subresource_index;
    } u;
} D3D12_TEXTURE_COPY_LOCATION;  // 48

typedef struct {
    size_t pointer;
} D3D12_CPU_DESCRIPTOR_HANDLE;  // 8

// --- the vtables -----------------------------------------------------------

#define TERN_SLOT(object, slot) (((void **)((void ***)(object))[0])[slot])

typedef long (*PfnCreateCommandQueue)(void *, const void *, const TernGuid *, void **);
typedef long (*PfnCreateCommandAllocator)(void *, int, const TernGuid *, void **);
typedef long (*PfnCreateCommandList)(void *, int, int, void *, void *, const TernGuid *, void **);
typedef long (*PfnCreateDescriptorHeap)(void *, const D3D12_DESCRIPTOR_HEAP_DESC *, const TernGuid *, void **);
typedef void (*PfnGetCpuHandle)(void *, D3D12_CPU_DESCRIPTOR_HANDLE *);
typedef long (*PfnCreateCommittedResource)(void *, const void *, unsigned, const void *, int,
                                           const void *, const TernGuid *, void **);
typedef void (*PfnCreateRenderTargetView)(void *, void *, const void *, D3D12_CPU_DESCRIPTOR_HANDLE);
typedef long (*PfnCreateFence)(void *, unsigned long long, unsigned, const TernGuid *, void **);
typedef void (*PfnGetCopyableFootprints)(void *, const void *, unsigned, unsigned, unsigned long long,
                                         D3D12_PLACED_SUBRESOURCE_FOOTPRINT *, unsigned *,
                                         unsigned long long *, unsigned long long *);
typedef unsigned long long (*PfnGetCompletedValue)(void *);
typedef long (*PfnFenceSignal)(void *, void *, unsigned long long);
typedef long (*PfnResetAllocator)(void *);
typedef long (*PfnResetList)(void *, void *, void *);
typedef long (*PfnCloseList)(void *);
typedef void (*PfnResourceBarrier)(void *, unsigned, const D3D12_RESOURCE_BARRIER *);
typedef void (*PfnOMSetRenderTargets)(void *, unsigned, const D3D12_CPU_DESCRIPTOR_HANDLE *, int, const void *);
typedef void (*PfnClearRenderTargetView)(void *, D3D12_CPU_DESCRIPTOR_HANDLE, const float *, unsigned, const void *);
typedef void (*PfnCopyTextureRegion)(void *, const D3D12_TEXTURE_COPY_LOCATION *, unsigned,
                                     unsigned, const D3D12_TEXTURE_COPY_LOCATION *, const void *);
typedef void (*PfnExecuteCommandLists)(void *, unsigned, void *const *);
typedef long (*PfnMap)(void *, unsigned, const D3D12_RANGE *, void **);
typedef void (*PfnUnmap)(void *, unsigned, const D3D12_RANGE *);
typedef void (*PfnSetName)(void *, const wchar_t *);

typedef void *(*PfnD3D12CreateDevice)(void *, int, const TernGuid *, void **);
typedef void *(*PfnCreateDXGIFactory1)(const TernGuid *, void **);
typedef void (*PfnGetDesc1)(void *, void *);

// --- the loaded library ----------------------------------------------------

typedef struct {
    void *d3d12;
    void *dxgi;
    PfnD3D12CreateDevice create_device;
    PfnCreateDXGIFactory1 create_factory;
    int tried;
    int ready;
} TernD3D12Driver;

static TernD3D12Driver d3d12_driver;

static TernD3D12Driver *d3d12_get_driver(void) {
    if (d3d12_driver.tried) {
        return d3d12_driver.ready ? &d3d12_driver : NULL;
    }
    d3d12_driver.tried = 1;

    // An explicit TERN_D3D12_LIBRARY wins and suppresses the platform name, so a
    // test can ask for a library that is not there and see the failure.
    const char *path = getenv("TERN_D3D12_LIBRARY");
#ifdef _WIN32
    const char *default_name = "d3d12.dll";
#else
    const char *default_name = "libd3d12.so";
#endif
    d3d12_driver.d3d12 = tern_lib_open(path && path[0] ? path : default_name);
    if (!d3d12_driver.d3d12) {
        return NULL;
    }

    d3d12_driver.create_device =
        (PfnD3D12CreateDevice)tern_lib_symbol(d3d12_driver.d3d12, "D3D12CreateDevice");
    if (!d3d12_driver.create_device) {
        return NULL;
    }

    // DXGI is only needed for the adapter name, so a missing one is not fatal.
    const char *dxgi_path = getenv("TERN_DXGI_LIBRARY");
#ifdef _WIN32
    const char *dxgi_default = "dxgi.dll";
#else
    const char *dxgi_default = path && path[0] ? path : "libdxgi.so";
#endif
    d3d12_driver.dxgi = tern_lib_open(dxgi_path && dxgi_path[0] ? dxgi_path : dxgi_default);
    if (!d3d12_driver.dxgi) {
        d3d12_driver.dxgi = d3d12_driver.d3d12;   // the mock exports both
    }
    d3d12_driver.create_factory =
        (PfnCreateDXGIFactory1)tern_lib_symbol(d3d12_driver.dxgi, "CreateDXGIFactory1");

    d3d12_driver.ready = 1;
    return &d3d12_driver;
}

// --- devices ---------------------------------------------------------------

typedef struct {
    void *device;
    void *queue;
    void *allocator;
    void *list;
    void *heap;
    void *target;       // the render target texture
    void *readback;     // the buffer the pixels are copied into
    void *fence;
    D3D12_CPU_DESCRIPTOR_HANDLE rtv;
    unsigned long long fence_value;
    unsigned long long row_pitch;
    unsigned long long total_bytes;
    int width;
    int height;
    int open;
} TernD3D12Target;

static TernD3D12Target d3d12_targets[D3D12_MAX_DEVICES];

static void d3d12_release(void *object) {
    if (!object) {
        return;
    }
    ((unsigned long (*)(void *))TERN_SLOT(object, 2))(object);   // Release
}

static long d3d12_wait(TernD3D12Target *target) {
    // Polling GetCompletedValue keeps one code path on both platforms: a script
    // renders offscreen and waits, which is not a frame-rate critical path.
    PfnGetCompletedValue completed =
        (PfnGetCompletedValue)TERN_SLOT(target->fence, 8);
    for (int waited = 0; waited < D3D12_WAIT_TIMEOUT_MS; waited++) {
        if (completed(target->fence) >= target->fence_value) {
            return 0;
        }
#ifndef _WIN32
        usleep(1000);
#else
        Sleep(1);
#endif
    }
    return -1;
}

static void d3d12_submit(TernD3D12Target *target) {
    ((long (*)(void *))TERN_SLOT(target->list, 9))(target->list);            // Close
    void *lists[1] = { target->list };
    ((PfnExecuteCommandLists)TERN_SLOT(target->queue, 10))(target->queue, 1, lists);
    target->fence_value++;
    ((PfnFenceSignal)TERN_SLOT(target->queue, 14))(target->queue, target->fence, target->fence_value);
    if (d3d12_wait(target) != 0) {
        tern_panic("D3D12Error", "the GPU did not finish within %d ms", D3D12_WAIT_TIMEOUT_MS);
    }
}

static void d3d12_reset(TernD3D12Target *target) {
    ((PfnResetAllocator)TERN_SLOT(target->allocator, 8))(target->allocator);
    ((PfnResetList)TERN_SLOT(target->list, 10))(target->list, target->allocator, NULL);
}

static void d3d12_barrier(TernD3D12Target *target, void *resource, unsigned before, unsigned after) {
    D3D12_RESOURCE_BARRIER barrier;
    memset(&barrier, 0, sizeof barrier);
    barrier.type = 0;   // D3D12_RESOURCE_BARRIER_TYPE_TRANSITION
    barrier.flags = 0;
    barrier.u.transition.resource = resource;
    barrier.u.transition.subresource = 0xFFFFFFFFu;
    barrier.u.transition.state_before = before;
    barrier.u.transition.state_after = after;
    ((PfnResourceBarrier)TERN_SLOT(target->list, 26))(target->list, 1, &barrier);
}

// --- argument helpers ------------------------------------------------------

static int d3d12_int_arg(Value v, const char *what) {
    if (v.tag != T_INT) {
        tern_panic("TypeError", "%s must be an integer, not %s", what, tern_type_name(v));
    }
    return (int)v.as.i;
}

static int d3d12_dimension_arg(Value v, const char *what) {
    int size = d3d12_int_arg(v, what);
    if (size < 1 || size > D3D12_MAX_DIMENSION) {
        tern_panic("ArgumentError", "%s must be between 1 and %d", what, D3D12_MAX_DIMENSION);
    }
    return size;
}

static double d3d12_unit_arg(Value v, const char *what) {
    if (!v_is_num(v)) {
        tern_panic("TypeError", "%s must be a number, not %s", what, tern_type_name(v));
    }
    double number = v_as_double(v);
    if (number < 0 || number > 1) {
        tern_panic("ArgumentError", "%s must be between 0 and 1", what);
    }
    return number;
}

static TernD3D12Target *d3d12_slot_arg(Value v) {
    if (v.tag != T_INT) {
        tern_panic("TypeError", "expected a device handle, not %s", tern_type_name(v));
    }
    int handle = (int)v.as.i;
    if (handle < 1 || handle > D3D12_MAX_DEVICES || !d3d12_targets[handle - 1].open) {
        tern_panic("D3D12Error", "no open device with handle %d", handle);
    }
    return &d3d12_targets[handle - 1];
}

static void d3d12_unavailable(const char *why) {
    tern_panic("D3D12Error", "Direct3D 12 is not available on this system: %s", why);
}

// --- creating a device -----------------------------------------------------

static TernD3D12Target *d3d12_make_device(int width, int height) {
    TernD3D12Driver *driver = d3d12_get_driver();
    if (!driver) {
        d3d12_unavailable("the Direct3D 12 library could not be loaded");
    }

    TernD3D12Target *target = NULL;
    for (int i = 0; i < D3D12_MAX_DEVICES; i++) {
        if (!d3d12_targets[i].open) {
            target = &d3d12_targets[i];
            break;
        }
    }
    if (!target) {
        tern_panic("D3D12Error", "too many Direct3D 12 devices (%d)", D3D12_MAX_DEVICES);
    }
    memset(target, 0, sizeof *target);

    long hr = (long)driver->create_device(NULL, D3D_FEATURE_LEVEL_11_0_,
                                          &TERN_IID_ID3D12Device, &target->device);
    if (hr < 0 || !target->device) {
        tern_panic("D3D12Error",
                   "Direct3D 12 is not available on this system: D3D12CreateDevice failed (0x%08lx)",
                   (unsigned long)hr & 0xFFFFFFFFul);
    }

    D3D12_HEAP_PROPERTIES default_heap;
    memset(&default_heap, 0, sizeof default_heap);
    default_heap.type = D3D12_HEAP_TYPE_DEFAULT_;
    default_heap.creation_node_mask = 1;

    D3D12_HEAP_PROPERTIES readback_heap;
    memset(&readback_heap, 0, sizeof readback_heap);
    readback_heap.type = D3D12_HEAP_TYPE_READBACK_;

    // queue: { Type, Priority, Flags, NodeMask }
    struct { int type; int priority; unsigned flags; unsigned node_mask; } queue_desc = {
        D3D12_COMMAND_LIST_TYPE_DIRECT_, 0, 0, 0
    };
    hr = ((PfnCreateCommandQueue)TERN_SLOT(target->device, 8))(
        target->device, &queue_desc, &TERN_IID_ID3D12Device, &target->queue);
    if (hr < 0) {
        d3d12_unavailable("a command queue could not be created");
    }

    hr = ((PfnCreateCommandAllocator)TERN_SLOT(target->device, 9))(
        target->device, D3D12_COMMAND_LIST_TYPE_DIRECT_, &TERN_IID_ID3D12Device, &target->allocator);
    if (hr < 0) {
        d3d12_unavailable("a command allocator could not be created");
    }

    hr = ((PfnCreateCommandList)TERN_SLOT(target->device, 12))(
        target->device, D3D12_COMMAND_LIST_TYPE_DIRECT_, 0, target->allocator, NULL,
        &TERN_IID_ID3D12Device, &target->list);
    if (hr < 0) {
        d3d12_unavailable("a command list could not be created");
    }
    ((PfnCloseList)TERN_SLOT(target->list, 9))(target->list);

    D3D12_DESCRIPTOR_HEAP_DESC heap_desc;
    memset(&heap_desc, 0, sizeof heap_desc);
    heap_desc.type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV_;
    heap_desc.num_descriptors = 1;
    hr = ((PfnCreateDescriptorHeap)TERN_SLOT(target->device, 14))(
        target->device, &heap_desc, &TERN_IID_ID3D12Device, &target->heap);
    if (hr < 0) {
        d3d12_unavailable("a descriptor heap could not be created");
    }
    ((PfnGetCpuHandle)TERN_SLOT(target->heap, 9))(target->heap, &target->rtv);

    D3D12_RESOURCE_DESC texture;
    memset(&texture, 0, sizeof texture);
    texture.dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D_;
    texture.width = (unsigned long long)width;
    texture.height = (unsigned)height;
    texture.depth_or_array_size = 1;
    texture.mip_levels = 1;
    texture.format = DXGI_FORMAT_R8G8B8A8_UNORM_;
    texture.sample_desc.count = 1;
    texture.layout = 0;   // D3D12_TEXTURE_LAYOUT_UNKNOWN
    texture.flags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET_;

    hr = ((PfnCreateCommittedResource)TERN_SLOT(target->device, 27))(
        target->device, &default_heap, 0, &texture, D3D12_RESOURCE_STATE_COMMON_,
        NULL, &TERN_IID_ID3D12Device, &target->target);
    if (hr < 0) {
        d3d12_unavailable("a render target could not be created");
    }
    ((PfnCreateRenderTargetView)TERN_SLOT(target->device, 20))(
        target->device, target->target, NULL, target->rtv);

    // The readback buffer is sized from the layout the device reports, so its
    // row pitch is whatever this driver wants rather than a guess.
    D3D12_PLACED_SUBRESOURCE_FOOTPRINT layout;
    unsigned rows = 0;
    unsigned long long row_size = 0;
    memset(&layout, 0, sizeof layout);
    ((PfnGetCopyableFootprints)TERN_SLOT(target->device, 38))(
        target->device, &texture, 0, 1, 0, &layout, &rows, &row_size, &target->total_bytes);
    target->row_pitch = layout.footprint.row_pitch;
    if (target->row_pitch == 0) {
        target->row_pitch = ((unsigned long long)width * 4 + 255) & ~255ull;
        target->total_bytes = target->row_pitch * (unsigned long long)height;
    }

    D3D12_RESOURCE_DESC buffer;
    memset(&buffer, 0, sizeof buffer);
    buffer.dimension = D3D12_RESOURCE_DIMENSION_BUFFER_;
    buffer.width = target->total_bytes;
    buffer.height = 1;
    buffer.depth_or_array_size = 1;
    buffer.mip_levels = 1;
    buffer.format = 0;
    buffer.sample_desc.count = 1;
    buffer.layout = 1;   // D3D12_TEXTURE_LAYOUT_ROW_MAJOR

    hr = ((PfnCreateCommittedResource)TERN_SLOT(target->device, 27))(
        target->device, &readback_heap, 0, &buffer, D3D12_RESOURCE_STATE_COPY_DEST_,
        NULL, &TERN_IID_ID3D12Device, &target->readback);
    if (hr < 0) {
        d3d12_unavailable("a readback buffer could not be created");
    }

    hr = ((PfnCreateFence)TERN_SLOT(target->device, 36))(
        target->device, 0, 0, &TERN_IID_ID3D12Device, &target->fence);
    if (hr < 0) {
        d3d12_unavailable("a fence could not be created");
    }

    target->width = width;
    target->height = height;
    target->fence_value = 0;
    target->open = 1;
    return target;
}

static void d3d12_free_device(TernD3D12Target *target) {
    d3d12_release(target->fence);
    d3d12_release(target->readback);
    d3d12_release(target->target);
    d3d12_release(target->heap);
    d3d12_release(target->list);
    d3d12_release(target->allocator);
    d3d12_release(target->queue);
    d3d12_release(target->device);
    memset(target, 0, sizeof *target);
}

// --- the namespace ---------------------------------------------------------

static Value n_d3d12_available(int argc, Value *argv) {
    (void)argc; (void)argv;
    return v_bool(d3d12_get_driver() != NULL);
}

static Value n_d3d12_info(int argc, Value *argv) {
    (void)argc; (void)argv;
    TernD3D12Driver *driver = d3d12_get_driver();
    if (!driver) {
        d3d12_unavailable("the Direct3D 12 library could not be loaded");
    }

    Map *report = map_new();
    map_set(report, str_from_cstr("name"), str_from_cstr(""));
    map_set(report, str_from_cstr("nodes"), v_int(1));

    if (driver->create_factory) {
        void *factory = NULL;
        if ((long)driver->create_factory(&TERN_IID_IDXGIFactory1, &factory) >= 0 && factory) {
            void *adapter = NULL;
            long hr = ((long (*)(void *, unsigned, void **))TERN_SLOT(factory, 12))(
                factory, 0, &adapter);
            if (hr >= 0 && adapter) {
                unsigned char description[320];
                memset(description, 0, sizeof description);
                ((PfnGetDesc1)TERN_SLOT(adapter, 10))(adapter, description);
                char utf8[256];
                int at = 0;
                // The description is UTF-16; only the BMP is copied, which is all
                // adapter names use.
                for (int i = 0; i + 1 < 256 && at < (int)sizeof utf8 - 1; i += 2) {
                    unsigned code = (unsigned)description[i] |
                                    ((unsigned)description[i + 1] << 8);
                    if (code == 0) {
                        break;
                    }
                    utf8[at++] = (char)(code < 128 ? code : '?');
                }
                utf8[at] = '\0';
                map_set(report, str_from_cstr("name"), str_from_cstr(utf8));
                d3d12_release(adapter);
            }
            d3d12_release(factory);
        }
    }
    return v_obj((Obj *)report);
}

static Value n_d3d12_create(int argc, Value *argv) {
    int width = d3d12_dimension_arg(argv[0], "width");
    int height = d3d12_dimension_arg(arg(argc, argv, 1), "height");
    TernD3D12Target *target = d3d12_make_device(width, height);
    return v_int((int64_t)(target - d3d12_targets) + 1);
}

static Value n_d3d12_clear(int argc, Value *argv) {
    TernD3D12Target *target = d3d12_slot_arg(argv[0]);
    float colour[4];
    colour[0] = (float)d3d12_unit_arg(arg(argc, argv, 1), "red");
    colour[1] = (float)d3d12_unit_arg(arg(argc, argv, 2), "green");
    colour[2] = (float)d3d12_unit_arg(arg(argc, argv, 3), "blue");
    colour[3] = (float)d3d12_unit_arg(arg(argc, argv, 4), "alpha");

    d3d12_reset(target);
    d3d12_barrier(target, target->target, D3D12_RESOURCE_STATE_COMMON_, D3D12_RESOURCE_STATE_RENDER_TARGET_);
    ((PfnOMSetRenderTargets)TERN_SLOT(target->list, 46))(
        target->list, 1, &target->rtv, 0, NULL);
    ((PfnClearRenderTargetView)TERN_SLOT(target->list, 48))(
        target->list, target->rtv, colour, 0, NULL);
    d3d12_barrier(target, target->target, D3D12_RESOURCE_STATE_RENDER_TARGET_, D3D12_RESOURCE_STATE_COPY_SOURCE_);
    d3d12_submit(target);
    return v_nil();
}

static void d3d12_copy_out(TernD3D12Target *target) {
    d3d12_reset(target);
    D3D12_TEXTURE_COPY_LOCATION destination;
    memset(&destination, 0, sizeof destination);
    destination.resource = target->readback;
    destination.type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT_;
    destination.u.placed_footprint.footprint.format = DXGI_FORMAT_R8G8B8A8_UNORM_;
    destination.u.placed_footprint.footprint.width = (unsigned)target->width;
    destination.u.placed_footprint.footprint.height = (unsigned)target->height;
    destination.u.placed_footprint.footprint.depth = 1;
    destination.u.placed_footprint.footprint.row_pitch = (unsigned)target->row_pitch;

    D3D12_TEXTURE_COPY_LOCATION source;
    memset(&source, 0, sizeof source);
    source.resource = target->target;
    source.type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX_;
    source.u.subresource_index = 0;

    ((PfnCopyTextureRegion)TERN_SLOT(target->list, 16))(
        target->list, &destination, 0, 0, &source, NULL);

    d3d12_barrier(target, target->target, D3D12_RESOURCE_STATE_COPY_SOURCE_,
                  D3D12_RESOURCE_STATE_COMMON_);
    d3d12_submit(target);
}

static Value n_d3d12_pixels(int argc, Value *argv) {
    (void)argc;
    TernD3D12Target *target = d3d12_slot_arg(argv[0]);
    d3d12_copy_out(target);

    D3D12_RANGE range;
    range.begin = 0;
    range.end = 0;
    void *data = NULL;
    long hr = ((PfnMap)TERN_SLOT(target->readback, 8))(target->readback, 0, &range, &data);
    if (hr < 0 || !data) {
        tern_panic("D3D12Error", "the readback buffer could not be mapped (0x%08lx)",
                   (unsigned long)hr & 0xFFFFFFFFul);
    }

    // Bottom row first, like gl.pixels, so a script does not care which backend
    // drew the picture.
    size_t row_bytes = (size_t)target->width * 4;
    List *list = list_new((int)(row_bytes * (size_t)target->height));
    for (int y = target->height - 1; y >= 0; y--) {
        const unsigned char *row = (const unsigned char *)data + (size_t)y * target->row_pitch;
        for (size_t i = 0; i < row_bytes; i++) {
            list_push(list, v_int(row[i]));
        }
    }

    ((PfnUnmap)TERN_SLOT(target->readback, 9))(target->readback, 0, &range);
    return v_obj((Obj *)list);
}

static Value n_d3d12_save(int argc, Value *argv) {
    (void)argc;
    TernD3D12Target *target = d3d12_slot_arg(argv[0]);
    const char *path = path_arg(argc, argv, 1);
    d3d12_copy_out(target);

    D3D12_RANGE range;
    range.begin = 0;
    range.end = 0;
    void *data = NULL;
    long hr = ((PfnMap)TERN_SLOT(target->readback, 8))(target->readback, 0, &range, &data);
    if (hr < 0 || !data) {
        tern_panic("D3D12Error", "the readback buffer could not be mapped (0x%08lx)",
                   (unsigned long)hr & 0xFFFFFFFFul);
    }

    size_t row_bytes = (size_t)target->width * 4;
    unsigned char *tight = (unsigned char *)malloc(row_bytes * (size_t)target->height);
    if (!tight) {
        tern_panic("IOError", "cannot write '%s': out of memory", path);
    }
    for (int y = 0; y < target->height; y++) {
        memcpy(tight + (size_t)y * row_bytes,
               (const unsigned char *)data + (size_t)y * target->row_pitch, row_bytes);
    }
    ((PfnUnmap)TERN_SLOT(target->readback, 9))(target->readback, 0, &range);

    tern_png_write(path, target->width, target->height, tight, 1);
    free(tight);
    return v_nil();
}

static Value n_d3d12_size(int argc, Value *argv) {
    (void)argc;
    TernD3D12Target *target = d3d12_slot_arg(argv[0]);
    List *list = list_new(2);
    list_push(list, v_int(target->width));
    list_push(list, v_int(target->height));
    return v_obj((Obj *)list);
}

static Value n_d3d12_close(int argc, Value *argv) {
    (void)argc;
    TernD3D12Target *target = d3d12_slot_arg(argv[0]);
    d3d12_free_device(target);
    return v_nil();
}

#endif  // TERN_D3D12_H
