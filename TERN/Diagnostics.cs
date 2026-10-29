using System;
using System.Collections.Generic;
using System.Text;

namespace TERN
{
    /// <summary>A position in a TERN source file.</summary>
    public readonly struct SourcePosition
    {
        public readonly int Offset;
        public readonly int Line;
        public readonly int Column;

        public SourcePosition(int offset, int line, int column)
        {
            Offset = offset;
            Line = line;
            Column = column;
        }

        public override string ToString() => Column > 0 ? $"{Line}:{Column}" : $"{Line}";
    }

    public enum DiagnosticSeverity
    {
        Info,
        Warning,
        Error,
    }

    /// <summary>A single compiler or runtime message.</summary>
    public sealed class Diagnostic
    {
        public DiagnosticSeverity Severity { get; }
        public string Code { get; }
        public string Message { get; }
        public SourcePosition Position { get; }
        public string? SourceName { get; }

        public Diagnostic(DiagnosticSeverity severity, string code, string message, SourcePosition position, string? sourceName)
        {
            Severity = severity;
            Code = code;
            Message = message;
            Position = position;
            SourceName = sourceName;
        }

        public string Format()
        {
            var where = SourceName is null ? Position.ToString() : $"{SourceName}:{Position}";
            var kind = Severity switch
            {
                DiagnosticSeverity.Error => "error",
                DiagnosticSeverity.Warning => "warning",
                _ => "info",
            };
            return $"{where}: {kind} {Code}: {Message}";
        }

        public override string ToString() => Format();
    }

    /// <summary>Collects diagnostics emitted by the front end.</summary>
    public sealed class DiagnosticBag
    {
        private readonly List<Diagnostic> _items = new();
        private readonly List<string> _sources = new();

        public IReadOnlyList<Diagnostic> Items => _items;
        public bool HasErrors { get; private set; }
        public int Count => _items.Count;

        public void Error(string code, string message, SourcePosition position, string? source = null)
        {
            HasErrors = true;
            _items.Add(new Diagnostic(DiagnosticSeverity.Error, code, message, position, source));
        }

        public void Warning(string code, string message, SourcePosition position, string? source = null)
        {
            _items.Add(new Diagnostic(DiagnosticSeverity.Warning, code, message, position, source));
        }

        public void ErrorAt(string code, string message, int offset)
        {
            Error(code, message, new SourcePosition(offset, 1, offset + 1));
        }

        public void WriteTo(Action<string> sink)
        {
            foreach (var diagnostic in _items)
            {
                sink(diagnostic.Format());
            }
        }
    }

    /// <summary>Thrown by the host when a script makes an uncaught error.</summary>
    public sealed class TernError : Exception
    {
        public string ErrorType { get; }
        public IReadOnlyList<string> Trace { get; }
        public Runtime.Value Payload { get; }

        public TernError(string errorType, string message, IReadOnlyList<string>? trace = null,
            Runtime.Value payload = default)
            : base(message)
        {
            ErrorType = errorType;
            Trace = trace ?? Array.Empty<string>();
            Payload = payload.Kind is Runtime.ValueKind.Missing or Runtime.ValueKind.Nil
                ? Runtime.Value.Str(message)
                : payload;
        }

        public string Describe()
        {
            var builder = new StringBuilder();
            builder.Append(ErrorType).Append(": ").Append(Message);

            foreach (var frame in Trace)
            {
                builder.Append('\n').Append("  at ").Append(frame);
            }

            return builder.ToString();
        }
    }

    /// <summary>Thrown by the compiler when a script cannot be translated to bytecode.</summary>
    public sealed class TernCompileException : Exception
    {
        public IReadOnlyList<Diagnostic> Diagnostics { get; }

        public TernCompileException(IReadOnlyList<Diagnostic> diagnostics)
            : base(Build(diagnostics))
        {
            Diagnostics = diagnostics;
        }

        private static string Build(IReadOnlyList<Diagnostic> diagnostics)
        {
            var builder = new StringBuilder();
            foreach (var diagnostic in diagnostics)
            {
                if (diagnostic.Severity != DiagnosticSeverity.Error)
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append('\n');
                }

                builder.Append(diagnostic.Format());
            }

            return builder.Length == 0 ? "compilation failed" : builder.ToString();
        }
    }
}
