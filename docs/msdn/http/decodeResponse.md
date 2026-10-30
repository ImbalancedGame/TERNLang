# http.decodeResponse

Parses a response from bytes.

## Syntax

```tern
http.decodeResponse(bytes)
```

## Parameters

| Parameter | Type | Description |
| --- | --- | --- |
| `bytes` | `list of int` | A complete response: headers and body as bytes. |

## Return value

- Type: `map`
- The response, as for `http.get`.

## Remarks

Handles chunked bodies and `Content-Length`; the reason phrase may be empty.

## Errors

| Error | Raised when |
| --- | --- |
| `HttpError` | The text is not a well-formed response. |

## Example

```tern
let bytes = [];
for (c in "HTTP/1.1 204 No Content".chars()) { bytes.push(ord(c)); }
bytes.push(13); bytes.push(10); bytes.push(13); bytes.push(10);
println(http.decodeResponse(bytes)["status"]);
```

## See also

- [http.encodeRequest](encodeRequest.md)

---

Part of the [`http`](index.md) namespace.
