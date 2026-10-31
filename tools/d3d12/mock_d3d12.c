// A fake d3d12.dll, exported as a shared library, for testing std:d3d12 on a
// machine that has no Windows and no GPU.
//
// It implements the same COM vtables at the same slots as the real thing, so
// the runtime's calls land where they would on Windows. What it does not do is
// render: the "texture" is a width, a height and the colour last cleared, and a
// copy to the readback buffer writes that colour out at the row pitch the device
// reported. That is enough to prove the whole path — device, queue, allocator,
// command list, descriptor heap, render target, barriers, copy and map — because
// every one of those calls has to arrive at the right vtable slot for the
// pixels to come back out.
//
// Build: cc -shared -fPIC -o libd3d12.so tools/d3d12/mock_d3d12.c
// Run:   TERN_D3D12_LIBRARY=$PWD/libd3d12.so ./tools/tern tools/d3d12/probe.tern

#define _GNU_SOURCE
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef struct { uint32_t a; uint16_t b, c; unsigned char d[8]; } Guid;

// ------------------------------------------------------------ interface ids
//
// Windows answers E_NOINTERFACE when a Create* is handed the id of an interface
// it is not creating, and TERN was handing `IID_ID3D12Device` to all of them.
// This mock used to ignore the id entirely, so every test passed and the first
// real GPU returned 0x80004002 from D3D12CreateDevice. It checks them now.
//
// The ids are the uuid() values from d3d12.idl and dxgi.idl. They are the
// check's own copy on purpose: a typo in tern_d3d12.h or D3D12.cs has to
// disagree with something that was not typed in the same edit.

#define E_NOINTERFACE_ 0x80004002L
#define E_INVALIDARG_ 0x80070057L

static const Guid IID_ID3D12Device_ = {
    0x189819f1, 0x1db6, 0x4b57, { 0xbe, 0x54, 0x18, 0x21, 0x33, 0x9b, 0x85, 0xf7 }
};
static const Guid IID_ID3D12CommandQueue_ = {
    0x0ec870a6, 0x5d7e, 0x4c22, { 0x8c, 0xfc, 0x5b, 0xaa, 0xe0, 0x76, 0x16, 0xed }
};
static const Guid IID_ID3D12CommandAllocator_ = {
    0x6102dee4, 0xaf59, 0x4b09, { 0xb9, 0x99, 0xb4, 0x4d, 0x73, 0xf0, 0x9b, 0x24 }
};
static const Guid IID_ID3D12GraphicsCommandList_ = {
    0x5b160d0f, 0xac1b, 0x4185, { 0x8b, 0xa8, 0xb3, 0xae, 0x42, 0xa5, 0xa4, 0x55 }
};
static const Guid IID_ID3D12DescriptorHeap_ = {
    0x8efb471d, 0x616c, 0x4f49, { 0x90, 0xf7, 0x12, 0x7b, 0xb7, 0x63, 0xfa, 0x51 }
};
static const Guid IID_ID3D12Resource_ = {
    0x696442be, 0xa72e, 0x4059, { 0xbc, 0x79, 0x5b, 0x5c, 0x98, 0x04, 0x0f, 0xad }
};
static const Guid IID_ID3D12Fence_ = {
    0x0a753dcf, 0xc4d8, 0x4b91, { 0xad, 0xf6, 0xbe, 0x5a, 0x60, 0xd9, 0x5a, 0x76 }
};
static const Guid IID_ID3D12RootSignature_ = {
    0xc54a6b66, 0x72df, 0x4ee8, { 0x8b, 0xe5, 0xa9, 0x46, 0xa1, 0x42, 0x92, 0x14 }
};
static const Guid IID_ID3D12PipelineState_ = {
    0x765a30f3, 0xf624, 0x4c6f, { 0xa8, 0x28, 0xac, 0xe9, 0x48, 0x62, 0x24, 0x45 }
};
static const Guid IID_IDXGIFactory1_ = {
    0x770aae78, 0xf26f, 0x4dba, { 0xa8, 0x29, 0x25, 0x3c, 0x83, 0xd1, 0xb3, 0x87 }
};

/// The message a wrong id gets, naming the call so the failure is actionable.
static void mock_wrong_iid(const char *call, const Guid *iid) {
    fprintf(stderr,
            "mock_d3d12: %s was given interface id "
            "{%08x-%04x-%04x-%02x%02x-%02x%02x%02x%02x%02x%02x}; Windows answers "
            "E_NOINTERFACE here\n",
            call, (unsigned)iid->a, (unsigned)iid->b, (unsigned)iid->c,
            iid->d[0], iid->d[1], iid->d[2], iid->d[3],
            iid->d[4], iid->d[5], iid->d[6], iid->d[7]);
}

/// Compares an id the way Windows does: byte for byte.
static int mock_iid_is(const Guid *iid, const Guid *expected) {
    return iid && memcmp(&iid->a, &expected->a, sizeof iid->a) == 0 &&
           iid->b == expected->b && iid->c == expected->c &&
           memcmp(iid->d, expected->d, sizeof iid->d) == 0;
}

#define CHECK_IID(call, expected)                       \
    do {                                                \
        if (!mock_iid_is(iid, &(expected))) {           \
            mock_wrong_iid((call), iid);                \
            return E_NOINTERFACE_;                      \
        }                                               \
    } while (0)


typedef struct { int type, cpu_page_property, memory_pool_preference; unsigned node_mask, visible; } HeapProps;
typedef struct { unsigned count, quality; } SampleDesc;
typedef struct {
    int dimension; unsigned long long alignment, width; unsigned height;
    unsigned short depth, mips; int format; SampleDesc sample; int layout, flags;
} ResourceDesc;
typedef struct { int format; unsigned width, height, depth, pitch; } Footprint;
typedef struct { unsigned long long offset; Footprint footprint; } PlacedFootprint;
typedef struct { void *resource; int type; union { PlacedFootprint placed; unsigned index; } u; } CopyLocation;
typedef struct { size_t pointer; } CpuHandle;
// D3D12_CLEAR_VALUE: a format and four floats (or a depth/stencil pair).
typedef struct { int format; union { float colour[4]; int depth_stencil[2]; } u; } ClearValue;

typedef struct {
    void **vtbl;
} MockObject;

typedef struct {
    void **vtbl;
    unsigned kind;              // 0 = texture, 1 = buffer
    unsigned width, height;
    unsigned long long size;
    unsigned char colour[4];
    unsigned char *memory;
    unsigned state;             // the D3D12_RESOURCE_STATES it is really in
    unsigned readback;          // created in a READBACK heap
} MockResource;

// The transition half of D3D12_RESOURCE_BARRIER, laid out as the header has it.
typedef struct {
    int type;
    unsigned flags;
    union {
        struct {
            void *resource;
            unsigned subresource;
            unsigned state_before;
            unsigned state_after;
        } transition;
    } u;
} MockBarrier;

typedef struct {
    void **vtbl;
    MockResource *target;
    unsigned char colour[4];
    int closing;
    unsigned long long vertex_address;
    unsigned vertex_count;
} MockList;

typedef struct {
    void **vtbl;
    unsigned long long completed;
    unsigned long long signaled;
} MockFence;

// ------------------------------------------------------------------ resources

static MockObject *mock_new(void **vtbl);
static unsigned long mock_addref(void *self);
static unsigned long mock_release(void *self);
static long mock_query(void *self, const Guid *iid, void **out);
static MockResource *resource_from_address(unsigned long long address);
static long device_create_pipeline_state(void *self, const void *description, const Guid *iid,
                                         void **pipeline);
static long device_create_root_signature(void *self, unsigned node_mask, const void *blob,
                                         size_t length, const Guid *iid, void **signature);
static void list_draw_instanced(void *self, unsigned vertices, unsigned instances,
                                unsigned start_vertex, unsigned start_instance);
static void list_set_topology(void *self, int topology);
static void list_set_pipeline(void *self, void *pipeline);
static void list_set_signature(void *self, void *signature);
static void list_set_vertex_buffers(void *self, unsigned slot, unsigned count, const void *view);

static MockObject *mock_new(void **vtbl) {
    MockObject *object = (MockObject *)calloc(1, sizeof(MockObject));
    object->vtbl = vtbl;
    return object;
}

static unsigned long mock_addref(void *self) {
    (void)self;
    return 1;
}

static unsigned long mock_release(void *self) {
    (void)self;
    return 0;
}

static const Guid IID_ID3D12InfoQueue_ = {
    0x0742a90b, 0xc387, 0x483f, { 0xb9, 0x46, 0x30, 0xa7, 0xe4, 0xe6, 0x14, 0x58 }
};
static const Guid IID_ID3D12Debug_ = {
    0x344488b7, 0x6846, 0x474b, { 0xb9, 0x89, 0xf0, 0x27, 0x44, 0x82, 0x45, 0xe0 }
};
static const Guid IID_IUnknown_ = {
    0x00000000, 0x0000, 0x0000, { 0xc0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46 }
};
static const Guid IID_ID3D12Device_early = {
    0x189819f1, 0x1db6, 0x4b57, { 0xbe, 0x54, 0x18, 0x21, 0x33, 0x9b, 0x85, 0xf7 }
};

// The debug layer's message queue. Real Windows stores the driver's validation
// problems here; storing a made-up one lets the plumbing be tested (the query,
// the message struct, the sizes, the severity, the clear) without a GPU.
#define MOCK_MESSAGE_SLOTS 8
#define MOCK_MESSAGE_TEXT 256

typedef struct {
    int severity;
    unsigned id;
    char text[MOCK_MESSAGE_TEXT];
} MockMessage;

static struct {
    void **vtbl;
    MockMessage messages[MOCK_MESSAGE_SLOTS];
    unsigned count;
} mock_info_queue;

static struct {
    void **vtbl;
    int enabled;
} mock_debug;

static int mock_invalid_calls_wanted(void) {
    static int cached = -1;
    if (cached < 0) {
        const char *value = getenv("TERN_D3D12_INVALID_CALL");
        cached = value && value[0] && strcmp(value, "0") != 0;
    }
    return cached;
}

static void mock_store_message(int severity, unsigned id, const char *text) {
    if (mock_info_queue.count >= MOCK_MESSAGE_SLOTS) {
        return;
    }
    MockMessage *slot = &mock_info_queue.messages[mock_info_queue.count++];
    slot->severity = severity;
    slot->id = id;
    snprintf(slot->text, sizeof slot->text, "%s", text);
}

// D3D12_MESSAGE as d3d12sdklayers.h lays it out: Category, Severity, ID,
// const char *pDescription, SIZE_T DescriptionByteLength.
typedef struct {
    int category;
    int severity;
    unsigned id;
    const char *description;
    size_t description_bytes;
} MockDebugMessage;

static unsigned long long info_get_num_stored(void *self) {
    (void)self;
    return mock_info_queue.count;
}

static long info_get_message(void *self, unsigned long long index, MockDebugMessage *message,
                             size_t *size) {
    (void)self;
    if (!size) {
        return E_INVALIDARG_;
    }
    if (index >= mock_info_queue.count) {
        return E_INVALIDARG_;
    }
    MockMessage *slot = &mock_info_queue.messages[index];
    size_t needed = sizeof(MockDebugMessage) + strlen(slot->text) + 1;
    if (!message) {
        // The documented two-call pattern: ask for the size, then the message.
        *size = needed;
        return 0;
    }
    if (*size < needed) {
        *size = needed;
        return E_INVALIDARG_;
    }
    // The description follows the structure, the way the runtime reads it.
    char *text = (char *)(message + 1);
    memcpy(text, slot->text, strlen(slot->text) + 1);
    message->category = 0;
    message->severity = slot->severity;
    message->id = slot->id;
    message->description = text;
    message->description_bytes = strlen(slot->text) + 1;
    *size = needed;
    return 0;
}

static void info_clear(void *self) {
    (void)self;
    mock_info_queue.count = 0;
}

static void *info_vtbl[9] = {
    [0] = (void *)mock_query, [1] = (void *)mock_addref, [2] = (void *)mock_release,
    [4] = (void *)info_clear,
    [5] = (void *)info_get_message,
    [8] = (void *)info_get_num_stored,
};

static void debug_enable(void *self) {
    (void)self;
    mock_debug.enabled = 1;
    fprintf(stderr, "mock_d3d12: the debug layer was enabled\n");
}

static void *debug_vtbl[4] = {
    [0] = (void *)mock_query, [1] = (void *)mock_addref, [2] = (void *)mock_release,
    [3] = (void *)debug_enable,   // EnableDebugLayer
};

/// D3D12GetDebugInterface, exported like the real one.
long D3D12GetDebugInterface(const Guid *iid, void **out) {
    if (!iid || !out) {
        return E_INVALIDARG_;
    }
    if (memcmp(iid, &IID_ID3D12Debug_, sizeof(Guid)) != 0) {
        *out = NULL;
        return E_NOINTERFACE_;
    }
    mock_debug.vtbl = debug_vtbl;
    *out = &mock_debug;
    return 0;
}

static long mock_query(void *self, const Guid *iid, void **out) {
    if (!out) {
        return E_INVALIDARG_;
    }
    if (iid && memcmp(iid, &IID_ID3D12InfoQueue_, sizeof(Guid)) == 0) {
        mock_info_queue.vtbl = info_vtbl;
        *out = &mock_info_queue;
        return 0;
    }
    if (iid && (memcmp(iid, &IID_IUnknown_, sizeof(Guid)) == 0 ||
                memcmp(iid, &IID_ID3D12Device_early, sizeof(Guid)) == 0)) {
        *out = self;
        return 0;
    }
    // Anything else really does answer E_NOINTERFACE on Windows, and answering it
    // here is what makes a wrong interface id visible in a test.
    *out = NULL;
    return E_NOINTERFACE_;
}

static unsigned char mock_byte(float value) {
    double scaled = (double)value * 255.0;
    return (unsigned char)(scaled + 0.5);
}

// --------------------------------------------------------------- the device

static long device_create_command_queue(void *self, const void *desc, const Guid *iid, void **out);
static long device_create_command_allocator(void *self, int type, const Guid *iid, void **out);
static long device_create_command_list(void *self, int type, int flags, void *allocator,
                                       void *state, const Guid *iid, void **out);
static long device_create_descriptor_heap(void *self, const void *desc, const Guid *iid, void **out);
static long device_create_committed_resource(void *self, const HeapProps *heap, unsigned flags,
                                             const ResourceDesc *desc, int state, const void *clear,
                                             const Guid *iid, void **out);
static void device_create_rtv(void *self, void *resource, const void *desc, CpuHandle handle);
static long device_create_fence(void *self, unsigned long long value, unsigned flags,
                                const Guid *iid, void **out);
static void device_get_footprints(void *self, const ResourceDesc *desc, unsigned first, unsigned count,
                                  unsigned long long offset, PlacedFootprint *layouts,
                                  unsigned *rows, unsigned long long *row_size,
                                  unsigned long long *total);

// ------------------------------------------------------- device removal
//
// A removed device is reported at whatever call next touches it, which is
// rarely the one that removed it and is the reason the runtime now asks
// GetDeviceRemovedReason after every submit. These knobs reproduce both halves
// of that: TERN_D3D12_REMOVED_AFTER=n removes the device after the nth
// ExecuteCommandLists, and TERN_D3D12_MAP_REMOVED=1 makes the readback map fail
// the way it did in the report that started this.
static int mock_removed;
static unsigned mock_state_mismatches;
static unsigned mock_submits;

static unsigned mock_removal_code(void) {
    const char *value = getenv("TERN_D3D12_REMOVED_REASON");
    return value && value[0] ? (unsigned)strtoul(value, NULL, 0) : 0x887A0006u;
}

static long device_removed_reason(void *self) {
    (void)self;
    return mock_removed ? (long)mock_removal_code() : 0;
}

static void *device_vtbl[44] = {
    [0] = (void *)mock_query, [1] = (void *)mock_addref, [2] = (void *)mock_release,
    [8] = (void *)device_create_command_queue,
    [9] = (void *)device_create_command_allocator,
    [10] = (void *)device_create_pipeline_state,
    [12] = (void *)device_create_command_list,
    [16] = (void *)device_create_root_signature,
    [14] = (void *)device_create_descriptor_heap,
    [20] = (void *)device_create_rtv,
    [27] = (void *)device_create_committed_resource,
    [36] = (void *)device_create_fence,
    [37] = (void *)device_removed_reason,
    [38] = (void *)device_get_footprints,
};

// --------------------------------------------------------------- the queue

static void queue_execute(void *self, unsigned count, void *const *lists);
static long queue_signal(void *self, void *fence, unsigned long long value);

static void *queue_vtbl[19] = {
    [0] = (void *)mock_query, [1] = (void *)mock_addref, [2] = (void *)mock_release,
    [10] = (void *)queue_execute,
    [14] = (void *)queue_signal,
};

// ------------------------------------------------------------ the allocator

static long allocator_reset(void *self) {
    (void)self;
    return 0;
}

static void *allocator_vtbl[9] = {
    [0] = (void *)mock_query, [1] = (void *)mock_addref, [2] = (void *)mock_release,
    [8] = (void *)allocator_reset,
};

// ---------------------------------------------------------- the command list

static long list_close(void *self);
static long list_reset(void *self, void *allocator, void *state);
static void list_copy_region(void *self, const CopyLocation *destination, unsigned x, unsigned y,
                             unsigned z, const CopyLocation *source, const void *box);
static void list_barrier(void *self, unsigned count, const void *barriers);
static void list_om_set_targets(void *self, unsigned count, const CpuHandle *targets, int single, const void *depth);
static void list_clear_rtv(void *self, CpuHandle handle, const float *colour, unsigned count, const void *rects);

static void list_set_topology(void *self, int topology) { (void)self; (void)topology; }
static void list_set_pipeline(void *self, void *pipeline) { (void)self; (void)pipeline; }
static void list_set_signature(void *self, void *signature) { (void)self; (void)signature; }

static void list_set_vertex_buffers(void *self, unsigned slot, unsigned count, const void *view) {
    (void)slot;
    if (count < 1 || !view) {
        return;
    }
    // D3D12_VERTEX_BUFFER_VIEW: BufferLocation, SizeInBytes, StrideInBytes.
    const unsigned long long *fields = (const unsigned long long *)view;
    MockList *list = (MockList *)self;
    list->vertex_address = fields[0];
    list->vertex_count = (unsigned)((const unsigned *)view)[2];
}

static void list_draw_instanced(void *self, unsigned vertices, unsigned instances,
                                unsigned start_vertex, unsigned start_instance) {
    (void)instances; (void)start_vertex; (void)start_instance;
    MockList *list = (MockList *)self;
    MockResource *buffer = resource_from_address(list->vertex_address);
    if (!buffer || !buffer->memory) {
        list->vertex_count = vertices;
        return;
    }
    // The first vertex's colour, which is how the readback proves the vertex data
    // travelled from the script into the buffer. The layout is seven floats:
    // x, y, z, r, g, b, a.
    const float *vertex = (const float *)buffer->memory;
    for (int channel = 0; channel < 4; channel++) {
        double scaled = (double)vertex[3 + channel] * 255.0;
        list->colour[channel] = (unsigned char)(scaled + 0.5);
    }
    list->vertex_count = vertices;
}

static void *list_vtbl[60] = {
    [12] = (void *)list_draw_instanced,
    [20] = (void *)list_set_topology,
    [25] = (void *)list_set_pipeline,
    [30] = (void *)list_set_signature,
    [44] = (void *)list_set_vertex_buffers,
    [0] = (void *)mock_query, [1] = (void *)mock_addref, [2] = (void *)mock_release,
    [9] = (void *)list_close,
    [10] = (void *)list_reset,
    [16] = (void *)list_copy_region,
    [26] = (void *)list_barrier,
    [46] = (void *)list_om_set_targets,
    [48] = (void *)list_clear_rtv,
};

// ---------------------------------------------------------- descriptor heap

static void heap_get_cpu(void *self, CpuHandle *handle) {
    (void)self;
    handle->pointer = 1;   // any non-zero value will do: the mock never reads it
}

static void *heap_vtbl[11] = {
    [0] = (void *)mock_query, [1] = (void *)mock_addref, [2] = (void *)mock_release,
    [9] = (void *)heap_get_cpu,
};

// ------------------------------------------------------------------ resource

static long resource_map(void *self, unsigned subresource, const void *range, void **data) {
    (void)subresource;
    (void)range;
    MockResource *resource = (MockResource *)self;
    const char *map_removed = getenv("TERN_D3D12_MAP_REMOVED");
    if (resource->readback && map_removed && map_removed[0] && strcmp(map_removed, "0") != 0) {
        mock_removed = 1;
        *data = NULL;
        return (long)0x887A0005L;
    }
    *data = resource->memory;
    return 0;
}

// Fake GPU addresses: a table maps them back to the resource, which is how the
// mock finds a vertex buffer again from the view the command list was given.
#define MOCK_MAX_RESOURCES 64
static MockResource *mock_resources[MOCK_MAX_RESOURCES];
static int mock_resource_count;

static unsigned long long resource_get_address(void *self) {
    MockResource *resource = (MockResource *)self;
    for (int i = 0; i < mock_resource_count; i++) {
        if (mock_resources[i] == resource) {
            return (unsigned long long)(i + 1) * 0x1000ull;
        }
    }
    if (mock_resource_count >= MOCK_MAX_RESOURCES) {
        return 0;
    }
    mock_resources[mock_resource_count++] = resource;
    return (unsigned long long)mock_resource_count * 0x1000ull;
}

static MockResource *resource_from_address(unsigned long long address) {
    if (address == 0 || address % 0x1000ull != 0) {
        return NULL;
    }
    unsigned index = (unsigned)(address / 0x1000ull);
    if (index < 1 || index > (unsigned)mock_resource_count) {
        return NULL;
    }
    return mock_resources[index - 1];
}

static void resource_unmap(void *self, unsigned subresource, const void *range) {
    (void)self;
    (void)subresource;
    (void)range;
}

static void *resource_vtbl[15] = {
    [0] = (void *)mock_query, [1] = (void *)mock_addref, [2] = (void *)mock_release,
    [8] = (void *)resource_map,
    [9] = (void *)resource_unmap,
    [11] = (void *)resource_get_address,
};

// --- blobs, the compiler and the root signature ----------------------------

typedef struct {
    void **vtbl;
    char *data;
    size_t size;
} MockBlob;

static const void *blob_pointer(void *self) {
    return ((MockBlob *)self)->data;
}

static size_t blob_size(void *self) {
    return ((MockBlob *)self)->size;
}

static unsigned long blob_release(void *self) {
    MockBlob *blob = (MockBlob *)self;
    if (blob->data) {
        free(blob->data);
    }
    free(blob);
    return 0;
}

static void *blob_vtbl[5] = {
    [0] = (void *)mock_query, [1] = (void *)mock_addref, [2] = (void *)blob_release,
    [3] = (void *)blob_pointer,
    [4] = (void *)blob_size,
};

static MockBlob *blob_new(const char *text) {
    MockBlob *blob = (MockBlob *)calloc(1, sizeof(MockBlob));
    blob->vtbl = blob_vtbl;
    blob->size = strlen(text);
    blob->data = (char *)malloc(blob->size + 1);
    memcpy(blob->data, text, blob->size + 1);
    return blob;
}

// D3DCompile: refuses a shader with "FAIL" in it and otherwise returns a stand-in
// blob, so the compile path is exercised without HLSL.
long D3DCompile(const void *source, size_t size, const char *name, const void *defines,
                void *include, const char *entry, const char *target, unsigned flags1,
                unsigned flags2, void **code, void **errors) {
    (void)name; (void)defines; (void)include; (void)entry; (void)flags1; (void)flags2;
    const char *text = (const char *)source;
    *errors = NULL;
    if (size >= 4 && (memmem(text, size, "FAIL", 4) || memmem(text, size, "fail", 4))) {
        *code = NULL;
        if (errors) {
            *errors = blob_new("tern.hlsl:1:1: error: the mock compiler was told to fail\n");
        }
        return -1;   // E_FAIL
    }
    if (target && memmem(target, strlen(target), "ps_", 3)) {
        *code = blob_new("PIXELSHADER");
    } else {
        *code = blob_new("VERTEXSHADER");
    }
    return 0;
}

long D3D12SerializeRootSignature(const void *description, int version, void **blob, void **errors) {
    (void)description; (void)version;
    *blob = blob_new("ROOTSIGNATURE");
    if (errors) {
        *errors = NULL;
    }
    return 0;
}

static long device_create_root_signature(void *self, unsigned node_mask, const void *blob,
                                         size_t length, const Guid *iid, void **signature) {
    (void)self; (void)node_mask; (void)blob; (void)length;
    CHECK_IID("CreateRootSignature", IID_ID3D12RootSignature_);
    *signature = mock_new((void **)1);
    ((MockObject *)*signature)->vtbl = resource_vtbl;
    return 0;
}

static long device_create_pipeline_state(void *self, const void *description, const Guid *iid,
                                         void **pipeline) {
    (void)self; (void)description;
    CHECK_IID("CreateGraphicsPipelineState", IID_ID3D12PipelineState_);
    *pipeline = mock_new((void **)1);
    ((MockObject *)*pipeline)->vtbl = resource_vtbl;
    return 0;
}

// --------------------------------------------------------------------- fence

static unsigned long long fence_value(void *self) {
    return ((MockFence *)self)->completed;
}

static void *fence_vtbl[11] = {
    [0] = (void *)mock_query, [1] = (void *)mock_addref, [2] = (void *)mock_release,
    [8] = (void *)fence_value,
};

// -------------------------------------------------------------- book keeping

static MockResource *mock_target;   // the render target the list draws into

static long device_create_command_queue(void *self, const void *desc, const Guid *iid, void **out) {
    (void)self; (void)desc;
    CHECK_IID("CreateCommandQueue", IID_ID3D12CommandQueue_);
    *out = mock_new(queue_vtbl);
    return 0;
}

static long device_create_command_allocator(void *self, int type, const Guid *iid, void **out) {
    (void)self; (void)type;
    CHECK_IID("CreateCommandAllocator", IID_ID3D12CommandAllocator_);
    *out = mock_new(allocator_vtbl);
    return 0;
}

static long device_create_command_list(void *self, int type, int flags, void *allocator,
                                       void *state, const Guid *iid, void **out) {
    (void)self; (void)type; (void)flags; (void)allocator; (void)state;
    CHECK_IID("CreateCommandList", IID_ID3D12GraphicsCommandList_);
    MockList *list = (MockList *)calloc(1, sizeof(MockList));
    list->vtbl = list_vtbl;
    *out = list;
    return 0;
}

static long device_create_descriptor_heap(void *self, const void *desc, const Guid *iid, void **out) {
    (void)self; (void)desc;
    CHECK_IID("CreateDescriptorHeap", IID_ID3D12DescriptorHeap_);
    *out = mock_new(heap_vtbl);
    return 0;
}

static long device_create_committed_resource(void *self, const HeapProps *heap, unsigned flags,
                                             const ResourceDesc *desc, int state, const void *clear,
                                             const Guid *iid, void **out) {
    CHECK_IID("CreateCommittedResource", IID_ID3D12Resource_);
    (void)self; (void)flags; (void)iid;
    MockResource *resource = (MockResource *)calloc(1, sizeof(MockResource));
    resource->readback = heap && heap->type == 3;
    resource->vtbl = resource_vtbl;
    resource->state = (unsigned)state;
    resource->kind = desc->dimension == 3 ? 0u : 1u;
    resource->width = (unsigned)desc->width;
    resource->height = desc->height;
    if (resource->kind == 1) {
        resource->size = desc->width;
        resource->memory = (unsigned char *)calloc(1, (size_t)desc->width);
    } else {
        // The colour the resource is cleared to, until a copy reads it.
        resource->colour[0] = 0;
        resource->colour[1] = 0;
        resource->colour[2] = 0;
        resource->colour[3] = 255;

        // A render target carries the colour it will be cleared to, so that the
        // driver can throw the old contents away instead of preserving them.
        // Passing NULL is legal and the Direct3D 12 debug layer warns about it
        // on every clear, which is exactly the warning a real run printed.
        if (desc->flags & 1u) {   // D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET
            const ClearValue *value = (const ClearValue *)clear;
            if (!value) {
                mock_state_mismatches++;
                fprintf(stderr,
                        "mock_d3d12: CLEAR VALUE MISSING: a render target was created with no "
                        "optimized clear value, so every clear is unordered\n");
            } else if (value->format != desc->format) {
                mock_state_mismatches++;
                fprintf(stderr,
                        "mock_d3d12: CLEAR VALUE MISMATCH: the clear value is format %d and the "
                        "resource is format %d\n", value->format, desc->format);
            } else {
                for (int i = 0; i < 4; i++) {
                    float channel = value->u.colour[i];
                    if (!(channel >= 0.0f && channel <= 1.0f)) {
                        mock_state_mismatches++;
                        fprintf(stderr,
                                "mock_d3d12: CLEAR VALUE MISMATCH: channel %d is %f, outside "
                                "[0, 1]\n", i, (double)channel);
                        break;
                    }
                    resource->colour[i] = mock_byte(channel);
                }
            }
        }
        mock_target = resource;
    }
    *out = resource;
    return 0;
}

static void device_create_rtv(void *self, void *resource, const void *desc, CpuHandle handle) {
    (void)self; (void)desc; (void)handle;
    mock_target = (MockResource *)resource;
}

static long device_create_fence(void *self, unsigned long long value, unsigned flags,
                                const Guid *iid, void **out) {
    CHECK_IID("CreateFence", IID_ID3D12Fence_);
    (void)self; (void)flags; (void)iid;
    MockFence *fence = (MockFence *)calloc(1, sizeof(MockFence));
    fence->vtbl = fence_vtbl;
    fence->completed = value;
    *out = fence;
    return 0;
}

static void device_get_footprints(void *self, const ResourceDesc *desc, unsigned first, unsigned count,
                                  unsigned long long offset, PlacedFootprint *layouts,
                                  unsigned *rows, unsigned long long *row_size,
                                  unsigned long long *total) {
    (void)self; (void)first; (void)count;
    unsigned pitch = ((unsigned)desc->width * 4u + 255u) & ~255u;
    if (layouts) {
        layouts[0].offset = offset;
        layouts[0].footprint.format = desc->format;
        layouts[0].footprint.width = (unsigned)desc->width;
        layouts[0].footprint.height = desc->height;
        layouts[0].footprint.depth = 1;
        layouts[0].footprint.pitch = pitch;
    }
    if (rows) {
        *rows = desc->height;
    }
    if (row_size) {
        *row_size = (unsigned long long)desc->width * 4u;
    }
    if (total) {
        *total = (unsigned long long)pitch * desc->height;
    }
}

static void queue_execute(void *self, unsigned count, void *const *lists) {
    (void)self;
    mock_submits++;
    const char *after = getenv("TERN_D3D12_REMOVED_AFTER");
    if (after && after[0] && (unsigned)strtoul(after, NULL, 0) == mock_submits && !mock_removed) {
        mock_removed = 1;
        fprintf(stderr, "mock_d3d12: the device was removed after %u submit(s)\n", mock_submits);
    }
    for (unsigned i = 0; i < count; i++) {
        MockList *list = (MockList *)lists[i];
        if (list->closing && mock_target) {
            memcpy(mock_target->colour, list->colour, 4);
        }
    }
}

static long queue_signal(void *self, void *fence, unsigned long long value) {
    (void)self;
    ((MockFence *)fence)->completed = value;
    return 0;
}

static long list_close(void *self) {
    ((MockList *)self)->closing = 1;
    return 0;
}

static long list_reset(void *self, void *allocator, void *state) {
    (void)allocator; (void)state;
    MockList *list = (MockList *)self;
    list->closing = 0;
    return 0;
}

static void list_barrier(void *self, unsigned count, const void *barriers) {
    (void)self;
    const MockBarrier *list = (const MockBarrier *)barriers;
    for (unsigned i = 0; i < count; i++) {
        if (list[i].type != 0) {
            continue;   // not a transition barrier
        }
        MockResource *resource = (MockResource *)list[i].u.transition.resource;
        if (!resource) {
            continue;
        }
        // This is the check the debug layer makes and a driver acts on. A
        // barrier that names the wrong `before` state is undefined behaviour:
        // the debug layer complains, and Windows can remove the device, which
        // then shows up much later as an unrelated call failing with
        // 0x887A0005. It happened for real — `clear` left the target in
        // COPY_SOURCE and `draw` announced COMMON — so it is an error here.
        if (resource->state != list[i].u.transition.state_before) {
            mock_state_mismatches++;
            fprintf(stderr,
                    "mock_d3d12: RESOURCE STATE MISMATCH: a barrier claims state %u "
                    "but the resource is in %u (going to %u)\n",
                    list[i].u.transition.state_before, resource->state,
                    list[i].u.transition.state_after);
        }
        resource->state = list[i].u.transition.state_after;
    }
}

static void list_om_set_targets(void *self, unsigned count, const CpuHandle *targets, int single, const void *depth) {
    (void)self; (void)count; (void)targets; (void)single; (void)depth;
}

static void list_clear_rtv(void *self, CpuHandle handle, const float *colour, unsigned count, const void *rects) {
    (void)handle; (void)count; (void)rects;
    MockList *list = (MockList *)self;
    list->colour[0] = mock_byte(colour[0]);
    list->colour[1] = mock_byte(colour[1]);
    list->colour[2] = mock_byte(colour[2]);
    list->colour[3] = mock_byte(colour[3]);
}

// ID3D12GraphicsCommandList::CopyTextureRegion, with every argument the real
// method takes. That is deliberate: TERN called it through a function pointer of
// its own making, the pointer was missing DstZ, and the mock - which had the
// same omission - agreed with it. A real GPU did not, and removed the device on
// the first copy. Compiling this against the documented signature means the
// arguments have to arrive in the right registers or the check below fires.
static void list_copy_region(void *self, const CopyLocation *destination, unsigned x, unsigned y,
                             unsigned z, const CopyLocation *source, const void *box) {
    (void)self; (void)box;
    if (z != 0 || x != 0 || y != 0) {
        mock_state_mismatches++;
        fprintf(stderr,
                "mock_d3d12: COPY ARGUMENT MISMATCH: CopyTextureRegion(dst, %u, %u, %u, src, box) "
                "for a 2D copy of the whole subresource\n", x, y, z);
        return;
    }
    if (!destination || !source) {
        mock_state_mismatches++;
        fprintf(stderr,
                "mock_d3d12: COPY ARGUMENT MISMATCH: CopyTextureRegion reached the wrong "
                "arguments - the call site and the documented signature disagree\n");
        return;
    }
    if (mock_invalid_calls_wanted()) {
        // Stands in for whatever the driver would store here. What is being
        // tested is the plumbing: that the queue can be found, that the message
        // is read at the size it says it is, and that it is printed and cleared.
        mock_store_message(1 /* D3D12_MESSAGE_SEVERITY_ERROR */, 0x00000001u,
                           "ID3D12CommandList::CopyTextureRegion: the mock recorded a "
                           "validation error (TERN_D3D12_INVALID_CALL=1)");
    }
    MockResource *buffer = (MockResource *)destination->resource;
    MockResource *texture = (MockResource *)source->resource;
    if (!buffer || !texture || !buffer->memory) {
        return;
    }
    unsigned pitch = destination->u.placed.footprint.pitch;
    unsigned width = destination->u.placed.footprint.width;
    unsigned height = destination->u.placed.footprint.height;
    for (unsigned row = 0; row < height; row++) {
        unsigned char *line = buffer->memory + (size_t)row * pitch;
        for (unsigned column = 0; column < width; column++) {
            memcpy(line + (size_t)column * 4, texture->colour, 4);
        }
    }
}

// ----------------------------------------------------------------------- DXGI

typedef struct {
    void **vtbl;
} MockFactory;

typedef struct {
    void **vtbl;
} MockAdapter;

static void adapter_get_desc1(void *self, void *description);

static void *adapter_vtbl[11] = {
    [0] = (void *)mock_query, [1] = (void *)mock_addref, [2] = (void *)mock_release,
    [10] = (void *)adapter_get_desc1,
};

static long factory_enum_adapters1(void *self, unsigned index, void **adapter) {
    (void)self;
    if (index != 0) {
        *adapter = NULL;
        return -1;   // E_FAIL: no more adapters
    }
    MockAdapter *made = (MockAdapter *)calloc(1, sizeof(MockAdapter));
    made->vtbl = adapter_vtbl;
    *adapter = made;
    return 0;
}

static void adapter_get_desc1(void *self, void *description) {
    (void)self;
    unsigned short *text = (unsigned short *)description;
    const char *name = "TERN mock adapter";
    for (int i = 0; name[i]; i++) {
        text[i] = (unsigned short)name[i];
    }
    text[strlen(name)] = 0;
}

static long factory_is_current(void *self) { (void)self; return 1; }

static void *factory_vtbl[14] = {
    [0] = (void *)mock_query, [1] = (void *)mock_addref, [2] = (void *)mock_release,
    [12] = (void *)factory_enum_adapters1,
    [13] = (void *)factory_is_current,
};

// The exported entry points.

long D3D12CreateDevice(void *adapter, int feature_level, const Guid *iid, void **device) {
    (void)adapter;
    CHECK_IID("D3D12CreateDevice", IID_ID3D12Device_);
    if (feature_level != 0xB000) {
        // 11_0 is what the runtime asks for; Windows rejects anything it does
        // not know with E_INVALIDARG.
        fprintf(stderr, "mock_d3d12: D3D12CreateDevice feature level %#x is not 11_0\n",
                (unsigned)feature_level);
        return E_INVALIDARG_;
    }
    *device = mock_new(device_vtbl);
    return 0;
}

long CreateDXGIFactory1(const Guid *iid, void **factory) {
    CHECK_IID("CreateDXGIFactory1", IID_IDXGIFactory1_);
    *factory = mock_new(factory_vtbl);
    return 0;
}
