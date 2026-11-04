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

`false` and `nil` are **falsy**. Everything else is truthy, including `0`, `0.0`,
`""`, and empty lists and maps. `bool(v)` converts explicitly and agrees with
the `if` rule.

This is not the Python rule and is worth knowing before writing `if (len(items))`
in one language and reading it in the other: a count of zero is truthy here, so
that test is always taken. Write `if (len(items) > 0)`, which says what it means
in every language.

The rule is checked in `tests/03_control.tern`, which pins all seven cases, and
`tests/17_stdlib.tern` pins `bool("")`. This section used to claim `0`, `""` and
the empty collections were falsy, which neither back end has ever done.

### Numeric behaviour

* `int` op `int` stays `int` for `+ - * % **` and for exact division.
* `/` always produces a `float` unless both sides are integers and divide
  evenly (`7 / 2` → `3.5`, `8 / 2` → `4`).
* Any operation mixing `int` and `float` promotes to `float`.
* `1 == 1.0` is `true`; `1 < 2.5` compares numerically.
* Integer `+ - *` promote to `float` instead of wrapping when the result does
  not fit in 64 bits: `9223372036854775807 + 1` is `9.223372036854776E+18`.
  `**`, `<<` and `>>` wrap, so bit tricks keep working on the full word.
* Division by zero raises a runtime `DivisionByZero` error.
* `%` follows C# semantics (truncated toward zero) for floats as well as ints,
  so `-7 % 2` and `-7.5 % 2` are both negative.
* Bitwise `& | ^ << >>` take a `float` when it has no fractional part
  (`1 << 2.0` is `4`) and raise otherwise.
* Lists compare element by element, then by length: `[1, 2] < [1, 3]` is
  `true`. Other non-numeric values only compare with their own kind.

---

## 3. Variables

```tern
let x = 1;        // mutable
const LIMIT = 10; // immutable: assignment is a compile error
x = 2;            // ok
x += 5;           // compound assignment: += -= *= /= %= **= &= |= ^= <<= >>=
x++;              // increment/decrement on a bare variable
--x;
```

Three of the assignment operators test the target before deciding whether the
value is wanted, and the value is *not evaluated* when it is not:

```tern
counts[word] ??= 0;             // counts[word] = counts[word] ?? 0, one lookup
nickname ||= person["name"];    // the value only when the target is falsy
flag &&= check();               // check() only when the target is truthy
```

They are `<target> ??= v`, `<target> ||= v` and `<target> &&= v`, and each is
worth the whole of its long form when the value is a call: `m[k] ??= expensive()`
calls `expensive` once per miss, not once per read plus once per miss. The
target's own subexpressions run once either way - `m[f()] ??= v` calls `f` once,
whether or not the value is needed - and the expression is worth the target's
value afterwards, so `let n = (x ??= 0);` works. `||=` and `&&=` follow the
truthiness in section 2, which means `0` counts as truthy: `n ||= 5` leaves a
zero alone, and `??=` is the one to reach for when zero is not a value the
target can have.

* Declarations are block scoped. A block may shadow an outer name.
* Assignment to an undeclared name is `TRN0206`.
* Assigning to a `const` is `TRN0209`.
* A declaration without an initializer is `nil`: `let pending;`.

### Declaring by pattern

`let` and `const` take the same patterns `match` tests with, which turns the
usual unpack-a-result dance into one line:

```tern
let [quotient, remainder] = divide(17, 5);
let {"status": status, "body": body} = response;   // other keys are ignored
let [head, ...tail] = lines;
const [width, height] = [640, 480];

for [key, value] in pairs { println("${key}: ${value}"); }
```

The subject is evaluated once and then matched: a list pattern needs a list of
exactly that length (use `...rest` to take a prefix), and a map pattern needs
each key it names, with any other keys ignored. A subject that does not fit is a
`TypeError` naming both sides - `[a, b] does not match [1, 2, 3]` - rather than a
declaration that quietly binds `nil`. Every name in a pattern has to be
different (`TRN0216`), and a pattern needs an initializer (`TRN0215`).

---

## 4. Operators

From lowest to highest precedence:

| Level | Operators                        | Associativity |
| ----- | -------------------------------- | ------------- |
| 1     | `=` `+=` `-=` `*=` `/=` `%=` `**=` `&=` `\|=` `^=` `<<=` `>>=` `??=` `\|\|=` `&&=` | right |
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

### Raw and multi-line text

Three quotes start a **raw** string: what is between them is the text, with no
escapes at all, and it may span lines. That is what an embedded shader, SQL
statement, JSON document or Windows path needs, because in those a backslash is
a backslash.

```tern
let vertex = """
    struct Vertex { float3 position : POSITION; };
    float4 main(Vertex input) : SV_Position {
        return float4(input.position, 1.0);
    }
    """;
```

The indentation every line shares is removed, so the text can be indented with
the code around it and still come out at column zero; the line break after the
opening quotes and the whitespace before the closing quotes are not part of the
value either. `${}` still interpolates. The only thing that needs escaping is
`${` itself, written `$${`:

```tern
println("""a $${template} b""");   // a ${template} b
```

A raw string ends at the first `"""`, so a document that must contain one is the
one case where an ordinary string is the answer.

### Formatting

`text.format` (and `"...".format`) fills placeholders, and a placeholder can say
how the value is written. This is the difference between a column that lines up
and a `padStart` on every line:

```tern
println(format("{:>8.2f}|", 3.14159));      //     3.14|
println(format("{:,} bytes, {:.1%}", 1234567, 0.1234));
                                             // 1,234,567 bytes, 12.3%
println(format("{:08.3f}", -3.14159));      // -003.142
println(format("{:x} {:b}", 48879, 10));    // beef 1010
println(format("{:*^9}|", "mid"));          // ***mid***|
```

The specification is `{[[fill]align][sign][0][width][,][.precision][type]}`:

| Part | Meaning |
| --- | --- |
| `align` | `<` left, `>` right, `^` centre. Numbers right by default, text left. |
| `fill` | Padding character, before an align: `{:*^9}`. |
| `sign` | `+` always shows a sign; a space reserves one for positives. |
| `0` | Zero padding to the width, after the sign. |
| `width` | Minimum width. |
| `,` | Thousands grouping. |
| `.precision` | Decimals for `f`/`e`/`%`, a maximum length for text. |
| `type` | `d` `f` `e` `x` `X` `o` `b` `%` `s`. |

With no type the value prints exactly as `str` would, so a width never changes
the digits. A type that cannot fit the value is an error rather than a quiet
conversion: `{:d}` on `3.9` says to use `{:.0f}` or `int()`.

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

Strings are sequences of Unicode code units, so `len` counts them: `len("héllo")`
is 5 and `len("日本語")` is 3. Indexing, slicing and iteration move whole
characters, so multi-byte text is never split in half.

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

### Labeled loops

A name and a colon before any loop names it, and `break name;` or
`continue name;` then leaves or repeats *that* loop instead of the innermost
one. It is the way out of two nested loops without a flag that every pass has
to test:

```tern
outer: for row in rows {
    for column in columns {
        if blocked(row, column) { break outer; }
        visit(row, column);
    }
}
```

The label belongs to the loop, not to the statement after it: `name: while`,
`name: for`, `name: loop` and `name: do` are the four forms, and
`name: let x = 1;` is an error (`TRN0121`). A `break` or `continue` naming a
loop that does not enclose it is an error too (`TRN0220`). A
`finally` between the jump and the loop it leaves runs, and locals captured by
a closure keep the value they had — leaving a loop early cannot change a value
a closure already captured.

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
  reports a catchable `StackOverflow` error at 10 000 frames rather than
  crashing the process. `tern run` and compiled binaries share the limit, so a
  script recursing to a given depth behaves the same on both.
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

### Spread

`...` splices a value's elements into a list literal, a map literal or a call.
It is always the same question — *the elements of this, not this* — and it is
answered by the same rule everywhere: what a `for` loop would give you.

```tern
let pair = [1, 2];
let more = [3, 4];

[...pair]                  // [1, 2]            a copy
[...pair, ...more]         // [1, 2, 3, 4]
[0, ...pair, 9]            // [0, 1, 2, 9]
[..."abc"]                 // ["a", "b", "c"]   text gives characters
[...0..<3]                 // [0, 1, 2]         a range gives numbers
[...{"x": 1, "y": 2}]      // [1, 2]            a map gives its values

{...defaults, "size": 1280}    // defaults first, the override after
{...a, ...b}                   // merged, later entries winning
{...m}                         // a copy of m

fn sum3(a, b, c) { return a + b + c; }
sum3(...[1, 2, 3])         // 6
sum3(1, ...[2, 3])         // 6
sum3(...[1, 2], 3)         // 6
```

In a list or a map literal, entries before and after a spread keep their order,
so `[...a, x, ...b]` reads the way it looks. A spread always copies: appending
to `[...xs]` cannot change `xs`.

In a call, the spread may be anywhere in the argument list and may be mixed with
ordinary arguments; a `...rest` parameter collects the result just as it would
from written-out ones. The callee may be anything callable — a function, a
builtin, a struct constructor or a bound method (`xs.push(...more)`).

The operand is evaluated once, and must be iterable: a list, a map, a string or a
range. Anything else is a `TypeError` at run time (`int is not iterable`), and a
map literal spreading something that is not a map raises
`a map literal spreads a map, not list`. `...` anywhere else — `let x = ...xs;` —
is a parse error (`TRN0114`).

Two things follow from the cost: a list without a spread is still one
instruction (`NewList` with its count), and a spread list is built by appending
to one list, so it is one allocation however many parts it has. A spread call
collects its arguments into a list first, which is what lets the count be
unknown until the call happens.

### Comprehensions

A list comprehension builds a list from a source in one expression:
`[element for name in source if condition]`. The element is any expression,
the source is anything a `for` loop can iterate, and the filters are optional
and may be repeated — every one of them has to hold.

```tern
[x * 2 for x in [1, 2, 3]]                   // [2, 4, 6]
[x for x in 1..10 if x % 2 == 0]             // [2, 4, 6, 8, 10]
[x * x for x in 0..<6 if x > 1 if x < 4]     // [4, 9]
[c for c in "abc"]                           // ["a", "b", "c"]
[k + "=" + str(v) for k, v in m]             // ["a=1", "b=2"]
```

A map comprehension is the same with `key: value` in front:

```tern
{str(x): x * x for x in [1, 2, 3]}           // {"1": 1, "2": 4, "3": 9}
{word: len(word) for word in words}
```

The loop variable is scoped to the comprehension: it does not leak afterwards,
and reusing an outer name inside one leaves the outer one alone. The source is
evaluated once, not once per element.

One `for` per comprehension; a second one is a parse error (`TRN0122`) rather
than a nested loop written to look flat — put the inner comprehension in the
element instead:

```tern
[[y for y in 0..<x] for x in 0..<3]          // [[], [0], [0, 1]]
```

A comprehension compiles to the same loop a hand-written one would: an empty
list, a `for`-style iteration, and an append per element, so it costs nothing
beyond the loop it replaces.

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
    {"kind": "click", "x": x, "y": y} => "click at ${x},${y}",
    {"kind": kind}       => "a ${kind} event",
    {name}               => "something called ${name}",
    _                    => "anything else",
};
```

Supported patterns: literals, `nil`, booleans, or-patterns (`a | b`), numeric
ranges (`2..9` inclusive, `2..<10` exclusive, negatives like `-9..-1`), list
patterns with `...rest`, map patterns, binding names (`n` binds), and guards
(`pattern if condition`). Patterns nest, so `[[a, b], [c, d]]` and
`{"rows": [[x, y], ...rest]}` bind every name they mention.

A map pattern is a **subset test**: each named key must be present and its
value must match, and any other keys in the subject are ignored. `{name}` is
shorthand for `{name: name}`, and `{}` matches any map at all (it is the map
equivalent of `_`), so put the more specific map arms first. A key whose value
is `nil` still counts as present: `{"a": nil}` does not match `{}`.

A range arm is a numeric test: it matches numbers and simply fails for anything
else, so a mixed set of arms never raises "cannot compare". Guards are ordinary
expressions and are only reached when the arm's pattern matched, so
`n if n > 100` after a `_`-free prefix still runs for non-numbers - check the
type in the guard (`n if type(n) == "int" and n > 100`) when the subject can be
mixed.

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

### Exit status

A script that is part of a build should say whether it worked. `os.exit` ends
the program with a status — 0 for success, anything else for failure — which is
what a shell, a Makefile or CI reads:

```tern
import "std:os";

let options = cli.parse({"out": "picture.png"});
if (options == nil) { os.exit(2); }        // --help was printed

if (!wrote_the_file) {
    println("could not write ${options["out"]}");
    os.exit(1);
}
```

`os.exit` is not an error, so a `try` block cannot swallow it: a script that
means to stop is not talked out of it by its own handler. Output written before
the call is flushed first. A status outside 0–255 is a `RangeError`, which *is*
catchable — the program is still running when that one is raised.

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
`std:random`, `std:text`, `std:lists`, `std:net`, `std:http`, `std:rcon`,
`std:sftp`, `std:cli`, `std:gl`, `std:d3d12`. Their members are also exposed as
namespaces (`math.sqrt`, `json.parse`, ...), so importing is optional.

```tern
import "std:json";
let data = json.parse("{\"n\": 3}");
println(data.n);
println(json.stringify([1, 2], 2));
```

### Options, in one call

`std:cli` reads the script's arguments against a schema of defaults, so an
option's name, type and documentation are one line instead of four. `false` is a
switch, a number takes a number, anything else takes text; `--help` prints the
table below from the schema and the descriptions, and answers `nil`.

```tern
import "std:cli";

fn main() {
    let options = cli.parse({
        "out": "picture.png",
        "size": "320x240",
        "samples": 1,
        "ascii": false,
    }, {
        "out": "where the picture goes",
        "size": "WIDTHxHEIGHT",
        "samples": "rays per pixel",
        "ascii": "print a text preview as well",
    });
    if (options == nil) { return; }    // --help already printed

    let size = cli.pair(options["size"], "--size");
    println(options["out"], size[0], size[1], options["samples"]);
}

main();
```

```
$ tern run shot.tern --help
usage: <script> [options]

options:
  --out TEXT           default "picture.png"   where the picture goes
  --size TEXT          default "320x240"       WIDTHxHEIGHT
  --samples N          default 1               rays per pixel
  --ascii              default false           print a text preview as well
  --help               this text
```

A typo is a `UsageError` that names the nearest option rather than a silent
default: `unknown option '--samplse'; did you mean '--samples'?`. Leftover
positional arguments come back from `cli.args()`, `cli.has("samples")` says
whether it was typed at all, and `--` ends the options.

---

## 13. Standard library

The complete, always-current list of globals, namespaces and per-type methods
lives in [`stdlib.md`](stdlib.md), which is generated by `tern docs` from the
engine itself. The summary below covers the parts you will use most.

For the reference proper there is [`msdn/`](msdn/README.md): one page per
function and per method, laid out like the Microsoft documentation — syntax,
parameters, return value, remarks, errors, an example and the neighbouring
calls. It is generated from the same catalogue plus the prose in
`tools/reference/`, so a function cannot be documented unless it exists, and
`tools/test.sh` fails if the pages are out of date.

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
| `fs` | `read readBytes write append exists size remove list mkdir isFile isDir open readChunk readAt seek position fileSize close` |
| `os` | `env setEnv cwd platform args joinPath lineSeparator` |
| `time` | `now millis sleep iso` |
| `random` | `seed float int bool choice shuffle` |
| `text` | `format upper lower trim split join pad` |
| `lists` | `range repeat zip enumerate chunk sort` |
| `net` | `encode decode listen accept connect send receive sendBytes receiveBytes maxFrame udpListen udpSend udpReceive close timeout port peer` |
| `http` | `request get post encodeRequest decodeResponse exchange` |
| `rcon` | `connect attach execute close` |
| `sftp` | `upload download list run batch command` |
| `gl` | `available info create clear program draw pixels save size close` |
| `d3d12` | `available info create clear pipeline draw pixels save size close` |

`text.format` accepts `{}` placeholders or indexed `{0}` placeholders.

### Files

`fs.read` reads a whole file, which is the wrong tool for a log that is still
growing. `fs.open` returns a handle that remembers its position, and
`fs.readChunk` returns the next piece:

```tern
let handle = fs.open("/var/log/huge.log");
let offset = 0;
while (true) {
    let chunk = fs.readChunk(handle, 10 * 1024 * 1024);
    if (chunk == "") { break; }
    offset = fs.position(handle);      // byte offset to resume from
    process(chunk);
}
fs.close(handle);
```

Offsets and lengths are bytes, and a chunk that would end in the middle of a
multi-byte character is extended by the two or three bytes the character still
needs, so chunks concatenate back into the file and nothing is decoded twice.
`fs.readAt(path, offset[, length])` does the same read without a handle, for
random access: reading the header of a 4 GiB archive costs one system call and
the bytes actually returned. A read at or past the end of the file is `""`, not
an error, which is what ends the loop above; `length` defaults to the rest of
the file and is capped at 1 GiB per call. `fs.seek`, `fs.position` and
`fs.fileSize` round out the handle, and `fs.close` releases it — a second close,
or a read on a handle that is not open, raises `IOError`.

Relative paths in `fs`, `gl` and `d3d12` calls resolve against the process's
working directory, which is what a shell command means by a path, and is the
same rule in the interpreter and in a compiled binary:

```
cd /srv/app && tern run /opt/tools/check.tern     # "logs/x.txt" is /srv/app/logs/x.txt
```

Only `import` resolves against the importing file, so a library can import its
own neighbours without depending on where the caller was started. Before this
rule the interpreter resolved `fs` paths against the script's directory while a
compiled binary used the working directory, which made a script that passed its
tests fail once it was run from another directory.

`fs.writeBytes` and `fs.appendBytes` are the other half of `readBytes`: they
take a list of `0..255` and put it on disk unchanged. `fs.write` cannot do that,
because it takes text and writes its UTF-8 encoding, and a string cannot hold a
byte such as `0x89` at all — `fs.write("out.bin", text.fromBytes([137]))` writes
three bytes (`EF BF BD`), not one. A PNG begins with `0x89`, so
`examples/raytracer.tern` builds its file as a list of bytes and writes it with
`writeBytes`; the whole encoder is about sixty lines of TERN.

`json.parse` raises a `JsonError` naming the problem and the offset in the text
(`expected ':' at offset 4`); `json.tryParse` returns its fallback instead of
raising. Documents nested deeper than 512 levels are refused rather than
overflowing the stack, and `json.stringify` writes map keys in insertion order.

### HTTP

`std:http` is a client: it speaks HTTP/1.1 on a socket, with no dependency on a
library that may not be installed. `http.get(url[, options])` and
`http.post(url, body[, options])` cover the common cases, and
`http.request(method, url[, options])` takes any method.

```tern
let response = http.get("https://example.com/index.html", {"timeout": 5000});
println(response["status"], response["reason"], response["headers"]["content-type"]);
println(response["body"]);
```

`https://` works: the connection is wrapped in TLS with the same machinery
`net.connect` uses, so the certificate is checked against the system store unless
`ca` names a PEM bundle (or `verify` is false). A `Host` header is only given a
port when it is not the scheme's default.

A response is a map with `status` (integer), `reason`, `version`, `headers`
(a map with lowercased names, repeated headers joined by `, `), `body` (text) and
`bytes` (the same body as a list of bytes). Options:

| Option | Meaning | Default |
| --- | --- | --- |
| `headers` | Map of extra header names to values; `Host`, `Connection` and `User-Agent` are sent unless you set them | none |
| `body` | Request body as text or bytes; `Content-Length` is added unless you set it | none |
| `timeout` | Milliseconds allowed for connecting and for the whole exchange | 10000 |
| `redirects` | Redirects to follow (301, 302, 303, 307, 308); a 303 switches to GET | 5 |
| `maxBytes` | Largest response body accepted | 8 MiB |
| `verify` | Check the TLS certificate chain and name (`https://` only) | `true` |
| `ca` | PEM bundle to trust instead of the system store (`https://` only) | system store |

Responses are framed by `Transfer-Encoding: chunked`, then `Content-Length`, then
end of stream, so a server that closes the connection at the end of the body
works too. Requests ask for `Connection: close`, which means one connection per
request and no keep-alive bookkeeping.

The halves are exposed for scripts that bring their own connection:
`http.encodeRequest(method, url[, options])` returns the request bytes,
`http.decodeResponse(bytes)` parses a complete response, and
`http.exchange(socket, method, url[, options])` writes a request on a socket from
`net.connect` and reads one response from it (no redirects there — one exchange,
one response).

### RCON

`std:rcon` speaks the Source RCON protocol that most game servers use: a
little-endian length, an id, a type, the body, and two NUL bytes.

```tern
import "std:rcon";

let session = rcon.connect("127.0.0.1", 27015, "secret");
println(rcon.execute(session, "status"));
rcon.close(session);
```

`rcon.connect(host, port, password[, options])` opens the socket, logs in and
returns a handle; a refused password raises `AuthenticationError` and the server
is left alone. `rcon.attach(socket, password[, options])` logs in on a connection
you opened with `net.connect` — the handle becomes the session, so `net.send` on
it then says `needs a connection handle, not an rcon session`. `rcon.execute`
runs one command and returns everything the server sent, joining responses that
arrive in several packets (the 4096-byte split is common). `rcon.close` closes
the socket. The only option is `timeout` (milliseconds, default 5000).

### SFTP

FTP transfers passwords in clear text; SFTP is the same job over SSH, and it is
what `std:sftp` speaks. There is no SFTP library to link against here, and
implementing SSH by hand is out of the question, so the namespace drives the
OpenSSH client in batch mode: the commands go to `sftp`'s standard input and
`BatchMode=yes` makes a missing key fail instead of prompting.

```tern
import "std:sftp";

let server = {"host": "example.com", "user": "deploy", "key": "/home/me/.ssh/id_ed25519"};
sftp.upload("build/app.tar.gz", "/srv/app.tar.gz", server);
sftp.download("/var/log/app.log", "app.log", server);
println(sftp.list("/srv", server));
```

Options are `host` and `user` (both required), `key` (a private key file),
`port` (default 22), `timeout` (milliseconds, default 30000) and `binary` (the
client to run, `sftp` from `PATH` unless you point it somewhere else). A local file
that is missing, or a key that cannot be read, is reported before anything is
started. `sftp.run(commands, options)` runs your own batch commands and returns
`{"status", "output", "error"}` without raising, which is what `upload`,
`download` and `list` are built on — they raise `SftpError` when `sftp` exits
non-zero, with the first line of its error output. `sftp.batch(commands)` and
`sftp.command(options)` return the batch text and the command line, so a script
can print exactly what would run. Paths with spaces are quoted.

The `sftp` program must be installed (OpenSSH client) and the host must accept
key-based authentication: there is no password prompt to drive. Use `binary`
when the client lives off `PATH`.

This is the only place TERN starts an external program. It is a deliberate
trade: SFTP is a subsystem inside an SSH connection, and SSH is cryptography
end to end, so the alternatives were linking an SSH library — which would stop
compiled programs from being self-contained — or writing the transport by hand,
which this project does not do. Everything else in the networking stack,
including TLS, is implemented in the two back ends themselves.

### Graphics (OpenGL)

`std:gl` renders with real OpenGL, but nothing is linked into the binary: libEGL
is opened at run time with `dlopen`, a surfaceless pbuffer supplies a context
with no window and no X server, and the GL entry points come from
`eglGetProcAddress`. A script that never renders does not need a driver, and one
that does gets `GLError` — not a crash — when there is none.

```tern
import "std:gl";

let surface = gl.create(640, 480);
gl.clear(surface, 0.1, 0.1, 0.15, 1);
let program = gl.program(surface, vertexSource, fragmentSource);
gl.draw(surface, program, [
    -0.5, -0.5, 1, 0, 0, 1,     // x, y, r, g, b, a
     0.5, -0.5, 0, 1, 0, 1,
     0.0,  0.5, 0, 0, 1, 1,
]);
gl.save(surface, "triangle.png");
gl.close(surface);
```

Vertices are six floats each and three make a triangle; attribute 0 receives the
`vec2` position and attribute 1 the `vec4` colour, so a program is written the
usual way (`layout(location = 0) in vec2 position;`). The context is OpenGL 3.3
core, which is what the shaders are compiled against. `gl.pixels(handle)` reads
the surface back as RGBA bytes with the bottom row first, like `glReadPixels`;
`gl.save` writes the same pixels as a PNG (top row first, so the image is the
right way up) with stored deflate blocks — no zlib, no image library.

DLSS is deliberately missing. It is NVIDIA's proprietary NGX SDK: it needs a
signed agreement, an RTX card and integration into a Direct3D or Vulkan
renderer, so it cannot live in a dependency-free scripting language. Direct3D 12
is not missing — it is the next section, and unlike this module it also works
from a Windows binary.

`tools/test-gl.sh` builds `tools/gl/mock_driver.c` — a fake libEGL that answers
the same calls — points `TERN_GL_LIBRARY` at it, runs the same program in both
back ends, and decodes the PNG that comes out. That is how the binding is tested
without a GPU; with a real driver the same script exercises the actual hardware.

### Graphics (Direct3D 12)

`std:d3d12` is the Windows renderer: a device, an offscreen render target, a
clear, a pipeline, a draw, and the pixels back as a PNG. Like the OpenGL module
it links nothing — `d3d12.dll` is opened at run time and every method is a call
through the COM vtable at the slot the Windows SDK gives it. HLSL is compiled at
run time by `d3dcompiler_47.dll`, which is loaded the same way.

```tern
import "std:d3d12";

let target = d3d12.create(640, 480);
d3d12.clear(target, 0.1, 0.1, 0.15, 1);

let pipeline = d3d12.pipeline(target, vertexSource, pixelSource);
d3d12.draw(target, pipeline, [
    -0.5, -0.5, 1, 0, 0, 1,
     0.5, -0.5, 0, 1, 0, 1,
     0.0,  0.5, 0, 0, 1, 1,
]);

d3d12.save(target, "triangle.png");
d3d12.close(target);
```

`d3d12.pipeline` compiles both stages, creates an empty root signature with
`ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT` and builds the graphics pipeline state. The
input layout is fixed so that one vertex format works everywhere: `POSITION` as
`float3` at offset 0, `COLOR` as `float4` at offset 12, stride 28. Both stages
use the entry point `main`. A shader that fails to compile raises `D3D12Error`
with the compiler's first line, which is usually the line number and the reason.

The structures are mirrored by hand, so the Windows build checks the mirror
against the real `d3d12.h`: `tools/d3d12/gen_layout_check.py` turns the
`_Static_assert`s in `tern_d3d12.h` into a translation unit that
`tools/test-d3d12.sh` compiles for Windows. The graphics pipeline description is
656 bytes with the input layout at 552, and a single misplaced field is a
compile error rather than a corrupt draw.

`d3d12.pixels` returns RGBA bytes with the bottom row first, exactly like
`gl.pixels`, so one script can render with either backend; `d3d12.save` flips
them for the PNG. Resource sizes are capped at 16384, which is the Direct3D 12
limit for a 2D texture, and the readback pitch comes from the device rather than
from a guess.

Two caveats. The compiled backend has to be a Windows binary
(`python3 native/ternc.py --target windows app.tern -o app.exe`), and the
interpreter needs .NET on Windows. Neither is available in a POSIX build, where
every call raises `D3D12Error: Direct3D 12 is not available on this system`.

Which modules a Windows binary has is not uniform, and the difference is worth
knowing before writing a program that has to run there: `math`, `time`, `json`,
`os`, `random`, `text`, `lists`, `fs` and `d3d12` are all present, while `net`,
`http`, `rcon`, `sftp` and `gl` raise
`UnsupportedError: this module is not part of the Windows build yet` — the names
exist, so the failure is a sentence rather than `map has no method 'send'`.
`d3d12.available()` is the way to ask whether the renderer is really there: it
returns `false` when `d3d12.dll` cannot be loaded, and `create` can still fail on
a machine without `d3dcompiler_47.dll`, so a program that only wants to render
when it can should treat the whole attempt as fallible.

The pipeline is verified against a mock driver, not against a GPU: no Windows
machine is available here, so the tests prove the plumbing — that the vertex
buffer reaches the draw and the colour lands in the target — and not that
Direct3D accepts the description. Running `tools/tern tools/d3d12/probe.tern`
on a Windows machine is the check that remains.

The mock answers the way Windows does, which took a while to get right: it used
to ignore the interface id that every `Create*` is given and hand back an object
regardless, so a binding that passed the *device's* id to `CreateCommandQueue`
passed every test here and failed at the first call on real hardware with
`0x80004002` (`E_NOINTERFACE`). The ids are now all correct — each call asks for
the interface it is creating — and `tools/d3d12/mock_d3d12.c` rejects a wrong one
exactly as Windows does, so the same mistake fails on this machine. The ids come
from the `uuid()` attributes in `d3d12.idl` and `dxgi.idl`, which is what the
SDK's `DEFINE_GUID` lines are generated from.

An HRESULT that is not quoted verbatim is translated: "no adapter on this machine
supports Direct3D 12 at feature level 11_0" is `DXGI_ERROR_UNSUPPORTED`, and "an
interface id was rejected - this is a bug in TERN" is the one above.

---

## 14. Program entry, arguments and sandboxing

A program is executed top to bottom. There is no `main` requirement; the last
top-level value of a REPL line is echoed.

Command line arguments are available as a list:

```tern
let args = os.args();
```

Runner options go before the script path; everything after it is the script's,
even when it starts with `--`. The rule is a split, not a filter, because a
script with its own flags has no other way to see them:

```
tern run --no-net backup.tern --dry-run /srv      # os.args() == ["--dry-run", "/srv"]
```

Before this split the runner scanned the whole command line for `--` options,
so `tern run deploy.tern --dry-run` ran a real deployment and the flag never
reached the script. `--` as the first argument is no longer special: everything
after the path already belongs to the script.

The embedder can disable file system access. When disabled, every `fs` call and
`import` of a file raises a catchable error:

```
blocked: file system access is disabled for this script
```

Network access is disabled the same way (`--no-net`, or `engine.AllowNetwork =
false`). `net.listen` and `net.connect` then raise `network access is disabled for
this script`; `net.encode` and `net.decode` keep working, since they never touch
a socket.

### Sockets and the wire format

`std:net` sends values, not bytes: `net.send(socket, value)` encodes the value
and writes it as one frame (a 4-byte big-endian length followed by the payload),
and `net.receive(socket)` reads exactly one frame back. Sockets are addressed by
integer handles, so a script behaves the same in the interpreter and in a
compiled binary.

```tern
import "std:net";

struct Ping { from = "", n = 0 }

let server = net.listen("127.0.0.1", 8080, {"token": "s3cret"});
let client = net.connect("127.0.0.1", net.port(server),
                         {"token": "s3cret", "timeout": 5000});
let peer = net.accept(server);

net.timeout(peer, 5000);          // milliseconds; 0 waits forever
net.send(client, Ping("client", 1));
println(net.receive(peer));       // Ping(from: "client", n: 1)

net.close(client);
net.close(peer);
net.close(server);
```

`net.listen(0)` binds an ephemeral port; read it back with `net.port(handle)`.

Both `listen` and `connect` take an optional options map:

| Option | Meaning | Default |
| --- | --- | --- |
| `token` | Shared secret the peer must present before the connection is handed to the script | no token |
| `maxFrame` | Largest frame this socket will accept, in bytes (1 .. 64 MiB) | 1 MiB |
| `timeout` | Connect, send and receive timeout in milliseconds; `0` waits forever | no timeout |
| `raw` | Talk to a peer that does not speak the framing protocol: no hello, no frames | `false` |
| `tls` | Encrypt the connection (client side only) | `false` |
| `sni` | Name used for SNI and for the certificate check | the host |
| `verify` | Check the certificate chain and the name | `true` |
| `ca` | PEM bundle to trust instead of the system store | system store |

`maxFrame` is a memory guard, not a wire setting: the length in a frame header is
compared with the limit before the payload is read, so four bytes from a peer
cannot make the process allocate 64 MiB. Raise it with `net.maxFrame(handle, n)`
when a protocol needs bigger messages.

Every connection opens with a hello frame: one version byte, the token as a
length-prefixed string, and nothing else yet (the rest is reserved). `connect`
sends it and `accept` reads it, so a listener created with a token refuses a
mismatched peer by sending the reason back and closing, before the script sees
the socket; `net.accept` then raises `AuthenticationError`. The handshake is
compared in constant time and gives up after five seconds (or the listener's own
`timeout`, when that is shorter). A peer that vanishes mid-handshake is a
`ConnectionError`.

`net.sendBytes(socket, bytes)` and `net.receiveBytes(socket, count)` move raw
bytes with no frame header, for protocols layered on top of `net` (HTTP, RCON,
anything with its own framing). Byte lists use integers in 0..255 and are
rejected otherwise. `text.toBytes` and `text.fromBytes` convert between text and
those lists, so a line-oriented protocol does not need a hand-written encoder:

```tern
net.sendBytes(socket, text.toBytes("PING\r\n"));
let line = text.fromBytes(net.receiveBytes(socket, 6));    // "PONG\r\n"
```

`{"raw": true}` on `listen` or `connect` skips the hello handshake and turns off
framing entirely: `send`/`receive` then raise a `TypeError` explaining that the
connection is raw, and only the byte calls work. That is the mode for a peer
with its own protocol — a browser, an RCON server, a database — which would
otherwise sit waiting for a hello nobody sends.

`net.receiveSome(socket, count)` returns as soon as any bytes arrive, up to
`count`, and returns `[]` at end of stream, which is what a read-until-closed
loop wants; `net.receiveBytes` instead waits for exactly `count` and raises when
the peer closes early. A timeout is still an error, so a loop that must tell
"the peer went away" from "nothing arrived yet" sets `net.timeout` first:

```tern
net.timeout(socket, 2000);
let total = 0;
while true {
    let chunk = net.receiveSome(socket, 65536);
    if len(chunk) == 0 { break; }        // end of stream, not a timeout
    total += len(chunk);
}
println("read " + str(total) + " bytes");
```

A connection can be encrypted with `{"tls": true}`:

```tern
let secure = net.connect("example.com", 443, {"tls": true, "timeout": 5000});
net.sendBytes(secure, http.encodeRequest("GET", "https://example.com/"));
println(http.decodeResponse(net.receiveBytes(secure, 4096)));
```

The handshake happens before the hello, so everything after it — frames, raw
bytes, `http.exchange` — is encrypted and the API does not change. The chain is
checked against the system store, or against the PEM bundle named by `ca`, and
the name in the certificate is checked against `sni` (the host when it is not
given). `verify: false` trusts any certificate and skips the name check, which is
what a self-signed server needs. TLS is a thin wrapper over a real library —
`SslStream` in the interpreter and the system OpenSSL in a compiled binary,
loaded at run time so a machine without it can still build scripts — and never
hand-rolled crypto. Only the client side is implemented: `net.listen` refuses
`tls`. `tools/test-tls.sh` runs a live handshake against a generated certificate
in both back ends.

Datagrams are separate handles from `net.udpListen`:

```tern
let socket = net.udpListen(9000);            // or udpListen("0.0.0.0", 9000)
net.udpSend(socket, "127.0.0.1", 9001, [0x10, 0x20]);

let packet = net.udpReceive(socket);          // {"from": "127.0.0.1:9001", "data": [...]}
if packet != nil {
    println(packet["from"], len(packet["data"]));
}
```

`udpSend` takes text (sent as UTF-8) or a byte list and moves at most 65507
bytes per datagram. `udpReceive` returns one datagram as a map with `from` and
`data`, or `nil` when a `net.timeout` expires — unlike a stream, a datagram that
never arrives is ordinary traffic rather than an error. A smaller size argument
truncates the datagram. UDP is unreliable by definition: ordering, duplicates
and retries belong in the script (the runtime will not add a reliable layer).

Any value can travel: `nil`, booleans, integers, floats, strings, lists, maps,
ranges and structs. Integers and floats keep their type and exact bits, map
insertion order is preserved, and a range keeps its step and whether its end is
inclusive. A struct is sent with its type name and field names, so the receiver
rebuilds a real instance of that struct with the same fields — fields it does
not know about are dropped and fields the sender omitted keep their defaults. If
the receiver has not declared that struct, the fields arrive as a map instead,
so old readers keep working.

Encoding is compact and deterministic: the same value always produces the same
bytes, and `net.encode` / `net.decode` expose it directly for storage or for
talking to another language:

```tern
let bytes = net.encode({"a": [1, 2]});
println(len(bytes));              // 25
println(net.decode(bytes));       // {"a": [1, 2]}
```

Limits: values nest at most 128 levels deep and a frame is at most 64 MiB.
Decoding rejects truncated input, unknown tags and trailing bytes with a
catchable `SerializationError`, and `net.receive` raises `ConnectionError` when
the other side closes the connection.

---

## 15. Grammar summary

```ebnf
program        = statement* EOF ;
statement      = decl | fnDecl | structDecl | importDecl | exprStmt
               | ifStmt | whileStmt | loopStmt | forStmt | doWhileStmt
               | returnStmt | breakStmt | continueStmt | throwStmt
               | tryStmt | block ;
decl           = ("let" | "const") (IDENT | pattern) ("=" expression)? ";"? ;
fnDecl         = "fn" IDENT "(" params? ")" block ;
params         = param ("," param)* ;
param          = IDENT ("=" expression)? | "..." IDENT ;
structDecl     = "struct" IDENT "{" (field ("," field)*)? method* "}" ;
field          = IDENT ("=" expression)? ;
importDecl     = "import" STRING ("as" IDENT)? ";"? ;
ifStmt         = "if" expression block ("else" (ifStmt | block))? ;
whileStmt      = (IDENT ":")? "while" expression block ;
loopStmt       = (IDENT ":")? "loop" block ;
forStmt        = (IDENT ":")? "for" ((IDENT ("," IDENT)?) | pattern) "in" expression block ;
doWhileStmt    = (IDENT ":")? "do" block "while" expression ";"? ;
tryStmt        = "try" block ("catch" ("(" IDENT ")")? block)? ("finally" block)? ;
throwStmt      = "throw" expression ";"? ;
breakStmt      = "break" IDENT? ";"? ;
continueStmt   = "continue" IDENT? ";"? ;
block          = "{" statement* "}" ;
pattern        = alternative ("|" alternative)* ;
alternative    = literal | range | "_" | IDENT | listPattern | mapPattern ;
range          = number (".." | "..<") number ;
listPattern    = "[" (pattern ("," pattern)*)? "..." IDENT? "]" ;
mapPattern     = "{" (entry ("," entry)*)? "}" ;
entry          = (IDENT | STRING) (":" pattern)? ;
```

Expressions follow the precedence table in §4 and include: literals, list/map
displays (each element or entry may be `...expression`), list comprehensions
(`"[" expression "for" IDENT ("," IDENT)? "in" expression ("if" expression)* "]"`),
map comprehensions (the same with `key ":" value` in front), identifiers, `this`,
calls (each argument may be `...expression`), member access, indexing, slicing,
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
| `TRN03xx` | linter (warnings only) |

Codes worth recognising, because the message alone does not say where to look:
`TRN0114` a spread outside a list, a map or a call, `TRN0122` a comprehension
with a second `for`, `TRN0204` a file module that cannot be loaded,
`TRN0206` undefined name, `TRN0207` too many arguments, `TRN0210` invalid assignment target, `TRN0212`
unsupported assignment operator, `TRN0213` unbounded range outside a slice,
`TRN0214` more than two names in a `for`, `TRN0215` a pattern declaration with
no initializer, `TRN0216` a name bound twice in one pattern, `TRN0218` a `std:`
module this build does not have, `TRN0219` an internal disagreement about the
operand stack (below), `TRN0220` a `break` or `continue` that names no
enclosing loop, `TRN0221` a struct method that captures an outer
variable it cannot reach, `TRN0222` an internal disagreement about a spread.

`TRN0219` is never the script's fault and never the script's problem to solve:
it means the compiler's own count of what a statement left on the operand stack
did not come back to where it started, so the frame size it works out for the
function cannot be trusted. Both back ends size a frame from that number - the
interpreter its operand stack, the compiled C its `Value st[]` array - so the
check exists to turn a wrong frame into one compiler message instead of a
corrupt run. Every statement is meant to begin and end with the same depth; when
this fires, the stack-tracking in the compiler is what needs the fix, and the
report should say which statement disagreed.

`TRN0218` is about the runner rather than the script: a `std:` module is
compiled into it, so the message lists the ones the build does have. If the
name you expected is not among them, the runner is older than the script —
`tern version` says which assembly is answering and when it was built.

### The linter

Every compilation runs a small AST analysis pass. Its findings are **warnings**:
the script still compiles and runs exactly as written, and warnings print to
stderr during `tern run` unless you pass `--quiet`.

| Code | Meaning |
| ---- | ------- |
| `TRN0304` | assignment used as a condition: `if (x = 1)` |
| `TRN0305` | a value compared with itself: `x == x` |
| `TRN0306` | two collection literals compared, which is always false |
| `TRN0308` | a local shadows an enclosing local of the same name |
| `TRN0310` | member access on a literal `nil`, which always raises |

The linter never fires for `nil` receivers inside a `try` block, since catching
that error is a legitimate pattern, and it ignores shadowing of top-level
variables, which is normal in scripts.

### Command line

```bash
tern run script.tern [args]   # execute, printing lint warnings to stderr
tern run script.tern --quiet  # execute without lint warnings
tern run script.tern --time   # also report wall clock time
tern run script.tern --no-fs  # sandbox the file system
tern run script.tern --no-net # sandbox the network
tern check script.tern        # parse, lint and compile, but do not run
tern disasm script.tern       # dump the bytecode
tern emit script.tern -o out.json  # export the bytecode as JSON (native backend)
tern docs                     # print the standard library reference
tern repl                     # interactive session
```

`tern check` exits with `2` on errors and `0` when the file compiles, even if it
produced warnings. `tern disasm` is the tool to reach for when reasoning about
performance.

`tern emit` is the input to the ahead-of-time backend in [`native/`](../native/README.md),
which compiles the same bytecode to C and then to a standalone binary:

```bash
native/build.sh script.tern -o script
./script
```

### The REPL

`tern repl` evaluates one expression at a time and echoes the value of the last
expression. Blocks may span lines: `tern>` continues with `...` until braces,
brackets and quotes balance. Commands start with `:`.

| Command | Effect |
| ------- | ------ |
| `:help` | show the command list |
| `:globals` | list the names defined so far |
| `:reset` | forget every definition and start over |
| `:quit` / `exit` | leave the session |
