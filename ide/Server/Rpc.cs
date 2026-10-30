using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TERN.Ide
{
    /// <summary>
    /// The Language Server Protocol's framing: a `Content-Length` header, a blank
    /// line, then exactly that many bytes of UTF-8 JSON. It is deliberately
    /// simple — the protocol's whole transport is nine lines of code — and using
    /// it rather than something bespoke means the server works with any editor
    /// that speaks LSP, not just the one in this repository.
    /// </summary>
    public static class Rpc
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>Reads one message, or null at end of input.</summary>
        public static JsonNode? Read(Stream input)
        {
            var length = -1;
            var header = new StringBuilder();

            for (;;)
            {
                var next = input.ReadByte();
                if (next < 0)
                {
                    return null;
                }

                if (next == '\n')
                {
                    var line = header.ToString().TrimEnd('\r');
                    header.Clear();

                    if (line.Length == 0)
                    {
                        if (length < 0)
                        {
                            // A blank line before any header: skip it.
                            continue;
                        }

                        break;
                    }

                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(line.Substring("Content-Length:".Length).Trim(), out var parsed))
                        {
                            length = parsed;
                        }
                    }

                    continue;
                }

                header.Append((char)next);
            }

            var buffer = new byte[length];
            var read = 0;
            while (read < length)
            {
                var chunk = input.Read(buffer, read, length - read);
                if (chunk <= 0)
                {
                    return null;
                }

                read += chunk;
            }

            return JsonNode.Parse(Encoding.UTF8.GetString(buffer));
        }

        /// <summary>Writes one message and flushes it; the client is streaming, not batch.</summary>
        public static void Write(Stream output, JsonNode message)
        {
            var body = Encoding.UTF8.GetBytes(message.ToJsonString(Options));
            var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            lock (output)
            {
                output.Write(header, 0, header.Length);
                output.Write(body, 0, body.Length);
                output.Flush();
            }
        }
    }
}
