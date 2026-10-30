# net.udpReceive

Waits for one datagram.

## Syntax

```tern
net.udpReceive(socket, maxBytes?)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `socket` | `int` | A datagram handle. |
| `maxBytes` | `int` | Optional ceiling; 65507 by default. |

## Return value

- Type: `map`
- `{"from": "host:port", "data": [bytes]}`.

## Remarks

A timeout set with `net.timeout` returns `nil` rather than raising, which is what makes a receive loop possible; anything longer than `maxBytes` is truncated.

## Errors

| Error | Raised when |
| --- | --- |
| `NetError` | The socket fails. |

## Example

```tern
let packet = net.udpReceive(socket);
if (packet != nil) { println(packet["from"]); }
```

## See also

- [net.udpSend](udpSend.md)
- [net.udpListen](udpListen.md)

---

Part of the [`net`](index.md) namespace.
