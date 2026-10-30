# http.request

Performs a request with any method.

## Syntax

```tern
http.request(method, url, options?)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `method` | `string` | The HTTP method. |
| `url` | `string` | The URL. |
| `options` | `map` | As for `http.get`, plus `body`. |

## Return value

- Type: `map`
- The response.

## Errors

| Error | Raised when |
| --- | --- |
| `HttpError` | The exchange fails. |

## Example

```tern
let removed = http.request("DELETE", "https://example.com/item/1");
```

## See also

- [http.get](get.md)
- [http.post](post.md)

---

Part of the [`http`](index.md) namespace.
