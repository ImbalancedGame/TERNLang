# net.udpListen

Opens a datagram socket.

## Syntax

```tern
net.udpListen(port, host?, options?)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `port` | `int` | The port to bind; `0` asks for a free one. |
| `host` | `string` | In the three-argument form, the address to bind. |
| `options` | `map` | Optional; none are read by datagram sockets. |

## Return value

- Type: `int`
- A datagram handle.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | The port is out of range. |
| `NetError` | The socket cannot be opened. |

## Example

```tern
let socket = net.udpListen(0);
```

## See also

- [net.udpSend](udpSend.md)
- [net.udpReceive](udpReceive.md)

---

Part of the [`net`](index.md) namespace.
