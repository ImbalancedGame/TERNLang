// TERN native runtime - value representation and public API.
//
// The native backend compiles TERN bytecode to C: every function becomes a real
// C function, every jump a `goto`, so there is no dispatch loop at run time.
// This header is what the generated code talks to.
#ifndef TERN_RT_H
#define TERN_RT_H

// Before every system header, here and in the generated file that includes this
// one first. <sys/types.h> caches off_t when it is first read, so a
// _FILE_OFFSET_BITS that arrives later leaves `struct stat` 32-bit while
// <sys/stat.h> still redirects stat() to the 64-bit variant: stat() then writes
// 56 bytes into a 48-byte structure and st_size is read from the wrong offset.
// On Windows that made fs.size() answer 0 for a 1.2 MB PNG. tern_platform.h
// asserts the two agree, so this cannot regress quietly.
#ifndef _FILE_OFFSET_BITS
#define _FILE_OFFSET_BITS 64
#endif

#define _GNU_SOURCE
#include <setjmp.h>
#include <stdarg.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// ------------------------------------------------------------------ values

typedef enum {
    T_NIL = 0,
    T_BOOL = 1,
    T_INT = 2,
    T_FLOAT = 3,
    T_OBJ = 4,
    T_MISSING = 5,
} Tag;

typedef enum {
    O_STR = 0,
    O_LIST = 1,
    O_MAP = 2,
    O_CLOSURE = 3,
    O_CELL = 4,
    O_INSTANCE = 5,
    O_TYPE = 6,
    O_NATIVE = 7,
    O_BOUND = 8,
    O_RANGE = 9,
    O_ITER = 10,
} ObjType;

typedef struct Obj {
    ObjType type;
    uint8_t marked;     // collector mark bit
    uint8_t klass;      // size class (0..GC_CLASSES-1), GC_LARGE for big objects
    uint16_t reserved;
} Obj;

// 16 bytes, and - unlike the .NET struct - it holds no managed references, so a
// copy is two plain moves and the whole value travels in two registers.
typedef struct Value {
    union {
        int64_t i;
        double f;
        Obj *o;
    } as;
    uint8_t tag;
} Value;

static inline Value v_nil(void) {
    Value v;
    v.as.i = 0;
    v.tag = T_NIL;
    return v;
}

static inline Value v_bool(bool b) {
    Value v;
    v.as.i = b ? 1 : 0;
    v.tag = T_BOOL;
    return v;
}

static inline Value v_int(int64_t i) {
    Value v;
    v.as.i = i;
    v.tag = T_INT;
    return v;
}

static inline Value v_float(double d) {
    Value v;
    v.as.f = d;
    v.tag = T_FLOAT;
    return v;
}

static inline Value v_obj(Obj *o) {
    Value v;
    v.as.o = o;
    v.tag = T_OBJ;
    return v;
}

static inline Value v_missing(void) {
    Value v;
    v.as.i = 0;
    v.tag = T_MISSING;
    return v;
}

static inline bool v_is_int(Value v) { return v.tag == T_INT; }

static inline bool v_is_num(Value v) { return v.tag == T_INT || v.tag == T_FLOAT; }

static inline bool v_is_obj(Value v, ObjType t) { return v.tag == T_OBJ && v.as.o->type == t; }

static inline bool v_is_str(Value v) { return v_is_obj(v, O_STR); }

static inline bool v_is_list(Value v) { return v_is_obj(v, O_LIST); }

static inline bool v_is_map(Value v) { return v_is_obj(v, O_MAP); }

static inline bool v_is_nil(Value v) { return v.tag == T_NIL; }

static inline bool v_truthy(Value v) {
    return !(v.tag == T_NIL || (v.tag == T_BOOL && v.as.i == 0));
}

static inline double v_as_double(Value v) {
    return v.tag == T_FLOAT ? v.as.f : (double)v.as.i;
}

// ------------------------------------------------------------------ objects

typedef struct Str {
    Obj h;
    uint32_t len;      // bytes
    uint32_t hash;
    uint32_t units;    // UTF-16 code units; 0 means "not counted yet"
    uint32_t reserved;
    char chars[];
} Str;

typedef struct List {
    Obj h;
    int32_t count;
    int32_t cap;
    Value *items;
} List;

typedef struct MapEntry {
    uint64_t hash;
    Value key;
    Value value;
    uint8_t used;
    uint8_t dead;
} MapEntry;

typedef struct Map {
    Obj h;
    int32_t count;   // live entries
    int32_t used;    // live + tombstones
    int32_t cap;     // entry capacity (power of two)
    MapEntry *entries;
    // Keys in insertion order: TERN maps iterate in the order keys were first
    // added, so the hash table alone cannot drive `keys`, `for k, v in m`, or
    // the display form.
    Value *order;
    int32_t order_count;
    int32_t order_cap;
} Map;

typedef struct Cell {
    Obj h;
    Value value;
} Cell;

struct Proto;

typedef struct Closure {
    Obj h;
    struct Proto *proto;
    Cell **up;
} Closure;

typedef struct TypeObj {
    Obj h;
    const char *name;
    int32_t nfields;
    const char **field_names;
    Value *defaults;
    int32_t nmethods;
    const char **method_names;
    Closure **methods;
    Closure **defproto;     // per-field default prototypes (NULL when all literal)
    int32_t *field_slots;   // name -> slot, resolved once at startup
} TypeObj;

typedef struct Instance {
    Obj h;
    TypeObj *type;
    Value *fields;
} Instance;

typedef Value (*NativeFn)(int argc, Value *argv);

typedef struct Native {
    Obj h;
    const char *name;
    NativeFn fn;
    // Argument-count contract, checked before the call. `min_args < 0` means
    // unchecked (namespaces and methods, for now); `max_args < 0` means
    // variadic. Counts exclude the receiver of a method.
    int16_t min_args;
    int16_t max_args;
} Native;

typedef struct Bound {
    Obj h;
    Value receiver;
    Value callee;
} Bound;

typedef struct Range {
    Obj h;
    int64_t start;
    int64_t end;
    int64_t step;      // never 0: `range(0, 10, 0)` is normalised to 1
    bool inclusive;
} Range;

typedef struct Iterator {
    Obj h;
    Value target;
    int32_t kind;   // 0 list, 1 map, 2 range, 3 string
    int32_t index;
} Iterator;

typedef struct DivMagic {
    int64_t divisor;
    uint64_t magic;
    int shift;
} DivMagic;

typedef Value (*FnPtr)(Closure *self, int argc, Value *argv);

typedef struct Proto {
    FnPtr fn;
    const char *name;
    int32_t params;
    int32_t required;
    int32_t locals;
    int32_t stack;
    uint8_t rest;
    uint8_t is_method;
    int32_t nup;        // captured upvalues (closures are traced through them)
    Value *consts;
    const DivMagic *divs;
} Proto;

// ------------------------------------------------------------- exceptions
//
// `try` blocks install a TernTry on a global stack; `throw` (and every runtime
// panic) unwinds to the innermost one with longjmp. Generated functions that
// contain a try block keep their frame in memory so a longjmp cannot lose it.

struct TernFrame;

typedef struct TernTry {
    jmp_buf env;
    struct TernTry *prev;
    int sp;
    int depth;      // call depth when the handler was installed
    struct TernFrame *frames;  // innermost frame when the handler was installed
} TernTry;

extern TernTry *tern_try_top;
extern Value tern_thrown;
extern int tern_call_depth;

// Casts away `volatile` when handing a frame slot to the runtime.
#define NP(p) ((Value *)(void *)(p))

// Garbage collection. Roots are the registered global/constant arrays plus the
// C stack and registers, which are scanned conservatively, so generated code
// never has to describe its frame.
void tern_gc_set_stack_bottom(void *anchor);

// ------------------------------------------------------------------- traces
//
// Uncaught errors print the call stack, the same way the interpreter does. Each
// `tern_call` of a closure puts a frame on its own C stack and links it into a
// thread of frames: pushing costs three stack stores plus one global, and the
// frame dies with the call, so nothing needs cleaning up when a `try` handler
// unwinds. `tern_set_line` is emitted wherever the source line changes, so the
// innermost frame always knows the line it is executing.
void tern_trace_frames(void *output, const char *file);

// Name shown in traces (the script's module name).
extern const char *tern_program_file;

extern int tern_current_line;

typedef struct TernFrame {
    const Proto *proto;
    int caller_line;            // line of the call site in the caller
    struct TernFrame *prev;
} TernFrame;

// Innermost live frame, NULL at module level outside any call.
extern TernFrame *tern_frame_cur;

static inline void tern_set_line(int line) { tern_current_line = line; }

// Starts a frame for `proto`; `frame` must outlive the call (it is the caller's
// local). Ending it restores the caller's line so a later error in the caller
// still reports the call site it had reached.
#ifdef TERN_NO_TRACES
// Compiled without traces: keep the call sites but do no bookkeeping, so the
// line stores still identify where an uncaught error happened.
static inline void tern_frame_enter(TernFrame *frame, const Proto *proto) {
    (void)frame;
    (void)proto;
}

static inline void tern_frame_leave(TernFrame *frame) { (void)frame; }
#else
static inline void tern_frame_enter(TernFrame *frame, const Proto *proto) {
    frame->proto = proto;
    frame->caller_line = tern_current_line;
    frame->prev = tern_frame_cur;
    tern_frame_cur = frame;
}

static inline void tern_frame_leave(TernFrame *frame) {
    tern_frame_cur = frame->prev;
    tern_current_line = frame->caller_line;
}
#endif

void tern_set_args(int count, char **values);
void tern_gc_register_values(Value *base, int count);
void tern_gc_register_ptrs(void **base, int count);
void tern_gc_collect(void);
size_t tern_gc_live_bytes(void);

void tern_raise(Value payload);
void tern_raise_kind(const char *kind, Value payload);
void tern_throw_str(const char *kind, const char *message);

// ------------------------------------------------------------------ API

Value *tern_alloc(size_t size, ObjType type);
void tern_rt_init(void);

Str *str_from(const char *data, size_t len);
Value str_value(const char *data, size_t len);
Value str_from_cstr(const char *text);
Value str_from_int(int64_t value);
Value str_from_double(double value);

List *list_new(int cap);
Value tern_list_from(int count, Value *items);
void list_push(List *list, Value v);
Value list_get(List *list, int64_t index);
void list_set(List *list, int64_t index, Value v);

Map *map_new(void);
void map_set(Map *map, Value key, Value val);
bool map_get(Map *map, Value key, Value *out);
bool map_has(Map *map, Value key);
bool map_remove(Map *map, Value key);

Cell *cell_new(Value value);
Closure *closure_new(Proto *proto, int upvals);

Value tern_call(Value callee, int argc, Value *argv);
Value tern_new_instance(TypeObj *type, int argc, Value *argv);
Value tern_invoke(Value receiver, const char *name, int argc, Value *argv);
Value tern_get_member(Value target, const char *name);
Value tern_set_member(Value target, const char *name, Value value);
Value tern_get_index(Value target, Value index);
Value tern_set_index(Value target, Value index, Value value);
Value tern_slice(Value target, Value start, Value end, bool inclusive);
bool tern_contains(Value container, Value item);
int64_t tern_length(Value target);

Value tern_add(Value a, Value b);
Value tern_sub(Value a, Value b);
Value tern_mul(Value a, Value b);
Value tern_div(Value a, Value b);
Value tern_mod(Value a, Value b);
Value tern_pow(Value a, Value b);
Value tern_band(Value a, Value b);
Value tern_bor(Value a, Value b);
Value tern_bxor(Value a, Value b);
Value tern_shl(Value a, Value b);
Value tern_shr(Value a, Value b);
Value tern_neg(Value a);
Value tern_bnot(Value a);
Value tern_compare(Value a, Value b);   // returns -1/0/1 as an int value
bool tern_equal(Value a, Value b);
uint64_t tern_hash(Value v);

Value tern_concat(Value *parts, int count);
Value tern_to_string(Value v);      // display form (strings unquoted)
Str *tern_to_debug(Value v);        // debug form (strings quoted)
const char *tern_type_name(Value v);

void tern_panic(const char *kind, const char *fmt, ...);
Value tern_lookup_global(const char *name);
Value tern_lookup_std_member(const char *name);
NativeFn tern_builtin_method(Value receiver, const char *name);
Value tern_iter_new(Value iterable);
bool tern_iter_next(Value iterator, Value *first, Value *second);

// Generated by ternc: binds the module's globals by name.
void tern_bind_globals(Value *globals, const char **names, int count);

#endif
