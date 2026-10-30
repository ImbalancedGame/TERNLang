# net.maxFrame

Reads, or changes, the largest frame a connection accepts.

## Syntax

```tern
net.maxFrame(connection, bytes)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `connection` | `int` | A connection handle from `net.connect` or `net.accept`. |
| `bytes` | `int` | The new ceiling, 256..67108864. |

## Return value

- Type: `int`
- The ceiling now in force.

## Remarks

A connection inherits its server's ceiling. Raising it lets a peer send larger frames; the receiver still checks the header before allocating.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | The value is out of range. |

## Example

```tern
println(net.maxFrame(connection, 65536));
```

## See also

- [net.listen](listen.md)
- [net.receive](receive.md)

---

Part of the [`net`](index.md) namespace.
