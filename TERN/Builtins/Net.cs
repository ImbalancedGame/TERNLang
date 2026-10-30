using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
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
        public int TimeoutMs { get; set; }
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
    /// </summary>
    internal static class NetModule
    {
        /// <summary>Refuse absurd frame lengths instead of allocating them.</summary>
        private const int MaxFrame = 64 * 1024 * 1024;

        public static void Install(TernEngine engine)
        {
            var map = new TernMap();

            Add(map, "encode", 1, 1, (vm, a) => Value.Of(Wire.ToList(Wire.Encode(vm, a[0]))),
                "Encodes a value to a list of bytes.");
            Add(map, "decode", 1, 1, (vm, a) => Wire.Decode(vm, Wire.FromList(vm, a[0])),
                "Decodes bytes produced by encode back into a value.");

            Add(map, "listen", 1, 2, (vm, a) =>
            {
                RequireNetwork(vm);

                var host = a.Length > 1 ? a[0].AsString() : "127.0.0.1";
                var port = (int)Number(vm, a.Length > 1 ? a[1] : a[0], "port");

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

                return new Value(engine.Net.Add(socket, listener: true));
            }, "Listens on a port and returns a server handle: net.listen(port) or net.listen(host, port).");

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

                try
                {
                    var connection = endpoint.Socket.Accept();
                    connection.NoDelay = true;
                    return new Value(engine.Net.Add(connection, listener: false));
                }
                catch (SocketException error)
                {
                    throw vm.Error("NetError", $"accept() failed: {error.Message}");
                }
            }, "Waits for one connection and returns its handle.");

            Add(map, "connect", 2, 2, (vm, a) =>
            {
                RequireNetwork(vm);

                var host = a[0].AsString();
                var port = (int)Number(vm, a[1], "port");
                var address = Resolve(vm, host);
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    socket.Connect(new IPEndPoint(address, port));
                    socket.NoDelay = true;
                }
                catch (SocketException error)
                {
                    socket.Dispose();
                    throw vm.Error("NetError", $"cannot connect to {host}:{port}: {error.Message}");
                }

                return new Value(engine.Net.Add(socket, listener: false));
            }, "Connects to a server and returns the connection handle.");

            Add(map, "send", 2, 2, (vm, a) =>
            {
                var endpoint = Connection(vm, engine, a[0], "send");
                var payload = Wire.Encode(vm, a[1]);
                if (payload.Length > MaxFrame)
                {
                    throw vm.Error("NetError", $"value is larger than {MaxFrame} bytes");
                }

                var frame = new byte[4 + payload.Length];
                BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
                payload.CopyTo(frame, 4);
                SendAll(vm, endpoint, frame);
                return Value.Nil;
            }, "Encodes a value and sends it as one frame.");

            Add(map, "receive", 1, 1, (vm, a) =>
            {
                var endpoint = Connection(vm, engine, a[0], "receive");
                Span<byte> header = stackalloc byte[4];
                ReadExactly(vm, endpoint, header);

                var length = BinaryPrimitives.ReadInt32BigEndian(header);
                if (length < 0 || length > MaxFrame)
                {
                    throw vm.Error("NetError", $"frame of {length} bytes is out of range");
                }

                var payload = new byte[length];
                ReadExactly(vm, endpoint, payload);
                return Wire.Decode(vm, payload);
            }, "Waits for one frame and returns the decoded value.");

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

        // ------------------------------------------------------------------ helpers

        private static void Add(TernMap map, string name, int min, int max, NativeFn function, string? documentation = null) =>
            map.Set(Value.Str(name), Value.Of(new TernNative(name, function, min, max, documentation)));

        private static void RequireNetwork(VM vm)
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

        private static double Number(VM vm, in Value value, string what)
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

        private static IPAddress Resolve(VM vm, string host)
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
                    if (address.AddressFamily == AddressFamily.InterNetworkV6)
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

        private static void SendAll(VM vm, NetEndpoint endpoint, ReadOnlySpan<byte> frame)
        {
            var socket = endpoint.Socket;
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

        private static void ReadExactly(VM vm, NetEndpoint endpoint, Span<byte> buffer)
        {
            var socket = endpoint.Socket;
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
                throw vm.Error("NetError", "receive() timed out");
            }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionReset)
            {
                throw vm.Error("ConnectionError", "connection reset by the other side");
            }
            catch (SocketException error)
            {
                throw vm.Error("NetError", $"receive() failed: {error.Message}");
            }
            catch (ObjectDisposedException)
            {
                throw vm.Error("NetError", "socket is closed");
            }
        }
    }
}
