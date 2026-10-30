# print

Writes its arguments to standard output, separated by a space.

## Syntax

```tern
print(values...)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `values` | `any` | Any number, including none. |

## Return value

`nil`.

## Remarks

Writes without a trailing newline; `println` adds one. Each value is rendered the way `str` renders it, so strings appear unquoted.

## Example

```tern
print("a", 1, true);
println("");
```

## See also

- [println](println.md)
- [eprint](eprint.md)
- [str](str.md)
- [repr](repr.md)

---

Part of the [`global`](index.md) namespace.
