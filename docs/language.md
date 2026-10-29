# The TERN Language Reference

TERN is a small, fast, dynamically typed scripting language with a modern,
expression-oriented syntax. It is compiled to bytecode and executed on a stack
virtual machine written in C#.

This document describes TERN 2.x. The syntax is deliberately **not** compatible
with TERN 1.x (`scope X { Method { arg0 } }`, `@If(...)`, `arg0..argN`): those
constructs are gone.

```tern
fn greet(name, punctuation = "!") {
    return "hello, ${name}${punctuation}";
}

let names = ["ada", "grace", "alan"];
for name in names {
    println(greet(name.upper()));
}
```

---

## Contents

1. [Lexical structure](#1-lexical-structure)
2. [Values and types](#2-values-and-types)
3. [Variables](#3-variables)
4. [Operators](#4-operators)
5. [Strings](#5-strings)
6. [Control flow](#6-control-flow)
7. [Functions and closures](#7-functions-and-closures)
8. [Collections](#8-collections)
9. [Structs](#9-structs)
10. [Pattern matching](#10-pattern-matching)
11. [Errors](#11-errors)
12. [Modules and imports](#12-modules-and-imports)
13. [Standard library](#13-standard-library)
14. [Program entry, arguments and sandboxing](#14-program-entry-arguments-and-sandboxing)
15. [Grammar summary](#15-grammar-summary)
16. [Diagnostics](#16-diagnostics)

---

## 1. Lexical structure

### Comments

```tern
// line comment
/* block comment, /* nestable */ still a comment */
```

### Identifiers

An identifier starts with a letter or `_` and continues with letters, digits or
`_`. Unicode letters are allowed.

### Literals

| Kind    | Examples                                        |
| ------- | ----------------------------------------------- |
| Integer | `42`, `-7`, `0xFF`, `0b1010`, `1_000_000`        |
| Float   | `3.5`, `1e9`, `2.5e-3`                          |
| String  | `"hi"`, `'hi'`, `"tab\there"`, `"${name}!"`     |
| Boolean | `true`, `false`                                 |
| Nil     | `nil`                                           |
| List    | `[1, 2, 3]`                                     |
| Map     | `{"a": 1}`, `{}`                                |

Integers are 64-bit signed. Floats are IEEE-754 doubles. Integers that do not
fit in 64 bits are reported as `TRN0005` at compile time.

### Keywords

```
let  const  fn   return if    else  while for   in    break
continue struct match try   catch finally throw import as
nil  true   false loop   this do    and   or    not   typeof
```

---

## 2. Values and types

TERN has eight value kinds. `type(x)` returns the name in the first column.

| `type(x)` | Description                          | Literal / constructor       |
| --------- | ------------------------------------ | --------------------------- |
| `"int"`   | 64-bit signed integer                | `42`                        |
| `"float"` | 64-bit floating point                | `3.5`                       |
| `"string"`| Immutable UTF-16 text                | `"hi"`                      |
| `"bool"`  | `true` / `false`                     | `true`                      |
| `"nil"`   | The absence of a value               | `nil`                       |
| `"list"`  | Growable, mixed-type sequence        | `[1, "two"]`                |
| `"map"`   | Insertion-ordered key/value table    | `{"k": 1}`                  |
| `"range"` | Lazy integer interval                | `range(0, 10)`, `0..<10`    |
| `"function"` | A user or native function         | `fn(x) => x`                |
| struct name | An instance of a user struct       | `Point(1, 2)`               |

Functions, structs and builtin namespaces print as `function`/struct name.

### Truthiness

`false`, `nil`, `0`, `0.0`, `""` and empty lists/maps are **falsy**. Everything
else is truthy. `bool(v)` converts explicitly.

### Numeric behaviour

* `int` op `int` stays `int` for `+ - * % **` and for exact division.
* `/` always produces a `float` unless both sides are integers and divide
  evenly (`7 / 2` → `3.5`, `8 / 2` → `4`).
* Any operation mixing `int` and `float` promotes to `float`.
* `1 == 1.0` is `true`; `1 < 2.5` compares numerically.
* Division by zero raises a runtime `ZeroDivisionError`.
* `%` follows C# semantics (truncated toward zero).

---

## 3. Variables

```tern
let x = 1;        // mutable
const LIMIT = 10; // immutable: assignment is a compile error
x = 2;            // ok
x += 5;           // compound assignment: += -= *= /= %=
x++;              // increment/decrement on a bare variable
--x;
```

* Declarations are block scoped. A block may shadow an outer name.
* Assignment to an undeclared name is `TRN0201`.
* Assigning to a `const` is `TRN0202`.
* A declaration without an initializer is `nil`: `let pending;`.

---

## 4. Operators

From lowest to highest precedence:

| Level | Operators                        | Associativity |
| ----- | -------------------------------- | ------------- |
| 1     | `=` `+=` `-=` `*=` `/=` `%=`     | right         |
| 2     | `cond ? a : b` (ternary)         | right         |
| 3     | `??`                             | right         |
| 4     | `or` `\|\|`                      | left          |
| 5     | `and` `&&`                       | left          |
| 6     | `==` `!=`                        | left          |
| 7     | `<` `<=` `>` `>=`                | left          |
| 8     | `in`, `not in`                   | left          |
| 9     | `\|`                             | left          |
| 10    | `^`                              | left          |
| 11    | `&`                              | left          |
| 12    | `<<` `>>`                        | left          |
| 13    | `+` `-`                          | left          |
| 14    | `*` `/` `%`                      | left          |
| 15    | `**`                             | right         |
| 16    | unary `-` `+` `!` `not` `~` `typeof` | right     |
| 17    | `.` `[]` `()` `?.`               | left          |

Unary operators bind tighter than `**`, so `-x ** 2` is `(-x) ** 2`. Write
`-(x ** 2)` for the other meaning. Exponentiation is right associative:
`2 ** 3 ** 2` is `2 ** 9` = `512`.

Notes:

* `??` returns the left side unless it is `nil`: `name ?? "anonymous"`.
* `cond ? a : b` is the inline conditional: `let s = n > 0 ? "pos" : "neg";`
* `?.` short-circuits a member access when the receiver is `nil`:
  `user?.name` is `nil` instead of an error. It guards the access itself, so
  `user?.getName()` still raises if `getName` is missing.
* `+` concatenates strings, merges maps, and concatenates lists.
* `in` tests membership: `"b" in "abc"`, `2 in [1, 2]`, `"k" in map`,
  `3 in 0..<5`. `not in` is the negation. Membership is also available on
  strings, lists, maps, ranges and struct instances.
* `~` is bitwise NOT; `&`, `|`, `^`, `<<`, `>>` are bitwise on integers.
* `**` is exponentiation; integer bases and exponents stay integral.

---

## 5. Strings

Strings are immutable and use `+` for concatenation.

### Interpolation

`${}` embeds any expression; interpolation nests.

```tern
let who = "world";
println("hello, ${who.toUpper()}");     // hello, WORLD
println("sum: ${1 + 2 * 3}");           // sum: 7
println("nested: ${ ["a"].map(fn(s) => "${s}!") }");
```

### Escapes

`\n` `\t` `\r` `\\` `\"` `\'` `\0` `\u{1F600}` (braced) and `\uFFFF`.

Unknown escapes produce warning `TRN0009` and are kept verbatim.

### Methods

```
upper lower trim trimStart trimEnd
split lines replace replaceFirst
startsWith endsWith contains
indexOf lastIndexOf
substring slice chars words
repeat padStart padEnd reversed
toInt toFloat count toString isEmpty
```

Indexing and slicing work like lists: `s[0]`, `s[-1]`, `s[1..3]`,
`s[1..<3]`, `s[..2]`, `s[2..]`, `s[..]`. `..` includes the upper bound and
`..<` excludes it; a missing bound means "from the start" or "to the end".

Iterating a string yields its characters:

```tern
for c in "hey" { println(c); }   // h e y
```

---

## 6. Control flow

### if / else

`if` is an expression, so it can be assigned and used as a block's last value.

```tern
if score > 90 { println("A"); }
else if score > 80 { println("B"); }
else { println("C"); }

let label = if ready { "go" } else { "wait" };
```

### while

```tern
while n > 0 { n--; }
```

### loop

Infinite loop with `break`:

```tern
loop {
    if done() { break; }
}
```

### for

`for` iterates lists, strings, ranges, map values, map keys, and map pairs.

```tern
for item in list { }
for c in "abc" { }
for i in 0..<10 { }            // exclusive upper bound
for i in 0..10 { }             // inclusive upper bound
for value in map { }           // values
for key, value in map { }      // key/value pairs
```

### do / while

```tern
do {
    attempt++;
} while (!succeeded);
```

`break` and `continue` work in all loop forms.

---

## 7. Functions and closures

```tern
fn add(a, b) { return a + b; }

fn greet(name, greeting = "hello", ...rest) {
    return "${greeting}, ${name} (${rest.length} extras)";
}

// Expression-bodied lambdas
let double = fn(x) => x * 2;
let triple = (x) => x * 3;

// Default arrows and destructuring-free callbacks
println([1, 2, 3].map(fn(v) => v + 1));   // [2, 3, 4]
```

* Missing trailing arguments become `nil` (or the declared default).
* Extra arguments are `TRN0207` unless the last parameter is `...rest`, which
  collects them into a list.
* Functions are first class: store them in variables, pass them, return them.
* Closures capture their defining scope **by reference**, so counter closures
  observe mutations:

```tern
fn makeCounter() {
    let n = 0;
    return fn() { n++; return n; };
}
let c = makeCounter();
println(c());  // 1
println(c());  // 2
```

* TERN does not have tail-call optimisation; deep recursion grows the stack and
  reports `StackOverflowError` rather than crashing the process.
* Calling a non-function is a runtime `TypeError`.

---

## 8. Collections

### Lists

```tern
let xs = [3, 1, 2];
xs.push(4);            // append
xs[0] = 9;             // index assignment (grows with nil on gaps)
xs[-1];                // negative index from the end
xs[0..2];              // slice, returns a new list
xs[1..<3];             // exclusive slice
xs[2..];               // everything from index 2
xs[..2];               // everything up to and including index 2
```

Methods:

```
push pop shift unshift insert remove removeValue
indexOf contains join reverse sort slice
map filter reduce each any all find findIndex
sum min max first last take drop
flatten unique clone clear concat
toString isEmpty
```

Sorting is stable and uses natural comparison; pass a comparator function for
custom ordering (`xs.sort(fn(a, b) => b - a)`).

### Maps

```tern
let m = {"b": 2, "a": 1};
m.c = 3;               // dot access
m["d"] = 4;            // index access
m.has("a");            // true
m.get("missing", 0);   // 0
m.keys(); m.values(); m.entries();
m + {"e": 5};          // merge returns a new map
```

Maps preserve insertion order and accept any value as a key.

### Ranges

```tern
let r = 0..<5;         // exclusive upper bound
r.toList();            // [0, 1, 2, 3, 4]
range(2, 10);          // inclusive by default
3 in 0..<5;            // true
len(0..<5);            // 5, computed without materialising the range
```

A range is lazy: printing it shows `0..<5`, and `len()` is O(1).

---

## 9. Structs

```tern
struct Point {
    x = 0, y = 0

    fn length() { return math.sqrt(this.x ** 2 + this.y ** 2); }
    fn moved(dx, dy) { return Point(this.x + dx, this.y + dy); }
}

let p = Point(3, 4);
println(p.length());        // 5
println(p);                 // Point(x: 3, y: 4)
println(type(p));           // Point
```

* Fields are constructed positionally in declaration order.
* Defaults may be expressions and are evaluated **per instance**:

```tern
struct Stack { items = [] }          // each instance gets its own list
```

* Methods receive the instance as `this`; call with `.` or access them as
  values (`let f = p.length;`).
* Instances are mutable: `p.x = 10`.
* Field defaults in a `struct` body are separated by newlines or commas.

---

## 10. Pattern matching

`match` is an expression. Arms are tried in order; the first match wins.

```tern
let describe = match value {
    1 | 2 | 3            => "small",
    4..9                 => "medium",
    10..<100 if value % 2 == 0 => "even medium",
    "hi"                 => "greeting",
    nil                  => "nothing",
    true                 => "yes",
    []                   => "empty list",
    [x]                  => "one item: ${x}",
    [a, b]               => "pair",
    [first, ...rest]     => "list starting with ${first}",
    _                    => "anything else",
};
```

Supported patterns: literals, `nil`, booleans, or-patterns (`a | b`), integer
ranges (`2..9` inclusive, `2..<10` exclusive), list patterns with `...rest`,
binding names (`n` binds), and guards (`pattern if condition`).

`_` is a wildcard. Arms may be blocks:

```tern
match event {
    "click" => { println("clicked"); count++; },
    _       => println("other"),
}
```

---

## 11. Errors

Errors are values you can throw and catch. Anything can be thrown; the thrown
value is the `catch` binding.

```tern
try {
    risky();
} catch (e) {
    println("failed: ${e}");
} finally {
    println("cleanup");
}
```

* `throw value;` raises. Without a `catch` it aborts the program.
* `catch (e) { }` binds the payload; `catch { }` (no binding) is also allowed.
* `finally` always runs, including on `return`, `break`, and rethrow.
* Runtime errors (index out of range, wrong type, division by zero, assertion
  failure) are **catchable**, and the payload is a string message.

Useful builtins:

```tern
assert(cond);              // throws "AssertionError: ..." when falsy
assert(cond, "message");
error("boom");             // throw a string
panic("unrecoverable");    // throw with a Panic type
```

Uncaught errors print a message and a stack trace, then exit with code 1:

```
TypeError: nil has no method 'missing'
  at inner (app:1)
  at outer (app:2)
  at <module> (app:3)
```

---

## 12. Modules and imports

```tern
import "lib/helpers.tern";      // relative to the importing file
import "lib/helpers.tern" as h; // namespaced under h
import "std:math";              // std modules also flatten into scope
```

Importing a file executes it once (results are cached per engine) and makes its
top level bindings available.

Standard modules: `std:math`, `std:json`, `std:fs`, `std:os`, `std:time`,
`std:random`, `std:text`, `std:lists`. Their members are also exposed as
namespaces (`math.sqrt`, `json.parse`, ...), so importing is optional.

```tern
import "std:json";
let data = json.parse("{\"n\": 3}");
println(data.n);
println(json.stringify([1, 2], 2));
```

---

## 13. Standard library

The complete, always-current list of globals, namespaces and per-type methods
lives in [`stdlib.md`](stdlib.md), which is generated by `tern docs` from the
engine itself. The summary below covers the parts you will use most.

### Globals

```
print println eprint input
len type str repr int float bool chr ord
range keys values has
assert error panic
abs min max copy sleep
```

### Namespaces

| Namespace | Members |
| --------- | ------- |
| `math` | `abs ceil floor round trunc sqrt cbrt pow exp log log2 log10 sin cos tan asin acos atan atan2 min max clamp sign hypot gcd lcm isInteger isNaN toRadians toDegrees` + `pi e tau infinity nan` |
| `json` | `parse stringify tryParse` |
| `fs` | `read readBytes write append exists size remove list mkdir isFile isDir` |
| `os` | `env setEnv cwd platform args joinPath lineSeparator` |
| `time` | `now millis sleep iso` |
| `random` | `seed float int bool choice shuffle` |
| `text` | `format upper lower trim split join pad` |
| `lists` | `range repeat zip enumerate chunk sort` |

`text.format` accepts `{}` placeholders or indexed `{0}` placeholders.

---

## 14. Program entry, arguments and sandboxing

A program is executed top to bottom. There is no `main` requirement; the last
top-level value of a REPL line is echoed.

Command line arguments are available as a list:

```tern
let args = os.args();
```

The embedder can disable file system access. When disabled, every `fs` call and
`import` of a file raises a catchable error:

```
blocked: file system access is disabled for this script
```

---

## 15. Grammar summary

```ebnf
program        = statement* EOF ;
statement      = decl | fnDecl | structDecl | importDecl | exprStmt
               | ifStmt | whileStmt | loopStmt | forStmt | doWhileStmt
               | returnStmt | breakStmt | continueStmt | throwStmt
               | tryStmt | block ;
decl           = ("let" | "const") IDENT ("=" expression)? ";"? ;
fnDecl         = "fn" IDENT "(" params? ")" block ;
params         = param ("," param)* ;
param          = IDENT ("=" expression)? | "..." IDENT ;
structDecl     = "struct" IDENT "{" (field ("," field)*)? method* "}" ;
field          = IDENT ("=" expression)? ;
importDecl     = "import" STRING ("as" IDENT)? ";"? ;
ifStmt         = "if" expression block ("else" (ifStmt | block))? ;
whileStmt      = "while" expression block ;
loopStmt       = "loop" block ;
forStmt        = "for" (IDENT ",")? IDENT "in" expression block ;
doWhileStmt    = "do" block "while" expression ";"? ;
tryStmt        = "try" block ("catch" ("(" IDENT ")")? block)? ("finally" block)? ;
throwStmt      = "throw" expression ";"? ;
block          = "{" statement* "}" ;
```

Expressions follow the precedence table in §4 and include: literals, list/map
displays, identifiers, `this`, calls, member access, indexing, slicing,
lambdas, `match`, `if`-expressions, ternaries (`a ? b : c`), ranges (`a..b`),
slices (`x[a..b]`, `x[..b]`, `x[a..]`), unary/binary operators including `in`,
and assignments to variables, members and index targets.

---

## 16. Diagnostics

Diagnostics print as `name:line:col: error CODE: message`. Exit code is 2 when
compilation fails.

| Range | Stage |
| ----- | ----- |
| `TRN00xx` | lexer |
| `TRN01xx` | parser |
| `TRN02xx` | compiler (scoping, constness, struct rules, arity) |

`tern check <file>` compiles without running and reports diagnostics. `tern
disasm <file>` dumps the bytecode, which is useful when reasoning about
performance.
