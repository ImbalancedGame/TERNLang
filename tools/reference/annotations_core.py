"""Content for the MSDN-style reference: core namespaces and methods.

Split from annotations.py only to keep the files a readable size; the generator
merges the three dictionaries.
"""

NAMESPACES = {
    "global": {
        "title": "global",
        "summary": "The functions that need no import.",
        "intro": """
These are bound in every script, module and REPL session. They cover conversion,
output, reflection and the two ways to fail.

Names are not keywords: a `let` may shadow `len`. A shadowed global is still
reachable through nothing else, so shadowing one is usually a mistake.
""",
        "example": [
            'println("items:", len([1, 2, 3]), type(1.5));',
            'assert(len("abc") == 3, "length");',
        ],
    },
    "json": {
        "title": "json",
        "summary": "Reading and writing JSON.",
        "intro": """
`json.parse` produces the obvious values: objects become maps, arrays become
lists, numbers become integers when they are written without a fraction or
exponent and floats otherwise, and `null` becomes `nil`.

`json.stringify` accepts maps, lists, strings, numbers, booleans and `nil`.
Anything else — a function, a handle — is reported rather than silently dropped.
""",
        "example": [
            'import "std:json";',
            "",
            'let value = json.parse("{\\"a\\": [1, 2, 3]}");',
            'println(value["a"][1]);',
            'println(json.stringify(value, 2));',
        ],
    },
    "lists": {
        "title": "lists",
        "summary": "Whole-list helpers that do not belong on a receiver.",
        "intro": """
The methods on a list cover the common cases; these are the operations that read
better as a function — building a list, pairing two lists, or sorting without
touching the original.
""",
        "example": [
            'import "std:lists";',
            "",
            'println(lists.zip([1, 2], ["a", "b"]));',
            "println(lists.chunk([1, 2, 3, 4, 5], 2));",
        ],
    },
    "math": {
        "title": "math",
        "summary": "Trigonometry, rounding, and the constants.",
        "intro": """
Angles are radians. `math.pi`, `math.tau`, `math.e`, `math.nan` and
`math.infinity` are constants, not functions: write them without parentheses.

`math.clamp` keeps the argument's type — an integer in, an integer out — while
`floor`, `ceil`, `trunc` and `round` always return a float, so that `-2.0` stays
recognisably a number from the same family. Wrap the result in `int()` when the
integer matters.
""",
        "example": [
            'import "std:math";',
            "",
            "println(math.sin(math.pi / 2));",
            "println(math.round(2.567, 2));",
            "println(math.clamp(15, 0, 10));",
        ],
    },
    "os": {
        "title": "os",
        "summary": "Arguments, environment, paths and the platform.",
        "intro": """
`os.args` holds the arguments after the script path. `os.joinPath` uses the
platform's separator, so a path built here runs on the machine that built it and
nowhere else; a path that has to travel belongs in a config file, not in `joinPath`.
""",
        "example": [
            'import "std:os";',
            "",
            'println(os.platform, os.cwd());',
            'println(os.joinPath("build", "out", "app.png"));',
            'println(os.env("HOME", "(unset)"));',
        ],
    },
    "random": {
        "title": "random",
        "summary": "A seeded generator, for games and for tests.",
        "intro": """
One generator per engine, seeded from the clock unless `random.seed` is called.
Seeding makes a run reproducible, which is what a failing test needs.

`random.int` is inclusive at both ends. `random.choice` returns `nil` for an
empty list rather than raising, so a caller can fall back.
""",
        "example": [
            'import "std:random";',
            "",
            "random.seed(1234);",
            "println(random.int(1, 6));",
            'println(random.choice(["rock", "paper", "scissors"]));',
        ],
    },
    "text": {
        "title": "text",
        "summary": "Text helpers that read better as functions.",
        "intro": """
The same operations exist as methods on a string; `text.format` is the one with
no method equivalent that matters, and `text.join` is the function form of
`list.join` for code that already has a list in hand.
""",
        "example": [
            'import "std:text";',
            "",
            'println(text.format("{} of {}", 3, 10));',
            'println(text.join(["a", "b"], "-"));',
        ],
    },
    "time": {
        "title": "time",
        "summary": "The clock, and sleeping.",
        "intro": """
Two clocks are exposed because two questions are asked: `time.millis` measures a
duration (a monotonic-enough count of milliseconds since the epoch), and
`time.iso` asks what the date is. `time.now` is the epoch in seconds as a float,
which is the form most protocols want.
""",
        "example": [
            'import "std:time";',
            "",
            "let started = time.millis();",
            "time.sleep(0.05);",
            "println(time.millis() - started >= 50);",
            'println(time.iso(time.now()));',
        ],
    },
}

FUNCTIONS = {
    # --------------------------------------------------------------- global
    "global.print": {
        "summary": "Writes its arguments to standard output, separated by a space.",
        "params": [("values", "any", "Any number, including none.")],
        "returns": None,
        "remarks": "Writes without a trailing newline; `println` adds one. Each value is "
                   "rendered the way `str` renders it, so strings appear unquoted.",
        "example": ['print("a", 1, true);', 'println("");'],
        "see": ["println", "eprint", "str", "repr"],
    },
    "global.println": {
        "summary": "Writes its arguments and a newline.",
        "params": [("values", "any", "Any number, including none.")],
        "returns": None,
        "example": ['println("hello", "world");'],
        "see": ["print", "repr"],
    },
    "global.eprint": {
        "summary": "Writes its arguments to standard error.",
        "params": [("values", "any", "Any number, including none.")],
        "returns": None,
        "remarks": "Diagnostics belong here: standard output stays usable for data even "
                   "when the script is being piped.",
        "example": ['eprint("warning: nothing to do");'],
        "see": ["print", "println"],
    },
    "global.str": {
        "summary": "Converts a value to its string form.",
        "params": [("value", "any", "The value.")],
        "returns": ("string", "Its display form."),
        "remarks": "Strings are returned unchanged; inside a list or a map they are quoted, "
                   "which is what `repr` does everywhere. A float always shows its fractional "
                   "part: `str(2.0)` is `2.0`, not `2`.",
        "example": ['println(str(1.5) + "!");', "println(str([1, 2]));"],
        "see": ["repr", "global.format"],
    },
    "global.repr": {
        "summary": "Converts a value to a debug representation.",
        "params": [("value", "any", "The value.")],
        "returns": ("string", "A form that quotes strings and shows types where they differ."),
        "remarks": "`str` is for the user, `repr` is for the programmer: `str(\"a\")` is `a`, "
                   "`repr(\"a\")` is `\"a\"`.",
        "example": ['println(repr("a"), str("a"));'],
        "see": ["str"],
    },
    "global.len": {
        "summary": "Counts the items in a string, list, map or range.",
        "params": [("value", "string|list|map|range", "The value.")],
        "returns": ("int", "The length."),
        "errors": [("TypeError", "The value has no length.")],
        "remarks": "For a string this counts UTF-16 code units, which is what an index "
                   "means too: ASCII text counts one per character, and a code point above "
                   "U+FFFF counts two. `chars()` splits by character, so `len(s.chars())` is "
                   "the number of characters and `len(s)` is the number of indices.",
        "example": ['println(len("héllo"), len([1, 2]), len({"a": 1}), len(0..<5));'],
        "see": ["string.count", "list.isEmpty"],
    },
    "global.type": {
        "summary": "The type name of a value.",
        "params": [("value", "any", "Any value.")],
        "returns": ("string", "`nil`, `bool`, `int`, `float`, `string`, `list`, `map`, `range`, "
                              "`function`, `native` or a struct's name."),
        "example": ['println(type(nil), type(1), type(1.0), type("s"), type([]), type({}));'],
        "see": ["bool", "str"],
    },
    "global.bool": {
        "summary": "Converts a value to a boolean.",
        "params": [("value", "any", "The value.")],
        "returns": ("bool", "The truth value."),
        "remarks": "`nil`, `false`, `0`, `0.0`, `\"\"`, `[]` and `{}` are false; everything "
                   "else, including a range, is true.",
        "example": ['println(bool(0), bool(""), bool([]), bool("0"));'],
        "see": ["type"],
    },
    "global.int": {
        "summary": "Converts a value to an integer.",
        "params": [("value", "any", "The value."),
                   ("radix", "int", "Optional, 2..36; only used when the value is a string.")],
        "returns": ("int", "The integer."),
        "errors": [("ArgumentError", "A string cannot be parsed."),
                   ("TypeError", "The value cannot be converted.")],
        "remarks": "A float truncates toward zero; `true` is 1 and `nil` is 0. A string is "
                   "trimmed first, and with no radix it may be integral text (`\"42\"`) or a "
                   "float (`\"4.9\"`, which truncates).",
        "example": ['println(int("ff", 16), int(4.9), int(true), int(-4.9));'],
        "see": ["float", "string.toInt"],
    },
    "global.float": {
        "summary": "Converts a value to a float.",
        "params": [("value", "any", "The value.")],
        "returns": ("float", "The value as a float."),
        "errors": [("ArgumentError", "A string cannot be parsed."),
                   ("TypeError", "The value cannot be converted.")],
        "example": ['println(float("2.5") + 1.0);'],
        "see": ["int", "string.toFloat"],
    },
    "global.chr": {
        "summary": "The string for a Unicode code point.",
        "params": [("code", "int", "0..1114111.")],
        "returns": ("string", "The one-character string."),
        "errors": [("ArgumentError", "The code point is out of range or a surrogate.")],
        "example": ['println(chr(9731));'],
        "see": ["ord"],
    },
    "global.ord": {
        "summary": "The code point of a string's first character.",
        "params": [("text", "string", "A non-empty string.")],
        "returns": ("int", "The code point."),
        "errors": [("ArgumentError", "The string is empty.")],
        "example": ['println(ord("A"), ord("☃"));'],
        "see": ["chr"],
    },
    "global.range": {
        "summary": "Creates an integer range.",
        "params": [("stop", "int", "The exclusive end, when it is the only argument."),
                   ("start", "int", "The inclusive start."),
                   ("stop", "int", "The exclusive end.")],
        "returns": ("range", "A range that can be iterated, indexed by `len`, or materialised with `toList`."),
        "errors": [("TypeError", "An argument is not an integer.")],
        "remarks": "`range(5)` is `0..<5`, `range(2, 5)` is `2..<5`, and `range(0, 10, 2)` "
                   "steps by two. A negative step counts down.",
        "example": ["println(len(range(5)), range(2, 5).toList());"],
        "see": ["lists.range", "range.toList"],
    },
    "global.keys": {
        "summary": "The keys of a map, in insertion order.",
        "params": [("map", "map", "The map.")],
        "returns": ("list", "The keys."),
        "errors": [("TypeError", "The argument is not a map.")],
        "example": ['println(keys({"a": 1, "b": 2}));'],
        "see": ["values", "map.keys"],
    },
    "global.values": {
        "summary": "The values of a map, in insertion order.",
        "params": [("map", "map", "The map.")],
        "returns": ("list", "The values."),
        "errors": [("TypeError", "The argument is not a map.")],
        "example": ['println(values({"a": 1, "b": 2}));'],
        "see": ["keys", "map.values"],
    },
    "global.has": {
        "summary": "True when a map has a key.",
        "params": [("map", "map", "The map."), ("key", "any", "The key, compared by value.")],
        "returns": ("bool", "Whether the key is present."),
        "errors": [("TypeError", "The first argument is not a map.")],
        "example": ['println(has({"a": 1}, "a"), has({"a": 1}, "b"));'],
        "see": ["keys", "map.has"],
    },
    "global.copy": {
        "summary": "A shallow copy of a list or a map.",
        "params": [("value", "list|map", "The container.")],
        "returns": ("list|map", "A new container with the same items."),
        "errors": [("TypeError", "The value is neither a list nor a map.")],
        "remarks": "The items are shared, not cloned: changing a nested list through the copy "
                   "changes it through the original too.",
        "example": ['let a = [1, 2]; let b = copy(a); b.push(3); println(len(a), len(b));'],
        "see": ["list.clone", "map.clone"],
    },
    "global.assert": {
        "summary": "Raises unless the condition is truthy.",
        "params": [("condition", "any", "The condition."),
                   ("message", "string", "Optional text for the failure.")],
        "returns": None,
        "errors": [("AssertionError", "The condition is falsy; the message is the error's text.")],
        "remarks": "Always evaluated: an `assert` is not compiled out, and it is caught by "
                   "`catch` like any other error, which is what makes it usable in tests. The "
                   "value a `catch` binds is the message string, not an object with fields.",
        "example": ['assert(1 + 1 == 2, "arithmetic");'],
        "see": ["error", "panic"],
    },
    "global.error": {
        "summary": "Raises an error the script can catch.",
        "params": [("message", "any", "The message; rendered with `str`.")],
        "returns": None,
        "errors": [("Error", "Always: this is the only thing it does.")],
        "remarks": "The error's name is `Error`. Use it for a condition the caller is expected "
                   "to handle; `panic` is for one they are not.",
        "example": ['try { error("bad input"); } catch (e) { println("caught:", e); }'],
        "see": ["panic", "assert"],
    },
    "global.panic": {
        "summary": "Aborts the script with an uncaught error.",
        "params": [("message", "any", "Optional text.")],
        "returns": None,
        "errors": [("Panic", "Always.")],
        "remarks": "A panic is still catchable from TERN code — the difference from `error` is "
                   "that a panic means the program cannot continue. An uncaught one exits with "
                   "status 1 and a stack trace on standard error.",
        "example": ['try { panic("unreachable"); } catch (e) { println(e); }'],
        "see": ["error", "assert"],
    },
    "global.input": {
        "summary": "Reads one line from standard input.",
        "params": [("prompt", "string", "Optional text written before reading, with no newline.")],
        "returns": ("string", "The line, without its terminator; `nil` at end of input."),
        "remarks": "End of input returns `nil` rather than an empty string, so a loop can tell "
                   "a blank line from a closed pipe.",
        "example": ['let name = input("name? ");', 'if (name != nil) { println("hello", name); }'],
        "see": ["print"],
    },
    "global.sleep": {
        "summary": "Blocks for a number of milliseconds.",
        "params": [("milliseconds", "int", "How long; a float is rejected, so round it first.")],
        "returns": None,
        "errors": [("TypeError", "The value is not an integer."),
                   ("ArgumentError", "The value is negative.")],
        "example": ["sleep(250);"],
        "see": ["time.sleep"],
    },
    "global.min": {
        "summary": "The smallest of its arguments.",
        "params": [("values", "number|string", "One or more comparable values.")],
        "returns": ("any", "The smallest."),
        "errors": [("ArgumentError", "Called with no arguments."),
                   ("TypeError", "The values are not comparable.")],
        "example": ["println(min(3, 1, 2), min([3, 1, 2]));"],
        "see": ["max", "list.min"],
    },
    "global.max": {
        "summary": "The largest of its arguments.",
        "params": [("values", "number|string", "One or more comparable values.")],
        "returns": ("any", "The largest."),
        "errors": [("ArgumentError", "Called with no arguments.")],
        "example": ["println(max(3, 1, 2));"],
        "see": ["min", "list.max"],
    },
    "global.abs": {
        "summary": "The absolute value.",
        "params": [("value", "number", "The value.")],
        "returns": ("number", "Its magnitude, of the same kind."),
        "errors": [("TypeError", "The argument is not a number.")],
        "example": ["println(abs(-3), abs(-2.5));"],
        "see": ["math.abs"],
    },

    # ----------------------------------------------------------------- json
    "json.parse": {
        "summary": "Parses JSON text into values.",
        "params": [("text", "string", "The document.")],
        "returns": ("any", "A map, list, string, number, boolean or `nil`."),
        "errors": [("JsonError", "The text is not JSON; the message gives the line and column.")],
        "remarks": "A number written with a fraction or exponent becomes a float; anything else "
                   "becomes an integer. A duplicate key replaces the earlier value and keeps its "
                   "position, like `map.set`.",
        "example": ['println(json.parse("{\\"a\\": 1}")["a"]);'],
        "see": ["json.tryParse", "json.stringify"],
    },
    "json.tryParse": {
        "summary": "Parses, returning nil for bad input.",
        "params": [("text", "string", "The document."),
                   ("fallback", "any", "Optional; `nil` by default.")],
        "returns": ("any", "The parsed value, or the fallback."),
        "remarks": "For input from a network or a file that may be something else entirely.",
        "example": ['println(json.tryParse("{", "default"));'],
        "see": ["json.parse"],
    },
    "json.stringify": {
        "summary": "Serialises a value as JSON.",
        "params": [("value", "any", "A value made of maps, lists, strings, numbers, booleans and nil."),
                   ("indent", "int", "Optional spaces per level; compact when omitted.")],
        "returns": ("string", "The JSON text."),
        "errors": [("TypeError", "The value contains something JSON cannot hold.")],
        "remarks": "Keys are written in insertion order, and strings are escaped according to "
                   "RFC 8259. A float that is whole is written without a fractional part, which "
                   "means it reads back as an integer.",
        "example": ['println(json.stringify({"a": [1, 2]}, 2));'],
        "see": ["json.parse"],
    },

    # ---------------------------------------------------------------- lists
    "lists.range": {
        "summary": "A list of integers from start to end.",
        "params": [("start", "int", "The first value, or the stop when it is the only argument."),
                   ("stop", "int", "The exclusive end."),
                   ("step", "int", "Optional, 1 by default; may be negative.")],
        "returns": ("list", "The integers."),
        "errors": [("ArgumentError", "`step` is zero.")],
        "remarks": "The list is materialised, unlike `range()`, which stays symbolic. Use this "
                   "when you need to index or mutate the result.",
        "example": ["println(lists.range(5), lists.range(2, 8, 2));"],
        "see": ["global.range", "range.toList"],
    },
    "lists.chunk": {
        "summary": "Splits a list into lists of at most n items.",
        "params": [("list", "list", "The list."), ("size", "int", "The chunk size, at least 1.")],
        "returns": ("list of list", "The chunks; the last may be shorter."),
        "errors": [("ArgumentError", "`size` is less than 1.")],
        "example": ["println(lists.chunk([1, 2, 3, 4, 5], 2));"],
        "see": ["list.take", "list.slice"],
    },
    "lists.enumerate": {
        "summary": "Index and value pairs.",
        "params": [("list", "list", "The list.")],
        "returns": ("list of list", "`[index, value]` entries."),
        "example": ["for (pair in lists.enumerate([\"a\", \"b\"])) { println(pair[0], pair[1]); }"],
        "see": ["list.each"],
    },
    "lists.repeat": {
        "summary": "A list with one value repeated.",
        "params": [("value", "any", "The value."), ("count", "int", "How many copies, at least 0.")],
        "returns": ("list", "The new list."),
        "errors": [("ArgumentError", "`count` is negative.")],
        "remarks": "The value is copied, so a repeated list is N independent lists.",
        "example": ["println(lists.repeat(0, 4));"],
        "see": ["list.concat"],
    },
    "lists.sort": {
        "summary": "A sorted copy, leaving the original alone.",
        "params": [("list", "list", "The list."),
                   ("comparator", "function", "Optional `fn (a, b)` returning a number; ascending when negative.")],
        "returns": ("list", "The sorted copy."),
        "errors": [("TypeError", "The items are not comparable, or the comparator does not return a number.")],
        "example": ["let xs = [3, 1, 2]; println(lists.sort(xs), xs);"],
        "see": ["list.sort"],
    },
    "lists.zip": {
        "summary": "Pairs the items of several lists.",
        "params": [("lists", "list", "Two or more lists.")],
        "returns": ("list of list", "One entry per index, as long as the shortest list."),
        "errors": [("ArgumentError", "Called with fewer than two lists.")],
        "remarks": "Uneven lists truncate to the shortest rather than padding with `nil`.",
        "example": ['println(lists.zip([1, 2, 3], ["a", "b"]));'],
        "see": ["lists.enumerate"],
    },

    # ----------------------------------------------------------------- math
    "math.pi": {
        "summary": "The constant pi.",
        "params": [],
        "returns": ("float", "3.141592653589793."),
        "remarks": "A constant, not a function: write `math.pi`.",
        "example": ["println(math.pi);"],
        "see": ["math.tau", "math.toRadians"],
    },
    "math.tau": {
        "summary": "The constant tau, two pi.",
        "params": [],
        "returns": ("float", "6.283185307179586."),
        "example": ["println(math.tau == 2.0 * math.pi);"],
        "see": ["math.pi"],
    },
    "math.e": {
        "summary": "The constant e.",
        "params": [],
        "returns": ("float", "2.718281828459045."),
        "example": ["println(math.e);"],
        "see": ["math.exp", "math.log"],
    },
    "math.nan": {
        "summary": "A quiet NaN.",
        "params": [],
        "returns": ("float", "A value that is not equal to anything, including itself."),
        "remarks": "Compare with `math.isNaN`, never with `==`.",
        "example": ["println(math.nan == math.nan, math.isNaN(math.nan));"],
        "see": ["math.isNaN"],
    },
    "math.infinity": {
        "summary": "Positive infinity.",
        "params": [],
        "returns": ("float", "Infinity; its negative is `-math.infinity`."),
        "example": ["println(math.infinity > 1e308);"],
        "see": ["math.isNaN"],
    },
    "math.abs": {
        "summary": "The absolute value.",
        "params": [("value", "number", "The value.")],
        "returns": ("number", "Its magnitude, of the same kind."),
        "example": ["println(math.abs(-2.5));"],
        "see": ["global.abs", "math.sign"],
    },
    "math.sqrt": {
        "summary": "The square root.",
        "params": [("value", "number", "A non-negative number.")],
        "returns": ("float", "The root; NaN for a negative argument rather than an error."),
        "example": ["println(math.sqrt(9.0));"],
        "see": ["math.cbrt", "math.pow"],
    },
    "math.cbrt": {
        "summary": "The cube root.",
        "params": [("value", "number", "Any number.")],
        "returns": ("float", "The root, negative for a negative argument."),
        "example": ["println(math.cbrt(-27.0));"],
        "see": ["math.sqrt", "math.pow"],
    },
    "math.pow": {
        "summary": "Raises a number to a power.",
        "params": [("base", "number", "The base."), ("exponent", "number", "The exponent.")],
        "returns": ("float", "The power."),
        "remarks": "Always a float; `**` on two integers gives an integer and raises on overflow.",
        "example": ["println(math.pow(2.0, 10.0));"],
        "see": ["math.exp", "math.sqrt"],
    },
    "math.exp": {
        "summary": "Raises e to a power.",
        "params": [("value", "number", "The exponent.")],
        "returns": ("float", "e to the power."),
        "example": ["println(math.exp(0.0));"],
        "see": ["math.log", "math.e"],
    },
    "math.log": {
        "summary": "The logarithm, in any base.",
        "params": [("value", "number", "A positive number."),
                   ("base", "number", "Optional; e by default.")],
        "returns": ("float", "The logarithm; NaN for a non-positive value."),
        "example": ["println(math.log(math.e), math.log(8.0, 2.0));"],
        "see": ["math.log2", "math.log10"],
    },
    "math.log2": {
        "summary": "The base 2 logarithm.",
        "params": [("value", "number", "A positive number.")],
        "returns": ("float", "The logarithm."),
        "example": ["println(math.log2(1024.0));"],
        "see": ["math.log"],
    },
    "math.log10": {
        "summary": "The base 10 logarithm.",
        "params": [("value", "number", "A positive number.")],
        "returns": ("float", "The logarithm."),
        "example": ["println(math.log10(1000.0));"],
        "see": ["math.log"],
    },
    "math.sin": {
        "summary": "The sine of an angle in radians.",
        "params": [("angle", "number", "Radians.")],
        "returns": ("float", "The sine."),
        "example": ["println(math.sin(math.pi / 2));"],
        "see": ["math.cos", "math.toRadians"],
    },
    "math.cos": {
        "summary": "The cosine of an angle in radians.",
        "params": [("angle", "number", "Radians.")],
        "returns": ("float", "The cosine."),
        "example": ["println(math.round(math.cos(0.0)));"],
        "see": ["math.sin"],
    },
    "math.tan": {
        "summary": "The tangent of an angle in radians.",
        "params": [("angle", "number", "Radians.")],
        "returns": ("float", "The tangent."),
        "example": ["println(math.round(math.tan(math.pi / 4)));"],
        "see": ["math.sin", "math.cos"],
    },
    "math.asin": {
        "summary": "The arc sine.",
        "params": [("value", "number", "-1..1.")],
        "returns": ("float", "The angle in radians; NaN outside the domain."),
        "example": ["println(math.asin(1.0));"],
        "see": ["math.atan", "math.acos"],
    },
    "math.acos": {
        "summary": "The arc cosine.",
        "params": [("value", "number", "-1..1.")],
        "returns": ("float", "The angle in radians."),
        "example": ["println(math.acos(1.0));"],
        "see": ["math.asin"],
    },
    "math.atan": {
        "summary": "The arc tangent.",
        "params": [("value", "number", "Any number.")],
        "returns": ("float", "The angle in radians, in -pi/2..pi/2."),
        "example": ["println(math.atan(1.0));"],
        "see": ["math.atan2"],
    },
    "math.atan2": {
        "summary": "The angle of a vector.",
        "params": [("y", "number", "The vertical component."), ("x", "number", "The horizontal component.")],
        "returns": ("float", "The angle in radians, in -pi..pi."),
        "remarks": "The argument order is `atan2(y, x)`, like the C function, so it is the "
                   "angle from the positive x axis to the point (x, y).",
        "example": ['println(math.atan2(1.0, 0.0) == math.pi / 2);'],
        "see": ["math.atan"],
    },
    "math.floor": {
        "summary": "Rounds down to an integer.",
        "params": [("value", "number", "The value.")],
        "returns": ("float", "The largest whole number not greater than the value, as a float: `-2.0`."),
        "example": ["println(math.floor(-1.5), math.floor(1.5));"],
        "see": ["math.ceil", "math.trunc"],
    },
    "math.ceil": {
        "summary": "Rounds up to an integer.",
        "params": [("value", "number", "The value.")],
        "returns": ("float", "The smallest whole number not less than the value, as a float: `2.0`."),
        "example": ["println(math.ceil(1.2));"],
        "see": ["math.floor"],
    },
    "math.trunc": {
        "summary": "Drops the fractional part.",
        "params": [("value", "number", "The value.")],
        "returns": ("float", "The value toward zero, as a float: `-1.0`."),
        "remarks": "`trunc(-1.5)` is `-1.0` where `floor(-1.5)` is `-2.0`.",
        "example": ["println(math.trunc(-1.5));"],
        "see": ["math.floor"],
    },
    "math.round": {
        "summary": "Rounds to the nearest integer, or to n digits.",
        "params": [("value", "number", "The value."),
                   ("digits", "int", "Optional; when given the result is a float.")],
        "returns": ("float", "The rounded value, as a float: `3.0`."),
        "remarks": "Halves round away from zero, so `round(2.5)` is `3.0` and `round(-2.5)` is "
                   "`-3.0`. Use `int()` on the result when an integer is wanted.",
        "example": ["println(math.round(2.5), math.round(2.567, 2));"],
        "see": ["math.floor", "float.round"],
    },
    "math.sign": {
        "summary": "The sign of a number.",
        "params": [("value", "number", "The value.")],
        "returns": ("int", "`-1`, `0` or `1`, even for a float argument."),
        "example": ["println(math.sign(-3), math.sign(0), math.sign(0.5));"],
        "see": ["math.abs"],
    },
    "math.clamp": {
        "summary": "Constrains a value to a range.",
        "params": [("value", "number", "The value."),
                   ("low", "number", "The lower bound."), ("high", "number", "The upper bound.")],
        "returns": ("number", "The value, or the nearer bound."),
        "errors": [("ArgumentError", "`low` is greater than `high`.")],
        "example": ["println(math.clamp(15, 0, 10), math.clamp(-1, 0, 10));"],
        "see": ["math.min", "math.max"],
    },
    "math.min": {
        "summary": "The smallest argument.",
        "params": [("values", "number", "One or more numbers.")],
        "returns": ("number", "The smallest."),
        "example": ["println(math.min(3.0, 1.0, 2.0));"],
        "see": ["math.max", "global.min"],
    },
    "math.max": {
        "summary": "The largest argument.",
        "params": [("values", "number", "One or more numbers.")],
        "returns": ("number", "The largest."),
        "example": ["println(math.max(3.0, 1.0, 2.0));"],
        "see": ["math.min", "global.max"],
    },
    "math.gcd": {
        "summary": "The greatest common divisor.",
        "params": [("a", "int", "A positive integer."), ("b", "int", "A positive integer.")],
        "returns": ("int", "The divisor."),
        "errors": [("ArgumentError", "Either argument is zero or negative.")],
        "example": ["println(math.gcd(12, 18));"],
        "see": ["math.lcm"],
    },
    "math.lcm": {
        "summary": "The least common multiple.",
        "params": [("a", "int", "A positive integer."), ("b", "int", "A positive integer.")],
        "returns": ("int", "The multiple."),
        "errors": [("ArgumentError", "Either argument is zero or negative.")],
        "example": ["println(math.lcm(4, 6));"],
        "see": ["math.gcd"],
    },
    "math.hypot": {
        "summary": "The length of a vector.",
        "params": [("x", "number", "The horizontal component."), ("y", "number", "The vertical component.")],
        "returns": ("float", "sqrt(x*x + y*y), computed without intermediate overflow."),
        "example": ["println(math.hypot(3.0, 4.0));"],
        "see": ["math.sqrt", "math.atan2"],
    },
    "math.toRadians": {
        "summary": "Degrees to radians.",
        "params": [("degrees", "number", "The angle in degrees.")],
        "returns": ("float", "The angle in radians."),
        "example": ["println(math.toRadians(180.0) == math.pi);"],
        "see": ["math.toDegrees"],
    },
    "math.toDegrees": {
        "summary": "Radians to degrees.",
        "params": [("radians", "number", "The angle in radians.")],
        "returns": ("float", "The angle in degrees."),
        "example": ["println(math.toDegrees(math.pi));"],
        "see": ["math.toRadians"],
    },
    "math.isNaN": {
        "summary": "True for NaN.",
        "params": [("value", "any", "The value.")],
        "returns": ("bool", "Whether it is a float that is not a number."),
        "example": ["println(math.isNaN(math.nan));"],
        "see": ["math.isInteger"],
    },
    "math.isInteger": {
        "summary": "True for whole numbers.",
        "params": [("value", "any", "The value.")],
        "returns": ("bool", "Whether it is an integer or a whole float."),
        "remarks": "`math.isInteger(2.0)` is true; `math.isInteger(2.5)` is false and so is "
                   "`math.isInteger(\"2\")`.",
        "example": ["println(math.isInteger(2.0), math.isInteger(2.5));"],
        "see": ["math.isNaN"],
    },

    # ------------------------------------------------------------------- os
    "os.args": {
        "summary": "The script's arguments.",
        "params": [],
        "returns": ("list of string", "Everything after the script path."),
        "example": ["println(os.args);"],
        "see": ["os.env"],
    },
    "os.cwd": {
        "summary": "The current working directory.",
        "params": [],
        "returns": ("string", "An absolute path."),
        "example": ["println(os.cwd());"],
        "see": ["os.joinPath"],
    },
    "os.platform": {
        "summary": "The host platform name.",
        "params": [],
        "returns": ("string", "`linux`, `macos` or `windows`."),
        "example": ["println(os.platform == \"linux\");"],
        "see": ["os.lineSeparator"],
    },
    "os.lineSeparator": {
        "summary": "The platform's line separator.",
        "params": [],
        "returns": ("string", "`\\n` everywhere except Windows, where it is `\\r\\n`."),
        "example": ["println(len(os.lineSeparator));"],
        "see": ["os.platform"],
    },
    "os.env": {
        "summary": "Reads an environment variable.",
        "params": [("name", "string", "The variable."),
                   ("fallback", "any", "Optional; returned when the variable is unset.")],
        "returns": ("string|any", "The value, or the fallback (`nil` when none was given)."),
        "example": ["println(os.env(\"TERM\", \"(none)\"));"],
        "see": ["os.setEnv"],
    },
    "os.setEnv": {
        "summary": "Sets an environment variable for this process.",
        "params": [("name", "string", "The variable."), ("value", "string", "The value; `\\\"\\\"` removes it.")],
        "returns": None,
        "errors": [("IOError", "The platform refuses the change.")],
        "remarks": "The change is visible to this process and to programs it starts, not to "
                   "the shell it was started from.",
        "example": ['os.setEnv("TERN_TEST", "1");', 'println(os.env("TERN_TEST"));'],
        "see": ["os.env"],
    },
    "os.joinPath": {
        "summary": "Joins path segments with the platform separator.",
        "params": [("segments", "string", "One or more segments.")],
        "returns": ("string", "The joined path."),
        "remarks": "No normalisation: `..` is kept and an absolute segment resets the path. "
                   "Uses `/` on POSIX and `\\` on Windows.",
        "example": ['println(os.joinPath("a", "b", "c.txt"));'],
        "see": ["fs.read"],
    },

    # --------------------------------------------------------------- random
    "random.seed": {
        "summary": "Seeds the generator.",
        "params": [("seed", "int", "The seed.")],
        "returns": None,
        "remarks": "A seeded run is reproducible byte for byte, including across the two back "
                   "ends only when the generator is the same — it is not, so treat the sequence "
                   "as reproducible per build, not across them.",
        "example": ["random.seed(7);", "println(random.int(0, 99));"],
        "see": ["random.int"],
    },
    "random.int": {
        "summary": "A random integer in an inclusive range.",
        "params": [("low", "int", "The lower bound, or the upper bound when it is the only argument."),
                   ("high", "int", "The upper bound, inclusive.")],
        "returns": ("int", "The value; bounds given in the wrong order are swapped."),
        "example": ["println(random.int(1, 6));"],
        "see": ["random.float", "random.choice"],
    },
    "random.float": {
        "summary": "A random float in a half-open range.",
        "params": [("low", "number", "Optional, 0.0 by default."),
                   ("high", "number", "Optional, 1.0 by default.")],
        "returns": ("float", "In `[low, high)`."),
        "example": ["println(random.float() < 1.0);"],
        "see": ["random.int"],
    },
    "random.bool": {
        "summary": "A random boolean.",
        "params": [],
        "returns": ("bool", "True or false, with equal probability."),
        "example": ["println(type(random.bool()) == \"bool\");"],
        "see": ["random.int"],
    },
    "random.choice": {
        "summary": "A random item from a list.",
        "params": [("list", "list", "A non-empty list.")],
        "returns": ("any", "The item; `nil` for an empty list."),
        "example": ['println(random.choice(["a", "b", "c"]));'],
        "see": ["random.shuffle"],
    },
    "random.shuffle": {
        "summary": "Shuffles a list in place.",
        "params": [("list", "list", "The list; changed in place.")],
        "returns": ("list", "The same list, for chaining."),
        "example": ["let deck = [1, 2, 3, 4];", "random.shuffle(deck);", "println(len(deck));"],
        "see": ["random.choice", "list.sort"],
    },

    # ----------------------------------------------------------------- text
    "text.format": {
        "summary": "Fills placeholders with arguments.",
        "params": [("template", "string", "Text with `{}` or `{0}` placeholders."),
                   ("values", "any", "The values to substitute.")],
        "returns": ("string", "The filled text."),
        "errors": [("ArgumentError", "A placeholder has no matching argument, or the index is out of range.")],
        "remarks": "`{}` consumes the next argument; `{0}`, `{1}` index explicitly and may "
                   "repeat. `{{` and `}}` write literal braces. Values are rendered with `str`.",
        "example": ['println(text.format("{} + {} = {}", 1, 2, 3));', 'println(text.format("{1} {0}", "a", "b"));'],
        "see": ["string.format", "global.str"],
    },
    "text.join": {
        "summary": "Joins a list with a separator.",
        "params": [("items", "list", "The items; each is rendered with `str`."),
                   ("separator", "string", "Optional, `,` by default.")],
        "returns": ("string", "The joined text."),
        "example": ['println(text.join([1, 2, 3], " - "));'],
        "see": ["list.join"],
    },
    "text.upper": {
        "summary": "An uppercased copy.",
        "params": [("text", "string", "The text.")],
        "returns": ("string", "The copy."),
        "example": ['println(text.upper("abc"));'],
        "see": ["text.lower", "string.upper"],
    },
    "text.lower": {
        "summary": "A lowercased copy.",
        "params": [("text", "string", "The text.")],
        "returns": ("string", "The copy."),
        "example": ['println(text.lower("ABC"));'],
        "see": ["text.upper"],
    },
    "text.trim": {
        "summary": "Text without surrounding whitespace.",
        "params": [("text", "string", "The text.")],
        "returns": ("string", "The trimmed copy."),
        "example": ['println(text.trim("  a  ") + "|");'],
        "see": ["string.trim"],
    },
    "text.split": {
        "summary": "Splits text on a separator.",
        "params": [("text", "string", "The text."), ("separator", "string", "The separator.")],
        "returns": ("list of string", "The pieces."),
        "example": ['println(text.split("a,b", ","));'],
        "see": ["string.split"],
    },
    "text.pad": {
        "summary": "Pads text to a width on the right.",
        "params": [("text", "string", "The text."), ("width", "int", "The target width."),
                   ("fill", "string", "Optional, a space by default.")],
        "returns": ("string", "The padded text; unchanged when already wide enough."),
        "example": ['println(text.pad("ab", 5, ".") + "|");'],
        "see": ["string.padStart", "string.padEnd"],
    },

    "text.toBytes": {
        "summary": "Encodes text as UTF-8 bytes.",
        "params": [("text", "string", "The text to encode.")],
        "returns": ("list of int", "The UTF-8 bytes, as integers 0..255."),
        "errors": [("TypeError", "The argument is not a string.")],
        "remarks": "The inverse of `text.fromBytes`. Everything the runtime reads and writes - "
                   "files, sockets, `net.sendBytes` - is bytes, so this is how text crosses into "
                   "a byte-level protocol.",
        "example": ['println(text.toBytes("héllo"));'],
        "see": ["text.fromBytes", "net.sendBytes"],
    },
    "text.fromBytes": {
        "summary": "Decodes a list of bytes as UTF-8 text.",
        "params": [("bytes", "list of int", "The bytes, as integers 0..255."),
                   ("offset", "int", "Optional, where to start; 0 by default."),
                   ("length", "int", "Optional, how many bytes; the rest of the list by default.")],
        "returns": ("string", "The decoded text."),
        "errors": [("TypeError", "The first argument is not a list, or an item is not an integer."),
                   ("ArgumentError", "The offset and length fall outside the list.")],
        "remarks": "Invalid UTF-8 decodes to U+FFFD rather than raising: a sequence split across "
                   "two reads should give a replacement character, not kill the reader. The "
                   "offset and length let a caller decode one field out of a packet without "
                   "copying the bytes first.",
        "example": ['println(text.fromBytes([104, 195, 169, 108, 108, 111]));'],
        "see": ["text.toBytes", "net.receiveSome"],
    },

    # ----------------------------------------------------------------- time
    "time.now": {
        "summary": "Seconds since the Unix epoch.",
        "params": [],
        "returns": ("float", "The time, with sub-second precision."),
        "example": ["println(time.now() > 1600000000.0);"],
        "see": ["time.millis", "time.iso"],
    },
    "time.millis": {
        "summary": "Milliseconds since the Unix epoch.",
        "params": [],
        "returns": ("int", "The count."),
        "remarks": "The right clock for measuring an interval: subtract two readings.",
        "example": ["let started = time.millis();", "sleep(10);", "println(time.millis() > started);"],
        "see": ["time.now"],
    },
    "time.iso": {
        "summary": "A time as an ISO 8601 string.",
        "params": [("epoch", "number", "Optional; the current time by default.")],
        "returns": ("string", "`YYYY-MM-DDTHH:MM:SSZ`, in UTC."),
        "example": ['println(time.iso(0));'],
        "see": ["time.now"],
    },
    "time.sleep": {
        "summary": "Suspends the script for a number of seconds.",
        "params": [("seconds", "number", "How long; fractions are allowed.")],
        "returns": None,
        "errors": [("ArgumentError", "The value is negative.")],
        "example": ["time.sleep(0.01);"],
        "see": ["global.sleep"],
    },
}
