# http.encodeRequest

Renders a request as the bytes that would be sent.

## Syntax

```tern
http.encodeRequest(method, url, options?)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `method` | `string` | The method. |
| `url` | `string` | The URL. |
| `options` | `map` | As for `http.get`. |

## Return value

- Type: `list of int`
- The request bytes, ready for `net.sendBytes` or `http.exchange`.

## Remarks

Pure: nothing is sent and no socket is opened, which is what makes the header rules testable. The `Host` header carries a port only when it is not the scheme's default. `encodeRequest` returns bytes, not text, because a body may be arbitrary bytes.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | The URL scheme is not supported. |

## Example

```tern
println(http.encodeRequest("GET", "https://example.com/a"));
```

## See also

- [http.decodeResponse](decodeResponse.md)
- [http.exchange](exchange.md)

---

Part of the [`http`](index.md) namespace.
