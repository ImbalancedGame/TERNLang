using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TERN.Compiler;
using TERN.Runtime;

namespace TERN.Builtins
{
    /// <summary>One open socket, addressed from a script by a small integer handle.</summary>
    internal sealed class NetEndpoint
    {
        public NetEndpoint(Socket socket, bool listener)
        {
            Socket = socket;
            Listener = listener;
        }

        public Socket Socket { get; }
        public bool Listener { get; }

        /// <summary>Receive/send timeout in milliseconds; 0 waits forever.</summary>
        public int TimeoutMs { get; set; }

        /// <summary>Largest frame this socket will accept, in bytes.</summary>
        public int MaxFrame { get; set; } = NetModule.DefaultMaxFrame;

        /// <summary>The token the peer must present, for a listener given one.</summary>
        public string? Token { get; set; }
    }

    /// <summary>
    /// The handle table behind the <c>net</c> namespace. Scripts see integers so
    /// the same code works on both back ends; the registry maps them to sockets.
    /// </summary>
    internal sealed class NetRegistry
    {
        private readonly Dictionary<long, NetEndpoint> _endpoints = new();
        private long _next = 1;

        public long Add(Socket socket, bool listener)
        {
            var handle = _next++;
            _endpoints[handle] = new NetEndpoint(socket, listener);
            return handle;
        }

        public NetEndpoint Require(VM vm, long handle)
        {
            if (!_endpoints.TryGetValue(handle, out var endpoint))
            {
                throw vm.Error("NetError", $"no open socket with handle {handle}");
            }

            return endpoint;
        }

        public void Close(long handle)
        {
            if (_endpoints.Remove(handle, out var endpoint))
            {
                CloseSocket(endpoint);
            }
        }

        public void CloseAll()
        {
            foreach (var endpoint in _endpoints.Values)
            {
                CloseSocket(endpoint);
            }

            _endpoints.Clear();
        }

        private static void CloseSocket(NetEndpoint endpoint)
        {
            try
            {
                endpoint.Socket.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException)
            {
                // Already closed or never connected: nothing to flush.
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            endpoint.Socket.Dispose();
        }
    }

    /// <summary>
    /// <c>net</c>: sockets plus the wire format. A connection speaks length
    /// prefixed frames, each one an encoded TERN value, so `net.send(conn, data)`
    /// and `net.receive(conn)` move plain data (structs included) with no
    /// schema, no string parsing and no JSON in between.
    ///
    /// Every connection opens with a hello frame (see <see cref="Hello"/>) so a
    /// listener given a token can refuse a peer before the script sees it.
    /// </summary>
    internal static class NetModule
    {
        /// <summary>The largest frame either back end will ever accept.</summary>
        public const int FrameCeiling = 64 * 1024 * 1024;

        /// <summary>
        /// What a connection accepts before net.maxFrame says otherwise. A server
        /// that takes whatever a peer claims otherwise lets four bytes cost it
        /// 64 MiB, once per connection.
        /// </summary>
        public const int DefaultMaxFrame = 1024 * 1024;

        /// <summary>How long a listener waits for a peer's hello.</summary>
        private const int HandshakeTimeoutMs = 5000;

        private const byte ProtocolVersion = 1;

        public static void Install(TernEngine engine)
        {
            var map = new TernMap();

            Add(map, "encode", 1, 1, (vm, a) => Value.Of(Wire.ToList(Wire.Encode(vm, a[0]))),
                "Encodes a value to a list of bytes.");
            Add(map, "decode", 1, 1, (vm, a) => Wire.Decode(vm, Wire.FromList(vm, a[0])),
                "Decodes bytes produced by encode back into a value.");

            Add(map, "listen", 1, 3, (vm, a) =>
            {
                RequireNetwork(vm);

                // listen(port) | listen(port, options) | listen(host, port, options):
                // a second argument that is a number selects the host form.
                string host = "127.0.0.1";
                var portValue = a[0];
                var optionsValue = a.Length > 1 && a[1].Kind != ValueKind.Int ? a[1] : Value.Nil;
                if (a.Length > 1 && a[1].Kind == ValueKind.Int)
                {
                    host = Text(vm, a[0], "host");
                    portValue = a[1];
                    optionsValue = a.Length > 2 ? a[2] : Value.Nil;
                }

                var options = ParseOptions(vm, optionsValue, "listen");
                var port = (int)Number(vm, portValue, "port");

                var address = Resolve(vm, host);
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    socket.Bind(new IPEndPoint(address, port));
                    socket.Listen(128);
                }
                catch (SocketException error)
                {
                    socket.Dispose();
                    throw vm.Error("NetError", $"cannot listen on {host}:{port}: {error.Message}");
                }

                var handle = engine.Net.Add(socket, listener: true);
                var endpoint = engine.Net.Require(vm, handle);
                endpoint.Token = options.Token;
                endpoint.MaxFrame = options.MaxFrame;
                endpoint.TimeoutMs = options.Timeout;
                return new Value(handle);
            }, "Listens on a port and returns a server handle. Options: token, maxFrame.");

            Add(map, "accept", 1, 1, (vm, a) =>
            {
                var endpoint = engine.Net.Require(vm, Handle(vm, a[0]));
                if (!endpoint.Listener)
                {
                    throw vm.Error("NetError", "accept() needs a server handle from listen()");
                }

                if (endpoint.TimeoutMs > 0 &&
                    !endpoint.Socket.Poll(Math.Max(1, endpoint.TimeoutMs) * 1000, SelectMode.SelectRead))
                {
                    throw vm.Error("NetError", "accept() timed out");
                }

                Socket connection;
                try
                {
                    connection = endpoint.Socket.Accept();
                    connection.NoDelay = true;
                }
                catch (SocketException error)
                {
                    throw vm.Error("NetError", $"accept() failed: {error.Message}");
                }

                Authenticate(vm, connection, endpoint);

                var handle = engine.Net.Add(connection, listener: false);
                engine.Net.Require(vm, handle).MaxFrame = endpoint.MaxFrame;
                return new Value(handle);
            }, "Waits for one connection, checks its hello, and returns its handle.");

            Add(map, "connect", 2, 3, (vm, a) =>
            {
                RequireNetwork(vm);

                var host = Text(vm, a[0], "host");
                var options = ParseOptions(vm, a.Length > 2 ? a[2] : Value.Nil, "connect");
                var port = (int)Number(vm, a[1], "port");
                var address = Resolve(vm, host);
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    Connect(vm, socket, new IPEndPoint(address, port), host, port, options.Timeout);
                    socket.NoDelay = true;
                }
                catch (SocketException error)
                {
                    socket.Dispose();
                    throw vm.Error("NetError", $"cannot connect to {host}:{port}: {error.Message}");
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }

                try
                {
                    SendHello(vm, socket, options.Token);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }

                var handle = engine.Net.Add(socket, listener: false);
                var endpoint = engine.Net.Require(vm, handle);
                endpoint.TimeoutMs = options.Timeout;
                endpoint.MaxFrame = options.MaxFrame;
                if (options.Timeout > 0)
                {
                    socket.ReceiveTimeout = options.Timeout;
                    socket.SendTimeout = options.Timeout;
                }

                return new Value(handle);
            }, "Connects and says hello. Options: timeout, token, maxFrame.");

            Add(map, "send", 2, 2, (vm, a) =>
            {
                var endpoint = Connection(vm, engine, a[0], "send");
                var payload = Wire.Encode(vm, a[1]);
                if (payload.Length > FrameCeiling)
                {
                    throw vm.Error("NetError", $"value is larger than {FrameCeiling} bytes");
                }

                var frame = new byte[4 + payload.Length];
                BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
                payload.CopyTo(frame, 4);
                SendAll(vm, endpoint.Socket, frame);
                return Value.Nil;
            }, "Encodes a value and sends it as one frame.");

            Add(map, "receive", 1, 1, (vm, a) =>
            {
                var endpoint = Connection(vm, engine, a[0], "receive");
                return Wire.Decode(vm, ReceiveFrame(vm, endpoint));
            }, "Waits for one frame and returns the decoded value.");

            Add(map, "sendBytes", 2, 2, (vm, a) =>
            {
                var endpoint = Connection(vm, engine, a[0], "sendBytes");
                var bytes = Wire.FromList(vm, a[1]);
                SendAll(vm, endpoint.Socket, bytes);
                return Value.Nil;
            }, "Sends raw bytes (no frame header). Used by protocols layered on net.");

            Add(map, "receiveBytes", 2, 2, (vm, a) =>
            {
                var endpoint = Connection(vm, engine, a[0], "receiveBytes");
                var count = (int)Number(vm, a[1], "count");
                if (count < 0)
                {
                    throw vm.Error("NetError", "receiveBytes() needs a count of 0 or more");
                }

                var buffer = new byte[count];
                ReadExactly(vm, endpoint.Socket, buffer, endpoint.MaxFrame);
                return Value.Of(Wire.ToList(buffer));
            }, "Reads exactly n bytes (no frame header).");

            Add(map, "close", 1, 1, (vm, a) =>
            {
                engine.Net.Close(Handle(vm, a[0]));
                return Value.Nil;
            }, "Closes a socket handle.");

            Add(map, "timeout", 2, 2, (vm, a) =>
            {
                var endpoint = engine.Net.Require(vm, Handle(vm, a[0]));
                var milliseconds = (int)Number(vm, a[1], "milliseconds");
                if (milliseconds < 0)
                {
                    milliseconds = 0;
                }

                endpoint.TimeoutMs = milliseconds;
                try
                {
                    endpoint.Socket.ReceiveTimeout = milliseconds;
                    endpoint.Socket.SendTimeout = milliseconds;
                }
                catch (ObjectDisposedException)
                {
                    throw vm.Error("NetError", "socket is closed");
                }

                return Value.Nil;
            }, "Sets the receive/send timeout in milliseconds (0 waits forever).");

            Add(map, "maxFrame", 2, 2, (vm, a) =>
            {
                var endpoint = engine.Net.Require(vm, Handle(vm, a[0]));
                var limit = Number(vm, a[1], "bytes");
                if (limit < 0 || limit > FrameCeiling)
                {
                    throw vm.Error("NetError", $"maxFrame() must be between 0 and {FrameCeiling} bytes");
                }

                endpoint.MaxFrame = (int)limit;
                return Value.Nil;
            }, "Largest frame this socket accepts; a peer cannot make you allocate more.");

            Add(map, "port", 1, 1, (vm, a) =>
            {
                var endpoint = engine.Net.Require(vm, Handle(vm, a[0]));
                return endpoint.Socket.LocalEndPoint is IPEndPoint local
                    ? new Value(local.Port)
                    : new Value(0);
            }, "Local port of a socket (useful after listen(0)).");

            Add(map, "peer", 1, 1, (vm, a) =>
            {
                var endpoint = engine.Net.Require(vm, Handle(vm, a[0]));
                return endpoint.Socket.RemoteEndPoint is IPEndPoint remote
                    ? Value.Str(remote.ToString())
                    : Value.Nil;
            }, "Address of the other side of a connection.");

            engine.RegisterValue("net", Value.Of(map));
            engine.RegisterStdModule(new StdModule
            {
                Name = "std:net",
                Members = map,
                FlatNames = Array.Empty<string>(),
            });
        }

        // ------------------------------------------------------------- handshake

        /// <summary>
        /// The first frame of every connection: one version byte, then the token
        /// as a length-prefixed UTF-8 string (length 0 for "no token"). Bytes
        /// after the token are reserved for later versions to negotiate with.
        /// </summary>
        internal static byte[] Hello(string? token)
        {
            var tokenBytes = token is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(token);
            var frame = new List<byte>(2 + tokenBytes.Length) { ProtocolVersion };

            var length = (uint)tokenBytes.Length;
            do
            {
                var chunk = (byte)(length & 0x7F);
                length >>= 7;
                frame.Add(length == 0 ? chunk : (byte)(chunk | 0x80));
            }
            while (length != 0);

            frame.AddRange(tokenBytes);
            return frame.ToArray();
        }

        private static void SendHello(VM vm, Socket socket, string? token)
        {
            var payload = Hello(token);
            var frame = new byte[4 + payload.Length];
            BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
            payload.CopyTo(frame, 4);
            SendAll(vm, socket, frame);
        }

        /// <summary>
        /// Reads the peer's hello on a freshly accepted connection and refuses it
        /// when the version is unknown or the token does not match the one this
        /// listener was created with. The connection is closed on refusal.
        /// </summary>
        private static void Authenticate(VM vm, Socket connection, NetEndpoint listener)
        {
            var timeout = listener.TimeoutMs > 0 ? listener.TimeoutMs : HandshakeTimeoutMs;
            connection.ReceiveTimeout = timeout;

            byte[]? reason = null;
            try
            {
                var header = new byte[4];
                ReadHello(vm, connection, header);

                var length = BinaryPrimitives.ReadInt32BigEndian(header);
                if (length < 1 || length > FrameCeiling)
                {
                    reason = Encoding.UTF8.GetBytes($"hello of {length} bytes is out of range");
                }
                else
                {
                    var payload = new byte[length];
                    ReadHello(vm, connection, payload);
                    reason = CheckHello(payload, listener.Token);
                }
            }
            finally
            {
                connection.ReceiveTimeout = 0;
            }

            if (reason is not null)
            {
                // Say why, then close: a rejected peer gets no second chance.
                try
                {
                    SendAll(vm, connection, reason);
                }
                catch (TernError)
                {
                    // The rejection message is best effort.
                }

                connection.Dispose();
                throw vm.Error("AuthenticationError", Encoding.UTF8.GetString(reason));
            }
        }

        /// <summary>
        /// Reads part of a peer's hello. Anything other than the bytes themselves
        /// is reported as a dead handshake, so a silent or hostile peer cannot
        /// hang the listener or look like a script-level error.
        /// </summary>
        private static void ReadHello(VM vm, Socket socket, byte[] buffer)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                int read;
                try
                {
                    read = socket.Receive(buffer, offset, buffer.Length - offset, SocketFlags.None);
                }
                catch (Exception error) when (error is SocketException or ObjectDisposedException)
                {
                    throw vm.Error("ConnectionError", "connection closed during the handshake");
                }

                if (read <= 0)
                {
                    throw vm.Error("ConnectionError", "connection closed during the handshake");
                }

                offset += read;
            }
        }

        /// <summary>
        /// Checks the hello payload: one version byte, the token length as a
        /// varint, then the token. Returns the refusal message, or null when the
        /// peer may stay.
        /// </summary>
        private static byte[]? CheckHello(byte[] payload, string? expected)
        {
            if (payload[0] != ProtocolVersion)
            {
                return Encoding.UTF8.GetBytes($"unsupported protocol version {payload[0]}");
            }

            var at = 1;
            var length = 0;
            var shift = 0;
            var malformed = false;
            while (true)
            {
                if (at >= payload.Length)
                {
                    malformed = true;
                    break;
                }

                var chunk = payload[at++];
                length |= (chunk & 0x7F) << shift;
                if ((chunk & 0x80) == 0)
                {
                    break;
                }

                shift += 7;
                if (shift > 28)
                {
                    malformed = true;
                    break;
                }
            }

            if (!malformed && (length < 0 || length != payload.Length - at))
            {
                malformed = true;
            }

            if (malformed)
            {
                return Encoding.UTF8.GetBytes("hello length is not a valid number");
            }

            if (expected is not null)
            {
                var presented = new byte[length];
                Array.Copy(payload, at, presented, 0, length);
                if (!FixedTimeEquals(Encoding.UTF8.GetBytes(expected), presented))
                {
                    return Encoding.UTF8.GetBytes("token rejected");
                }
            }

            return null;
        }

        /// <summary>Compares without returning early, so a token's prefix is not timed.</summary>
        private static bool FixedTimeEquals(byte[] expected, byte[] presented)
        {
            var difference = expected.Length ^ presented.Length;
            for (var i = 0; i < expected.Length; i++)
            {
                difference |= expected[i] ^ (i < presented.Length ? presented[i] : 0);
            }

            return difference == 0;
        }

        // ---------------------------------------------------------------- options

        internal readonly struct NetOptions
        {
            public NetOptions(string? token, int maxFrame, int timeout)
            {
                Token = token;
                MaxFrame = maxFrame;
                Timeout = timeout;
            }

            public string? Token { get; }
            public int MaxFrame { get; }
            public int Timeout { get; }
        }

        private static NetOptions ParseOptions(VM vm, in Value value, string operation)
        {
            if (value.Kind == ValueKind.Nil)
            {
                return new NetOptions(null, DefaultMaxFrame, 0);
            }

            if (value.Kind != ValueKind.Map)
            {
                throw vm.Error("TypeError", $"{operation}() options must be a map, not {vm.TypeName(value)}");
            }

            var map = value.AsMap();
            string? token = null;
            var maxFrame = DefaultMaxFrame;
            var timeout = 0;

            if (map.TryGet(Value.Str("token"), out var tokenValue))
            {
                if (tokenValue.Kind != ValueKind.Str)
                {
                    throw vm.Error("TypeError", $"option 'token' must be a string, not {vm.TypeName(tokenValue)}");
                }

                token = tokenValue.AsString();
                if (token.Length == 0)
                {
                    throw vm.Error("ArgumentError", "option 'token' must not be empty");
                }
            }

            if (map.TryGet(Value.Str("maxFrame"), out var frameValue))
            {
                var limit = Number(vm, frameValue, "maxFrame");
                if (limit <= 0 || limit > FrameCeiling)
                {
                    throw vm.Error("NetError", $"option 'maxFrame' must be between 1 and {FrameCeiling} bytes");
                }

                maxFrame = (int)limit;
            }

            if (map.TryGet(Value.Str("timeout"), out var timeoutValue))
            {
                var milliseconds = Number(vm, timeoutValue, "timeout");
                if (milliseconds < 0)
                {
                    throw vm.Error("ArgumentError", "option 'timeout' must not be negative");
                }

                timeout = (int)milliseconds;
            }

            return new NetOptions(token, maxFrame, timeout);
        }

        private static void Connect(VM vm, Socket socket, IPEndPoint endPoint, string host, int port, int timeoutMs)
        {
            if (timeoutMs <= 0)
            {
                socket.Connect(endPoint);
                return;
            }

            var attempt = socket.ConnectAsync(endPoint);
            if (!attempt.Wait(timeoutMs))
            {
                socket.Dispose();
                throw vm.Error("NetError", $"cannot connect to {host}:{port}: timed out after {timeoutMs} ms");
            }

            // Surfaces the socket error the wait swallowed.
            attempt.GetAwaiter().GetResult();
        }

        // ------------------------------------------------------------------ bytes

        private static byte[] ReceiveFrame(VM vm, NetEndpoint endpoint)
        {
            Span<byte> header = stackalloc byte[4];
            ReadExactly(vm, endpoint.Socket, header, endpoint.MaxFrame, "frame");
            var length = BinaryPrimitives.ReadInt32BigEndian(header);
            if (length < 0)
            {
                throw vm.Error("NetError", $"frame of {length} bytes is out of range");
            }

            if (length > endpoint.MaxFrame)
            {
                throw vm.Error("NetError",
                    $"frame of {length} bytes is larger than this socket's limit of {endpoint.MaxFrame}");
            }

            var payload = new byte[length];
            if (length > 0)
            {
                ReadExactly(vm, endpoint.Socket, payload, endpoint.MaxFrame, "frame");
            }

            return payload;
        }

        internal static void SendAll(VM vm, Socket socket, ReadOnlySpan<byte> frame)
        {
            try
            {
                while (frame.Length > 0)
                {
                    var sent = socket.Send(frame);
                    if (sent <= 0)
                    {
                        throw vm.Error("NetError", "connection closed while sending");
                    }

                    frame = frame[sent..];
                }
            }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.TimedOut)
            {
                throw vm.Error("NetError", "send() timed out");
            }
            catch (SocketException error)
            {
                throw vm.Error("NetError", $"send() failed: {error.Message}");
            }
            catch (ObjectDisposedException)
            {
                throw vm.Error("NetError", "socket is closed");
            }
        }

        private static void ReadExactly(VM vm, NetEndpoint endpoint, Span<byte> buffer) =>
            ReadExactly(vm, endpoint.Socket, buffer, endpoint.MaxFrame, "frame");

        /// <summary>
        /// Fills <paramref name="buffer"/> or raises. A peer that closes mid-way is
        /// reported separately from one that closes cleanly between frames.
        /// </summary>
        internal static void ReadExactly(VM vm, Socket socket, Span<byte> buffer, int limit, string what = "frame")
        {
            var offset = 0;
            try
            {
                while (offset < buffer.Length)
                {
                    var read = socket.Receive(buffer[offset..], SocketFlags.None);
                    if (read <= 0)
                    {
                        throw vm.Error("ConnectionError", offset == 0
                            ? "connection closed by the other side"
                            : "connection closed in the middle of a frame");
                    }

                    offset += read;
                }
            }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.TimedOut)
            {
                throw vm.Error("NetError", $"{what} timed out");
            }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionReset)
            {
                throw vm.Error("ConnectionError", "connection reset by the other side");
            }
            catch (SocketException error)
            {
                throw vm.Error("NetError", $"{what} failed: {error.Message}");
            }
            catch (ObjectDisposedException)
            {
                throw vm.Error("NetError", "socket is closed");
            }
        }

        // ---------------------------------------------------------------- helpers

        private static void Add(TernMap map, string name, int min, int max, NativeFn function, string? documentation = null) =>
            map.Set(Value.Str(name), Value.Of(new TernNative(name, function, min, max, documentation)));

        internal static void RequireNetwork(VM vm)
        {
            if (vm.Engine is { AllowNetwork: false })
            {
                throw vm.Error("PermissionError", "network access is disabled for this script");
            }
        }

        private static long Handle(VM vm, in Value value)
        {
            if (value.Kind != ValueKind.Int)
            {
                throw vm.Error("TypeError", $"expected a socket handle, not {vm.TypeName(value)}");
            }

            return value.AsInt();
        }

        internal static string Text(VM vm, in Value value, string what)
        {
            if (value.Kind != ValueKind.Str)
            {
                throw vm.Error("TypeError", $"{what} must be a string, not {vm.TypeName(value)}");
            }

            return value.AsString();
        }

        internal static double Number(VM vm, in Value value, string what)
        {
            if (value.Kind == ValueKind.Int)
            {
                return value.AsInt();
            }

            if (value.Kind == ValueKind.Float)
            {
                return value.AsDouble();
            }

            throw vm.Error("TypeError", $"{what} must be a number, not {vm.TypeName(value)}");
        }

        internal static IPAddress Resolve(VM vm, string host)
        {
            if (IPAddress.TryParse(host, out var parsed))
            {
                return parsed;
            }

            try
            {
                var addresses = Dns.GetHostAddresses(host);
                foreach (var address in addresses)
                {
                    if (address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        return address;
                    }
                }

                if (addresses.Length > 0)
                {
                    return addresses[0];
                }
            }
            catch (SocketException error)
            {
                throw vm.Error("NetError", $"cannot resolve '{host}': {error.Message}");
            }

            throw vm.Error("NetError", $"cannot resolve '{host}'");
        }

        private static NetEndpoint Connection(VM vm, TernEngine engine, in Value handle, string operation)
        {
            var endpoint = engine.Net.Require(vm, Handle(vm, handle));
            if (endpoint.Listener)
            {
                throw vm.Error("NetError", $"{operation}() needs a connection handle, not a server");
            }

            return endpoint;
        }
    }
}
