using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using TERN.Syntax;

namespace TERN.Ide
{
    /// <summary>One open file: its text, its version, and the parse of it.</summary>
    public sealed class Document
    {
        private int[] _lineStarts = Array.Empty<int>();

        public Document(string uri, string text, int version, string? path)
        {
            Uri = uri;
            Path = path;
            Update(text, version);
        }

        public string Uri { get; }

        public string? Path { get; }

        public string Text { get; private set; } = string.Empty;

        public int Version { get; private set; }

        /// <summary>When set, the last analysis produced errors and the AST is stale.</summary>
        public bool HasParseErrors { get; private set; }

        public BlockStmt? Program { get; private set; }

        public int LineCount => _lineStarts.Length;

        public void Update(string text, int version)
        {
            Text = text;
            Version = version;
            Reindex();
        }

        /// <summary>Re-parses. Cheap enough to do on every keystroke for a script-sized file.</summary>
        public void Analyze(TernEngine engine)
        {
            var diagnostics = new DiagnosticBag();
            Program = engine.Parse(Text, diagnostics, Path is null ? null : System.IO.Path.GetFileName(Path));
            HasParseErrors = diagnostics.HasErrors;
        }

        /// <summary>Line starts, so an offset becomes a position in O(log n).</summary>
        private void Reindex()
        {
            var starts = new List<int>(Math.Max(8, Text.Length / 32));
            starts.Add(0);
            for (var i = 0; i < Text.Length; i++)
            {
                if (Text[i] == '\n' && i + 1 <= Text.Length)
                {
                    starts.Add(i + 1);
                }
            }

            _lineStarts = starts.ToArray();
        }

        /// <summary>Zero-based (line, character), the way LSP counts.</summary>
        public (int Line, int Character) PositionAt(int offset)
        {
            if (offset <= 0)
            {
                return (0, 0);
            }

            if (offset > Text.Length)
            {
                offset = Text.Length;
            }

            var index = Array.BinarySearch(_lineStarts, offset);
            if (index < 0)
            {
                index = ~index - 1;
            }

            if (index < 0)
            {
                index = 0;
            }

            return (index, offset - _lineStarts[index]);
        }

        public int OffsetAt(int line, int character)
        {
            if (_lineStarts.Length == 0)
            {
                return 0;
            }

            if (line < 0)
            {
                return 0;
            }

            if (line >= _lineStarts.Length)
            {
                return Text.Length;
            }

            var start = _lineStarts[line];
            var end = line + 1 < _lineStarts.Length ? _lineStarts[line + 1] : Text.Length;
            return Math.Min(start + Math.Max(0, character), end);
        }

        public string LineText(int line)
        {
            if (line < 0 || line >= _lineStarts.Length)
            {
                return string.Empty;
            }

            var start = _lineStarts[line];
            var end = line + 1 < _lineStarts.Length ? _lineStarts[line + 1] : Text.Length;
            return Text.Substring(start, end - start).TrimEnd('\r', '\n');
        }

        /// <summary>The identifier characters around an offset.</summary>
        public (string Word, int Start, int End) WordAt(int offset)
        {
            var start = offset;
            while (start > 0 && IsIdentifier(Text[start - 1]))
            {
                start--;
            }

            var end = offset;
            while (end < Text.Length && IsIdentifier(Text[end]))
            {
                end++;
            }

            return (Text.Substring(start, end - start), start, end);
        }

        public static bool IsIdentifier(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

        public JsonObject ToRange(int startOffset, int endOffset)
        {
            var (line, character) = PositionAt(startOffset);
            var (endLine, endCharacter) = PositionAt(endOffset);
            return new JsonObject
            {
                ["start"] = new JsonObject { ["line"] = line, ["character"] = character },
                ["end"] = new JsonObject { ["line"] = endLine, ["character"] = endCharacter },
            };
        }

        public static string PathFromUri(string uri)
        {
            if (!uri.StartsWith("file:", StringComparison.Ordinal))
            {
                return uri;
            }

            var path = System.Uri.UnescapeDataString(uri.Substring("file:".Length));
            return path.TrimStart('/') is var relative && System.IO.Path.DirectorySeparatorChar == '/' ? "/" + relative : path;
        }

        public static string UriFromPath(string path)
        {
            var full = System.IO.Path.GetFullPath(path);
            return "file://" + full.Replace("\\", "/");
        }
    }
}
