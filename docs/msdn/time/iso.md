# time.iso

A time as an ISO 8601 string.

## Syntax

```tern
time.iso(epoch)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `epoch` | `number` | Optional; the current time by default. |

## Return value

- Type: `string`
- `YYYY-MM-DDTHH:MM:SSZ`, in UTC.

## Example

```tern
println(time.iso(0));
```

## See also

- [time.now](now.md)

---

Part of the [`time`](index.md) namespace.
