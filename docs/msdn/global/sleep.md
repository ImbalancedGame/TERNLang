# sleep

Blocks for a number of milliseconds.

## Syntax

```tern
sleep(milliseconds)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `milliseconds` | `int` | How long; a float is rejected, so round it first. |

## Return value

`nil`.

## Errors

| Error | Raised when |
| --- | --- |
| `TypeError` | The value is not an integer. |
| `ArgumentError` | The value is negative. |

## Example

```tern
sleep(250);
```

## See also

- [time.sleep](../time/sleep.md)

---

Part of the [`global`](index.md) namespace.
