# TERN standard library reference

Every function, method and constant the language exposes, one page each: syntax, parameters, return value, remarks, errors, an example and the neighbouring calls.

The pages are generated from the engine's own catalogue (`tools/tern docs --json`) and the prose in `tools/reference/`. `bash tools/gen-docs.sh` rewrites them.

## Namespaces

| Namespace | Description |
| --- | --- |
| [`d3d12`](d3d12/index.md) | Direct3D 12: device, render target, pipeline and draw. |
| [`fs`](fs/index.md) | Files, directories and reading a file in pieces. |
| [`gl`](gl/index.md) | OpenGL, loaded at run time so nothing is linked. |
| [`global`](global/index.md) | The functions that need no import. |
| [`http`](http/index.md) | An HTTP/1.1 client, including https. |
| [`json`](json/index.md) | Reading and writing JSON. |
| [`lists`](lists/index.md) | Whole-list helpers that do not belong on a receiver. |
| [`math`](math/index.md) | Trigonometry, rounding, and the constants. |
| [`net`](net/index.md) | Sockets, framing and datagrams. |
| [`os`](os/index.md) | Arguments, environment, paths and the platform. |
| [`random`](random/index.md) | A seeded generator, for games and for tests. |
| [`rcon`](rcon/index.md) | The Source RCON protocol for game servers. |
| [`sftp`](sftp/index.md) | File transfer over SSH, by driving the OpenSSH client. |
| [`text`](text/index.md) | Text helpers that read better as functions. |
| [`time`](time/index.md) | The clock, and sleeping. |

## Methods on built-in types

| Type | Methods |
| --- | --- |
| [`float`](float/index.md) | Float methods |
| [`int`](int/index.md) | Integer methods |
| [`list`](list/index.md) | List methods |
| [`map`](map/index.md) | Map methods |
| [`range`](range/index.md) | Range methods |
| [`string`](string/index.md) | String methods |

## Importing

A namespace is available once it is imported, except the globals, which are always there:

```tern
import "std:json";

println(json.parse("[1]")[0]);
```

The import name is `std:` followed by the namespace. The compiled back end embeds the same table, so a script that imports `std:gl` on Windows compiles and reports the module as unavailable rather than failing to build.

## Errors

Every page has an **Errors** table naming the kind of failure and when it happens. The kind is what an uncaught error prints before its message (`NetError: connection refused`). A `catch` binds the message string itself, so the kind is documentation and diagnostics rather than something to test for:

```tern
try {
    net.connect("127.0.0.1", 1, {"timeout": 10});
} catch (e) {
    println("failed:", e);
}
```

The same kinds are raised by both back ends; the interpreter and the compiled binary report the same message for the same failure, which `tools/compare.sh` checks.

