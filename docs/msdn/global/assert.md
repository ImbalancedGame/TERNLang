# assert

Raises unless the condition is truthy.

## Syntax

```tern
assert(condition, message?)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `condition` | `any` | The condition. |
| `message` | `string` | Optional text for the failure. |

## Return value

`nil`.

## Remarks

Always evaluated: an `assert` is not compiled out, and it is caught by `catch` like any other error, which is what makes it usable in tests. The value a `catch` binds is the message string, not an object with fields.

## Errors

| Error | Raised when |
| --- | --- |
| `AssertionError` | The condition is falsy; the message is the error's text. |

## Example

```tern
assert(1 + 1 == 2, "arithmetic");
```

## See also

- [error](error.md)
- [panic](panic.md)

---

Part of the [`global`](index.md) namespace.
