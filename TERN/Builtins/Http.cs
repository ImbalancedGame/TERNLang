using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using TERN.Compiler;
using TERN.Runtime;

namespace TERN.Builtins
{
    /// <summary>
    /// <c>http</c>: a small HTTP/1.1 client. It speaks the protocol directly on a
    /// socket — request line, headers, then a body framed by Content-Length,
    /// chunked encoding or end of stream — so a script can talk to any web server
    /// without depending on a library that is not there.
    ///
    /// The pieces are exposed separately (<c>encodeRequest</c>, <c>decodeResponse</c>,
    /// <c>exchange</c>) so a test, or a script with its own connection handling,
    /// can drive one half at a time; <c>request</c> glues them together and follows
    /// redirects.
    /// </summary>
    internal static class HttpModule
    {
        private const int DefaultTimeout = 10000;
        private const int DefaultRedirects = 5;
        private const int DefaultMaxBytes = 8 * 1024 * 1024;
        private const int MaxHeaderBytes = 64 * 1024;

        public static void Install(TernEngine engine)
        {
            var map = new TernMap();

            Add(map, "request", 2, 3, (vm, a) =>
                Request(vm, engine, NetModule.Text(vm, a[0], "method"), NetModule.Text(vm, a[1], "url"),
                        a.Length > 2 ? a[2] : Value.Nil),
                "Sends one HTTP request and returns the response as a map.");

            Add(map, "get", 1, 2, (vm, a) =>
                Request(vm, engine, "GET", NetModule.Text(vm, a[0], "url"), a.Length > 1 ? a[1] : Value.Nil),
                "GET a URL: http.get(url[, options]).");

            Add(map, "post", 2, 3, (vm, a) =>
            {
                var body = NetModule.Text(vm, a[1], "body");
                var options = ParseOptions(vm, a.Length > 2 ? a[2] : Value.Nil, "post");
                options.Body ??= Encoding.UTF8.GetBytes(body);
                return Request(vm, engine, "POST", NetModule.Text(vm, a[0], "url"), options);
            }, "POST a string to a URL: http.post(url, body[, options]).");

            Add(map, "encodeRequest", 2, 3, (vm, a) => Value.Of(Wire.ToList(
                    EncodeRequest(vm, NetModule.Text(vm, a[0], "method"), NetModule.Text(vm, a[1], "url"),
                                  ParseOptions(vm, a.Length > 2 ? a[2] : Value.Nil, "encodeRequest")))),
                "Builds the bytes of a request, so a script can send them itself.");

            Add(map, "decodeResponse", 1, 1, (vm, a) =>
                Value.Of(Parse(vm, Wire.FromList(vm, a[0]), DefaultMaxBytes)),
                "Parses a complete response from bytes.");

            Add(map, "exchange", 3, 4, (vm, a) =>
            {
                var endpoint = engine.Net.Require(vm, NetModule.Handle(vm, a[0]));
                if (endpoint.Kind != NetKind.Stream)
                {
                    throw vm.Error("HttpError", "exchange() needs a connection handle from net.connect()");
                }

                var options = ParseOptions(vm, a.Length > 3 ? a[3] : Value.Nil, "exchange");
                var request = EncodeRequest(vm, NetModule.Text(vm, a[1], "method"), NetModule.Text(vm, a[2], "url"), options);
                WriteAll(vm, endpoint.Stream, request);
                return Value.Of(ReadResponse(vm, endpoint.Stream, options.MaxBytes));
            }, "Writes a request on a net handle and reads one response from it.");

            engine.RegisterValue("http", Value.Of(map));
            engine.RegisterStdModule(new StdModule
            {
                Name = "std:http",
                Members = map,
                FlatNames = Array.Empty<string>(),
            });
        }

        private static void Add(TernMap map, string name, int min, int max, NativeFn function, string documentation) =>
            map.Set(Value.Str(name), Value.Of(new TernNative(name, function, min, max, documentation)));

        // ---------------------------------------------------------------- options

        private sealed class Options
        {
            public List<KeyValuePair<string, string>> Headers { get; } = new();
            public byte[]? Body { get; set; }
            public int Timeout { get; set; } = DefaultTimeout;
            public int Redirects { get; set; } = DefaultRedirects;
            public int MaxBytes { get; set; } = DefaultMaxBytes;
        }

        private static Options ParseOptions(VM vm, in Value value, string operation)
        {
            var options = new Options();
            if (value.Kind == ValueKind.Nil)
            {
                return options;
            }

            if (value.Kind != ValueKind.Map)
            {
                throw vm.Error("TypeError", $"{operation}() options must be a map, not {vm.TypeName(value)}");
            }

            var map = value.AsMap();
            if (map.TryGet(Value.Str("headers"), out var headers))
            {
                if (headers.Kind != ValueKind.Map)
                {
                    throw vm.Error("TypeError", $"option 'headers' must be a map, not {vm.TypeName(headers)}");
                }

                var headerKeys = headers.AsMap().Keys();
                for (var i = 0; i < headerKeys.Count; i++)
                {
                    var key = headerKeys[i];
                    var header = headers.AsMap().GetOrNil(key);
                    if (key.Kind != ValueKind.Str || header.Kind != ValueKind.Str)
                    {
                        throw vm.Error("TypeError", "header names and values must be strings");
                    }

                    var name = key.AsString();
                    var text = header.AsString();
                    if (!IsToken(name))
                    {
                        throw vm.Error("HttpError", $"header name '{name}' is not valid");
                    }

                    if (text.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                    {
                        throw vm.Error("HttpError", $"header '{name}' contains a line break");
                    }

                    options.Headers.Add(new KeyValuePair<string, string>(name, text));
                }
            }

            if (map.TryGet(Value.Str("body"), out var body))
            {
                options.Body = Bytes(vm, body, "body");
            }

            if (map.TryGet(Value.Str("timeout"), out var timeout))
            {
                var milliseconds = NetModule.Number(vm, timeout, "timeout");
                if (milliseconds < 0)
                {
                    throw vm.Error("ArgumentError", "option 'timeout' must not be negative");
                }

                options.Timeout = (int)milliseconds;
            }

            if (map.TryGet(Value.Str("redirects"), out var redirects))
            {
                var count = NetModule.Number(vm, redirects, "redirects");
                if (count < 0 || count > 20)
                {
                    throw vm.Error("ArgumentError", "option 'redirects' must be between 0 and 20");
                }

                options.Redirects = (int)count;
            }

            if (map.TryGet(Value.Str("maxBytes"), out var maxBytes))
            {
                var limit = NetModule.Number(vm, maxBytes, "maxBytes");
                if (limit < 0 || limit > 64 * 1024 * 1024)
                {
                    throw vm.Error("ArgumentError", "option 'maxBytes' must be between 0 and 67108864");
                }

                options.MaxBytes = (int)limit;
            }

            return options;
        }

        /// <summary>A header name is an HTTP token: printable, no spaces, no colons.</summary>
        private static bool IsToken(string name)
        {
            if (name.Length == 0)
            {
                return false;
            }

            foreach (var c in name)
            {
                if (c <= ' ' || c >= 127 || c == ':')
                {
                    return false;
                }
            }

            return true;
        }

        private static byte[] Bytes(VM vm, in Value value, string what)
        {
            if (value.Kind == ValueKind.Str)
            {
                return Encoding.UTF8.GetBytes(value.AsString());
            }

            if (value.Kind == ValueKind.List)
            {
                return Wire.FromList(vm, value);
            }

            throw vm.Error("TypeError", $"{what} must be a string or a list of bytes, not {vm.TypeName(value)}");
        }

        // -------------------------------------------------------------------- URLs

        private readonly struct Url
        {
            public Url(string host, int port, string path)
            {
                Host = host;
                Port = port;
                Path = path;
            }

            public string Host { get; }
            public int Port { get; }
            public string Path { get; }

            public string Authority => Port == 80 ? Host : $"{Host}:{Port}";
        }

        private static Url ParseUrl(VM vm, string url)
        {
            const string scheme = "http://";
            if (!url.StartsWith(scheme, StringComparison.Ordinal))
            {
                throw vm.Error("HttpError", $"only http:// URLs are supported, not '{url}'");
            }

            var rest = url.Substring(scheme.Length);
            var slash = rest.IndexOfAny(new[] { '/', '?' });
            var authority = slash < 0 ? rest : rest.Substring(0, slash);
            var path = slash < 0 ? "/" : rest.Substring(slash);
            if (path.StartsWith('?'))
            {
                path = "/" + path;
            }

            if (authority.Length == 0)
            {
                throw vm.Error("HttpError", $"URL has no host: '{url}'");
            }

            var colon = authority.LastIndexOf(':');
            if (colon < 0)
            {
                return new Url(authority, 80, path);
            }

            var host = authority.Substring(0, colon);
            var portText = authority.Substring(colon + 1);
            if (host.Length == 0)
            {
                throw vm.Error("HttpError", $"URL has no host: '{url}'");
            }

            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
                port < 1 || port > 65535)
            {
                throw vm.Error("HttpError", $"URL has a bad port: '{url}'");
            }

            return new Url(host, port, path);
        }

        // ------------------------------------------------------------- the request

        private static bool HasHeader(Options options, string name)
        {
            foreach (var header in options.Headers)
            {
                if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static byte[] EncodeRequest(VM vm, string method, string url, Options options)
        {
            if (method.Length == 0 || method.IndexOfAny(new[] { ' ', '\r', '\n' }) >= 0)
            {
                throw vm.Error("HttpError", $"method '{method}' is not valid");
            }

            var target = ParseUrl(vm, url);
            var text = new StringBuilder();
            text.Append(method).Append(' ').Append(target.Path).Append(" HTTP/1.1\r\n");

            if (!HasHeader(options, "Host"))
            {
                text.Append("Host: ").Append(target.Authority).Append("\r\n");
            }

            if (!HasHeader(options, "Connection"))
            {
                text.Append("Connection: close\r\n");
            }

            if (!HasHeader(options, "User-Agent"))
            {
                text.Append("User-Agent: TERN\r\n");
            }

            foreach (var header in options.Headers)
            {
                text.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            }

            if (options.Body is not null && !HasHeader(options, "Content-Length"))
            {
                text.Append("Content-Length: ").Append(options.Body.Length).Append("\r\n");
            }

            text.Append("\r\n");

            var head = Encoding.UTF8.GetBytes(text.ToString());
            if (options.Body is null)
            {
                return head;
            }

            var request = new byte[head.Length + options.Body.Length];
            head.CopyTo(request, 0);
            options.Body.CopyTo(request, head.Length);
            return request;
        }

        /// <summary>Writes every byte of a request, or raises.</summary>
        private static void WriteAll(VM vm, Stream stream, byte[] bytes)
        {
            try
            {
                stream.Write(bytes);
                stream.Flush();
            }
            catch (IOException error) when (InnerSocket(error) is { SocketErrorCode: SocketError.TimedOut })
            {
                throw vm.Error("HttpError", "the request timed out");
            }
            catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException)
            {
                throw vm.Error("ConnectionError", "connection closed while sending the request");
            }
        }

        private static SocketException? InnerSocket(IOException error) =>
            error.InnerException as SocketException;

        // ------------------------------------------------------------ the response

        /// <summary>Reads a response from a socket, honouring the framing headers.</summary>
        private static TernMap ReadResponse(VM vm, Stream stream, int maxBytes)
        {
            var reader = new Source(vm, stream, maxBytes);
            return Read(vm, reader, maxBytes);
        }

        private static TernMap Parse(VM vm, byte[] data, int maxBytes)
        {
            var reader = new Source(vm, data, maxBytes);
            return Read(vm, reader, maxBytes);
        }

        private static TernMap Read(VM vm, Source source, int maxBytes)
        {
            var statusLine = source.ReadLine("status line");
            if (!statusLine.StartsWith("HTTP/", StringComparison.Ordinal))
            {
                throw vm.Error("HttpError", "the response does not start with an HTTP status line");
            }

            var firstSpace = statusLine.IndexOf(' ');
            var secondSpace = firstSpace < 0 ? -1 : statusLine.IndexOf(' ', firstSpace + 1);
            if (firstSpace < 0)
            {
                throw vm.Error("HttpError", $"malformed status line: '{statusLine}'");
            }

            var version = statusLine.Substring(0, firstSpace);
            var codeText = secondSpace < 0
                ? statusLine.Substring(firstSpace + 1)
                : statusLine.Substring(firstSpace + 1, secondSpace - firstSpace - 1);
            var reason = secondSpace < 0 ? string.Empty : statusLine.Substring(secondSpace + 1);

            if (codeText.Length != 3 || !int.TryParse(codeText, NumberStyles.None, CultureInfo.InvariantCulture, out var status))
            {
                throw vm.Error("HttpError", $"malformed status line: '{statusLine}'");
            }

            var headers = new TernMap();
            var order = new List<string>();
            while (true)
            {
                var line = source.ReadLine("headers");
                if (line.Length == 0)
                {
                    break;
                }

                var colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    throw vm.Error("HttpError", $"malformed header line: '{line}'");
                }

                var name = line.Substring(0, colon).Trim().ToLowerInvariant();
                var value = line.Substring(colon + 1).Trim();
                if (headers.TryGet(Value.Str(name), out var existing))
                {
                    headers.Set(Value.Str(name), Value.Str(existing.AsString() + ", " + value));
                }
                else
                {
                    order.Add(name);
                    headers.Set(Value.Str(name), Value.Str(value));
                }
            }

            // Framing: chunked wins, then an explicit length, then end of stream.
            byte[] body;
            if (headers.TryGet(Value.Str("transfer-encoding"), out var encoding) &&
                encoding.AsString().ToLowerInvariant().Contains("chunked"))
            {
                body = source.ReadChunked(maxBytes);
            }
            else if (headers.TryGet(Value.Str("content-length"), out var lengthValue))
            {
                if (!int.TryParse(lengthValue.AsString(), NumberStyles.None, CultureInfo.InvariantCulture, out var length))
                {
                    throw vm.Error("HttpError", $"bad Content-Length: '{lengthValue.AsString()}'");
                }

                if (length > maxBytes)
                {
                    throw vm.Error("HttpError", $"response body is larger than {maxBytes} bytes");
                }

                body = source.ReadExactly(length, "body");
            }
            else
            {
                body = source.ReadToEnd(maxBytes);
            }

            var response = new TernMap();
            response.Set(Value.Str("status"), new Value(status));
            response.Set(Value.Str("reason"), Value.Str(reason));
            response.Set(Value.Str("version"), Value.Str(version));
            response.Set(Value.Str("headers"), Value.Of(headers));
            response.Set(Value.Str("body"), Value.Str(Encoding.UTF8.GetString(body)));
            response.Set(Value.Str("bytes"), Value.Of(Wire.ToList(body)));
            return response;
        }

        /// <summary>
        /// A byte source that may be a complete buffer or a socket. Everything the
        /// parser needs — lines, exact counts, chunked bodies — is built on
        /// <see cref="Fill"/>, so both paths behave identically.
        /// </summary>
        private sealed class Source
        {
            private readonly VM _vm;
            private readonly Stream? _socket;
            private readonly byte[]? _fixed;
            private readonly List<byte> _buffer = new();
            private readonly int _maxBytes;
            private int _position;

            public Source(VM vm, byte[] data, int maxBytes)
            {
                _vm = vm;
                _fixed = data;
                _maxBytes = maxBytes;
            }

            public Source(VM vm, Stream socket, int maxBytes)
            {
                _vm = vm;
                _socket = socket;
                _maxBytes = maxBytes;
            }

            public int Available => _finite ? _fixed!.Length - _position : _buffer.Count - _position;

            private bool _finite => _socket is null;

            public bool TryFill(int need)
            {
                if (Available >= need)
                {
                    return true;
                }

                if (_finite)
                {
                    return false;
                }

                var chunk = new byte[4096];
                while (Available < need)
                {
                    int read;
                    try
                    {
                        read = _socket!.Read(chunk);
                    }
                    catch (IOException error) when (InnerSocket(error) is { SocketErrorCode: SocketError.TimedOut })
                    {
                        throw _vm.Error("HttpError", "the response timed out");
                    }
                    catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException)
                    {
                        throw _vm.Error("ConnectionError", "connection closed while reading the response");
                    }

                    if (read <= 0)
                    {
                        throw _vm.Error("ConnectionError", "connection closed while reading the response");
                    }

                    for (var i = 0; i < read; i++)
                    {
                        _buffer.Add(chunk[i]);
                    }

                    if (_buffer.Count > _maxBytes + MaxHeaderBytes)
                    {
                        throw _vm.Error("HttpError", $"response body is larger than {_maxBytes} bytes");
                    }
                }

                return true;
            }

            public string ReadLine(string what)
            {
                for (var scanned = _position; ; scanned++)
                {
                    if (!TryFill(scanned - _position + 1))
                    {
                        throw _vm.Error(_socket is null ? "HttpError" : "ConnectionError",
                            _socket is null
                                ? $"response is incomplete (no end of {what})"
                                : $"connection closed while reading the {what}");
                    }

                    // A line ends at LF; a preceding CR is part of the terminator.
                    if (ByteAt(scanned) == '\n')
                    {
                        var end = scanned > _position && ByteAt(scanned - 1) == '\r' ? scanned - 1 : scanned;
                        var text = Encoding.UTF8.GetString(Slice(_position, end - _position));
                        _position = scanned + 1;
                        return text;
                    }

                    if (scanned - _position > MaxHeaderBytes)
                    {
                        throw _vm.Error("HttpError", $"response header line is larger than {MaxHeaderBytes} bytes");
                    }
                }
            }

            private byte ByteAt(int index) => _finite ? _fixed![index] : _buffer[index];

            private byte[] Slice(int start, int length)
            {
                var result = new byte[length];
                if (_finite)
                {
                    Array.Copy(_fixed!, start, result, 0, length);
                }
                else
                {
                    _buffer.CopyTo(start, result, 0, length);
                }

                return result;
            }

            public byte[] ReadExactly(int length, string what)
            {
                if (length < 0)
                {
                    throw _vm.Error("HttpError", $"bad {what} length: {length}");
                }

                if (length > _maxBytes)
                {
                    throw _vm.Error("HttpError", $"response body is larger than {_maxBytes} bytes");
                }

                if (!TryFill(length))
                {
                    throw _vm.Error(_socket is null ? "HttpError" : "ConnectionError",
                        _socket is null
                            ? $"response is incomplete (wanted {length} bytes of {what})"
                            : $"connection closed while reading the {what}");
                }

                var result = Slice(_position, length);
                _position += length;
                return result;
            }

            public byte[] ReadToEnd(int maxBytes)
            {
                if (_finite)
                {
                    var remaining = _fixed!.Length - _position;
                    if (remaining > maxBytes)
                    {
                        throw _vm.Error("HttpError", $"response body is larger than {maxBytes} bytes");
                    }

                    var result = Slice(_position, remaining);
                    _position += remaining;
                    return result;
                }

                var chunk = new byte[4096];
                while (true)
                {
                    int read;
                    try
                    {
                        read = _socket!.Read(chunk);
                    }
                    catch (SocketException error) when (error.SocketErrorCode == SocketError.TimedOut)
                    {
                        throw _vm.Error("HttpError", "the response timed out");
                    }
                    catch (Exception error) when (error is SocketException or ObjectDisposedException)
                    {
                        throw _vm.Error("ConnectionError", "connection closed while reading the response");
                    }

                    if (read <= 0)
                    {
                        break;
                    }

                    for (var i = 0; i < read; i++)
                    {
                        _buffer.Add(chunk[i]);
                    }

                    if (_buffer.Count - _position > maxBytes)
                    {
                        throw _vm.Error("HttpError", $"response body is larger than {maxBytes} bytes");
                    }
                }

                var body = Slice(_position, _buffer.Count - _position);
                _position = _buffer.Count;
                return body;
            }

            public byte[] ReadChunked(int maxBytes)
            {
                var body = new List<byte>();
                while (true)
                {
                    var line = ReadLine("chunk size");
                    var semicolon = line.IndexOf(';');
                    var sizeText = (semicolon < 0 ? line : line.Substring(0, semicolon)).Trim();
                    if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) || size < 0)
                    {
                        throw _vm.Error("HttpError", $"bad chunk size: '{sizeText}'");
                    }

                    if (size == 0)
                    {
                        // Trailer headers run to an empty line and are not part of the body.
                        while (ReadLine("trailer").Length > 0)
                        {
                        }

                        return body.ToArray();
                    }

                    if (body.Count + size > maxBytes)
                    {
                        throw _vm.Error("HttpError", $"response body is larger than {maxBytes} bytes");
                    }

                    body.AddRange(ReadExactly(size, "chunk"));
                    ReadLine("chunk terminator");
                }
            }
        }

        // ------------------------------------------------------------- connections

        private static Value Request(VM vm, TernEngine engine, string method, string url, in Value optionsValue)
        {
            NetModule.RequireNetwork(vm);
            return Value.Of(Send(vm, method, url, ParseOptions(vm, optionsValue, "request")));
        }

        private static Value Request(VM vm, TernEngine engine, string method, string url, Options options)
        {
            NetModule.RequireNetwork(vm);
            return Value.Of(Send(vm, method, url, options));
        }

        private static TernMap Send(VM vm, string method, string url, Options options)
        {
            var currentMethod = method;
            var currentUrl = url;
            var hops = 0;

            while (true)
            {
                var target = ParseUrl(vm, currentUrl);
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                {
                    ReceiveTimeout = options.Timeout,
                    SendTimeout = options.Timeout,
                };

                try
                {
                    socket.Connect(NetModule.Resolve(vm, target.Host), target.Port);
                }
                catch (SocketException error)
                {
                    throw vm.Error("HttpError", $"cannot connect to {target.Host}:{target.Port}: {error.Message}");
                }

                var stream = new NetworkStream(socket, ownsSocket: false);
                WriteAll(vm, stream, EncodeRequest(vm, currentMethod, currentUrl, options));
                var response = ReadResponse(vm, stream, options.MaxBytes);

                if (!response.TryGet(Value.Str("headers"), out var headersValue) ||
                    !headersValue.AsMap().TryGet(Value.Str("location"), out var locationValue) ||
                    !IsRedirect(response))
                {
                    return response;
                }

                if (hops >= options.Redirects)
                {
                    throw vm.Error("HttpError", $"too many redirects (limit {options.Redirects})");
                }

                hops++;
                currentUrl = Resolve(target, locationValue.AsString());
                if (response.TryGet(Value.Str("status"), out var statusValue) && statusValue.AsInt() == 303)
                {
                    currentMethod = "GET";
                    options.Body = null;
                }
            }
        }

        private static bool IsRedirect(TernMap response)
        {
            if (!response.TryGet(Value.Str("status"), out var status))
            {
                return false;
            }

            return status.AsInt() is 301 or 302 or 303 or 307 or 308;
        }

        private static string Resolve(Url from, string location)
        {
            if (location.Contains("://", StringComparison.Ordinal))
            {
                return location;
            }

            if (location.StartsWith('/'))
            {
                return $"http://{from.Authority}{location}";
            }

            var directory = from.Path.LastIndexOf('/');
            var prefix = directory <= 0 ? "/" : from.Path.Substring(0, directory + 1);
            return $"http://{from.Authority}{prefix}{location}";
        }
    }
}
