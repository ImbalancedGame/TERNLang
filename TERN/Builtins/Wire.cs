using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TERN.Runtime;

namespace TERN.Builtins
{
    /// <summary>
    /// The TERN wire format: one compact binary encoding covering every value
    /// kind, including struct instances, which travel with their type name and
    /// field names so the far side can rebuild them as the same struct when it
    /// declares one.
    ///
    /// Layout (little endian):
    /// <code>
    ///   0 nil          4 float    (8 bytes, IEEE 754)
    ///   1 false        5 string   (varint length, UTF-8)
    ///   2 true         6 list     (varint count, values)
    ///   3 int          7 map      (varint count, key/value pairs)
    ///                  8 struct   (string type, varint count, name/value pairs)
    ///                  9 range    (start, end, step as int64, 1 byte inclusive)
    /// </code>
    /// Lengths and counts are unsigned LEB128 varints so small payloads stay
    /// small. The format is identical in the native runtime
    /// (<c>native/runtime/tern_rt.c</c>), which is what keeps a compiled server
    /// and an interpreted client talking.
    /// </summary>
    internal static class Wire
    {
        private const byte NilTag = 0;
        private const byte FalseTag = 1;
        private const byte TrueTag = 2;
        private const byte IntTag = 3;
        private const byte FloatTag = 4;
        private const byte StrTag = 5;
        private const byte ListTag = 6;
        private const byte MapTag = 7;
        private const byte StructTag = 8;
        private const byte RangeTag = 9;

        /// <summary>Nesting limit, so a hostile payload cannot blow the stack.</summary>
        public const int MaxDepth = 128;

        /// <summary>Largest encoded value accepted, in bytes.</summary>
        public const int MaxBytes = 64 * 1024 * 1024;

        private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

        public static byte[] Encode(VM vm, in Value value)
        {
            using var buffer = new MemoryStream(64);
            Write(vm, buffer, value, 0);
            if (buffer.Length > MaxBytes)
            {
                throw vm.Error("SerializationError", $"value is larger than {MaxBytes} bytes");
            }

            return buffer.ToArray();
        }

        public static Value Decode(VM vm, ReadOnlySpan<byte> data)
        {
            if (data.Length > MaxBytes)
            {
                throw vm.Error("SerializationError", $"payload is larger than {MaxBytes} bytes");
            }

            var reader = new Reader(data);
            var value = Read(vm, ref reader, 0);
            if (reader.Remaining != 0)
            {
                throw vm.Error("SerializationError", $"{reader.Remaining} trailing byte(s) after the value");
            }

            return value;
        }

        // ------------------------------------------------------------------ write

        private static void Write(VM vm, MemoryStream stream, in Value value, int depth)
        {
            if (depth > MaxDepth)
            {
                throw vm.Error("SerializationError", $"value nests deeper than {MaxDepth} levels");
            }

            switch (value.Kind)
            {
                case ValueKind.Nil:
                case ValueKind.Missing:
                    stream.WriteByte(NilTag);
                    return;

                case ValueKind.Bool:
                    stream.WriteByte(value.AsBool() ? TrueTag : FalseTag);
                    return;

                case ValueKind.Int:
                {
                    stream.WriteByte(IntTag);
                    Span<byte> bits = stackalloc byte[8];
                    BinaryPrimitives.WriteInt64LittleEndian(bits, value.AsInt());
                    stream.Write(bits);
                    return;
                }

                case ValueKind.Float:
                {
                    stream.WriteByte(FloatTag);
                    Span<byte> bits = stackalloc byte[8];
                    BinaryPrimitives.WriteDoubleLittleEndian(bits, value.AsDouble());
                    stream.Write(bits);
                    return;
                }

                case ValueKind.Str:
                {
                    stream.WriteByte(StrTag);
                    WriteString(stream, value.AsString());
                    return;
                }

                case ValueKind.List:
                {
                    var list = value.AsList();
                    stream.WriteByte(ListTag);
                    WriteVarint(stream, (ulong)list.Count);
                    for (var i = 0; i < list.Count; i++)
                    {
                        Write(vm, stream, list[i], depth + 1);
                    }

                    return;
                }

                case ValueKind.Map:
                {
                    var map = value.AsMap();
                    stream.WriteByte(MapTag);
                    WriteVarint(stream, (ulong)map.Count);
                    for (var i = 0; i < map.SlotCount; i++)
                    {
                        if (!map.TryGetEntry(i, out var key, out var item))
                        {
                            continue;
                        }

                        Write(vm, stream, key, depth + 1);
                        Write(vm, stream, item, depth + 1);
                    }

                    return;
                }

                case ValueKind.Range:
                {
                    var range = value.AsRange();
                    stream.WriteByte(RangeTag);
                    Span<byte> bits = stackalloc byte[8];
                    BinaryPrimitives.WriteInt64LittleEndian(bits, range.Start);
                    stream.Write(bits);
                    BinaryPrimitives.WriteInt64LittleEndian(bits, range.End);
                    stream.Write(bits);
                    BinaryPrimitives.WriteInt64LittleEndian(bits, range.Step);
                    stream.Write(bits);
                    stream.WriteByte(range.Inclusive ? (byte)1 : (byte)0);
                    return;
                }

                case ValueKind.Instance:
                {
                    var instance = value.AsInstance();
                    stream.WriteByte(StructTag);
                    WriteString(stream, instance.Type.Name);
                    WriteVarint(stream, (ulong)instance.Type.FieldCount);
                    for (var i = 0; i < instance.Type.FieldCount; i++)
                    {
                        WriteString(stream, instance.Type.FieldNames[i]);
                        Write(vm, stream, instance.Fields[i], depth + 1);
                    }

                    return;
                }

                default:
                    throw vm.Error("SerializationError",
                        $"cannot serialize a {vm.TypeName(value)}; send data (nil, booleans, numbers, strings, lists, maps, ranges or structs)");
            }
        }

        private static void WriteString(MemoryStream stream, string text)
        {
            var bytes = Utf8.GetBytes(text);
            WriteVarint(stream, (ulong)bytes.Length);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static void WriteVarint(MemoryStream stream, ulong value)
        {
            while (value >= 0x80)
            {
                stream.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }

            stream.WriteByte((byte)value);
        }

        // ------------------------------------------------------------------- read

        private ref struct Reader
        {
            private readonly ReadOnlySpan<byte> _data;
            private int _offset;

            public Reader(ReadOnlySpan<byte> data)
            {
                _data = data;
                _offset = 0;
            }

            public int Remaining => _data.Length - _offset;

            public byte Byte(VM vm)
            {
                if (_offset >= _data.Length)
                {
                    throw vm.Error("SerializationError", "truncated value");
                }

                return _data[_offset++];
            }

            public ReadOnlySpan<byte> Bytes(VM vm, int count)
            {
                if (count < 0 || Remaining < count)
                {
                    throw vm.Error("SerializationError", "truncated value");
                }

                var slice = _data.Slice(_offset, count);
                _offset += count;
                return slice;
            }

            public ulong Varint(VM vm)
            {
                ulong result = 0;
                var shift = 0;
                while (true)
                {
                    var b = Byte(vm);
                    result |= (ulong)(b & 0x7F) << shift;
                    if ((b & 0x80) == 0)
                    {
                        return result;
                    }

                    shift += 7;
                    if (shift > 63)
                    {
                        throw vm.Error("SerializationError", "malformed length");
                    }
                }
            }

            /// <summary>A count that cannot exceed the bytes left in the buffer.</summary>
            public int Count(VM vm)
            {
                var count = Varint(vm);
                if (count > (ulong)Remaining)
                {
                    throw vm.Error("SerializationError", "length exceeds the payload");
                }

                return (int)count;
            }

            public string String(VM vm)
            {
                var length = Count(vm);
                return Utf8.GetString(Bytes(vm, length));
            }
        }

        private static Value Read(VM vm, ref Reader reader, int depth)
        {
            if (depth > MaxDepth)
            {
                throw vm.Error("SerializationError", $"value nests deeper than {MaxDepth} levels");
            }

            var tag = reader.Byte(vm);
            switch (tag)
            {
                case NilTag:
                    return Value.Nil;

                case FalseTag:
                    return Value.False;

                case TrueTag:
                    return Value.True;

                case IntTag:
                    return new Value(BinaryPrimitives.ReadInt64LittleEndian(reader.Bytes(vm, 8)));

                case FloatTag:
                    return new Value(BinaryPrimitives.ReadDoubleLittleEndian(reader.Bytes(vm, 8)));

                case StrTag:
                    return Value.Str(reader.String(vm));

                case ListTag:
                {
                    var count = reader.Count(vm);
                    var list = new TernList(count);
                    for (var i = 0; i < count; i++)
                    {
                        list.Add(Read(vm, ref reader, depth + 1));
                    }

                    return Value.Of(list);
                }

                case MapTag:
                {
                    var count = reader.Count(vm);
                    var map = new TernMap(count);
                    for (var i = 0; i < count; i++)
                    {
                        var key = Read(vm, ref reader, depth + 1);
                        var item = Read(vm, ref reader, depth + 1);
                        map.Set(key, item);
                    }

                    return Value.Of(map);
                }

                case RangeTag:
                {
                    var start = BinaryPrimitives.ReadInt64LittleEndian(reader.Bytes(vm, 8));
                    var end = BinaryPrimitives.ReadInt64LittleEndian(reader.Bytes(vm, 8));
                    var step = BinaryPrimitives.ReadInt64LittleEndian(reader.Bytes(vm, 8));
                    var inclusive = reader.Byte(vm) != 0;
                    return Value.Of(new TernRange(start, end, inclusive, step));
                }

                case StructTag:
                {
                    var typeName = reader.String(vm);
                    var count = reader.Count(vm);
                    var names = new string[count];
                    var values = new Value[count];
                    for (var i = 0; i < count; i++)
                    {
                        names[i] = reader.String(vm);
                        values[i] = Read(vm, ref reader, depth + 1);
                    }

                    return BuildStruct(vm, typeName, names, values);
                }

                default:
                    throw vm.Error("SerializationError", $"unknown value tag {tag}");
            }
        }

        /// <summary>
        /// Rebuilds a struct when this program declares it, and falls back to a
        /// plain map of the same fields when it does not — a receiver that has not
        /// declared the struct still gets the data.
        /// </summary>
        private static Value BuildStruct(VM vm, string typeName, string[] names, Value[] values)
        {
            var type = vm.FindType(typeName);

            if (type is null)
            {
                var map = new TernMap(names.Length);
                for (var i = 0; i < names.Length; i++)
                {
                    map.Set(Value.Str(names[i]), values[i]);
                }

                return Value.Of(map);
            }

            // Fields keep their defaults and are overwritten by name, so a sender
            // and receiver whose declarations differ in order still line up.
            var instance = type.Create(vm);
            for (var i = 0; i < names.Length; i++)
            {
                if (type.TryGetField(names[i], out var index) && index < instance.Fields.Length)
                {
                    instance.Fields[index] = values[i];
                }
            }

            return Value.Of(instance);
        }

        /// <summary>Byte list to a value list, which is what a TERN script sees.</summary>
        public static TernList ToList(ReadOnlySpan<byte> bytes)
        {
            var list = new TernList(bytes.Length);
            foreach (var b in bytes)
            {
                list.Add(new Value(b));
            }

            return list;
        }

        /// <summary>A list of integer bytes from a script, validated.</summary>
        public static byte[] FromList(VM vm, in Value value)
        {
            if (value.Kind != ValueKind.List)
            {
                throw vm.Error("TypeError", $"expected a list of bytes, not {vm.TypeName(value)}");
            }

            var list = value.AsList();
            var bytes = new byte[list.Count];
            for (var i = 0; i < list.Count; i++)
            {
                var item = list[i];
                if (item.Kind != ValueKind.Int || item.AsInt() < 0 || item.AsInt() > 255)
                {
                    throw vm.Error("TypeError", $"byte {i} is not an integer in 0..255");
                }

                bytes[i] = (byte)item.AsInt();
            }

            return bytes;
        }
    }
}
