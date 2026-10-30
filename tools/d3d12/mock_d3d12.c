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
} MockResource;

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

static long mock_query(void *self, const Guid *iid, void **out) {
    (void)iid;
    *out = self;
    return 0;
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
                             const CopyLocation *source, const void *box);
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
    (void)self; (void)node_mask; (void)blob; (void)length; (void)iid;
    *signature = mock_new((void **)1);
    ((MockObject *)*signature)->vtbl = resource_vtbl;
    return 0;
}

static long device_create_pipeline_state(void *self, const void *description, const Guid *iid,
                                         void **pipeline) {
    (void)self; (void)description; (void)iid;
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
    (void)self; (void)desc; (void)iid;
    *out = mock_new(queue_vtbl);
    return 0;
}

static long device_create_command_allocator(void *self, int type, const Guid *iid, void **out) {
    (void)self; (void)type; (void)iid;
    *out = mock_new(allocator_vtbl);
    return 0;
}

static long device_create_command_list(void *self, int type, int flags, void *allocator,
                                       void *state, const Guid *iid, void **out) {
    (void)self; (void)type; (void)flags; (void)allocator; (void)state; (void)iid;
    MockList *list = (MockList *)calloc(1, sizeof(MockList));
    list->vtbl = list_vtbl;
    *out = list;
    return 0;
}

static long device_create_descriptor_heap(void *self, const void *desc, const Guid *iid, void **out) {
    (void)self; (void)desc; (void)iid;
    *out = mock_new(heap_vtbl);
    return 0;
}

static long device_create_committed_resource(void *self, const HeapProps *heap, unsigned flags,
                                             const ResourceDesc *desc, int state, const void *clear,
                                             const Guid *iid, void **out) {
    (void)self; (void)heap; (void)flags; (void)state; (void)clear; (void)iid;
    MockResource *resource = (MockResource *)calloc(1, sizeof(MockResource));
    resource->vtbl = resource_vtbl;
    resource->kind = desc->dimension == 3 ? 0u : 1u;
    resource->width = (unsigned)desc->width;
    resource->height = desc->height;
    if (resource->kind == 1) {
        resource->size = desc->width;
        resource->memory = (unsigned char *)calloc(1, (size_t)desc->width);
    } else {
        // The texture's cleared colour lives here until a copy reads it.
        resource->colour[0] = 0;
        resource->colour[1] = 0;
        resource->colour[2] = 0;
        resource->colour[3] = 255;
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
    (void)self; (void)count; (void)barriers;
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

static void list_copy_region(void *self, const CopyLocation *destination, unsigned x, unsigned y,
                             const CopyLocation *source, const void *box) {
    (void)self; (void)x; (void)y; (void)box;
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
    (void)adapter; (void)feature_level; (void)iid;
    *device = mock_new(device_vtbl);
    return 0;
}

long CreateDXGIFactory1(const Guid *iid, void **factory) {
    (void)iid;
    *factory = mock_new(factory_vtbl);
    return 0;
}
