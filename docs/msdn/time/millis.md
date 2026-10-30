# time.millis

Milliseconds since the Unix epoch.

## Syntax

```tern
time.millis()
```

## Parameters

_None._

## Return value

- Type: `int`
- The count.

## Remarks

The right clock for measuring an interval: subtract two readings.

## Example

```tern
let started = time.millis();
sleep(10);
println(time.millis() > started);
```

## See also

- [time.now](now.md)

---

Part of the [`time`](index.md) namespace.
