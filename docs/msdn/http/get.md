# http.get

Performs a GET and returns the response.

## Syntax

```tern
http.get(url, options?)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `url` | `string` | An `http://` or `https://` URL. |
| `options` | `map` | Optional: `headers`, `timeout`, `redirects`, `maxBytes`, `verify`, `ca`. |

## Return value

- Type: `map`
- `status`, `reason`, `version`, `headers`, `body`.

## Errors

| Error | Raised when |
| --- | --- |
| `ArgumentError` | The scheme is not http or https. |
| `HttpError` | The response is malformed, too large, or the exchange times out. |
| `NetError` | The connection fails. |

## Example

```tern
let page = http.get("https://example.com/");
println(page["status"]);
```

## See also

- [http.post](post.md)
- [http.request](request.md)
- [http.exchange](exchange.md)

---

Part of the [`http`](index.md) namespace.
