# input

Reads one line from standard input.

## Syntax

```tern
input(prompt?)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `prompt` | `string` | Optional text written before reading, with no newline. |

## Return value

- Type: `string`
- The line, without its terminator; `nil` at end of input.

## Remarks

End of input returns `nil` rather than an empty string, so a loop can tell a blank line from a closed pipe.

## Example

```tern
let name = input("name? ");
if (name != nil) { println("hello", name); }
```

## See also

- [print](print.md)

---

Part of the [`global`](index.md) namespace.
