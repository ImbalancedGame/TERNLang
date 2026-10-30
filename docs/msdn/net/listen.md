# net.listen

Opens a listening socket and returns a server handle.

## Syntax

```tern
net.listen(port, host?, options?)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `port` | `int` | The port to bind; `0` asks the system for a free one. `net.port` reports it. |
| `host` | `string` | In the three-argument form, the address to bind. |
| `options` | `map` | Optional: `token`, `maxFrame`, `timeout`. |

## Return value

- Type: `int`
- A server handle for `net.accept`.

## Remarks

Three forms are accepted: `listen(port)`, `listen(port, options)` and `listen(host, port, options)`; a numeric second argument selects the last. `token` makes every accepted peer prove it knows the string before the connection exists. `maxFrame` caps the frame a peer may claim (default 1 MiB) and is checked before the payload is read, so a hostile header cannot make this side allocate.

## Errors

| Error | Raised when |
| --- | --- |
| `TypeError` | An option has the wrong type. |
| `ArgumentError` | `maxFrame` is outside 256..67108864, `timeout` is negative, or `token` is empty. |
| `NetError` | The address cannot be bound. |

## Example

```tern
let server = net.listen(0, {"token": "s3cret", "maxFrame": 4096});
println(net.port(server) > 0);
```

## See also

- [net.accept](accept.md)
- [net.connect](connect.md)
- [net.maxFrame](maxFrame.md)

---

Part of the [`net`](index.md) namespace.
