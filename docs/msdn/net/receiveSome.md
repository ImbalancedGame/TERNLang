# net.receiveSome

Reads up to n bytes, or returns an empty list at end of stream.

## Syntax

```tern
net.receiveSome(connection, count)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `connection` | `int` | A connection handle from `net.connect` or `net.accept`. |
| `count` | `int` | The most bytes to take, 1..67108864. |

## Return value

- Type: `list of int`
- What arrived: 1..count bytes, or an empty list at end of stream.

## Remarks

The difference from `net.receiveBytes` is that a short read is normal: it returns as soon as any bytes arrive, so it suits a protocol loop that reads until the peer closes. An empty list means end of stream, not a timeout - a timeout raises `NetError`, which is why a caller wanting to distinguish the two should set `net.timeout` first. Reads do not cross frame headers; use this only on a connection opened or accepted with the `raw` option.

## Errors

| Error | Raised when |
| --- | --- |
| `NetError` | `count` is out of range, the read times out, or the socket is closed. |
| `ConnectionError` | The other side reset the connection. |

## Example

```tern
while true {
    let chunk = net.receiveSome(connection, 4096);
    if len(chunk) == 0 { break; }
    println(len(chunk));
}
```

## See also

- [net.receiveBytes](receiveBytes.md)
- [net.sendBytes](sendBytes.md)
- [net.timeout](timeout.md)

---

Part of the [`net`](index.md) namespace.
