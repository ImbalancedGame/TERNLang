using System;
using System.Net.Sockets;
using System.Text;
using TERN.Compiler;
using TERN.Runtime;

namespace TERN.Builtins
{
    /// <summary>
    /// <c>rcon</c>: the Source RCON protocol that game servers (Source, GoldSrc,
    /// Minecraft mods, ARK, ...) speak. A packet is a little-endian length, an id,
    /// a type, the body and two NUL bytes:
    ///
    /// <code>
    /// int32 size    // of everything after this field
    /// int32 id
    /// int32 type    // 3 = auth, 2 = command, 0 = response
    /// bytes body
    /// int8 0, int8 0
    /// </code>
    ///
    /// A session lives on a socket handle, so <c>rcon.attach</c> can take one that
    /// was opened elsewhere (a tunnel, or a test server that answers by hand).
    /// </summary>
    internal static class RconModule
    {
        private const int DefaultTimeout = 5000;
        private const int MaxPacket = 1024 * 1024;

        private const int TypeResponse = 0;
        private const int TypeCommand = 2;
        private const int TypeAuth = 3;

        public static void Install(TernEngine engine)
        {
            var map = new TernMap();

            Add(map, "connect", 3, 4, (vm, a) =>
            {
                NetModule.RequireNetwork(vm);

                var host = NetModule.Text(vm, a[0], "host");
                var port = (int)NetModule.Number(vm, a[1], "port");
                var password = NetModule.Text(vm, a[2], "password");
                var timeout = ParseTimeout(vm, a.Length > 3 ? a[3] : Value.Nil, "connect");

                var address = NetModule.Resolve(vm, host);
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    socket.NoDelay = true;
                    socket.ReceiveTimeout = timeout;
                    socket.SendTimeout = timeout;
                    socket.Connect(address, port);
                }
                catch (SocketException error)
                {
                    socket.Dispose();
                    throw vm.Error("RconError", $"cannot connect to {host}:{port}: {error.Message}");
                }

                var handle = engine.Net.Add(socket, NetKind.Rcon);
                engine.Net.Require(vm, handle).TimeoutMs = timeout;

                try
                {
                    Authenticate(vm, socket, password);
                }
                catch
                {
                    engine.Net.Close(handle);
                    throw;
                }

                return new Value(handle);
            }, "Connects and logs in: rcon.connect(host, port, password[, options]).");

            Add(map, "attach", 2, 3, (vm, a) =>
            {
                var endpoint = engine.Net.Require(vm, NetModule.Handle(vm, a[0]));
                if (endpoint.Kind != NetKind.Stream)
                {
                    throw vm.Error("NetError", "attach() needs a connection handle from net.connect()");
                }

                var password = NetModule.Text(vm, a[1], "password");
                var timeout = ParseTimeout(vm, a.Length > 2 ? a[2] : Value.Nil, "attach");

                endpoint.Kind = NetKind.Rcon;
                endpoint.TimeoutMs = timeout;
                if (timeout > 0)
                {
                    endpoint.Socket.ReceiveTimeout = timeout;
                    endpoint.Socket.SendTimeout = timeout;
                }

                Authenticate(vm, endpoint.Socket, password);
                return new Value(NetModule.Handle(vm, a[0]));
            }, "Logs in on an existing net handle: rcon.attach(socket, password[, options]).");

            Add(map, "execute", 2, 2, (vm, a) =>
            {
                var endpoint = Session(vm, engine, a[0], "execute");
                return Value.Str(Execute(vm, endpoint.Socket, NetModule.Text(vm, a[1], "command")));
            }, "Runs one command and returns everything the server sent back.");

            Add(map, "close", 1, 1, (vm, a) =>
            {
                engine.Net.Close(NetModule.Handle(vm, a[0]));
                return Value.Nil;
            }, "Closes an rcon session.");

            engine.RegisterValue("rcon", Value.Of(map));
            engine.RegisterStdModule(new StdModule
            {
                Name = "std:rcon",
                Members = map,
                FlatNames = Array.Empty<string>(),
            });
        }

        private static void Add(TernMap map, string name, int min, int max, NativeFn function, string documentation) =>
            map.Set(Value.Str(name), Value.Of(new TernNative(name, function, min, max, documentation)));

        private static int ParseTimeout(VM vm, in Value value, string operation)
        {
            if (value.Kind == ValueKind.Nil)
            {
                return DefaultTimeout;
            }

            if (value.Kind != ValueKind.Map)
            {
                throw vm.Error("TypeError", $"{operation}() options must be a map, not {vm.TypeName(value)}");
            }

            var map = value.AsMap();
            if (!map.TryGet(Value.Str("timeout"), out var timeout))
            {
                return DefaultTimeout;
            }

            var milliseconds = NetModule.Number(vm, timeout, "timeout");
            if (milliseconds < 0)
            {
                throw vm.Error("ArgumentError", "option 'timeout' must not be negative");
            }

            return (int)milliseconds;
        }

        private static NetEndpoint Session(VM vm, TernEngine engine, in Value handle, string operation)
        {
            var endpoint = engine.Net.Require(vm, NetModule.Handle(vm, handle));
            if (endpoint.Kind != NetKind.Rcon)
            {
                throw vm.Error("NetError", $"{operation}() needs a handle from rcon.connect() or rcon.attach()");
            }

            return endpoint;
        }

        // ---------------------------------------------------------------- packets

        private static void SendPacket(VM vm, Socket socket, int id, int type, string body)
        {
            var payload = Encoding.UTF8.GetBytes(body);
            var packet = new byte[4 + 4 + 4 + payload.Length + 2];
            WriteInt32(packet, 0, 4 + 4 + payload.Length + 2);
            WriteInt32(packet, 4, id);
            WriteInt32(packet, 8, type);
            payload.CopyTo(packet, 12);
            NetModule.SendAll(vm, socket, packet);
        }

        private static (int Id, int Type, string Body) ReadPacket(VM vm, Socket socket)
        {
            Span<byte> header = stackalloc byte[4];
            NetModule.ReadExactly(vm, socket, header, MaxPacket, "rcon");
            var size = ReadInt32(header);
            if (size < 10 || size > MaxPacket)
            {
                throw vm.Error("RconError", $"packet of {size} bytes is out of range");
            }

            var payload = new byte[size];
            NetModule.ReadExactly(vm, socket, payload, MaxPacket, "rcon");
            var id = ReadInt32(payload.AsSpan(0, 4));
            var type = ReadInt32(payload.AsSpan(4, 4));
            var body = Encoding.UTF8.GetString(payload, 8, size - 10);
            return (id, type, body);
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        private static int ReadInt32(ReadOnlySpan<byte> buffer) =>
            buffer[0] | (buffer[1] << 8) | (buffer[2] << 16) | (buffer[3] << 24);

        // ------------------------------------------------------------ the protocol

        /// <summary>
        /// Logs in. A refused password comes back as a type 2 packet with id -1;
        /// some servers send an empty response packet first, which is ignored.
        /// </summary>
        private static void Authenticate(VM vm, Socket socket, string password)
        {
            SendPacket(vm, socket, 1, TypeAuth, password);

            for (var attempt = 0; attempt < 8; attempt++)
            {
                var packet = ReadPacket(vm, socket);
                if (packet.Type != TypeResponse)
                {
                    if (packet.Id == -1)
                    {
                        throw vm.Error("AuthenticationError", "rcon authentication failed");
                    }

                    return;
                }
            }

            throw vm.Error("AuthenticationError", "rcon authentication failed");
        }

        /// <summary>
        /// Sends a command and an empty sentinel packet, then collects the answer
        /// until the sentinel's id comes back. Servers split long output into
        /// several packets, so the pieces are joined.
        /// </summary>
        private static string Execute(VM vm, Socket socket, string command)
        {
            var text = new StringBuilder();
            SendPacket(vm, socket, 1, TypeCommand, command);
            SendPacket(vm, socket, 2, TypeResponse, string.Empty);

            for (var packets = 0; packets < 4096; packets++)
            {
                var packet = ReadPacket(vm, socket);
                if (packet.Id == 2)
                {
                    return text.ToString();
                }

                if (packet.Id == 1 && packet.Type == TypeResponse)
                {
                    text.Append(packet.Body);
                }
            }

            throw vm.Error("RconError", "the server sent more than 4096 packets for one command");
        }
    }
}
