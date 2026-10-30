# net.udpSend

Sends one datagram.

## Syntax

```tern
net.udpSend(socket, host, port, bytes)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `socket` | `int` | A datagram handle. |
| `host` | `string` | Where to send it. |
| `port` | `int` | The destination port. |
| `bytes` | `list of int` | The payload, at most 65507 bytes. |

## Return value

`nil`.

## Remarks

Datagrams are not framed and not acknowledged: the payload is exactly the bytes given.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | The payload is larger than 65507 bytes. |
| `NetError` | The send fails. |

## Example

```tern
net.udpSend(socket, "127.0.0.1", port, [1, 2, 3]);
```

## See also

- [net.udpReceive](udpReceive.md)

---

Part of the [`net`](index.md) namespace.
