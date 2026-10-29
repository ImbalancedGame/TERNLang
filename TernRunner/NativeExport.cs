using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TERN.Compiler;
using TERN.Runtime;

namespace TernRunner
{
    /// <summary>
    /// Serialises a compiled module to the JSON interchange format consumed by the
    /// native backend (<c>native/ternc.py</c>). This is a read-only view of the
    /// bytecode: nothing here changes how the interpreter runs a script.
    /// </summary>
    public static class NativeExport
    {
        public const int FormatVersion = 1;

        public static string ToJson(TernModule module)
        {
            var exporter = new Exporter();
            return exporter.Write(module);
        }

        private sealed class Exporter
        {
            private readonly Dictionary<FunctionProto, int> _index = new(ReferenceEqualityComparer.Instance);
            private readonly List<FunctionProto> _protos = new();
            private readonly Dictionary<FunctionProto, int> _protoModule = new(ReferenceEqualityComparer.Instance);
            private readonly Dictionary<TernModule, int> _moduleIndex = new(ReferenceEqualityComparer.Instance);
            private readonly List<TernModule> _modules = new();

            public string Write(TernModule module)
            {
                // Module 0 is always the script that was run.
                _moduleIndex[module] = 0;
                _modules.Add(module);
                number(module.Entry, 0);

                var json = new StringBuilder(64 * 1024);
                json.Append("{\"version\":").Append(FormatVersion);
                json.Append(",\"module\":");
                quote(json, module.Name);

                // One entry per module: its globals, its exported names and the
                // prototype that runs its body.
                json.Append(",\"modules\":[");
                for (var m = 0; m < _modules.Count; m++)
                {
                    if (m > 0)
                    {
                        json.Append(',');
                    }

                    var entry = _modules[m];
                    json.Append("{\"name\":");
                    quote(json, entry.Name);
                    json.Append(",\"entry\":").Append(_index[entry.Entry]);
                    json.Append(",\"globals\":[");

                    var slots = entry.Scope.Slots;
                    for (var i = 0; i < slots.Count; i++)
                    {
                        if (i > 0)
                        {
                            json.Append(',');
                        }

                        quote(json, slots[i].Name);
                    }

                    json.Append("],\"exports\":[");
                    var firstExport = true;
                    for (var i = 0; i < slots.Count; i++)
                    {
                        if (slots[i].IsHost || !slots[i].Exported)
                        {
                            continue;
                        }

                        if (!firstExport)
                        {
                            json.Append(',');
                        }

                        firstExport = false;
                        json.Append("{\"name\":");
                        quote(json, slots[i].Name);
                        json.Append(",\"slot\":").Append(i).Append('}');
                    }

                    json.Append("]}");
                }

                json.Append(']').Append(",\"protos\":[");
                for (var i = 0; i < _protos.Count; i++)
                {
                    if (i > 0)
                    {
                        json.Append(',');
                    }

                    writeProto(json, _protos[i]);
                }

                json.Append("]}");
                return json.ToString();
            }

            /// <summary>Assigns a stable index to a prototype and everything it references.</summary>
            private int number(FunctionProto proto, int module)
            {
                if (_index.TryGetValue(proto, out var index))
                {
                    return index;
                }

                index = _protos.Count;
                _index[proto] = index;
                _protoModule[proto] = module;
                _protos.Add(proto);

                for (var i = 0; i < proto.Prototypes.Length; i++)
                {
                    if (proto.Prototypes[i] is { } child)
                    {
                        number(child, module);
                    }
                }

                for (var i = 0; i < proto.Constants.Length; i++)
                {
                    var constant = proto.Constants[i];
                    if (IsTypeConstant(constant, out var type))
                    {
                        foreach (var method in type.Methods.Values)
                        {
                            number(method.Proto, module);
                        }

                        for (var f = 0; f < type.DefaultPrototypes.Length; f++)
                        {
                            if (type.DefaultPrototypes[f] is { } defaultProto)
                            {
                                number(defaultProto, module);
                            }
                        }
                    }
                    else if (constant.Kind == ValueKind.Module)
                    {
                        numberModule(constant.AsModule());
                    }
                }

                return index;
            }

            /// <summary>Registers an imported module (and its whole body) for export.</summary>
            private int numberModule(TernModule module)
            {
                if (_moduleIndex.TryGetValue(module, out var index))
                {
                    return index;
                }

                index = _modules.Count;
                _moduleIndex[module] = index;
                _modules.Add(module);
                number(module.Entry, index);
                return index;
            }

            private static bool IsTypeConstant(in Value value, out TernType type)
            {
                if (value.Kind == ValueKind.Type)
                {
                    type = (TernType)value.AsObject()!;
                    return true;
                }

                type = null!;
                return false;
            }

            private void writeProto(StringBuilder json, FunctionProto proto)
            {
                json.Append("{\"module\":").Append(_protoModule.TryGetValue(proto, out var owner) ? owner : 0);
                json.Append(",\"name\":");
                if (proto.Name is null)
                {
                    json.Append("null");
                }
                else
                {
                    quote(json, proto.Name);
                }

                json.Append(",\"params\":").Append(proto.ParamCount);
                json.Append(",\"required\":").Append(proto.RequiredParams);
                json.Append(",\"rest\":").Append(proto.HasRest ? "true" : "false");
                json.Append(",\"method\":").Append(proto.IsMethod ? "true" : "false");
                json.Append(",\"locals\":").Append(proto.LocalCount);
                json.Append(",\"stack\":").Append(proto.MaxStack);

                json.Append(",\"upvalues\":[");
                for (var i = 0; i < proto.Upvalues.Length; i++)
                {
                    if (i > 0)
                    {
                        json.Append(',');
                    }

                    json.Append('[').Append(proto.Upvalues[i].FromLocal ? 1 : 0)
                        .Append(',').Append(proto.Upvalues[i].Index).Append(']');
                }

                json.Append(']');

                json.Append(",\"subs\":[");
                for (var i = 0; i < proto.Prototypes.Length; i++)
                {
                    if (i > 0)
                    {
                        json.Append(',');
                    }

                    json.Append(proto.Prototypes[i] is { } child && _index.TryGetValue(child, out var sub)
                        ? sub
                        : -1);
                }

                json.Append(']');

                json.Append(",\"consts\":[");
                for (var i = 0; i < proto.Constants.Length; i++)
                {
                    if (i > 0)
                    {
                        json.Append(',');
                    }

                    writeConstant(json, proto.Constants[i]);
                }

                json.Append(']');

                json.Append(",\"divs\":[");
                for (var i = 0; i < proto.Divisions.Length; i++)
                {
                    if (i > 0)
                    {
                        json.Append(',');
                    }

                    json.Append('[').Append(proto.Divisions[i].Divisor)
                        .Append(',').Append(proto.Divisions[i].Magic)
                        .Append(',').Append(proto.Divisions[i].Shift).Append(']');
                }

                json.Append(']');

                var instructions = proto.Instructions;

                // Line per instruction, so compiled binaries can report where an
                // uncaught error happened instead of only a function name.
                json.Append(",\"lines\":[");
                for (var i = 0; i < instructions.Length; i++)
                {
                    if (i > 0)
                    {
                        json.Append(',');
                    }

                    json.Append(i < proto.Lines.Length ? proto.Lines[i] : 0);
                }

                json.Append(']');

                json.Append(",\"code\":[");
                for (var i = 0; i < instructions.Length; i++)
                {
                    if (i > 0)
                    {
                        json.Append(',');
                    }

                    // Opcode names, not ordinals: adding an opcode to the C# enum must
                    // never silently shift the meaning of an exported program.
                    var instruction = instructions[i];
                    json.Append("[\"").Append(instruction.Op.ToString()).Append("\",")
                        .Append(instruction.A).Append(',')
                        .Append(instruction.B).Append(',')
                        .Append(instruction.C).Append(']');
                }

                json.Append("]}");
            }

            /// <summary>
            /// JSON text for a float constant that survives the trip through Python's
            /// json module. Bare "-0" would parse as the integer zero, losing the sign,
            /// so integral floats always carry a fractional part.
            /// </summary>
            private static string FloatText(double number)
            {
                if (double.IsNaN(number))
                {
                    return "NaN";
                }

                if (double.IsPositiveInfinity(number))
                {
                    return "Infinity";
                }

                if (double.IsNegativeInfinity(number))
                {
                    return "-Infinity";
                }

                var text = number.ToString("R", CultureInfo.InvariantCulture);
                if (text.IndexOfAny(new[] { '.', 'E', 'e' }) < 0)
                {
                    text += ".0";
                }

                return text;
            }

            private void writeConstant(StringBuilder json, in Value value)
            {
                switch (value.Kind)
                {
                    case ValueKind.Nil:
                        json.Append("{\"k\":\"nil\"}");
                        return;

                    case ValueKind.Bool:
                        json.Append("{\"k\":\"b\",\"v\":").Append(value.AsBool() ? "true" : "false").Append('}');
                        return;

                    case ValueKind.Int:
                        json.Append("{\"k\":\"i\",\"v\":").Append(value.AsInt()).Append('}');
                        return;

                    case ValueKind.Float:
                        json.Append("{\"k\":\"f\",\"v\":").Append(FloatText(value.AsDouble())).Append('}');
                        return;

                    case ValueKind.Str:
                        json.Append("{\"k\":\"s\",\"v\":");
                        quote(json, value.AsString());
                        json.Append('}');
                        return;

                    case ValueKind.Type when IsTypeConstant(value, out var type):
                        writeType(json, type);
                        return;

                    case ValueKind.Module:
                        json.Append("{\"k\":\"module\",\"mod\":")
                            .Append(numberModule(value.AsModule())).Append('}');
                        return;

                    case ValueKind.Native:
                        json.Append("{\"k\":\"native\",\"name\":");
                        quote(json, value.AsNative().Name);
                        json.Append('}');
                        return;

                    case ValueKind.Map when value.AsMap() is { } map:
                        json.Append("{\"k\":\"map\",\"entries\":[");
                        var firstEntry = true;
                        var keys = map.Keys();
                        for (var k = 0; k < keys.Count; k++)
                        {
                            if (!firstEntry)
                            {
                                json.Append(',');
                            }

                            firstEntry = false;
                            json.Append('[');
                            writeConstant(json, keys[k]);
                            json.Append(',');
                            writeConstant(json, map.GetOrNil(keys[k]));
                            json.Append(']');
                        }

                        json.Append("]}");
                        return;

                    default:
                        throw new NotSupportedException(
                            $"the native backend cannot export a {value.Kind} constant yet");
                }
            }

            private void writeType(StringBuilder json, TernType type)
            {
                json.Append("{\"k\":\"type\",\"name\":");
                quote(json, type.Name);

                json.Append(",\"fields\":[");
                for (var i = 0; i < type.FieldNames.Length; i++)
                {
                    if (i > 0)
                    {
                        json.Append(',');
                    }

                    quote(json, type.FieldNames[i]);
                }

                json.Append("],\"defaults\":[");
                for (var i = 0; i < type.Defaults.Length; i++)
                {
                    if (i > 0)
                    {
                        json.Append(',');
                    }

                    writeConstant(json, type.Defaults[i]);
                }

                json.Append("],\"defprotos\":[");
                for (var i = 0; i < type.DefaultPrototypes.Length; i++)
                {
                    if (i > 0)
                    {
                        json.Append(',');
                    }

                    json.Append(type.DefaultPrototypes[i] is { } proto && _index.TryGetValue(proto, out var index)
                        ? index
                        : -1);
                }

                json.Append("],\"methods\":[");
                var first = true;
                foreach (var (name, closure) in type.Methods)
                {
                    if (!first)
                    {
                        json.Append(',');
                    }

                    first = false;
                    json.Append('[');
                    quote(json, name);
                    json.Append(',').Append(_index.TryGetValue(closure.Proto, out var method) ? method : -1);
                    json.Append(']');
                }

                json.Append("]}");
            }

            private static void quote(StringBuilder json, string text)
            {
                json.Append('"');
                foreach (var c in text)
                {
                    switch (c)
                    {
                        case '"':
                            json.Append("\\\"");
                            break;
                        case '\\':
                            json.Append("\\\\");
                            break;
                        case '\n':
                            json.Append("\\n");
                            break;
                        case '\r':
                            json.Append("\\r");
                            break;
                        case '\t':
                            json.Append("\\t");
                            break;
                        default:
                            if (c < 0x20 || c > 0x7e)
                            {
                                json.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            }
                            else
                            {
                                json.Append(c);
                            }

                            break;
                    }
                }

                json.Append('"');
            }
        }
    }
}
