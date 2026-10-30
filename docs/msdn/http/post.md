# http.post

Performs a POST with a body.

## Syntax

```tern
http.post(url, body, options?)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `url` | `string` | The URL. |
| `body` | `string` | The request body. |
| `options` | `map` | As for `http.get`. |

## Return value

- Type: `map`
- The response, as for `http.get`.

## Errors

| Error | Raised when |
| --- | --- |
| `HttpError` | The exchange fails. |

## Example

```tern
let sent = http.post("https://example.com/api", "{\"ok\": true}", {"headers": {"content-type": "application/json"}});
```

## See also

- [http.get](get.md)
- [http.request](request.md)

---

Part of the [`http`](index.md) namespace.
