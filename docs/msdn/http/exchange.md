# http.exchange

Sends a request over a socket the caller already owns.

## Syntax

```tern
http.exchange(connection, method, url, options?)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `connection` | `int` | A connection from `net.connect`, already in the right state. |
| `method` | `string` | The HTTP method. |
| `url` | `string` | The URL. |
| `options` | `map` | Optional; as for `http.get`, including `body`. |

## Return value

- Type: `map`
- The response.

## Remarks

This is how a script talks HTTP over a tunnel, a proxy, or a TLS connection it set up itself.

## Errors

| Error | Raised when |
| --- | --- |
| `HttpError` | The response is incomplete. |
| `NetError` | The socket fails. |

## Example

```tern
let reply = http.exchange(connection, "GET", "http://example.com/");
```

## See also

- [http.encodeRequest](encodeRequest.md)
- [net.connect](../net/connect.md)

---

Part of the [`http`](index.md) namespace.
