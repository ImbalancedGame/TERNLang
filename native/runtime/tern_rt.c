// TERN native runtime - implementation.
#include "tern_rt.h"
#include "tern_platform.h"

// A builtin method table entry. The argument counts include the receiver,
// mirroring how the interpreter registers its methods.
typedef struct MethodSlot {
    const char *name;
    NativeFn fn;
    int16_t min_args;   // counts the receiver, like the interpreter's tables
    int16_t max_args;
} MethodSlot;

// Defined with the globals table; used by tern_invoke below.
static void check_method_arity(const char *name, const MethodSlot *slot, int argc_with_receiver);
static const MethodSlot *builtin_slot(Value receiver, const char *name);
static Value make_native_arity(const char *name, NativeFn fn, int min_args, int max_args);

// ------------------------------------------------------------------ errors

// Recursion is bounded by the C stack rather than by a bytecode budget. The
// limit is high enough for real programs (about 3 MB of stack) and low enough
// that hitting it raises a catchable error instead of a SIGSEGV.
#define TERN_MAX_CALL_DEPTH 10000

// Every module's global array, registered as it starts, so the wire format can
// find a struct type by name when a value arrives from the network.
#define TERN_MAX_GLOBAL_TABLES 64
typedef struct GlobalTable {
    Value *values;
    const char **names;
    int count;
} GlobalTable;

static GlobalTable tern_global_tables[TERN_MAX_GLOBAL_TABLES];
static int tern_global_table_count = 0;

static TypeObj *tern_find_type(const char *name) {
    for (int i = tern_global_table_count - 1; i >= 0; i--) {
        GlobalTable *table = &tern_global_tables[i];
        for (int slot = 0; slot < table->count; slot++) {
            if (strcmp(table->names[slot], name) != 0) {
                continue;
            }
            Value value = table->values[slot];
            if (value.tag == T_OBJ && value.as.o->type == O_TYPE) {
                return (TypeObj *)value.as.o;
            }
        }
    }
    return NULL;
}

TernTry *tern_try_top = NULL;
Value tern_thrown;
int tern_call_depth = 0;
static const char *tern_thrown_kind = "Error";

// ------------------------------------------------------------------ traces
//
// The frame table itself; the push/pop helpers are inline in the header so that
// tern_call and every generated call site stay cheap.

TernFrame *tern_frame_cur = NULL;
int tern_current_line = 0;
const char *tern_program_file = "<program>";

void tern_trace_frames(void *output, const char *file) {
    FILE *out = (FILE *)output;
    // Innermost first, matching the interpreter: each frame reports the line it
    // had reached, which for the innermost one is where the error was raised.
    int printed = 0;
    TernFrame *child = NULL;
    for (TernFrame *frame = tern_frame_cur; frame && printed < 32; frame = frame->prev) {
        const char *name = frame->proto && frame->proto->name ? frame->proto->name : "<anonymous>";
        // A frame's own line is the call site the frame it called recorded.
        int line = child ? child->caller_line : tern_current_line;
        fprintf(out, "  at %s (%s:%d)\n", name, file, line);
        child = frame;
        printed++;
    }
    if (child == NULL && tern_current_line > 0) {
        // Built with --no-traces, or an error outside any TERN frame.
        fprintf(out, "  at <module> (%s:%d)\n", file, tern_current_line);
    }
}

// Reports an uncaught error the way the interpreter does and stops the program.
static void report_uncaught(const char *kind, Value payload) {
    fflush(stdout);
    fprintf(stderr, "%s: ", kind);
    Value text = tern_to_string(payload);
    Str *str = (Str *)text.as.o;
    fwrite(str->chars, 1, str->len, stderr);
    fputc('\n', stderr);
    tern_trace_frames(stderr, tern_program_file);
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
    s->units = 0;
    s->hash = hash_bytes(data, len);
    if (len) {
        memcpy(s->chars, data, len);
    }
    s->chars[len] = '\0';
    return s;
}

Value str_value(const char *data, size_t len) { return v_obj((Obj *)str_from(data, len)); }

// --- UTF-8 <-> code units -------------------------------------------------
//
// Strings are stored as UTF-8, but the language counts positions in UTF-16 code
// units (like the interpreter): every code point above U+FFFF counts as two, so
// `len("👍")` is 2 and `len("héllo")` is 5. Everything that talks about string
// positions (index, slice, iteration, chars, indexOf, ord, pad widths) works in
// those units.

/// Decodes the code point starting at `p`, which must be < `end`. Invalid bytes
/// decode as themselves, one byte at a time, so a bad string still counts.
static uint32_t utf8_decode(const char *p, const char *end, int *size) {
    unsigned char lead = (unsigned char)*p;
    if (lead < 0x80) {
        *size = 1;
        return lead;
    }

    int extra = lead >= 0xF0 ? 3 : lead >= 0xE0 ? 2 : lead >= 0xC0 ? 1 : 0;
    if (extra == 0 || p + 1 + extra > end) {
        *size = 1;
        return lead;
    }

    uint32_t code = lead & (0x3F >> extra);
    for (int i = 1; i <= extra; i++) {
        unsigned char next = (unsigned char)p[i];
        if ((next & 0xC0) != 0x80) {
            *size = 1;
            return lead;
        }
        code = (code << 6) | (next & 0x3F);
    }

    *size = 1 + extra;
    return code;
}

/// Writes `code` as UTF-8; returns the number of bytes (1..4).
static int utf8_encode(uint32_t code, char *out) {
    if (code < 0x80) {
        out[0] = (char)code;
        return 1;
    }
    if (code < 0x800) {
        out[0] = (char)(0xC0 | (code >> 6));
        out[1] = (char)(0x80 | (code & 0x3F));
        return 2;
    }
    if (code < 0x10000) {
        out[0] = (char)(0xE0 | (code >> 12));
        out[1] = (char)(0x80 | ((code >> 6) & 0x3F));
        out[2] = (char)(0x80 | (code & 0x3F));
        return 3;
    }
    out[0] = (char)(0xF0 | (code >> 18));
    out[1] = (char)(0x80 | ((code >> 12) & 0x3F));
    out[2] = (char)(0x80 | ((code >> 6) & 0x3F));
    out[3] = (char)(0x80 | (code & 0x3F));
    return 4;
}

/// Code points above U+FFFF occupy two UTF-16 code units.
static int utf16_width(uint32_t code) { return code >= 0x10000 ? 2 : 1; }

static int64_t str_units(const Str *s) {
    // Strings are immutable, so the count is cached in the object. Strings are
    // rarely longer than a few hundred bytes, so this keeps `len()` O(1) on
    // the second call and beyond without costing anything at creation.
    Str *mutable = (Str *)s;
    if (mutable->units == 0) {
        int64_t units = 0;
        const char *p = s->chars;
        const char *end = s->chars + s->len;
        while (p < end) {
            int size;
            units += utf16_width(utf8_decode(p, end, &size));
            p += size;
        }
        mutable->units = (uint32_t)units;
    }
    return (int64_t)mutable->units;
}

/// Byte offset of code-unit index `unit`, clamped to [0, len]. An index that
/// lands inside a surrogate pair resolves to the start of that character.
static uint32_t str_offset_of_unit(const Str *s, int64_t unit) {
    if (unit <= 0) {
        return 0;
    }

    const char *p = s->chars;
    const char *end = s->chars + s->len;
    int64_t seen = 0;
    while (p < end) {
        int size;
        int width = utf16_width(utf8_decode(p, end, &size));
        if (seen + width > unit) {
            return (uint32_t)(p - s->chars);
        }
        seen += width;
        p += size;
    }
    return s->len;
}

/// Code-unit index of a byte offset (used by indexOf, which searches bytes).
static int64_t str_unit_of_offset(const Str *s, uint32_t offset) {
    const char *p = s->chars;
    const char *end = s->chars + (offset < s->len ? offset : s->len);
    int64_t units = 0;
    while (p < end) {
        int size;
        units += utf16_width(utf8_decode(p, end, &size));
        p += size;
    }
    return units;
}

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
    } else {
        // .NET writes exponents in upper case ("1E+20"); match it exactly so
        // both back ends print identical text.
        for (char *p = buffer; *p; p++) {
            if (*p == 'e') {
                *p = 'E';
            }
        }
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

// The error names the index the script wrote, not the one left after the
// negative adjustment: `xs[-9]` reports -9 in both back ends.
Value list_get(List *list, int64_t index) {
    int64_t written = index;
    if (index < 0) {
        index += list->count;
    }
    if (index < 0 || index >= list->count) {
        tern_panic("IndexError", "index %lld out of range for list of length %d",
                   (long long)written, list->count);
    }
    return list->items[index];
}

void list_set(List *list, int64_t index, Value v) {
    int64_t written = index;
    if (index < 0) {
        index += list->count;
    }
    if (index < 0 || index >= list->count) {
        tern_panic("IndexError", "index %lld out of range for list of length %d",
                   (long long)written, list->count);
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

// Bitwise operators report the operator, not the operand: "operator '&'
// requires integers but got string". Like the interpreter's ToInteger they
// accept a float with no fractional part, as long as it fits a long.
static int64_t require_int_op(Value v, const char *op) {
    if (v.tag == T_FLOAT) {
        double number = v.as.f;
        if (floor(number) == number && fabs(number) < 9.22e18) {
            return (int64_t)number;
        }
    }
    if (v.tag != T_INT) {
        tern_panic("TypeError", "operator '%s' requires integers but got %s", op, tern_type_name(v));
    }
    return v.as.i;
}

// Math builtins coerce: the interpreter reads a non-number as 0.0 (it goes
// through a defaulting numeric accessor), so compiled code must not read a
// numeric union field out of a string or nil, which yields garbage.
static double v_as_number(Value v) { return v_is_num(v) ? v_as_double(v) : 0.0; }

// Integer requirement for builtin arguments. Like the interpreter's
// RequireInt it accepts an integral float, and it names the function and the
// 1-based argument position, counting a method's receiver as argument 1.
static int64_t need_int(Value v, const char *fn, int index) {
    if (v.tag == T_INT) {
        return v.as.i;
    }
    if (v.tag == T_FLOAT) {
        double number = v.as.f;
        if (floor(number) == number && fabs(number) < 9.2e18) {
            return (int64_t)number;
        }
    }
    tern_panic("TypeError", "%s() argument %d must be an integer, got %s",
               fn, index, ((Str *)tern_to_debug(v))->chars);
    return 0;
}

// .NET saturates a float-to-integer conversion that is out of range: NaN
// becomes 0, a too-large value becomes INT64_MAX and a too-small one INT64_MIN.
// A plain cast would give INT64_MIN at both ends, so clamp here to match.
static int64_t f64_to_i64(double number) {
    if (number != number) return 0;
    if (number >= 9223372036854775808.0) return INT64_MAX;
    if (number <= -9223372036854775808.0) return INT64_MIN;
    return (int64_t)number;
}

// Float parameters take any number and name the position like need_int does.
static double need_number(Value v, const char *fn, int index) {
    if (!v_is_num(v)) {
        tern_panic("TypeError", "%s() argument %d must be a number, got %s",
                   fn, index, ((Str *)tern_to_debug(v))->chars);
    }
    return v_as_double(v);
}

// Indexing reports the container: "list index must be an integer, not string".
static int64_t require_index(Value v, const char *kind) {
    if (v.tag != T_INT) {
        tern_panic("TypeError", "%s index must be an integer, not %s", kind, tern_type_name(v));
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
        joined->units = 0;
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

// Arithmetic type errors name both operands, like the interpreter.
static void require_numbers(Value a, Value b, const char *op) {
    if (!v_is_num(a) || !v_is_num(b)) {
        tern_panic("TypeError", "operator '%s' is not defined for %s and %s",
                   op, tern_type_name(a), tern_type_name(b));
    }
}

Value tern_sub(Value a, Value b) {
    if (a.tag == T_INT && b.tag == T_INT) {
        int64_t result;
        if (__builtin_sub_overflow(a.as.i, b.as.i, &result)) {
            return v_float((double)a.as.i - (double)b.as.i);
        }
        return v_int(result);
    }
    require_numbers(a, b, "-");
    return v_float(v_as_double(a) - v_as_double(b));
}

Value tern_mul(Value a, Value b) {
    if (a.tag == T_INT && b.tag == T_INT) {
        int64_t result;
        if (__builtin_mul_overflow(a.as.i, b.as.i, &result)) {
            return v_float((double)a.as.i * (double)b.as.i);
        }
        return v_int(result);
    }
    require_numbers(a, b, "*");
    return v_float(v_as_double(a) * v_as_double(b));
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
    require_numbers(a, b, "/");
    double divisor = v_as_double(b);
    if (divisor == 0) {
        tern_panic("DivisionByZero", "division by zero");
    }
    return v_float(v_as_double(a) / divisor);
}

Value tern_mod(Value a, Value b) {
    if (a.tag == T_INT && b.tag == T_INT) {
        if (b.as.i == 0) {
            tern_panic("DivisionByZero", "modulo by zero");
        }
        return v_int(b.as.i == -1 ? 0 : a.as.i % b.as.i);
    }
    require_numbers(a, b, "%");
    double divisor = v_as_double(b);
    if (divisor == 0) {
        tern_panic("DivisionByZero", "modulo by zero");
    }
    // Truncated toward zero, like the integer case and like C#.
    return v_float(fmod(v_as_double(a), divisor));
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
    require_numbers(a, b, "**");
    return v_float(pow(v_as_double(a), v_as_double(b)));
}

Value tern_band(Value a, Value b) { return v_int(require_int_op(a, "&") & require_int_op(b, "&")); }
Value tern_bor(Value a, Value b) { return v_int(require_int_op(a, "|") | require_int_op(b, "|")); }
Value tern_bxor(Value a, Value b) { return v_int(require_int_op(a, "^") ^ require_int_op(b, "^")); }

// The left operand is converted first, so a bad pair is blamed on the same
// operand the interpreter blames it on.
Value tern_shl(Value a, Value b) {
    int64_t value = require_int_op(a, "<<");
    int64_t shift = require_int_op(b, "<<");
    return v_int((int64_t)((uint64_t)value << (shift & 63)));
}

Value tern_shr(Value a, Value b) {
    int64_t value = require_int_op(a, ">>");
    int64_t shift = require_int_op(b, ">>");
    return v_int(value >> (shift & 63));
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

Value tern_bnot(Value a) {
    if (a.tag != T_INT) {
        tern_panic("TypeError", "bitwise not requires an integer");
    }
    return v_int(~a.as.i);
}

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
    // Lists compare element by element, then by length, like the interpreter.
    if (v_is_list(a) && v_is_list(b)) {
        List *x = (List *)a.as.o, *y = (List *)b.as.o;
        int32_t shared = x->count < y->count ? x->count : y->count;
        for (int32_t i = 0; i < shared; i++) {
            Value cmp_value = tern_compare(x->items[i], y->items[i]);
            if (cmp_value.as.i != 0) {
                return cmp_value;
            }
        }
        return v_int(x->count < y->count ? -1 : (x->count > y->count ? 1 : 0));
    }
    tern_panic("TypeError", "cannot compare %s with %s", tern_type_name(a), tern_type_name(b));
    return v_nil();
}

// ------------------------------------------------------------------ access

// Defined with the range methods below; `len(range)` and `x in range` need them
// before they are defined.
static int64_t range_length(const Range *range);
static bool range_contains(const Range *range, int64_t value);

int64_t tern_length(Value v) {
    if (v_is_str(v)) {
        return str_units((Str *)v.as.o);
    }
    if (v_is_list(v)) {
        return ((List *)v.as.o)->count;
    }
    if (v_is_map(v)) {
        return ((Map *)v.as.o)->count;
    }
    if (v.tag == T_OBJ && v.as.o->type == O_RANGE) {
        return (int32_t)range_length((Range *)v.as.o);
    }
    tern_panic("TypeError", "%s has no member 'length'", tern_type_name(v));
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

// Defined with the globals table below; calls check it before entering.
static void check_arity(const Native *native, int argc);

Value tern_call(Value callee, int argc, Value *argv) {
    if (callee.tag == T_OBJ) {
        switch (callee.as.o->type) {
            case O_CLOSURE: {
                Closure *closure = (Closure *)callee.as.o;
                if (++tern_call_depth > TERN_MAX_CALL_DEPTH) {
                    tern_call_depth--;
                    tern_panic("StackOverflow", "call depth exceeded %d", TERN_MAX_CALL_DEPTH);
                }
                TernFrame frame;
                tern_frame_enter(&frame, closure->proto);
                Value result = closure->proto->fn(closure, argc, argv);
                tern_frame_leave(&frame);
                tern_call_depth--;
                return result;
            }
            case O_NATIVE: {
                Native *native = (Native *)callee.as.o;
                check_arity(native, argc);
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
    // Its Native carries the receiver-inclusive contract, which is what
    // tern_call sees once the receiver is prepended.
    const MethodSlot *method = builtin_slot(target, name);
    if (method) {
        Value callee = make_native_arity(name, method->fn, method->min_args, method->max_args);
        Native *native = (Native *)callee.as.o;
        (void)native;
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
                return list_get((List *)target.as.o, require_index(index, "list"));
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
                int64_t position = require_index(index, "string");
                int64_t written = position;
                int64_t units = str_units(s);
                if (position < 0) {
                    position += units;
                }
                if (position < 0 || position >= units) {
                    tern_panic("IndexError", "index %lld out of range for string of length %lld",
                               (long long)written, (long long)units);
                }
                uint32_t offset = str_offset_of_unit(s, position);
                int size;
                utf8_decode(s->chars + offset, s->chars + s->len, &size);
                return str_value(s->chars + offset, (size_t)size);
            }
            case O_RANGE: {
                Range *range = (Range *)target.as.o;
                if (index.tag != T_INT) {
                    tern_panic("TypeError", "range index must be an integer, not %s",
                               tern_type_name(index));
                }
                int64_t written = index.as.i;
                int64_t offset = written;
                int64_t length = range_length(range);
                if (offset < 0) {
                    offset += length;
                }
                if (offset < 0 || offset >= length) {
                    tern_panic("IndexError", "index %lld out of range for range of length %lld",
                               (long long)written, (long long)length);
                }
                return v_int(range->start + offset * range->step);
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
                list_set((List *)target.as.o, require_index(index, "list"), value);
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
            int64_t current = range->start + (int64_t)it->index * range->step;
            bool done = range->step > 0
                ? (range->inclusive ? current > range->end : current >= range->end)
                : (range->inclusive ? current < range->end : current <= range->end);
            if (done) {
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
            int size;
            utf8_decode(s->chars + it->index, s->chars + s->len, &size);
            *first = str_value(s->chars + it->index, (size_t)size);
            it->index += size;
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
    joined->units = 0;
    joined->chars[position] = '\0';
    joined->hash = hash_bytes(joined->chars, joined->len);
    return v_obj((Obj *)joined);
}

Value tern_slice(Value target, Value start, Value end, bool inclusive) {
    if (v_is_list(target)) {
        List *list = (List *)target.as.o;
        int64_t from = start.tag == T_NIL ? 0 : need_int(start, "slice", 2);
        int64_t to = end.tag == T_NIL ? list->count : need_int(end, "slice", 3) + (inclusive ? 1 : 0);
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
        int64_t units = str_units(s);
        int64_t from = start.tag == T_NIL ? 0 : need_int(start, "slice", 2);
        int64_t to = end.tag == T_NIL ? units : need_int(end, "slice", 3) + (inclusive ? 1 : 0);
        if (from < 0) from += units;
        if (to < 0) to += units;
        if (from < 0) from = 0;
        if (to > units) to = units;
        if (to < from) to = from;
        uint32_t begin = str_offset_of_unit(s, from);
        uint32_t finish = str_offset_of_unit(s, to);
        return str_value(s->chars + begin, (size_t)(finish - begin));
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
        if (item.tag != T_INT) {
            return false;
        }
        return range_contains((Range *)container.as.o, item.as.i);
    }
    tern_panic("TypeError", "cannot use 'in' on a %s", tern_type_name(container));
    return false;
}

// ------------------------------------------------------------------ natives

static Value native_string(Value v) { return tern_to_string(v); }

// Builtins that cast an argument report it the way the interpreter's wrapped
// InvalidCastException does: "keys(): argument is not of the expected type".
static void bad_argument(const char *name) {
    tern_panic("TypeError", "%s(): argument is not of the expected type", name);
}

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
    return need_int(arg(argc, argv, index), what, index + 1);
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

static Value n_len(int argc, Value *argv) {
    Value v = arg(argc, argv, 0);
    switch (v.tag) {
        case T_OBJ:
            if (v.as.o->type == O_STR || v.as.o->type == O_LIST || v.as.o->type == O_MAP ||
                v.as.o->type == O_RANGE) {
                return v_int(tern_length(v));
            }
            break;
        default:
            break;
    }
    tern_panic("TypeError", "len() is not defined for %s", ((Str *)tern_to_debug(v))->chars);
    return v_int(0);
}

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
        return v_int(f64_to_i64(v.as.f));
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
    Value arg0 = arg(argc, argv, 0);
    if (!v_is_str(arg0)) {
        bad_argument("ord");
    }
    Str *s = (Str *)arg0.as.o;
    if (s->len == 0) {
        tern_panic("ArgumentError", "ord() requires a non-empty string");
    }
    int size;
    return v_int((int64_t)utf8_decode(s->chars, s->chars + s->len, &size));
}

// The only way to make a range: `tern_alloc` does not zero, so every field has
// to be written here rather than at each construction site.
static Range *range_make(int64_t start, int64_t end, bool inclusive, int64_t step) {
    Range *range = (Range *)tern_alloc(sizeof(Range), O_RANGE);
    range->start = start;
    range->end = end;
    range->step = step == 0 ? 1 : step;
    range->inclusive = inclusive;
    return range;
}

static Value n_range(int argc, Value *argv) {
    int64_t start = 0, end = 0;
    if (argc >= 2) {
        start = iarg(argc, argv, 0, "range");
        end = iarg(argc, argv, 1, "range");
    } else {
        end = iarg(argc, argv, 0, "range");
    }
    int64_t step = argc > 2 ? iarg(argc, argv, 2, "range") : 1;
    return v_obj((Obj *)range_make(start, end, false, step));
}

static Value n_keys(int argc, Value *argv) {
    Value v = arg(argc, argv, 0);
    if (!v_is_map(v)) {
        bad_argument("keys");
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
        bad_argument("values");
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
        bad_argument("has");
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
    return v_float(fabs(v_as_number(v)));
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

static Value m_floor(int argc, Value *argv) { return v_float(floor(v_as_number(arg(argc, argv, 0)))); }
static Value m_ceil(int argc, Value *argv) { return v_float(ceil(v_as_number(arg(argc, argv, 0)))); }
static Value m_round(int argc, Value *argv) { return v_float(round(v_as_number(arg(argc, argv, 0)))); }
static Value m_sqrt(int argc, Value *argv) { return v_float(sqrt(v_as_number(arg(argc, argv, 0)))); }
static Value m_trunc(int argc, Value *argv) { return v_float(trunc(v_as_number(arg(argc, argv, 0)))); }
static Value m_pow(int argc, Value *argv) {
    return v_float(pow(v_as_number(arg(argc, argv, 0)), v_as_number(arg(argc, argv, 1))));
}
static Value m_abs(int argc, Value *argv) { return n_abs(argc, argv); }
static Value m_min(int argc, Value *argv) { return n_min(argc, argv); }
static Value m_max(int argc, Value *argv) { return n_max(argc, argv); }
static Value m_sign(int argc, Value *argv) {
    double v = v_as_number(arg(argc, argv, 0));
    return v_int(v < 0 ? -1 : (v > 0 ? 1 : 0));
}
static Value m_clamp(int argc, Value *argv) {
    Value value = arg(argc, argv, 0);
    double low = v_as_number(arg(argc, argv, 1));
    double high = v_as_number(arg(argc, argv, 2));
    double clamped = v_as_number(value) < low ? low
                    : (v_as_number(value) > high ? high : v_as_number(value));
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
#ifdef _WIN32
    return (double)GetTickCount64();
#else
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (double)ts.tv_sec * 1000.0 + (double)ts.tv_nsec / 1e6;
#endif
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

// Case conversion walks code points and uses the C library's Unicode tables
// (the runtime calls setlocale(LC_CTYPE, "C.UTF-8") at start-up), so "héllo"
// upper-cases to "HÉLLO" and CJK text passes through untouched. A code point
// whose uppercase form is a different length (ß -> SS) is handled by encoding
// the mapped code point only, which matches the interpreter for every
// single-code-point mapping.
static Value case_map(Value self, bool upper) {
    Str *s = (Str *)self.as.o;

    // Fast path: pure ASCII maps one byte to one byte, so the result is exact
    // and needs no buffer growth.
    bool ascii = true;
    for (uint32_t i = 0; i < s->len; i++) {
        if ((unsigned char)s->chars[i] >= 0x80) {
            ascii = false;
            break;
        }
    }
    if (ascii) {
        Str *out = (Str *)tern_alloc(sizeof(Str) + s->len + 1, O_STR);
        for (uint32_t i = 0; i < s->len; i++) {
            char c = s->chars[i];
            if (upper && c >= 'a' && c <= 'z') {
                c = (char)(c - 32);
            } else if (!upper && c >= 'A' && c <= 'Z') {
                c = (char)(c + 32);
            }
            out->chars[i] = c;
        }
        out->len = s->len;
        out->units = s->len;      // one byte per code unit
        out->chars[s->len] = '\0';
        out->hash = hash_bytes(out->chars, out->len);
        return v_obj((Obj *)out);
    }

    Buf buf;
    buf_init(&buf);
    const char *p = s->chars;
    const char *end = s->chars + s->len;
    while (p < end) {
        unsigned char lead = (unsigned char)*p;
        if (lead < 0x80) {
            // ASCII fast path: the overwhelming majority of text, and towupper
            // through the locale tables would dominate the cost otherwise.
            char mapped = (char)lead;
            if (upper && lead >= 'a' && lead <= 'z') {
                mapped = (char)(lead - 32);
            } else if (!upper && lead >= 'A' && lead <= 'Z') {
                mapped = (char)(lead + 32);
            }
            buf_add_char(&buf, mapped);
            p++;
            continue;
        }

        int size;
        uint32_t code = utf8_decode(p, end, &size);
        wint_t mapped = upper ? towupper((wint_t)code) : towlower((wint_t)code);
        if (mapped != 0 && mapped != (wint_t)code && mapped <= 0x10FFFF) {
            code = (uint32_t)mapped;
        }
        char bytes[4];
        int written = utf8_encode(code, bytes);
        buf_add(&buf, bytes, (size_t)written);
        p += size;
    }
    return buf_finish(&buf);
}

static Value s_upper(int argc, Value *argv) { return case_map(argv[0], true); }
static Value s_lower(int argc, Value *argv) { return case_map(argv[0], false); }

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
    if (!found) {
        return v_int(-1);
    }
    return v_int(str_unit_of_offset(s, (uint32_t)(found - s->chars)));
}

static Value s_lastindexof(int argc, Value *argv) {
    Str *s = self_str(argc, argv);
    Str *needle = as_str(arg(argc, argv, 1), "lastIndexOf");
    if (needle->len > s->len) {
        return v_int(-1);
    }
    for (int64_t at = (int64_t)s->len - (int64_t)needle->len; at >= 0; at--) {
        if (memcmp(s->chars + at, needle->chars, needle->len) == 0) {
            return v_int(str_unit_of_offset(s, (uint32_t)at));
        }
    }
    return v_int(-1);
}

static Value s_substring(int argc, Value *argv) {
    Str *s = self_str(argc, argv);
    int64_t units = str_units(s);
    int64_t from = iarg(argc, argv, 1, "substring");
    int64_t to = argc > 2 && argv[2].tag != T_NIL ? iarg(argc, argv, 2, "substring") : units;
    if (from < 0) from += units;
    if (to < 0) to += units;
    if (from < 0) from = 0;
    if (to > units) to = units;
    if (to < from) to = from;

    // Indices count code units, so a multi-byte character is never cut in two.
    uint32_t begin = str_offset_of_unit(s, from);
    uint32_t finish = str_offset_of_unit(s, to);
    return str_value(s->chars + begin, (size_t)(finish - begin));
}

static Value s_slice(int argc, Value *argv) {
    Str *s = self_str(argc, argv);
    int64_t units = str_units(s);

    // Both bounds are optional and count code units, like len() and indexing.
    // A bound that is passed must be an integer: nil is not short for "absent".
    int64_t from = 0;
    int64_t to = units;
    if (argc > 1) {
        from = need_int(argv[1], "slice", 2);
    }
    if (argc > 2) {
        to = need_int(argv[2], "slice", 3);
    }
    if (from < 0) from += units;
    if (to < 0) to += units;
    if (from < 0) from = 0;
    if (to > units) to = units;
    if (to < from) to = from;

    uint32_t begin = str_offset_of_unit(s, from);
    uint32_t finish = str_offset_of_unit(s, to);
    return str_value(s->chars + begin, (size_t)(finish - begin));
}

static Value s_split(int argc, Value *argv) {
    Str *s = self_str(argc, argv);
    List *parts = list_new(8);

    // The default separator is a single space, not "any whitespace": the
    // interpreter leaves empty pieces in place ("a  b".split() has three).
    Value separator_value = argc > 1 ? argv[1] : str_value(" ", 1);
    Str *separator = as_str(separator_value, "split");

    if (separator->len == 0) {
        // Empty separator: one piece per character, whole code points only.
        for (uint32_t i = 0; i < s->len; ) {
            int size;
            utf8_decode(s->chars + i, s->chars + s->len, &size);
            list_push(parts, str_value(s->chars + i, (size_t)size));
            i += (uint32_t)size;
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
    if (times < 0) {
        tern_panic("ArgumentError", "repeat count cannot be negative");
    }
    if (times == 0) {
        return str_value("", 0);
    }
    size_t total = (size_t)times * s->len;
    Str *out = (Str *)tern_alloc(sizeof(Str) + total + 1, O_STR);
    for (int64_t i = 0; i < times; i++) {
        memcpy(out->chars + i * s->len, s->chars, s->len);
    }
    out->len = (uint32_t)total;
    out->units = 0;
    out->chars[total] = '\0';
    out->hash = hash_bytes(out->chars, out->len);
    return v_obj((Obj *)out);
}

static Value s_chars(int argc, Value *argv) {
    Str *s = self_str(argc, argv);
    List *chars = list_new((int)s->len);
    for (uint32_t i = 0; i < s->len; ) {
        int size;
        utf8_decode(s->chars + i, s->chars + s->len, &size);
        list_push(chars, str_value(s->chars + i, (size_t)size));
        i += (uint32_t)size;
    }
    return v_obj((Obj *)chars);
}

// `words()` is not `split()`: it splits on any whitespace and drops the empty
// pieces, so "" has no words while "".split() has one empty field.
static Value s_words(int argc, Value *argv) {
    Str *s = self_str(argc, argv);
    List *words = list_new(8);
    uint32_t i = 0;
    while (i < s->len) {
        while (i < s->len && is_space(s->chars[i])) i++;
        uint32_t start = i;
        while (i < s->len && !is_space(s->chars[i])) i++;
        if (i > start) {
            list_push(words, str_value(s->chars + start, i - start));
        }
    }
    return v_obj((Obj *)words);
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
            if (end > start) {
                list_push(lines, str_value(s->chars + start, end - start));
            }
            start = i + 1;
        }
    }
    return v_obj((Obj *)lines);
}

static Value s_isempty(int argc, Value *argv) { return v_bool(self_str(argc, argv)->len == 0); }

// `toInt([fallback])`: parse the whole trimmed text or use the fallback, and
// raise when neither applies — strtoll alone would answer 0 for "abc".
static Value s_toint(int argc, Value *argv) {
    Str *s = self_str(argc, argv);
    const char *start = s->chars;
    const char *end = s->chars + s->len;
    while (start < end && (*start == ' ' || *start == '\t' || *start == '\n' || *start == '\r')) start++;
    while (end > start && (end[-1] == ' ' || end[-1] == '\t' || end[-1] == '\n' || end[-1] == '\r')) end--;

    char *consumed = NULL;
    errno = 0;
    long long value = strtoll(start, &consumed, 10);
    if (start < end && consumed == end && errno != ERANGE) {
        return v_int((int64_t)value);
    }
    if (argc > 1) {
        return argv[1];
    }
    tern_panic("ArgumentError", "cannot parse '%.*s' as an integer", (int)(end - start), start);
    return v_nil();
}

static Value s_tofloat(int argc, Value *argv) {
    Str *s = self_str(argc, argv);
    const char *start = s->chars;
    const char *end = s->chars + s->len;
    while (start < end && (*start == ' ' || *start == '\t' || *start == '\n' || *start == '\r')) start++;
    while (end > start && (end[-1] == ' ' || end[-1] == '\t' || end[-1] == '\n' || end[-1] == '\r')) end--;

    char *consumed = NULL;
    double number = strtod(start, &consumed);
    if (start < end && consumed == end) {
        return v_float(number);
    }
    if (argc > 1) {
        return argv[1];
    }
    tern_panic("ArgumentError", "cannot parse '%.*s' as a number", (int)(end - start), start);
    return v_nil();
}

static Value s_tostring(int argc, Value *argv) {
    (void)argc;
    return argv[0];
}

static Value s_pad(int argc, Value *argv, bool start, const char *name) {
    Str *s = self_str(argc, argv);
    int64_t width = iarg(argc, argv, 1, name);
    Str *fill = NULL;
    if (argc > 2) {
        Str *candidate = as_str(argv[2], name);
        if (candidate->len > 0) {
            fill = candidate;
        }
    }

    int64_t units = str_units(s);
    if (width <= units) {
        return argv[0];
    }

    // The fill can be any (multi-byte, multi-code-point) string: repeat it
    // until the padding reaches the requested number of code units, copying
    // whole characters only.
    int64_t need = width - units;
    Buf pad;
    buf_init(&pad);
    if (fill) {
        int64_t produced = 0;
        while (produced < need) {
            for (uint32_t i = 0; i < fill->len && produced < need; ) {
                int size;
                utf8_decode(fill->chars + i, fill->chars + fill->len, &size);
                if (produced + utf16_width(utf8_decode(fill->chars + i, fill->chars + fill->len, &size)) > need) {
                    break;
                }
                buf_add(&pad, fill->chars + i, (size_t)size);
                produced += utf16_width(utf8_decode(fill->chars + i, fill->chars + fill->len, &size));
                i += (uint32_t)size;
            }
            if (fill->len == 0) {
                break;
            }
        }
    } else {
        for (int64_t i = 0; i < need; i++) {
            buf_add_char(&pad, ' ');
        }
    }

    Str *out = (Str *)tern_alloc(sizeof(Str) + pad.len + s->len + 1, O_STR);
    size_t position = 0;
    if (start) {
        memcpy(out->chars, pad.data, pad.len);
        position = pad.len;
    }
    memcpy(out->chars + position, s->chars, s->len);
    position += s->len;
    if (!start) {
        memcpy(out->chars + position, pad.data, pad.len);
        position += pad.len;
    }
    free(pad.data);

    out->len = (uint32_t)position;
    out->units = 0;
    out->chars[position] = '\0';
    out->hash = hash_bytes(out->chars, out->len);
    return v_obj((Obj *)out);
}

static Value s_padstart(int argc, Value *argv) { return s_pad(argc, argv, true, "padStart"); }
static Value s_padend(int argc, Value *argv) { return s_pad(argc, argv, false, "padEnd"); }

static Value s_reversed(int argc, Value *argv) {
    Str *s = self_str(argc, argv);
    Buf buf;
    buf_init(&buf);
    // Walk forward recording each code point's start, then emit them backwards:
    // reversing bytes would shred multi-byte characters.
    uint32_t *starts = (uint32_t *)malloc(sizeof(uint32_t) * (s->len + 1));
    int count = 0;
    for (uint32_t i = 0; i < s->len; ) {
        starts[count++] = i;
        int size;
        utf8_decode(s->chars + i, s->chars + s->len, &size);
        i += (uint32_t)size;
    }
    for (int i = count - 1; i >= 0; i--) {
        uint32_t from = starts[i];
        uint32_t to = i + 1 < count ? starts[i + 1] : s->len;
        buf_add(&buf, s->chars + from, to - from);
    }
    free(starts);
    return buf_finish(&buf);
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
    List *list = self_list(argc, argv);
    for (int i = 1; i < argc; i++) {
        list_push(list, argv[i]);
    }
    // The new length, like the interpreter (not the list itself).
    return v_int(list->count);
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
    int added = argc - 1;
    if (added > 0) {
        list_reserve(list, list->count + added);
        memmove(list->items + added, list->items, (size_t)list->count * sizeof(Value));
        for (int i = 0; i < added; i++) {
            list->items[i] = argv[i + 1];
        }
        list->count += added;
    }
    return v_int(list->count);
}

static Value l_join(int argc, Value *argv) {
    List *list = self_list(argc, argv);
    const char *separator = ",";
    size_t separator_len = 1;
    if (argc > 1) {
        // Any value works as a separator: it is rendered, not required to be a
        // string, exactly like the items themselves.
        Str *rendered = (Str *)tern_to_string(argv[1]).as.o;
        separator = rendered->chars;
        separator_len = rendered->len;
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
    Value start = argc > 1 ? v_int(need_int(argv[1], "slice", 2)) : v_nil();
    Value end = argc > 2 ? v_int(need_int(argv[2], "slice", 3)) : v_nil();
    return tern_slice(argv[0], start, end, false);
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
    if (argc > 1) {
        // Comparator-driven sort (insertion sort: simple and stable).
        Value comparator = argv[1];
        for (int32_t i = 1; i < list->count; i++) {
            Value key = list->items[i];
            int32_t j = i - 1;
            while (j >= 0) {
                Value pair[2] = {key, list->items[j]};
                Value result = tern_call(comparator, 2, pair);
                if (v_is_num(result) && v_as_double(result) >= 0) {
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
    if (at < 0 || at > list->count) {
        tern_panic("IndexError", "insert index %lld out of range for list of length %d",
                   (long long)at, list->count);
    }
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



// --- number methods --------------------------------------------------------

static Value num_abs(Value v) {
    if (v.tag == T_INT) return v.as.i < 0 ? v_int(-v.as.i) : v;
    return v_float(fabs(v_as_number(v)));
}

static Value n_abs_self(int argc, Value *argv) { (void)argc; return num_abs(argv[0]); }

// `5.clamp(1, 9)` keeps the integer type and needs integer bounds;
// `2.5.clamp(0.0, 1.0)` accepts any numbers.
static Value n_clamp_self(int argc, Value *argv) {
    Value v = argv[0];
    if (v.tag == T_INT) {
        int64_t value = v.as.i;
        int64_t low = need_int(arg(argc, argv, 1), "clamp", 2);
        int64_t high = need_int(arg(argc, argv, 2), "clamp", 3);
        return v_int(value < low ? low : (value > high ? high : value));
    }

    double value = v_as_double(v);
    double low = need_number(arg(argc, argv, 1), "clamp", 2);
    double high = need_number(arg(argc, argv, 2), "clamp", 3);
    if (value < low) value = low;
    if (value > high) value = high;
    return v_float(value);
}

static Value n_round_self(int argc, Value *argv) {
    // The interpreter rounds half away from zero and returns a float, so a
    // compiled program must not disagree about either.
    Value v = argv[0];
    int digits = argc > 1 ? (int)need_int(argv[1], "round", 2) : 0;
    double value = v_as_double(v);
    if (digits <= 0) return v_float(round(value));
    double scale = pow(10.0, digits);
    return v_float(round(value * scale) / scale);
}

static Value n_toint_self(int argc, Value *argv) {
    (void)argc;
    Value v = argv[0];
    return v_int(v.tag == T_INT ? v.as.i : f64_to_i64(v.as.f));
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
    int64_t count = need_int(argv[0], "times", 1);
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

// ------------------------------------------------------------------- ranges
//
// A range is start, end and step; `range(0, 10, 3)` walks 0, 3, 6, 9 and a
// negative step walks backwards, exactly like the interpreter's.

static int64_t range_length(const Range *range) {
    int64_t span = range->step > 0
        ? (range->inclusive ? range->end - range->start + 1 : range->end - range->start)
        : (range->inclusive ? range->start - range->end + 1 : range->start - range->end);
    if (span <= 0) {
        return 0;
    }
    int64_t step = range->step < 0 ? -range->step : range->step;
    return (span + step - 1) / step;
}

static bool range_contains(const Range *range, int64_t value) {
    if (range->step > 0) {
        if (value < range->start || (range->inclusive ? value > range->end : value >= range->end)) {
            return false;
        }
    } else {
        if (value > range->start || (range->inclusive ? value < range->end : value <= range->end)) {
            return false;
        }
    }
    return (value - range->start) % range->step == 0;
}

static Value r_tolist(int argc, Value *argv) {
    (void)argc;
    Range *range = (Range *)argv[0].as.o;
    int64_t count = range_length(range);
    List *list = list_new((int)count);
    for (int64_t i = 0, value = range->start; i < count; i++, value += range->step) {
        list_push(list, v_int(value));
    }
    return v_obj((Obj *)list);
}

static Value r_contains(int argc, Value *argv) {
    Value needle = arg(argc, argv, 1);
    if (needle.tag != T_INT) return v_bool(false);
    Range *range = (Range *)argv[0].as.o;
    return v_bool(range_contains(range, needle.as.i));
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
    // list.each returns nil, so range.each does too.
    return v_nil();
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
    int depth = argc > 1 ? (int)need_int(argv[1], "flatten", 2) : 1;
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
    int64_t count = need_int(arg(argc, argv, 1), "take", 2);
    if (count < 0) count = 0;
    if (count > list->count) count = list->count;
    List *out = list_new((int)count);
    for (int64_t i = 0; i < count; i++) list_push(out, list->items[i]);
    return v_obj((Obj *)out);
}

static Value l_drop(int argc, Value *argv) {
    List *list = require_list(argv[0], "drop");
    int64_t count = need_int(arg(argc, argv, 1), "drop", 2);
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
            memmove(&list->items[i], &list->items[i + 1],
                    (size_t)(list->count - i - 1) * sizeof(Value));
            list->count--;
            return v_bool(true);
        }
    }
    return v_bool(false);
}

static Value m_tostring_self(int argc, Value *argv) {
    (void)argc;
    return tern_to_string(argv[0]);
}

// merge is the interpreter's `+`: a map plus a map merges, a map plus a string
// concatenates, and anything else is the operator's own type error.
static Value m_merge(int argc, Value *argv) {
    return tern_add(argv[0], arg(argc, argv, 1));
}

// --- lists namespace -------------------------------------------------------

static Value ln_range(int argc, Value *argv) {
    int64_t start = 0, end;
    if (argc >= 2) {
        start = need_int(argv[0], "range", 1);
        end = need_int(argv[1], "range", 2);
    } else {
        end = need_int(arg(argc, argv, 0), "range", 1);
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
    int64_t size = need_int(arg(argc, argv, 1), "chunk", 2);
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
    int count = (int)need_int(arg(argc, argv, 1), "repeat", 2);
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
    int width = (int)need_int(arg(argc, argv, 1), "pad", 2);
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
    rng_state = (uint64_t)need_int(arg(argc, argv, 0), "seed", 1) * 2685821657736338717ULL + 1;
    return v_nil();
}

// `random.float([low, high])`: a value in [low, high), defaulting to [0, 1).
static Value rnd_float(int argc, Value *argv) {
    double low = argc > 0 ? v_as_number(arg(argc, argv, 0)) : 0.0;
    double high = argc > 1 ? v_as_number(argv[1]) : 1.0;
    double unit = (double)(rng_next() >> 11) / 9007199254740992.0;
    return v_float(low + unit * (high - low));
}

static Value rnd_int(int argc, Value *argv) {
    int64_t low = need_int(arg(argc, argv, 0), "int", 1);
    int64_t high = argc > 1 ? need_int(argv[1], "int", 2) : low;
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
    return v_as_number(arg(argc, argv, index));
}

static Value m_lcm(int argc, Value *argv) {
    int64_t a = need_int(arg(argc, argv, 0), "lcm", 1);
    int64_t b = need_int(arg(argc, argv, 1), "lcm", 2);
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
        // Insertion order, like every other map traversal (and the interpreter):
        // walking the hash slots instead would reorder the document.
        bool first = true;
        for (int32_t i = 0; i < map->order_count; i++) {
            Value value;
            if (!map_get(map, map->order[i], &value)) continue;
            if (!first) buf_add_char(buf, ',');
            first = false;
            if (indent) {
                buf_add_char(buf, '\n');
                for (int k = 0; k < (level + 1) * indent; k++) buf_add_char(buf, ' ');
            }
            Value key = tern_to_string(map->order[i]);
            json_escape(buf, (Str *)key.as.o);
            buf_add_char(buf, ':');
            if (indent) buf_add_char(buf, ' ');
            json_write(buf, value, indent, level + 1);
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
    int failed;          // set on the first syntax error; parsing stops there
    char message[128];   // the error text, formatted where the failure happened
} JsonParser;

// Parsing is recursive, so the depth is bounded: past this the interpreter
// would die of a stack overflow rather than raise a catchable error.
#define JSON_MAX_DEPTH 512

static Value json_parse_value(JsonParser *p, int depth);

static void json_skip(JsonParser *p) {
    while (p->pos < p->length) {
        char c = p->text[p->pos];
        if (c == ' ' || c == '\t' || c == '\n' || c == '\r') { p->pos++; continue; }
        break;
    }
}

/// Records a syntax error and returns nil, so callers can just `return`.
/// The message is formatted on the spot: it carries the offset where the
/// problem was found, which later moves `pos` out of reach. The wording
/// matches JsonParser in TERN/Builtins/StandardLibrary.cs.
static Value json_failf(JsonParser *p, const char *format, ...) {
    p->failed = 1;
    va_list args;
    va_start(args, format);
    vsnprintf(p->message, sizeof p->message, format, args);
    va_end(args);
    return v_nil();
}

static JsonParser json_parser_new(Str *text) {
    JsonParser parser;
    parser.text = text->chars;
    parser.length = (int)text->len;
    parser.pos = 0;
    parser.failed = 0;
    parser.message[0] = '\0';
    return parser;
}

/// `true`, `false` and `null` are matched whole; a prefix that runs off the end
/// of the text fails like any other wrong literal.
static Value json_literal(JsonParser *p, const char *literal, Value result) {
    size_t length = strlen(literal);
    if (p->pos + (int)length > p->length ||
        memcmp(p->text + p->pos, literal, length) != 0) {
        return json_failf(p, "invalid literal at offset %d", p->pos);
    }
    p->pos += (int)length;
    return result;
}

/// Numbers are scanned first and parsed second, so `1-2` is one bad number
/// rather than a 1 followed by trailing content.
static Value json_parse_number(JsonParser *p, char first) {
    const char *start = p->text + p->pos;
    int begin = p->pos;
    if (first == '-' || first == '+') {
        p->pos++;
    }

    bool is_float = false;
    while (p->pos < p->length) {
        char c = p->text[p->pos];
        if (c == '.' || c == 'e' || c == 'E') {
            is_float = true;
        } else if (c != '+' && c != '-' && (c < '0' || c > '9')) {
            break;
        }
        p->pos++;
    }

    int length = p->pos - begin;
    if (length == 0) {
        return json_failf(p, "unexpected character '%c' at offset %d", first, begin);
    }

    char *consumed = NULL;
    if (!is_float) {
        errno = 0;
        long long whole = strtoll(start, &consumed, 10);
        if (consumed == start + length && errno != ERANGE) {
            return v_int((int64_t)whole);
        }
    }

    errno = 0;
    double number = strtod(start, &consumed);
    if (consumed == start + length) {
        return v_float(number);
    }

    return json_failf(p, "invalid number '%.*s'", length, start);
}

static Value json_parse_string(JsonParser *p) {
    if (p->text[p->pos] != '"') {
        return json_failf(p, "expected a string key at offset %d", p->pos);
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
                    return json_failf(p, "invalid unicode escape");
                }
                char hex[5];
                memcpy(hex, p->text + p->pos, 4);
                hex[4] = 0;
                char *hex_end = NULL;
                long code = strtol(hex, &hex_end, 16);
                if (!hex_end || hex_end != hex + 4) {
                    return json_failf(p, "invalid unicode escape");
                }
                p->pos += 4;

                // A high surrogate followed by a low one is a single code point;
                // encoded apart they would be two invalid sequences.
                if (code >= 0xD800 && code <= 0xDBFF && p->pos + 6 <= p->length &&
                    p->text[p->pos] == '\\' && p->text[p->pos + 1] == 'u') {
                    char low_hex[5];
                    memcpy(low_hex, p->text + p->pos + 2, 4);
                    low_hex[4] = 0;
                    char *low_end = NULL;
                    long low = strtol(low_hex, &low_end, 16);
                    if (low_end && low_end == low_hex + 4 && low >= 0xDC00 && low <= 0xDFFF) {
                        p->pos += 6;
                        code = 0x10000 + ((code - 0xD800) << 10) + (low - 0xDC00);
                    }
                }

                // An unpaired surrogate is not a code point: it cannot be encoded
                // as UTF-8, so it becomes the replacement character, which is
                // also what the interpreter prints for one.
                if (code >= 0xD800 && code <= 0xDFFF) {
                    code = 0xFFFD;
                }

                if (code < 0x80) {
                    buf_add_char(&buf, (char)code);
                } else if (code < 0x800) {
                    buf_add_char(&buf, (char)(0xC0 | (code >> 6)));
                    buf_add_char(&buf, (char)(0x80 | (code & 0x3F)));
                } else if (code < 0x10000) {
                    buf_add_char(&buf, (char)(0xE0 | (code >> 12)));
                    buf_add_char(&buf, (char)(0x80 | ((code >> 6) & 0x3F)));
                    buf_add_char(&buf, (char)(0x80 | (code & 0x3F)));
                } else {
                    buf_add_char(&buf, (char)(0xF0 | (code >> 18)));
                    buf_add_char(&buf, (char)(0x80 | ((code >> 12) & 0x3F)));
                    buf_add_char(&buf, (char)(0x80 | ((code >> 6) & 0x3F)));
                    buf_add_char(&buf, (char)(0x80 | (code & 0x3F)));
                }
                break;
            }
            default:
                return json_failf(p, "invalid escape '\\%c'", esc);
        }
    }
    return json_failf(p, "unterminated string");
}

static Value json_parse_object(JsonParser *p, int depth) {
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
            return json_failf(p, "expected a string key at offset %d", p->pos);
        }
        Value key = json_parse_string(p);
        if (p->failed) {
            return v_nil();
        }

        json_skip(p);
        if (p->pos >= p->length || p->text[p->pos] != ':') {
            return json_failf(p, "expected ':' at offset %d", p->pos);
        }
        p->pos++;

        Value value = json_parse_value(p, depth + 1);
        if (p->failed) {
            return v_nil();
        }
        map_set(map, key, value);

        json_skip(p);
        if (p->pos >= p->length) {
            return json_failf(p, "unexpected end of object");
        }
        if (p->text[p->pos] == ',') {
            p->pos++;
            continue;
        }
        if (p->text[p->pos] == '}') {
            p->pos++;
            return v_obj((Obj *)map);
        }
        return json_failf(p, "expected ',' or '}' at offset %d", p->pos);
    }
    return v_nil();
}

static Value json_parse_array(JsonParser *p, int depth) {
    List *list = list_new(4);
    p->pos++;   // [
    json_skip(p);
    if (p->pos < p->length && p->text[p->pos] == ']') {
        p->pos++;
        return v_obj((Obj *)list);
    }

    while (!p->failed) {
        Value item = json_parse_value(p, depth + 1);
        if (p->failed) {
            return v_nil();
        }
        list_push(list, item);

        json_skip(p);
        if (p->pos >= p->length) {
            return json_failf(p, "unexpected end of array");
        }
        if (p->text[p->pos] == ',') {
            p->pos++;
            continue;
        }
        if (p->text[p->pos] == ']') {
            p->pos++;
            return v_obj((Obj *)list);
        }
        return json_failf(p, "expected ',' or ']' at offset %d", p->pos);
    }
    return v_nil();
}

static Value json_parse_value(JsonParser *p, int depth) {
    if (depth > JSON_MAX_DEPTH) {
        return json_failf(p, "JSON nested too deeply (limit %d)", JSON_MAX_DEPTH);
    }
    json_skip(p);
    if (p->pos >= p->length) {
        return json_failf(p, "unexpected end of input");
    }

    char c = p->text[p->pos];
    if (c == '{') {
        return json_parse_object(p, depth);
    }
    if (c == '[') {
        return json_parse_array(p, depth);
    }
    if (c == '"') {
        return json_parse_string(p);
    }
    if (c == 't') {
        return json_literal(p, "true", v_bool(true));
    }
    if (c == 'f') {
        return json_literal(p, "false", v_bool(false));
    }
    if (c == 'n') {
        return json_literal(p, "null", v_nil());
    }
    return json_parse_number(p, c);
}

/// Runs one parse to completion. Returns false when the text is not valid
/// JSON, leaving the reason in `parser.message`.
static bool json_parse_document(JsonParser *parser, Value *result) {
    *result = json_parse_value(parser, 0);
    if (parser->failed) {
        return false;
    }
    json_skip(parser);
    if (parser->pos != parser->length) {
        json_failf(parser, "unexpected trailing content at offset %d", parser->pos);
        return false;
    }
    return true;
}

static Value js_parse(int argc, Value *argv) {
    Value text = arg(argc, argv, 0);
    if (!v_is_str(text)) {
        bad_argument("parse");
    }
    JsonParser parser = json_parser_new((Str *)text.as.o);
    Value value = v_nil();
    if (!json_parse_document(&parser, &value)) {
        tern_panic("JsonError", "%s", parser.message);
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
    JsonParser parser = json_parser_new((Str *)text.as.o);
    Value value = v_nil();
    if (!json_parse_document(&parser, &value)) {
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

// ------------------------------------------------------------------ file system
//
// `fs` is its own namespace, matching the interpreter: `os` carries no file
// operations, so a script that works under `tern run` works here too. Every
// failure raises a catchable IOError instead of returning a sentinel.

static const char *path_arg(int argc, Value *argv, int index) {
    Value value = arg(argc, argv, index);
    if (!v_is_str(value)) {
        tern_panic("TypeError", "path must be a string, got %s", tern_type_name(value));
    }
    return ((Str *)value.as.o)->chars;
}

// Creates every missing directory above `path`, so `fs.write("a/b/c.txt")`
// works without an explicit `fs.mkdir`, as it does in the interpreter.
static void make_parent_dirs(const char *path) {
    char buffer[4096];
    size_t length = strlen(path);
    if (length >= sizeof buffer) {
        tern_panic("IOError", "path too long");
    }
    memcpy(buffer, path, length + 1);
    // Skip a leading '/' so the root is never passed to mkdir as "".
    for (size_t i = 1; i < length; i++) {
        if (buffer[i] == '/') {
            buffer[i] = '\0';
            mkdir(buffer, 0777);
            buffer[i] = '/';
        }
    }
}

static void remove_tree(const char *path) {
    struct stat info;
    if (lstat(path, &info) != 0) {
        return;   // already gone: removing is idempotent
    }
    if (!S_ISDIR(info.st_mode)) {
        unlink(path);
        return;
    }
    DIR *dir = opendir(path);
    if (!dir) {
        tern_panic("IOError", "cannot remove '%s'", path);
    }
    struct dirent *entry;
    while ((entry = readdir(dir)) != NULL) {
        if (strcmp(entry->d_name, ".") == 0 || strcmp(entry->d_name, "..") == 0) {
            continue;
        }
        char child[4096];
        snprintf(child, sizeof child, "%s/%s", path, entry->d_name);
        remove_tree(child);
    }
    closedir(dir);
    rmdir(path);
}

static Value fs_read(int argc, Value *argv) {
    const char *path = path_arg(argc, argv, 0);
    FILE *handle = fopen(path, "rb");
    if (!handle) {
        tern_panic("IOError", "cannot read '%s'", path);
    }
    Buf buf;
    buf_init(&buf);
    char chunk[4096];
    size_t got;
    while ((got = fread(chunk, 1, sizeof chunk, handle)) > 0) buf_add(&buf, chunk, got);
    fclose(handle);
    return buf_finish(&buf);
}

static Value fs_readbytes(int argc, Value *argv) {
    Value text = fs_read(argc, argv);
    Str *str = (Str *)text.as.o;
    List *list = list_new((int)str->len);
    for (uint32_t i = 0; i < str->len; i++) {
        list_push(list, v_int((unsigned char)str->chars[i]));
    }
    return v_obj((Obj *)list);
}

// Reading a file in pieces. The interpreter keeps a FileStream per handle and
// this keeps a FILE*; both hand out small integers and both refuse to split a
// multi-byte character across two chunks, so the two back ends return the same
// text for the same offsets.

#define FS_MAX_HANDLES 64
#define FS_MAX_CHUNK 1073741824

static FILE *fs_handles[FS_MAX_HANDLES];
static char *fs_paths[FS_MAX_HANDLES];

static int fs_slot_arg(Value v) {
    if (v.tag != T_INT) {
        tern_panic("TypeError", "expected a file handle, not %s", tern_type_name(v));
    }
    int handle = (int)v.as.i;
    if (handle < 1 || handle > FS_MAX_HANDLES || !fs_handles[handle - 1]) {
        tern_panic("IOError", "no open file with handle %d", handle);
    }
    return handle - 1;
}

static long fs_offset_arg(Value v, const char *function, int index) {
    int64_t offset = need_int(v, function, index);
    if (offset < 0) {
        tern_panic("ArgumentError", "offset must not be negative");
    }
    return (long)offset;
}

static int fs_length_arg(Value v, const char *function, int index) {
    int64_t length = need_int(v, function, index);
    if (length < 1 || length > FS_MAX_CHUNK) {
        tern_panic("ArgumentError", "length must be between 1 and %d", FS_MAX_CHUNK);
    }
    return (int)length;
}

/// How many bytes the character at the end of the buffer still needs, read from
/// its lead byte. Zero means the chunk ends on a boundary.
static int fs_missing_continuation(const unsigned char *buffer, size_t length) {
    for (size_t back = 1; back <= 4 && back <= length; back++) {
        unsigned char c = buffer[length - back];
        if ((c & 0xC0) == 0x80) {
            continue;
        }
        int need = c < 0x80 ? 1
            : (c & 0xE0) == 0xC0 ? 2
            : (c & 0xF0) == 0xE0 ? 3
            : (c & 0xF8) == 0xF0 ? 4
            : 1;
        return need > (int)back ? need - (int)back : 0;
    }
    return 0;
}

/// Reads at most `length` bytes at the handle's position into a malloc'd buffer
/// and returns how many bytes it holds. NULL means nothing was read.
static char *fs_read_chunk(FILE *handle, int length, size_t *count) {
    *count = 0;
    off_t position = ftello(handle);
    struct stat info;
    if (position < 0 || fstat(fileno(handle), &info) != 0) {
        return NULL;
    }

    off_t remaining = info.st_size > position ? info.st_size - position : 0;
    size_t take = (size_t)(remaining < (off_t)length ? remaining : (off_t)length);
    if (take == 0) {
        return NULL;
    }

    // Room for the three continuation bytes a trailing character may need.
    char *buffer = (char *)malloc(take + 3);
    if (!buffer) {
        tern_panic("IOError", "cannot read %zu bytes: out of memory", take);
    }

    size_t read = fread(buffer, 1, take, handle);
    if (read == take) {
        int extra = fs_missing_continuation((const unsigned char *)buffer, read);
        if (extra > 0) {
            read += fread(buffer + read, 1, (size_t)extra, handle);
        }
    }

    *count = read;
    return buffer;
}

static Value fs_open(int argc, Value *argv) {
    const char *path = path_arg(argc, argv, 0);
    int slot = -1;
    for (int i = 0; i < FS_MAX_HANDLES; i++) {
        if (!fs_handles[i]) {
            slot = i;
            break;
        }
    }
    if (slot < 0) {
        tern_panic("IOError", "too many open files (%d)", FS_MAX_HANDLES);
    }

    FILE *handle = fopen(path, "rb");
    if (!handle) {
        tern_panic("IOError", "cannot open '%s': %s", path, strerror(errno));
    }

    char *copy = (char *)malloc(strlen(path) + 1);
    if (!copy) {
        fclose(handle);
        tern_panic("IOError", "cannot open '%s': out of memory", path);
    }
    strcpy(copy, path);

    fs_handles[slot] = handle;
    fs_paths[slot] = copy;
    return v_int(slot + 1);
}

static Value fs_readchunk(int argc, Value *argv) {
    int slot = fs_slot_arg(argv[0]);
    int length = fs_length_arg(arg(argc, argv, 1), "readChunk", 2);

    size_t count = 0;
    char *data = fs_read_chunk(fs_handles[slot], length, &count);
    if (!data) {
        return str_value("", 0);
    }
    Value text = str_value(data, count);
    free(data);
    return text;
}

static Value fs_readat(int argc, Value *argv) {
    const char *path = path_arg(argc, argv, 0);
    long offset = fs_offset_arg(arg(argc, argv, 1), "readAt", 2);
    int length = argc > 2 ? fs_length_arg(argv[2], "readAt", 3) : FS_MAX_CHUNK;

    FILE *handle = fopen(path, "rb");
    if (!handle) {
        tern_panic("IOError", "cannot open '%s': %s", path, strerror(errno));
    }
    if (fseeko(handle, (off_t)offset, SEEK_SET) != 0) {
        int error = errno;
        fclose(handle);
        tern_panic("IOError", "cannot seek '%s': %s", path, strerror(error));
    }

    size_t count = 0;
    char *data = fs_read_chunk(handle, length, &count);
    fclose(handle);
    if (!data) {
        return str_value("", 0);
    }
    Value text = str_value(data, count);
    free(data);
    return text;
}

static Value fs_seek(int argc, Value *argv) {
    int slot = fs_slot_arg(argv[0]);
    long offset = fs_offset_arg(arg(argc, argv, 1), "seek", 2);
    if (fseeko(fs_handles[slot], (off_t)offset, SEEK_SET) != 0) {
        tern_panic("IOError", "cannot seek '%s': %s", fs_paths[slot], strerror(errno));
    }
    return v_int((int64_t)ftello(fs_handles[slot]));
}

static Value fs_position(int argc, Value *argv) {
    (void)argc;
    int slot = fs_slot_arg(argv[0]);
    off_t position = ftello(fs_handles[slot]);
    if (position < 0) {
        tern_panic("IOError", "cannot seek '%s': %s", fs_paths[slot], strerror(errno));
    }
    return v_int((int64_t)position);
}

static Value fs_filesize(int argc, Value *argv) {
    (void)argc;
    int slot = fs_slot_arg(argv[0]);
    struct stat info;
    if (fstat(fileno(fs_handles[slot]), &info) != 0) {
        tern_panic("IOError", "cannot stat '%s': %s", fs_paths[slot], strerror(errno));
    }
    return v_int((int64_t)info.st_size);
}

static Value fs_close(int argc, Value *argv) {
    (void)argc;
    int slot = fs_slot_arg(argv[0]);
    fclose(fs_handles[slot]);
    fs_handles[slot] = NULL;
    free(fs_paths[slot]);
    fs_paths[slot] = NULL;
    return v_nil();
}

static Value fs_write_file(int argc, Value *argv, const char *mode) {
    const char *path = path_arg(argc, argv, 0);
    Value rendered = tern_to_string(arg(argc, argv, 1));
    Str *body = (Str *)rendered.as.o;
    if (mode[0] == 'w') {
        make_parent_dirs(path);
    }
    FILE *handle = fopen(path, mode);
    if (!handle) {
        tern_panic("IOError", "cannot %s '%s'", mode[0] == 'a' ? "append to" : "write", path);
    }
    fwrite(body->chars, 1, body->len, handle);
    fclose(handle);
    return v_nil();
}

static Value fs_write(int argc, Value *argv) { return fs_write_file(argc, argv, "wb"); }
static Value fs_append(int argc, Value *argv) { return fs_write_file(argc, argv, "ab"); }

static Value fs_exists(int argc, Value *argv) {
    struct stat info;
    return v_bool(stat(path_arg(argc, argv, 0), &info) == 0);
}

static Value fs_isfile(int argc, Value *argv) {
    struct stat info;
    return v_bool(stat(path_arg(argc, argv, 0), &info) == 0 && S_ISREG(info.st_mode));
}

static Value fs_isdir(int argc, Value *argv) {
    struct stat info;
    return v_bool(stat(path_arg(argc, argv, 0), &info) == 0 && S_ISDIR(info.st_mode));
}

static Value fs_size(int argc, Value *argv) {
    const char *path = path_arg(argc, argv, 0);
    struct stat info;
    if (stat(path, &info) != 0) {
        tern_panic("IOError", "cannot stat '%s'", path);
    }
    return v_int((int64_t)info.st_size);
}

static Value fs_remove(int argc, Value *argv) {
    remove_tree(path_arg(argc, argv, 0));
    return v_nil();
}

static Value fs_mkdir(int argc, Value *argv) {
    const char *path = path_arg(argc, argv, 0);
    make_parent_dirs(path);
    if (mkdir(path, 0777) != 0 && errno != EEXIST) {
        tern_panic("IOError", "cannot create '%s'", path);
    }
    return v_nil();
}

static int compare_names(const void *left, const void *right) {
    return strcmp(*(const char *const *)left, *(const char *const *)right);
}

static Value fs_list(int argc, Value *argv) {
    const char *path = path_arg(argc, argv, 0);
    DIR *dir = opendir(path);
    if (!dir) {
        tern_panic("IOError", "cannot list '%s'", path);
    }

    char **names = NULL;
    int count = 0, capacity = 0;
    struct dirent *entry;
    while ((entry = readdir(dir)) != NULL) {
        if (strcmp(entry->d_name, ".") == 0 || strcmp(entry->d_name, "..") == 0) {
            continue;
        }
        if (count == capacity) {
            capacity = capacity ? capacity * 2 : 16;
            names = (char **)realloc(names, sizeof(char *) * (size_t)capacity);
        }
        names[count++] = strdup(entry->d_name);
    }
    closedir(dir);

    if (count > 0) {
        qsort(names, (size_t)count, sizeof(char *), compare_names);
    }
    List *list = list_new(count);
    for (int i = 0; i < count; i++) {
        list_push(list, str_from_cstr(names[i]));
        free(names[i]);
    }
    free(names);
    return v_obj((Obj *)list);
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

// The method tables mirror the interpreter's exactly: a name that is not
// listed here raises MemberError on both back ends, so a script cannot start
// working only because it was compiled.
static const MethodSlot int_methods[] = {
    {"abs", n_abs_self, 1, 1}, {"clamp", n_clamp_self, 3, 3}, {"times", n_times_self, 2, 2},
    {"toFloat", n_tofloat_self, 1, 1}, {"toString", n_tostring_self, 1, 1}, {NULL, NULL},
};

static const MethodSlot float_methods[] = {
    {"abs", n_abs_self, 1, 1}, {"clamp", n_clamp_self, 3, 3}, {"round", n_round_self, 1, 2},
    {"toInt", n_toint_self, 1, 1}, {"toString", n_tostring_self, 1, 1}, {NULL, NULL},
};

static const MethodSlot range_methods[] = {
    {"toList", r_tolist, 1, 1}, {"contains", r_contains, 2, 2}, {"length", r_length, 1, 1},
    {"toString", r_tostring, 1, 1}, {"map", r_map, 2, 2}, {"each", r_each, 2, 2}, {NULL, NULL},
};

static const MethodSlot map_methods[] = {
    {"keys", m_keys, 1, 1}, {"values", m_values, 1, 1}, {"has", m_has, 2, 2}, {"get", m_get, 2, 3},
    {"set", m_set, 3, 3}, {"remove", m_remove, 2, 2}, {"isEmpty", m_isempty, 1, 1}, {"clear", m_clear, 1, 1},
    {"clone", m_clone, 1, 1}, {"entries", m_entries, 1, 1}, {"contains", m_has, 2, 2},
    {"merge", m_merge, 2, 2}, {"toString", m_tostring_self, 1, 1}, {NULL, NULL},
};
static const MethodSlot string_methods[] = {
    {"upper", s_upper, 1, 1}, {"lower", s_lower, 1, 1}, {"trim", s_trim, 1, 1}, {"trimStart", s_trimstart, 1, 1},
    {"trimEnd", s_trimend, 1, 1}, {"contains", s_contains, 2, 2}, {"startsWith", s_starts, 2, 2},
    {"endsWith", s_ends, 2, 2}, {"indexOf", s_indexof, 2, 2}, {"lastIndexOf", s_lastindexof, 2, 2},
    {"count", s_count, 2, 2},
    {"substring", s_substring, 2, 3}, {"slice", s_slice, 1, 3}, {"split", s_split, 1, 2},
    {"replace", s_replace, 3, 3}, {"replaceFirst", s_replacefirst, 3, 3}, {"repeat", s_repeat, 2, 2},
    {"chars", s_chars, 1, 1}, {"lines", s_lines, 1, 1}, {"words", s_words, 1, 1},
    {"isEmpty", s_isempty, 1, 1}, {"toInt", s_toint, 1, 2}, {"toFloat", s_tofloat, 1, 2},
    {"toString", s_tostring, 1, 1}, {"padStart", s_padstart, 2, 3}, {"padEnd", s_padend, 2, 3},
    {"reversed", s_reversed, 1, 1}, {"format", s_format, 1, -1}, {NULL, NULL},
};

static const MethodSlot list_methods[] = {
    {"push", l_push, 2, -1}, {"pop", l_pop, 1, 1}, {"shift", l_shift, 1, 1}, {"unshift", l_unshift, 2, -1},
    {"join", l_join, 1, 2}, {"contains", l_contains, 2, 2}, {"indexOf", l_indexof, 2, 2},
    {"slice", l_slice, 1, 3}, {"reverse", l_reverse, 1, 1}, {"sort", l_sort, 1, 2},
    {"first", l_first, 1, 1}, {"last", l_last, 1, 1}, {"isEmpty", l_isempty, 1, 1}, {"sum", l_sum, 1, 1},
    {"min", l_min, 1, 1}, {"max", l_max, 1, 1}, {"clear", l_clear, 1, 1}, {"clone", l_clone, 1, 1},
    {"concat", l_concat, 2, 2}, {"insert", l_insert, 3, 3}, {"remove", l_remove, 2, 2},
    {"map", l_map, 2, 2}, {"filter", l_filter, 2, 2}, {"each", l_each, 2, 2}, {"reduce", l_reduce, 2, 3},
    {"toString", l_tostring, 1, 1}, {"flatten", l_flatten, 1, 2}, {"unique", l_unique, 1, 1},
    {"take", l_take, 2, 2}, {"drop", l_drop, 2, 2}, {"all", l_all, 2, 2}, {"any", l_any, 2, 2},
    {"find", l_find, 2, 2}, {"findIndex", l_findindex, 2, 2}, {"removeValue", l_removevalue, 2, 2},
    {NULL, NULL},
};

static const MethodSlot *lookup_method(const MethodSlot *slots, const char *name) {
    for (int i = 0; slots[i].name; i++) {
        if (strcmp(slots[i].name, name) == 0) {
            return &slots[i];
        }
    }
    return NULL;
}

static const MethodSlot *builtin_slot(Value receiver, const char *name) {
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
    if (receiver.tag == T_INT) {
        return lookup_method(int_methods, name);
    }
    if (receiver.tag == T_FLOAT) {
        return lookup_method(float_methods, name);
    }
    return NULL;
}

NativeFn tern_builtin_method(Value receiver, const char *name) {
    const MethodSlot *slot = builtin_slot(receiver, name);
    return slot ? slot->fn : NULL;
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

    const MethodSlot *slot = builtin_slot(receiver, name);
    if (slot) {
        check_method_arity(name, slot, argc + 1);
        Value stack_args[argc + 1];
        stack_args[0] = receiver;
        for (int i = 0; i < argc; i++) {
            stack_args[i + 1] = argv[i];
        }
        return slot->fn(argc + 1, stack_args);
    }

    tern_panic("TypeError", "%s has no method '%s'", tern_type_name(receiver), name);
    return v_nil();
}

// ------------------------------------------------------------------ globals

typedef struct GlobalSlot {
    const char *name;
    NativeFn fn;
    int16_t min_args;
    int16_t max_args;
} GlobalSlot;

// The same argument-count contract the interpreter registers for each global,
// so a wrong-arity call fails in compiled code too instead of silently
// reading missing arguments as nil.
static const GlobalSlot global_natives[] = {
    {"print", n_print, 0, -1}, {"println", n_println, 0, -1},
    {"eprint", n_eprint, 0, -1}, {"input", n_input, 0, 1},
    {"len", n_len, 1, 1}, {"type", n_type, 1, 1}, {"str", n_str, 1, 1},
    {"repr", n_repr, 1, 1}, {"int", n_int, 1, 2}, {"float", n_float, 1, 1},
    {"bool", n_bool, 1, 1}, {"chr", n_chr, 1, 1}, {"ord", n_ord, 1, 1},
    {"range", n_range, 1, 3}, {"keys", n_keys, 1, 1}, {"values", n_values, 1, 1},
    {"has", n_has, 2, 2}, {"assert", n_assert, 1, 2}, {"error", n_error, 1, 1},
    {"panic", n_panic, 0, 1}, {"abs", n_abs, 1, 1}, {"min", n_min, 1, -1},
    {"max", n_max, 1, -1}, {"copy", n_copy, 1, 1}, {"sleep", n_sleep, 1, 1},
    {NULL, NULL, 0, 0},
};

static Value make_native_arity(const char *name, NativeFn fn, int min_args, int max_args) {
    Native *native = (Native *)tern_alloc(sizeof(Native), O_NATIVE);
    native->name = name;
    native->fn = fn;
    native->min_args = (int16_t)min_args;
    native->max_args = (int16_t)max_args;
    return v_obj((Obj *)native);
}

// Method argument counts are stored with the receiver included, but the
// message reports them without it, exactly like the interpreter.
static void check_method_arity(const char *name, const MethodSlot *slot, int argc_with_receiver) {
    if (argc_with_receiver >= slot->min_args &&
        (slot->max_args < 0 || argc_with_receiver <= slot->max_args)) {
        return;
    }
    char expected[32];
    if (slot->max_args < 0) {
        snprintf(expected, sizeof expected, "at least %d", slot->min_args - 1);
    } else {
        snprintf(expected, sizeof expected, "%d to %d", slot->min_args - 1, slot->max_args - 1);
    }
    tern_panic("ArgumentError", "%s() expects %s argument(s) but got %d",
               name, expected, argc_with_receiver - 1);
}

static void check_arity(const Native *native, int argc) {
    if (native->min_args < 0) {
        return;
    }
    if (argc >= native->min_args && (native->max_args < 0 || argc <= native->max_args)) {
        return;
    }
    char expected[32];
    if (native->max_args < 0) {
        snprintf(expected, sizeof expected, "at least %d", native->min_args);
    } else if (native->min_args == native->max_args) {
        snprintf(expected, sizeof expected, "%d", native->min_args);
    } else {
        snprintf(expected, sizeof expected, "%d to %d", native->min_args, native->max_args);
    }
    tern_panic("ArgumentError", "%s() expects %s argument(s) but got %d",
               native->name, expected, argc);
}

// ------------------------------------------------------------------- wire
//
// The TERN wire format, byte for byte the same as TERN/Builtins/Wire.cs so an
// interpreted client can talk to a compiled server and the other way round.
// Structs travel with their type name and field names; the receiver rebuilds
// them when it declares that struct and gets a plain map when it does not.

#define WIRE_MAX_DEPTH 128
#define WIRE_MAX_BYTES (64 * 1024 * 1024)

static void wire_i64(Buf *buf, int64_t value) {
    unsigned char bytes[8];
    for (int i = 0; i < 8; i++) {
        bytes[i] = (unsigned char)((uint64_t)value >> (8 * i));
    }
    buf_add(buf, (const char *)bytes, 8);
}

static void wire_f64(Buf *buf, double value) {
    uint64_t bits;
    memcpy(&bits, &value, 8);
    wire_i64(buf, (int64_t)bits);
}

static void wire_varint(Buf *buf, uint64_t value) {
    while (value >= 0x80) {
        buf_add_char(buf, (char)((value & 0x7f) | 0x80));
        value >>= 7;
    }
    buf_add_char(buf, (char)value);
}

static void wire_text(Buf *buf, const char *text, size_t length) {
    wire_varint(buf, (uint64_t)length);
    buf_add(buf, text, length);
}

static void wire_value(Buf *buf, Value v, int depth);

static void wire_instance(Buf *buf, Instance *instance, int depth) {
    TypeObj *type = instance->type;
    buf_add_char(buf, 8); /* struct */
    wire_text(buf, type->name, strlen(type->name));
    wire_varint(buf, (uint64_t)type->nfields);
    for (int32_t i = 0; i < type->nfields; i++) {
        const char *name = type->field_names[i];
        wire_text(buf, name, strlen(name));
        wire_value(buf, instance->fields[i], depth + 1);
    }
}

static void wire_value(Buf *buf, Value v, int depth) {
    if (depth > WIRE_MAX_DEPTH) {
        tern_panic("SerializationError", "value nests deeper than %d levels", WIRE_MAX_DEPTH);
    }

    switch (v.tag) {
        case T_NIL:
        case T_MISSING:
            buf_add_char(buf, 0);
            return;
        case T_BOOL:
            buf_add_char(buf, v.as.i ? 2 : 1);
            return;
        case T_INT:
            buf_add_char(buf, 3);
            wire_i64(buf, v.as.i);
            return;
        case T_FLOAT:
            buf_add_char(buf, 4);
            wire_f64(buf, v.as.f);
            return;
        default:
            break;
    }

    if (v.tag != T_OBJ) {
        tern_panic("SerializationError", "cannot serialize a %s", tern_type_name(v));
    }

    switch (v.as.o->type) {
        case O_STR: {
            Str *s = (Str *)v.as.o;
            buf_add_char(buf, 5);
            wire_text(buf, s->chars, s->len);
            return;
        }

        case O_LIST: {
            List *list = (List *)v.as.o;
            buf_add_char(buf, 6);
            wire_varint(buf, (uint64_t)list->count);
            for (int32_t i = 0; i < list->count; i++) {
                wire_value(buf, list->items[i], depth + 1);
            }
            return;
        }

        case O_MAP: {
            Map *map = (Map *)v.as.o;
            buf_add_char(buf, 7);
            wire_varint(buf, (uint64_t)map->order_count);
            for (int32_t i = 0; i < map->order_count; i++) {
                Value key = map->order[i];
                Value item;
                if (!map_get(map, key, &item)) {
                    continue;
                }
                wire_value(buf, key, depth + 1);
                wire_value(buf, item, depth + 1);
            }
            return;
        }

        case O_RANGE: {
            Range *range = (Range *)v.as.o;
            buf_add_char(buf, 9);
            wire_i64(buf, range->start);
            wire_i64(buf, range->end);
            wire_i64(buf, range->step);
            buf_add_char(buf, range->inclusive ? 1 : 0);
            return;
        }

        case O_INSTANCE:
            wire_instance(buf, (Instance *)v.as.o, depth);
            return;

        default:
            tern_panic("SerializationError",
                       "cannot serialize a %s; send data (nil, booleans, numbers, strings, lists, maps, ranges or structs)",
                       tern_type_name(v));
    }
}

typedef struct WireReader {
    const unsigned char *data;
    size_t length;
    size_t offset;
} WireReader;

static unsigned char wire_byte(WireReader *reader) {
    if (reader->offset >= reader->length) {
        tern_panic("SerializationError", "truncated value");
    }
    return reader->data[reader->offset++];
}

static const unsigned char *wire_take(WireReader *reader, size_t count) {
    if (reader->offset + count > reader->length) {
        tern_panic("SerializationError", "truncated value");
    }
    const unsigned char *slice = reader->data + reader->offset;
    reader->offset += count;
    return slice;
}

static int64_t wire_read_i64(WireReader *reader) {
    const unsigned char *bytes = wire_take(reader, 8);
    uint64_t value = 0;
    for (int i = 7; i >= 0; i--) {
        value = (value << 8) | bytes[i];
    }
    return (int64_t)value;
}

static double wire_read_f64(WireReader *reader) {
    int64_t bits = wire_read_i64(reader);
    double value;
    memcpy(&value, &bits, 8);
    return value;
}

static uint64_t wire_read_varint(WireReader *reader) {
    uint64_t result = 0;
    int shift = 0;
    for (;;) {
        unsigned char b = wire_byte(reader);
        result |= (uint64_t)(b & 0x7f) << shift;
        if ((b & 0x80) == 0) {
            return result;
        }
        shift += 7;
        if (shift > 63) {
            tern_panic("SerializationError", "malformed length");
        }
    }
}

/* A count can never exceed the bytes left in the buffer. */
static int32_t wire_read_count(WireReader *reader) {
    uint64_t count = wire_read_varint(reader);
    if (count > (uint64_t)(reader->length - reader->offset)) {
        tern_panic("SerializationError", "length exceeds the payload");
    }
    return (int32_t)count;
}

static Value wire_read(WireReader *reader, int depth);

/* Allocates a string from a counted byte range. */
static Value wire_read_text(WireReader *reader) {
    int32_t length = wire_read_count(reader);
    const unsigned char *bytes = wire_take(reader, (size_t)length);
    return str_value((const char *)bytes, (size_t)length);
}

static Value wire_read_struct(WireReader *reader, int depth) {
    Value name = wire_read_text(reader);
    Str *type_name = (Str *)name.as.o;
    int32_t count = wire_read_count(reader);

    TypeObj *type = tern_find_type(type_name->chars);

    // Both the names and the values live in lists, not in malloc'd arrays: the
    // collector scans objects it owns, but a bare Value array on the heap is
    // invisible to it, so a value only referenced from there can be swept
    // between two reads.
    List *names = list_new(count > 0 ? count : 1);
    List *values = list_new(count > 0 ? count : 1);

    for (int32_t i = 0; i < count; i++) {
        list_push(names, wire_read_text(reader));
        list_push(values, wire_read(reader, depth + 1));
    }

    Value result;
    if (type) {
        Value instance = tern_new_instance(type, 0, NULL);
        for (int32_t i = 0; i < count; i++) {
            Str *field = (Str *)names->items[i].as.o;
            int32_t slot = find_field(type, field->chars);
            if (slot >= 0) {
                ((Instance *)instance.as.o)->fields[slot] = values->items[i];
            }
        }
        result = instance;
    } else {
        /* No such struct here: hand back the fields as a map. */
        Map *map = map_new();
        for (int32_t i = 0; i < count; i++) {
            map_set(map, names->items[i], values->items[i]);
        }
        result = v_obj((Obj *)map);
    }

    return result;
}

static Value wire_read(WireReader *reader, int depth) {
    if (depth > WIRE_MAX_DEPTH) {
        tern_panic("SerializationError", "value nests deeper than %d levels", WIRE_MAX_DEPTH);
    }

    unsigned char tag = wire_byte(reader);
    switch (tag) {
        case 0: return v_nil();
        case 1: return v_bool(false);
        case 2: return v_bool(true);
        case 3: return v_int(wire_read_i64(reader));
        case 4: return v_float(wire_read_f64(reader));
        case 5: return wire_read_text(reader);

        case 6: {
            int32_t count = wire_read_count(reader);
            List *list = list_new(count > 0 ? count : 1);
            for (int32_t i = 0; i < count; i++) {
                list_push(list, wire_read(reader, depth + 1));
            }
            return v_obj((Obj *)list);
        }

        case 7: {
            int32_t count = wire_read_count(reader);
            Map *map = map_new();
            for (int32_t i = 0; i < count; i++) {
                Value key = wire_read(reader, depth + 1);
                Value item = wire_read(reader, depth + 1);
                map_set(map, key, item);
            }
            return v_obj((Obj *)map);
        }

        case 8:
            return wire_read_struct(reader, depth);

        case 9: {
            int64_t start = wire_read_i64(reader);
            int64_t end = wire_read_i64(reader);
            int64_t step = wire_read_i64(reader);
            bool inclusive = wire_byte(reader) != 0;
            return v_obj((Obj *)range_make(start, end, inclusive, step));
        }

        default:
            tern_panic("SerializationError", "unknown value tag %d", (int)tag);
    }

    return v_nil();
}

static Value wire_encode(Value v) {
    Buf buf;
    buf_init(&buf);
    wire_value(&buf, v, 0);
    if (buf.len > WIRE_MAX_BYTES) {
        free(buf.data);
        tern_panic("SerializationError", "value is larger than %d bytes", WIRE_MAX_BYTES);
    }

    List *bytes = list_new((int)buf.len);
    for (size_t i = 0; i < buf.len; i++) {
        list_push(bytes, v_int((unsigned char)buf.data[i]));
    }
    free(buf.data);
    return v_obj((Obj *)bytes);
}

static Value wire_decode(Value bytes) {
    if (!v_is_list(bytes)) {
        tern_panic("TypeError", "expected a list of bytes, not %s", tern_type_name(bytes));
    }

    List *list = (List *)bytes.as.o;
    if ((size_t)list->count > (size_t)WIRE_MAX_BYTES) {
        tern_panic("SerializationError", "payload is larger than %d bytes", WIRE_MAX_BYTES);
    }

    unsigned char *data = (unsigned char *)malloc((size_t)(list->count > 0 ? list->count : 1));
    for (int32_t i = 0; i < list->count; i++) {
        Value item = list->items[i];
        if (item.tag != T_INT || item.as.i < 0 || item.as.i > 255) {
            free(data);
            tern_panic("TypeError", "byte %d is not an integer in 0..255", i);
        }
        data[i] = (unsigned char)item.as.i;
    }

    WireReader reader = {data, (size_t)list->count, 0};
    Value result = wire_read(&reader, 0);
    if (reader.offset != reader.length) {
        size_t left = reader.length - reader.offset;
        free(data);
        tern_panic("SerializationError", "%d trailing byte(s) after the value", (int)left);
    }
    free(data);
    return result;
}

// -------------------------------------------------------------------- net
//
// Sockets, addressed by small integer handles so a script talks to the same
// API in both back ends. Frames are a 4 byte big endian length followed by an
// encoded value, and every connection opens with a hello frame so a listener
// with a token can refuse a peer before the script ever sees it.

#define NET_MAX_HANDLES 128
#define NET_MAX_FRAME (64 * 1024 * 1024)
#define NET_DEFAULT_MAX_FRAME (1024 * 1024)
#define NET_HANDSHAKE_TIMEOUT_MS 5000
#define NET_PROTOCOL_VERSION 1

// 0 = stream connection, 1 = listening stream socket, 2 = bound datagram socket.
static int net_fds[NET_MAX_HANDLES];
static int net_kinds[NET_MAX_HANDLES];
static int net_max_frames[NET_MAX_HANDLES];
static int net_timeouts[NET_MAX_HANDLES];
static char *net_tokens[NET_MAX_HANDLES];
static void *net_ssl[NET_MAX_HANDLES];      // SSL* once a connection is encrypted
static void *net_ssl_ctx[NET_MAX_HANDLES];  // the SSL_CTX* that SSL* belongs to

static int net_slot(int handle) {
    if (handle < 1 || handle > NET_MAX_HANDLES || net_fds[handle - 1] < 0) {
        tern_panic("NetError", "no open socket with handle %d", handle);
    }
    return handle - 1;
}

static int net_store(int fd, int kind) {
    for (int i = 0; i < NET_MAX_HANDLES; i++) {
        if (net_fds[i] < 0) {
            net_fds[i] = fd;
            net_kinds[i] = kind;
            net_max_frames[i] = NET_DEFAULT_MAX_FRAME;
            net_timeouts[i] = 0;
            net_tokens[i] = NULL;
            return i + 1;
        }
    }
    close(fd);
    tern_panic("NetError", "too many open sockets (limit %d)", NET_MAX_HANDLES);
    return 0;
}

static int net_handle_arg(Value v) {
    if (v.tag != T_INT) {
        tern_panic("TypeError", "expected a socket handle, not %s", tern_type_name(v));
    }
    return (int)v.as.i;
}

static const char *net_arg_text(Value v, const char *what) {
    if (!v_is_str(v)) {
        tern_panic("TypeError", "%s must be a string, not %s", what, tern_type_name(v));
    }
    return ((Str *)v.as.o)->chars;
}

static int net_arg_int(Value v, const char *what) {
    if (v.tag != T_INT) {
        tern_panic("TypeError", "%s must be a number, not %s", what, tern_type_name(v));
    }
    return (int)v.as.i;
}

// A number that may be an int or a float, for millisecond arguments.
static double net_arg_number(Value v, const char *what) {
    if (v.tag == T_INT) return (double)v.as.i;
    if (v.tag == T_FLOAT) return v.as.f;
    tern_panic("TypeError", "%s must be a number, not %s", what, tern_type_name(v));
    return 0;
}

static Value n_net_encode(int argc, Value *argv) {
    (void)argc;
    return wire_encode(argv[0]);
}

static Value n_net_decode(int argc, Value *argv) {
    (void)argc;
    return wire_decode(argv[0]);
}

static void net_fail(const char *what) {
    tern_panic("NetError", "%s: %s", what, strerror(errno));
}

// --- raw byte plumbing ----------------------------------------------------

/// Writes every byte or raises. `what` names the operation in the message.
static void net_write_all(int fd, const void *data, size_t length, const char *what) {
    const unsigned char *bytes = (const unsigned char *)data;
    size_t sent = 0;
    while (sent < length) {
        ssize_t n = send(fd, bytes + sent, length - sent, MSG_NOSIGNAL);
        if (n <= 0) {
            if (errno == EAGAIN || errno == EWOULDBLOCK) {
                tern_panic("NetError", "%s() timed out", what);
            }
            tern_panic("NetError", "%s() failed: %s", what, strerror(errno));
        }
        sent += (size_t)n;
    }
}

/// Reads exactly `length` bytes into `buffer`. Returns the number of bytes read:
/// less than `length` only at end of stream, and -1 on a socket error.
static ssize_t net_read_raw(int fd, unsigned char *buffer, size_t length) {
    size_t got = 0;
    while (got < length) {
        ssize_t n = recv(fd, buffer + got, length - got, 0);
        if (n == 0) {
            return (ssize_t)got;
        }
        if (n < 0) {
            if (errno == EINTR) continue;
            return -1;
        }
        got += (size_t)n;
    }
    return (ssize_t)got;
}

#ifndef _WIN32

// --- TLS ------------------------------------------------------------------
//
// OpenSSL is loaded at run time rather than linked: the headers are not needed
// for these calls (every one takes opaque pointers and ints), and a build
// machine without libssl can still produce a binary that runs everywhere else.
// The signatures are the stable public ones of OpenSSL 1.1 and 3.x.

typedef struct ssl_ctx_st SSL_CTX;
typedef struct ssl_st SSL;
typedef struct ssl_method_st SSL_METHOD;

#define SSL_VERIFY_PEER 1
#define SSL_CTRL_SET_TLSEXT_HOSTNAME 55
#define TLSEXT_NAMETYPE_host_name 0
#define SSL_ERROR_ZERO_RETURN 6
#define SSL_ERROR_WANT_READ 2
#define SSL_ERROR_WANT_WRITE 3
#define SSL_ERROR_SYSCALL 5

static void *tls_library = NULL;
static int tls_state = 0;   // 0 = not tried, 1 = usable, -1 = unavailable

static SSL_METHOD *(*tls_TLS_client_method)(void);
static SSL_CTX *(*tls_SSL_CTX_new)(const SSL_METHOD *);
static void (*tls_SSL_CTX_free)(SSL_CTX *);
static int (*tls_SSL_CTX_set_default_verify_paths)(SSL_CTX *);
static int (*tls_SSL_CTX_load_verify_locations)(SSL_CTX *, const char *, const char *);
static SSL *(*tls_SSL_new)(SSL_CTX *);
static void (*tls_SSL_free)(SSL *);
static int (*tls_SSL_set_fd)(SSL *, int);
static void (*tls_SSL_set_verify)(SSL *, int, void *);
static long (*tls_SSL_ctrl)(SSL *, int, long, void *);
static int (*tls_SSL_set1_host)(SSL *, const char *);
static int (*tls_SSL_connect)(SSL *);
static int (*tls_SSL_read)(SSL *, void *, int);
static int (*tls_SSL_write)(SSL *, const void *, int);
static int (*tls_SSL_get_error)(const SSL *, int);
static int (*tls_SSL_shutdown)(SSL *);
static long (*tls_SSL_get_verify_result)(const SSL *);

static bool tls_load(void) {
    if (tls_state != 0) {
        return tls_state > 0;
    }

    static const char *names[] = { "libssl.so.3", "libssl.so.1.1", "libssl.so", NULL };
    for (int i = 0; names[i] && !tls_library; i++) {
        tls_library = dlopen(names[i], RTLD_NOW);
    }

    if (!tls_library) {
        tls_state = -1;
        return false;
    }

    *(void **)(&tls_TLS_client_method) = dlsym(tls_library, "TLS_client_method");
    *(void **)(&tls_SSL_CTX_new) = dlsym(tls_library, "SSL_CTX_new");
    *(void **)(&tls_SSL_CTX_free) = dlsym(tls_library, "SSL_CTX_free");
    *(void **)(&tls_SSL_CTX_set_default_verify_paths) = dlsym(tls_library, "SSL_CTX_set_default_verify_paths");
    *(void **)(&tls_SSL_CTX_load_verify_locations) = dlsym(tls_library, "SSL_CTX_load_verify_locations");
    *(void **)(&tls_SSL_new) = dlsym(tls_library, "SSL_new");
    *(void **)(&tls_SSL_free) = dlsym(tls_library, "SSL_free");
    *(void **)(&tls_SSL_set_fd) = dlsym(tls_library, "SSL_set_fd");
    *(void **)(&tls_SSL_set_verify) = dlsym(tls_library, "SSL_set_verify");
    *(void **)(&tls_SSL_ctrl) = dlsym(tls_library, "SSL_ctrl");
    *(void **)(&tls_SSL_set1_host) = dlsym(tls_library, "SSL_set1_host");
    *(void **)(&tls_SSL_connect) = dlsym(tls_library, "SSL_connect");
    *(void **)(&tls_SSL_read) = dlsym(tls_library, "SSL_read");
    *(void **)(&tls_SSL_write) = dlsym(tls_library, "SSL_write");
    *(void **)(&tls_SSL_get_error) = dlsym(tls_library, "SSL_get_error");
    *(void **)(&tls_SSL_shutdown) = dlsym(tls_library, "SSL_shutdown");
    *(void **)(&tls_SSL_get_verify_result) = dlsym(tls_library, "SSL_get_verify_result");

    if (!tls_TLS_client_method || !tls_SSL_CTX_new || !tls_SSL_new || !tls_SSL_connect ||
        !tls_SSL_read || !tls_SSL_write || !tls_SSL_get_error) {
        tls_state = -1;
        return false;
    }

    tls_state = 1;
    return true;
}

/// Wraps `fd` in TLS. On success the pair is handed back and the caller keeps
/// both alive until the socket closes.
static bool tls_handshake(int fd, const char *host, bool verify, const char *ca,
                          SSL **out_ssl, SSL_CTX **out_ctx) {
    if (!tls_load()) {
        tern_panic("TlsError", "TLS support is not available on this system");
    }

    SSL_CTX *ctx = tls_SSL_CTX_new(tls_TLS_client_method());
    if (!ctx) {
        tern_panic("TlsError", "TLS handshake failed");
    }

    if (ca) {
        if (tls_SSL_CTX_load_verify_locations(ctx, ca, NULL) != 1) {
            tls_SSL_CTX_free(ctx);
            tern_panic("TlsError", "cannot read the CA file '%s'", ca);
        }
    } else if (tls_SSL_CTX_set_default_verify_paths) {
        tls_SSL_CTX_set_default_verify_paths(ctx);
    }

    SSL *ssl = tls_SSL_new(ctx);
    if (!ssl) {
        tls_SSL_CTX_free(ctx);
        tern_panic("TlsError", "TLS handshake failed");
    }

    tls_SSL_set_fd(ssl, fd);
    tls_SSL_set_verify(ssl, verify ? SSL_VERIFY_PEER : 0, NULL);
    // SNI: SSL_set_tlsext_host_name is a macro over SSL_ctrl.
    tls_SSL_ctrl(ssl, SSL_CTRL_SET_TLSEXT_HOSTNAME, TLSEXT_NAMETYPE_host_name, (void *)host);
    if (verify && tls_SSL_set1_host) {
        tls_SSL_set1_host(ssl, host);
    }

    if (tls_SSL_connect(ssl) != 1) {
        tls_SSL_free(ssl);
        tls_SSL_CTX_free(ctx);
        tern_panic("TlsError", "TLS handshake failed");
    }

    if (verify && tls_SSL_get_verify_result && tls_SSL_get_verify_result(ssl) != 0) {
        tls_SSL_free(ssl);
        tls_SSL_CTX_free(ctx);
        tern_panic("TlsError", "TLS handshake failed");
    }

    *out_ssl = ssl;
    *out_ctx = ctx;
    return true;
}

/// Reads from an encrypted socket with the same contract as net_read_raw: the
/// number of bytes, short at end of stream, or -1 with errno set (EAGAIN for a
/// timeout).
static ssize_t tls_read_some(void *handle, unsigned char *buffer, size_t length) {
    SSL *ssl = (SSL *)handle;
    size_t got = 0;
    while (got < length) {
        int n = tls_SSL_read(ssl, buffer + got, (int)(length - got));
        if (n > 0) {
            got += (size_t)n;
            continue;
        }

        int error = tls_SSL_get_error(ssl, n);
        if (error == SSL_ERROR_ZERO_RETURN) {
            return (ssize_t)got;
        }
        if (error == SSL_ERROR_WANT_READ || error == SSL_ERROR_WANT_WRITE) {
            continue;
        }
        if (error == SSL_ERROR_SYSCALL) {
            if (errno == EAGAIN || errno == EWOULDBLOCK) {
                if (got > 0) return (ssize_t)got;
                errno = EAGAIN;
                return -1;
            }
            if (n == 0) {
                return (ssize_t)got;
            }
            return -1;
        }

        errno = ECONNRESET;
        return -1;
    }
    return (ssize_t)got;
}

/// Reads whatever is available, at most `length` bytes: the contract a protocol
/// parser wants, where a short read is normal and only 0 means end of stream.
static ssize_t tls_read_once(void *handle, unsigned char *buffer, size_t length) {
    SSL *ssl = (SSL *)handle;
    for (;;) {
        int n = tls_SSL_read(ssl, buffer, (int)length);
        if (n > 0) {
            return (ssize_t)n;
        }

        int error = tls_SSL_get_error(ssl, n);
        if (error == SSL_ERROR_ZERO_RETURN) {
            return 0;
        }
        if (error == SSL_ERROR_WANT_READ || error == SSL_ERROR_WANT_WRITE) {
            continue;
        }
        if (error == SSL_ERROR_SYSCALL && n == 0) {
            return 0;
        }
        return -1;
    }
}

/// Writes every byte through `ssl`, raising like net_write_all does.
static void tls_write_all(void *handle, const void *data, size_t length, const char *what) {
    SSL *ssl = (SSL *)handle;
    const unsigned char *bytes = (const unsigned char *)data;
    size_t sent = 0;
    while (sent < length) {
        int n = tls_SSL_write(ssl, bytes + sent, (int)(length - sent));
        if (n > 0) {
            sent += (size_t)n;
            continue;
        }

        int error = tls_SSL_get_error(ssl, n);
        if (error == SSL_ERROR_WANT_READ || error == SSL_ERROR_WANT_WRITE) {
            continue;
        }
        if (error == SSL_ERROR_SYSCALL && (errno == EAGAIN || errno == EWOULDBLOCK)) {
            tern_panic("NetError", "%s() timed out", what);
        }
        tern_panic("NetError", "%s() failed: %s", what,
                   strerror(errno ? errno : ECONNRESET));
    }
}


// --- the socket I/O the net API actually uses ------------------------------
//
// Everything below reads and writes through a slot so a connection can be plain
// or encrypted without the frame code knowing which.

static void net_slot_write(int slot, const void *data, size_t length, const char *what) {
    if (net_ssl[slot]) {
        tls_write_all(net_ssl[slot], data, length, what);
        return;
    }
    net_write_all(net_fds[slot], data, length, what);
}

static ssize_t net_slot_read(int slot, unsigned char *buffer, size_t length) {
    if (net_ssl[slot]) {
        return tls_read_some(net_ssl[slot], buffer, length);
    }
    return net_read_raw(net_fds[slot], buffer, length);
}

static int net_bound_port(int fd) {
    struct sockaddr_in address;
    socklen_t length = sizeof address;
    if (getsockname(fd, (struct sockaddr *)&address, &length) != 0) {
        return 0;
    }
    return ntohs(address.sin_port);
}

// --- the hello frame ------------------------------------------------------

/// Version byte, then the token as a length-prefixed UTF-8 string (length 0 for
/// "no token"). Trailing bytes are reserved for later versions to negotiate.
static void net_send_hello(int slot, const char *token) {
    size_t token_length = token ? strlen(token) : 0;
    unsigned char payload[16];
    size_t at = 0;
    payload[at++] = NET_PROTOCOL_VERSION;

    size_t remaining = token_length;
    do {
        unsigned char chunk = (unsigned char)(remaining & 0x7F);
        remaining >>= 7;
        payload[at++] = remaining ? (unsigned char)(chunk | 0x80) : chunk;
    } while (remaining);

    unsigned char header[4];
    uint32_t total = (uint32_t)(at + token_length);
    header[0] = (unsigned char)(total >> 24);
    header[1] = (unsigned char)(total >> 16);
    header[2] = (unsigned char)(total >> 8);
    header[3] = (unsigned char)total;

    net_slot_write(slot, header, 4, "connect");
    net_slot_write(slot, payload, at, "connect");
    if (token_length) {
        net_slot_write(slot, token, token_length, "connect");
    }
}

static bool net_const_time_equal(const char *expected, const unsigned char *presented, size_t length) {
    size_t expected_length = strlen(expected);
    unsigned int difference = (unsigned int)(expected_length ^ length);
    for (size_t i = 0; i < expected_length; i++) {
        unsigned char other = i < length ? presented[i] : 0;
        difference |= (unsigned int)((unsigned char)expected[i] ^ other);
    }
    return difference == 0;
}

typedef enum {
    NET_HELLO_ACCEPTED = 0,
    NET_HELLO_CLOSED = 1,
    NET_HELLO_REFUSED = 2,
} NetHelloResult;

/// Reads a peer's hello: a 4 byte big endian length, then a payload of one
/// version byte, the token length as a varint and the token itself.
static NetHelloResult net_check_hello(int slot, const char *expected, char *scratch, size_t scratch_size) {
    unsigned char header[4];
    if (net_slot_read(slot, header, 4) <= 0) {
        return NET_HELLO_CLOSED;
    }

    uint32_t length = ((uint32_t)header[0] << 24) | ((uint32_t)header[1] << 16) |
                      ((uint32_t)header[2] << 8) | (uint32_t)header[3];
    if (length < 1 || length > NET_MAX_FRAME) {
        snprintf(scratch, scratch_size, "hello of %u bytes is out of range", (unsigned)length);
        return NET_HELLO_REFUSED;
    }

    unsigned char *payload = (unsigned char *)malloc(length);
    if (net_slot_read(slot, payload, length) <= 0) {
        free(payload);
        return NET_HELLO_CLOSED;
    }

    if (payload[0] != NET_PROTOCOL_VERSION) {
        snprintf(scratch, scratch_size, "unsupported protocol version %d", (int)payload[0]);
        free(payload);
        return NET_HELLO_REFUSED;
    }

    size_t at = 1;
    long token_length = 0;
    int shift = 0;
    bool malformed = false;
    for (;;) {
        if (at >= length) {
            malformed = true;
            break;
        }
        unsigned char byte = payload[at++];
        token_length |= (long)(byte & 0x7F) << shift;
        if (!(byte & 0x80)) break;
        shift += 7;
        if (shift > 28) {
            malformed = true;
            break;
        }
    }

    if (!malformed && (token_length < 0 || (size_t)token_length != (size_t)length - at)) {
        malformed = true;
    }

    if (malformed) {
        snprintf(scratch, scratch_size, "hello length is not a valid number");
        free(payload);
        return NET_HELLO_REFUSED;
    }

    bool accepted = !expected || net_const_time_equal(expected, payload + at, (size_t)token_length);
    free(payload);
    if (!accepted) {
        snprintf(scratch, scratch_size, "token rejected");
        return NET_HELLO_REFUSED;
    }

    return NET_HELLO_ACCEPTED;
}

// --- options --------------------------------------------------------------

typedef struct NetOptions {
    const char *token;
    int max_frame;
    int timeout;
    bool tls;
    const char *sni;
    bool verify;
    const char *ca;
} NetOptions;

static NetOptions net_options(Value v, const char *operation) {
    NetOptions options = { NULL, NET_DEFAULT_MAX_FRAME, 0, false, NULL, true, NULL };
    if (v.tag == T_NIL || v.tag == T_MISSING) {
        return options;
    }
    if (!v_is_map(v)) {
        tern_panic("TypeError", "%s() options must be a map, not %s", operation, tern_type_name(v));
    }

    Map *map = (Map *)v.as.o;
    Value found;
    if (map_get(map, str_from_cstr("token"), &found)) {
        if (!v_is_str(found)) {
            tern_panic("TypeError", "option 'token' must be a string, not %s", tern_type_name(found));
        }
        Str *token = (Str *)found.as.o;
        if (token->len == 0) {
            tern_panic("ArgumentError", "option 'token' must not be empty");
        }
        options.token = token->chars;
    }

    if (map_get(map, str_from_cstr("maxFrame"), &found)) {
        double limit = net_arg_number(found, "maxFrame");
        if (limit <= 0 || limit > NET_MAX_FRAME) {
            tern_panic("NetError", "option 'maxFrame' must be between 1 and %d bytes", NET_MAX_FRAME);
        }
        options.max_frame = (int)limit;
    }

    if (map_get(map, str_from_cstr("timeout"), &found)) {
        double milliseconds = net_arg_number(found, "timeout");
        if (milliseconds < 0) {
            tern_panic("ArgumentError", "option 'timeout' must not be negative");
        }
        options.timeout = (int)milliseconds;
    }

    if (map_get(map, str_from_cstr("tls"), &found)) {
        if (found.tag != T_BOOL) {
            tern_panic("TypeError", "option 'tls' must be a boolean, not %s", tern_type_name(found));
        }
        options.tls = found.as.i != 0;
    }

    if (map_get(map, str_from_cstr("sni"), &found)) {
        options.sni = net_arg_text(found, "option 'sni'");
    }

    if (map_get(map, str_from_cstr("verify"), &found)) {
        if (found.tag != T_BOOL) {
            tern_panic("TypeError", "option 'verify' must be a boolean, not %s", tern_type_name(found));
        }
        options.verify = found.as.i != 0;
    }

    if (map_get(map, str_from_cstr("ca"), &found)) {
        options.ca = net_arg_text(found, "option 'ca'");
    }

    return options;
}

/// Resolves `host` into an IPv4 address, accepting a literal address as given.
static void net_resolve(const char *host, struct sockaddr_in *address, int port) {
    memset(address, 0, sizeof *address);
    address->sin_family = AF_INET;
    address->sin_port = htons((uint16_t)port);
    if (inet_pton(AF_INET, host, &address->sin_addr) == 1) {
        return;
    }

    struct addrinfo hints;
    struct addrinfo *found = NULL;
    memset(&hints, 0, sizeof hints);
    hints.ai_family = AF_INET;
    hints.ai_socktype = SOCK_STREAM;
    int status = getaddrinfo(host, NULL, &hints, &found);
    if (status != 0) {
        tern_panic("NetError", "cannot resolve '%s': %s", host, gai_strerror(status));
    }
    if (!found) {
        tern_panic("NetError", "cannot resolve '%s'", host);
    }
    address->sin_addr = ((struct sockaddr_in *)found->ai_addr)->sin_addr;
    freeaddrinfo(found);
}

// --- the socket calls -----------------------------------------------------

static Value n_net_listen(int argc, Value *argv) {
    // listen(port) | listen(port, options) | listen(host, port, options).
    const char *host = "127.0.0.1";
    Value port_value = argv[0];
    Value options_value = argc > 1 && argv[1].tag != T_INT ? argv[1] : v_nil();
    if (argc > 1 && argv[1].tag == T_INT) {
        host = net_arg_text(argv[0], "host");
        port_value = argv[1];
        options_value = argc > 2 ? argv[2] : v_nil();
    }

    NetOptions options = net_options(options_value, "listen");
    if (options.tls) {
        tern_panic("TlsError", "listen() cannot accept tls: server TLS is not supported");
    }

    int port = net_arg_int(port_value, "port");

    struct sockaddr_in address;
    net_resolve(host, &address, port);

    int fd = socket(AF_INET, SOCK_STREAM, 0);
    if (fd < 0) {
        net_fail("cannot create a socket");
    }

    int reuse = 1;
    setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &reuse, sizeof reuse);

    if (bind(fd, (struct sockaddr *)&address, sizeof address) != 0) {
        int error = errno;
        close(fd);
        tern_panic("NetError", "cannot listen on %s:%d: %s", host, port, strerror(error));
    }

    if (listen(fd, 128) != 0) {
        int error = errno;
        close(fd);
        tern_panic("NetError", "cannot listen on %s:%d: %s", host, port, strerror(error));
    }

    if (options.timeout > 0) {
        struct timeval tv;
        tv.tv_sec = options.timeout / 1000;
        tv.tv_usec = (options.timeout % 1000) * 1000;
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof tv);
    }

    int handle = net_store(fd, 1);
    net_max_frames[handle - 1] = options.max_frame;
    net_timeouts[handle - 1] = options.timeout;
    if (options.token) {
        net_tokens[handle - 1] = strdup(options.token);
    }
    return v_int(handle);
}

static Value n_net_accept(int argc, Value *argv) {
    (void)argc;
    int slot = net_slot(net_handle_arg(argv[0]));
    if (net_kinds[slot] != 1) {
        tern_panic("NetError", "accept() needs a server handle from listen()");
    }

    int listener = net_fds[slot];
    char *expected = net_tokens[slot];
    int inherited_max = net_max_frames[slot];
    int handshake_ms = net_timeouts[slot] > 0 ? net_timeouts[slot] : NET_HANDSHAKE_TIMEOUT_MS;

    struct sockaddr_in peer;
    socklen_t length = sizeof peer;
    int fd = accept(listener, (struct sockaddr *)&peer, &length);
    if (fd < 0) {
        if (errno == EAGAIN || errno == EWOULDBLOCK) {
            tern_panic("NetError", "accept() timed out");
        }
        net_fail("accept() failed");
    }

    int one = 1;
    setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &one, sizeof one);

    // The peer must say hello before the script sees the socket. The wait is
    // bounded so one silent peer cannot hold the server forever.
    struct timeval handshake;
    handshake.tv_sec = handshake_ms / 1000;
    handshake.tv_usec = (handshake_ms % 1000) * 1000;
    setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &handshake, sizeof handshake);

    int handle = net_store(fd, 0);

    char scratch[96];
    NetHelloResult result = net_check_hello(handle - 1, expected, scratch, sizeof scratch);

    // The handshake wait is not the script's timeout, so the connection starts
    // without one no matter what the listener used.
    struct timeval none = { 0, 0 };
    setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &none, sizeof none);

    if (result == NET_HELLO_CLOSED) {
        close(fd);
        net_fds[handle - 1] = -1;
        net_kinds[handle - 1] = 0;
        tern_panic("ConnectionError", "connection closed during the handshake");
    }

    if (result == NET_HELLO_REFUSED) {
        // Say why, then close: a rejected peer gets no second chance.
        send(fd, scratch, strlen(scratch), MSG_NOSIGNAL);
        close(fd);
        net_fds[handle - 1] = -1;
        net_kinds[handle - 1] = 0;
        tern_panic("AuthenticationError", "%s", scratch);
    }

    net_max_frames[handle - 1] = inherited_max;
    return v_int(handle);
}

static Value n_net_connect(int argc, Value *argv) {
    (void)argc;
    const char *host = net_arg_text(argv[0], "host");
    int port = net_arg_int(argv[1], "port");
    NetOptions options = net_options(argc > 2 ? argv[2] : v_nil(), "connect");

    struct sockaddr_in address;
    net_resolve(host, &address, port);

    int fd = socket(AF_INET, SOCK_STREAM, 0);
    if (fd < 0) {
        net_fail("cannot create a socket");
    }

    if (options.timeout <= 0) {
        if (connect(fd, (struct sockaddr *)&address, sizeof address) != 0) {
            int error = errno;
            close(fd);
            tern_panic("NetError", "cannot connect to %s:%d: %s", host, port, strerror(error));
        }
    } else {
        int flags = fcntl(fd, F_GETFL, 0);
        fcntl(fd, F_SETFL, flags | O_NONBLOCK);
        int rc = connect(fd, (struct sockaddr *)&address, sizeof address);
        if (rc != 0 && errno != EINPROGRESS) {
            int error = errno;
            close(fd);
            tern_panic("NetError", "cannot connect to %s:%d: %s", host, port, strerror(error));
        }

        if (rc != 0) {
            struct pollfd waiting = { fd, POLLOUT, 0 };
            int ready = poll(&waiting, 1, options.timeout);
            if (ready == 0) {
                close(fd);
                tern_panic("NetError", "cannot connect to %s:%d: timed out after %d ms",
                           host, port, options.timeout);
            }
            if (ready < 0) {
                int error = errno;
                close(fd);
                tern_panic("NetError", "cannot connect to %s:%d: %s", host, port, strerror(error));
            }

            int error = 0;
            socklen_t error_size = sizeof error;
            getsockopt(fd, SOL_SOCKET, SO_ERROR, &error, &error_size);
            if (error != 0) {
                close(fd);
                tern_panic("NetError", "cannot connect to %s:%d: %s", host, port, strerror(error));
            }
        }

        fcntl(fd, F_SETFL, flags);
    }

    int one = 1;
    setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &one, sizeof one);

    if (options.timeout > 0) {
        struct timeval tv;
        tv.tv_sec = options.timeout / 1000;
        tv.tv_usec = (options.timeout % 1000) * 1000;
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof tv);
        setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &tv, sizeof tv);
    }

    int handle = net_store(fd, 0);
    net_max_frames[handle - 1] = options.max_frame;
    net_timeouts[handle - 1] = options.timeout;

    if (options.tls) {
        SSL *ssl = NULL;
        SSL_CTX *ctx = NULL;
        if (!tls_handshake(fd, options.sni ? options.sni : host, options.verify, options.ca, &ssl, &ctx)) {
            close(fd);
            net_fds[handle - 1] = -1;
            net_kinds[handle - 1] = 0;
            tern_panic("TlsError", "TLS handshake failed");
        }
        net_ssl[handle - 1] = (void *)ssl;
        net_ssl_ctx[handle - 1] = (void *)ctx;
    }

    net_send_hello(handle - 1, options.token);
    return v_int(handle);
}

/// Refuses a handle that is not a stream connection, naming what it is instead.
static void net_require_stream(int slot, const char *operation) {
    if (net_kinds[slot] == 1) {
        tern_panic("NetError", "%s() needs a connection handle, not a server", operation);
    }
    if (net_kinds[slot] == 2) {
        tern_panic("NetError", "%s() needs a connection handle, not a datagram socket", operation);
    }
    if (net_kinds[slot] != 0) {
        tern_panic("NetError", "%s() needs a connection handle, not an rcon session", operation);
    }
}

static int net_connection_slot(Value v, const char *operation) {
    int slot = net_slot(net_handle_arg(v));
    net_require_stream(slot, operation);
    return slot;
}

static int net_datagram_slot(Value v, const char *operation) {
    int slot = net_slot(net_handle_arg(v));
    if (net_kinds[slot] != 2) {
        tern_panic("NetError", "%s() needs a datagram handle from udpListen()", operation);
    }
    return slot;
}

static Value n_net_send(int argc, Value *argv) {
    (void)argc;
    int slot = net_slot(net_handle_arg(argv[0]));
    net_require_stream(slot, "send");

    Value encoded = wire_encode(argv[1]);
    List *payload = (List *)encoded.as.o;
    if (payload->count > NET_MAX_FRAME) {
        tern_panic("NetError", "value is larger than %d bytes", NET_MAX_FRAME);
    }

    uint32_t length = (uint32_t)payload->count;
    unsigned char *frame = (unsigned char *)malloc((size_t)length + 4);
    frame[0] = (unsigned char)(length >> 24);
    frame[1] = (unsigned char)(length >> 16);
    frame[2] = (unsigned char)(length >> 8);
    frame[3] = (unsigned char)length;
    for (int32_t i = 0; i < payload->count; i++) {
        frame[4 + i] = (unsigned char)payload->items[i].as.i;
    }

    net_slot_write(slot, frame, (size_t)length + 4, "send");
    free(frame);
    return v_nil();
}

static void net_read_exactly(int slot, unsigned char *buffer, size_t length, bool first) {
    ssize_t got = net_slot_read(slot, buffer, length);
    if (got < 0) {
        if (errno == EAGAIN || errno == EWOULDBLOCK) {
            tern_panic("NetError", "frame timed out");
        }
        tern_panic("NetError", "receive() failed: %s", strerror(errno));
    }
    if ((size_t)got < length) {
        tern_panic("ConnectionError", first
                           ? "connection closed by the other side"
                           : "connection closed in the middle of a frame");
    }
}

static Value n_net_receive(int argc, Value *argv) {
    (void)argc;
    int slot = net_slot(net_handle_arg(argv[0]));
    net_require_stream(slot, "receive");
    int limit = net_max_frames[slot];

    unsigned char header[4];
    net_read_exactly(slot, header, 4, true);

    uint32_t length = ((uint32_t)header[0] << 24) | ((uint32_t)header[1] << 16) |
                      ((uint32_t)header[2] << 8) | (uint32_t)header[3];
    if (length > (uint32_t)limit) {
        tern_panic("NetError", "frame of %u bytes is larger than this socket's limit of %d",
                   (unsigned)length, limit);
    }

    unsigned char *payload = (unsigned char *)malloc(length > 0 ? length : 1);
    if (length > 0) {
        net_read_exactly(slot, payload, length, false);
    }

    List *bytes = list_new((int)length);
    for (uint32_t i = 0; i < length; i++) {
        list_push(bytes, v_int(payload[i]));
    }
    free(payload);

    return wire_decode(v_obj((Obj *)bytes));
}

static List *net_bytes_arg(Value v) {
    if (!v_is_list(v)) {
        tern_panic("TypeError", "expected a list of bytes, not %s", tern_type_name(v));
    }
    return (List *)v.as.o;
}

static Value n_net_send_bytes(int argc, Value *argv) {
    (void)argc;
    int slot = net_connection_slot(argv[0], "sendBytes");
    List *bytes = net_bytes_arg(argv[1]);

    size_t length = (size_t)bytes->count;
    unsigned char *buffer = (unsigned char *)malloc(length > 0 ? length : 1);
    for (size_t i = 0; i < length; i++) {
        Value byte = bytes->items[i];
        if (byte.tag != T_INT || byte.as.i < 0 || byte.as.i > 255) {
            free(buffer);
            tern_panic("TypeError", "byte %d is not an integer in 0..255", (int)i);
        }
        buffer[i] = (unsigned char)byte.as.i;
    }

    net_slot_write(slot, buffer, length, "sendBytes");
    free(buffer);
    return v_nil();
}

static Value n_net_receive_bytes(int argc, Value *argv) {
    (void)argc;
    int slot = net_connection_slot(argv[0], "receiveBytes");
    int count = net_arg_int(argv[1], "count");
    if (count < 0) {
        tern_panic("NetError", "receiveBytes() needs a count of 0 or more");
    }

    unsigned char *buffer = (unsigned char *)malloc(count > 0 ? (size_t)count : 1);
    if (count > 0) {
        net_read_exactly(slot, buffer, (size_t)count, true);
    }

    List *bytes = list_new(count);
    for (int i = 0; i < count; i++) {
        list_push(bytes, v_int(buffer[i]));
    }
    free(buffer);
    return v_obj((Obj *)bytes);
}

// --- datagrams ------------------------------------------------------------

#define NET_DATAGRAM_CEILING 65507

/// udpListen(port) | udpListen(port, options) | udpListen(host, port, options).
static Value n_net_udp_listen(int argc, Value *argv) {
    const char *host = "127.0.0.1";
    Value port_value = argv[0];
    Value options_value = argc > 1 && argv[1].tag != T_INT ? argv[1] : v_nil();
    if (argc > 1 && argv[1].tag == T_INT) {
        host = net_arg_text(argv[0], "host");
        port_value = argv[1];
        options_value = argc > 2 ? argv[2] : v_nil();
    }

    NetOptions options = net_options(options_value, "udpListen");
    int port = net_arg_int(port_value, "port");

    struct sockaddr_in address;
    net_resolve(host, &address, port);

    int fd = socket(AF_INET, SOCK_DGRAM, 0);
    if (fd < 0) {
        net_fail("cannot create a socket");
    }

    int reuse = 1;
    setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &reuse, sizeof reuse);

    if (bind(fd, (struct sockaddr *)&address, sizeof address) != 0) {
        int error = errno;
        close(fd);
        tern_panic("NetError", "cannot listen on %s:%d: %s", host, port, strerror(error));
    }

    if (options.timeout > 0) {
        struct timeval tv;
        tv.tv_sec = options.timeout / 1000;
        tv.tv_usec = (options.timeout % 1000) * 1000;
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof tv);
    }

    int handle = net_store(fd, 2);
    net_timeouts[handle - 1] = options.timeout;
    return v_int(handle);
}

static Value n_net_udp_send(int argc, Value *argv) {
    (void)argc;
    int slot = net_datagram_slot(argv[0], "udpSend");
    const char *host = net_arg_text(argv[1], "host");
    int port = net_arg_int(argv[2], "port");

    Value payload = argv[3];
    unsigned char *buffer = NULL;
    size_t length = 0;
    bool owns = true;

    if (v_is_str(payload)) {
        Str *text = (Str *)payload.as.o;
        length = (size_t)text->len;
        buffer = (unsigned char *)text->chars;
        owns = false;
    } else if (v_is_list(payload)) {
        List *bytes = net_bytes_arg(payload);
        length = (size_t)bytes->count;
        buffer = (unsigned char *)malloc(length > 0 ? length : 1);
        for (size_t i = 0; i < length; i++) {
            Value byte = bytes->items[i];
            if (byte.tag != T_INT || byte.as.i < 0 || byte.as.i > 255) {
                free(buffer);
                tern_panic("TypeError", "byte %d is not an integer in 0..255", (int)i);
            }
            buffer[i] = (unsigned char)byte.as.i;
        }
    } else {
        tern_panic("TypeError", "expected a string or a list of bytes, not %s",
                   tern_type_name(payload));
    }

    if (length > NET_DATAGRAM_CEILING) {
        if (owns) free(buffer);
        tern_panic("NetError", "datagram of %d bytes is larger than %d",
                   (int)length, NET_DATAGRAM_CEILING);
    }

    struct sockaddr_in address;
    net_resolve(host, &address, port);

    ssize_t sent = sendto(net_fds[slot], buffer, length, MSG_NOSIGNAL,
                          (struct sockaddr *)&address, sizeof address);
    if (owns) free(buffer);
    if (sent < 0) {
        tern_panic("NetError", "udpSend() failed: %s", strerror(errno));
    }

    return v_nil();
}

static Value n_net_udp_receive(int argc, Value *argv) {
    int slot = net_datagram_slot(argv[0], "udpReceive");
    int size = argc > 1 ? net_arg_int(argv[1], "size") : NET_DATAGRAM_CEILING;
    if (size < 0 || size > NET_DATAGRAM_CEILING) {
        tern_panic("NetError", "udpReceive() size must be between 0 and %d bytes",
                   NET_DATAGRAM_CEILING);
    }

    unsigned char *buffer = (unsigned char *)malloc(size > 0 ? (size_t)size : 1);
    struct sockaddr_in from;
    socklen_t from_size = sizeof from;
    ssize_t got = recvfrom(net_fds[slot], buffer, (size_t)size, 0,
                           (struct sockaddr *)&from, &from_size);
    if (got < 0) {
        free(buffer);
        if (errno == EAGAIN || errno == EWOULDBLOCK) {
            // A datagram that never arrives is normal traffic, not an error.
            return v_nil();
        }
        tern_panic("NetError", "udpReceive() failed: %s", strerror(errno));
    }

    char text[64];
    char address[INET_ADDRSTRLEN];
    inet_ntop(AF_INET, &from.sin_addr, address, sizeof address);
    snprintf(text, sizeof text, "%s:%d", address, ntohs(from.sin_port));

    List *bytes = list_new((int)got);
    for (ssize_t i = 0; i < got; i++) {
        list_push(bytes, v_int(buffer[i]));
    }
    free(buffer);

    Map *datagram = map_new();
    map_set(datagram, str_from_cstr("from"), str_from_cstr(text));
    map_set(datagram, str_from_cstr("data"), v_obj((Obj *)bytes));
    return v_obj((Obj *)datagram);
}

static Value n_net_close(int argc, Value *argv) {
    (void)argc;
    int handle = net_handle_arg(argv[0]);
    int slot = handle - 1;
    if (handle < 1 || handle > NET_MAX_HANDLES || net_fds[slot] < 0) {
        return v_nil();
    }
    if (net_ssl[slot]) {
        if (tls_state > 0) {
            tls_SSL_shutdown((SSL *)net_ssl[slot]);
            tls_SSL_free((SSL *)net_ssl[slot]);
            if (net_ssl_ctx[slot]) {
                tls_SSL_CTX_free((SSL_CTX *)net_ssl_ctx[slot]);
            }
        }
        net_ssl[slot] = NULL;
        net_ssl_ctx[slot] = NULL;
    }

    close(net_fds[slot]);
    net_fds[slot] = -1;
    net_kinds[slot] = 0;
    net_max_frames[slot] = NET_DEFAULT_MAX_FRAME;
    net_timeouts[slot] = 0;
    free(net_tokens[slot]);
    net_tokens[slot] = NULL;
    return v_nil();
}

static Value n_net_timeout(int argc, Value *argv) {
    (void)argc;
    int slot = net_slot(net_handle_arg(argv[0]));
    int milliseconds = net_arg_int(argv[1], "milliseconds");
    if (milliseconds < 0) {
        milliseconds = 0;
    }

    struct timeval tv;
    tv.tv_sec = milliseconds / 1000;
    tv.tv_usec = (milliseconds % 1000) * 1000;
    setsockopt(net_fds[slot], SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof tv);
    setsockopt(net_fds[slot], SOL_SOCKET, SO_SNDTIMEO, &tv, sizeof tv);
    net_timeouts[slot] = milliseconds;
    return v_nil();
}

static Value n_net_max_frame(int argc, Value *argv) {
    (void)argc;
    int slot = net_slot(net_handle_arg(argv[0]));
    double limit = net_arg_number(argv[1], "bytes");
    if (limit < 0 || limit > NET_MAX_FRAME) {
        tern_panic("NetError", "maxFrame() must be between 0 and %d bytes", NET_MAX_FRAME);
    }
    net_max_frames[slot] = (int)limit;
    return v_nil();
}

static Value n_net_port(int argc, Value *argv) {
    (void)argc;
    int slot = net_slot(net_handle_arg(argv[0]));
    return v_int(net_bound_port(net_fds[slot]));
}

static Value n_net_peer(int argc, Value *argv) {
    (void)argc;
    int slot = net_slot(net_handle_arg(argv[0]));

    struct sockaddr_in peer;
    socklen_t length = sizeof peer;
    if (getpeername(net_fds[slot], (struct sockaddr *)&peer, &length) != 0) {
        return v_nil();
    }

    char text[64];
    char address[INET_ADDRSTRLEN];
    inet_ntop(AF_INET, &peer.sin_addr, address, sizeof address);
    snprintf(text, sizeof text, "%s:%d", address, ntohs(peer.sin_port));
    return str_from_cstr(text);
}


// -------------------------------------------------------------------- http
//
// An HTTP/1.1 client written on the same sockets std:net uses. The pieces the
// C# side exposes separately (encodeRequest, decodeResponse, exchange) are here
// too, so a test can drive one half at a time and both back ends answer with the
// same bytes and the same messages.

#define HTTP_DEFAULT_TIMEOUT 10000
#define HTTP_DEFAULT_REDIRECTS 5
#define HTTP_DEFAULT_MAX_BYTES (8 * 1024 * 1024)
#define HTTP_MAX_HEADER_BYTES (64 * 1024)
#define HTTP_MAX_HEADERS 32

typedef struct {
    unsigned char *data;
    size_t len;
    size_t cap;
} ByteBuf;

static void bb_reserve(ByteBuf *buffer, size_t extra) {
    if (buffer->len + extra <= buffer->cap) {
        return;
    }
    size_t cap = buffer->cap ? buffer->cap : 256;
    while (cap < buffer->len + extra) {
        cap *= 2;
    }
    buffer->data = (unsigned char *)realloc(buffer->data, cap);
    buffer->cap = cap;
}

static void bb_add(ByteBuf *buffer, const void *data, size_t length) {
    bb_reserve(buffer, length);
    if (length) {
        memcpy(buffer->data + buffer->len, data, length);
    }
    buffer->len += length;
}

static void bb_add_text(ByteBuf *buffer, const char *text) {
    bb_add(buffer, text, strlen(text));
}

static void bb_free(ByteBuf *buffer) {
    free(buffer->data);
    buffer->data = NULL;
    buffer->len = 0;
    buffer->cap = 0;
}

/// A byte source over a socket or a complete buffer, so the parser is written
/// once. `fd` is -1 for the fixed-buffer form.
typedef struct {
    int fd;
    void *ssl;              // set when the connection is encrypted
    const unsigned char *fixed;
    size_t fixed_len;
    size_t pos;
    ByteBuf buffer;
    int max_bytes;
} HttpSource;

/// Reads from the source's transport: plain recv or the TLS layer.
static ssize_t http_recv(HttpSource *source, unsigned char *buffer, size_t length) {
    if (source->ssl) {
        return tls_read_once(source->ssl, buffer, length);
    }
    return recv(source->fd, buffer, length, 0);
}

static int http_source_available(HttpSource *source) {
    if (source->fd < 0) {
        return (int)(source->fixed_len - source->pos);
    }
    return (int)(source->buffer.len - source->pos);
}

static unsigned char http_byte(HttpSource *source, size_t index) {
    return source->fd < 0 ? source->fixed[index] : source->buffer.data[index];
}

/// Makes `need` bytes readable from the cursor. Returns false when a fixed
/// buffer runs out, which is how truncated input is detected.
static bool http_fill(HttpSource *source, int need) {
    if (http_source_available(source) >= need) {
        return true;
    }

    if (source->fd < 0) {
        return false;
    }

    unsigned char chunk[4096];
    while (http_source_available(source) < need) {
        ssize_t read = http_recv(source, chunk, sizeof chunk);
        if (read == 0) {
            tern_panic("ConnectionError", "connection closed while reading the response");
        }
        if (read < 0) {
            if (errno == EAGAIN || errno == EWOULDBLOCK) {
                tern_panic("HttpError", "the response timed out");
            }
            if (errno == EINTR) {
                continue;
            }
            tern_panic("ConnectionError", "connection closed while reading the response");
        }

        bb_add(&source->buffer, chunk, (size_t)read);
        if ((int)source->buffer.len > source->max_bytes + HTTP_MAX_HEADER_BYTES) {
            tern_panic("HttpError", "response body is larger than %d bytes", source->max_bytes);
        }
    }

    return true;
}

/// Reads one CRLF terminated line into a freshly allocated C string.
static char *http_line(HttpSource *source, const char *what) {
    int scanned = (int)source->pos;
    for (;;) {
        if (!http_fill(source, scanned - (int)source->pos + 1)) {
            tern_panic("HttpError", "response is incomplete (no end of %s)", what);
        }
        if (scanned - (int)source->pos > HTTP_MAX_HEADER_BYTES) {
            tern_panic("HttpError", "response header line is larger than %d bytes",
                       HTTP_MAX_HEADER_BYTES);
        }

        if (http_byte(source, (size_t)scanned) == '\n') {
            int end = scanned;
            if (scanned > (int)source->pos && http_byte(source, (size_t)(scanned - 1)) == '\r') {
                end = scanned - 1;
            }
            size_t length = (size_t)(end - (int)source->pos);
            char *text = (char *)malloc(length + 1);
            for (size_t i = 0; i < length; i++) {
                text[i] = (char)http_byte(source, (size_t)((int)source->pos + (int)i));
            }
            text[length] = '\0';
            source->pos = (size_t)(scanned + 1);
            return text;
        }

        scanned++;
    }
}

/// Copies `length` bytes out of the source.
static unsigned char *http_exact(HttpSource *source, int length, const char *what) {
    if (length < 0) {
        tern_panic("HttpError", "bad %s length: %d", what, length);
    }
    if (length > source->max_bytes) {
        tern_panic("HttpError", "response body is larger than %d bytes", source->max_bytes);
    }

    if (!http_fill(source, length)) {
        tern_panic("HttpError", "response is incomplete (wanted %d bytes of %s)", length, what);
    }

    unsigned char *result = (unsigned char *)malloc(length > 0 ? (size_t)length : 1);
    for (int i = 0; i < length; i++) {
        result[i] = http_byte(source, source->pos + (size_t)i);
    }
    source->pos += (size_t)length;
    return result;
}

/// Reads the rest of the stream, which is how a response without a length ends.
static unsigned char *http_to_end(HttpSource *source, int max_bytes, size_t *out_length) {
    if (source->fd < 0) {
        size_t remaining = source->fixed_len - source->pos;
        if (remaining > (size_t)max_bytes) {
            tern_panic("HttpError", "response body is larger than %d bytes", max_bytes);
        }
        unsigned char *result = (unsigned char *)malloc(remaining > 0 ? remaining : 1);
        memcpy(result, source->fixed + source->pos, remaining);
        source->pos = source->fixed_len;
        *out_length = remaining;
        return result;
    }

    unsigned char chunk[4096];
    for (;;) {
        ssize_t read = http_recv(source, chunk, sizeof chunk);
        if (read == 0) {
            break;
        }
        if (read < 0) {
            if (errno == EAGAIN || errno == EWOULDBLOCK) {
                tern_panic("HttpError", "the response timed out");
            }
            if (errno == EINTR) {
                continue;
            }
            tern_panic("ConnectionError", "connection closed while reading the response");
        }
        bb_add(&source->buffer, chunk, (size_t)read);
        if ((int)(source->buffer.len - source->pos) > max_bytes) {
            tern_panic("HttpError", "response body is larger than %d bytes", max_bytes);
        }
    }

    size_t length = source->buffer.len - source->pos;
    unsigned char *result = (unsigned char *)malloc(length > 0 ? length : 1);
    memcpy(result, source->buffer.data + source->pos, length);
    source->pos = source->buffer.len;
    *out_length = length;
    return result;
}

static unsigned char *http_chunked(HttpSource *source, int max_bytes, size_t *out_length) {
    ByteBuf body = { 0 };
    for (;;) {
        char *line = http_line(source, "chunk size");
        char *text = line;
        while (*text == ' ' || *text == '\t') text++;
        char *semicolon = strchr(text, ';');
        if (semicolon) *semicolon = '\0';
        size_t length = strlen(text);
        while (length > 0 && (text[length - 1] == ' ' || text[length - 1] == '\t')) {
            text[--length] = '\0';
        }

        long size = 0;
        bool ok = length > 0;
        for (size_t i = 0; ok && i < length; i++) {
            char c = text[i];
            int digit;
            if (c >= '0' && c <= '9') digit = c - '0';
            else if (c >= 'a' && c <= 'f') digit = c - 'a' + 10;
            else if (c >= 'A' && c <= 'F') digit = c - 'A' + 10;
            else { ok = false; break; }
            size = size * 16 + digit;
            if (size > 0x7FFFFFFF) { ok = false; }
        }

        if (!ok) {
            tern_panic("HttpError", "bad chunk size: '%s'", text);
        }

        if (size == 0) {
            free(line);
            for (;;) {
                char *trailer = http_line(source, "trailer");
                if (trailer[0] == '\0') {
                    free(trailer);
                    break;
                }
                free(trailer);
            }
            *out_length = body.len;
            return body.data;
        }

        if ((long)body.len + size > max_bytes) {
            free(line);
            bb_free(&body);
            tern_panic("HttpError", "response body is larger than %d bytes", max_bytes);
        }
        free(line);

        unsigned char *chunk = http_exact(source, (int)size, "chunk");
        bb_add(&body, chunk, (size_t)size);
        free(chunk);

        char *terminator = http_line(source, "chunk terminator");
        free(terminator);
    }
}

static bool http_all_digits(const char *text, size_t length) {
    if (length == 0) {
        return false;
    }
    for (size_t i = 0; i < length; i++) {
        if (text[i] < '0' || text[i] > '9') {
            return false;
        }
    }
    return true;
}

static bool http_parse_number(const char *text, long minimum, long maximum, long *out) {
    size_t length = strlen(text);
    if (!http_all_digits(text, length)) {
        return false;
    }
    long value = 0;
    for (size_t i = 0; i < length; i++) {
        value = value * 10 + (text[i] - '0');
        if (value > maximum) {
            return false;
        }
    }
    if (value < minimum) {
        return false;
    }
    *out = value;
    return true;
}

/// Parses "http(s)://host[:port][/path?query]" into its parts.
static void http_parse_url(const char *url, char **host, int *port, char **path, bool *tls) {
    bool secure = strncmp(url, "https://", 8) == 0;
    if (!secure && strncmp(url, "http://", 7) != 0) {
        tern_panic("HttpError", "only http:// and https:// URLs are supported, not '%s'", url);
    }
    if (tls) {
        *tls = secure;
    }

    const char *rest = url + (secure ? 8 : 7);
    int default_port = secure ? 443 : 80;
    const char *slash = strpbrk(rest, "/?");
    size_t authority_length = slash ? (size_t)(slash - rest) : strlen(rest);
    if (authority_length == 0) {
        tern_panic("HttpError", "URL has no host: '%s'", url);
    }

    if (slash) {
        if (*slash == '?') {
            *path = (char *)malloc(strlen(slash) + 2);
            (*path)[0] = '/';
            strcpy(*path + 1, slash);
        } else {
            *path = strdup(slash);
        }
    } else {
        *path = strdup("/");
    }

    char *authority = (char *)malloc(authority_length + 1);
    memcpy(authority, rest, authority_length);
    authority[authority_length] = '\0';

    char *colon = strrchr(authority, ':');
    *port = default_port;
    if (colon) {
        *colon = '\0';
        long parsed = 0;
        if (authority[0] == '\0' || !http_parse_number(colon + 1, 1, 65535, &parsed)) {
            free(authority);
            free(*path);
            tern_panic("HttpError", "URL has a bad port: '%s'", url);
        }
        *port = (int)parsed;
    }

    if (authority[0] == '\0') {
        free(authority);
        free(*path);
        tern_panic("HttpError", "URL has no host: '%s'", url);
    }

    *host = authority;
}

typedef struct {
    int count;
    Value names[HTTP_MAX_HEADERS];
    Value values[HTTP_MAX_HEADERS];
    Value body;
    bool has_body;
    int timeout;
    int redirects;
    int max_bytes;
    bool verify;
    const char *ca;
} HttpOptions;

static bool http_bytes_value(Value v, ByteBuf *out) {
    if (v_is_str(v)) {
        Str *text = (Str *)v.as.o;
        bb_add(out, text->chars, (size_t)text->len);
        return true;
    }

    if (v_is_list(v)) {
        List *bytes = (List *)v.as.o;
        for (int i = 0; i < bytes->count; i++) {
            Value byte = bytes->items[i];
            if (byte.tag != T_INT || byte.as.i < 0 || byte.as.i > 255) {
                tern_panic("TypeError", "byte %d is not an integer in 0..255", i);
            }
            unsigned char one = (unsigned char)byte.as.i;
            bb_add(out, &one, 1);
        }
        return true;
    }

    return false;
}

static void http_parse_options(Value v, const char *operation, HttpOptions *options) {
    options->count = 0;
    options->body = v_nil();
    options->has_body = false;
    options->timeout = HTTP_DEFAULT_TIMEOUT;
    options->redirects = HTTP_DEFAULT_REDIRECTS;
    options->max_bytes = HTTP_DEFAULT_MAX_BYTES;
    options->verify = true;
    options->ca = NULL;

    if (v.tag == T_NIL || v.tag == T_MISSING) {
        return;
    }

    if (!v_is_map(v)) {
        tern_panic("TypeError", "%s() options must be a map, not %s", operation, tern_type_name(v));
    }

    Map *map = (Map *)v.as.o;
    Value found;

    if (map_get(map, str_from_cstr("headers"), &found)) {
        if (!v_is_map(found)) {
            tern_panic("TypeError", "option 'headers' must be a map, not %s", tern_type_name(found));
        }

        Map *headers = (Map *)found.as.o;

        // Walk the map in insertion order, like the interpreter does.
        for (int i = 0; i < headers->order_count; i++) {
            Value key = headers->order[i];
            Value value = v_nil();
            map_get(headers, key, &value);
            if (!v_is_str(key) || !v_is_str(value)) {
                tern_panic("TypeError", "header names and values must be strings");
            }
            if (options->count >= HTTP_MAX_HEADERS) {
                tern_panic("HttpError", "too many request headers (limit %d)", HTTP_MAX_HEADERS);
            }

            Str *name = (Str *)key.as.o;
            Str *text = (Str *)value.as.o;
            bool token = name->len > 0;
            for (int c = 0; token && c < name->len; c++) {
                if (name->chars[c] <= ' ' || name->chars[c] >= 127 || name->chars[c] == ':') {
                    token = false;
                }
            }
            if (!token) {
                tern_panic("HttpError", "header name '%s' is not valid", name->chars);
            }
            if (strpbrk(text->chars, "\r\n") != NULL) {
                tern_panic("HttpError", "header '%s' contains a line break", name->chars);
            }

            options->names[options->count] = key;
            options->values[options->count] = value;
            options->count++;
        }
    }

    if (map_get(map, str_from_cstr("body"), &found)) {
        options->body = found;
        options->has_body = true;
    }

    if (map_get(map, str_from_cstr("timeout"), &found)) {
        double milliseconds = net_arg_number(found, "timeout");
        if (milliseconds < 0) {
            tern_panic("ArgumentError", "option 'timeout' must not be negative");
        }
        options->timeout = (int)milliseconds;
    }

    if (map_get(map, str_from_cstr("redirects"), &found)) {
        double count = net_arg_number(found, "redirects");
        if (count < 0 || count > 20) {
            tern_panic("ArgumentError", "option 'redirects' must be between 0 and 20");
        }
        options->redirects = (int)count;
    }

    if (map_get(map, str_from_cstr("verify"), &found)) {
        if (found.tag != T_BOOL) {
            tern_panic("TypeError", "option 'verify' must be a boolean, not %s", tern_type_name(found));
        }
        options->verify = found.as.i != 0;
    }

    if (map_get(map, str_from_cstr("ca"), &found)) {
        options->ca = net_arg_text(found, "option 'ca'");
    }

    if (map_get(map, str_from_cstr("maxBytes"), &found)) {
        double limit = net_arg_number(found, "maxBytes");
        if (limit < 0 || limit > 67108864) {
            tern_panic("ArgumentError", "option 'maxBytes' must be between 0 and 67108864");
        }
        options->max_bytes = (int)limit;
    }
}

static bool http_has_header(HttpOptions *options, const char *name) {
    for (int i = 0; i < options->count; i++) {
        Str *candidate = (Str *)options->names[i].as.o;
        if (strcasecmp(candidate->chars, name) == 0) {
            return true;
        }
    }
    return false;
}

/// Builds the request bytes: request line, the headers we own, the script's
/// headers in their own order, then the body.
static void http_encode_request(const char *method, const char *url, HttpOptions *options, ByteBuf *out) {
    if (method[0] == '\0' || strpbrk(method, " \r\n") != NULL) {
        tern_panic("HttpError", "method '%s' is not valid", method);
    }

    char *host = NULL;
    char *path = NULL;
    int port = 0;
    bool secure = false;
    http_parse_url(url, &host, &port, &path, &secure);

    char head[64];
    bb_add_text(out, method);
    bb_add_text(out, " ");
    bb_add_text(out, path);
    bb_add_text(out, " HTTP/1.1\r\n");

    if (!http_has_header(options, "Host")) {
        bb_add_text(out, "Host: ");
        bb_add_text(out, host);
        if (port != (secure ? 443 : 80)) {
            snprintf(head, sizeof head, ":%d", port);
            bb_add_text(out, head);
        }
        bb_add_text(out, "\r\n");
    }

    if (!http_has_header(options, "Connection")) {
        bb_add_text(out, "Connection: close\r\n");
    }

    if (!http_has_header(options, "User-Agent")) {
        bb_add_text(out, "User-Agent: TERN\r\n");
    }

    for (int i = 0; i < options->count; i++) {
        Str *name = (Str *)options->names[i].as.o;
        Str *value = (Str *)options->values[i].as.o;
        bb_add_text(out, name->chars);
        bb_add_text(out, ": ");
        bb_add_text(out, value->chars);
        bb_add_text(out, "\r\n");
    }

    ByteBuf body = { 0 };
    if (options->has_body && options->body.tag != T_NIL) {
        if (!http_bytes_value(options->body, &body)) {
            free(host);
            free(path);
            bb_free(&body);
            tern_panic("TypeError", "body must be a string or a list of bytes, not %s",
                       tern_type_name(options->body));
        }
        if (!http_has_header(options, "Content-Length")) {
            snprintf(head, sizeof head, "Content-Length: %d\r\n", (int)body.len);
            bb_add_text(out, head);
        }
    }

    bb_add_text(out, "\r\n");
    bb_add(out, body.data, body.len);
    bb_free(&body);
    free(host);
    free(path);
}

/// Parses one response from a source. `status_line`, `reason` and `version` are
/// needed by the redirect logic, so they come back through `out_*` when asked.
static Map *http_read(HttpSource *source, int max_bytes, char **out_reason, int *out_status) {
    char *status_line = http_line(source, "status line");
    if (strncmp(status_line, "HTTP/", 5) != 0) {
        free(status_line);
        tern_panic("HttpError", "the response does not start with an HTTP status line");
    }

    char *first_space = strchr(status_line, ' ');
    if (!first_space) {
        tern_panic("HttpError", "malformed status line: '%s'", status_line);
    }

    char *second_space = strchr(first_space + 1, ' ');
    size_t code_length = (second_space ? (size_t)(second_space - first_space - 1)
                                       : strlen(first_space + 1));
    char code_text[8];
    if (code_length == 0 || code_length > 3) {
        tern_panic("HttpError", "malformed status line: '%s'", status_line);
    }
    memcpy(code_text, first_space + 1, code_length);
    code_text[code_length] = '\0';

    long status = 0;
    if (!http_parse_number(code_text, 0, 999, &status) || code_length != 3) {
        tern_panic("HttpError", "malformed status line: '%s'", status_line);
    }

    char *reason = second_space ? strdup(second_space + 1) : strdup("");
    *first_space = '\0';
    char *version = strdup(status_line);
    free(status_line);

    Map *headers = map_new();
    List *order = list_new(4);
    for (;;) {
        char *line = http_line(source, "headers");
        if (line[0] == '\0') {
            free(line);
            break;
        }

        char *colon = strchr(line, ':');
        if (!colon || colon == line) {
            tern_panic("HttpError", "malformed header line: '%s'", line);
        }

        *colon = '\0';
        char *name = line;
        char *value = colon + 1;
        while (*value == ' ' || *value == '\t') value++;

        // Names are compared case-insensitively, so they are stored folded.
        for (char *p = name; *p; p++) {
            if (*p >= 'A' && *p <= 'Z') *p = (char)(*p - 'A' + 'a');
        }

        size_t value_length = strlen(value);
        while (value_length > 0 && (value[value_length - 1] == ' ' || value[value_length - 1] == '\t')) {
            value[--value_length] = '\0';
        }

        Value key = str_from_cstr(name);
        Value existing = v_nil();
        if (map_get(headers, key, &existing)) {
            ByteBuf joined = { 0 };
            bb_add_text(&joined, ((Str *)existing.as.o)->chars);
            bb_add_text(&joined, ", ");
            bb_add_text(&joined, value);
            map_set(headers, key, v_obj((Obj *)str_from((const char *)joined.data, joined.len)));
            bb_free(&joined);
        } else {
            map_set(headers, key, str_from_cstr(value));
            list_push(order, key);
        }

        free(line);
    }

    unsigned char *body = NULL;
    size_t body_length = 0;
    Value framing = v_nil();
    if (map_get(headers, str_from_cstr("transfer-encoding"), &framing) &&
        strstr(((Str *)framing.as.o)->chars, "chunked") != NULL) {
        body = http_chunked(source, max_bytes, &body_length);
    } else if (map_get(headers, str_from_cstr("content-length"), &framing)) {
        long length = 0;
        if (!http_parse_number(((Str *)framing.as.o)->chars, 0, 0x7FFFFFFF, &length)) {
            tern_panic("HttpError", "bad Content-Length: '%s'", ((Str *)framing.as.o)->chars);
        }
        if (length > max_bytes) {
            tern_panic("HttpError", "response body is larger than %d bytes", max_bytes);
        }
        body = http_exact(source, (int)length, "body");
        body_length = (size_t)length;
    } else {
        body = http_to_end(source, max_bytes, &body_length);
    }

    Map *response = map_new();
    map_set(response, str_from_cstr("status"), v_int(status));
    map_set(response, str_from_cstr("reason"), str_from_cstr(reason));
    map_set(response, str_from_cstr("version"), str_from_cstr(version));
    map_set(response, str_from_cstr("headers"), v_obj((Obj *)headers));

    Value body_value = v_obj((Obj *)str_from((const char *)body, body_length));
    map_set(response, str_from_cstr("body"), body_value);

    List *bytes = list_new((int)body_length);
    for (size_t i = 0; i < body_length; i++) {
        list_push(bytes, v_int(body[i]));
    }
    map_set(response, str_from_cstr("bytes"), v_obj((Obj *)bytes));
    free(body);

    if (out_reason) *out_reason = reason; else free(reason);
    if (out_status) *out_status = (int)status;
    free(version);
    return response;
}

static int http_connect(const char *host, int port, int timeout) {
    struct sockaddr_in address;
    net_resolve(host, &address, port);

    int fd = socket(AF_INET, SOCK_STREAM, 0);
    if (fd < 0) {
        tern_panic("HttpError", "cannot connect to %s:%d: %s", host, port, strerror(errno));
    }

    if (timeout > 0) {
        struct timeval tv;
        tv.tv_sec = timeout / 1000;
        tv.tv_usec = (timeout % 1000) * 1000;
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof tv);
        setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &tv, sizeof tv);
    }

    if (connect(fd, (struct sockaddr *)&address, sizeof address) != 0) {
        int error = errno;
        close(fd);
        tern_panic("HttpError", "cannot connect to %s:%d: %s", host, port, strerror(error));
    }

    return fd;
}

static void http_write_all(int fd, const unsigned char *data, size_t length);

static void http_write_stream(void *ssl, int fd, const unsigned char *data, size_t length) {
    if (ssl) {
        tls_write_all(ssl, data, length, "send");
        return;
    }
    http_write_all(fd, data, length);
}

static void http_write_all(int fd, const unsigned char *data, size_t length) {
    size_t sent = 0;
    while (sent < length) {
        ssize_t n = send(fd, data + sent, length - sent, MSG_NOSIGNAL);
        if (n <= 0) {
            if (errno == EAGAIN || errno == EWOULDBLOCK) {
                tern_panic("HttpError", "the request timed out");
            }
            tern_panic("ConnectionError", "connection closed while sending the request");
        }
        sent += (size_t)n;
    }
}

static bool http_is_redirect(int status) {
    return status == 301 || status == 302 || status == 303 || status == 307 || status == 308;
}

/// Resolves a Location header against the URL it came from.
static char *http_resolve_url(const char *base_url, const char *location) {
    if (strstr(location, "://") != NULL) {
        return strdup(location);
    }

    char *host = NULL;
    char *path = NULL;
    int port = 0;
    bool secure = false;
    http_parse_url(base_url, &host, &port, &path, &secure);

    ByteBuf out = { 0 };
    bb_add_text(&out, secure ? "https://" : "http://");
    bb_add_text(&out, host);
    if (port != (secure ? 443 : 80)) {
        char port_text[16];
        snprintf(port_text, sizeof port_text, ":%d", port);
        bb_add_text(&out, port_text);
    }

    if (location[0] == '/') {
        bb_add_text(&out, location);
    } else {
        char *slash = strrchr(path, '/');
        if (slash) {
            size_t prefix = (size_t)(slash - path) + 1;
            bb_add(&out, path, prefix);
        } else {
            bb_add_text(&out, "/");
        }
        bb_add_text(&out, location);
    }

    bb_add(&out, "", 1);
    free(host);
    free(path);
    return (char *)out.data;
}

static Map *http_send_impl(const char *method, const char *url, HttpOptions *options) {
    char *current_method = strdup(method);
    char *current_url = strdup(url);
    int hops = 0;

    for (;;) {
        char *host = NULL;
        char *path = NULL;
        int port = 0;
        bool secure = false;
        http_parse_url(current_url, &host, &port, &path, &secure);
        free(path);

        int fd = http_connect(host, port, options->timeout);
        void *ssl = NULL;
        void *ctx = NULL;
        if (secure) {
            SSL *handshake = NULL;
            SSL_CTX *handshake_ctx = NULL;
            tls_handshake(fd, host, options->verify, options->ca, &handshake, &handshake_ctx);
            ssl = (void *)handshake;
            ctx = (void *)handshake_ctx;
        }
        free(host);

        ByteBuf request = { 0 };
        http_encode_request(current_method, current_url, options, &request);
        http_write_stream(ssl, fd, request.data, request.len);
        bb_free(&request);

        HttpSource source;
        memset(&source, 0, sizeof source);
        source.fd = fd;
        source.ssl = ssl;
        source.max_bytes = options->max_bytes;

        char *reason = NULL;
        int status = 0;
        Map *response = http_read(&source, options->max_bytes, &reason, &status);
        bb_free(&source.buffer);

        if (ssl) {
            tls_SSL_shutdown((SSL *)ssl);
            tls_SSL_free((SSL *)ssl);
            tls_SSL_CTX_free((SSL_CTX *)ctx);
        }
        close(fd);

        Value location = v_nil();
        Value headers = v_nil();
        bool redirected = http_is_redirect(status) &&
                          map_get(response, str_from_cstr("headers"), &headers) &&
                          map_get((Map *)headers.as.o, str_from_cstr("location"), &location);

        if (!redirected) {
            free(reason);
            free(current_method);
            free(current_url);
            return response;
        }

        if (hops >= options->redirects) {
            free(reason);
            tern_panic("HttpError", "too many redirects (limit %d)", options->redirects);
        }

        hops++;
        char *next = http_resolve_url(current_url, ((Str *)location.as.o)->chars);
        free(current_url);
        current_url = next;
        if (status == 303) {
            free(current_method);
            current_method = strdup("GET");
            options->has_body = false;
        }
        free(reason);
    }
}

// --- the namespace members -------------------------------------------------

static Value n_http_request(int argc, Value *argv) {
    const char *method = net_arg_text(argv[0], "method");
    const char *url = net_arg_text(argv[1], "url");
    HttpOptions options;
    http_parse_options(argc > 2 ? argv[2] : v_nil(), "request", &options);
    return v_obj((Obj *)http_send_impl(method, url, &options));
}

static Value n_http_get(int argc, Value *argv) {
    const char *url = net_arg_text(argv[0], "url");
    HttpOptions options;
    http_parse_options(argc > 1 ? argv[1] : v_nil(), "request", &options);
    return v_obj((Obj *)http_send_impl("GET", url, &options));
}

static Value n_http_post(int argc, Value *argv) {
    const char *url = net_arg_text(argv[0], "url");
    const char *body = net_arg_text(argv[1], "body");
    HttpOptions options;
    http_parse_options(argc > 2 ? argv[2] : v_nil(), "post", &options);
    if (!options.has_body) {
        options.body = str_from_cstr(body);
        options.has_body = true;
    }
    return v_obj((Obj *)http_send_impl("POST", url, &options));
}

static Value n_http_encode_request(int argc, Value *argv) {
    const char *method = net_arg_text(argv[0], "method");
    const char *url = net_arg_text(argv[1], "url");
    HttpOptions options;
    http_parse_options(argc > 2 ? argv[2] : v_nil(), "encodeRequest", &options);

    ByteBuf request = { 0 };
    http_encode_request(method, url, &options, &request);

    List *bytes = list_new((int)request.len);
    for (size_t i = 0; i < request.len; i++) {
        list_push(bytes, v_int(request.data[i]));
    }
    bb_free(&request);
    return v_obj((Obj *)bytes);
}

static Value n_http_decode_response(int argc, Value *argv) {
    (void)argc;
    if (!v_is_list(argv[0])) {
        tern_panic("TypeError", "expected a list of bytes, not %s", tern_type_name(argv[0]));
    }

    List *bytes = (List *)argv[0].as.o;
    ByteBuf data = { 0 };
    for (int i = 0; i < bytes->count; i++) {
        Value byte = bytes->items[i];
        if (byte.tag != T_INT || byte.as.i < 0 || byte.as.i > 255) {
            bb_free(&data);
            tern_panic("TypeError", "byte %d is not an integer in 0..255", i);
        }
        unsigned char one = (unsigned char)byte.as.i;
        bb_add(&data, &one, 1);
    }

    HttpSource source;
    memset(&source, 0, sizeof source);
    source.fd = -1;
    source.fixed = data.data;
    source.fixed_len = data.len;
    source.max_bytes = HTTP_DEFAULT_MAX_BYTES;

    Map *response = http_read(&source, HTTP_DEFAULT_MAX_BYTES, NULL, NULL);
    bb_free(&data);
    return v_obj((Obj *)response);
}

static Value n_http_exchange(int argc, Value *argv) {
    int slot = net_slot(net_handle_arg(argv[0]));
    if (net_kinds[slot] != 0) {
        tern_panic("HttpError", "exchange() needs a connection handle from net.connect()");
    }

    const char *method = net_arg_text(argv[1], "method");
    const char *url = net_arg_text(argv[2], "url");
    HttpOptions options;
    http_parse_options(argc > 3 ? argv[3] : v_nil(), "exchange", &options);

    ByteBuf request = { 0 };
    http_encode_request(method, url, &options, &request);
    http_write_stream(net_ssl[slot], net_fds[slot], request.data, request.len);
    bb_free(&request);

    HttpSource source;
    memset(&source, 0, sizeof source);
    source.fd = net_fds[slot];
    source.ssl = net_ssl[slot];
    source.max_bytes = options.max_bytes;

    Map *response = http_read(&source, options.max_bytes, NULL, NULL);
    bb_free(&source.buffer);
    return v_obj((Obj *)response);
}


// -------------------------------------------------------------------- rcon
//
// The Source RCON protocol: a little-endian length, an id, a type, the body and
// two NUL bytes. Sessions live on net handles (kind 3), so rcon.attach can take
// over a connection opened elsewhere and both back ends agree on what every
// handle refers to.

#define RCON_DEFAULT_TIMEOUT 5000
#define RCON_MAX_PACKET (1024 * 1024)
#define RCON_TYPE_RESPONSE 0
#define RCON_TYPE_COMMAND 2
#define RCON_TYPE_AUTH 3

static void rcon_write_all(int fd, const unsigned char *data, size_t length) {
    size_t sent = 0;
    while (sent < length) {
        ssize_t n = send(fd, data + sent, length - sent, MSG_NOSIGNAL);
        if (n <= 0) {
            if (errno == EAGAIN || errno == EWOULDBLOCK) {
                tern_panic("RconError", "rcon timed out");
            }
            tern_panic("RconError", "rcon failed: %s", strerror(errno));
        }
        sent += (size_t)n;
    }
}

static void rcon_read_exactly(int fd, unsigned char *buffer, size_t length, bool first) {
    ssize_t got = net_read_raw(fd, buffer, length);
    if (got < 0) {
        if (errno == EAGAIN || errno == EWOULDBLOCK) {
            tern_panic("RconError", "rcon timed out");
        }
        tern_panic("RconError", "rcon failed: %s", strerror(errno));
    }
    if ((size_t)got < length) {
        tern_panic("ConnectionError", first
                           ? "connection closed by the other side"
                           : "connection closed in the middle of a frame");
    }
}

static void rcon_put_int32(unsigned char *buffer, int32_t value) {
    buffer[0] = (unsigned char)value;
    buffer[1] = (unsigned char)(value >> 8);
    buffer[2] = (unsigned char)(value >> 16);
    buffer[3] = (unsigned char)(value >> 24);
}

static int32_t rcon_get_int32(const unsigned char *buffer) {
    return (int32_t)((uint32_t)buffer[0] | ((uint32_t)buffer[1] << 8) |
                     ((uint32_t)buffer[2] << 16) | ((uint32_t)buffer[3] << 24));
}

static void rcon_send_packet(int fd, int32_t id, int32_t type, const char *body, size_t length) {
    size_t size = 4 + 4 + length + 2;
    unsigned char *packet = (unsigned char *)malloc(size + 4);
    rcon_put_int32(packet, (int32_t)size);
    rcon_put_int32(packet + 4, id);
    rcon_put_int32(packet + 8, type);
    memcpy(packet + 12, body, length);
    packet[12 + length] = 0;
    packet[13 + length] = 0;
    rcon_write_all(fd, packet, size + 4);
    free(packet);
}

/// Reads one packet, leaving the body in `body` (NUL terminated).
static void rcon_read_packet(int fd, int32_t *id, int32_t *type, ByteBuf *body) {
    unsigned char header[4];
    rcon_read_exactly(fd, header, 4, true);
    int32_t size = rcon_get_int32(header);
    if (size < 10 || size > RCON_MAX_PACKET) {
        tern_panic("RconError", "packet of %d bytes is out of range", size);
    }

    unsigned char *payload = (unsigned char *)malloc((size_t)size);
    rcon_read_exactly(fd, payload, (size_t)size, false);
    *id = rcon_get_int32(payload);
    *type = rcon_get_int32(payload + 4);
    bb_add(body, payload + 8, (size_t)size - 10);
    free(payload);
}

/// Logs in. Returns false when the server refuses the password; connection
/// problems raise, because they are not about the password.
static bool rcon_authenticate(int fd, const char *password) {
    rcon_send_packet(fd, 1, RCON_TYPE_AUTH, password, strlen(password));

    for (int attempt = 0; attempt < 8; attempt++) {
        ByteBuf body = { 0 };
        int32_t id = 0;
        int32_t type = 0;
        rcon_read_packet(fd, &id, &type, &body);
        bb_free(&body);

        if (type != RCON_TYPE_RESPONSE) {
            if (id == -1) {
                return false;
            }
            return true;
        }
    }

    return false;
}

static int rcon_fd(Value v, const char *operation) {
    int slot = net_slot(net_handle_arg(v));
    if (net_kinds[slot] != 3) {
        tern_panic("NetError", "%s() needs a handle from rcon.connect() or rcon.attach()", operation);
    }
    return net_fds[slot];
}

static int rcon_timeout(Value v, const char *operation) {
    if (v.tag == T_NIL || v.tag == T_MISSING) {
        return RCON_DEFAULT_TIMEOUT;
    }
    if (!v_is_map(v)) {
        tern_panic("TypeError", "%s() options must be a map, not %s", operation, tern_type_name(v));
    }

    Value found;
    if (!map_get((Map *)v.as.o, str_from_cstr("timeout"), &found)) {
        return RCON_DEFAULT_TIMEOUT;
    }

    double milliseconds = net_arg_number(found, "timeout");
    if (milliseconds < 0) {
        tern_panic("ArgumentError", "option 'timeout' must not be negative");
    }
    return (int)milliseconds;
}

static void rcon_set_timeout(int fd, int milliseconds) {
    struct timeval tv;
    tv.tv_sec = milliseconds / 1000;
    tv.tv_usec = (milliseconds % 1000) * 1000;
    setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof tv);
    setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &tv, sizeof tv);
}

static Value n_rcon_connect(int argc, Value *argv) {
    const char *host = net_arg_text(argv[0], "host");
    int port = net_arg_int(argv[1], "port");
    const char *password = net_arg_text(argv[2], "password");
    int timeout = rcon_timeout(argc > 3 ? argv[3] : v_nil(), "connect");

    struct sockaddr_in address;
    net_resolve(host, &address, port);

    int fd = socket(AF_INET, SOCK_STREAM, 0);
    if (fd < 0) {
        tern_panic("RconError", "cannot connect to %s:%d: %s", host, port, strerror(errno));
    }

    int one = 1;
    setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &one, sizeof one);
    rcon_set_timeout(fd, timeout);

    if (connect(fd, (struct sockaddr *)&address, sizeof address) != 0) {
        int error = errno;
        close(fd);
        tern_panic("RconError", "cannot connect to %s:%d: %s", host, port, strerror(error));
    }

    if (!rcon_authenticate(fd, password)) {
        close(fd);
        tern_panic("AuthenticationError", "rcon authentication failed");
    }

    int handle = net_store(fd, 3);
    net_timeouts[handle - 1] = timeout;
    return v_int(handle);
}

static Value n_rcon_attach(int argc, Value *argv) {
    int slot = net_slot(net_handle_arg(argv[0]));
    if (net_kinds[slot] != 0) {
        tern_panic("NetError", "attach() needs a connection handle from net.connect()");
    }

    const char *password = net_arg_text(argv[1], "password");
    int timeout = rcon_timeout(argc > 2 ? argv[2] : v_nil(), "attach");

    net_kinds[slot] = 3;
    net_timeouts[slot] = timeout;
    rcon_set_timeout(net_fds[slot], timeout);

    if (!rcon_authenticate(net_fds[slot], password)) {
        tern_panic("AuthenticationError", "rcon authentication failed");
    }

    return v_int(slot + 1);
}

static Value n_rcon_execute(int argc, Value *argv) {
    (void)argc;
    int fd = rcon_fd(argv[0], "execute");
    const char *command = net_arg_text(argv[1], "command");

    rcon_send_packet(fd, 1, RCON_TYPE_COMMAND, command, strlen(command));
    rcon_send_packet(fd, 2, RCON_TYPE_RESPONSE, "", 0);

    ByteBuf text = { 0 };
    for (int packets = 0; packets < 4096; packets++) {
        ByteBuf body = { 0 };
        int32_t id = 0;
        int32_t type = 0;
        rcon_read_packet(fd, &id, &type, &body);

        if (id == 2) {
            bb_free(&body);
            Value result = v_obj((Obj *)str_from(text.data ? (const char *)text.data : "", text.len));
            bb_free(&text);
            return result;
        }

        if (id == 1 && type == RCON_TYPE_RESPONSE) {
            bb_add(&text, body.data, body.len);
        }
        bb_free(&body);
    }

    bb_free(&text);
    tern_panic("RconError", "the server sent more than 4096 packets for one command");
    return v_nil();
}


// -------------------------------------------------------------------- sftp
//
// File transfer over SSH, driven through the OpenSSH client in batch mode: the
// commands go to sftp's standard input, the answer comes back on its output and
// BatchMode=yes makes a missing key fail instead of waiting for a password.
// Writing the SSH transport by hand is not on the table, so this is the same
// choice the interpreter makes.

#define SFTP_DEFAULT_TIMEOUT 30000
#define SFTP_MAX_ARGUMENTS 16

typedef struct {
    const char *host;
    const char *user;
    const char *key;
    const char *binary;
    int port;
    int timeout;
} SftpOptions;

static const char *sftp_arg_text(Value v, const char *what) {
    return net_arg_text(v, what);
}

static void sftp_parse_options(Value v, const char *operation, SftpOptions *options) {
    options->host = NULL;
    options->user = NULL;
    options->key = NULL;
    options->binary = "sftp";
    options->port = 22;
    options->timeout = SFTP_DEFAULT_TIMEOUT;

    if (!v_is_map(v)) {
        tern_panic("TypeError", "%s() options must be a map, not %s", operation, tern_type_name(v));
    }

    Map *map = (Map *)v.as.o;
    Value found;
    if (map_get(map, str_from_cstr("host"), &found)) {
        options->host = sftp_arg_text(found, "option 'host'");
    }
    if (map_get(map, str_from_cstr("user"), &found)) {
        options->user = sftp_arg_text(found, "option 'user'");
    }
    if (map_get(map, str_from_cstr("key"), &found)) {
        options->key = sftp_arg_text(found, "option 'key'");
    }
    if (map_get(map, str_from_cstr("binary"), &found)) {
        options->binary = sftp_arg_text(found, "option 'binary'");
        if (options->binary[0] == '\0') {
            tern_panic("ArgumentError", "option 'binary' must not be empty");
        }
    }
    if (map_get(map, str_from_cstr("port"), &found)) {
        double port = net_arg_number(found, "option 'port'");
        if (port < 1 || port > 65535) {
            tern_panic("ArgumentError", "option 'port' must be between 1 and 65535");
        }
        options->port = (int)port;
    }
    if (map_get(map, str_from_cstr("timeout"), &found)) {
        double milliseconds = net_arg_number(found, "option 'timeout'");
        if (milliseconds < 0) {
            tern_panic("ArgumentError", "option 'timeout' must not be negative");
        }
        options->timeout = (int)milliseconds;
    }

    if (!options->host || options->host[0] == '\0') {
        tern_panic("ArgumentError", "option 'host' is required");
    }
    if (!options->user || options->user[0] == '\0') {
        tern_panic("ArgumentError", "option 'user' is required");
    }
}

/// Splits a list of batch commands, refusing anything that is not one line.
static List *sftp_commands(Value v) {
    if (!v_is_list(v)) {
        tern_panic("TypeError", "expected a list of commands, not %s", tern_type_name(v));
    }

    List *list = (List *)v.as.o;
    for (int i = 0; i < list->count; i++) {
        Value item = list->items[i];
        if (!v_is_str(item)) {
            tern_panic("TypeError", "command %d must be a string, not %s", i, tern_type_name(item));
        }
        Str *text = (Str *)item.as.o;
        if (strpbrk(text->chars, "\r\n") != NULL) {
            tern_panic("SftpError", "command %d contains a line break", i);
        }
    }
    return list;
}

/// sftp has no escaping of its own, so a path with a space is quoted.
static void sftp_quote(const char *path, ByteBuf *out) {
    bool needs_quotes = strpbrk(path, " \t") != NULL;
    if (!needs_quotes) {
        bb_add_text(out, path);
        return;
    }

    bb_add_text(out, "\"");
    for (const char *p = path; *p; p++) {
        if (*p == '"') {
            bb_add_text(out, "\\\"");
        } else {
            bb_add(out, p, 1);
        }
    }
    bb_add_text(out, "\"");
}

/// The batch file text: one command per line.
static void sftp_batch_text(List *commands, ByteBuf *out) {
    for (int i = 0; i < commands->count; i++) {
        Str *text = (Str *)commands->items[i].as.o;
        bb_add(out, text->chars, (size_t)text->len);
        bb_add_text(out, "\n");
    }
}

/// The argv sftp is started with, in one place so command/run/tests agree.
static int sftp_argv(SftpOptions *options, char *port_text, size_t port_size,
                     const char *argv[SFTP_MAX_ARGUMENTS]) {
    snprintf(port_text, port_size, "%d", options->port);

    char *target = (char *)malloc(strlen(options->user) + strlen(options->host) + 2);
    sprintf(target, "%s@%s", options->user, options->host);

    int at = 0;
    argv[at++] = options->binary;
    argv[at++] = "-b";
    argv[at++] = "-";
    argv[at++] = "-P";
    argv[at++] = port_text;
    if (options->key) {
        argv[at++] = "-i";
        argv[at++] = options->key;
    }
    argv[at++] = "-o";
    argv[at++] = "BatchMode=yes";
    argv[at++] = "-o";
    argv[at++] = "StrictHostKeyChecking=accept-new";
    argv[at++] = target;
    argv[at] = NULL;
    return at;
}

/// Runs sftp with `batch` on its input. Returns the exit status, or -1 with a
/// message in `why` when the program could not be started or timed out.
static int sftp_exec(char *const argv[], const char *batch, size_t batch_length,
                     int timeout_ms, ByteBuf *out, ByteBuf *err, char *why, size_t why_size) {
    int to_child[2];
    int from_child[2];
    int errors[2];
    if (pipe(to_child) != 0 || pipe(from_child) != 0 || pipe(errors) != 0) {
        snprintf(why, why_size, "cannot run '%s': %s", argv[0], strerror(errno));
        return -1;
    }

    pid_t pid = fork();
    if (pid < 0) {
        snprintf(why, why_size, "cannot run '%s': %s", argv[0], strerror(errno));
        return -1;
    }

    if (pid == 0) {
        dup2(to_child[0], STDIN_FILENO);
        dup2(from_child[1], STDOUT_FILENO);
        dup2(errors[1], STDERR_FILENO);
        close(to_child[0]); close(to_child[1]);
        close(from_child[0]); close(from_child[1]);
        close(errors[0]); close(errors[1]);
        execvp(argv[0], argv);
        _exit(127);
    }

    close(to_child[0]);
    close(from_child[1]);
    close(errors[1]);

    size_t written = 0;
    while (written < batch_length) {
        ssize_t n = write(to_child[1], batch + written, batch_length - written);
        if (n <= 0) {
            if (errno == EINTR) continue;
            break;
        }
        written += (size_t)n;
    }
    close(to_child[1]);

    struct pollfd waiting[2];
    waiting[0].fd = from_child[0];
    waiting[0].events = POLLIN;
    waiting[0].revents = 0;
    waiting[1].fd = errors[0];
    waiting[1].events = POLLIN;
    waiting[1].revents = 0;

    struct timespec started;
    clock_gettime(CLOCK_MONOTONIC, &started);
    int open_pipes = 2;

    while (open_pipes > 0) {
        struct timespec now;
        clock_gettime(CLOCK_MONOTONIC, &now);
        long elapsed = (now.tv_sec - started.tv_sec) * 1000 +
                       (now.tv_nsec - started.tv_nsec) / 1000000;
        long remaining = (long)timeout_ms - elapsed;
        if (remaining <= 0) {
            kill(pid, SIGKILL);
            waitpid(pid, NULL, 0);
            snprintf(why, why_size, "'%s' timed out after %d ms", argv[0], timeout_ms);
            return -1;
        }

        int ready = poll(waiting, 2, (int)remaining);
        if (ready < 0) {
            if (errno == EINTR) continue;
            kill(pid, SIGKILL);
            waitpid(pid, NULL, 0);
            snprintf(why, why_size, "cannot run '%s': %s", argv[0], strerror(errno));
            return -1;
        }

        for (int i = 0; i < 2 && open_pipes > 0; i++) {
            if (waiting[i].fd < 0 || !(waiting[i].revents & (POLLIN | POLLHUP | POLLERR))) {
                continue;
            }

            unsigned char chunk[4096];
            ssize_t n = read(waiting[i].fd, chunk, sizeof chunk);
            if (n > 0) {
                bb_add(i == 0 ? out : err, chunk, (size_t)n);
            } else if (n == 0 || (errno != EINTR && errno != EAGAIN)) {
                close(waiting[i].fd);
                waiting[i].fd = -1;
                open_pipes--;
            }
        }
    }

    int status = 0;
    waitpid(pid, &status, 0);
    if (WIFEXITED(status)) {
        return WEXITSTATUS(status);
    }
    return -1;
}

/// Every batch call funnels through here: build the batch, run it, report.
static Value sftp_run_batch(Value options_value, const char *operation, List *commands, bool raise) {
    SftpOptions options;
    sftp_parse_options(options_value, operation, &options);
    if (options.key && access(options.key, R_OK) != 0) {
        tern_panic("SftpError", "cannot read the key file '%s'", options.key);
    }

    ByteBuf batch = { 0 };
    sftp_batch_text(commands, &batch);
    bb_add(&batch, "", 1);

    char port_text[16];
    const char *argv[SFTP_MAX_ARGUMENTS];
    sftp_argv(&options, port_text, sizeof port_text, argv);

    ByteBuf out = { 0 };
    ByteBuf err = { 0 };
    char why[128];
    why[0] = '\0';

    int status = sftp_exec((char *const *)argv, (const char *)batch.data, batch.len,
                           options.timeout, &out, &err, why, sizeof why);
    bb_free(&batch);

    if (status < 0) {
        bb_free(&out);
        bb_free(&err);
        tern_panic("SftpError", "%s", why[0] ? why : "sftp failed");
    }

    if (status == 127) {
        bb_free(&out);
        bb_free(&err);
        tern_panic("SftpError", "cannot run '%s': is it installed?", options.binary);
    }

    if (status != 0 && raise) {
        // The first non-empty line of stderr is the useful part of the message.
        size_t start = 0;
        for (size_t i = 0; i <= err.len; i++) {
            if (i == err.len || err.data[i] == '\n') {
                size_t begin = start;
                while (begin < i && (err.data[begin] == ' ' || err.data[begin] == '\t')) begin++;
                size_t end = i;
                while (end > begin && (err.data[end - 1] == '\r' || err.data[end - 1] == ' ')) end--;
                if (end > begin) {
                    char *text = (char *)malloc(end - begin + 1);
                    memcpy(text, err.data + begin, end - begin);
                    text[end - begin] = '\0';
                    tern_panic("SftpError", "sftp failed with status %d: %s", status, text);
                }
                start = i + 1;
            }
        }
        tern_panic("SftpError", "sftp failed with status %d", status);
    }

    Map *report = map_new();
    map_set(report, str_from_cstr("status"), v_int(status));
    map_set(report, str_from_cstr("output"), v_obj((Obj *)str_from((const char *)out.data, out.len)));
    map_set(report, str_from_cstr("error"), v_obj((Obj *)str_from((const char *)err.data, err.len)));
    bb_free(&out);
    bb_free(&err);
    return v_obj((Obj *)report);
}

static Value n_sftp_batch(int argc, Value *argv) {
    (void)argc;
    List *commands = sftp_commands(argv[0]);
    ByteBuf text = { 0 };
    sftp_batch_text(commands, &text);
    Value result = v_obj((Obj *)str_from(text.data ? (const char *)text.data : "", text.len));
    bb_free(&text);
    return result;
}

static Value n_sftp_command(int argc, Value *argv) {
    (void)argc;
    SftpOptions options;
    sftp_parse_options(argv[0], "command", &options);

    char port_text[16];
    const char *argv2[SFTP_MAX_ARGUMENTS];
    int count = sftp_argv(&options, port_text, sizeof port_text, argv2);

    List *parts = list_new(count);
    for (int i = 0; i < count; i++) {
        list_push(parts, str_from_cstr(argv2[i]));
    }
    return v_obj((Obj *)parts);
}

static Value n_sftp_upload(int argc, Value *argv) {
    const char *local = sftp_arg_text(argv[0], "local");
    const char *remote = sftp_arg_text(argv[1], "remote");

    if (access(local, R_OK) != 0) {
        tern_panic("SftpError", "local file '%s' does not exist", local);
    }

    ByteBuf line = { 0 };
    bb_add_text(&line, "put ");
    sftp_quote(local, &line);
    bb_add_text(&line, " ");
    sftp_quote(remote, &line);

    List *commands = list_new(1);
    list_push(commands, v_obj((Obj *)str_from((const char *)line.data, line.len)));
    bb_free(&line);

    sftp_run_batch(argc > 2 ? argv[2] : v_nil(), "upload", commands, true);
    return v_nil();
}

static Value n_sftp_download(int argc, Value *argv) {
    const char *remote = sftp_arg_text(argv[0], "remote");
    const char *local = sftp_arg_text(argv[1], "local");

    ByteBuf line = { 0 };
    bb_add_text(&line, "get ");
    sftp_quote(remote, &line);
    bb_add_text(&line, " ");
    sftp_quote(local, &line);

    List *commands = list_new(1);
    list_push(commands, v_obj((Obj *)str_from((const char *)line.data, line.len)));
    bb_free(&line);

    sftp_run_batch(argc > 2 ? argv[2] : v_nil(), "download", commands, true);
    return v_nil();
}

static Value n_sftp_list(int argc, Value *argv) {
    const char *remote = sftp_arg_text(argv[0], "remote");

    ByteBuf line = { 0 };
    bb_add_text(&line, "ls -1 ");
    sftp_quote(remote, &line);

    List *commands = list_new(1);
    list_push(commands, v_obj((Obj *)str_from((const char *)line.data, line.len)));
    bb_free(&line);

    Value report = sftp_run_batch(argc > 1 ? argv[1] : v_nil(), "list", commands, true);
    Map *map = (Map *)report.as.o;
    Value output = v_nil();
    map_get(map, str_from_cstr("output"), &output);

    Str *text = (Str *)output.as.o;
    List *names = list_new(8);
    size_t start = 0;
    for (size_t i = 0; i <= (size_t)text->len; i++) {
        if (i == (size_t)text->len || text->chars[i] == '\n') {
            size_t end = i;
            while (end > start && text->chars[end - 1] == '\r') end--;
            if (end > start) {
                list_push(names, v_obj((Obj *)str_from(text->chars + start, end - start)));
            }
            start = i + 1;
        }
    }
    return v_obj((Obj *)names);
}

static Value n_sftp_run(int argc, Value *argv) {
    (void)argc;
    List *commands = sftp_commands(argv[0]);
    return sftp_run_batch(argv[1], "run", commands, false);
}

// --- gl: OpenGL through libEGL ---------------------------------------------
//
// The same shape as the interpreter's binding: libEGL is opened at run time with
// dlopen, a surfaceless pbuffer gives a context with no display server, and the
// GL entry points come from eglGetProcAddress. Nothing is linked into the
// binary, so a program that never renders does not need a driver.

#define GL_MAX_CONTEXTS 8
#define GL_MAX_PROGRAMS 64
#define GL_VERTEX_FLOATS 6

#define EGL_NONE 0x3038
#define EGL_SURFACE_TYPE 0x3033
#define EGL_PBUFFER_BIT 0x0001
#define EGL_RENDERABLE_TYPE 0x3040
#define EGL_OPENGL_BIT 0x0008
#define EGL_RED_SIZE 0x3024
#define EGL_GREEN_SIZE 0x3023
#define EGL_BLUE_SIZE 0x3022
#define EGL_ALPHA_SIZE 0x3021
#define EGL_DEPTH_SIZE 0x3025
#define EGL_WIDTH 0x3057
#define EGL_HEIGHT 0x3056
#define EGL_CONTEXT_MAJOR_VERSION 0x3098
#define EGL_CONTEXT_MINOR_VERSION 0x30FB
#define EGL_CONTEXT_OPENGL_PROFILE_MASK 0x30FD
#define EGL_CONTEXT_OPENGL_CORE_PROFILE_BIT 0x0001
#define EGL_OPENGL_API 0x30A2
#define EGL_PLATFORM_SURFACELESS 0x313D

#define GL_COLOR_BUFFER_BIT 0x00004000u
#define GL_DEPTH_BUFFER_BIT 0x00000100u
#define GL_ARRAY_BUFFER 0x8892u
#define GL_STATIC_DRAW 0x88E4u
#define GL_FLOAT 0x1406u
#define GL_TRIANGLES 0x0004u
#define GL_VERTEX_SHADER 0x8B31u
#define GL_FRAGMENT_SHADER 0x8B30u
#define GL_COMPILE_STATUS 0x8B81u
#define GL_LINK_STATUS 0x8B82u
#define GL_RGBA 0x1908u
#define GL_UNSIGNED_BYTE 0x1401u
#define GL_VENDOR 0x1F00u
#define GL_RENDERER 0x1F01u
#define GL_VERSION 0x1F02u
#define GL_SHADING_LANGUAGE_VERSION 0x8B8Cu

typedef void (*GlClearColorFn)(float, float, float, float);
typedef void (*GlClearFn)(unsigned);
typedef void (*GlViewportFn)(int, int, int, int);
typedef unsigned (*GlCreateShaderFn)(unsigned);
typedef void (*GlShaderSourceFn)(unsigned, int, const char *const *, const int *);
typedef void (*GlCompileShaderFn)(unsigned);
typedef void (*GlGetShaderivFn)(unsigned, unsigned, int *);
typedef void (*GlGetShaderInfoLogFn)(unsigned, int, int *, char *);
typedef unsigned (*GlCreateProgramFn)(void);
typedef void (*GlAttachShaderFn)(unsigned, unsigned);
typedef void (*GlLinkProgramFn)(unsigned);
typedef void (*GlGetProgramivFn)(unsigned, unsigned, int *);
typedef void (*GlGetProgramInfoLogFn)(unsigned, int, int *, char *);
typedef void (*GlUseProgramFn)(unsigned);
typedef void (*GlGenVertexArraysFn)(int, unsigned *);
typedef void (*GlBindVertexArrayFn)(unsigned);
typedef void (*GlGenBuffersFn)(int, unsigned *);
typedef void (*GlBindBufferFn)(unsigned, unsigned);
typedef void (*GlBufferDataFn)(unsigned, ptrdiff_t, const void *, unsigned);
typedef void (*GlEnableVertexAttribArrayFn)(unsigned);
typedef void (*GlVertexAttribPointerFn)(unsigned, int, unsigned, unsigned char, int, const void *);
typedef void (*GlDrawArraysFn)(unsigned, int, int);
typedef void (*GlReadPixelsFn)(int, int, int, int, unsigned, unsigned, void *);
typedef const unsigned char *(*GlGetStringFn)(unsigned);
typedef void (*GlFinishFn)(void);
typedef void (*GlDeleteProgramFn)(unsigned);
typedef void (*GlDeleteShaderFn)(unsigned);
typedef void (*GlDeleteBuffersFn)(int, const unsigned *);
typedef void (*GlDeleteVertexArraysFn)(int, const unsigned *);

typedef struct {
    void *library;
    void *(*get_display)(void *);
    void *(*get_platform_display)(unsigned, void *, const int *);
    int (*initialize)(void *, int *, int *);
    int (*bind_api)(unsigned);
    int (*choose_config)(void *, const int *, void **, int, int *);
    void *(*create_context)(void *, void *, void *, const int *);
    void *(*create_pbuffer_surface)(void *, void *, const int *);
    int (*make_current)(void *, void *, void *, void *);
    void *(*get_proc_address)(const char *);
    void (*destroy_surface)(void *, void *);
    void (*destroy_context)(void *, void *);
    int ready;
} GlDriver;

typedef struct {
    void *display;
    void *surface;
    void *context;
    int width, height;
    unsigned buffer, array;
    unsigned programs[GL_MAX_PROGRAMS];
    int program_count;

    GlClearColorFn clear_color;
    GlClearFn clear;
    GlViewportFn viewport;
    GlCreateShaderFn create_shader;
    GlShaderSourceFn shader_source;
    GlCompileShaderFn compile_shader;
    GlGetShaderivFn get_shaderiv;
    GlGetShaderInfoLogFn get_shader_info_log;
    GlCreateProgramFn create_program;
    GlAttachShaderFn attach_shader;
    GlLinkProgramFn link_program;
    GlGetProgramivFn get_programiv;
    GlGetProgramInfoLogFn get_program_info_log;
    GlUseProgramFn use_program;
    GlGenVertexArraysFn gen_vertex_arrays;
    GlBindVertexArrayFn bind_vertex_array;
    GlGenBuffersFn gen_buffers;
    GlBindBufferFn bind_buffer;
    GlBufferDataFn buffer_data;
    GlEnableVertexAttribArrayFn enable_vertex_attrib_array;
    GlVertexAttribPointerFn vertex_attrib_pointer;
    GlDrawArraysFn draw_arrays;
    GlReadPixelsFn read_pixels;
    GlGetStringFn get_string;
    GlFinishFn gl_finish;
    GlDeleteProgramFn delete_program;
    GlDeleteShaderFn delete_shader;
    GlDeleteBuffersFn delete_buffers;
    GlDeleteVertexArraysFn delete_vertex_arrays;
} GlContext;

static GlDriver gl_driver;
static int gl_driver_tried;
static GlContext *gl_contexts[GL_MAX_CONTEXTS];

static GlDriver *gl_driver_get(void) {
    if (gl_driver_tried) {
        return gl_driver.ready ? &gl_driver : NULL;
    }
    gl_driver_tried = 1;

    const char *names[3];
    int count = 0;
    // TERN_GL_LIBRARY names the library and suppresses the fallback, so a test
    // can ask for a driver that is not there and see the failure.
    const char *configured = getenv("TERN_GL_LIBRARY");
    if (configured && configured[0]) {
        names[count++] = configured;
    } else {
        names[count++] = "libEGL.so.1";
        names[count++] = "libEGL.so";
    }

    for (int i = 0; i < count; i++) {
        void *library = dlopen(names[i], RTLD_NOW | RTLD_LOCAL);
        if (!library) {
            continue;
        }

        gl_driver.library = library;
        *(void **)(&gl_driver.get_display) = dlsym(library, "eglGetDisplay");
        *(void **)(&gl_driver.get_platform_display) = dlsym(library, "eglGetPlatformDisplayEXT");
        *(void **)(&gl_driver.initialize) = dlsym(library, "eglInitialize");
        *(void **)(&gl_driver.bind_api) = dlsym(library, "eglBindAPI");
        *(void **)(&gl_driver.choose_config) = dlsym(library, "eglChooseConfig");
        *(void **)(&gl_driver.create_context) = dlsym(library, "eglCreateContext");
        *(void **)(&gl_driver.create_pbuffer_surface) = dlsym(library, "eglCreatePbufferSurface");
        *(void **)(&gl_driver.make_current) = dlsym(library, "eglMakeCurrent");
        *(void **)(&gl_driver.get_proc_address) = dlsym(library, "eglGetProcAddress");
        *(void **)(&gl_driver.destroy_surface) = dlsym(library, "eglDestroySurface");
        *(void **)(&gl_driver.destroy_context) = dlsym(library, "eglDestroyContext");

        gl_driver.ready = gl_driver.initialize && gl_driver.choose_config
            && gl_driver.create_context && gl_driver.create_pbuffer_surface
            && gl_driver.make_current && gl_driver.get_proc_address
            && (gl_driver.get_display || gl_driver.get_platform_display);
        if (gl_driver.ready) {
            return &gl_driver;
        }
    }
    return NULL;
}

static int gl_arg_dimension(Value v, const char *what, int index) {
    int64_t size = need_int(v, "create", index);
    if (size < 1 || size > 8192) {
        tern_panic("ArgumentError", "%s must be between 1 and 8192", what);
    }
    return (int)size;
}

static double gl_arg_unit(Value v, const char *what) {
    if (!v_is_num(v)) {
        tern_panic("TypeError", "%s must be a number, not %s", what, tern_type_name(v));
    }
    double number = v_as_double(v);
    if (number < 0 || number > 1) {
        tern_panic("ArgumentError", "%s must be between 0 and 1", what);
    }
    return number;
}

static void gl_load_entry_points(GlContext *ctx, const char *operation) {
    void *(*proc)(const char *) = gl_driver.get_proc_address;
    *(void **)(&ctx->clear_color) = proc("glClearColor");
    *(void **)(&ctx->clear) = proc("glClear");
    *(void **)(&ctx->viewport) = proc("glViewport");
    *(void **)(&ctx->create_shader) = proc("glCreateShader");
    *(void **)(&ctx->shader_source) = proc("glShaderSource");
    *(void **)(&ctx->compile_shader) = proc("glCompileShader");
    *(void **)(&ctx->get_shaderiv) = proc("glGetShaderiv");
    *(void **)(&ctx->get_shader_info_log) = proc("glGetShaderInfoLog");
    *(void **)(&ctx->create_program) = proc("glCreateProgram");
    *(void **)(&ctx->attach_shader) = proc("glAttachShader");
    *(void **)(&ctx->link_program) = proc("glLinkProgram");
    *(void **)(&ctx->get_programiv) = proc("glGetProgramiv");
    *(void **)(&ctx->get_program_info_log) = proc("glGetProgramInfoLog");
    *(void **)(&ctx->use_program) = proc("glUseProgram");
    *(void **)(&ctx->gen_vertex_arrays) = proc("glGenVertexArrays");
    *(void **)(&ctx->bind_vertex_array) = proc("glBindVertexArray");
    *(void **)(&ctx->gen_buffers) = proc("glGenBuffers");
    *(void **)(&ctx->bind_buffer) = proc("glBindBuffer");
    *(void **)(&ctx->buffer_data) = proc("glBufferData");
    *(void **)(&ctx->enable_vertex_attrib_array) = proc("glEnableVertexAttribArray");
    *(void **)(&ctx->vertex_attrib_pointer) = proc("glVertexAttribPointer");
    *(void **)(&ctx->draw_arrays) = proc("glDrawArrays");
    *(void **)(&ctx->read_pixels) = proc("glReadPixels");
    *(void **)(&ctx->get_string) = proc("glGetString");
    *(void **)(&ctx->gl_finish) = proc("glFinish");
    *(void **)(&ctx->delete_program) = proc("glDeleteProgram");
    *(void **)(&ctx->delete_shader) = proc("glDeleteShader");
    *(void **)(&ctx->delete_buffers) = proc("glDeleteBuffers");
    *(void **)(&ctx->delete_vertex_arrays) = proc("glDeleteVertexArrays");

    if (!ctx->clear || !ctx->clear_color || !ctx->viewport || !ctx->create_shader
        || !ctx->shader_source || !ctx->compile_shader || !ctx->get_shaderiv
        || !ctx->create_program || !ctx->attach_shader || !ctx->link_program
        || !ctx->get_programiv || !ctx->use_program || !ctx->gen_vertex_arrays
        || !ctx->bind_vertex_array || !ctx->gen_buffers || !ctx->bind_buffer
        || !ctx->buffer_data || !ctx->enable_vertex_attrib_array
        || !ctx->vertex_attrib_pointer || !ctx->draw_arrays || !ctx->read_pixels
        || !ctx->gl_finish) {
        tern_panic("GLError", "OpenGL is not available on this system: the driver is missing what %s needs",
                   operation);
    }
}

static GlContext *gl_context_make(int width, int height, const char *operation) {
    GlDriver *driver = gl_driver_get();
    if (!driver) {
        tern_panic("GLError", "OpenGL is not available on this system: libEGL could not be loaded");
    }

    void *display = NULL;
    if (driver->get_platform_display) {
        display = driver->get_platform_display(EGL_PLATFORM_SURFACELESS, NULL, NULL);
    }
    if (!display && driver->get_display) {
        display = driver->get_display(NULL);
    }
    if (!display) {
        tern_panic("GLError", "OpenGL is not available on this system: no EGL display");
    }
    if (driver->initialize(display, NULL, NULL) == 0) {
        tern_panic("GLError", "OpenGL is not available on this system: eglInitialize failed");
    }
    if (driver->bind_api && driver->bind_api(EGL_OPENGL_API) == 0) {
        tern_panic("GLError", "OpenGL is not available on this system: EGL has no OpenGL support");
    }

    const int config_attributes[] = {
        EGL_SURFACE_TYPE, EGL_PBUFFER_BIT,
        EGL_RENDERABLE_TYPE, EGL_OPENGL_BIT,
        EGL_RED_SIZE, 8, EGL_GREEN_SIZE, 8, EGL_BLUE_SIZE, 8, EGL_ALPHA_SIZE, 8,
        EGL_DEPTH_SIZE, 24,
        EGL_NONE,
    };
    void *config = NULL;
    int count = 0;
    if (driver->choose_config(display, config_attributes, &config, 1, &count) == 0 || count < 1) {
        tern_panic("GLError", "OpenGL is not available on this system: no matching EGL config");
    }

    const int context_attributes[] = {
        EGL_CONTEXT_MAJOR_VERSION, 3, EGL_CONTEXT_MINOR_VERSION, 3,
        EGL_CONTEXT_OPENGL_PROFILE_MASK, EGL_CONTEXT_OPENGL_CORE_PROFILE_BIT,
        EGL_NONE,
    };
    void *context = driver->create_context(display, config, NULL, context_attributes);
    if (!context) {
        tern_panic("GLError", "OpenGL is not available on this system: cannot create an OpenGL 3.3 context");
    }

    const int surface_attributes[] = { EGL_WIDTH, width, EGL_HEIGHT, height, EGL_NONE };
    void *surface = driver->create_pbuffer_surface(display, config, surface_attributes);
    if (!surface) {
        tern_panic("GLError", "OpenGL is not available on this system: cannot create a %dx%d surface", width, height);
    }
    if (driver->make_current(display, surface, surface, context) == 0) {
        tern_panic("GLError", "OpenGL is not available on this system: cannot make the context current");
    }

    GlContext *made = (GlContext *)calloc(1, sizeof(GlContext));
    if (!made) {
        tern_panic("GLError", "OpenGL is not available on this system: out of memory");
    }
    made->display = display;
    made->surface = surface;
    made->context = context;
    made->width = width;
    made->height = height;

    gl_load_entry_points(made, operation);
    made->viewport(0, 0, width, height);
    made->gen_vertex_arrays(1, &made->array);
    made->bind_vertex_array(made->array);
    made->gen_buffers(1, &made->buffer);
    made->bind_buffer(GL_ARRAY_BUFFER, made->buffer);
    if (made->array == 0 || made->buffer == 0) {
        tern_panic("GLError", "OpenGL is not available on this system: cannot prepare %s", operation);
    }
    return made;
}

static void gl_context_free(GlContext *ctx) {
    if (!ctx) {
        return;
    }
    for (int i = 0; i < ctx->program_count; i++) {
        if (ctx->delete_program) ctx->delete_program(ctx->programs[i]);
    }
    if (ctx->buffer && ctx->delete_buffers) ctx->delete_buffers(1, &ctx->buffer);
    if (ctx->array && ctx->delete_vertex_arrays) ctx->delete_vertex_arrays(1, &ctx->array);
    if (gl_driver.make_current) gl_driver.make_current(ctx->display, NULL, NULL, NULL);
    if (ctx->surface && gl_driver.destroy_surface) gl_driver.destroy_surface(ctx->display, ctx->surface);
    if (ctx->context && gl_driver.destroy_context) gl_driver.destroy_context(ctx->display, ctx->context);
    free(ctx);
}

static int gl_slot_arg(Value v) {
    if (v.tag != T_INT) {
        tern_panic("TypeError", "expected a context handle, not %s", tern_type_name(v));
    }
    int handle = (int)v.as.i;
    if (handle < 1 || handle > GL_MAX_CONTEXTS || !gl_contexts[handle - 1]) {
        tern_panic("GLError", "no open context with handle %d", handle);
    }
    return handle - 1;
}

static unsigned gl_program_arg(GlContext *ctx, Value v) {
    if (v.tag != T_INT) {
        tern_panic("TypeError", "expected a program handle, not %s", tern_type_name(v));
    }
    int handle = (int)v.as.i;
    if (handle < 1 || handle > ctx->program_count) {
        tern_panic("GLError", "no shader program with handle %d", handle);
    }
    return ctx->programs[handle - 1];
}

static const char *gl_string(GlContext *ctx, unsigned name) {
    if (!ctx->get_string) {
        return "";
    }
    const unsigned char *text = ctx->get_string(name);
    return text ? (const char *)text : "";
}

static Value n_gl_available(int argc, Value *argv) {
    (void)argc;
    (void)argv;
    return v_bool(gl_driver_get() != NULL);
}

static Value n_gl_info(int argc, Value *argv) {
    (void)argc;
    (void)argv;
    GlContext *ctx = gl_context_make(16, 16, "info");
    Map *report = map_new();
    map_set(report, str_from_cstr("vendor"), str_from_cstr(gl_string(ctx, GL_VENDOR)));
    map_set(report, str_from_cstr("renderer"), str_from_cstr(gl_string(ctx, GL_RENDERER)));
    map_set(report, str_from_cstr("version"), str_from_cstr(gl_string(ctx, GL_VERSION)));
    map_set(report, str_from_cstr("glsl"), str_from_cstr(gl_string(ctx, GL_SHADING_LANGUAGE_VERSION)));
    gl_context_free(ctx);
    return v_obj((Obj *)report);
}

static Value n_gl_create(int argc, Value *argv) {
    int width = gl_arg_dimension(argv[0], "width", 1);
    int height = gl_arg_dimension(arg(argc, argv, 1), "height", 2);

    int slot = -1;
    for (int i = 0; i < GL_MAX_CONTEXTS; i++) {
        if (!gl_contexts[i]) {
            slot = i;
            break;
        }
    }
    if (slot < 0) {
        tern_panic("GLError", "too many OpenGL contexts (%d)", GL_MAX_CONTEXTS);
    }

    gl_contexts[slot] = gl_context_make(width, height, "create");
    return v_int(slot + 1);
}

static Value n_gl_clear(int argc, Value *argv) {
    GlContext *ctx = gl_contexts[gl_slot_arg(argv[0])];
    float r = (float)gl_arg_unit(arg(argc, argv, 1), "red");
    float g = (float)gl_arg_unit(arg(argc, argv, 2), "green");
    float b = (float)gl_arg_unit(arg(argc, argv, 3), "blue");
    float a = (float)gl_arg_unit(arg(argc, argv, 4), "alpha");
    ctx->clear_color(r, g, b, a);
    ctx->clear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
    ctx->gl_finish();
    return v_nil();
}

static unsigned gl_compile_shader(GlContext *ctx, unsigned kind, Str *source, const char *which) {
    unsigned shader = ctx->create_shader(kind);
    const char *text = source->chars;
    int length = (int)source->len;
    ctx->shader_source(shader, 1, &text, &length);
    ctx->compile_shader(shader);

    int compiled = 0;
    ctx->get_shaderiv(shader, GL_COMPILE_STATUS, &compiled);
    if (!compiled) {
        char log[1024];
        log[0] = '\0';
        if (ctx->get_shader_info_log) {
            int written = 0;
            ctx->get_shader_info_log(shader, sizeof log, &written, log);
            log[sizeof log - 1] = '\0';
            for (char *p = log; *p; p++) {
                if (*p == '\r' || *p == '\n') *p = ' ';
            }
            // Trim both ends the way the interpreter's InfoLog does.
            char *start = log;
            while (*start == ' ') start++;
            size_t trimmed = strlen(start);
            while (trimmed > 0 && start[trimmed - 1] == ' ') start[--trimmed] = '\0';
            memmove(log, start, trimmed + 1);
        }
        ctx->delete_shader(shader);
        if (log[0]) {
            tern_panic("GLError", "the %s shader did not compile: %s", which, log);
        }
        tern_panic("GLError", "the %s shader did not compile", which);
    }
    return shader;
}

static Value n_gl_program(int argc, Value *argv) {
    (void)argc;
    GlContext *ctx = gl_contexts[gl_slot_arg(argv[0])];
    net_arg_text(argv[1], "vertex shader");
    net_arg_text(argv[2], "fragment shader");
    Str *vertex = (Str *)argv[1].as.o;
    Str *fragment = (Str *)argv[2].as.o;

    unsigned vertex_id = gl_compile_shader(ctx, GL_VERTEX_SHADER, vertex, "vertex");
    unsigned fragment_id = gl_compile_shader(ctx, GL_FRAGMENT_SHADER, fragment, "fragment");

    unsigned program = ctx->create_program();
    ctx->attach_shader(program, vertex_id);
    ctx->attach_shader(program, fragment_id);
    ctx->link_program(program);

    int linked = 0;
    ctx->get_programiv(program, GL_LINK_STATUS, &linked);
    if (!linked) {
        char log[1024];
        log[0] = '\0';
        if (ctx->get_program_info_log) {
            int written = 0;
            ctx->get_program_info_log(program, sizeof log, &written, log);
            log[sizeof log - 1] = '\0';
            for (char *p = log; *p; p++) {
                if (*p == '\r' || *p == '\n') *p = ' ';
            }
            char *start = log;
            while (*start == ' ') start++;
            size_t trimmed = strlen(start);
            while (trimmed > 0 && start[trimmed - 1] == ' ') start[--trimmed] = '\0';
            memmove(log, start, trimmed + 1);
        }
        ctx->delete_shader(vertex_id);
        ctx->delete_shader(fragment_id);
        if (log[0]) {
            tern_panic("GLError", "the shader program did not link: %s", log);
        }
        tern_panic("GLError", "the shader program did not link");
    }

    ctx->delete_shader(vertex_id);
    ctx->delete_shader(fragment_id);

    if (ctx->program_count >= GL_MAX_PROGRAMS) {
        tern_panic("GLError", "too many shader programs (%d)", GL_MAX_PROGRAMS);
    }
    ctx->programs[ctx->program_count++] = program;
    return v_int(ctx->program_count);
}

static Value n_gl_draw(int argc, Value *argv) {
    (void)argc;
    GlContext *ctx = gl_contexts[gl_slot_arg(argv[0])];
    unsigned program = gl_program_arg(ctx, argv[1]);
    Value value = argv[2];

    if (!v_is_list(value)) {
        tern_panic("TypeError", "expected a list of vertices, not %s", tern_type_name(value));
    }
    List *list = (List *)value.as.o;
    if (list->count % GL_VERTEX_FLOATS != 0) {
        tern_panic("ArgumentError",
                   "vertices must be groups of %d numbers (x, y, r, g, b, a), not %d",
                   GL_VERTEX_FLOATS, list->count);
    }

    float *vertices = (float *)malloc((size_t)list->count * sizeof(float) + 4);
    if (!vertices) {
        tern_panic("GLError", "cannot draw %d vertices: out of memory", list->count);
    }
    for (int32_t i = 0; i < list->count; i++) {
        if (!v_is_num(list->items[i])) {
            free(vertices);
            tern_panic("TypeError", "vertex %d must be a number, not %s", i, tern_type_name(list->items[i]));
        }
        vertices[i] = (float)v_as_double(list->items[i]);
    }

    ctx->use_program(program);
    ctx->bind_vertex_array(ctx->array);
    ctx->bind_buffer(GL_ARRAY_BUFFER, ctx->buffer);
    ctx->buffer_data(GL_ARRAY_BUFFER, (ptrdiff_t)list->count * (ptrdiff_t)sizeof(float),
                     vertices, GL_STATIC_DRAW);
    free(vertices);

    int stride = GL_VERTEX_FLOATS * (int)sizeof(float);
    ctx->enable_vertex_attrib_array(0);
    ctx->vertex_attrib_pointer(0, 2, GL_FLOAT, 0, stride, (const void *)0);
    ctx->enable_vertex_attrib_array(1);
    ctx->vertex_attrib_pointer(1, 4, GL_FLOAT, 0, stride, (const void *)(2 * sizeof(float)));
    ctx->draw_arrays(GL_TRIANGLES, 0, list->count / GL_VERTEX_FLOATS);
    ctx->gl_finish();
    return v_nil();
}

static unsigned char *gl_read_pixels(GlContext *ctx) {
    size_t bytes = (size_t)ctx->width * (size_t)ctx->height * 4;
    unsigned char *pixels = (unsigned char *)malloc(bytes ? bytes : 1);
    if (!pixels) {
        tern_panic("GLError", "cannot read %zu bytes: out of memory", bytes);
    }
    ctx->read_pixels(0, 0, ctx->width, ctx->height, GL_RGBA, GL_UNSIGNED_BYTE, pixels);
    return pixels;
}

static Value n_gl_pixels(int argc, Value *argv) {
    (void)argc;
    GlContext *ctx = gl_contexts[gl_slot_arg(argv[0])];
    unsigned char *pixels = gl_read_pixels(ctx);
    size_t bytes = (size_t)ctx->width * (size_t)ctx->height * 4;
    List *list = list_new((int)bytes);
    for (size_t i = 0; i < bytes; i++) {
        list_push(list, v_int(pixels[i]));
    }
    free(pixels);
    return v_obj((Obj *)list);
}

static uint32_t gl_crc_table[256];
static int gl_crc_ready;

static void gl_crc_init(void) {
    if (gl_crc_ready) return;
    for (uint32_t n = 0; n < 256; n++) {
        uint32_t c = n;
        for (int k = 0; k < 8; k++) {
            c = (c & 1) ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }
        gl_crc_table[n] = c;
    }
    gl_crc_ready = 1;
}

static void gl_write_u32(ByteBuf *buf, uint32_t value) {
    unsigned char bytes[4] = {
        (unsigned char)(value >> 24), (unsigned char)(value >> 16),
        (unsigned char)(value >> 8), (unsigned char)value,
    };
    bb_add(buf, bytes, 4);
}

static void gl_png_chunk(ByteBuf *out, const char *type, const unsigned char *data, size_t length) {
    gl_write_u32(out, (uint32_t)length);
    uint32_t crc = 0xFFFFFFFFu;
    for (int i = 0; i < 4; i++) {
        unsigned char byte = (unsigned char)type[i];
        bb_add(out, &byte, 1);
        crc = gl_crc_table[(crc ^ byte) & 0xFF] ^ (crc >> 8);
    }
    if (length) {
        bb_add(out, data, length);
        for (size_t i = 0; i < length; i++) {
            crc = gl_crc_table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        }
    }
    gl_write_u32(out, crc ^ 0xFFFFFFFFu);
}

static void gl_png_write(const char *path, int width, int height,
                         const unsigned char *rgba, int flip) {
    gl_crc_init();

    size_t row = (size_t)width * 4;
    unsigned char *raw = (unsigned char *)malloc((row + 1) * (size_t)height);
    if (!raw) {
        tern_panic("IOError", "cannot write '%s': out of memory", path);
    }
    for (int y = 0; y < height; y++) {
        int source = flip ? height - 1 - y : y;
        raw[y * (row + 1)] = 0;
        memcpy(raw + y * (row + 1) + 1, rgba + (size_t)source * row, row);
    }

    ByteBuf idat = { 0 };
    bb_add_text(&idat, "");
    // zlib header, then stored deflate blocks (no compression, valid PNG).
    unsigned char header[2] = { 0x78, 0x01 };
    bb_add(&idat, header, 2);
    size_t total = (row + 1) * (size_t)height;
    size_t offset = 0;
    do {
        size_t length = total - offset;
        if (length > 65535) length = 65535;
        unsigned char block[5];
        block[0] = (offset + length >= total) ? 1 : 0;
        block[1] = (unsigned char)(length & 0xFF);
        block[2] = (unsigned char)(length >> 8);
        block[3] = (unsigned char)(~length & 0xFF);
        block[4] = (unsigned char)((~length >> 8) & 0xFF);
        bb_add(&idat, block, 5);
        bb_add(&idat, raw + offset, length);
        offset += length;
    } while (offset < total);

    uint32_t a = 1, b = 0;
    for (size_t i = 0; i < total; i++) {
        a = (a + raw[i]) % 65521;
        b = (b + a) % 65521;
    }
    gl_write_u32(&idat, (b << 16) | a);
    free(raw);

    ByteBuf out = { 0 };
    static const unsigned char signature[8] = { 0x89, 'P', 'N', 'G', 0x0D, 0x0A, 0x1A, 0x0A };
    bb_add(&out, signature, 8);

    unsigned char ihdr[13];
    ihdr[0] = (unsigned char)(width >> 24); ihdr[1] = (unsigned char)(width >> 16);
    ihdr[2] = (unsigned char)(width >> 8);  ihdr[3] = (unsigned char)width;
    ihdr[4] = (unsigned char)(height >> 24); ihdr[5] = (unsigned char)(height >> 16);
    ihdr[6] = (unsigned char)(height >> 8);  ihdr[7] = (unsigned char)height;
    ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
    gl_png_chunk(&out, "IHDR", ihdr, sizeof ihdr);
    gl_png_chunk(&out, "IDAT", idat.data, idat.len);
    gl_png_chunk(&out, "IEND", NULL, 0);
    bb_free(&idat);

    FILE *file = fopen(path, "wb");
    if (!file) {
        bb_free(&out);
        tern_panic("IOError", "cannot write '%s': %s", path, strerror(errno));
    }
    fwrite(out.data, 1, out.len, file);
    fclose(file);
    bb_free(&out);
}

static Value n_gl_save(int argc, Value *argv) {
    (void)argc;
    GlContext *ctx = gl_contexts[gl_slot_arg(argv[0])];
    const char *path = path_arg(argc, argv, 1);
    unsigned char *pixels = gl_read_pixels(ctx);
    gl_png_write(path, ctx->width, ctx->height, pixels, 1);
    free(pixels);
    return v_nil();
}

static Value n_gl_size(int argc, Value *argv) {
    (void)argc;
    GlContext *ctx = gl_contexts[gl_slot_arg(argv[0])];
    List *list = list_new(2);
    list_push(list, v_int(ctx->width));
    list_push(list, v_int(ctx->height));
    return v_obj((Obj *)list);
}

static Value n_gl_close(int argc, Value *argv) {
    (void)argc;
    int slot = gl_slot_arg(argv[0]);
    gl_context_free(gl_contexts[slot]);
    gl_contexts[slot] = NULL;
    return v_nil();
}

#else

// Windows builds carry the language, `fs`, `os`, `time`, `math`, `json`, `text`
// and `lists`. The socket, file-transfer and graphics modules need the Winsock
// and dlopen translations that come next, so they answer instead of vanishing:
// a script that calls one gets a sentence, not "nil has no method 'send'".

static Value n_module_missing(int argc, Value *argv) {
    (void)argc;
    (void)argv;
    tern_panic("UnsupportedError", "this module is not part of the Windows build yet");
    return v_nil();
}

static void add_missing_members(Map *map, const char *const *names) {
    for (int i = 0; names[i]; i++) {
        map_set(map, str_from_cstr(names[i]),
                make_native_arity(names[i], n_module_missing, 0, -1));
    }
}

static Value missing_module(const char *module, const char *const *names) {
    (void)module;
    Map *map = map_new();
    add_missing_members(map, names);
    return v_obj((Obj *)map);
}

#endif

static Value namespace_map(const char *ns) {
    Map *map = map_new();

    if (strcmp(ns, "math") == 0) {
        map_set(map, str_from_cstr("floor"), make_native_arity("floor", m_floor, 1, 1));
        map_set(map, str_from_cstr("ceil"), make_native_arity("ceil", m_ceil, 1, 1));
        map_set(map, str_from_cstr("round"), make_native_arity("round", m_round, 1, 2));
        map_set(map, str_from_cstr("sqrt"), make_native_arity("sqrt", m_sqrt, 1, 1));
        map_set(map, str_from_cstr("trunc"), make_native_arity("trunc", m_trunc, 1, 1));
        map_set(map, str_from_cstr("pow"), make_native_arity("pow", m_pow, 2, 2));
        map_set(map, str_from_cstr("abs"), make_native_arity("abs", m_abs, 1, 1));
        map_set(map, str_from_cstr("min"), make_native_arity("min", m_min, 1, -1));
        map_set(map, str_from_cstr("max"), make_native_arity("max", m_max, 1, -1));
        map_set(map, str_from_cstr("sign"), make_native_arity("sign", m_sign, 1, 1));
        map_set(map, str_from_cstr("clamp"), make_native_arity("clamp", m_clamp, 3, 3));
        map_set(map, str_from_cstr("isInteger"), make_native_arity("isInteger", m_isinteger, 1, 1));
        map_set(map, str_from_cstr("isNaN"), make_native_arity("isNaN", m_isnan, 1, 1));
        map_set(map, str_from_cstr("gcd"), make_native_arity("gcd", m_gcd, 2, 2));
        map_set(map, str_from_cstr("pi"), v_float(3.141592653589793));
        map_set(map, str_from_cstr("e"), v_float(2.718281828459045));
        map_set(map, str_from_cstr("tau"), v_float(6.283185307179586));
        map_set(map, str_from_cstr("infinity"), v_float(INFINITY));
        map_set(map, str_from_cstr("nan"), v_float(NAN));
        map_set(map, str_from_cstr("lcm"), make_native_arity("lcm", m_lcm, 2, 2));
        map_set(map, str_from_cstr("log"), make_native_arity("log", m_log, 1, 2));
        map_set(map, str_from_cstr("log2"), make_native_arity("log2", m_log2, 1, 1));
        map_set(map, str_from_cstr("log10"), make_native_arity("log10", m_log10, 1, 1));
        map_set(map, str_from_cstr("cbrt"), make_native_arity("cbrt", m_cbrt, 1, 1));
        map_set(map, str_from_cstr("hypot"), make_native_arity("hypot", m_hypot, 2, 2));
        map_set(map, str_from_cstr("exp"), make_native_arity("exp", m_exp, 1, 1));
        map_set(map, str_from_cstr("sin"), make_native_arity("sin", m_sin, 1, 1));
        map_set(map, str_from_cstr("cos"), make_native_arity("cos", m_cos, 1, 1));
        map_set(map, str_from_cstr("tan"), make_native_arity("tan", m_tan, 1, 1));
        map_set(map, str_from_cstr("asin"), make_native_arity("asin", m_asin, 1, 1));
        map_set(map, str_from_cstr("acos"), make_native_arity("acos", m_acos, 1, 1));
        map_set(map, str_from_cstr("atan"), make_native_arity("atan", m_atan, 1, 1));
        map_set(map, str_from_cstr("atan2"), make_native_arity("atan2", m_atan2, 2, 2));
        map_set(map, str_from_cstr("toDegrees"), make_native_arity("toDegrees", m_todegrees, 1, 1));
        map_set(map, str_from_cstr("toRadians"), make_native_arity("toRadians", m_toradians, 1, 1));
        return v_obj((Obj *)map);
    }

    if (strcmp(ns, "lists") == 0) {
        map_set(map, str_from_cstr("range"), make_native_arity("range", ln_range, 1, 3));
        map_set(map, str_from_cstr("zip"), make_native_arity("zip", ln_zip, 1, -1));
        map_set(map, str_from_cstr("enumerate"), make_native_arity("enumerate", ln_enumerate, 1, 1));
        map_set(map, str_from_cstr("chunk"), make_native_arity("chunk", ln_chunk, 2, 2));
        map_set(map, str_from_cstr("sort"), make_native_arity("sort", ln_sort, 1, 2));
        map_set(map, str_from_cstr("repeat"), make_native_arity("repeat", ln_repeat, 2, 2));
        return v_obj((Obj *)map);
    }

    if (strcmp(ns, "text") == 0) {
        // Exactly the interpreter's `std:text` members: methods such as
        // `trimStart` are reached through a string receiver, not through the
        // namespace, and keeping the namespaces identical keeps a name from
        // resolving to the wrong function in `tern_lookup_std_member`.
        map_set(map, str_from_cstr("format"), make_native_arity("format", s_format, 1, -1));
        map_set(map, str_from_cstr("pad"), make_native_arity("pad", tx_pad, 2, 3));
        map_set(map, str_from_cstr("trim"), make_native_arity("trim", s_trim, 1, 1));
        map_set(map, str_from_cstr("join"), make_native_arity("join", ln_join, 1, 2));
        map_set(map, str_from_cstr("upper"), make_native_arity("upper", s_upper, 1, 1));
        map_set(map, str_from_cstr("lower"), make_native_arity("lower", s_lower, 1, 1));
        map_set(map, str_from_cstr("split"), make_native_arity("split", s_split, 2, 2));
        return v_obj((Obj *)map);
    }

    if (strcmp(ns, "json") == 0) {
        map_set(map, str_from_cstr("stringify"), make_native_arity("stringify", js_stringify, 1, 2));
        map_set(map, str_from_cstr("parse"), make_native_arity("parse", js_parse, 1, 1));
        map_set(map, str_from_cstr("tryParse"), make_native_arity("tryParse", js_tryparse, 1, 2));
        return v_obj((Obj *)map);
    }

    if (strcmp(ns, "os") == 0) {
        map_set(map, str_from_cstr("platform"), make_native_arity("platform", os_platform, 0, 0));
        map_set(map, str_from_cstr("lineSeparator"), make_native_arity("lineSeparator", os_linesep, 0, 0));
        map_set(map, str_from_cstr("cwd"), make_native_arity("cwd", os_cwd, 0, 0));
        map_set(map, str_from_cstr("args"), make_native_arity("args", os_args, 0, 0));
        map_set(map, str_from_cstr("env"), make_native_arity("env", os_env, 1, 2));
        map_set(map, str_from_cstr("setEnv"), make_native_arity("setEnv", os_setenv, 2, 2));
        map_set(map, str_from_cstr("joinPath"), make_native_arity("joinPath", os_joinpath, 0, -1));
        return v_obj((Obj *)map);
    }

    if (strcmp(ns, "fs") == 0) {
        map_set(map, str_from_cstr("read"), make_native_arity("read", fs_read, 1, 1));
        map_set(map, str_from_cstr("readBytes"), make_native_arity("readBytes", fs_readbytes, 1, 1));
        map_set(map, str_from_cstr("write"), make_native_arity("write", fs_write, 2, 2));
        map_set(map, str_from_cstr("append"), make_native_arity("append", fs_append, 2, 2));
        map_set(map, str_from_cstr("exists"), make_native_arity("exists", fs_exists, 1, 1));
        map_set(map, str_from_cstr("size"), make_native_arity("size", fs_size, 1, 1));
        map_set(map, str_from_cstr("remove"), make_native_arity("remove", fs_remove, 1, 1));
        map_set(map, str_from_cstr("list"), make_native_arity("list", fs_list, 1, 1));
        map_set(map, str_from_cstr("mkdir"), make_native_arity("mkdir", fs_mkdir, 1, 1));
        map_set(map, str_from_cstr("open"), make_native_arity("open", fs_open, 1, 1));
        map_set(map, str_from_cstr("readChunk"), make_native_arity("readChunk", fs_readchunk, 2, 2));
        map_set(map, str_from_cstr("readAt"), make_native_arity("readAt", fs_readat, 2, 3));
        map_set(map, str_from_cstr("seek"), make_native_arity("seek", fs_seek, 2, 2));
        map_set(map, str_from_cstr("position"), make_native_arity("position", fs_position, 1, 1));
        map_set(map, str_from_cstr("fileSize"), make_native_arity("fileSize", fs_filesize, 1, 1));
        map_set(map, str_from_cstr("close"), make_native_arity("close", fs_close, 1, 1));
        map_set(map, str_from_cstr("isFile"), make_native_arity("isFile", fs_isfile, 1, 1));
        map_set(map, str_from_cstr("isDir"), make_native_arity("isDir", fs_isdir, 1, 1));
        return v_obj((Obj *)map);
    }

#ifdef _WIN32
    if (strcmp(ns, "net") == 0) {
        static const char *names[] = { "encode", "decode", "listen", "accept", "connect",
                                       "send", "receive", "sendBytes", "receiveBytes",
                                       "maxFrame", "udpListen", "udpSend", "udpReceive",
                                       "close", "timeout", "port", "peer", NULL };
        return missing_module("net", names);
    }
    if (strcmp(ns, "http") == 0) {
        static const char *names[] = { "request", "get", "post", "encodeRequest",
                                       "decodeResponse", "exchange", NULL };
        return missing_module("http", names);
    }
    if (strcmp(ns, "rcon") == 0) {
        static const char *names[] = { "connect", "attach", "execute", "close", NULL };
        return missing_module("rcon", names);
    }
    if (strcmp(ns, "sftp") == 0) {
        static const char *names[] = { "upload", "download", "list", "run", "batch",
                                       "command", NULL };
        return missing_module("sftp", names);
    }
    if (strcmp(ns, "gl") == 0) {
        static const char *names[] = { "available", "info", "create", "clear", "program",
                                       "draw", "pixels", "save", "size", "close", NULL };
        return missing_module("gl", names);
    }
#else
    if (strcmp(ns, "net") == 0) {
        // The argument counts mirror TERN/Builtins/Net.cs, so a wrong call fails
        // the same way in both back ends.
        map_set(map, str_from_cstr("encode"),
                make_native_arity("encode", n_net_encode, 1, 1));
        map_set(map, str_from_cstr("decode"),
                make_native_arity("decode", n_net_decode, 1, 1));
        map_set(map, str_from_cstr("listen"),
                make_native_arity("listen", n_net_listen, 1, 3));
        map_set(map, str_from_cstr("accept"),
                make_native_arity("accept", n_net_accept, 1, 1));
        map_set(map, str_from_cstr("connect"),
                make_native_arity("connect", n_net_connect, 2, 3));
        map_set(map, str_from_cstr("sendBytes"),
                make_native_arity("sendBytes", n_net_send_bytes, 2, 2));
        map_set(map, str_from_cstr("receiveBytes"),
                make_native_arity("receiveBytes", n_net_receive_bytes, 2, 2));
        map_set(map, str_from_cstr("maxFrame"),
                make_native_arity("maxFrame", n_net_max_frame, 2, 2));
        map_set(map, str_from_cstr("send"),
                make_native_arity("send", n_net_send, 2, 2));
        map_set(map, str_from_cstr("receive"),
                make_native_arity("receive", n_net_receive, 1, 1));
        map_set(map, str_from_cstr("udpListen"),
                make_native_arity("udpListen", n_net_udp_listen, 1, 3));
        map_set(map, str_from_cstr("udpSend"),
                make_native_arity("udpSend", n_net_udp_send, 4, 4));
        map_set(map, str_from_cstr("udpReceive"),
                make_native_arity("udpReceive", n_net_udp_receive, 1, 2));
        map_set(map, str_from_cstr("close"),
                make_native_arity("close", n_net_close, 1, 1));
        map_set(map, str_from_cstr("timeout"),
                make_native_arity("timeout", n_net_timeout, 2, 2));
        map_set(map, str_from_cstr("port"),
                make_native_arity("port", n_net_port, 1, 1));
        map_set(map, str_from_cstr("peer"),
                make_native_arity("peer", n_net_peer, 1, 1));
        return v_obj((Obj *)map);
    }

    if (strcmp(ns, "http") == 0) {
        // The argument counts mirror TERN/Builtins/Http.cs.
        map_set(map, str_from_cstr("request"),
                make_native_arity("request", n_http_request, 2, 3));
        map_set(map, str_from_cstr("get"),
                make_native_arity("get", n_http_get, 1, 2));
        map_set(map, str_from_cstr("post"),
                make_native_arity("post", n_http_post, 2, 3));
        map_set(map, str_from_cstr("encodeRequest"),
                make_native_arity("encodeRequest", n_http_encode_request, 2, 3));
        map_set(map, str_from_cstr("decodeResponse"),
                make_native_arity("decodeResponse", n_http_decode_response, 1, 1));
        map_set(map, str_from_cstr("exchange"),
                make_native_arity("exchange", n_http_exchange, 3, 4));
        return v_obj((Obj *)map);
    }

    if (strcmp(ns, "rcon") == 0) {
        // The argument counts mirror TERN/Builtins/Rcon.cs.
        map_set(map, str_from_cstr("connect"),
                make_native_arity("connect", n_rcon_connect, 3, 4));
        map_set(map, str_from_cstr("attach"),
                make_native_arity("attach", n_rcon_attach, 2, 3));
        map_set(map, str_from_cstr("execute"),
                make_native_arity("execute", n_rcon_execute, 2, 2));
        map_set(map, str_from_cstr("close"),
                make_native_arity("close", n_net_close, 1, 1));
        return v_obj((Obj *)map);
    }

    if (strcmp(ns, "gl") == 0) {
        // The argument counts mirror TERN/Builtins/Gl.cs.
        map_set(map, str_from_cstr("available"),
                make_native_arity("available", n_gl_available, 0, 0));
        map_set(map, str_from_cstr("info"),
                make_native_arity("info", n_gl_info, 0, 0));
        map_set(map, str_from_cstr("create"),
                make_native_arity("create", n_gl_create, 2, 2));
        map_set(map, str_from_cstr("clear"),
                make_native_arity("clear", n_gl_clear, 5, 5));
        map_set(map, str_from_cstr("program"),
                make_native_arity("program", n_gl_program, 3, 3));
        map_set(map, str_from_cstr("draw"),
                make_native_arity("draw", n_gl_draw, 3, 3));
        map_set(map, str_from_cstr("pixels"),
                make_native_arity("pixels", n_gl_pixels, 1, 1));
        map_set(map, str_from_cstr("save"),
                make_native_arity("save", n_gl_save, 2, 2));
        map_set(map, str_from_cstr("size"),
                make_native_arity("size", n_gl_size, 1, 1));
        map_set(map, str_from_cstr("close"),
                make_native_arity("close", n_gl_close, 1, 1));
        return v_obj((Obj *)map);
    }

    if (strcmp(ns, "sftp") == 0) {
        // The argument counts mirror TERN/Builtins/Sftp.cs.
        map_set(map, str_from_cstr("upload"),
                make_native_arity("upload", n_sftp_upload, 2, 3));
        map_set(map, str_from_cstr("download"),
                make_native_arity("download", n_sftp_download, 2, 3));
        map_set(map, str_from_cstr("list"),
                make_native_arity("list", n_sftp_list, 1, 2));
        map_set(map, str_from_cstr("run"),
                make_native_arity("run", n_sftp_run, 2, 2));
        map_set(map, str_from_cstr("batch"),
                make_native_arity("batch", n_sftp_batch, 1, 1));
        map_set(map, str_from_cstr("command"),
                make_native_arity("command", n_sftp_command, 1, 1));
        return v_obj((Obj *)map);
    }

#endif

    if (strcmp(ns, "random") == 0) {
        map_set(map, str_from_cstr("seed"), make_native_arity("seed", rnd_seed, 1, 1));
        map_set(map, str_from_cstr("float"), make_native_arity("float", rnd_float, 0, 2));
        map_set(map, str_from_cstr("int"), make_native_arity("int", rnd_int, 1, 2));
        map_set(map, str_from_cstr("bool"), make_native_arity("bool", rnd_bool, 0, 0));
        map_set(map, str_from_cstr("choice"), make_native_arity("choice", rnd_choice, 1, 1));
        map_set(map, str_from_cstr("shuffle"), make_native_arity("shuffle", rnd_shuffle, 1, 1));
        return v_obj((Obj *)map);
    }

    if (strcmp(ns, "time") == 0) {
        map_set(map, str_from_cstr("millis"), make_native_arity("millis", t_millis, 0, 0));
        map_set(map, str_from_cstr("iso"), make_native_arity("iso", t_iso, 1, 1));
        map_set(map, str_from_cstr("now"), make_native_arity("now", t_now, 0, 0));
        map_set(map, str_from_cstr("sleep"), make_native_arity("sleep", n_sleep, 1, 1));
        return v_obj((Obj *)map);
    }

    return v_obj((Obj *)map);
}

/// Looks a function up in the well known namespaces (math, text, json, ...).
/// Used for `import "std:x"` constants and for globals the interpreter exposes.
Value tern_lookup_std_member(const char *name) {
    static const char *spaces[] = {"math", "time", "json", "os", "random", "text", "lists",
                                  "fs", "net", "http", "rcon", "sftp", "gl", NULL};
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
            return make_native_arity(global_natives[i].name, global_natives[i].fn,
                                     global_natives[i].min_args, global_natives[i].max_args);
        }
    }

    if (strcmp(name, "math") == 0 || strcmp(name, "time") == 0 ||
        strcmp(name, "json") == 0 || strcmp(name, "os") == 0 ||
        strcmp(name, "random") == 0 || strcmp(name, "text") == 0 ||
        strcmp(name, "lists") == 0 || strcmp(name, "net") == 0 ||
        strcmp(name, "http") == 0 || strcmp(name, "rcon") == 0 ||
        strcmp(name, "sftp") == 0 || strcmp(name, "gl") == 0) {
        return namespace_map(name);
    }

    if (strcmp(name, "fs") == 0) {
        return namespace_map("fs");
    }

    return v_nil();
}

void tern_bind_globals(Value *globals, const char **names, int count) {
    tern_gc_register_values(globals, count);

    if (tern_global_table_count < TERN_MAX_GLOBAL_TABLES) {
        GlobalTable *table = &tern_global_tables[tern_global_table_count++];
        table->values = globals;
        table->names = names;
        table->count = count;
    }

    for (int i = 0; i < count; i++) {
        globals[i] = tern_lookup_global(names[i]);
    }
}

void tern_rt_init(void) {
    /* stdout is unbuffered-per-line by default; keep line buffering for pipes. */
    setvbuf(stdout, NULL, _IOLBF, 0);

    /* Socket handles are 1-based indexes; -1 marks a free slot. */
    for (int i = 0; i < NET_MAX_HANDLES; i++) {
        net_fds[i] = -1;
        net_kinds[i] = 0;
        net_max_frames[i] = NET_DEFAULT_MAX_FRAME;
        net_timeouts[i] = 0;
        net_tokens[i] = NULL;
        net_ssl[i] = NULL;
        net_ssl_ctx[i] = NULL;
    }

    /* towupper/towlower need a UTF-8 locale to know about non-ASCII letters. */
    setlocale(LC_CTYPE, "C.UTF-8");

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
