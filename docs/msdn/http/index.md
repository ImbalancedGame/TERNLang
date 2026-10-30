# http

An HTTP/1.1 client, including https.

`http` speaks HTTP/1.1 on a socket it opens itself; there is no library behind
it. `https://` URLs are wrapped in TLS with the same transport `net.connect`
uses, so the certificate is checked against the system store unless `ca` names a
PEM bundle or `verify` is false.

A response is a map with `status`, `reason`, `version`, `headers` (lower-cased
keys) and `body`.

## Functions

| Function | Arguments | Description |
| --- | --- | --- |
| [`decodeResponse`](decodeResponse.md) | 1..1 | Parses a response from bytes. |
| [`encodeRequest`](encodeRequest.md) | 2..3 | Renders a request as the bytes that would be sent. |
| [`exchange`](exchange.md) | 3..4 | Sends a request over a socket the caller already owns. |
| [`get`](get.md) | 1..2 | Performs a GET and returns the response. |
| [`post`](post.md) | 2..3 | Performs a POST with a body. |
| [`request`](request.md) | 2..3 | Performs a request with any method. |

## Example

```tern
import "std:http";

let response = http.get("https://example.com/", {"timeout": 5000});
println(response["status"], len(response["body"]));
```

