# time.sleep

Suspends the script for a number of seconds.

## Syntax

```tern
time.sleep(seconds)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `seconds` | `number` | How long; fractions are allowed. |

## Return value

`nil`.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | The value is negative. |

## Example

```tern
time.sleep(0.01);
```

## See also

- [global.sleep](../global/sleep.md)

---

Part of the [`time`](index.md) namespace.
