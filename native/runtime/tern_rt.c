// TERN native runtime - implementation.
#include "tern_rt.h"
#include <math.h>
#include <time.h>
#include <unistd.h>

// ------------------------------------------------------------------ errors

TernTry *tern_try_top = NULL;
Value tern_thrown;
int tern_call_depth = 0;
static const char *tern_thrown_kind = "Error";

// Reports an uncaught error the way the interpreter does and stops the program.
static void report_uncaught(const char *kind, Value payload) {
    fflush(stdout);
    fprintf(stderr, "%s: ", kind);
    Value text = tern_to_string(payload);
    Str *str = (Str *)text.as.o;
    fwrite(str->chars, 1, str->len, stderr);
    fputc('\n', stderr);
    exit(1);
}

void tern_raise_kind(const char *kind, Value payload) {
    if (tern_try_top) {
        TernTry *handler = tern_try_top;
        tern_try_top = handler->prev;
        tern_thrown = payload;
        tern_thrown_kind = kind;
        longjmp(handler->env, 1);
    }
    report_uncaught(kind, payload);
}

void tern_raise(Value payload) { tern_raise_kind("Error", payload); }

void tern_throw_str(const char *kind, const char *message) {
    tern_raise_kind(kind, str_from_cstr(message));
}

void tern_panic(const char *kind, const char *fmt, ...) {
    char message[1024];
    va_list args;
    va_start(args, fmt);
    vsnprintf(message, sizeof message, fmt, args);
    va_end(args);
    tern_raise_kind(kind, str_from_cstr(message));
}

// ------------------------------------------------------------------ allocation
//
// Objects come out of 16-byte size classes (16..512 bytes) with a free list per
// class, backed by 256 KiB blocks that are bump-allocated; anything larger gets
// a block of its own. A mark-sweep collector reclaims them.
//
// Roots are the registered global/constant arrays plus the C stack and the
// callee-saved registers, which are scanned conservatively: a word that looks
// like the address of an object start keeps that object (and everything it
// points at) alive. That is why the generated code does not have to describe
// its frame to the collector.

#define GC_ALIGN 16u
#define GC_CLASSES 32
#define GC_SMALL_MAX (GC_CLASSES * GC_ALIGN)
#define GC_LARGE 255
#define GC_BLOCK (256u * 1024u)
#define GC_BITMAP_BYTES(payload) (((payload) / GC_ALIGN + 7u) / 8u)

typedef struct Block {
    char *data;
    size_t cap;
    size_t used;
    uint8_t *starts;    // one bit per 16-byte slot: object boundaries
    int single;         // holds exactly one large object
} Block;

static Block **gc_blocks = NULL;     // sorted by data address
static int gc_block_count = 0;
static int gc_block_cap = 0;
static Block *gc_current = NULL;
static void *gc_free[GC_CLASSES];

static size_t gc_since = 0;
static size_t gc_threshold = 16u << 20;   // collect late; small scripts never do
static int gc_threshold_fixed = 0;        // TERN_GC_THRESHOLD was set
static size_t gc_collections = 0;
static size_t gc_allocated_total = 0;
static size_t gc_live_bytes = 0;
static char *gc_stack_bottom = NULL;

// Objects already sitting in a free list keep their slot in the block, so later
// sweeps must not release their inner arrays a second time. `type` is set to
// this sentinel; `klass` keeps the size the sweep needs.
#define GC_DEAD ((ObjType)255)

// The free-list link lives in an object's second word: the first word holds the
// object header (type, marked, klass) and the sweep needs `klass` to know each
// object's size.
static void *gc_link(const Obj *object) {
    void *next;
    memcpy(&next, (const char *)object + 8, sizeof next);
    return next;
}

static void gc_set_link(Obj *object, void *next) {
    memcpy((char *)object + 8, &next, sizeof next);
}

typedef struct ValueRoot {
    Value *base;
    int count;
} ValueRoot;

typedef struct PtrRoot {
    void **base;
    int count;
} PtrRoot;

static ValueRoot *gc_value_roots = NULL;
static int gc_value_root_count = 0;
static PtrRoot *gc_ptr_roots = NULL;
static int gc_ptr_root_count = 0;

static void gc_mark_object(Obj *object);

/// `anchor` must be the address of a local in `main`: every function the
/// program calls runs below it, so [current frame, anchor) is the whole live C
/// stack. Using a helper's local here would be wrong - its frame is dead by the
/// time the program body runs and its memory gets reused.
void tern_gc_set_stack_bottom(void *anchor) {
    gc_stack_bottom = (char *)anchor;
}

void tern_gc_register_values(Value *base, int count) {
    gc_value_roots = (ValueRoot *)realloc(gc_value_roots, (size_t)(gc_value_root_count + 1) * sizeof(ValueRoot));
    gc_value_roots[gc_value_root_count].base = base;
    gc_value_roots[gc_value_root_count].count = count;
    gc_value_root_count++;
}

void tern_gc_register_ptrs(void **base, int count) {
    gc_ptr_roots = (PtrRoot *)realloc(gc_ptr_roots, (size_t)(gc_ptr_root_count + 1) * sizeof(PtrRoot));
    gc_ptr_roots[gc_ptr_root_count].base = base;
    gc_ptr_roots[gc_ptr_root_count].count = count;
    gc_ptr_root_count++;
}

static void gc_index_insert(Block *block) {
    if (gc_block_count == gc_block_cap) {
        gc_block_cap = gc_block_cap ? gc_block_cap * 2 : 16;
        gc_blocks = (Block **)realloc(gc_blocks, (size_t)gc_block_cap * sizeof(Block *));
    }

    int at = gc_block_count;
    while (at > 0 && gc_blocks[at - 1]->data > block->data) {
        gc_blocks[at] = gc_blocks[at - 1];
        at--;
    }
    gc_blocks[at] = block;
    gc_block_count++;
}

static Block *gc_new_block(size_t payload) {
    size_t bitmap = GC_BITMAP_BYTES(payload);
    size_t header = (sizeof(Block) + bitmap + GC_ALIGN - 1) & ~(size_t)(GC_ALIGN - 1);
    char *raw = (char *)malloc(header + payload);
    if (!raw) {
        tern_panic("MemoryError", "out of memory allocating %zu bytes", payload);
    }

    Block *block = (Block *)raw;
    block->starts = (uint8_t *)(raw + sizeof(Block));
    block->data = raw + header;
    block->cap = payload;
    block->used = 0;
    block->single = 0;
    memset(block->starts, 0, bitmap);
    gc_index_insert(block);
    return block;
}

/// The block that owns `address`, or NULL. Blocks are sorted, so this is a
/// binary search over a handful of entries.
static Block *gc_block_of(const void *address) {
    const char *pointer = (const char *)address;
    int low = 0, high = gc_block_count - 1;
    while (low <= high) {
        int middle = (low + high) / 2;
        Block *block = gc_blocks[middle];
        if (pointer < block->data) {
            high = middle - 1;
        } else if (pointer >= block->data + block->cap) {
            low = middle + 1;
        } else {
            return block;
        }
    }
    return NULL;
}

static bool gc_is_object_start(const void *address) {
    Block *block = gc_block_of(address);
    if (!block) {
        return false;
    }

    size_t offset = (size_t)((const char *)address - block->data);
    if ((offset % GC_ALIGN) != 0 || offset >= block->used) {
        return false;
    }

    return (block->starts[offset / GC_ALIGN / 8] & (uint8_t)(1u << ((offset / GC_ALIGN) % 8))) != 0;
}

static void gc_poison(Block *block, size_t offset) {
    block->starts[(offset / GC_ALIGN) / 8] |= (uint8_t)(1u << ((offset / GC_ALIGN) % 8));
}

static void gc_release(Obj *object) {
    switch (object->type) {
        case O_LIST:
            free(((List *)object)->items);
            break;
        case O_MAP: {
            Map *map = (Map *)object;
            free(map->entries);
            free(map->order);
            break;
        }
        case O_INSTANCE:
            free(((Instance *)object)->fields);
            break;
        default:
            break;
    }
}

// ------------------------------------------------------------------ collector

typedef struct GrayStack {
    Obj **items;
    size_t count;
    size_t cap;
} GrayStack;

static GrayStack gc_gray;

static void gc_push(Obj *object) {
    if (gc_gray.count == gc_gray.cap) {
        gc_gray.cap = gc_gray.cap ? gc_gray.cap * 2 : 1024;
        gc_gray.items = (Obj **)realloc(gc_gray.items, gc_gray.cap * sizeof(Obj *));
        if (!gc_gray.items) {
            tern_panic("MemoryError", "collector ran out of memory");
        }
    }
    gc_gray.items[gc_gray.count++] = object;
}

static void gc_mark_value(Value value) {
    if (value.tag == T_OBJ) {
        gc_mark_object(value.as.o);
    }
}

static void gc_mark_object(Obj *object) {
    if (!object || object->marked) {
        return;
    }
    object->marked = 1;
    gc_push(object);
}

static void gc_mark_conservative(void *candidate) {
    if (gc_is_object_start(candidate)) {
        gc_mark_object((Obj *)candidate);
    }
}

static void gc_trace(void) {
    while (gc_gray.count > 0) {
        Obj *object = gc_gray.items[--gc_gray.count];
        switch (object->type) {
            case O_LIST: {
                List *list = (List *)object;
                for (int32_t i = 0; i < list->count; i++) {
                    gc_mark_value(list->items[i]);
                }
                break;
            }
            case O_MAP: {
                Map *map = (Map *)object;
                for (int32_t i = 0; i < map->cap; i++) {
                    MapEntry *entry = &map->entries[i];
                    if (entry->used && !entry->dead) {
                        gc_mark_value(entry->key);
                        gc_mark_value(entry->value);
                    }
                }
                break;
            }
            case O_CLOSURE: {
                Closure *closure = (Closure *)object;
                int upvalues = closure->proto ? closure->proto->nup : 0;
                for (int i = 0; i < upvalues; i++) {
                    gc_mark_object((Obj *)closure->up[i]);
                }
                break;
            }
            case O_CELL:
                gc_mark_value(((Cell *)object)->value);
                break;
            case O_INSTANCE: {
                Instance *instance = (Instance *)object;
                for (int32_t i = 0; i < instance->type->nfields; i++) {
                    gc_mark_value(instance->fields[i]);
                }
                break;
            }
            case O_TYPE: {
                TypeObj *type = (TypeObj *)object;
                for (int32_t i = 0; i < type->nfields; i++) {
                    gc_mark_value(type->defaults[i]);
                    if (type->defproto) {
                        gc_mark_object((Obj *)type->defproto[i]);
                    }
                }
                for (int32_t i = 0; i < type->nmethods; i++) {
                    gc_mark_object((Obj *)type->methods[i]);
                }
                break;
            }
            case O_BOUND: {
                Bound *bound = (Bound *)object;
                gc_mark_value(bound->receiver);
                gc_mark_value(bound->callee);
                break;
            }
            case O_ITER: {
                Iterator *iterator = (Iterator *)object;
                gc_mark_value(iterator->target);
                break;
            }
            default:
                break;
        }
    }
}

// Reads memory it does not own on purpose (the scan is word-granular over a
// byte range), so address sanitizer instrumentation is switched off for it.
__attribute__((no_sanitize_address))
__attribute__((no_sanitize_address))
static void gc_scan_conservative_range(const char *from, const char *to) {
    // Round inwards so the scan only ever sees whole words.
    const char *start = (const char *)(((uintptr_t)from + sizeof(void *) - 1) & ~(uintptr_t)(sizeof(void *) - 1));
    const char *end = (const char *)((uintptr_t)to & ~(uintptr_t)(sizeof(void *) - 1));
    for (const char *cursor = start; cursor + sizeof(void *) <= end; cursor += sizeof(void *)) {
        void *candidate;
        memcpy(&candidate, cursor, sizeof candidate);
        if (candidate) {
            gc_mark_conservative(candidate);
        }
    }
}

size_t tern_gc_live_bytes(void) { return gc_live_bytes; }

/// `TERN_GC_STATS=1` makes a program print one line about the collector to
/// stderr when it exits. Handy when tuning a workload; never on by default.
static void gc_print_stats(void) {
    fprintf(stderr, "gc: %zu collections, %zu bytes allocated, %zu bytes live, %d blocks\n",
            gc_collections, gc_allocated_total, gc_live_bytes, gc_block_count);
}

void tern_gc_collect(void) {
    if (!gc_stack_bottom) {
        return;
    }
    gc_collections++;

    // Spilling the registers with setjmp is what makes a register-only pointer
    // visible to the scanner.
    jmp_buf registers;
    memset(&registers, 0, sizeof registers);
    setjmp(registers);
    // GCC spills every callee-saved register to the stack here, so the scan
    // below sees values that only live in registers.
    __builtin_unwind_init();

    gc_gray.count = 0;

    for (int i = 0; i < gc_value_root_count; i++) {
        ValueRoot *root = &gc_value_roots[i];
        for (int j = 0; j < root->count; j++) {
            gc_mark_value(root->base[j]);
        }
    }

    for (int i = 0; i < gc_ptr_root_count; i++) {
        PtrRoot *root = &gc_ptr_roots[i];
        for (int j = 0; j < root->count; j++) {
            gc_mark_object((Obj *)root->base[j]);
        }
    }

    gc_mark_value(tern_thrown);

    char here = 0;
    const char *top = (const char *)&here;
    if (top < gc_stack_bottom) {
        gc_scan_conservative_range(top, gc_stack_bottom);
    } else {
        gc_scan_conservative_range(gc_stack_bottom, top);
    }

    gc_scan_conservative_range((const char *)&registers, (const char *)&registers + sizeof registers);
    gc_trace();

    // Free lists are rebuilt from scratch every time: a block that is released
    // below must not leave entries behind that point into freed memory.
    for (int i = 0; i < GC_CLASSES; i++) {
        gc_free[i] = NULL;
    }

    // ---- sweep, pass 1: keep or drop each block
    size_t live_bytes = 0;
    int write = 0;
    for (int i = 0; i < gc_block_count; i++) {
        Block *block = gc_blocks[i];
        bool keep = true;

        if (block->single) {
            Obj *object = (Obj *)block->data;
            if (object->marked) {
                object->marked = 0;
                live_bytes += block->cap;
            } else {
                gc_release(object);
                keep = false;
            }
        } else {
            int live = 0;
            for (size_t offset = 0; offset < block->used; ) {
                Obj *object = (Obj *)(block->data + offset);
                offset += ((size_t)object->klass + 1) * GC_ALIGN;
                if (object->marked) {
                    live++;
                } else if (object->type != GC_DEAD) {
                    gc_release(object);
                }
            }

            // A block with nothing left in it (and not the one being bumped)
            // goes back to malloc.
            if (live == 0 && block != gc_current) {
                keep = false;
            }
        }

        if (keep) {
            if (!block->single) {
                // ---- pass 2 for this block: dead objects join the free lists
                for (size_t offset = 0; offset < block->used; ) {
                    Obj *object = (Obj *)(block->data + offset);
                    size_t size = ((size_t)object->klass + 1) * GC_ALIGN;
                    offset += size;
                    if (object->marked) {
                        object->marked = 0;
                        live_bytes += size;
                    } else if (object->type != GC_DEAD) {
                        int klass = (int)(size / GC_ALIGN) - 1;
                        object->type = GC_DEAD;
                        object->klass = (uint8_t)klass;
                        gc_set_link(object, gc_free[klass]);
                        gc_free[klass] = object;
                    }
                }
            }
            gc_blocks[write++] = block;
        } else {
            free(block);
        }
    }
    gc_block_count = write;

    gc_live_bytes = live_bytes;
    gc_since = 0;
    if (!gc_threshold_fixed) {
        size_t floor = 16u << 20;
        gc_threshold = live_bytes * 2 > floor ? live_bytes * 2 : floor;
    }
}

/// Drops free-list entries that point into a block that is about to be freed.
/// (Called before the sweep releases blocks; kept separate for clarity.)
/// Fresh bump block. Collections are triggered by `tern_alloc` itself, which
/// keeps the threshold meaningful instead of tying it to block size.
static void gc_new_current(size_t size) {
    if (!gc_current || gc_current->used + size > gc_current->cap) {
        gc_current = gc_new_block(GC_BLOCK);
    }
}

Value *tern_alloc(size_t size, ObjType type) {
    size = (size + (GC_ALIGN - 1)) & ~(size_t)(GC_ALIGN - 1);

    // One compare per allocation. Everything below may call into user code
    // (string formatting), so it is done before any object exists.
    if (gc_since >= gc_threshold) {
        tern_gc_collect();
    }

    Obj *object = NULL;

    if (size <= GC_SMALL_MAX) {
        int klass = (int)(size / GC_ALIGN) - 1;
        object = (Obj *)gc_free[klass];
        if (object) {
            gc_free[klass] = gc_link(object);
            memset(object, 0, size);
        }
    }

    if (!object) {
        if (size > GC_SMALL_MAX) {
            // One block per large object: the sweep can free the whole block.
            Block *block = gc_new_block(size);
            block->single = 1;
            block->used = size;
            gc_poison(block, 0);
            object = (Obj *)block->data;
        } else {
            int klass = (int)(size / GC_ALIGN) - 1;
            if (!gc_current || gc_current->used + size > gc_current->cap) {
                gc_new_current(size);
                object = (Obj *)gc_free[klass];
                if (object) {
                    gc_free[klass] = gc_link(object);
                    memset(object, 0, size);
                }
            }

            if (!object) {
                Block *block = gc_current;
                if (!block || block->used + size > block->cap) {
                    tern_panic("MemoryError", "out of memory");
                }
                size_t offset = block->used;
                gc_poison(block, offset);
                block->used = offset + size;
                object = (Obj *)(block->data + offset);
            }
        }
    }

    gc_since += size;
    gc_allocated_total += size;
    object->type = type;
    object->marked = 0;
    object->klass = size <= GC_SMALL_MAX ? (uint8_t)(size / GC_ALIGN - 1) : GC_LARGE;
    return (Value *)object;
}

// ------------------------------------------------------------------ strings

static uint32_t hash_bytes(const char *data, size_t len) {
    uint32_t hash = 2166136261u;
    for (size_t i = 0; i < len; i++) {
        hash ^= (unsigned char)data[i];
        hash *= 16777619u;
    }
    return hash;
}

Str *str_from(const char *data, size_t len) {
    Str *s = (Str *)tern_alloc(sizeof(Str) + len + 1, O_STR);
    s->len = (uint32_t)len;
    s->hash = hash_bytes(data, len);
    if (len) {
        memcpy(s->chars, data, len);
    }
    s->chars[len] = '\0';
    return s;
}

Value str_value(const char *data, size_t len) { return v_obj((Obj *)str_from(data, len)); }

Value str_from_cstr(const char *text) { return str_value(text, strlen(text)); }

Value str_from_int(int64_t value) {
    char buffer[32];
    int n = snprintf(buffer, sizeof buffer, "%lld", (long long)value);
    return str_value(buffer, (size_t)n);
}

// Shortest representation that round-trips, matching .NET's "R" formatting.
Value str_from_double(double value) {
    char buffer[40];
    if (isnan(value)) {
        return str_from_cstr("nan");
    }
    if (isinf(value)) {
        return str_from_cstr(value > 0 ? "inf" : "-inf");
    }

    for (int precision = 15; precision <= 17; precision++) {
        snprintf(buffer, sizeof buffer, "%.*g", precision, value);
        if (strtod(buffer, NULL) == value) {
            break;
        }
    }

    // Integers print with a trailing ".0" so floats stay distinguishable.
    if (!strpbrk(buffer, ".eE")) {
        size_t n = strlen(buffer);
        buffer[n] = '.';
        buffer[n + 1] = '0';
        buffer[n + 2] = '\0';
    }
    return str_from_cstr(buffer);
}

// ------------------------------------------------------------------ lists

List *list_new(int cap) {
    if (cap < 4) {
        cap = 4;
    }
    List *list = (List *)tern_alloc(sizeof(List), O_LIST);
    list->cap = cap;
    list->count = 0;
    list->items = (Value *)calloc((size_t)cap, sizeof(Value));
    return list;
}

static void list_reserve(List *list, int needed) {
    if (needed <= list->cap) {
        return;
    }
    int cap = list->cap;
    while (cap < needed) {
        cap *= 2;
    }
    list->items = (Value *)realloc(list->items, (size_t)cap * sizeof(Value));
    list->cap = cap;
}

Value tern_list_from(int count, Value *items) {
    List *list = list_new(count);
    for (int i = 0; i < count; i++) list_push(list, items[i]);
    return v_obj((Obj *)list);
}

void list_push(List *list, Value v) {
    list_reserve(list, list->count + 1);
    list->items[list->count++] = v;
}

Value list_get(List *list, int64_t index) {
    if (index < 0) {
        index += list->count;
    }
    if (index < 0 || index >= list->count) {
        tern_panic("IndexError", "index %lld out of range for list of length %d",
                   (long long)index, list->count);
    }
    return list->items[index];
}

void list_set(List *list, int64_t index, Value v) {
    if (index < 0) {
        index += list->count;
    }
    if (index < 0 || index >= list->count) {
        tern_panic("IndexError", "index %lld out of range for list of length %d",
                   (long long)index, list->count);
    }
    list->items[index] = v;
}

// ------------------------------------------------------------------ hashing

static uint64_t mix(uint64_t v) {
    v *= 0x9E3779B97F4A7C15ULL;
    v = (v ^ (v >> 30)) * 0xBF58476D1CE4E5B9ULL;
    v = (v ^ (v >> 27)) * 0x94D049BB133111EBULL;
    return v ^ (v >> 31);
}

uint64_t tern_hash(Value v) {
    switch (v.tag) {
        case T_NIL:
        case T_MISSING:
            return 0;
        case T_BOOL:
            return v.as.i ? 1 : 2;
        case T_INT:
            return mix((uint64_t)v.as.i);
        case T_FLOAT: {
            double d = v.as.f;
            if (d >= -9.007199254740992e15 && d <= 9.007199254740992e15 && floor(d) == d) {
                return mix((uint64_t)(int64_t)d);
            }
            uint64_t bits;
            memcpy(&bits, &d, 8);
            return mix(bits);
        }
        case T_OBJ:
            switch (v.as.o->type) {
                case O_STR:
                    return ((Str *)v.as.o)->hash;
                default:
                    return mix((uint64_t)(uintptr_t)v.as.o);
            }
        default:
            return 0;
    }
}

bool tern_equal(Value a, Value b) {
    if (a.tag != b.tag) {
        if (v_is_num(a) && v_is_num(b)) {
            return v_as_double(a) == v_as_double(b);
        }
        return false;
    }
    switch (a.tag) {
        case T_NIL:
        case T_MISSING:
            return true;
        case T_BOOL:
        case T_INT:
            return a.as.i == b.as.i;
        case T_FLOAT:
            return a.as.f == b.as.f;
        case T_OBJ:
            if (a.as.o->type == O_STR) {
                Str *x = (Str *)a.as.o, *y = (Str *)b.as.o;
                return x->len == y->len && memcmp(x->chars, y->chars, x->len) == 0;
            }
            return a.as.o == b.as.o;
        default:
            return false;
    }
}

// ------------------------------------------------------------------ maps

Map *map_new(void) {
    Map *map = (Map *)tern_alloc(sizeof(Map), O_MAP);
    map->cap = 8;
    map->count = 0;
    map->used = 0;
    map->entries = (MapEntry *)calloc(8, sizeof(MapEntry));
    map->order = NULL;
    map->order_count = 0;
    map->order_cap = 0;
    return map;
}

static void map_insert_raw(Map *map, uint64_t hash, Value key, Value value);

static void map_grow(Map *map) {
    MapEntry *old = map->entries;
    int old_cap = map->cap;

    map->cap = old_cap * 2;
    map->entries = (MapEntry *)calloc((size_t)map->cap, sizeof(MapEntry));
    map->used = 0;
    map->count = 0;

    for (int i = 0; i < old_cap; i++) {
        if (old[i].used && !old[i].dead) {
            map_insert_raw(map, old[i].hash, old[i].key, old[i].value);
        }
    }

    free(old);
}

static MapEntry *map_find(Map *map, uint64_t hash, Value key) {
    uint32_t mask = (uint32_t)map->cap - 1;
    uint32_t slot = (uint32_t)hash & mask;
    MapEntry *fallback = NULL;

    for (;;) {
        MapEntry *entry = &map->entries[slot];
        if (!entry->used) {
            return fallback ? fallback : entry;
        }
        if (entry->dead) {
            if (!fallback) {
                fallback = entry;
            }
        } else if (entry->hash == hash && tern_equal(entry->key, key)) {
            return entry;
        }
        slot = (slot + 1) & mask;
    }
}

static void map_insert_raw(Map *map, uint64_t hash, Value key, Value value) {
    MapEntry *entry = map_find(map, hash, key);
    if (!entry->used || entry->dead) {
        map->used++;
        map->count++;
    }
    entry->hash = hash;
    entry->key = key;
    entry->value = value;
    entry->used = 1;
    entry->dead = 0;
}

void map_set(Map *map, Value key, Value value) {
    if (map->used + 1 > map->cap - map->cap / 4) {
        map_grow(map);
    }

    uint64_t hash = tern_hash(key);
    MapEntry *entry = map_find(map, hash, key);
    bool fresh = !entry->used || entry->dead;
    entry->hash = hash;
    entry->key = key;
    entry->value = value;
    entry->used = 1;
    entry->dead = 0;
    if (fresh) {
        map->count++;
        map->used++;
        if (map->order_count == map->order_cap) {
            map->order_cap = map->order_cap ? map->order_cap * 2 : 8;
            map->order = (Value *)realloc(map->order, (size_t)map->order_cap * sizeof(Value));
        }
        map->order[map->order_count++] = key;
    }
}

bool map_get(Map *map, Value key, Value *out) {
    MapEntry *entry = map_find(map, tern_hash(key), key);
    if (!entry->used || entry->dead) {
        return false;
    }
    *out = entry->value;
    return true;
}

bool map_has(Map *map, Value key) {
    MapEntry *entry = map_find(map, tern_hash(key), key);
    return entry->used && !entry->dead;
}

bool map_remove(Map *map, Value key) {
    MapEntry *entry = map_find(map, tern_hash(key), key);
    if (!entry->used || entry->dead) {
        return false;
    }
    entry->dead = 1;
    map->count--;
    for (int32_t i = 0; i < map->order_count; i++) {
        if (tern_equal(map->order[i], key)) {
            memmove(&map->order[i], &map->order[i + 1],
                    (size_t)(map->order_count - i - 1) * sizeof(Value));
            map->order_count--;
            break;
        }
    }
    return true;
}

// ------------------------------------------------------------------ closures

Cell *cell_new(Value value) {
    Cell *cell = (Cell *)tern_alloc(sizeof(Cell), O_CELL);
    cell->value = value;
    return cell;
}

Closure *closure_new(Proto *proto, int upvals) {
    Closure *closure = (Closure *)tern_alloc(sizeof(Closure) + (size_t)upvals * sizeof(Cell *), O_CLOSURE);
    closure->proto = proto;
    closure->up = upvals ? (Cell **)((char *)closure + sizeof(Closure)) : NULL;
    return closure;
}

// ------------------------------------------------------------------ buffers

typedef struct Buf {
    char *data;
    size_t len;
    size_t cap;
} Buf;

static void buf_init(Buf *buf) {
    buf->cap = 64;
    buf->len = 0;
    buf->data = (char *)malloc(buf->cap);
}

static void buf_need(Buf *buf, size_t extra) {
    if (buf->len + extra + 1 <= buf->cap) {
        return;
    }
    while (buf->cap < buf->len + extra + 1) {
        buf->cap *= 2;
    }
    buf->data = (char *)realloc(buf->data, buf->cap);
}

static void buf_add(Buf *buf, const char *text, size_t len) {
    buf_need(buf, len);
    memcpy(buf->data + buf->len, text, len);
    buf->len += len;
}

static void buf_add_cstr(Buf *buf, const char *text) { buf_add(buf, text, strlen(text)); }

static void buf_add_char(Buf *buf, char c) {
    buf_need(buf, 1);
    buf->data[buf->len++] = c;
}

static Value buf_finish(Buf *buf) {
    Value result = str_value(buf->data, buf->len);
    free(buf->data);
    return result;
}

// ------------------------------------------------------------------ conversion

const char *tern_type_name(Value v) {
    switch (v.tag) {
        case T_NIL: return "nil";
        case T_BOOL: return "bool";
        case T_INT: return "int";
        case T_FLOAT: return "float";
        case T_MISSING: return "missing";
        default: break;
    }
    switch (v.as.o->type) {
        case O_STR: return "string";
        case O_LIST: return "list";
        case O_MAP: return "map";
        case O_INSTANCE: return ((Instance *)v.as.o)->type->name;
        case O_CLOSURE: return "function";
        case O_NATIVE: return "native";
        case O_BOUND: return "method";
        case O_RANGE: return "range";
        case O_TYPE: return "struct";
        case O_CELL: return "cell";
        default: return "object";
    }
}

static void debug_into(Buf *buf, Value v);

Str *tern_to_debug(Value v) {
    Buf buf;
    buf_init(&buf);
    debug_into(&buf, v);
    Value result = buf_finish(&buf);
    return (Str *)result.as.o;
}

Value tern_to_string(Value v) {
    if (v.tag == T_OBJ && v.as.o->type == O_STR) {
        return v;
    }
    return v_obj((Obj *)tern_to_debug(v));
}

static void debug_string(Buf *buf, Str *s) {
    buf_add_char(buf, '"');
    for (uint32_t i = 0; i < s->len; i++) {
        char c = s->chars[i];
        switch (c) {
            case '\n': buf_add_cstr(buf, "\\n"); break;
            case '\r': buf_add_cstr(buf, "\\r"); break;
            case '\t': buf_add_cstr(buf, "\\t"); break;
            case '\\': buf_add_cstr(buf, "\\\\"); break;
            case '"': buf_add_cstr(buf, "\\\""); break;
            default: buf_add_char(buf, c); break;
        }
    }
    buf_add_char(buf, '"');
}

static void debug_into(Buf *buf, Value v) {
    switch (v.tag) {
        case T_NIL: buf_add_cstr(buf, "nil"); return;
        case T_MISSING: buf_add_cstr(buf, "<missing>"); return;
        case T_BOOL: buf_add_cstr(buf, v.as.i ? "true" : "false"); return;
        case T_INT: {
            char tmp[32];
            int n = snprintf(tmp, sizeof tmp, "%lld", (long long)v.as.i);
            buf_add(buf, tmp, (size_t)n);
            return;
        }
        case T_FLOAT: {
            Value s = str_from_double(v.as.f);
            Str *str = (Str *)s.as.o;
            buf_add(buf, str->chars, str->len);
            return;
        }
        default: break;
    }

    switch (v.as.o->type) {
        case O_STR:
            debug_string(buf, (Str *)v.as.o);
            return;

        case O_LIST: {
            List *list = (List *)v.as.o;
            buf_add_char(buf, '[');
            for (int32_t i = 0; i < list->count; i++) {
                if (i) {
                    buf_add_cstr(buf, ", ");
                }
                debug_into(buf, list->items[i]);
            }
            buf_add_char(buf, ']');
            return;
        }

        case O_MAP: {
            Map *map = (Map *)v.as.o;
            buf_add_char(buf, '{');
            bool first = true;
            for (int32_t i = 0; i < map->order_count; i++) {
                if (!first) {
                    buf_add_cstr(buf, ", ");
                }
                first = false;
                Value value = v_nil();
                map_get(map, map->order[i], &value);
                debug_into(buf, map->order[i]);
                buf_add_cstr(buf, ": ");
                debug_into(buf, value);
            }
            buf_add_char(buf, '}');
            return;
        }

        case O_INSTANCE: {
            Instance *instance = (Instance *)v.as.o;
            buf_add_cstr(buf, instance->type->name);
            buf_add_char(buf, '(');
            for (int32_t i = 0; i < instance->type->nfields; i++) {
                if (i) {
                    buf_add_cstr(buf, ", ");
                }
                if (instance->type->field_names) {
                    buf_add_cstr(buf, instance->type->field_names[i]);
                    buf_add_cstr(buf, ": ");
                }
                debug_into(buf, instance->fields[i]);
            }
            buf_add_char(buf, ')');
            return;
        }

        case O_CLOSURE: {
            Closure *closure = (Closure *)v.as.o;
            buf_add_cstr(buf, "<fn ");
            buf_add_cstr(buf, closure->proto->name ? closure->proto->name : "anonymous");
            buf_add_char(buf, '>');
            return;
        }

        case O_NATIVE: {
            buf_add_cstr(buf, "<native ");
            buf_add_cstr(buf, ((Native *)v.as.o)->name);
            buf_add_char(buf, '>');
            return;
        }

        case O_BOUND: {
            buf_add_cstr(buf, "<method>");
            return;
        }

        case O_RANGE: {
            Range *range = (Range *)v.as.o;
            char tmp[64];
            int n = snprintf(tmp, sizeof tmp, "%lld%s%lld", (long long)range->start,
                             range->inclusive ? ".." : "..<", (long long)range->end);
            buf_add(buf, tmp, (size_t)n);
            return;
        }

        case O_TYPE: {
            buf_add_cstr(buf, "<struct ");
            buf_add_cstr(buf, ((TypeObj *)v.as.o)->name);
            buf_add_char(buf, '>');
            return;
        }

        default:
            buf_add_cstr(buf, "<object>");
            return;
    }
}

// ------------------------------------------------------------------ arithmetic

static int64_t require_int(Value v, const char *what) {
    if (v.tag != T_INT) {
        tern_panic("TypeError", "%s expects an integer, not %s", what, tern_type_name(v));
    }
    return v.as.i;
}

Value tern_add(Value a, Value b) {
    if (a.tag == T_INT && b.tag == T_INT) {
        int64_t result;
        if (__builtin_add_overflow(a.as.i, b.as.i, &result)) {
            return v_float((double)a.as.i + (double)b.as.i);
        }
        return v_int(result);
    }
    if (v_is_num(a) && v_is_num(b)) {
        return v_float(v_as_double(a) + v_as_double(b));
    }
    if (v_is_str(a) || v_is_str(b)) {
        Value sa = tern_to_string(a);
        Value sb = tern_to_string(b);
        Str *x = (Str *)sa.as.o, *y = (Str *)sb.as.o;
        Str *joined = (Str *)tern_alloc(sizeof(Str) + x->len + y->len + 1, O_STR);
        joined->len = x->len + y->len;
        memcpy(joined->chars, x->chars, x->len);
        memcpy(joined->chars + x->len, y->chars, y->len);
        joined->chars[joined->len] = '\0';
        joined->hash = hash_bytes(joined->chars, joined->len);
        return v_obj((Obj *)joined);
    }
    if (v_is_list(a) && v_is_list(b)) {
        List *x = (List *)a.as.o, *y = (List *)b.as.o;
        List *joined = list_new(x->count + y->count);
        for (int32_t i = 0; i < x->count; i++) {
            list_push(joined, x->items[i]);
        }
        for (int32_t i = 0; i < y->count; i++) {
            list_push(joined, y->items[i]);
        }
        return v_obj((Obj *)joined);
    }
    if (v_is_map(a) && v_is_map(b)) {
        // Merging walks the insertion-order arrays, not the hash slots: the
        // result must keep both maps' order, left map first.
        Map *x = (Map *)a.as.o, *y = (Map *)b.as.o;
        Map *merged = map_new();
        for (int32_t i = 0; i < x->order_count; i++) {
            Value value;
            if (map_get(x, x->order[i], &value)) {
                map_set(merged, x->order[i], value);
            }
        }
        for (int32_t i = 0; i < y->order_count; i++) {
            Value value;
            if (map_get(y, y->order[i], &value)) {
                map_set(merged, y->order[i], value);
            }
        }
        return v_obj((Obj *)merged);
    }
    tern_panic("TypeError", "operator '+' is not defined for %s and %s",
               tern_type_name(a), tern_type_name(b));
    return v_nil();
}

static double numeric(Value v, const char *op) {
    if (!v_is_num(v)) {
        tern_panic("TypeError", "operator '%s' expects numbers, got %s", op, tern_type_name(v));
    }
    return v_as_double(v);
}

Value tern_sub(Value a, Value b) {
    if (a.tag == T_INT && b.tag == T_INT) {
        int64_t result;
        if (__builtin_sub_overflow(a.as.i, b.as.i, &result)) {
            return v_float((double)a.as.i - (double)b.as.i);
        }
        return v_int(result);
    }
    return v_float(numeric(a, "-") - numeric(b, "-"));
}

Value tern_mul(Value a, Value b) {
    if (a.tag == T_INT && b.tag == T_INT) {
        int64_t result;
        if (__builtin_mul_overflow(a.as.i, b.as.i, &result)) {
            return v_float((double)a.as.i * (double)b.as.i);
        }
        return v_int(result);
    }
    return v_float(numeric(a, "*") * numeric(b, "*"));
}

Value tern_div(Value a, Value b) {
    if (a.tag == T_INT && b.tag == T_INT) {
        if (b.as.i == 0) {
            tern_panic("DivisionByZero", "division by zero");
        }
        if (b.as.i == -1) {
            return a.as.i == INT64_MIN ? v_float(-(double)a.as.i) : v_int(-a.as.i);
        }
        if (a.as.i % b.as.i == 0) {
            return v_int(a.as.i / b.as.i);
        }
        return v_float((double)a.as.i / (double)b.as.i);
    }
    double divisor = numeric(b, "/");
    if (divisor == 0) {
        tern_panic("DivisionByZero", "division by zero");
    }
    return v_float(numeric(a, "/") / divisor);
}

Value tern_mod(Value a, Value b) {
    if (a.tag == T_INT && b.tag == T_INT) {
        if (b.as.i == 0) {
            tern_panic("DivisionByZero", "modulo by zero");
        }
        return v_int(b.as.i == -1 ? 0 : a.as.i % b.as.i);
    }
    double divisor = numeric(b, "%");
    if (divisor == 0) {
        tern_panic("DivisionByZero", "modulo by zero");
    }
    return v_float(fmod(numeric(a, "%"), divisor));
}

Value tern_pow(Value a, Value b) {
    if (a.tag == T_INT && b.tag == T_INT && b.as.i >= 0 && b.as.i <= 62) {
        int64_t result = 1;
        int64_t base = a.as.i;
        int64_t exponent = b.as.i;
        while (exponent > 0) {
            if (exponent & 1) {
                result *= base;
            }
            base *= base;
            exponent >>= 1;
        }
        return v_int(result);
    }
    return v_float(pow(numeric(a, "**"), numeric(b, "**")));
}

Value tern_band(Value a, Value b) { return v_int(require_int(a, "'&'") & require_int(b, "'&'")); }
Value tern_bor(Value a, Value b) { return v_int(require_int(a, "'|'") | require_int(b, "'|'")); }
Value tern_bxor(Value a, Value b) { return v_int(require_int(a, "'^'") ^ require_int(b, "'^'")); }

Value tern_shl(Value a, Value b) {
    int64_t shift = require_int(b, "'<<'");
    return v_int((int64_t)((uint64_t)require_int(a, "'<<'") << (shift & 63)));
}

Value tern_shr(Value a, Value b) {
    int64_t shift = require_int(b, "'>>'");
    return v_int(require_int(a, "'>>'") >> (shift & 63));
}

Value tern_neg(Value a) {
    if (a.tag == T_INT) {
        return v_int(a.as.i == INT64_MIN ? a.as.i : -a.as.i);
    }
    if (a.tag == T_FLOAT) {
        return v_float(-a.as.f);
    }
    tern_panic("TypeError", "cannot negate %s", tern_type_name(a));
    return v_nil();
}

Value tern_bnot(Value a) { return v_int(~require_int(a, "'~'")); }

Value tern_compare(Value a, Value b) {
    if (v_is_num(a) && v_is_num(b)) {
        if (a.tag == T_INT && b.tag == T_INT) {
            return v_int(a.as.i < b.as.i ? -1 : (a.as.i > b.as.i ? 1 : 0));
        }
        double x = v_as_double(a), y = v_as_double(b);
        return v_int(x < y ? -1 : (x > y ? 1 : 0));
    }
    if (v_is_str(a) && v_is_str(b)) {
        Str *x = (Str *)a.as.o, *y = (Str *)b.as.o;
        uint32_t n = x->len < y->len ? x->len : y->len;
        int cmp = memcmp(x->chars, y->chars, n);
        if (cmp == 0) {
            cmp = x->len < y->len ? -1 : (x->len > y->len ? 1 : 0);
        }
        return v_int(cmp < 0 ? -1 : (cmp > 0 ? 1 : 0));
    }
    tern_panic("TypeError", "cannot compare %s with %s", tern_type_name(a), tern_type_name(b));
    return v_nil();
}

// ------------------------------------------------------------------ access

int64_t tern_length(Value v) {
    if (v_is_str(v)) {
        return ((Str *)v.as.o)->len;
    }
    if (v_is_list(v)) {
        return ((List *)v.as.o)->count;
    }
    if (v_is_map(v)) {
        return ((Map *)v.as.o)->count;
    }
    if (v.tag == T_OBJ && v.as.o->type == O_RANGE) {
        Range *range = (Range *)v.as.o;
        int64_t end = range->inclusive ? range->end + 1 : range->end;
        return end - range->start > 0 ? end - range->start : 0;
    }
    tern_panic("TypeError", "%s has no length", tern_type_name(v));
    return 0;
}

static Instance *require_instance(Value v) __attribute__((unused));
static Instance *require_instance(Value v) {
    if (v.tag == T_OBJ && v.as.o->type == O_INSTANCE) {
        return (Instance *)v.as.o;
    }
    tern_panic("TypeError", "expected a struct instance, not %s", tern_type_name(v));
    return NULL;
}

static int find_field(TypeObj *type, const char *name) {
    for (int32_t i = 0; i < type->nfields; i++) {
        if (strcmp(type->field_names[i], name) == 0) {
            return i;
        }
    }
    return -1;
}

static Closure *find_method(TypeObj *type, const char *name) {
    for (int32_t i = 0; i < type->nmethods; i++) {
        if (strcmp(type->method_names[i], name) == 0) {
            return type->methods[i];
        }
    }
    return NULL;
}

Value tern_new_instance(TypeObj *type, int argc, Value *argv) {
    if (argc > type->nfields) {
        tern_panic("TypeError", "%s() takes at most %d field argument(s) but %d were given",
                   type->name, type->nfields, argc);
    }

    Instance *instance = (Instance *)tern_alloc(sizeof(Instance), O_INSTANCE);
    instance->type = type;
    instance->fields = (Value *)calloc((size_t)(type->nfields > 0 ? type->nfields : 1), sizeof(Value));
    for (int32_t i = 0; i < type->nfields; i++) {
        instance->fields[i] = type->defaults[i];
    }

    /* Defaults that are not literals are compiled to a prototype each and run
       per instance, exactly like the interpreter does. */
    if (type->defproto) {
        for (int32_t i = 0; i < type->nfields; i++) {
            if (type->defproto[i]) {
                instance->fields[i] = tern_call(v_obj((Obj *)type->defproto[i]), 0, NULL);
            }
        }
    }

    for (int i = 0; i < argc; i++) {
        instance->fields[i] = argv[i];
    }

    return v_obj((Obj *)instance);
}

// Recursion is bounded by the C stack rather than by a bytecode budget. The
// limit is high enough for real programs (about 3 MB of stack) and low enough
// that hitting it raises a catchable error instead of a SIGSEGV.
static const int TERN_MAX_CALL_DEPTH = 10000;

Value tern_call(Value callee, int argc, Value *argv) {
    if (callee.tag == T_OBJ) {
        switch (callee.as.o->type) {
            case O_CLOSURE: {
                Closure *closure = (Closure *)callee.as.o;
                if (++tern_call_depth > TERN_MAX_CALL_DEPTH) {
                    tern_call_depth--;
                    tern_panic("StackOverflow", "call depth exceeded %d", TERN_MAX_CALL_DEPTH);
                }
                Value result = closure->proto->fn(closure, argc, argv);
                tern_call_depth--;
                return result;
            }
            case O_NATIVE: {
                Native *native = (Native *)callee.as.o;
                return native->fn(argc, argv);
            }
            case O_TYPE:
                return tern_new_instance((TypeObj *)callee.as.o, argc, argv);
            case O_BOUND: {
                Bound *bound = (Bound *)callee.as.o;
                Value stack_args[argc + 1];
                stack_args[0] = bound->receiver;
                for (int i = 0; i < argc; i++) {
                    stack_args[i + 1] = argv[i];
                }
                return tern_call(bound->callee, argc + 1, stack_args);
            }
            default:
                break;
        }
    }
    tern_panic("TypeError", "%s is not callable", tern_type_name(callee));
    return v_nil();
}

Value tern_get_member(Value target, const char *name) {
    if (target.tag == T_OBJ) {
        switch (target.as.o->type) {
            case O_INSTANCE: {
                Instance *instance = (Instance *)target.as.o;
                int field = find_field(instance->type, name);
                if (field >= 0) {
                    return instance->fields[field];
                }
                Closure *method = find_method(instance->type, name);
                if (method) {
                    Bound *bound = (Bound *)tern_alloc(sizeof(Bound), O_BOUND);
                    bound->receiver = target;
                    bound->callee = v_obj((Obj *)method);
                    return v_obj((Obj *)bound);
                }
                tern_panic("MemberError", "%s has no member '%s'", instance->type->name, name);
                break;
            }

            case O_MAP: {
                Value found;
                if (map_get((Map *)target.as.o, str_from_cstr(name), &found)) {
                    return found;
                }
                break;
            }

            default:
                break;
        }
    }

    if (strcmp(name, "length") == 0 || strcmp(name, "count") == 0 || strcmp(name, "size") == 0) {
        if (v_is_str(target) || v_is_list(target) || v_is_map(target) || v_is_obj(target, O_RANGE)) {
            return v_int(tern_length(target));
        }
    }

    if (v_is_obj(target, O_RANGE)) {
        Range *range = (Range *)target.as.o;
        if (strcmp(name, "start") == 0) return v_int(range->start);
        if (strcmp(name, "end") == 0) {
            return v_int(range->inclusive ? range->end + 1 : range->end);
        }
    }

    // A method read as a value (`let f = xs.push;`) becomes a bound method.
    NativeFn method = tern_builtin_method(target, name);
    if (method) {
        Native *native = (Native *)tern_alloc(sizeof(Native), O_NATIVE);
        native->name = name;
        native->fn = method;
        Bound *bound = (Bound *)tern_alloc(sizeof(Bound), O_BOUND);
        bound->receiver = target;
        bound->callee = v_obj((Obj *)native);
        return v_obj((Obj *)bound);
    }

    tern_panic("MemberError", "%s has no member '%s'", tern_type_name(target), name);
    return v_nil();
}

Value tern_set_member(Value target, const char *name, Value value) {
    if (target.tag == T_OBJ) {
        switch (target.as.o->type) {
            case O_INSTANCE: {
                Instance *instance = (Instance *)target.as.o;
                int field = find_field(instance->type, name);
                if (field < 0) {
                    tern_panic("MemberError", "%s has no field '%s'", instance->type->name, name);
                }
                instance->fields[field] = value;
                return value;
            }
            case O_MAP:
                map_set((Map *)target.as.o, str_from_cstr(name), value);
                return value;
            default:
                break;
        }
    }
    tern_panic("TypeError", "cannot set member '%s' on %s", name, tern_type_name(target));
    return value;
}

Value tern_get_index(Value target, Value index) {
    if (target.tag == T_OBJ) {
        switch (target.as.o->type) {
            case O_LIST: {
                if (index.tag == T_OBJ && index.as.o->type == O_RANGE) {
                    return tern_slice(target, v_int(((Range *)index.as.o)->start),
                                      v_int(((Range *)index.as.o)->end),
                                      ((Range *)index.as.o)->inclusive);
                }
                return list_get((List *)target.as.o, require_int(index, "list index"));
            }
            case O_MAP: {
                Value found;
                if (map_get((Map *)target.as.o, index, &found)) {
                    return found;
                }
                return v_nil();
            }
            case O_STR: {
                Str *s = (Str *)target.as.o;
                if (index.tag == T_OBJ && index.as.o->type == O_RANGE) {
                    return tern_slice(target, v_int(((Range *)index.as.o)->start),
                                      v_int(((Range *)index.as.o)->end),
                                      ((Range *)index.as.o)->inclusive);
                }
                int64_t position = require_int(index, "string index");
                if (position < 0) {
                    position += s->len;
                }
                if (position < 0 || position >= s->len) {
                    tern_panic("IndexError", "index %lld out of range for string of length %u",
                               (long long)position, s->len);
                }
                return str_value(&s->chars[position], 1);
            }
            case O_INSTANCE: {
                Instance *instance = (Instance *)target.as.o;
                if (!v_is_str(index)) {
                    tern_panic("TypeError", "%s index must be a string", instance->type->name);
                }
                int field = find_field(instance->type, ((Str *)index.as.o)->chars);
                if (field < 0) {
                    tern_panic("MemberError", "%s has no field '%s'", instance->type->name,
                               ((Str *)index.as.o)->chars);
                }
                return instance->fields[field];
            }
            default:
                break;
        }
    }
    tern_panic("TypeError", "%s does not support indexing", tern_type_name(target));
    return v_nil();
}

Value tern_set_index(Value target, Value index, Value value) {
    if (target.tag == T_OBJ) {
        switch (target.as.o->type) {
            case O_LIST:
                list_set((List *)target.as.o, require_int(index, "list index"), value);
                return value;
            case O_MAP:
                map_set((Map *)target.as.o, index, value);
                return value;
            case O_INSTANCE: {
                Instance *instance = (Instance *)target.as.o;
                if (!v_is_str(index)) {
                    tern_panic("TypeError", "%s index must be a string", instance->type->name);
                }
                int field = find_field(instance->type, ((Str *)index.as.o)->chars);
                if (field < 0) {
                    tern_panic("MemberError", "%s has no field '%s'", instance->type->name,
                               ((Str *)index.as.o)->chars);
                }
                instance->fields[field] = value;
                return value;
            }
            default:
                break;
        }
    }
    tern_panic("TypeError", "%s does not support indexed assignment", tern_type_name(target));
    return value;
}

Value tern_iter_new(Value iterable) {
    Iterator *it = (Iterator *)tern_alloc(sizeof(Iterator), O_ITER);
    it->target = iterable;
    it->index = 0;
    if (v_is_list(iterable)) {
        it->kind = 0;
    } else if (v_is_map(iterable)) {
        it->kind = 1;
    } else if (v_is_str(iterable)) {
        it->kind = 3;
    } else if (iterable.tag == T_OBJ && iterable.as.o->type == O_RANGE) {
        it->kind = 2;
    } else {
        tern_panic("TypeError", "cannot iterate over %s", tern_type_name(iterable));
    }
    return v_obj((Obj *)it);
}

bool tern_iter_next(Value value, Value *first, Value *second) {
    Iterator *it = (Iterator *)value.as.o;
    switch (it->kind) {
        case 0: {
            List *list = (List *)it->target.as.o;
            if (it->index >= list->count) {
                return false;
            }
            *first = list->items[it->index++];
            return true;
        }
        case 1: {
            Map *map = (Map *)it->target.as.o;
            while (it->index < map->order_count) {
                Value key = map->order[it->index++];
                Value value;
                if (!map_get(map, key, &value)) continue;
                *first = key;
                *second = value;
                return true;
            }
            return false;
        }
        case 2: {
            Range *range = (Range *)it->target.as.o;
            int64_t end = range->inclusive ? range->end + 1 : range->end;
            int64_t current = range->start + it->index;
            if (current >= end) {
                return false;
            }
            *first = v_int(current);
            it->index++;
            return true;
        }
        default: {
            Str *s = (Str *)it->target.as.o;
            if ((uint32_t)it->index >= s->len) {
                return false;
            }
            *first = str_value(s->chars + it->index, 1);
            it->index++;
            return true;
        }
    }
}

Value tern_concat(Value *parts, int count) {
    if (count == 1) {
        return tern_to_string(parts[0]);
    }

    // Fast path: ints, bools and strings, sized exactly first (one allocation).
    size_t total = 0;
    for (int i = 0; i < count; i++) {
        Value part = parts[i];
        if (v_is_str(part)) {
            total += ((Str *)part.as.o)->len;
        } else if (part.tag == T_INT) {
            char tmp[32];
            total += (size_t)snprintf(tmp, sizeof tmp, "%lld", (long long)part.as.i);
        } else if (part.tag == T_BOOL) {
            total += part.as.i ? 4 : 5;
        } else if (part.tag == T_NIL) {
            total += 3;
        } else {
            Buf buf;
            buf_init(&buf);
            for (int j = 0; j < count; j++) {
                Value piece = tern_to_string(parts[j]);
                buf_add(&buf, ((Str *)piece.as.o)->chars, ((Str *)piece.as.o)->len);
            }
            return buf_finish(&buf);
        }
    }

    Str *joined = (Str *)tern_alloc(sizeof(Str) + total + 1, O_STR);
    size_t position = 0;
    for (int i = 0; i < count; i++) {
        Value part = parts[i];
        if (v_is_str(part)) {
            Str *s = (Str *)part.as.o;
            memcpy(joined->chars + position, s->chars, s->len);
            position += s->len;
        } else if (part.tag == T_INT) {
            position += (size_t)snprintf(joined->chars + position, 32, "%lld", (long long)part.as.i);
        } else if (part.tag == T_BOOL) {
            const char *word = part.as.i ? "true" : "false";
            size_t n = strlen(word);
            memcpy(joined->chars + position, word, n);
            position += n;
        } else {
            memcpy(joined->chars + position, "nil", 3);
            position += 3;
        }
    }
    joined->len = (uint32_t)position;
    joined->chars[position] = '\0';
    joined->hash = hash_bytes(joined->chars, joined->len);
    return v_obj((Obj *)joined);
}

Value tern_slice(Value target, Value start, Value end, bool inclusive) {
    if (v_is_list(target)) {
        List *list = (List *)target.as.o;
        int64_t from = start.tag == T_NIL ? 0 : require_int(start, "slice");
        int64_t to = end.tag == T_NIL ? list->count : require_int(end, "slice") + (inclusive ? 1 : 0);
        if (from < 0) from += list->count;
        if (to < 0) to += list->count;
        if (from < 0) from = 0;
        if (to > list->count) to = list->count;
        if (to < from) to = from;
        List *result = list_new((int)(to - from));
        for (int64_t i = from; i < to; i++) {
            list_push(result, list->items[i]);
        }
        return v_obj((Obj *)result);
    }

    if (v_is_str(target)) {
        Str *s = (Str *)target.as.o;
        int64_t from = start.tag == T_NIL ? 0 : require_int(start, "slice");
        int64_t to = end.tag == T_NIL ? s->len : require_int(end, "slice") + (inclusive ? 1 : 0);
        if (from < 0) from += s->len;
        if (to < 0) to += s->len;
        if (from < 0) from = 0;
        if (to > s->len) to = s->len;
        if (to < from) to = from;
        return str_value(s->chars + from, (size_t)(to - from));
    }

    tern_panic("TypeError", "cannot slice %s", tern_type_name(target));
    return v_nil();
}

bool tern_contains(Value container, Value item) {
    if (v_is_str(container)) {
        Str *s = (Str *)container.as.o;
        Value needle = tern_to_string(item);
        Str *n = (Str *)needle.as.o;
        if (n->len == 0) {
            return true;
        }
        if (n->len > s->len) {
            return false;
        }
        return memmem(s->chars, s->len, n->chars, n->len) != NULL;
    }
    if (v_is_list(container)) {
        List *list = (List *)container.as.o;
        for (int32_t i = 0; i < list->count; i++) {
            if (tern_equal(list->items[i], item)) {
                return true;
            }
        }
        return false;
    }
    if (v_is_map(container)) {
        return map_has((Map *)container.as.o, item);
    }
    if (container.tag == T_OBJ && container.as.o->type == O_RANGE) {
        Range *range = (Range *)container.as.o;
        if (item.tag != T_INT) {
            return false;
        }
        int64_t end = range->inclusive ? range->end : range->end - 1;
        return item.as.i >= range->start && item.as.i <= end;
    }
    tern_panic("TypeError", "cannot use 'in' on a %s", tern_type_name(container));
    return false;
}

// ------------------------------------------------------------------ natives

static Value native_string(Value v) { return tern_to_string(v); }

static Str *as_str(Value v, const char *what) {
    if (!v_is_str(v)) {
        tern_panic("TypeError", "%s expects a string, not %s", what, tern_type_name(v));
    }
    return (Str *)v.as.o;
}

static Value arg(int argc, Value *argv, int index) {
    return index < argc ? argv[index] : v_nil();
}

static int64_t iarg(int argc, Value *argv, int index, const char *what) {
    return require_int(arg(argc, argv, index), what);
}

// --- globals ---------------------------------------------------------------

static Value n_print(int argc, Value *argv) {
    for (int i = 0; i < argc; i++) {
        if (i > 0) fputc(' ', stdout);
        Value text = native_string(argv[i]);
        Str *s = (Str *)text.as.o;
        fwrite(s->chars, 1, s->len, stdout);
    }
    return v_nil();
}

static Value n_println(int argc, Value *argv) {
    n_print(argc, argv);
    fputc('\n', stdout);
    return v_nil();
}

static Value n_eprint(int argc, Value *argv) {
    for (int i = 0; i < argc; i++) {
        if (i > 0) fputc(' ', stderr);
        Value text = native_string(argv[i]);
        Str *s = (Str *)text.as.o;
        fwrite(s->chars, 1, s->len, stderr);
    }
    return v_nil();
}

static Value n_len(int argc, Value *argv) { return v_int(tern_length(arg(argc, argv, 0))); }

static Value n_type(int argc, Value *argv) {
    return str_from_cstr(tern_type_name(arg(argc, argv, 0)));
}

static Value n_str(int argc, Value *argv) { return native_string(arg(argc, argv, 0)); }

static Value n_repr(int argc, Value *argv) {
    return v_obj((Obj *)tern_to_debug(arg(argc, argv, 0)));
}

static Value n_int(int argc, Value *argv) {
    Value v = arg(argc, argv, 0);
    if (v.tag == T_INT) {
        return v;
    }
    if (v.tag == T_FLOAT) {
        return v_int((int64_t)v.as.f);
    }
    if (v.tag == T_BOOL) {
        return v_int(v.as.i);
    }
    if (v_is_str(v)) {
        Str *s = (Str *)v.as.o;
        char *end = NULL;
        int64_t value;
        if (argc > 1) {
            value = strtoll(s->chars, &end, (int)iarg(argc, argv, 1, "int"));
        } else {
            // Same order as the interpreter: a whole number first, then a float
            // truncated towards zero. Anything else is a catchable error.
            value = strtoll(s->chars, &end, 10);
            if (s->len == 0 || (end && *end != '\0')) {
                double as_double = strtod(s->chars, &end);
                if (s->len == 0 || (end && *end != '\0')) {
                    tern_panic("ArgumentError", "cannot convert '%s' to an integer", s->chars);
                }
                value = (int64_t)as_double;
            }
        }
        if (end && end == s->chars && s->len > 0) {
            tern_panic("ArgumentError", "cannot convert '%s' to an integer", s->chars);
        }
        return v_int(value);
    }
    tern_panic("TypeError", "cannot convert %s to int", tern_type_name(v));
    return v_nil();
}

static Value n_float(int argc, Value *argv) {
    Value v = arg(argc, argv, 0);
    if (v.tag == T_FLOAT) {
        return v;
    }
    if (v.tag == T_INT || v.tag == T_BOOL) {
        return v_float((double)v.as.i);
    }
    if (v_is_str(v)) {
        Str *s = (Str *)v.as.o;
        char *end = NULL;
        double value = strtod(s->chars, &end);
        if (s->len == 0 || (end && *end != '\0')) {
            tern_panic("ArgumentError", "cannot convert '%s' to a number", s->chars);
        }
        return v_float(value);
    }
    tern_panic("TypeError", "cannot convert %s to float", tern_type_name(v));
    return v_nil();
}

static Value n_bool(int argc, Value *argv) { return v_bool(v_truthy(arg(argc, argv, 0))); }

static Value n_chr(int argc, Value *argv) {
    int64_t code = iarg(argc, argv, 0, "chr");
    char buffer[4];
    int n = 0;
    if (code < 0x80) {
        buffer[n++] = (char)code;
    } else if (code < 0x800) {
        buffer[n++] = (char)(0xC0 | (code >> 6));
        buffer[n++] = (char)(0x80 | (code & 0x3F));
    } else if (code < 0x10000) {
        buffer[n++] = (char)(0xE0 | (code >> 12));
        buffer[n++] = (char)(0x80 | ((code >> 6) & 0x3F));
        buffer[n++] = (char)(0x80 | (code & 0x3F));
    } else {
        buffer[n++] = (char)(0xF0 | (code >> 18));
        buffer[n++] = (char)(0x80 | ((code >> 12) & 0x3F));
        buffer[n++] = (char)(0x80 | ((code >> 6) & 0x3F));
        buffer[n++] = (char)(0x80 | (code & 0x3F));
    }
    return str_value(buffer, (size_t)n);
}

static Value n_ord(int argc, Value *argv) {
    Str *s = as_str(arg(argc, argv, 0), "ord");
    if (s->len == 0) {
        return v_nil();
    }
    return v_int((unsigned char)s->chars[0]);
}

static Value n_range(int argc, Value *argv) {
    Range *range = (Range *)tern_alloc(sizeof(Range), O_RANGE);
    if (argc >= 2) {
        range->start = iarg(argc, argv, 0, "range");
        range->end = iarg(argc, argv, 1, "range");
    } else {
        range->start = 0;
        range->end = iarg(argc, argv, 0, "range");
    }
    range->inclusive = false;
    return v_obj((Obj *)range);
}

static Value n_keys(int argc, Value *argv) {
    Value v = arg(argc, argv, 0);
    if (!v_is_map(v)) {
        tern_panic("TypeError", "keys expects a map");
    }
    Map *map = (Map *)v.as.o;
    List *keys = list_new(map->count);
    for (int32_t i = 0; i < map->order_count; i++) {
        list_push(keys, map->order[i]);
    }
    return v_obj((Obj *)keys);
}

static Value n_values(int argc, Value *argv) {
    Value v = arg(argc, argv, 0);
    if (!v_is_map(v)) {
        tern_panic("TypeError", "values expects a map");
    }
    Map *map = (Map *)v.as.o;
    List *values = list_new(map->count);
    for (int32_t i = 0; i < map->order_count; i++) {
        Value value;
        map_get(map, map->order[i], &value);
        list_push(values, value);
    }
    return v_obj((Obj *)values);
}

static Value n_has(int argc, Value *argv) {
    Value container = arg(argc, argv, 0);
    Value item = arg(argc, argv, 1);
    if (!v_is_map(container)) {
        tern_panic("TypeError", "has expects a map");
    }
    return v_bool(map_has((Map *)container.as.o, item));
}

static Value n_assert(int argc, Value *argv) {
    if (!v_truthy(arg(argc, argv, 0))) {
        Value message = arg(argc, argv, 1);
        if (v_is_str(message)) {
            tern_panic("AssertionError", "%s", ((Str *)message.as.o)->chars);
        }
        tern_panic("AssertionError", "assertion failed");
    }
    return v_nil();
}

static Value n_error(int argc, Value *argv) {
    Value message = native_string(arg(argc, argv, 0));
    tern_panic("Error", "%s", ((Str *)message.as.o)->chars);
    return v_nil();
}

static Value n_panic(int argc, Value *argv) {
    Value message = native_string(arg(argc, argv, 0));
    tern_panic("Panic", "%s", ((Str *)message.as.o)->chars);
    return v_nil();
}

static Value n_abs(int argc, Value *argv) {
    Value v = arg(argc, argv, 0);
    if (v.tag == T_INT) {
        return v_int(v.as.i < 0 ? -v.as.i : v.as.i);
    }
    return v_float(fabs(v_as_double(v)));
}

static Value n_min(int argc, Value *argv) {
    Value best = arg(argc, argv, 0);
    for (int i = 1; i < argc; i++) {
        if (tern_compare(argv[i], best).as.i < 0) {
            best = argv[i];
        }
    }
    return best;
}

static Value n_max(int argc, Value *argv) {
    Value best = arg(argc, argv, 0);
    for (int i = 1; i < argc; i++) {
        if (tern_compare(argv[i], best).as.i > 0) {
            best = argv[i];
        }
    }
    return best;
}

static Value n_copy(int argc, Value *argv) {
    Value v = arg(argc, argv, 0);
    if (v_is_list(v)) {
        List *source = (List *)v.as.o;
        List *copy = list_new(source->count);
        for (int32_t i = 0; i < source->count; i++) {
            list_push(copy, source->items[i]);
        }
        return v_obj((Obj *)copy);
    }
    if (v_is_map(v)) {
        Map *source = (Map *)v.as.o;
        Map *copy = map_new();
        for (int32_t i = 0; i < source->cap; i++) {
            if (source->entries[i].used && !source->entries[i].dead) {
                map_set(copy, source->entries[i].key, source->entries[i].value);
            }
        }
        return v_obj((Obj *)copy);
    }
    return v;
}

static Value n_sleep(int argc, Value *argv) {
    (void)argc;
    (void)argv;
    return v_nil();
}

static Value n_input(int argc, Value *argv) {
    n_print(argc, argv);
    fflush(stdout);
    char buffer[4096];
    if (!fgets(buffer, sizeof buffer, stdin)) {
        return v_nil();
    }
    size_t len = strlen(buffer);
    while (len && (buffer[len - 1] == '\n' || buffer[len - 1] == '\r')) {
        buffer[--len] = '\0';
    }
    return str_value(buffer, len);
}

// --- math / time namespaces ------------------------------------------------

static Value m_floor(int argc, Value *argv) { return v_float(floor(v_as_double(arg(argc, argv, 0)))); }
static Value m_ceil(int argc, Value *argv) { return v_float(ceil(v_as_double(arg(argc, argv, 0)))); }
static Value m_round(int argc, Value *argv) { return v_float(round(v_as_double(arg(argc, argv, 0)))); }
static Value m_sqrt(int argc, Value *argv) { return v_float(sqrt(v_as_double(arg(argc, argv, 0)))); }
static Value m_trunc(int argc, Value *argv) { return v_float(trunc(v_as_double(arg(argc, argv, 0)))); }
static Value m_pow(int argc, Value *argv) {
    return v_float(pow(v_as_double(arg(argc, argv, 0)), v_as_double(arg(argc, argv, 1))));
}
static Value m_abs(int argc, Value *argv) { return n_abs(argc, argv); }
static Value m_min(int argc, Value *argv) { return n_min(argc, argv); }
static Value m_max(int argc, Value *argv) { return n_max(argc, argv); }
static Value m_sign(int argc, Value *argv) {
    double v = v_as_double(arg(argc, argv, 0));
    return v_int(v < 0 ? -1 : (v > 0 ? 1 : 0));
}
static Value m_clamp(int argc, Value *argv) {
    Value value = arg(argc, argv, 0);
    double low = v_as_double(arg(argc, argv, 1));
    double high = v_as_double(arg(argc, argv, 2));
    double clamped = v_as_double(value) < low ? low
                    : (v_as_double(value) > high ? high : v_as_double(value));
    if (value.tag == T_INT) return v_int((int64_t)clamped);
    return v_float(clamped);
}
static Value m_isinteger(int argc, Value *argv) {
    Value v = arg(argc, argv, 0);
    if (v.tag == T_INT) {
        return v_bool(true);
    }
    return v_bool(v.tag == T_FLOAT && floor(v.as.f) == v.as.f);
}
static Value m_isnan(int argc, Value *argv) {
    Value v = arg(argc, argv, 0);
    return v_bool(v.tag == T_FLOAT && isnan(v.as.f));
}
static Value m_gcd(int argc, Value *argv) {
    int64_t a = iarg(argc, argv, 0, "gcd"), b = iarg(argc, argv, 1, "gcd");
    if (a < 0) a = -a;
    if (b < 0) b = -b;
    while (b) {
        int64_t t = a % b;
        a = b;
        b = t;
    }
    return v_int(a);
}

static double now_millis(void) {
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (double)ts.tv_sec * 1000.0 + (double)ts.tv_nsec / 1e6;
}

static Value t_millis(int argc, Value *argv) {
    (void)argc;
    (void)argv;
    return v_int((int64_t)now_millis());
}

// `time.iso(seconds)`: Unix seconds as an ISO 8601 UTC string, matching the
// interpreter's "yyyy-MM-ddTHH:mm:ssZ".
static Value t_iso(int argc, Value *argv) {
    Value seconds = arg(argc, argv, 0);
    long long millis = v_is_num(seconds)
        ? (long long)(v_as_double(seconds) * 1000.0)
        : (long long)seconds.as.i;
    time_t stamp = (time_t)(millis / 1000);
    struct tm broken;
    gmtime_r(&stamp, &broken);
    char buffer[32];
    strftime(buffer, sizeof buffer, "%Y-%m-%dT%H:%M:%SZ", &broken);
    return str_from_cstr(buffer);
}

static Value t_now(int argc, Value *argv) {
    (void)argc;
    (void)argv;
    return v_float(now_millis());
}

// --- string methods --------------------------------------------------------

// Receiver accessors. The method tables are only reached for the matching type,
// but namespace functions such as `text.upper` call these directly, so each one
// checks its receiver: a wrong type must raise, never dereference a non-object.
static Str *self_str(int argc, Value *argv) {
    (void)argc;
    if (!v_is_str(argv[0])) {
        tern_panic("TypeError", "expected a string, not %s", tern_type_name(argv[0]));
    }
    return (Str *)argv[0].as.o;
}

static Value s_upper(int argc, Value *argv) {
    Str *s = self_str(argc, argv);
    Str *out = (Str *)tern_alloc(sizeof(Str) + s->len + 1, O_STR);
    for (uint32_t i = 0; i < s->len; i++) {
        char c = s->chars[i];
        out->chars[i] = (c >= 'a' && c <= 'z') ? (char)(c - 32) : c;
    }
    out->len = s->len;
    out->chars[s->len] = '\0';
    out->hash = hash_bytes(out->chars, out->len);
    return v_obj((Obj *)out);
}

static Value s_lower(int argc, Value *argv) {
    Str *s = self_str(argc, argv);
    Str *out = (Str *)tern_alloc(sizeof(Str) + s->len + 1, O_STR);
    for (uint32_t i = 0; i < s->len; i++) {
        char c = s->chars[i];
        out->chars[i] = (c >= 'A' && c <= 'Z') ? (char)(c + 32) : c;
    }
    out->len = s->len;
    out->chars[s->len] = '\0';
    out->hash = hash_bytes(out->chars, out->len);
    return v_obj((Obj *)out);
}

static bool is_space(char c) { return c == ' ' || c == '\t' || c == '\n' || c == '\r'; }

static Value s_trim(int argc, Value *argv) {
    Str *s = self_str(argc, argv);
    uint32_t from = 0, to = s->len;
    while (from < to && is_space(s->chars[from])) from++;
    while (to > from && is_space(s->chars[to - 1])) to--;
    return str_value(s->chars + from, to - from);
}

static Value s_trimstart(int argc, Value *argv) {
    Str *s = self_str(argc, argv);
    uint32_t from = 0;
    while (from < s->len && is_space(s->chars[from])) from++;
    return str_value(s->chars + from, s->len - from);
}

static Value s_trimend(int argc, Value *argv) {
    Str *s = self_str(argc, argv);
    uint32_t to = s->len;
    while (to > 0 && is_space(s->chars[to - 1])) to--;
    return str_value(s->chars, to);
}

static Value s_contains(int argc, Value *argv) {
    Str *s = self_str(argc, argv);
    Str *needle = as_str(arg(argc, argv, 1), "contains");
    if (needle->len == 0) {
        return v_bool(true);
    }
    if (needle->len > s->len) {
        return v_bool(false);
    }
    return v_bool(memmem(s->chars, s->len, needle->chars, needle->len) != NULL);
}

static Value s_starts(int argc, Value *argv) {
    Str *s = self_str(argc, argv);
    Str *prefix = as_str(arg(argc, argv, 1), "startsWith");
    return v_bool(prefix->len <= s->len && memcmp(s->chars, prefix->chars, prefix->len) == 0);
}

static Value s_ends(int argc, Value *argv) {
    Str *s = self_str(argc, argv);
    Str *suffix = as_str(arg(argc, argv, 1), "endsWith");
    return v_bool(suffix->len <= s->len &&
                  memcmp(s->chars + s->len - suffix->len, suffix->chars, suffix->len) == 0);
}

// `count(needle)`: how many non-overlapping occurrences.
static Value s_count(int argc, Value *argv) {
    Str *s = self_str(argc, argv);
    Str *needle = as_str(arg(argc, argv, 1), "count");
    if (needle->len == 0 || needle->len > s->len) {
        return v_int(0);
    }
    int64_t total = 0;
    uint32_t at = 0;
    while (at + needle->len <= s->len) {
        char *found = memmem(s->chars + at, s->len - at, needle->chars, needle->len);
        if (!found) {
            break;
        }
        total++;
        at = (uint32_t)(found - s->chars) + needle->len;
    }
    return v_int(total);
}

static Value s_indexof(int argc, Value *argv) {
    Str *s = self_str(argc, argv);
    Str *needle = as_str(arg(argc, argv, 1), "indexOf");
    char *found = memmem(s->chars, s->len, needle->chars, needle->len);
    return v_int(found ? (int64_t)(found - s->chars) : -1);
}

static Value s_lastindexof(int argc, Value *argv) {
    Str *s = self_str(argc, argv);
    Str *needle = as_str(arg(argc, argv, 1), "lastIndexOf");
    if (needle->len > s->len) {
        return v_int(-1);
    }
    for (int64_t at = (int64_t)s->len - (int64_t)needle->len; at >= 0; at--) {
        if (memcmp(s->chars + at, needle->chars, needle->len) == 0) {
            return v_int(at);
        }
    }
    return v_int(-1);
}

static Value s_substring(int argc, Value *argv) {
    Str *s = self_str(argc, argv);
    int64_t from = iarg(argc, argv, 1, "substring");
    int64_t to = argc > 2 && argv[2].tag != T_NIL ? iarg(argc, argv, 2, "substring") : (int64_t)s->len;
    if (from < 0) from += s->len;
    if (to < 0) to += s->len;
    if (from < 0) from = 0;
    if (to > (int64_t)s->len) to = s->len;
    if (to < from) to = from;
    return str_value(s->chars + from, (size_t)(to - from));
}

static Value s_slice(int argc, Value *argv) {
    Str *s = self_str(argc, argv);
    int64_t from = iarg(argc, argv, 1, "slice");
    int64_t to = argc > 2 && argv[2].tag != T_NIL ? iarg(argc, argv, 2, "slice") : (int64_t)s->len;
    if (from < 0) from += s->len;
    if (to < 0) to += s->len;
    if (from < 0) from = 0;
    if (to > (int64_t)s->len) to = s->len;
    if (to < from) to = from;
    return str_value(s->chars + from, (size_t)(to - from));
}

static Value s_split(int argc, Value *argv) {
    Str *s = self_str(argc, argv);
    List *parts = list_new(8);
    if (argc < 2 || argv[1].tag == T_NIL) {
        uint32_t start = 0;
        for (uint32_t i = 0; i <= s->len; i++) {
            if (i == s->len || is_space(s->chars[i])) {
                if (i > start) {
                    list_push(parts, str_value(s->chars + start, i - start));
                }
                start = i + 1;
            }
        }
        return v_obj((Obj *)parts);
    }

    Str *separator = as_str(argv[1], "split");
    if (separator->len == 0) {
        for (uint32_t i = 0; i < s->len; i++) {
            list_push(parts, str_value(s->chars + i, 1));
        }
        return v_obj((Obj *)parts);
    }

    uint32_t start = 0;
    while (start <= s->len) {
        char *found = memmem(s->chars + start, s->len - start, separator->chars, separator->len);
        if (!found) {
            list_push(parts, str_value(s->chars + start, s->len - start));
            break;
        }
        size_t at = (size_t)(found - s->chars);
        list_push(parts, str_value(s->chars + start, at - start));
        start = (uint32_t)(at + separator->len);
    }
    return v_obj((Obj *)parts);
}

static Value s_replace(int argc, Value *argv) {
    Str *s = self_str(argc, argv);
    Str *needle = as_str(arg(argc, argv, 1), "replace");
    Str *replacement = as_str(arg(argc, argv, 2), "replace");
    if (needle->len == 0) {
        return v_obj((Obj *)s);
    }

    Buf buf;
    buf_init(&buf);
    uint32_t start = 0;
    while (start <= s->len) {
        char *found = memmem(s->chars + start, s->len - start, needle->chars, needle->len);
        if (!found) {
            buf_add(&buf, s->chars + start, s->len - start);
            break;
        }
        size_t at = (size_t)(found - s->chars);
        buf_add(&buf, s->chars + start, at - start);
        buf_add(&buf, replacement->chars, replacement->len);
        start = (uint32_t)(at + needle->len);
    }
    return buf_finish(&buf);
}

// Same as `replace` but stops after the first occurrence.
static Value s_replacefirst(int argc, Value *argv) {
    Str *s = self_str(argc, argv);
    Str *needle = as_str(arg(argc, argv, 1), "replaceFirst");
    Str *replacement = as_str(arg(argc, argv, 2), "replaceFirst");
    if (needle->len == 0) {
        return v_obj((Obj *)s);
    }
    char *found = memmem(s->chars, s->len, needle->chars, needle->len);
    if (!found) {
        return v_obj((Obj *)s);
    }

    size_t at = (size_t)(found - s->chars);
    Buf buf;
    buf_init(&buf);
    buf_add(&buf, s->chars, at);
    buf_add(&buf, replacement->chars, replacement->len);
    buf_add(&buf, s->chars + at + needle->len, s->len - at - needle->len);
    return buf_finish(&buf);
}

static Value s_repeat(int argc, Value *argv) {
    Str *s = self_str(argc, argv);
    int64_t times = iarg(argc, argv, 1, "repeat");
    if (times <= 0) {
        return str_value("", 0);
    }
    size_t total = (size_t)times * s->len;
    Str *out = (Str *)tern_alloc(sizeof(Str) + total + 1, O_STR);
    for (int64_t i = 0; i < times; i++) {
        memcpy(out->chars + i * s->len, s->chars, s->len);
    }
    out->len = (uint32_t)total;
    out->chars[total] = '\0';
    out->hash = hash_bytes(out->chars, out->len);
    return v_obj((Obj *)out);
}

static Value s_chars(int argc, Value *argv) {
    Str *s = self_str(argc, argv);
    List *chars = list_new((int)s->len);
    for (uint32_t i = 0; i < s->len; i++) {
        list_push(chars, str_value(s->chars + i, 1));
    }
    return v_obj((Obj *)chars);
}

static Value s_lines(int argc, Value *argv) {
    Str *s = self_str(argc, argv);
    List *lines = list_new(8);
    uint32_t start = 0;
    for (uint32_t i = 0; i <= s->len; i++) {
        if (i == s->len || s->chars[i] == '\n') {
            uint32_t end = i;
            if (end > start && s->chars[end - 1] == '\r') {
                end--;
            }
            list_push(lines, str_value(s->chars + start, end - start));
            start = i + 1;
        }
    }
    return v_obj((Obj *)lines);
}

static Value s_isempty(int argc, Value *argv) { return v_bool(self_str(argc, argv)->len == 0); }

static Value s_toint(int argc, Value *argv) {
    (void)argc;
    return v_int(strtoll(self_str(argc, argv)->chars, NULL, 10));
}

static Value s_tofloat(int argc, Value *argv) {
    (void)argc;
    return v_float(strtod(self_str(argc, argv)->chars, NULL));
}

static Value s_tostring(int argc, Value *argv) {
    (void)argc;
    return argv[0];
}

static Value s_pad(int argc, Value *argv, bool start) {
    Str *s = self_str(argc, argv);
    int64_t width = iarg(argc, argv, 1, "pad");
    char fill = ' ';
    if (argc > 2 && v_is_str(argv[2]) && ((Str *)argv[2].as.o)->len > 0) {
        fill = ((Str *)argv[2].as.o)->chars[0];
    }
    if (width <= (int64_t)s->len) {
        return argv[0];
    }
    size_t pad = (size_t)(width - s->len);
    Str *out = (Str *)tern_alloc(sizeof(Str) + (size_t)width + 1, O_STR);
    size_t position = 0;
    if (start) {
        memset(out->chars, fill, pad);
        position = pad;
    }
    memcpy(out->chars + position, s->chars, s->len);
    position += s->len;
    if (!start) {
        memset(out->chars + position, fill, pad);
        position += pad;
    }
    out->len = (uint32_t)position;
    out->chars[position] = '\0';
    out->hash = hash_bytes(out->chars, out->len);
    return v_obj((Obj *)out);
}

static Value s_padstart(int argc, Value *argv) { return s_pad(argc, argv, true); }
static Value s_padend(int argc, Value *argv) { return s_pad(argc, argv, false); }

static Value s_reversed(int argc, Value *argv) {
    Str *s = self_str(argc, argv);
    Str *out = (Str *)tern_alloc(sizeof(Str) + s->len + 1, O_STR);
    for (uint32_t i = 0; i < s->len; i++) {
        out->chars[i] = s->chars[s->len - 1 - i];
    }
    out->len = s->len;
    out->chars[s->len] = '\0';
    out->hash = hash_bytes(out->chars, out->len);
    return v_obj((Obj *)out);
}

// --- list methods ----------------------------------------------------------

static List *self_list(int argc, Value *argv) {
    (void)argc;
    if (!v_is_list(argv[0])) {
        tern_panic("TypeError", "expected a list, not %s", tern_type_name(argv[0]));
    }
    return (List *)argv[0].as.o;
}

static Value l_push(int argc, Value *argv) {
    list_push(self_list(argc, argv), arg(argc, argv, 1));
    return argv[0];
}

static Value l_pop(int argc, Value *argv) {
    List *list = self_list(argc, argv);
    if (list->count == 0) {
        return v_nil();
    }
    return list->items[--list->count];
}

static Value l_shift(int argc, Value *argv) {
    List *list = self_list(argc, argv);
    if (list->count == 0) {
        return v_nil();
    }
    Value first = list->items[0];
    memmove(list->items, list->items + 1, (size_t)(list->count - 1) * sizeof(Value));
    list->count--;
    return first;
}

static Value l_unshift(int argc, Value *argv) {
    List *list = self_list(argc, argv);
    list_reserve(list, list->count + 1);
    memmove(list->items + 1, list->items, (size_t)list->count * sizeof(Value));
    list->items[0] = arg(argc, argv, 1);
    list->count++;
    return argv[0];
}

static Value l_join(int argc, Value *argv) {
    List *list = self_list(argc, argv);
    const char *separator = ", ";
    size_t separator_len = 2;
    if (argc > 1 && v_is_str(argv[1])) {
        separator = ((Str *)argv[1].as.o)->chars;
        separator_len = ((Str *)argv[1].as.o)->len;
    }

    Buf buf;
    buf_init(&buf);
    for (int32_t i = 0; i < list->count; i++) {
        if (i) {
            buf_add(&buf, separator, separator_len);
        }
        Value text = tern_to_string(list->items[i]);
        Str *s = (Str *)text.as.o;
        buf_add(&buf, s->chars, s->len);
    }
    return buf_finish(&buf);
}

static Value l_contains(int argc, Value *argv) {
    return v_bool(tern_contains(argv[0], arg(argc, argv, 1)));
}

static Value l_indexof(int argc, Value *argv) {
    List *list = self_list(argc, argv);
    Value needle = arg(argc, argv, 1);
    for (int32_t i = 0; i < list->count; i++) {
        if (tern_equal(list->items[i], needle)) {
            return v_int(i);
        }
    }
    return v_int(-1);
}

static Value l_slice(int argc, Value *argv) {
    return tern_slice(argv[0], arg(argc, argv, 1), arg(argc, argv, 2), false);
}

static Value l_reverse(int argc, Value *argv) {
    List *list = self_list(argc, argv);
    for (int32_t i = 0, j = list->count - 1; i < j; i++, j--) {
        Value temp = list->items[i];
        list->items[i] = list->items[j];
        list->items[j] = temp;
    }
    return argv[0];
}

static int sort_cmp(const void *a, const void *b) {
    Value x = *(const Value *)a;
    Value y = *(const Value *)b;
    return (int)tern_compare(x, y).as.i;
}

static Value l_sort(int argc, Value *argv) {
    List *list = self_list(argc, argv);
    if (argc > 1 && argv[1].tag != T_NIL) {
        // Comparator-driven sort (insertion sort: simple and stable).
        Value comparator = argv[1];
        for (int32_t i = 1; i < list->count; i++) {
            Value key = list->items[i];
            int32_t j = i - 1;
            while (j >= 0) {
                Value pair[2] = {key, list->items[j]};
                Value result = tern_call(comparator, 2, pair);
                if (result.as.i >= 0) {
                    break;
                }
                list->items[j + 1] = list->items[j];
                j--;
            }
            list->items[j + 1] = key;
        }
        return argv[0];
    }
    qsort(list->items, (size_t)list->count, sizeof(Value), sort_cmp);
    return argv[0];
}

static Value l_first(int argc, Value *argv) {
    List *list = self_list(argc, argv);
    return list->count ? list->items[0] : v_nil();
}

static Value l_last(int argc, Value *argv) {
    List *list = self_list(argc, argv);
    return list->count ? list->items[list->count - 1] : v_nil();
}

static Value l_isempty(int argc, Value *argv) { return v_bool(self_list(argc, argv)->count == 0); }

static Value l_sum(int argc, Value *argv) {
    List *list = self_list(argc, argv);
    Value total = v_int(0);
    for (int32_t i = 0; i < list->count; i++) {
        total = tern_add(total, list->items[i]);
    }
    return total;
}

static Value l_min(int argc, Value *argv) {
    List *list = self_list(argc, argv);
    if (!list->count) {
        return v_nil();
    }
    Value best = list->items[0];
    for (int32_t i = 1; i < list->count; i++) {
        if (tern_compare(list->items[i], best).as.i < 0) {
            best = list->items[i];
        }
    }
    return best;
}

static Value l_max(int argc, Value *argv) {
    List *list = self_list(argc, argv);
    if (!list->count) {
        return v_nil();
    }
    Value best = list->items[0];
    for (int32_t i = 1; i < list->count; i++) {
        if (tern_compare(list->items[i], best).as.i > 0) {
            best = list->items[i];
        }
    }
    return best;
}

static Value l_clear(int argc, Value *argv) {
    self_list(argc, argv)->count = 0;
    return argv[0];
}

static Value l_clone(int argc, Value *argv) {
    List *list = self_list(argc, argv);
    List *copy = list_new(list->count);
    for (int32_t i = 0; i < list->count; i++) {
        list_push(copy, list->items[i]);
    }
    return v_obj((Obj *)copy);
}

static Value l_concat(int argc, Value *argv) {
    return tern_add(argv[0], arg(argc, argv, 1));
}

static Value l_insert(int argc, Value *argv) {
    List *list = self_list(argc, argv);
    int64_t at = iarg(argc, argv, 1, "insert");
    if (at < 0) at = 0;
    if (at > list->count) at = list->count;
    list_reserve(list, list->count + 1);
    memmove(list->items + at + 1, list->items + at, (size_t)(list->count - at) * sizeof(Value));
    list->items[at] = arg(argc, argv, 2);
    list->count++;
    return argv[0];
}

static Value l_remove(int argc, Value *argv) {
    List *list = self_list(argc, argv);
    int64_t at = iarg(argc, argv, 1, "remove");
    if (at < 0) at += list->count;
    if (at < 0 || at >= list->count) {
        return v_nil();
    }
    Value removed = list->items[at];
    memmove(list->items + at, list->items + at + 1, (size_t)(list->count - at - 1) * sizeof(Value));
    list->count--;
    return removed;
}

static Value l_map(int argc, Value *argv) {
    List *list = self_list(argc, argv);
    Value fn = arg(argc, argv, 1);
    List *out = list_new(list->count);
    for (int32_t i = 0; i < list->count; i++) {
        Value item = list->items[i];
        list_push(out, tern_call(fn, 1, &item));
    }
    return v_obj((Obj *)out);
}

static Value l_filter(int argc, Value *argv) {
    List *list = self_list(argc, argv);
    Value fn = arg(argc, argv, 1);
    List *out = list_new(8);
    for (int32_t i = 0; i < list->count; i++) {
        Value item = list->items[i];
        if (v_truthy(tern_call(fn, 1, &item))) {
            list_push(out, item);
        }
    }
    return v_obj((Obj *)out);
}

static Value l_each(int argc, Value *argv) {
    List *list = self_list(argc, argv);
    Value fn = arg(argc, argv, 1);
    for (int32_t i = 0; i < list->count; i++) {
        Value item = list->items[i];
        tern_call(fn, 1, &item);
    }
    return argv[0];
}

static Value l_reduce(int argc, Value *argv) {
    List *list = self_list(argc, argv);
    Value fn = arg(argc, argv, 1);
    Value accumulator = arg(argc, argv, 2);
    int32_t start = 0;
    if (argc < 3) {
        if (list->count == 0) {
            return v_nil();
        }
        accumulator = list->items[0];
        start = 1;
    }
    for (int32_t i = start; i < list->count; i++) {
        Value pair[2] = {accumulator, list->items[i]};
        accumulator = tern_call(fn, 2, pair);
    }
    return accumulator;
}

static Value l_tostring(int argc, Value *argv) {
    (void)argc;
    return v_obj((Obj *)tern_to_debug(argv[0]));
}

// --- map methods -----------------------------------------------------------

static Map *self_map(int argc, Value *argv) {
    (void)argc;
    if (!v_is_map(argv[0])) {
        tern_panic("TypeError", "expected a map, not %s", tern_type_name(argv[0]));
    }
    return (Map *)argv[0].as.o;
}

static Value m_keys(int argc, Value *argv) { return n_keys(1, argv); }
static Value m_values(int argc, Value *argv) { return n_values(1, argv); }
static Value m_has(int argc, Value *argv) { return v_bool(map_has(self_map(argc, argv), arg(argc, argv, 1))); }
static Value m_get(int argc, Value *argv) {
    Value found;
    if (map_get(self_map(argc, argv), arg(argc, argv, 1), &found)) {
        return found;
    }
    return arg(argc, argv, 2);
}
static Value m_set(int argc, Value *argv) {
    map_set(self_map(argc, argv), arg(argc, argv, 1), arg(argc, argv, 2));
    return argv[0];
}
// `map.remove(key)` reports whether the key was there, like the interpreter.
static Value m_remove(int argc, Value *argv) {
    return v_bool(map_remove(self_map(argc, argv), arg(argc, argv, 1)));
}
static Value m_isempty(int argc, Value *argv) { return v_bool(self_map(argc, argv)->count == 0); }
static Value m_clear(int argc, Value *argv) {
    Map *map = self_map(argc, argv);
    memset(map->entries, 0, (size_t)map->cap * sizeof(MapEntry));
    map->order_count = 0;
    map->count = 0;
    map->used = 0;
    return argv[0];
}
static Value m_clone(int argc, Value *argv) { return n_copy(1, argv); }

static Value m_entries(int argc, Value *argv) {
    Map *map = self_map(argc, argv);
    List *out = list_new(map->count);
    for (int32_t i = 0; i < map->order_count; i++) {
        Value value;
        map_get(map, map->order[i], &value);
        List *pair = list_new(2);
        list_push(pair, map->order[i]);
        list_push(pair, value);
        list_push(out, v_obj((Obj *)pair));
    }
    return v_obj((Obj *)out);
}

// --- dispatch --------------------------------------------------------------

typedef struct MethodSlot {
    const char *name;
    NativeFn fn;
} MethodSlot;


// --- number methods --------------------------------------------------------

static Value num_abs(Value v) {
    if (v.tag == T_INT) return v.as.i < 0 ? v_int(-v.as.i) : v;
    return v_float(fabs(v.as.f));
}

static Value n_abs_self(int argc, Value *argv) { (void)argc; return num_abs(argv[0]); }

static Value n_clamp_self(int argc, Value *argv) {
    Value v = argv[0];
    double low = v_as_double(arg(argc, argv, 1));
    double high = v_as_double(arg(argc, argv, 2));
    double value = v_as_double(v);
    if (value < low) value = low;
    if (value > high) value = high;
    if (v.tag == T_INT && arg(argc, argv, 1).tag == T_INT && arg(argc, argv, 2).tag == T_INT) {
        return v_int((int64_t)value);
    }
    return v_float(value);
}

static Value n_round_self(int argc, Value *argv) {
    Value v = argv[0];
    int digits = argc > 1 ? (int)require_int(argv[1], "round") : 0;
    double value = v_as_double(v);
    double scale = pow(10.0, digits);
    double rounded = round(value * scale) / scale;
    if (digits == 0) return v_int((int64_t)rounded);
    return v_float(rounded);
}

static Value n_toint_self(int argc, Value *argv) {
    (void)argc;
    Value v = argv[0];
    return v_int(v.tag == T_INT ? v.as.i : (int64_t)v.as.f);
}

static Value n_tofloat_self(int argc, Value *argv) {
    (void)argc;
    return v_float(v_as_double(argv[0]));
}

static Value n_tostring_self(int argc, Value *argv) {
    (void)argc;
    return tern_to_string(argv[0]);
}

static Value n_times_self(int argc, Value *argv) {
    int64_t count = require_int(argv[0], "times");
    Value callback = arg(argc, argv, 1);
    Value item;
    for (int64_t i = 0; i < count; i++) {
        item = v_int(i);
        tern_call(callback, 1, &item);
    }
    (void)item;
    return argv[0];
}

// --- range methods ---------------------------------------------------------

static Value r_tolist(int argc, Value *argv) {
    (void)argc;
    Range *range = (Range *)argv[0].as.o;
    int64_t end = range->inclusive ? range->end + 1 : range->end;
    List *list = list_new((int)(end > range->start ? end - range->start : 0));
    for (int64_t i = range->start; i < end; i++) list_push(list, v_int(i));
    return v_obj((Obj *)list);
}

static Value r_contains(int argc, Value *argv) {
    Value needle = arg(argc, argv, 1);
    if (needle.tag != T_INT) return v_bool(false);
    Range *range = (Range *)argv[0].as.o;
    int64_t high = range->inclusive ? range->end : range->end - 1;
    return v_bool(needle.as.i >= range->start && needle.as.i <= high);
}

static Value r_length(int argc, Value *argv) {
    (void)argc;
    return v_int(tern_length(argv[0]));
}

static Value r_tostring(int argc, Value *argv) {
    (void)argc;
    return tern_to_string(argv[0]);
}

static Value r_map(int argc, Value *argv) {
    Value list = r_tolist(0, argv);
    List *items = (List *)list.as.o;
    List *out = list_new(items->count);
    Value fn = arg(argc, argv, 1);
    for (int32_t i = 0; i < items->count; i++) {
        Value item = items->items[i];
        list_push(out, tern_call(fn, 1, &item));
    }
    return v_obj((Obj *)out);
}

static Value r_each(int argc, Value *argv) {
    Value list = r_tolist(0, argv);
    List *items = (List *)list.as.o;
    Value fn = arg(argc, argv, 1);
    for (int32_t i = 0; i < items->count; i++) {
        Value item = items->items[i];
        tern_call(fn, 1, &item);
    }
    return argv[0];
}


// `"{0} and {}"` style formatting, shared with the Text namespace.
static Value format_template(Value template_value, int argc, Value *argv) {
    Str *template = (Str *)template_value.as.o;
    const char *text = template->chars;
    int length = (int)template->len;

    size_t cap = (size_t)length + 32, used = 0;
    char *buffer = (char *)malloc(cap);
    int argument = 0;   // index into argv (template already consumed)
    int index = 0;

    while (index < length) {
        char c = text[index];
        if (c == '{') {
            if (index + 1 < length && text[index + 1] == '{') {
                if (used + 1 >= cap) { cap *= 2; buffer = realloc(buffer, cap); }
                buffer[used++] = '{';
                index += 2;
                continue;
            }

            int close = index + 1;
            while (close < length && text[close] != '}') close++;
            if (close >= length) {
                free(buffer);
                tern_panic("FormatError", "unclosed '{' in format string");
            }

            int begin = index + 1, end = close;
            while (begin < end && (text[begin] == ' ' || text[begin] == '\t')) begin++;
            while (end > begin && (text[end - 1] == ' ' || text[end - 1] == '\t')) end--;

            Value value;
            if (begin == end) {
                if (argument >= argc) {
                    free(buffer);
                    tern_panic("FormatError", "not enough arguments for format string");
                }
                value = argv[argument++];
            } else {
                int position = 0;
                bool numeric = true;
                for (int i = begin; i < end; i++) {
                    if (text[i] < '0' || text[i] > '9') { numeric = false; break; }
                    position = position * 10 + (text[i] - '0');
                }
                if (!numeric) {
                    free(buffer);
                    tern_panic("FormatError", "unsupported format specifier");
                }
                if (position >= argc) {
                    free(buffer);
                    tern_panic("FormatError", "format argument {%d} is missing", position);
                }
                value = argv[position];
                // Explicit placeholders also advance the implicit cursor.
                if (argument < position + 1) argument = position + 1;
            }

            Value rendered = tern_to_string(value);
            Str *piece = (Str *)rendered.as.o;
            while (used + piece->len + 1 >= cap) cap *= 2;
            buffer = realloc(buffer, cap);
            memcpy(buffer + used, piece->chars, piece->len);
            used += piece->len;
            index = close + 1;
            continue;
        }

        if (c == '}' && index + 1 < length && text[index + 1] == '}') {
            if (used + 1 >= cap) { cap *= 2; buffer = realloc(buffer, cap); }
            buffer[used++] = '}';
            index += 2;
            continue;
        }

        if (used + 1 >= cap) { cap *= 2; buffer = realloc(buffer, cap); }
        buffer[used++] = c;
        index++;
    }

    Str *result = str_from(buffer, used);
    free(buffer);
    return v_obj((Obj *)result);
}

static Value s_format(int argc, Value *argv) {
    return format_template(argv[0], argc - 1, argv + 1);
}




// --- extra list methods ----------------------------------------------------

static List *require_list(Value v, const char *what) {
    if (!v_is_list(v)) {
        tern_panic("TypeError", "%s expects a list, not %s", what, tern_type_name(v));
    }
    return (List *)v.as.o;
}

static void flatten_into(List *out, List *source, int depth) {
    for (int32_t i = 0; i < source->count; i++) {
        Value item = source->items[i];
        if (v_is_list(item) && depth > 0) {
            flatten_into(out, (List *)item.as.o, depth - 1);
        } else {
            list_push(out, item);
        }
    }
}

static Value l_flatten(int argc, Value *argv) {
    List *list = require_list(argv[0], "flatten");
    int depth = argc > 1 ? (int)require_int(argv[1], "flatten") : 1;
    List *out = list_new(list->count);
    flatten_into(out, list, depth);
    return v_obj((Obj *)out);
}

static Value l_unique(int argc, Value *argv) {
    (void)argc;
    List *list = require_list(argv[0], "unique");
    List *out = list_new(list->count);
    for (int32_t i = 0; i < list->count; i++) {
        bool seen = false;
        for (int32_t j = 0; j < out->count; j++) {
            if (tern_equal(out->items[j], list->items[i])) { seen = true; break; }
        }
        if (!seen) list_push(out, list->items[i]);
    }
    return v_obj((Obj *)out);
}

static Value l_take(int argc, Value *argv) {
    List *list = require_list(argv[0], "take");
    int64_t count = require_int(arg(argc, argv, 1), "take");
    if (count < 0) count = 0;
    if (count > list->count) count = list->count;
    List *out = list_new((int)count);
    for (int64_t i = 0; i < count; i++) list_push(out, list->items[i]);
    return v_obj((Obj *)out);
}

static Value l_drop(int argc, Value *argv) {
    List *list = require_list(argv[0], "drop");
    int64_t count = require_int(arg(argc, argv, 1), "drop");
    if (count < 0) count = 0;
    List *out = list_new(list->count);
    for (int64_t i = count; i < list->count; i++) list_push(out, list->items[i]);
    return v_obj((Obj *)out);
}

static Value l_all(int argc, Value *argv) {
    List *list = require_list(argv[0], "all");
    Value fn = arg(argc, argv, 1);
    for (int32_t i = 0; i < list->count; i++) {
        Value item = list->items[i];
        if (!v_truthy(tern_call(fn, 1, &item))) return v_bool(false);
    }
    return v_bool(true);
}

static Value l_any(int argc, Value *argv) {
    List *list = require_list(argv[0], "any");
    Value fn = arg(argc, argv, 1);
    for (int32_t i = 0; i < list->count; i++) {
        Value item = list->items[i];
        if (v_truthy(tern_call(fn, 1, &item))) return v_bool(true);
    }
    return v_bool(false);
}

static Value l_find(int argc, Value *argv) {
    List *list = require_list(argv[0], "find");
    Value fn = arg(argc, argv, 1);
    for (int32_t i = 0; i < list->count; i++) {
        Value item = list->items[i];
        if (v_truthy(tern_call(fn, 1, &item))) return item;
    }
    return v_nil();
}

static Value l_findindex(int argc, Value *argv) {
    List *list = require_list(argv[0], "findIndex");
    Value fn = arg(argc, argv, 1);
    for (int32_t i = 0; i < list->count; i++) {
        Value item = list->items[i];
        if (v_truthy(tern_call(fn, 1, &item))) return v_int(i);
    }
    return v_int(-1);
}

static Value l_removevalue(int argc, Value *argv) {
    List *list = require_list(argv[0], "removeValue");
    Value needle = arg(argc, argv, 1);
    for (int32_t i = 0; i < list->count; i++) {
        if (tern_equal(list->items[i], needle)) {
            Value removed = list->items[i];
            memmove(&list->items[i], &list->items[i + 1],
                    (size_t)(list->count - i - 1) * sizeof(Value));
            list->count--;
            return removed;
        }
    }
    return v_nil();
}

static Value m_tostring_self(int argc, Value *argv) {
    (void)argc;
    return tern_to_string(argv[0]);
}

static Value m_merge(int argc, Value *argv) {
    Map *map = (Map *)argv[0].as.o;
    Map *other = (Map *)arg(argc, argv, 1).as.o;
    Map *out = map_new();
    for (int32_t i = 0; i < map->cap; i++) {
        MapEntry *entry = &map->entries[i];
        if (entry->used && !entry->dead) map_set(out, entry->key, entry->value);
    }
    for (int32_t i = 0; i < other->cap; i++) {
        MapEntry *entry = &other->entries[i];
        if (entry->used && !entry->dead) map_set(out, entry->key, entry->value);
    }
    return v_obj((Obj *)out);
}

// --- lists namespace -------------------------------------------------------

static Value ln_range(int argc, Value *argv) {
    int64_t start = 0, end;
    if (argc >= 2) {
        start = require_int(argv[0], "range");
        end = require_int(argv[1], "range");
    } else {
        end = require_int(arg(argc, argv, 0), "range");
    }
    List *out = list_new((int)(end > start ? end - start : 0));
    for (int64_t i = start; i < end; i++) list_push(out, v_int(i));
    return v_obj((Obj *)out);
}

static Value ln_zip(int argc, Value *argv) {
    int32_t shortest = -1;
    for (int i = 0; i < argc; i++) {
        List *list = require_list(argv[i], "zip");
        if (shortest < 0 || list->count < shortest) shortest = list->count;
    }
    List *out = list_new(shortest < 0 ? 0 : shortest);
    for (int32_t i = 0; i < shortest; i++) {
        List *tuple = list_new(argc);
        for (int j = 0; j < argc; j++) {
            list_push(tuple, ((List *)argv[j].as.o)->items[i]);
        }
        list_push(out, v_obj((Obj *)tuple));
    }
    return v_obj((Obj *)out);
}

static Value ln_enumerate(int argc, Value *argv) {
    List *list = require_list(arg(argc, argv, 0), "enumerate");
    List *out = list_new(list->count);
    for (int32_t i = 0; i < list->count; i++) {
        List *pair = list_new(2);
        list_push(pair, v_int(i));
        list_push(pair, list->items[i]);
        list_push(out, v_obj((Obj *)pair));
    }
    return v_obj((Obj *)out);
}

static Value ln_chunk(int argc, Value *argv) {
    List *list = require_list(arg(argc, argv, 0), "chunk");
    int64_t size = require_int(arg(argc, argv, 1), "chunk");
    if (size < 1) size = 1;
    List *out = list_new((int)((list->count + size - 1) / size));
    for (int32_t i = 0; i < list->count; i += (int32_t)size) {
        List *chunk = list_new((int)size);
        for (int32_t j = i; j < list->count && j < i + size; j++) {
            list_push(chunk, list->items[j]);
        }
        list_push(out, v_obj((Obj *)chunk));
    }
    return v_obj((Obj *)out);
}

static Value ln_join(int argc, Value *argv) {
    Value list = arg(argc, argv, 0);
    Value separator = arg(argc, argv, 1);
    Value args[2];
    args[0] = list;
    args[1] = v_is_nil(separator) ? str_from_cstr("") : separator;
    return l_join(2, args);
}

static Value ln_sort(int argc, Value *argv) { return l_sort(argc, argv); }

// `lists.repeat(value, count)`: the value repeated n times.
static Value ln_repeat(int argc, Value *argv) {
    int count = (int)require_int(arg(argc, argv, 1), "repeat");
    if (count < 0) count = 0;
    List *list = list_new(count);
    Value value = arg(argc, argv, 0);
    for (int i = 0; i < count; i++) {
        list_push(list, value);
    }
    return v_obj((Obj *)list);
}

// `text.pad(s, width[, fill])` appends fill characters on the right.
static Value tx_pad(int argc, Value *argv) {
    Str *text = (Str *)tern_to_string(argv[0]).as.o;
    int width = (int)require_int(arg(argc, argv, 1), "pad");
    Value fill_value = argc > 2 ? tern_to_string(argv[2]) : str_from_cstr(" ");
    Str *fill = (Str *)fill_value.as.o;
    if ((int)text->len >= width || fill->len == 0) {
        return v_obj((Obj *)text);
    }
    Buf buf;
    buf_init(&buf);
    buf_add(&buf, text->chars, text->len);
    while ((int)buf.len < width) {
        buf_add(&buf, fill->chars, fill->len);
    }
    /* Trim any overshoot so the result is exactly `width` wide. */
    if ((int)buf.len > width) {
        buf.len = (size_t)width;
    }
    return buf_finish(&buf);
}

// --- random ---------------------------------------------------------------

static uint64_t rng_state = 0x2545f4914f6cdd1dULL;

static uint64_t rng_next(void) {
    uint64_t x = rng_state;
    x ^= x >> 12;
    x ^= x << 25;
    x ^= x >> 27;
    rng_state = x;
    return x * 0x2545F4914F6CDD1DULL;
}

static Value rnd_seed(int argc, Value *argv) {
    rng_state = (uint64_t)require_int(arg(argc, argv, 0), "seed") * 2685821657736338717ULL + 1;
    return v_nil();
}

// `random.float([low, high])`: a value in [low, high), defaulting to [0, 1).
static Value rnd_float(int argc, Value *argv) {
    double low = argc > 0 ? v_as_double(arg(argc, argv, 0)) : 0.0;
    double high = argc > 1 ? v_as_double(argv[1]) : 1.0;
    double unit = (double)(rng_next() >> 11) / 9007199254740992.0;
    return v_float(low + unit * (high - low));
}

static Value rnd_int(int argc, Value *argv) {
    int64_t low = require_int(arg(argc, argv, 0), "int");
    int64_t high = argc > 1 ? require_int(argv[1], "int") : low;
    if (argc <= 1) { low = 0; }
    int64_t span = high - low;
    if (span <= 0) return v_int(low);
    return v_int(low + (int64_t)(rng_next() % (uint64_t)span));
}

static Value rnd_bool(int argc, Value *argv) {
    (void)argc; (void)argv;
    return v_bool((rng_next() & 1) != 0);
}

static Value rnd_choice(int argc, Value *argv) {
    List *list = require_list(arg(argc, argv, 0), "choice");
    if (list->count == 0) return v_nil();
    return list->items[rng_next() % (uint64_t)list->count];
}

static Value rnd_shuffle(int argc, Value *argv) {
    List *list = require_list(arg(argc, argv, 0), "shuffle");
    List *out = list_new(list->count);
    for (int32_t i = 0; i < list->count; i++) list_push(out, list->items[i]);
    for (int32_t i = out->count - 1; i > 0; i--) {
        int32_t j = (int32_t)(rng_next() % (uint64_t)(i + 1));
        Value tmp = out->items[i];
        out->items[i] = out->items[j];
        out->items[j] = tmp;
    }
    return v_obj((Obj *)out);
}

// --- math extras ----------------------------------------------------------

static double darg(int argc, Value *argv, int index) {
    return v_as_double(arg(argc, argv, index));
}

static Value m_lcm(int argc, Value *argv) {
    int64_t a = require_int(arg(argc, argv, 0), "lcm");
    int64_t b = require_int(arg(argc, argv, 1), "lcm");
    int64_t x = a < 0 ? -a : a, y = b < 0 ? -b : b;
    while (y) { int64_t t = x % y; x = y; y = t; }
    return v_int(x == 0 ? 0 : a / x * b);
}

static Value m_log(int argc, Value *argv) { return v_float(log(darg(argc, argv, 0))); }
static Value m_log2(int argc, Value *argv) { return v_float(log2(darg(argc, argv, 0))); }
static Value m_log10(int argc, Value *argv) { return v_float(log10(darg(argc, argv, 0))); }
static Value m_cbrt(int argc, Value *argv) { return v_float(cbrt(darg(argc, argv, 0))); }
static Value m_hypot(int argc, Value *argv) { return v_float(hypot(darg(argc, argv, 0), darg(argc, argv, 1))); }
static Value m_exp(int argc, Value *argv) { return v_float(exp(darg(argc, argv, 0))); }
static Value m_sin(int argc, Value *argv) { return v_float(sin(darg(argc, argv, 0))); }
static Value m_cos(int argc, Value *argv) { return v_float(cos(darg(argc, argv, 0))); }
static Value m_tan(int argc, Value *argv) { return v_float(tan(darg(argc, argv, 0))); }
static Value m_asin(int argc, Value *argv) { return v_float(asin(darg(argc, argv, 0))); }
static Value m_acos(int argc, Value *argv) { return v_float(acos(darg(argc, argv, 0))); }
static Value m_atan(int argc, Value *argv) { return v_float(atan(darg(argc, argv, 0))); }
static Value m_atan2(int argc, Value *argv) { return v_float(atan2(darg(argc, argv, 0), darg(argc, argv, 1))); }
static Value m_todegrees(int argc, Value *argv) { return v_float(darg(argc, argv, 0) * (180.0 / M_PI)); }
static Value m_toradians(int argc, Value *argv) { return v_float(darg(argc, argv, 0) * (M_PI / 180.0)); }

// --- json -----------------------------------------------------------------

static void json_escape(Buf *buf, Str *str) {
    buf_add_char(buf, '"');
    for (uint32_t i = 0; i < str->len; i++) {
        unsigned char c = (unsigned char)str->chars[i];
        switch (c) {
            case '"': buf_add_cstr(buf, "\\\""); break;
            case '\\': buf_add_cstr(buf, "\\\\"); break;
            case '\n': buf_add_cstr(buf, "\\n"); break;
            case '\r': buf_add_cstr(buf, "\\r"); break;
            case '\t': buf_add_cstr(buf, "\\t"); break;
            default:
                if (c < 0x20) {
                    char tmp[8];
                    snprintf(tmp, sizeof tmp, "\\u%04x", c);
                    buf_add_cstr(buf, tmp);
                } else {
                    buf_add_char(buf, (char)c);
                }
                break;
        }
    }
    buf_add_char(buf, '"');
}

static void json_write(Buf *buf, Value v, int indent, int level) {
    switch (v.tag) {
        case T_NIL:
        case T_MISSING:
            buf_add_cstr(buf, "null");
            return;
        case T_BOOL:
            buf_add_cstr(buf, v.as.i ? "true" : "false");
            return;
        case T_INT: {
            char tmp[32];
            int n = snprintf(tmp, sizeof tmp, "%lld", (long long)v.as.i);
            buf_add(buf, tmp, (size_t)n);
            return;
        }
        case T_FLOAT: {
            Value s = str_from_double(v.as.f);
            Str *str = (Str *)s.as.o;
            buf_add(buf, str->chars, str->len);
            return;
        }
        default:
            break;
    }

    if (v_is_str(v)) {
        json_escape(buf, (Str *)v.as.o);
        return;
    }

    if (v_is_list(v)) {
        List *list = (List *)v.as.o;
        if (list->count == 0) { buf_add_cstr(buf, "[]"); return; }
        buf_add_char(buf, '[');
        for (int32_t i = 0; i < list->count; i++) {
            if (i) buf_add_char(buf, ',');
            if (indent) {
                buf_add_char(buf, '\n');
                for (int k = 0; k < (level + 1) * indent; k++) buf_add_char(buf, ' ');
            }
            json_write(buf, list->items[i], indent, level + 1);
        }
        if (indent) {
            buf_add_char(buf, '\n');
            for (int k = 0; k < level * indent; k++) buf_add_char(buf, ' ');
        }
        buf_add_char(buf, ']');
        return;
    }

    if (v_is_map(v)) {
        Map *map = (Map *)v.as.o;
        if (map->count == 0) { buf_add_cstr(buf, "{}"); return; }
        buf_add_char(buf, '{');
        bool first = true;
        for (int32_t i = 0; i < map->cap; i++) {
            MapEntry *entry = &map->entries[i];
            if (!entry->used || entry->dead) continue;
            if (!first) buf_add_char(buf, ',');
            first = false;
            if (indent) {
                buf_add_char(buf, '\n');
                for (int k = 0; k < (level + 1) * indent; k++) buf_add_char(buf, ' ');
            }
            Value key = tern_to_string(entry->key);
            json_escape(buf, (Str *)key.as.o);
            buf_add_char(buf, ':');
            if (indent) buf_add_char(buf, ' ');
            json_write(buf, entry->value, indent, level + 1);
        }
        if (indent) {
            buf_add_char(buf, '\n');
            for (int k = 0; k < level * indent; k++) buf_add_char(buf, ' ');
        }
        buf_add_char(buf, '}');
        return;
    }

    // Structs stringify as their text form.
    json_escape(buf, (Str *)tern_to_string(v).as.o);
}

static Value js_stringify(int argc, Value *argv) {
    Buf buf;
    buf_init(&buf);
    int indent = argc > 1 && v_truthy(argv[1]) ? 2 : 0;
    json_write(&buf, arg(argc, argv, 0), indent, 0);
    return buf_finish(&buf);
}

typedef struct JsonParser {
    const char *text;
    int length;
    int pos;
    int failed;     // set on the first syntax error; parsing stops there
} JsonParser;

static Value json_parse_value(JsonParser *p);

static void json_skip(JsonParser *p) {
    while (p->pos < p->length) {
        char c = p->text[p->pos];
        if (c == ' ' || c == '\t' || c == '\n' || c == '\r') { p->pos++; continue; }
        break;
    }
}

/// Records a syntax error and returns nil, so callers can just `return`.
static Value json_fail(JsonParser *p) {
    p->failed = 1;
    p->pos = p->length;
    return v_nil();
}

static bool json_expect(JsonParser *p, char wanted) {
    json_skip(p);
    if (p->pos < p->length && p->text[p->pos] == wanted) {
        p->pos++;
        return true;
    }
    json_fail(p);
    return false;
}

static Value json_parse_string(JsonParser *p) {
    if (p->text[p->pos] != '"') {
        return json_fail(p);
    }

    Buf buf;
    buf_init(&buf);
    p->pos++;   // opening quote
    while (p->pos < p->length) {
        char c = p->text[p->pos++];
        if (c == '"') {
            return buf_finish(&buf);
        }
        if (c != '\\') {
            buf_add_char(&buf, c);
            continue;
        }
        if (p->pos >= p->length) {
            break;
        }
        char esc = p->text[p->pos++];
        switch (esc) {
            case 'n': buf_add_char(&buf, '\n'); break;
            case 't': buf_add_char(&buf, '\t'); break;
            case 'r': buf_add_char(&buf, '\r'); break;
            case 'b': buf_add_char(&buf, '\b'); break;
            case 'f': buf_add_char(&buf, '\f'); break;
            case '"': buf_add_char(&buf, '"'); break;
            case '\\': buf_add_char(&buf, '\\'); break;
            case '/': buf_add_char(&buf, '/'); break;
            case 'u': {
                if (p->pos + 4 > p->length) {
                    return json_fail(p);
                }
                char hex[5];
                memcpy(hex, p->text + p->pos, 4);
                hex[4] = 0;
                char *hex_end = NULL;
                long code = strtol(hex, &hex_end, 16);
                if (!hex_end || hex_end != hex + 4) {
                    return json_fail(p);
                }
                p->pos += 4;
                if (code < 0x80) {
                    buf_add_char(&buf, (char)code);
                } else if (code < 0x800) {
                    buf_add_char(&buf, (char)(0xC0 | (code >> 6)));
                    buf_add_char(&buf, (char)(0x80 | (code & 0x3F)));
                } else {
                    buf_add_char(&buf, (char)(0xE0 | (code >> 12)));
                    buf_add_char(&buf, (char)(0x80 | ((code >> 6) & 0x3F)));
                    buf_add_char(&buf, (char)(0x80 | (code & 0x3F)));
                }
                break;
            }
            default:
                return json_fail(p);
        }
    }
    return json_fail(p);   // unterminated string
}

static Value json_parse_object(JsonParser *p) {
    Map *map = map_new();
    p->pos++;   // {
    json_skip(p);
    if (p->pos < p->length && p->text[p->pos] == '}') {
        p->pos++;
        return v_obj((Obj *)map);
    }

    while (!p->failed) {
        json_skip(p);
        if (p->pos >= p->length || p->text[p->pos] != '"') {
            return json_fail(p);
        }
        Value key = json_parse_string(p);
        if (p->failed) {
            return v_nil();
        }
        if (!json_expect(p, ':')) {
            return v_nil();
        }
        Value value = json_parse_value(p);
        if (p->failed) {
            return v_nil();
        }
        map_set(map, key, value);

        json_skip(p);
        if (p->pos < p->length && p->text[p->pos] == ',') {
            p->pos++;
            continue;
        }
        if (json_expect(p, '}')) {
            return v_obj((Obj *)map);
        }
        return v_nil();
    }
    return v_nil();
}

static Value json_parse_array(JsonParser *p) {
    List *list = list_new(4);
    p->pos++;   // [
    json_skip(p);
    if (p->pos < p->length && p->text[p->pos] == ']') {
        p->pos++;
        return v_obj((Obj *)list);
    }

    while (!p->failed) {
        Value item = json_parse_value(p);
        if (p->failed) {
            return v_nil();
        }
        list_push(list, item);

        json_skip(p);
        if (p->pos < p->length && p->text[p->pos] == ',') {
            p->pos++;
            continue;
        }
        if (json_expect(p, ']')) {
            return v_obj((Obj *)list);
        }
        return v_nil();
    }
    return v_nil();
}

static Value json_parse_value(JsonParser *p) {
    json_skip(p);
    if (p->pos >= p->length) {
        return json_fail(p);
    }

    char c = p->text[p->pos];
    if (c == '{') {
        return json_parse_object(p);
    }
    if (c == '[') {
        return json_parse_array(p);
    }
    if (c == '"') {
        return json_parse_string(p);
    }

    if (p->pos + 4 <= p->length && memcmp(p->text + p->pos, "true", 4) == 0) {
        p->pos += 4;
        return v_bool(true);
    }
    if (p->pos + 5 <= p->length && memcmp(p->text + p->pos, "false", 5) == 0) {
        p->pos += 5;
        return v_bool(false);
    }
    if (p->pos + 4 <= p->length && memcmp(p->text + p->pos, "null", 4) == 0) {
        p->pos += 4;
        return v_nil();
    }

    if (c == '-' || (c >= '0' && c <= '9')) {
        char *end = NULL;
        const char *start = p->text + p->pos;
        double as_double = strtod(start, &end);
        if (!end || end == start) {
            return json_fail(p);
        }
        bool whole = true;
        for (const char *cursor = start; cursor < end; cursor++) {
            if (*cursor == '.' || *cursor == 'e' || *cursor == 'E') {
                whole = false;
                break;
            }
        }
        p->pos = (int)(end - p->text);
        return whole ? v_int((int64_t)as_double) : v_float(as_double);
    }

    return json_fail(p);
}

static Value js_parse(int argc, Value *argv) {
    Value text = arg(argc, argv, 0);
    if (!v_is_str(text)) {
        tern_panic("TypeError", "json.parse expects a string, not %s", tern_type_name(text));
    }
    Str *str = (Str *)text.as.o;
    JsonParser parser = { str->chars, (int)str->len, 0, 0 };
    Value value = json_parse_value(&parser);
    json_skip(&parser);
    if (parser.failed || parser.pos != parser.length) {
        tern_panic("JsonError", "invalid JSON at offset %d", parser.pos);
    }
    return value;
}

/// `json.tryParse(text[, fallback])`: parses without raising; bad input or a
/// non-string argument yields the fallback (nil when omitted).
static Value js_tryparse(int argc, Value *argv) {
    Value fallback = argc > 1 ? argv[1] : v_nil();
    Value text = arg(argc, argv, 0);
    if (!v_is_str(text)) {
        return fallback;
    }
    Str *str = (Str *)text.as.o;
    JsonParser parser = { str->chars, (int)str->len, 0, 0 };
    Value value = json_parse_value(&parser);
    json_skip(&parser);
    if (parser.failed || parser.pos != parser.length) {
        return fallback;
    }
    return value;
}

// --- os -------------------------------------------------------------------

static Value os_platform(int argc, Value *argv) {
    (void)argc; (void)argv;
#if defined(__APPLE__)
    return str_from_cstr("macos");
#elif defined(_WIN32)
    return str_from_cstr("windows");
#else
    return str_from_cstr("linux");
#endif
}

static Value os_linesep(int argc, Value *argv) {
    (void)argc; (void)argv;
    return str_from_cstr("\n");
}

static Value os_cwd(int argc, Value *argv) {
    (void)argc; (void)argv;
    char buffer[4096];
    return getcwd(buffer, sizeof buffer) ? str_from_cstr(buffer) : str_from_cstr("");
}

static Value os_exists(int argc, Value *argv) {
    Value path = arg(argc, argv, 0);
    if (!v_is_str(path)) return v_bool(false);
    Str *str = (Str *)path.as.o;
    FILE *handle = fopen(str->chars, "rb");
    if (handle) { fclose(handle); return v_bool(true); }
    return v_bool(false);
}

static Value os_isdir(int argc, Value *argv) {
    Value path = arg(argc, argv, 0);
    if (!v_is_str(path)) return v_bool(false);
    Str *str = (Str *)path.as.o;
    char buffer[4096];
    snprintf(buffer, sizeof buffer, "%s/.", str->chars);
    FILE *handle = fopen(buffer, "rb");
    if (handle) { fclose(handle); return v_bool(true); }
    return v_bool(false);
}

static Value os_read(int argc, Value *argv) {
    Value path = arg(argc, argv, 0);
    if (!v_is_str(path)) return v_nil();
    Str *str = (Str *)path.as.o;
    FILE *handle = fopen(str->chars, "rb");
    if (!handle) {
        tern_panic("IOError", "cannot read '%s'", str->chars);
    }
    Buf buf;
    buf_init(&buf);
    char chunk[4096];
    size_t got;
    while ((got = fread(chunk, 1, sizeof chunk, handle)) > 0) buf_add(&buf, chunk, got);
    fclose(handle);
    return buf_finish(&buf);
}

static Value os_write(int argc, Value *argv) {
    Value path = arg(argc, argv, 0);
    Value text = arg(argc, argv, 1);
    if (!v_is_str(path)) return v_bool(false);
    Str *name = (Str *)path.as.o;
    Value rendered = tern_to_string(text);
    Str *body = (Str *)rendered.as.o;
    FILE *handle = fopen(name->chars, "wb");
    if (!handle) {
        tern_panic("IOError", "cannot write '%s'", name->chars);
    }
    fwrite(body->chars, 1, body->len, handle);
    fclose(handle);
    return v_bool(true);
}

static Value os_env(int argc, Value *argv) {
    Value name = arg(argc, argv, 0);
    if (!v_is_str(name)) return arg(argc, argv, 1);
    Str *str = (Str *)name.as.o;
    const char *value = getenv(str->chars);
    return value ? str_from_cstr(value) : arg(argc, argv, 1);
}

// The binary's own arguments, minus argv[0]: set once by `main` before the
// program body runs.
static int tern_arg_count = 0;
static char **tern_arg_values = NULL;

void tern_set_args(int count, char **values) {
    tern_arg_count = count > 0 ? count : 0;
    tern_arg_values = values;
}

static Value os_setenv(int argc, Value *argv) {
    Value name = arg(argc, argv, 0);
    if (!v_is_str(name)) return v_nil();
    Str *key = (Str *)name.as.o;
    Value value = tern_to_string(arg(argc, argv, 1));
    Str *text = (Str *)value.as.o;
    setenv(key->chars, text->chars, 1);
    return v_nil();
}

// `os.joinPath(a, b, ...)`: segments joined with '/' unless a later one is
// absolute, in which case it replaces what came before.
static Value os_joinpath(int argc, Value *argv) {
    Buf buf;
    buf_init(&buf);
    for (int i = 0; i < argc; i++) {
        Value part = tern_to_string(argv[i]);
        Str *text = (Str *)part.as.o;
        if (text->len == 0) {
            continue;
        }
        if (text->chars[0] == '/' && buf.len > 0) {
            buf.len = 0;
        } else if (buf.len > 0 && buf.data[buf.len - 1] != '/') {
            buf_add(&buf, "/", 1);
        }
        while (buf.len > 0 && text->chars[0] == '/' && buf.data[buf.len - 1] == '/') {
            buf.len--;   // no doubled separator
        }
        buf_add(&buf, text->chars, text->len);
    }
    return buf_finish(&buf);
}

static Value os_args(int argc, Value *argv) {
    (void)argc; (void)argv;
    List *list = list_new(tern_arg_count);
    for (int i = 0; i < tern_arg_count; i++) {
        list_push(list, str_from_cstr(tern_arg_values[i]));
    }
    return v_obj((Obj *)list);
}

static const MethodSlot number_methods[] = {
    {"abs", n_abs_self}, {"clamp", n_clamp_self}, {"round", n_round_self},
    {"toInt", n_toint_self}, {"toFloat", n_tofloat_self}, {"toString", n_tostring_self},
    {"times", n_times_self}, {NULL, NULL},
};

static const MethodSlot range_methods[] = {
    {"toList", r_tolist}, {"contains", r_contains}, {"length", r_length},
    {"toString", r_tostring}, {"map", r_map}, {"each", r_each}, {NULL, NULL},
};

static const MethodSlot map_methods[] = {
    {"keys", m_keys}, {"values", m_values}, {"has", m_has}, {"get", m_get},
    {"set", m_set}, {"remove", m_remove}, {"isEmpty", m_isempty}, {"clear", m_clear},
    {"clone", m_clone}, {"entries", m_entries}, {"contains", m_has},
    {"merge", m_merge}, {"toString", m_tostring_self}, {NULL, NULL},
};
static const MethodSlot string_methods[] = {
    {"upper", s_upper}, {"lower", s_lower}, {"trim", s_trim}, {"trimStart", s_trimstart},
    {"trimEnd", s_trimend}, {"contains", s_contains}, {"startsWith", s_starts},
    {"endsWith", s_ends}, {"indexOf", s_indexof}, {"lastIndexOf", s_lastindexof},
    {"count", s_count},
    {"substring", s_substring}, {"slice", s_slice}, {"split", s_split},
    {"replace", s_replace}, {"replaceFirst", s_replacefirst}, {"repeat", s_repeat},
    {"chars", s_chars}, {"lines", s_lines}, {"words", s_split},
    {"isEmpty", s_isempty}, {"toInt", s_toint}, {"toFloat", s_tofloat},
    {"toString", s_tostring}, {"padStart", s_padstart}, {"padEnd", s_padend},
    {"reversed", s_reversed}, {"format", s_format}, {NULL, NULL},
};

static const MethodSlot list_methods[] = {
    {"push", l_push}, {"pop", l_pop}, {"shift", l_shift}, {"unshift", l_unshift},
    {"join", l_join}, {"contains", l_contains}, {"indexOf", l_indexof},
    {"slice", l_slice}, {"reverse", l_reverse}, {"sort", l_sort},
    {"first", l_first}, {"last", l_last}, {"isEmpty", l_isempty}, {"sum", l_sum},
    {"min", l_min}, {"max", l_max}, {"clear", l_clear}, {"clone", l_clone},
    {"concat", l_concat}, {"insert", l_insert}, {"remove", l_remove},
    {"map", l_map}, {"filter", l_filter}, {"each", l_each}, {"reduce", l_reduce},
    {"toString", l_tostring}, {"flatten", l_flatten}, {"unique", l_unique},
    {"take", l_take}, {"drop", l_drop}, {"all", l_all}, {"any", l_any},
    {"find", l_find}, {"findIndex", l_findindex}, {"removeValue", l_removevalue},
    {NULL, NULL},
};

static NativeFn lookup_method(const MethodSlot *slots, const char *name) {
    for (int i = 0; slots[i].name; i++) {
        if (strcmp(slots[i].name, name) == 0) {
            return slots[i].fn;
        }
    }
    return NULL;
}

NativeFn tern_builtin_method(Value receiver, const char *name) {
    if (v_is_str(receiver)) {
        return lookup_method(string_methods, name);
    }
    if (v_is_list(receiver)) {
        return lookup_method(list_methods, name);
    }
    if (v_is_map(receiver)) {
        return lookup_method(map_methods, name);
    }
    if (v_is_obj(receiver, O_RANGE)) {
        return lookup_method(range_methods, name);
    }
    if (v_is_num(receiver)) {
        return lookup_method(number_methods, name);
    }
    return NULL;
}

Value tern_invoke(Value receiver, const char *name, int argc, Value *argv) {
    if (receiver.tag == T_OBJ) {
        switch (receiver.as.o->type) {
            case O_INSTANCE: {
                Instance *instance = (Instance *)receiver.as.o;
                int field = find_field(instance->type, name);
                if (field >= 0) {
                    Value member = instance->fields[field];
                    if (member.tag == T_OBJ &&
                        (member.as.o->type == O_CLOSURE || member.as.o->type == O_NATIVE)) {
                        return tern_call(member, argc, argv);
                    }
                }
                Closure *method = find_method(instance->type, name);
                if (method) {
                    Value stack_args[argc + 1];
                    stack_args[0] = receiver;
                    for (int i = 0; i < argc; i++) {
                        stack_args[i + 1] = argv[i];
                    }
                    return tern_call(v_obj((Obj *)method), argc + 1, stack_args);
                }
                break;
            }

            case O_MAP: {
                Value member;
                if (map_get((Map *)receiver.as.o, str_from_cstr(name), &member) &&
                    member.tag == T_OBJ &&
                    (member.as.o->type == O_CLOSURE || member.as.o->type == O_NATIVE)) {
                    return tern_call(member, argc, argv);
                }
                break;
            }

            default:
                break;
        }
    }

    NativeFn fn = tern_builtin_method(receiver, name);
    if (fn) {
        Value stack_args[argc + 1];
        stack_args[0] = receiver;
        for (int i = 0; i < argc; i++) {
            stack_args[i + 1] = argv[i];
        }
        return fn(argc + 1, stack_args);
    }

    tern_panic("TypeError", "%s has no method '%s'", tern_type_name(receiver), name);
    return v_nil();
}

// ------------------------------------------------------------------ globals

typedef struct GlobalSlot {
    const char *name;
    NativeFn fn;
} GlobalSlot;

static const GlobalSlot global_natives[] = {
    {"print", n_print}, {"println", n_println}, {"eprint", n_eprint}, {"input", n_input},
    {"len", n_len}, {"type", n_type}, {"str", n_str}, {"repr", n_repr},
    {"int", n_int}, {"float", n_float}, {"bool", n_bool}, {"chr", n_chr},
    {"ord", n_ord}, {"range", n_range}, {"keys", n_keys}, {"values", n_values},
    {"has", n_has}, {"assert", n_assert}, {"error", n_error}, {"panic", n_panic},
    {"abs", n_abs}, {"min", n_min}, {"max", n_max}, {"copy", n_copy},
    {"sleep", n_sleep}, {NULL, NULL},
};

static Value make_native(const char *name, NativeFn fn) {
    Native *native = (Native *)tern_alloc(sizeof(Native), O_NATIVE);
    native->name = name;
    native->fn = fn;
    return v_obj((Obj *)native);
}

static Value namespace_map(const char *ns) {
    Map *map = map_new();

    if (strcmp(ns, "math") == 0) {
        map_set(map, str_from_cstr("floor"), make_native("floor", m_floor));
        map_set(map, str_from_cstr("ceil"), make_native("ceil", m_ceil));
        map_set(map, str_from_cstr("round"), make_native("round", m_round));
        map_set(map, str_from_cstr("sqrt"), make_native("sqrt", m_sqrt));
        map_set(map, str_from_cstr("trunc"), make_native("trunc", m_trunc));
        map_set(map, str_from_cstr("pow"), make_native("pow", m_pow));
        map_set(map, str_from_cstr("abs"), make_native("abs", m_abs));
        map_set(map, str_from_cstr("min"), make_native("min", m_min));
        map_set(map, str_from_cstr("max"), make_native("max", m_max));
        map_set(map, str_from_cstr("sign"), make_native("sign", m_sign));
        map_set(map, str_from_cstr("clamp"), make_native("clamp", m_clamp));
        map_set(map, str_from_cstr("isInteger"), make_native("isInteger", m_isinteger));
        map_set(map, str_from_cstr("isNaN"), make_native("isNaN", m_isnan));
        map_set(map, str_from_cstr("gcd"), make_native("gcd", m_gcd));
        map_set(map, str_from_cstr("pi"), v_float(3.141592653589793));
        map_set(map, str_from_cstr("e"), v_float(2.718281828459045));
        map_set(map, str_from_cstr("tau"), v_float(6.283185307179586));
        map_set(map, str_from_cstr("infinity"), v_float(INFINITY));
        map_set(map, str_from_cstr("nan"), v_float(NAN));
        map_set(map, str_from_cstr("lcm"), make_native("lcm", m_lcm));
        map_set(map, str_from_cstr("log"), make_native("log", m_log));
        map_set(map, str_from_cstr("log2"), make_native("log2", m_log2));
        map_set(map, str_from_cstr("log10"), make_native("log10", m_log10));
        map_set(map, str_from_cstr("cbrt"), make_native("cbrt", m_cbrt));
        map_set(map, str_from_cstr("hypot"), make_native("hypot", m_hypot));
        map_set(map, str_from_cstr("exp"), make_native("exp", m_exp));
        map_set(map, str_from_cstr("sin"), make_native("sin", m_sin));
        map_set(map, str_from_cstr("cos"), make_native("cos", m_cos));
        map_set(map, str_from_cstr("tan"), make_native("tan", m_tan));
        map_set(map, str_from_cstr("asin"), make_native("asin", m_asin));
        map_set(map, str_from_cstr("acos"), make_native("acos", m_acos));
        map_set(map, str_from_cstr("atan"), make_native("atan", m_atan));
        map_set(map, str_from_cstr("atan2"), make_native("atan2", m_atan2));
        map_set(map, str_from_cstr("toDegrees"), make_native("toDegrees", m_todegrees));
        map_set(map, str_from_cstr("toRadians"), make_native("toRadians", m_toradians));
        map_set(map, str_from_cstr("toString"), make_native("toString", n_tostring_self));
        return v_obj((Obj *)map);
    }

    if (strcmp(ns, "lists") == 0) {
        map_set(map, str_from_cstr("range"), make_native("range", ln_range));
        map_set(map, str_from_cstr("zip"), make_native("zip", ln_zip));
        map_set(map, str_from_cstr("enumerate"), make_native("enumerate", ln_enumerate));
        map_set(map, str_from_cstr("chunk"), make_native("chunk", ln_chunk));
        map_set(map, str_from_cstr("join"), make_native("join", ln_join));
        map_set(map, str_from_cstr("repeat"), make_native("repeat", ln_repeat));
        map_set(map, str_from_cstr("sort"), make_native("sort", ln_sort));
        map_set(map, str_from_cstr("unique"), make_native("unique", l_unique));
        map_set(map, str_from_cstr("flatten"), make_native("flatten", l_flatten));
        map_set(map, str_from_cstr("take"), make_native("take", l_take));
        map_set(map, str_from_cstr("drop"), make_native("drop", l_drop));
        map_set(map, str_from_cstr("all"), make_native("all", l_all));
        map_set(map, str_from_cstr("any"), make_native("any", l_any));
        map_set(map, str_from_cstr("find"), make_native("find", l_find));
        map_set(map, str_from_cstr("findIndex"), make_native("findIndex", l_findindex));
        map_set(map, str_from_cstr("remove"), make_native("remove", l_remove));
        map_set(map, str_from_cstr("removeValue"), make_native("removeValue", l_removevalue));
        map_set(map, str_from_cstr("insert"), make_native("insert", l_insert));
        map_set(map, str_from_cstr("concat"), make_native("concat", l_concat));
        map_set(map, str_from_cstr("reverse"), make_native("reverse", l_reverse));
        map_set(map, str_from_cstr("map"), make_native("map", l_map));
        map_set(map, str_from_cstr("filter"), make_native("filter", l_filter));
        map_set(map, str_from_cstr("each"), make_native("each", l_each));
        map_set(map, str_from_cstr("reduce"), make_native("reduce", l_reduce));
        map_set(map, str_from_cstr("sum"), make_native("sum", l_sum));
        map_set(map, str_from_cstr("min"), make_native("min", l_min));
        map_set(map, str_from_cstr("max"), make_native("max", l_max));
        map_set(map, str_from_cstr("first"), make_native("first", l_first));
        map_set(map, str_from_cstr("last"), make_native("last", l_last));
        map_set(map, str_from_cstr("contains"), make_native("contains", l_contains));
        map_set(map, str_from_cstr("indexOf"), make_native("indexOf", l_indexof));
        return v_obj((Obj *)map);
    }

    if (strcmp(ns, "text") == 0) {
        map_set(map, str_from_cstr("format"), make_native("format", s_format));
        map_set(map, str_from_cstr("pad"), make_native("pad", tx_pad));
        map_set(map, str_from_cstr("trim"), make_native("trim", s_trim));
        map_set(map, str_from_cstr("trimStart"), make_native("trimStart", s_trimstart));
        map_set(map, str_from_cstr("trimEnd"), make_native("trimEnd", s_trimend));
        map_set(map, str_from_cstr("join"), make_native("join", ln_join));
        map_set(map, str_from_cstr("repeat"), make_native("repeat", ln_repeat));
        map_set(map, str_from_cstr("upper"), make_native("upper", s_upper));
        map_set(map, str_from_cstr("lower"), make_native("lower", s_lower));
        map_set(map, str_from_cstr("split"), make_native("split", s_split));
        map_set(map, str_from_cstr("words"), make_native("words", s_split));
        map_set(map, str_from_cstr("lines"), make_native("lines", s_lines));
        map_set(map, str_from_cstr("repeat"), make_native("repeat", s_repeat));
        map_set(map, str_from_cstr("contains"), make_native("contains", s_contains));
        map_set(map, str_from_cstr("startsWith"), make_native("startsWith", s_starts));
        map_set(map, str_from_cstr("endsWith"), make_native("endsWith", s_ends));
        map_set(map, str_from_cstr("indexOf"), make_native("indexOf", s_indexof));
        map_set(map, str_from_cstr("slice"), make_native("slice", s_slice));
        map_set(map, str_from_cstr("replace"), make_native("replace", s_replace));
        map_set(map, str_from_cstr("reversed"), make_native("reversed", s_reversed));
        map_set(map, str_from_cstr("toInt"), make_native("toInt", s_toint));
        map_set(map, str_from_cstr("toFloat"), make_native("toFloat", s_tofloat));
        map_set(map, str_from_cstr("isEmpty"), make_native("isEmpty", s_isempty));
        map_set(map, str_from_cstr("chars"), make_native("chars", s_chars));
        return v_obj((Obj *)map);
    }

    if (strcmp(ns, "json") == 0) {
        map_set(map, str_from_cstr("stringify"), make_native("stringify", js_stringify));
        map_set(map, str_from_cstr("parse"), make_native("parse", js_parse));
        map_set(map, str_from_cstr("tryParse"), make_native("tryParse", js_tryparse));
        return v_obj((Obj *)map);
    }

    if (strcmp(ns, "os") == 0) {
        map_set(map, str_from_cstr("platform"), make_native("platform", os_platform));
        map_set(map, str_from_cstr("lineSeparator"), make_native("lineSeparator", os_linesep));
        map_set(map, str_from_cstr("cwd"), make_native("cwd", os_cwd));
        map_set(map, str_from_cstr("args"), make_native("args", os_args));
        map_set(map, str_from_cstr("env"), make_native("env", os_env));
        map_set(map, str_from_cstr("setEnv"), make_native("setEnv", os_setenv));
        map_set(map, str_from_cstr("joinPath"), make_native("joinPath", os_joinpath));
        map_set(map, str_from_cstr("exists"), make_native("exists", os_exists));
        map_set(map, str_from_cstr("isFile"), make_native("isFile", os_exists));
        map_set(map, str_from_cstr("isDir"), make_native("isDir", os_isdir));
        map_set(map, str_from_cstr("read"), make_native("read", os_read));
        map_set(map, str_from_cstr("write"), make_native("write", os_write));
        return v_obj((Obj *)map);
    }

    if (strcmp(ns, "random") == 0) {
        map_set(map, str_from_cstr("seed"), make_native("seed", rnd_seed));
        map_set(map, str_from_cstr("float"), make_native("float", rnd_float));
        map_set(map, str_from_cstr("int"), make_native("int", rnd_int));
        map_set(map, str_from_cstr("bool"), make_native("bool", rnd_bool));
        map_set(map, str_from_cstr("choice"), make_native("choice", rnd_choice));
        map_set(map, str_from_cstr("shuffle"), make_native("shuffle", rnd_shuffle));
        return v_obj((Obj *)map);
    }

    if (strcmp(ns, "time") == 0) {
        map_set(map, str_from_cstr("millis"), make_native("millis", t_millis));
        map_set(map, str_from_cstr("iso"), make_native("iso", t_iso));
        map_set(map, str_from_cstr("now"), make_native("now", t_now));
        map_set(map, str_from_cstr("sleep"), make_native("sleep", n_sleep));
        return v_obj((Obj *)map);
    }

    return v_obj((Obj *)map);
}

/// Looks a function up in the well known namespaces (math, text, json, ...).
/// Used for `import "std:x"` constants and for globals the interpreter exposes.
Value tern_lookup_std_member(const char *name) {
    static const char *spaces[] = {"math", "time", "json", "os", "random", "text", "lists", NULL};
    for (int i = 0; spaces[i]; i++) {
        Value space = namespace_map(spaces[i]);
        Value member;
        if (map_get((Map *)space.as.o, str_from_cstr(name), &member)) {
            return member;
        }
    }
    return v_nil();
}

Value tern_lookup_global(const char *name) {
    for (int i = 0; global_natives[i].name; i++) {
        if (strcmp(global_natives[i].name, name) == 0) {
            return make_native(global_natives[i].name, global_natives[i].fn);
        }
    }

    if (strcmp(name, "math") == 0 || strcmp(name, "time") == 0 ||
        strcmp(name, "json") == 0 || strcmp(name, "os") == 0 ||
        strcmp(name, "random") == 0 || strcmp(name, "text") == 0 ||
        strcmp(name, "lists") == 0) {
        return namespace_map(name);
    }

    if (strcmp(name, "fs") == 0) {
        return namespace_map("os");
    }

    return v_nil();
}

void tern_bind_globals(Value *globals, const char **names, int count) {
    tern_gc_register_values(globals, count);
    for (int i = 0; i < count; i++) {
        globals[i] = tern_lookup_global(names[i]);
    }
}

void tern_rt_init(void) {
    /* stdout is unbuffered-per-line by default; keep line buffering for pipes. */
    setvbuf(stdout, NULL, _IOLBF, 0);

    /* TERN_GC_THRESHOLD exists so the collector can be stress-tested: a tiny
       value collects constantly and surfaces missing roots. */
    const char *threshold = getenv("TERN_GC_THRESHOLD");
    if (threshold && *threshold) {
        long long value = atoll(threshold);
        if (value > 0) {
            gc_threshold = (size_t)value;
            gc_threshold_fixed = 1;
        }
    }

    if (getenv("TERN_GC_STATS")) {
        atexit(gc_print_stats);
    }

}
