using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using TERN.Builtins;
using TERN.Syntax;

namespace TERN.Ide
{
    /// <summary>
    /// The language features, as pure functions from a document and a position
    /// to protocol objects. Keeping them free of transport means the tests can
    /// call them directly and the server can answer requests in any order.
    /// </summary>
    public sealed class Features
    {
        /// <summary>Mirrors the lexer's keyword table; a language server needs it for completion.</summary>
        public static readonly string[] Keywords =
        {
            "let", "const", "fn", "return", "if", "else", "while", "for", "in", "break",
            "continue", "struct", "match", "try", "catch", "finally", "throw", "import",
            "as", "nil", "true", "false", "loop", "this", "do", "and", "or", "not", "typeof",
        };

        private readonly TernEngine _engine;
        private readonly Dictionary<string, List<BuiltinDoc>> _namespaces = new(StringComparer.Ordinal);
        private readonly List<BuiltinDoc> _globals = new();
        private readonly List<(string Kind, string Name, string? Documentation)> _methods = new();

        public Features(TernEngine engine)
        {
            _engine = engine;

            foreach (var entry in engine.DescribeStandardLibrary())
            {
                if (entry.Group == "global")
                {
                    _globals.Add(entry);
                    continue;
                }

                if (!_namespaces.TryGetValue(entry.Group, out var members))
                {
                    members = new List<BuiltinDoc>();
                    _namespaces[entry.Group] = members;
                }

                members.Add(entry);
            }

            if (BuiltinMethods.Describe() is { } methods)
            {
                foreach (var method in methods)
                {
                    _methods.Add((method.Kind, method.Name, method.Documentation));
                }
            }
        }

        public IReadOnlyCollection<string> NamespaceNames => _namespaces.Keys;

        // ------------------------------------------------------------ diagnostics

        public JsonArray Diagnostics(Document document)
        {
            var result = new JsonArray();
            var diagnostics = _engine.Check(document.Text, document.Path is null ? null : System.IO.Path.GetFileName(document.Path), document.Path);

            foreach (var diagnostic in diagnostics)
            {
                var start = Math.Min(diagnostic.Position.Offset, document.Text.Length);
                var (word, wordStart, wordEnd) = document.WordAt(start);
                if (word.Length == 0)
                {
                    wordStart = start;
                    wordEnd = Math.Min(start + 1, document.Text.Length);
                }

                result.Add(new JsonObject
                {
                    ["range"] = document.ToRange(wordStart, wordEnd),
                    ["severity"] = diagnostic.Severity switch
                    {
                        DiagnosticSeverity.Error => 1,
                        DiagnosticSeverity.Warning => 2,
                        _ => 3,
                    },
                    ["code"] = diagnostic.Code,
                    ["source"] = "TERN",
                    ["message"] = diagnostic.Message,
                });
            }

            return result;
        }

        // ------------------------------------------------------------- completion

        public JsonArray Completion(Document document, int offset, SymbolIndex index)
        {
            var items = new JsonArray();
            var (prefix, prefixStart, _) = document.WordAt(offset);
            var before = prefixStart > 0 ? document.Text[prefixStart - 1] : '\0';

            if (before == '.')
            {
                // Member access: work out what is on the left of the dot.
                var receiverEnd = prefixStart - 1;
                var (receiver, _, _) = document.WordAt(receiverEnd);
                CompleteMembers(document, index, receiver, items);
                return items;
            }

            foreach (var keyword in Keywords)
            {
                items.Add(Item(keyword, 14, "keyword", null, rank: 3));
            }

            foreach (var entry in _globals)
            {
                items.Add(Item(entry.Name, 3, Signature(entry), entry.Documentation, rank: 1, snippet: Snippet(entry)));
            }

            foreach (var name in _namespaces.Keys)
            {
                items.Add(Item(name, 9, $"namespace std:{name}", $"{name} standard library namespace", rank: 2));
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var symbol in index.Symbols)
            {
                if (!seen.Add(symbol.Name))
                {
                    continue;
                }

                items.Add(Item(symbol.Name, symbol.LspKind, symbol.Detail, symbol.KindName, rank: 0));
            }

            return items;
        }

        private void CompleteMembers(Document document, SymbolIndex index, string receiver, JsonArray items)
        {
            if (_namespaces.TryGetValue(receiver, out var members))
            {
                foreach (var entry in members)
                {
                    items.Add(entry.IsConstant
                        ? Item(entry.Name, 21, $"{receiver}.{entry.Name}", entry.Documentation, rank: 0)
                        : Item(entry.Name, 3, Signature(entry), entry.Documentation, rank: 0, snippet: Snippet(entry)));
                }

                return;
            }

            var type = index.TypeOf(receiver);
            if (type is not null)
            {
                foreach (var symbol in index.Symbols.Where(s => s.Name == type || s.Detail.StartsWith(type + ".", StringComparison.Ordinal)))
                {
                    // Fields and methods of the struct, minus the struct itself.
                    if (symbol.Kind is SymbolKind.Field || symbol.Kind is SymbolKind.Method)
                    {
                        items.Add(Item(symbol.Name, symbol.LspKind, symbol.Detail, symbol.KindName, rank: 0));
                    }
                }

                foreach (var symbol in index.Symbols.Where(s => s.Kind == SymbolKind.Method && s.Name != type))
                {
                    items.Add(Item(symbol.Name, 2, symbol.Detail, "method", rank: 1));
                }

                return;
            }

            // Type unknown: offer the built-in methods rather than nothing. They
            // are the only members a value can have that the server can see
            // without inference.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (kind, name, documentation) in _methods)
            {
                if (!seen.Add(name))
                {
                    continue;
                }

                items.Add(Item(name, 2, $"{kind}.{name}", documentation, rank: 2));
            }
        }

        private static string Signature(BuiltinDoc entry) => entry.IsConstant
            ? entry.Name
            : $"{entry.Name}({Arity(entry.MinArguments, entry.MaxArguments)})";

        private static string Arity(int min, int max) =>
            max < 0 ? "..." : min == max ? string.Join(", ", Enumerable.Repeat("arg", min)) : string.Join(", ", Enumerable.Repeat("arg", max));

        private static string? Snippet(BuiltinDoc entry) =>
            entry.IsConstant ? null : $"{entry.Name}({(entry.MinArguments > 0 ? "$1" : string.Empty)})";

        private static JsonObject Item(string label, int kind, string? detail, string? documentation, int rank, string? snippet = null)
        {
            var item = new JsonObject
            {
                ["label"] = label,
                ["kind"] = kind,
                ["sortText"] = $"{rank}{label}",
            };

            if (detail is not null)
            {
                item["detail"] = detail;
            }

            if (documentation is not null)
            {
                item["documentation"] = new JsonObject
                {
                    ["kind"] = "markdown",
                    ["value"] = documentation,
                };
            }

            if (snippet is not null && snippet.Contains('$'))
            {
                item["insertText"] = snippet;
                item["insertTextFormat"] = 2;
            }

            return item;
        }

        // ------------------------------------------------------------------ hover

        public JsonObject? Hover(Document document, int offset, SymbolIndex index)
        {
            var (word, start, end) = document.WordAt(offset);
            if (word.Length == 0)
            {
                return null;
            }

            var markdown = new List<string>();

            // A member of a namespace: `math.` then the name.
            var dot = start - 1;
            while (dot >= 0 && document.Text[dot] == '.')
            {
                var (receiver, _, _) = document.WordAt(dot - 1);
                if (_namespaces.TryGetValue(receiver, out var members))
                {
                    var entry = members.FirstOrDefault(m => m.Name == word);
                    if (entry is not null)
                    {
                        markdown.Add($"```tern\n{receiver}.{Signature(entry)}\n```");
                        if (entry.Documentation is { } text)
                        {
                            markdown.Add(text);
                        }

                        return Markdown(document, start, end, markdown);
                    }
                }

                break;
            }

            var global = _globals.FirstOrDefault(g => g.Name == word);
            if (global is not null)
            {
                markdown.Add($"```tern\n{Signature(global)}\n```");
                if (global.Documentation is { } text)
                {
                    markdown.Add(text);
                }

                return Markdown(document, start, end, markdown);
            }

            if (_namespaces.ContainsKey(word))
            {
                markdown.Add($"```tern\nnamespace std:{word}\n```");
                markdown.Add($"Standard library namespace with {_namespaces[word].Count} member(s).");
                return Markdown(document, start, end, markdown);
            }

            var symbol = index.Resolve(word, start);
            if (symbol is not null)
            {
                markdown.Add($"```tern\n{symbol.Detail}\n```");
                markdown.Add($"{symbol.KindName} declared on line {symbol.Line}");
                return Markdown(document, start, end, markdown);
            }

            return null;
        }

        private static JsonObject Markdown(Document document, int start, int end, List<string> lines) => new()
        {
            ["contents"] = new JsonObject
            {
                ["kind"] = "markdown",
                ["value"] = string.Join("\n\n", lines),
            },
            ["range"] = document.ToRange(start, end),
        };

        // ------------------------------------------------------------- definition

        public JsonObject? Definition(Document document, int offset, SymbolIndex index)
        {
            var (word, start, end) = document.WordAt(offset);
            if (word.Length == 0)
            {
                return null;
            }

            var symbol = index.Resolve(word, start);
            if (symbol is null)
            {
                return null;
            }

            var nameEnd = Math.Min(symbol.Offset + word.Length, document.Text.Length);
            return new JsonObject
            {
                ["uri"] = document.Uri,
                ["range"] = document.ToRange(symbol.Offset, nameEnd),
            };
        }

        // --------------------------------------------------------------- rename

        /// <summary>
        /// Every occurrence of the identifier at <paramref name="offset"/>, for
        /// highlighting and for find-usages.
        /// </summary>
        public JsonArray References(Document document, int offset, SymbolIndex symbols, DeepIndex deep,
            bool includeDeclaration)
        {
            var (word, start, _) = document.WordAt(offset);
            var result = new JsonArray();
            if (word.Length == 0)
            {
                return result;
            }

            var declarationStart = symbols.Symbols.FirstOrDefault(s => s.Name == word)?.Offset ?? -1;
            foreach (var (at, length) in deep.Occurrences(word, symbols))
            {
                if (!includeDeclaration && at == declarationStart)
                {
                    continue;
                }

                result.Add(new JsonObject
                {
                    ["uri"] = document.Uri,
                    ["range"] = document.ToRange(at, at + length),
                });
            }

            return result;
        }

        /// <summary>
        /// A workspace edit renaming the identifier at <paramref name="offset"/>.
        /// Refused — with a reason — when the file declares the name more than
        /// once, because references are matched by name and a rename cannot tell
        /// a shadowed variable from the one the user picked.
        /// </summary>
        public (JsonObject? Edit, string? Refusal) Rename(Document document, int offset, string newName,
            SymbolIndex symbols, DeepIndex deep)
        {
            var (word, _, _) = document.WordAt(offset);
            if (word.Length == 0)
            {
                return (null, "there is no identifier under the cursor");
            }

            if (newName.Length == 0 || !Document.IsIdentifier(newName[0]) || char.IsDigit(newName[0]) ||
                !newName.All(Document.IsIdentifier))
            {
                return (null, $"'{newName}' is not a valid TERN identifier");
            }

            if (Keywords.Contains(newName))
            {
                return (null, $"'{newName}' is a keyword");
            }

            var declarations = symbols.Symbols.Count(s => s.Name == word && s.Kind is not SymbolKind.Parameter);
            if (declarations > 1)
            {
                return (null, $"'{word}' is declared {declarations} times in this file; renaming by name would move the wrong ones");
            }

            var edits = new JsonArray();
            foreach (var (at, length) in deep.Occurrences(word, symbols))
            {
                edits.Add(new JsonObject
                {
                    ["range"] = document.ToRange(at, at + length),
                    ["newText"] = newName,
                });
            }

            if (edits.Count == 0)
            {
                return (null, $"'{word}' is not used in this file");
            }

            return (new JsonObject
            {
                ["changes"] = new JsonObject
                {
                    [document.Uri] = edits,
                },
            }, null);
        }

        // --------------------------------------------------------------- format

        /// <summary>
        /// A conservative re-indent: it only changes leading whitespace, and only
        /// on lines that are not blank and not inside a multi-line string. The
        /// rest of the file is returned byte for byte, so formatting never
        /// rewrites code the user wrote deliberately.
        /// </summary>
        public static string Format(Document document, int indentWidth = 4)
        {
            var lines = document.Text.Split('\n');
            var builder = new System.Text.StringBuilder(document.Text.Length + 64);
            var depth = 0;
            var inBlockComment = false;
            var inTemplate = false;

            foreach (var raw in lines)
            {
                var line = raw.TrimEnd('\r');
                var trimmed = line.TrimStart();
                var indentText = line.Substring(0, line.Length - trimmed.Length);

                if (trimmed.Length == 0)
                {
                    builder.Append(raw).Append('\n');
                    continue;
                }

                // A line that starts with a closer belongs one level out.
                var leadingClosers = 0;
                foreach (var c in trimmed)
                {
                    if (c is '}' or ')' or ']')
                    {
                        leadingClosers++;
                    }
                    else
                    {
                        break;
                    }
                }

                var target = Math.Max(0, depth - leadingClosers);
                builder.Append(new string(' ', target * indentWidth)).Append(trimmed).Append('\n');

                // Track depth from the code on this line, ignoring comments and
                // string bodies so a brace in prose does not shift the file.
                var (opens, closes) = CountBraces(trimmed, ref inBlockComment, ref inTemplate);
                depth = Math.Max(0, depth + opens - closes);
                _ = indentText;
            }

            var text = builder.ToString();
            if (document.Text.EndsWith("\n") || !text.EndsWith("\n"))
            {
                return text;
            }

            return text.Substring(0, text.Length - 1);
        }

        private static (int Opens, int Closes) CountBraces(string line, ref bool inBlockComment, ref bool inTemplate)
        {
            var opens = 0;
            var closes = 0;
            var inString = false;
            var quote = '\0';

            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];

                if (inBlockComment)
                {
                    if (c == '*' && i + 1 < line.Length && line[i + 1] == '/')
                    {
                        inBlockComment = false;
                        i++;
                    }

                    continue;
                }

                if (inString)
                {
                    if (c == '\\' && i + 1 < line.Length)
                    {
                        i++;
                        continue;
                    }

                    if (c == quote)
                    {
                        inString = false;
                    }

                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    inString = true;
                    quote = c;
                    continue;
                }

                if (c == '/' && i + 1 < line.Length)
                {
                    if (line[i + 1] == '/')
                    {
                        break;
                    }

                    if (line[i + 1] == '*')
                    {
                        inBlockComment = true;
                        i++;
                        continue;
                    }
                }

                switch (c)
                {
                    case '{':
                    case '(':
                    case '[':
                        opens++;
                        break;
                    case '}':
                    case ')':
                    case ']':
                        closes++;
                        break;
                }
            }

            _ = inTemplate;
            return (opens, closes);
        }

        // --------------------------------------------------------- documentSymbol

        public JsonArray DocumentSymbols(Document document, SymbolIndex index)
        {
            var result = new JsonArray();
            foreach (var symbol in index.Symbols)
            {
                if (symbol.Kind is SymbolKind.Parameter or SymbolKind.Field or SymbolKind.Method)
                {
                    // Nested members are listed by the structure view but not by
                    // LSP's flat outline; keeping the outline to top-level
                    // declarations matches what every other editor shows.
                    continue;
                }

                var nameEnd = Math.Min(symbol.Offset + symbol.Name.Length, document.Text.Length);
                result.Add(new JsonObject
                {
                    ["name"] = symbol.Name,
                    ["detail"] = symbol.Detail,
                    ["kind"] = symbol.LspKind,
                    ["range"] = document.ToRange(symbol.Offset, nameEnd),
                    ["selectionRange"] = document.ToRange(symbol.Offset, nameEnd),
                });
            }

            return result;
        }

        public JsonArray WorkspaceSymbols(string query, IEnumerable<(Document Document, SymbolIndex Index)> documents)
        {
            var result = new JsonArray();
            var needle = query.Trim();

            foreach (var (document, index) in documents)
            {
                foreach (var symbol in index.Symbols)
                {
                    if (symbol.Kind is SymbolKind.Parameter)
                    {
                        continue;
                    }

                    if (needle.Length > 0 && symbol.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    result.Add(new JsonObject
                    {
                        ["name"] = symbol.Name,
                        ["kind"] = symbol.LspKind,
                        ["location"] = new JsonObject
                        {
                            ["uri"] = document.Uri,
                            ["range"] = document.ToRange(symbol.Offset, symbol.Offset + symbol.Name.Length),
                        },
                    });
                }
            }

            return result;
        }
    }
}
