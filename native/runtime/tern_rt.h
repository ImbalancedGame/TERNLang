// TERN native runtime - value representation and public API.
//
// The native backend compiles TERN bytecode to C: every function becomes a real
// C function, every jump a `goto`, so there is no dispatch loop at run time.
// This header is what the generated code talks to.
#ifndef TERN_RT_H
#define TERN_RT_H

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
    uint32_t len;
    uint32_t hash;
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
    Value *consts;
    const DivMagic *divs;
} Proto;

// ------------------------------------------------------------- exceptions
//
// `try` blocks install a TernTry on a global stack; `throw` (and every runtime
// panic) unwinds to the innermost one with longjmp. Generated functions that
// contain a try block keep their frame in memory so a longjmp cannot lose it.

typedef struct TernTry {
    jmp_buf env;
    struct TernTry *prev;
    int sp;
    int depth;      // call depth when the handler was installed
} TernTry;

extern TernTry *tern_try_top;
extern Value tern_thrown;
extern int tern_call_depth;

// Casts away `volatile` when handing a frame slot to the runtime.
#define NP(p) ((Value *)(void *)(p))

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
