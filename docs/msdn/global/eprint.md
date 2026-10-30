# eprint

Writes its arguments to standard error.

## Syntax

```tern
eprint(values...)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `values` | `any` | Any number, including none. |

## Return value

`nil`.

## Remarks

Diagnostics belong here: standard output stays usable for data even when the script is being piped.

## Example

```tern
eprint("warning: nothing to do");
```

## See also

- [print](print.md)
- [println](println.md)

---

Part of the [`global`](index.md) namespace.
