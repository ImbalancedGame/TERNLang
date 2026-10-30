# time.now

Seconds since the Unix epoch.

## Syntax

```tern
time.now()
```

## Parameters

_None._

## Return value

- Type: `float`
- The time, with sub-second precision.

## Example

```tern
println(time.now() > 1600000000.0);
```

## See also

- [time.millis](millis.md)
- [time.iso](iso.md)

---

Part of the [`time`](index.md) namespace.
