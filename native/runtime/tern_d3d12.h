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

#include <stddef.h>
#include <stdint.h>
#include <string.h>

// The window layer: Win32 loaded at run time, so a frame can be shown on screen
// instead of only written to a PNG. Optional - without it d3d12.window() answers
// nil and everything else still works.
#include "tern_window.h"

#define D3D12_MAX_DEVICES 4
#define D3D12_MAX_PROGRAMS 32
#define D3D12_VERTEX_FLOATS 7     // x, y, z, r, g, b, a in the buffer
#define D3D12_VERTEX_STRIDE (D3D12_VERTEX_FLOATS * 4)
#define D3D12_MAX_DIMENSION 16384
#define D3D12_WAIT_TIMEOUT_MS 30000

// The revision of this binding, reported by d3d12.info() and printed by
// examples/rotating_cube.tern. It exists because a report of
// "the readback buffer could not be mapped (0x887a0005)" was traced to a binary
// built before the fix that caused it: the two are indistinguishable without a
// stamp, and the fix looked like it had failed. Bump it whenever the D3D12 or
// window code changes what a failure means.
#define TERN_D3D12_BUILD "2026-10-31.5"

// --- the structures, with the offsets the SDK headers give them -------------

typedef struct {
    uint32_t a;
    uint16_t b, c;
    unsigned char d[8];
} TernGuid;

// Every Create* on the device takes the interface id it should hand back, and
// answers E_NOINTERFACE (0x80004002) when it does not recognise the one it was
// given. Passing the device's own id to CreateCommandQueue is therefore not
// merely untidy: it fails, on hardware, at the first call.
//
// These are copied from the uuid() attributes in d3d12.idl and dxgi.idl, which
// is what the SDK's DEFINE_GUID lines come from. They were wrong before — a
// device id with a correct first word and an invented tail — and nothing here
// could notice, because the mock driver never looked at the id it was passed.
// tools/d3d12/mock_d3d12.c now checks every one of them and answers
// E_NOINTERFACE exactly as Windows does, so a wrong id fails on the build
// machine instead of on a user's GPU.
static const TernGuid TERN_IID_ID3D12Device = {
    0x189819f1u, 0x1db6u, 0x4b57u, { 0xbe, 0x54, 0x18, 0x21, 0x33, 0x9b, 0x85, 0xf7 }
};
static const TernGuid TERN_IID_ID3D12CommandQueue = {
    0x0ec870a6u, 0x5d7eu, 0x4c22u, { 0x8c, 0xfc, 0x5b, 0xaa, 0xe0, 0x76, 0x16, 0xed }
};
static const TernGuid TERN_IID_ID3D12CommandAllocator = {
    0x6102dee4u, 0xaf59u, 0x4b09u, { 0xb9, 0x99, 0xb4, 0x4d, 0x73, 0xf0, 0x9b, 0x24 }
};
static const TernGuid TERN_IID_ID3D12GraphicsCommandList = {
    0x5b160d0fu, 0xac1bu, 0x4185u, { 0x8b, 0xa8, 0xb3, 0xae, 0x42, 0xa5, 0xa4, 0x55 }
};
static const TernGuid TERN_IID_ID3D12DescriptorHeap = {
    0x8efb471du, 0x616cu, 0x4f49u, { 0x90, 0xf7, 0x12, 0x7b, 0xb7, 0x63, 0xfa, 0x51 }
};
static const TernGuid TERN_IID_ID3D12Resource = {
    0x696442beu, 0xa72eu, 0x4059u, { 0xbc, 0x79, 0x5b, 0x5c, 0x98, 0x04, 0x0f, 0xad }
};
static const TernGuid TERN_IID_ID3D12Fence = {
    0x0a753dcfu, 0xc4d8u, 0x4b91u, { 0xad, 0xf6, 0xbe, 0x5a, 0x60, 0xd9, 0x5a, 0x76 }
};
static const TernGuid TERN_IID_ID3D12RootSignature = {
    0xc54a6b66u, 0x72dfu, 0x4ee8u, { 0x8b, 0xe5, 0xa9, 0x46, 0xa1, 0x42, 0x92, 0x14 }
};
static const TernGuid TERN_IID_ID3D12PipelineState = {
    0x765a30f3u, 0xf624u, 0x4c6fu, { 0xa8, 0x28, 0xac, 0xe9, 0x48, 0x62, 0x24, 0x45 }
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

// The pipeline structures. The sizes and offsets here were printed from the
// shipped d3d12.h and are asserted below, so a build on Linux fails loudly if a
// mirror drifts; on Windows the real header is used instead and the compiler
// checks every field by name.
typedef struct {
    const void *bytecode;
    size_t length;
} D3D12_SHADER_BYTECODE;        // 16

typedef struct {
    const void *declarations;
    unsigned entries;
    const unsigned *strides;
    unsigned stride_count;
    unsigned rasterized_stream;
} D3D12_STREAM_OUTPUT_DESC;     // 32

typedef struct {
    int blend_enable;
    int logic_op_enable;
    int src_blend;
    int dest_blend;
    int blend_op;
    int src_blend_alpha;
    int dest_blend_alpha;
    int blend_op_alpha;
    int logic_op;
    unsigned char write_mask;
    unsigned char pad[3];
} D3D12_RENDER_TARGET_BLEND_DESC; // 40

typedef struct {
    int alpha_to_coverage;
    int independent_blend;
    D3D12_RENDER_TARGET_BLEND_DESC render_target[8];
} D3D12_BLEND_DESC;              // 328

typedef struct {
    int fill_mode;
    int cull_mode;
    int front_counter_clockwise;
    int depth_bias;
    float depth_bias_clamp;
    float slope_scaled_depth_bias;
    int depth_clip_enable;
    int multisample_enable;
    int antialiased_line_enable;
    unsigned forced_sample_count;
    int conservative_raster;
} D3D12_RASTERIZER_DESC;        // 44

typedef struct {
    int stencil_fail_op;
    int stencil_depth_fail_op;
    int stencil_pass_op;
    int stencil_func;
} D3D12_DEPTH_STENCILOP_DESC;   // 16

typedef struct {
    int depth_enable;
    int depth_write_mask;
    int depth_func;
    int stencil_enable;
    unsigned char stencil_read_mask;
    unsigned char stencil_write_mask;
    unsigned char pad[2];
    D3D12_DEPTH_STENCILOP_DESC front_face;
    D3D12_DEPTH_STENCILOP_DESC back_face;
} D3D12_DEPTH_STENCIL_DESC;     // 52

typedef struct {
    const void *blob;
    size_t size;
} D3D12_CACHED_PIPELINE_STATE;  // 16

typedef struct {
    const char *semantic_name;
    unsigned semantic_index;
    int format;
    unsigned input_slot;
    unsigned aligned_byte_offset;
    int input_slot_class;
    unsigned instance_data_step_rate;
} D3D12_INPUT_ELEMENT_DESC;     // 32

typedef struct {
    const D3D12_INPUT_ELEMENT_DESC *elements;
    unsigned count;
} D3D12_INPUT_LAYOUT_DESC;     // 16

typedef struct {
    void *root_signature;
    D3D12_SHADER_BYTECODE vs;
    D3D12_SHADER_BYTECODE ps;
    D3D12_SHADER_BYTECODE ds;
    D3D12_SHADER_BYTECODE hs;
    D3D12_SHADER_BYTECODE gs;
    D3D12_STREAM_OUTPUT_DESC stream_output;
    D3D12_BLEND_DESC blend_state;
    unsigned sample_mask;
    D3D12_RASTERIZER_DESC rasterizer_state;
    D3D12_DEPTH_STENCIL_DESC depth_stencil_state;
    D3D12_INPUT_LAYOUT_DESC input_layout;
    int ib_strip_cut_value;
    int primitive_topology_type;
    unsigned render_target_count;
    int render_target_formats[8];
    int dsv_format;
    TernSampleDesc sample_desc;
    unsigned node_mask;
    D3D12_CACHED_PIPELINE_STATE cached_pso;
    int flags;
} D3D12_GRAPHICS_PIPELINE_STATE_DESC;   // 656

typedef struct {
    unsigned num_parameters;
    const void *parameters;
    unsigned num_static_samplers;
    const void *static_samplers;
    unsigned flags;
} D3D12_ROOT_SIGNATURE_DESC;    // 40

typedef struct {
    unsigned long long buffer_location;
    unsigned size_in_bytes;
    unsigned stride_in_bytes;
} D3D12_VERTEX_BUFFER_VIEW;     // 16

// The values the pipeline needs, from the same headers.
#define D3D12_HEAP_TYPE_UPLOAD_ 2
#define D3D12_RESOURCE_STATE_GENERIC_READ_ 2755
#define D3D12_ROOT_SIGNATURE_FLAG_ALLOW_IA_ 1
#define D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE_ 3
#define D3D12_PRIMITIVE_TOPOLOGY_TRIANGLELIST_ 4
#define D3D12_FILL_MODE_SOLID_ 3
#define D3D12_CULL_MODE_NONE_ 1
#define D3D12_COMPARISON_FUNC_ALWAYS_ 8
#define D3D12_BLEND_ONE_ 2
#define D3D12_BLEND_OP_ADD_ 1
#define D3D12_LOGIC_OP_NOOP_ 4
#define DXGI_FORMAT_R32G32B32_FLOAT_ 6
#define DXGI_FORMAT_R32G32B32A32_FLOAT_ 2
#define DXGI_FORMAT_UNKNOWN_ 0
#define D3D_ROOT_SIGNATURE_VERSION_1_ 1

// The same numbers as the SDK, checked at compile time on the platform where
// this mirror is actually used. tools/d3d12/gen_layout_check.py turns these into
// assertions against the real d3d12.h, which the Windows build compiles.
_Static_assert(sizeof(D3D12_HEAP_PROPERTIES) == 20, "heap properties size");
_Static_assert(offsetof(D3D12_HEAP_PROPERTIES, cpu_page_property) == 4, "heap cpu page offset");
_Static_assert(sizeof(D3D12_RESOURCE_DESC) == 56, "resource desc size");
_Static_assert(offsetof(D3D12_RESOURCE_DESC, width) == 16, "resource width offset");
_Static_assert(offsetof(D3D12_RESOURCE_DESC, format) == 32, "resource format offset");
_Static_assert(sizeof(D3D12_DESCRIPTOR_HEAP_DESC) == 16, "descriptor heap desc size");
_Static_assert(sizeof(D3D12_RESOURCE_BARRIER) == 32, "barrier size");
_Static_assert(offsetof(D3D12_RESOURCE_BARRIER, u) == 8, "barrier union offset");
_Static_assert(sizeof(D3D12_RANGE) == 16, "range size");
_Static_assert(sizeof(D3D12_PLACED_SUBRESOURCE_FOOTPRINT) == 32, "placed footprint size");
_Static_assert(offsetof(D3D12_PLACED_SUBRESOURCE_FOOTPRINT, footprint) == 8, "footprint offset");
_Static_assert(sizeof(D3D12_TEXTURE_COPY_LOCATION) == 48, "copy location size");
_Static_assert(offsetof(D3D12_TEXTURE_COPY_LOCATION, u) == 16, "copy location union offset");
_Static_assert(sizeof(D3D12_CPU_DESCRIPTOR_HANDLE) == 8, "cpu handle size");
_Static_assert(sizeof(D3D12_SHADER_BYTECODE) == 16, "shader bytecode size");
_Static_assert(sizeof(D3D12_STREAM_OUTPUT_DESC) == 32, "stream output size");
_Static_assert(sizeof(D3D12_RENDER_TARGET_BLEND_DESC) == 40, "blend target size");
_Static_assert(sizeof(D3D12_CACHED_PIPELINE_STATE) == 16, "cached pso size");
_Static_assert(sizeof(D3D12_INPUT_LAYOUT_DESC) == 16, "input layout size");
_Static_assert(sizeof(D3D12_DEPTH_STENCILOP_DESC) == 16, "stencil op size");
_Static_assert(sizeof(D3D12_GRAPHICS_PIPELINE_STATE_DESC) == 656, "pipeline state size");
_Static_assert(sizeof(D3D12_BLEND_DESC) == 328, "blend state size");
_Static_assert(sizeof(D3D12_RASTERIZER_DESC) == 44, "rasterizer state size");
_Static_assert(sizeof(D3D12_DEPTH_STENCIL_DESC) == 52, "depth stencil state size");
_Static_assert(sizeof(D3D12_ROOT_SIGNATURE_DESC) == 40, "root signature desc size");
_Static_assert(sizeof(D3D12_VERTEX_BUFFER_VIEW) == 16, "vertex buffer view size");
_Static_assert(sizeof(D3D12_INPUT_ELEMENT_DESC) == 32, "input element size");
_Static_assert(offsetof(D3D12_GRAPHICS_PIPELINE_STATE_DESC, blend_state) == 120, "blend offset");
_Static_assert(offsetof(D3D12_GRAPHICS_PIPELINE_STATE_DESC, rasterizer_state) == 452, "rasterizer offset");
_Static_assert(offsetof(D3D12_GRAPHICS_PIPELINE_STATE_DESC, input_layout) == 552, "input layout offset");
_Static_assert(offsetof(D3D12_GRAPHICS_PIPELINE_STATE_DESC, render_target_formats) == 580, "rtv format offset");

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

typedef long (*PfnD3D12CreateDevice)(void *, int, const TernGuid *, void **);
typedef long (*PfnCreateDXGIFactory1)(const TernGuid *, void **);
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
    // The state the render target is actually in, so the next barrier can name
    // it correctly. D3D12 compares this against what it was told and the result
    // of a mismatch is undefined: the debug layer complains, and a driver may
    // remove the device, which surfaces later as an unrelated call failing.
    unsigned state;
    int width;
    int height;
    int open;
    void *vertex_buffer;
    void *programs[D3D12_MAX_PROGRAMS];
    void *signatures[D3D12_MAX_PROGRAMS];
    int program_count;
} TernD3D12Target;

static TernD3D12Target d3d12_targets[D3D12_MAX_DEVICES];

static void d3d12_release(void *object) {
    if (!object) {
        return;
    }
    ((unsigned long (*)(void *))TERN_SLOT(object, 2))(object);   // Release
}

static const char *d3d12_removed_reason(TernD3D12Target *target);

/// Fails when the device is gone, naming the call that broke it.
///
/// DXGI_ERROR_DEVICE_REMOVED is reported at whatever call next touches the
/// device, which is rarely the one that removed it: the report that started
/// this was a map in save_frame failing because a barrier three frames earlier
/// had lied. Asking after every submit turns "somewhere before this" into
/// "during this call".
static void d3d12_check_device(TernD3D12Target *target, const char *what);

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

static void d3d12_submit(TernD3D12Target *target, const char *what) {
    ((long (*)(void *))TERN_SLOT(target->list, 9))(target->list);            // Close
    void *lists[1] = { target->list };
    ((PfnExecuteCommandLists)TERN_SLOT(target->queue, 10))(target->queue, 1, lists);
    target->fence_value++;
    ((PfnFenceSignal)TERN_SLOT(target->queue, 14))(target->queue, target->fence, target->fence_value);
    if (d3d12_wait(target) != 0) {
        const char *reason = d3d12_removed_reason(target);
        if (reason && reason[0]) {
            tern_panic("D3D12Error", "the GPU did not finish within %d ms during %s: %s",
                       D3D12_WAIT_TIMEOUT_MS, what, reason);
        }
        tern_panic("D3D12Error", "the GPU did not finish within %d ms during %s",
                   D3D12_WAIT_TIMEOUT_MS, what);
    }
    d3d12_check_device(target, what);
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

/// Moves the render target to `after`, naming the state it is really in.
///
/// This replaces the hard-coded pairs the calls used to write. `clear` leaves
/// the target in COPY_SOURCE and `draw` then announced it as COMMON, which is
/// not what the resource was in: a script that clears and draws (every frame of
/// every program) told the driver a lie on each frame.
static void d3d12_transition(TernD3D12Target *target, unsigned after) {
    if (target->state == after) {
        return;
    }
    d3d12_barrier(target, target->target, target->state, after);
    target->state = after;
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

/// Why the device was removed, when it was: GetDeviceRemovedReason gives the
/// fault, and DXGI_ERROR_DEVICE_REMOVED on its own says only that something went
/// wrong earlier, often in a different call. Returns an empty string when the
/// device is fine.
static const char *d3d12_removed_reason(TernD3D12Target *target) {
    if (!target || !target->device) {
        return "";
    }
    long reason = ((long (*)(void *))TERN_SLOT(target->device, 37))(target->device);
    switch ((unsigned long)reason & 0xFFFFFFFFul) {
    case 0: return "";
    case 0x887A0005ul: return "the device was removed";
    case 0x887A0006ul: return "the GPU hung and the driver reset it (TDR)";
    case 0x887A0007ul: return "the device was reset";
    case 0x887A0020ul: return "the driver hit an internal error";
    case 0x8007000Eul: return "out of memory";
    default: return "the device was removed for an unknown reason";
    }
}

static void d3d12_check_device(TernD3D12Target *target, const char *what) {
    const char *reason = d3d12_removed_reason(target);
    if (reason && reason[0]) {
        tern_panic("D3D12Error", "the device was removed during %s: %s", what, reason);
    }
}

/// What an HRESULT from this file means, in words a person can act on.
///
/// The raw code is always kept, because it is the only thing that makes a bug
/// report precise, but 0x80004002 sent a user looking at their machine when the
/// real answer was "TERN passed the wrong interface id".
static const char *d3d12_explain(long hr) {
    switch ((unsigned long)hr & 0xFFFFFFFFul) {
    case 0x80004002ul:
        return "an interface id was rejected - this is a bug in TERN's Direct3D "
               "binding, not in this machine";
    case 0x887A0004ul:
        return "no adapter on this machine supports Direct3D 12 at feature level "
               "11_0; a driver update may add it";
    case 0x887A0005ul:
        return "the graphics driver was reset while the call was running";
    case 0x887A0006ul:
        return "the graphics driver stopped responding and was reset";
    case 0x80070057ul:
        return "the description was rejected as invalid - this is a bug in TERN";
    case 0x8007000Eul:
        return "out of memory";
    case 0x80070005ul:
        return "access was denied - a remote desktop session or a sandbox "
               "without GPU access looks like this";
    default:
        return NULL;
    }
}

static void d3d12_fail(const char *what, long hr);

/// Panics with the HRESULT, what it means, and - when the device is gone - the
/// reason the driver gave, which is the only part that points at the fault.
static void d3d12_fail_removed(TernD3D12Target *target, const char *what, long hr) {
    const char *reason = ((unsigned long)hr & 0xFFFFFFFFul) == 0x887A0005ul
        ? d3d12_removed_reason(target)
        : NULL;
    if (reason && reason[0]) {
        tern_panic("D3D12Error", "%s: %s (0x%08lx)", what, reason,
                   (unsigned long)hr & 0xFFFFFFFFul);
    }
    d3d12_fail(what, hr);
}

/// Panics with the HRESULT and, when it is known, what it means.
static void d3d12_fail(const char *what, long hr) {
    const char *reason = d3d12_explain(hr);
    if (reason) {
        tern_panic("D3D12Error", "%s: %s (0x%08lx)", what, reason,
                   (unsigned long)hr & 0xFFFFFFFFul);
    }
    tern_panic("D3D12Error", "%s (0x%08lx)", what, (unsigned long)hr & 0xFFFFFFFFul);
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

    long hr = driver->create_device(NULL, D3D_FEATURE_LEVEL_11_0_,
                                    &TERN_IID_ID3D12Device, &target->device);
    if (hr < 0 || !target->device) {
        d3d12_fail("Direct3D 12 is not available on this system: "
                   "D3D12CreateDevice failed", hr);
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
        target->device, &queue_desc, &TERN_IID_ID3D12CommandQueue, &target->queue);
    if (hr < 0) {
        d3d12_unavailable("a command queue could not be created");
    }

    hr = ((PfnCreateCommandAllocator)TERN_SLOT(target->device, 9))(
        target->device, D3D12_COMMAND_LIST_TYPE_DIRECT_, &TERN_IID_ID3D12CommandAllocator,
        &target->allocator);
    if (hr < 0) {
        d3d12_unavailable("a command allocator could not be created");
    }

    hr = ((PfnCreateCommandList)TERN_SLOT(target->device, 12))(
        target->device, D3D12_COMMAND_LIST_TYPE_DIRECT_, 0, target->allocator, NULL,
        &TERN_IID_ID3D12GraphicsCommandList, &target->list);
    if (hr < 0) {
        d3d12_unavailable("a command list could not be created");
    }
    ((PfnCloseList)TERN_SLOT(target->list, 9))(target->list);

    D3D12_DESCRIPTOR_HEAP_DESC heap_desc;
    memset(&heap_desc, 0, sizeof heap_desc);
    heap_desc.type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV_;
    heap_desc.num_descriptors = 1;
    hr = ((PfnCreateDescriptorHeap)TERN_SLOT(target->device, 14))(
        target->device, &heap_desc, &TERN_IID_ID3D12DescriptorHeap, &target->heap);
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
        NULL, &TERN_IID_ID3D12Resource, &target->target);
    if (hr < 0) {
        d3d12_unavailable("a render target could not be created");
    }
    ((PfnCreateRenderTargetView)TERN_SLOT(target->device, 20))(
        target->device, target->target, NULL, target->rtv);
    target->state = D3D12_RESOURCE_STATE_COMMON_;

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
        NULL, &TERN_IID_ID3D12Resource, &target->readback);
    if (hr < 0) {
        d3d12_unavailable("a readback buffer could not be created");
    }

    hr = ((PfnCreateFence)TERN_SLOT(target->device, 36))(
        target->device, 0, 0, &TERN_IID_ID3D12Fence, &target->fence);
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
    for (int i = 0; i < target->program_count; i++) {
        d3d12_release(target->programs[i]);
        d3d12_release(target->signatures[i]);
    }
    d3d12_release(target->vertex_buffer);
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

// --- the pipeline ----------------------------------------------------------
//
// Shaders are HLSL source, compiled at run time by d3dcompiler_47.dll, which
// ships with Windows. The entry point is `main` in both stages; the vertex input
// is POSITION (x, y, z) followed by COLOR (r, g, b, a), which is what
// `d3d12.draw` packs from a script's x, y, r, g, b, a.

typedef long (*PfnD3DCompile)(const void *, size_t, const char *, const void *, void *,
                              const char *, const char *, unsigned, unsigned, void **, void **);
typedef long (*PfnSerializeRootSignature)(const D3D12_ROOT_SIGNATURE_DESC *, int, void **, void **);

static void *d3d_compiler_library;
static PfnD3DCompile d3d_compile;

static PfnD3DCompile d3d12_compiler(void) {
    if (d3d_compiler_library) {
        return d3d_compile;
    }
    // Only LoadLibrary is used here (not GetProcAddress) because the symbol is
    // exported straight from the compiler DLL.
    const char *configured = getenv("TERN_D3DCOMPILER_LIBRARY");
#ifdef _WIN32
    const char *default_name = "d3dcompiler_47.dll";
#else
    const char *default_name = "libd3dcompiler_47.so";
#endif
    d3d_compiler_library = tern_lib_open(configured && configured[0] ? configured : default_name);
    if (d3d_compiler_library) {
        *(void **)(&d3d_compile) = tern_lib_symbol(d3d_compiler_library, "D3DCompile");
    }
    return d3d_compile;
}

static const void *blob_bytes(void *blob, size_t *length) {
    *length = ((size_t (*)(void *))TERN_SLOT(blob, 4))(blob);          // GetBufferSize
    return ((const void *(*)(void *))TERN_SLOT(blob, 3))(blob);        // GetBufferPointer
}

/// The first line of a blob, trimmed. D3DCompile writes a multi-line report whose
/// first line names the file, the position and the error; the rest is the source
/// excerpt and caret, which is noise in a TERN message.
static char *blob_text(void *blob) {
    if (!blob) {
        return NULL;
    }
    size_t length = 0;
    const char *text = (const char *)blob_bytes(blob, &length);
    size_t end = 0;
    while (end < length && text[end] != '\r' && text[end] != '\n') {
        end++;
    }
    while (end > 0 && (text[end - 1] == ' ' || text[end - 1] == '\t')) {
        end--;
    }
    char *copy = (char *)malloc(end + 1);
    if (!copy) {
        return NULL;
    }
    memcpy(copy, text, end);
    copy[end] = '\0';
    return copy;
}

/// Compiles one stage and returns its blob, or raises with the compiler's message.
static void *d3d12_compile_stage(const char *source, const char *target, const char *which) {
    PfnD3DCompile compile = d3d12_compiler();
    if (!compile) {
        tern_panic("D3D12Error",
                   "the shader compiler is not available: d3dcompiler_47 could not be loaded");
    }

    void *code = NULL;
    void *errors = NULL;
    long hr = compile(source, strlen(source), "tern.hlsl", NULL, NULL, "main", target, 0, 0,
                      &code, &errors);
    if (hr < 0 || !code) {
        char *text = blob_text(errors);
        if (text && text[0]) {
            tern_panic("D3D12Error", "the %s shader did not compile: %s", which, text);
        }
        free(text);
        tern_panic("D3D12Error", "the %s shader did not compile (0x%08lx)", which,
                   (unsigned long)hr & 0xFFFFFFFFul);
    }
    if (errors) {
        d3d12_release(errors);
    }
    return code;
}

static void *d3d12_empty_root_signature(void *device) {
    D3D12_ROOT_SIGNATURE_DESC description;
    memset(&description, 0, sizeof description);
    description.flags = D3D12_ROOT_SIGNATURE_FLAG_ALLOW_IA_;

    PfnSerializeRootSignature serialize =
        (PfnSerializeRootSignature)tern_lib_symbol(d3d12_get_driver()->d3d12,
                                                   "D3D12SerializeRootSignature");
    if (!serialize) {
        tern_panic("D3D12Error",
                   "Direct3D 12 is not available on this system: D3D12SerializeRootSignature is missing");
    }

    void *blob = NULL;
    void *errors = NULL;
    if ((long)serialize(&description, D3D_ROOT_SIGNATURE_VERSION_1_, &blob, &errors) < 0 || !blob) {
        char *text = blob_text(errors);
        tern_panic("D3D12Error", "the root signature could not be serialized%s%s",
                   text ? ": " : "", text ? text : "");
    }
    if (errors) {
        d3d12_release(errors);
    }

    size_t length = 0;
    const void *bytes = blob_bytes(blob, &length);
    void *signature = NULL;
    long hr = ((long (*)(void *, unsigned, const void *, size_t, const TernGuid *, void **))
               TERN_SLOT(device, 16))(device, 0, bytes, length,
                                   &TERN_IID_ID3D12RootSignature, &signature);
    d3d12_release(blob);
    if (hr < 0 || !signature) {
        tern_panic("D3D12Error", "the root signature could not be created (0x%08lx)",
                   (unsigned long)hr & 0xFFFFFFFFul);
    }
    return signature;
}

static void *d3d12_make_program(TernD3D12Target *target, const char *vertex_source,
                                const char *pixel_source) {
    void *vertex = d3d12_compile_stage(vertex_source, "vs_5_0", "vertex");
    void *pixel = d3d12_compile_stage(pixel_source, "ps_5_0", "pixel");
    void *signature = d3d12_empty_root_signature(target->device);

    size_t vertex_length = 0, pixel_length = 0;
    const void *vertex_bytes = blob_bytes(vertex, &vertex_length);
    const void *pixel_bytes = blob_bytes(pixel, &pixel_length);

    static const D3D12_INPUT_ELEMENT_DESC elements[2] = {
        { "POSITION", 0, DXGI_FORMAT_R32G32B32_FLOAT_, 0, 0, 0, 0 },
        { "COLOR", 0, DXGI_FORMAT_R32G32B32A32_FLOAT_, 0, 12, 0, 0 },
    };

    D3D12_GRAPHICS_PIPELINE_STATE_DESC description;
    memset(&description, 0, sizeof description);
    description.root_signature = signature;
    description.vs.bytecode = vertex_bytes;
    description.vs.length = vertex_length;
    description.ps.bytecode = pixel_bytes;
    description.ps.length = pixel_length;
    for (int i = 0; i < 8; i++) {
        description.blend_state.render_target[i].src_blend = D3D12_BLEND_ONE_;
        description.blend_state.render_target[i].dest_blend = 0;   // D3D12_BLEND_ZERO
        description.blend_state.render_target[i].blend_op = D3D12_BLEND_OP_ADD_;
        description.blend_state.render_target[i].src_blend_alpha = D3D12_BLEND_ONE_;
        description.blend_state.render_target[i].dest_blend_alpha = 0;
        description.blend_state.render_target[i].blend_op_alpha = D3D12_BLEND_OP_ADD_;
        description.blend_state.render_target[i].logic_op = D3D12_LOGIC_OP_NOOP_;
        description.blend_state.render_target[i].write_mask = 0xF;
    }
    description.sample_mask = 0xFFFFFFFFu;
    description.rasterizer_state.fill_mode = D3D12_FILL_MODE_SOLID_;
    description.rasterizer_state.cull_mode = D3D12_CULL_MODE_NONE_;
    description.rasterizer_state.depth_clip_enable = 1;
    description.depth_stencil_state.depth_func = D3D12_COMPARISON_FUNC_ALWAYS_;
    description.depth_stencil_state.front_face.stencil_func = D3D12_COMPARISON_FUNC_ALWAYS_;
    description.depth_stencil_state.back_face.stencil_func = D3D12_COMPARISON_FUNC_ALWAYS_;
    description.input_layout.elements = elements;
    description.input_layout.count = 2;
    description.primitive_topology_type = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE_;
    description.render_target_count = 1;
    description.render_target_formats[0] = DXGI_FORMAT_R8G8B8A8_UNORM_;
    description.dsv_format = DXGI_FORMAT_UNKNOWN_;
    description.sample_desc.count = 1;

    void *pipeline = NULL;
    long hr = ((long (*)(void *, const D3D12_GRAPHICS_PIPELINE_STATE_DESC *, const TernGuid *, void **))
               TERN_SLOT(target->device, 10))(target->device, &description,
                                              &TERN_IID_ID3D12PipelineState, &pipeline);
    d3d12_release(vertex);
    d3d12_release(pixel);
    if (hr < 0 || !pipeline) {
        d3d12_release(signature);
        tern_panic("D3D12Error", "the pipeline state could not be created (0x%08lx)",
                   (unsigned long)hr & 0xFFFFFFFFul);
    }

    if (target->program_count >= D3D12_MAX_PROGRAMS) {
        d3d12_release(pipeline);
        d3d12_release(signature);
        tern_panic("D3D12Error", "too many pipelines (%d)", D3D12_MAX_PROGRAMS);
    }
    target->programs[target->program_count] = pipeline;
    target->signatures[target->program_count] = signature;
    return (void *)(size_t)(++target->program_count);
}

/// Copies the script's vertices into a fresh upload buffer and returns the view.
static D3D12_VERTEX_BUFFER_VIEW d3d12_upload_vertices(TernD3D12Target *target, Value value) {
    if (!v_is_list(value)) {
        tern_panic("TypeError", "expected a list of vertices, not %s", tern_type_name(value));
    }
    List *list = (List *)value.as.o;
    if (list->count % 6 != 0) {
        tern_panic("ArgumentError",
                   "vertices must be groups of 6 numbers (x, y, r, g, b, a), not %d", list->count);
    }

    size_t vertices = (size_t)list->count / 6;
    size_t bytes = vertices * D3D12_VERTEX_STRIDE;
    if (bytes == 0) {
        bytes = D3D12_VERTEX_STRIDE;
    }

    D3D12_HEAP_PROPERTIES upload_heap;
    memset(&upload_heap, 0, sizeof upload_heap);
    upload_heap.type = D3D12_HEAP_TYPE_UPLOAD_;

    D3D12_RESOURCE_DESC buffer;
    memset(&buffer, 0, sizeof buffer);
    buffer.dimension = D3D12_RESOURCE_DIMENSION_BUFFER_;
    buffer.width = bytes;
    buffer.height = 1;
    buffer.depth_or_array_size = 1;
    buffer.mip_levels = 1;
    buffer.layout = 1;   // ROW_MAJOR
    buffer.sample_desc.count = 1;

    void *resource = NULL;
    long hr = ((PfnCreateCommittedResource)TERN_SLOT(target->device, 27))(
        target->device, &upload_heap, 0, &buffer, D3D12_RESOURCE_STATE_GENERIC_READ_,
        NULL, &TERN_IID_ID3D12Resource, &resource);
    if (hr < 0 || !resource) {
        tern_panic("D3D12Error", "the vertex buffer could not be created (0x%08lx)",
                   (unsigned long)hr & 0xFFFFFFFFul);
    }

    D3D12_RANGE range;
    range.begin = 0;
    range.end = 0;
    void *data = NULL;
    hr = ((PfnMap)TERN_SLOT(resource, 8))(resource, 0, &range, &data);
    if (hr < 0 || !data) {
        d3d12_release(resource);
        d3d12_fail_removed(target, "the vertex buffer could not be mapped", hr);
    }

    float *packed = (float *)data;
    for (size_t i = 0; i < vertices; i++) {
        for (int field = 0; field < 6; field++) {
            Value item = list->items[i * 6 + (size_t)field];
            if (!v_is_num(item)) {
                ((PfnUnmap)TERN_SLOT(resource, 9))(resource, 0, &range);
                d3d12_release(resource);
                tern_panic("TypeError", "vertex %d must be a number, not %s",
                           (int)(i * 6 + (size_t)field), tern_type_name(item));
            }
        }
        packed[i * D3D12_VERTEX_FLOATS + 0] = (float)v_as_double(list->items[i * 6 + 0]);
        packed[i * D3D12_VERTEX_FLOATS + 1] = (float)v_as_double(list->items[i * 6 + 1]);
        packed[i * D3D12_VERTEX_FLOATS + 2] = 0.0f;
        packed[i * D3D12_VERTEX_FLOATS + 3] = (float)v_as_double(list->items[i * 6 + 2]);
        packed[i * D3D12_VERTEX_FLOATS + 4] = (float)v_as_double(list->items[i * 6 + 3]);
        packed[i * D3D12_VERTEX_FLOATS + 5] = (float)v_as_double(list->items[i * 6 + 4]);
        packed[i * D3D12_VERTEX_FLOATS + 6] = (float)v_as_double(list->items[i * 6 + 5]);
    }
    ((PfnUnmap)TERN_SLOT(resource, 9))(resource, 0, &range);

    D3D12_VERTEX_BUFFER_VIEW view;
    memset(&view, 0, sizeof view);
    view.buffer_location = ((unsigned long long (*)(void *))TERN_SLOT(resource, 11))(resource);
    view.size_in_bytes = (unsigned)bytes;
    view.stride_in_bytes = D3D12_VERTEX_STRIDE;

    d3d12_release(target->vertex_buffer);
    target->vertex_buffer = resource;
    return view;
}

static Value n_d3d12_pipeline(int argc, Value *argv) {
    (void)argc;
    TernD3D12Target *target = d3d12_slot_arg(argv[0]);
    net_arg_text(argv[1], "vertex shader");
    net_arg_text(argv[2], "pixel shader");
    Str *vertex = (Str *)argv[1].as.o;
    Str *pixel = (Str *)argv[2].as.o;
    return v_int((int64_t)(size_t)d3d12_make_program(target, vertex->chars, pixel->chars));
}

static Value n_d3d12_draw(int argc, Value *argv) {
    (void)argc;
    TernD3D12Target *target = d3d12_slot_arg(argv[0]);
    if (argv[1].tag != T_INT) {
        tern_panic("TypeError", "expected a pipeline handle, not %s", tern_type_name(argv[1]));
    }
    int handle = (int)argv[1].as.i;
    if (handle < 1 || handle > target->program_count) {
        tern_panic("D3D12Error", "no pipeline with handle %d", handle);
    }

    D3D12_VERTEX_BUFFER_VIEW view = d3d12_upload_vertices(target, argv[2]);
    int count = view.size_in_bytes / D3D12_VERTEX_STRIDE;

    d3d12_reset(target);
    d3d12_transition(target, D3D12_RESOURCE_STATE_RENDER_TARGET_);
    ((PfnOMSetRenderTargets)TERN_SLOT(target->list, 46))(target->list, 1, &target->rtv, 0, NULL);
    ((void (*)(void *, void *))TERN_SLOT(target->list, 30))(
        target->list, target->signatures[handle - 1]);                        // SetGraphicsRootSignature
    ((void (*)(void *, void *))TERN_SLOT(target->list, 25))(
        target->list, target->programs[handle - 1]);                          // SetPipelineState
    ((void (*)(void *, int))TERN_SLOT(target->list, 20))(
        target->list, D3D12_PRIMITIVE_TOPOLOGY_TRIANGLELIST_);                // IASetPrimitiveTopology
    ((void (*)(void *, unsigned, unsigned, const D3D12_VERTEX_BUFFER_VIEW *))TERN_SLOT(target->list, 44))(
        target->list, 0, 1, &view);                                           // IASetVertexBuffers
    ((void (*)(void *, unsigned, unsigned, unsigned, unsigned))TERN_SLOT(target->list, 12))(
        target->list, (unsigned)count, 1, 0, 0);                              // DrawInstanced
    d3d12_transition(target, D3D12_RESOURCE_STATE_COPY_SOURCE_);
    d3d12_submit(target, "d3d12.draw");
    return v_nil();
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
    map_set(report, str_from_cstr("build"), str_from_cstr(TERN_D3D12_BUILD));

    if (driver->create_factory) {
        void *factory = NULL;
        if (driver->create_factory(&TERN_IID_IDXGIFactory1, &factory) >= 0 && factory) {
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
    d3d12_transition(target, D3D12_RESOURCE_STATE_RENDER_TARGET_);
    ((PfnOMSetRenderTargets)TERN_SLOT(target->list, 46))(
        target->list, 1, &target->rtv, 0, NULL);
    ((PfnClearRenderTargetView)TERN_SLOT(target->list, 48))(
        target->list, target->rtv, colour, 0, NULL);
    d3d12_transition(target, D3D12_RESOURCE_STATE_COPY_SOURCE_);
    d3d12_submit(target, "d3d12.clear");
    return v_nil();
}

static void d3d12_copy_out(TernD3D12Target *target, const char *what) {
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

    d3d12_transition(target, D3D12_RESOURCE_STATE_COMMON_);
    d3d12_submit(target, what);
}

static Value n_d3d12_pixels(int argc, Value *argv) {
    (void)argc;
    TernD3D12Target *target = d3d12_slot_arg(argv[0]);
    d3d12_copy_out(target, "d3d12.pixels");

    D3D12_RANGE range;
    range.begin = 0;
    range.end = 0;
    void *data = NULL;
    long hr = ((PfnMap)TERN_SLOT(target->readback, 8))(target->readback, 0, &range, &data);
    if (hr < 0 || !data) {
        d3d12_fail_removed(target, "the readback buffer could not be mapped", hr);
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
    d3d12_copy_out(target, "d3d12.save");

    D3D12_RANGE range;
    range.begin = 0;
    range.end = 0;
    void *data = NULL;
    long hr = ((PfnMap)TERN_SLOT(target->readback, 8))(target->readback, 0, &range, &data);
    if (hr < 0 || !data) {
        d3d12_fail_removed(target, "the readback buffer could not be mapped", hr);
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

// --- windows ---------------------------------------------------------------

static Value n_d3d12_window(int argc, Value *argv) {
    int width = d3d12_dimension_arg(argv[0], "width");
    int height = d3d12_dimension_arg(arg(argc, argv, 1), "height");
    Value title_value = arg(argc, argv, 2);
    if (!v_is_str(title_value)) {
        tern_panic("TypeError", "title must be a string, not %s", tern_type_name(title_value));
    }
    const char *title = ((Str *)title_value.as.o)->chars;

    // nil rather than an error: a machine with no window layer (Linux, a
    // service, a test run) can still use everything else in this module.
    TernWindow *window = tern_window_open(width, height, title);
    if (!window) {
        return v_nil();
    }
    return v_int(tern_window_handle(window));
}

static Value n_d3d12_present(int argc, Value *argv) {
    Value handle_value = argv[0];
    if (handle_value.tag != T_INT) {
        tern_panic("TypeError", "expected a window handle, not %s", tern_type_name(handle_value));
    }
    TernWindow *window = tern_window_at((int)handle_value.as.i);
    if (!window) {
        tern_panic("D3D12Error", "no open window with handle %lld",
                   (long long)handle_value.as.i);
    }
    TernD3D12Target *target = d3d12_slot_arg(arg(argc, argv, 1));

    d3d12_copy_out(target, "d3d12.present");

    D3D12_RANGE range;
    range.begin = 0;
    range.end = 0;
    void *data = NULL;
    long hr = ((PfnMap)TERN_SLOT(target->readback, 8))(target->readback, 0, &range, &data);
    if (hr < 0 || !data) {
        d3d12_fail_removed(target, "the readback buffer could not be mapped", hr);
    }

    // tern_window_present wants rows top-first with no padding between them;
    // the readback has a row pitch and its rows are the other way up, so copy
    // through a scratch buffer only when the pitch does not already match.
    size_t row_bytes = (size_t)target->width * 4;
    unsigned char *tight = (unsigned char *)malloc(row_bytes * (size_t)target->height);
    if (!tight) {
        tern_panic("D3D12Error", "cannot show a frame: out of memory");
    }
    for (int y = 0; y < target->height; y++) {
        memcpy(tight + (size_t)y * row_bytes,
               (const unsigned char *)data + (size_t)y * target->row_pitch, row_bytes);
    }
    ((PfnUnmap)TERN_SLOT(target->readback, 9))(target->readback, 0, &range);

    int open = tern_window_present(window, tight, target->width, target->height);
    free(tight);
    return v_bool(open ? 1 : 0);
}

static Value n_d3d12_close_window(int argc, Value *argv) {
    (void)argc;
    Value handle_value = argv[0];
    if (handle_value.tag != T_INT) {
        tern_panic("TypeError", "expected a window handle, not %s", tern_type_name(handle_value));
    }
    TernWindow *window = tern_window_at((int)handle_value.as.i);
    if (window) {
        tern_window_close(window);
    }
    return v_nil();
}

#endif  // TERN_D3D12_H
