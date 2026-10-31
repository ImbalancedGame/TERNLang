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
`std:sftp`. Their members are also exposed as namespaces (`math.sqrt`,
`json.parse`, ...), so importing is optional.

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

Two APIs are deliberately missing. DirectX is Windows-only and the compiled
backend emits POSIX C, so it would need a Windows port first. DLSS is NVIDIA's
proprietary NGX SDK: it needs a signed agreement, an RTX card and integration
into a Direct3D or Vulkan renderer, so it cannot live in a dependency-free
scripting language.

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

The pipeline is verified against a mock driver, not against a GPU: no Windows
machine is available here, so the tests prove the plumbing — that the vertex
buffer reaches the draw and the colour lands in the target — and not that
Direct3D accepts the description. Running `tools/tern tools/d3d12/probe.tern`
on a Windows machine is the check that remains.

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
pattern        = alternative ("|" alternative)* ;
alternative    = literal | range | "_" | IDENT | listPattern | mapPattern ;
range          = number (".." | "..<") number ;
listPattern    = "[" (pattern ("," pattern)*)? "..." IDENT? "]" ;
mapPattern     = "{" (entry ("," entry)*)? "}" ;
entry          = (IDENT | STRING) (":" pattern)? ;
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
| `TRN03xx` | linter (warnings only) |

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
