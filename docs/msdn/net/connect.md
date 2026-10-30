# net.connect

Connects to a listening socket and returns a handle.

## Syntax

```tern
net.connect(host, port, options?)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `host` | `string` | A host name or address. |
| `port` | `int` | The port. |
| `options` | `map` | Optional: `token`, `maxFrame`, `timeout`, and for TLS `tls`, `sni`, `verify`, `ca`. |

## Return value

- Type: `int`
- A connection handle.

## Remarks

`timeout` is milliseconds and covers the connect and the hello exchange. `tls: true` wraps the socket before anything is sent; `sni` defaults to the host name and is what the certificate is checked against.

## Errors

| Error | Raised when |
| --- | --- |
| `TypeError` | `host` is not a string or `port` is not an integer. |
| `ArgumentError` | An option is out of range. |
| `NetError` | The address cannot be resolved or the connection is refused. |
| `AuthenticationError` | The listener rejected the token. |
| `TlsError` | The TLS handshake failed. |

## Example

```tern
let connection = net.connect("example.com", 443, {"tls": true, "timeout": 5000});
```

## See also

- [net.listen](listen.md)
- [net.send](send.md)
- [net.receive](receive.md)

---

Part of the [`net`](index.md) namespace.
