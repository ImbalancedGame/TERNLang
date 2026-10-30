using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace TERN.Ide
{
    /// <summary>
    /// The TERN syntax server: a Language Server Protocol implementation over
    /// stdio.
    ///
    /// It exists as a separate process on purpose. Rider-like IDEs put a
    /// language server behind a process boundary so that analysis can crash,
    /// restart or be swapped without taking the editor with it, and so that any
    /// editor — not only the one shipped here — can drive it. The protocol is
    /// LSP 3.17; the client is the Qt application in <c>ide/Studio</c>.
    ///
    /// Everything the server knows comes from the compiler in <c>TERN/</c>: the
    /// parser for the AST, the analyzer and compiler for diagnostics, and the
    /// engine's own catalogue for completions and hover text. Nothing about the
    /// language is reimplemented here, which is what keeps the editor and the
    /// interpreter honest with each other.
    /// </summary>
    public static class Program
    {
        private static readonly Dictionary<string, Document> Documents = new(StringComparer.Ordinal);

        private static TernEngine _engine = null!;
        private static Features _features = null!;
        private static TextWriter _log = TextWriter.Null;

        public static int Main(string[] args)
        {
            var input = Console.OpenStandardInput();
            var output = Console.OpenStandardOutput();

            foreach (var argument in args)
            {
                if (argument.StartsWith("--log=", StringComparison.Ordinal))
                {
                    _log = new StreamWriter(argument.Substring("--log=".Length), append: false) { AutoFlush = true };
                }
            }

            _engine = new TernEngine();
            _features = new Features(_engine);

            try
            {
                Serve(input, output);
            }
            catch (Exception error)
            {
                _log.WriteLine($"[fatal] {error}");
                return 1;
            }
            finally
            {
                _engine.Dispose();
                _log.Flush();
            }

            return 0;
        }

        private static void Serve(Stream input, Stream output)
        {
            for (;;)
            {
                JsonNode? message;
                try
                {
                    message = Rpc.Read(input);
                }
                catch (Exception error)
                {
                    _log.WriteLine($"[read] {error.Message}");
                    return;
                }

                if (message is null)
                {
                    return;
                }

                var method = message["method"]?.GetValue<string>();
                if (method is null)
                {
                    // A response to something we sent; the server sends no requests.
                    continue;
                }

                var id = message["id"];
                var parameters = message["params"] as JsonObject ?? new JsonObject();
                _log.WriteLine($"[recv] {method}");

                JsonNode? result;
                try
                {
                    result = Handle(method, parameters);
                }
                catch (Exception error)
                {
                    _log.WriteLine($"[error] {method}: {error}");
                    if (id is not null)
                    {
                        Rpc.Write(output, new JsonObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = id.DeepClone(),
                            ["error"] = new JsonObject
                            {
                                ["code"] = -32603,
                                ["message"] = error.Message,
                            },
                        });
                    }

                    continue;
                }

                if (id is null)
                {
                    continue;
                }

                var response = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id.DeepClone(),
                };

                if (result is null)
                {
                    response["result"] = null;
                }
                else
                {
                    response["result"] = result;
                }

                Rpc.Write(output, response);
            }
        }

        private static JsonNode? Handle(string method, JsonObject parameters)
        {
            switch (method)
            {
                case "initialize":
                    return new JsonObject
                    {
                        ["capabilities"] = new JsonObject
                        {
                            ["textDocumentSync"] = new JsonObject
                            {
                                ["openClose"] = true,
                                ["change"] = 1,       // full text: the analyzer wants the whole buffer
                                ["save"] = new JsonObject { ["includeText"] = false },
                            },
                            ["completionProvider"] = new JsonObject
                            {
                                ["triggerCharacters"] = new JsonArray(".", "\""),
                                ["resolveProvider"] = false,
                            },
                            ["hoverProvider"] = true,
                            ["definitionProvider"] = true,
                            ["referencesProvider"] = true,
                            ["documentSymbolProvider"] = true,
                            ["workspaceSymbolProvider"] = true,
                            ["documentFormattingProvider"] = true,
                            ["renameProvider"] = new JsonObject { ["prepareProvider"] = true },
                        },
                        ["serverInfo"] = new JsonObject
                        {
                            ["name"] = "tern-ls",
                            ["version"] = "1.0",
                        },
                    };

                case "initialized":
                case "shutdown":
                case "$/cancelRequest":
                case "textDocument/didSave":
                    return null;

                case "exit":
                    Environment.Exit(0);
                    return null;

                case "textDocument/didOpen":
                {
                    var document = parameters["textDocument"]!;
                    var uri = document["uri"]!.GetValue<string>();
                    var text = document["text"]!.GetValue<string>();
                    var version = document["version"]?.GetValue<int>() ?? 1;
                    var store = new Document(uri, text, version, Document.PathFromUri(uri));
                    store.Analyze(_engine);
                    Documents[uri] = store;
                    return null;
                }

                case "textDocument/didChange":
                {
                    var uri = parameters["textDocument"]!["uri"]!.GetValue<string>();
                    if (!Documents.TryGetValue(uri, out var document))
                    {
                        return null;
                    }

                    var change = parameters["contentChanges"]!.AsArray()[^1]!;
                    document.Update(change["text"]!.GetValue<string>(), parameters["textDocument"]!["version"]?.GetValue<int>() ?? document.Version + 1);
                    document.Analyze(_engine);
                    return null;
                }

                case "textDocument/didClose":
                {
                    var uri = parameters["textDocument"]!["uri"]!.GetValue<string>();
                    Documents.Remove(uri);
                    return null;
                }

                case "textDocument/diagnostic":
                case "textDocument/check":
                {
                    var document = Find(parameters);
                    return document is null ? null : _features.Diagnostics(document);
                }

                case "textDocument/completion":
                {
                    var (document, offset) = Locate(parameters);
                    if (document is null)
                    {
                        return null;
                    }

                    return new JsonObject
                    {
                        ["isIncomplete"] = false,
                        ["items"] = _features.Completion(document, offset, SymbolIndex.Build(document)),
                    };
                }

                case "textDocument/hover":
                {
                    var (document, offset) = Locate(parameters);
                    return document is null ? null : _features.Hover(document, offset, SymbolIndex.Build(document));
                }

                case "textDocument/definition":
                {
                    var (document, offset) = Locate(parameters);
                    return document is null ? null : _features.Definition(document, offset, SymbolIndex.Build(document));
                }

                case "textDocument/references":
                {
                    var (document, offset) = Locate(parameters);
                    if (document is null)
                    {
                        return null;
                    }

                    var symbols = SymbolIndex.Build(document);
                    var deep = DeepIndex.Build(document, symbols);
                    var includeDeclaration = parameters["context"]?["includeDeclaration"]?.GetValue<bool>() ?? true;
                    return _features.References(document, offset, symbols, deep, includeDeclaration);
                }

                case "textDocument/prepareRename":
                {
                    var (document, offset) = Locate(parameters);
                    if (document is null)
                    {
                        return null;
                    }

                    var (word, start, end) = document.WordAt(offset);
                    return word.Length == 0
                        ? null
                        : new JsonObject
                        {
                            ["range"] = document.ToRange(start, end),
                            ["placeholder"] = word,
                        };
                }

                case "textDocument/rename":
                {
                    var (document, offset) = Locate(parameters);
                    if (document is null)
                    {
                        return null;
                    }

                    var newName = parameters["newName"]!.GetValue<string>();
                    var symbols = SymbolIndex.Build(document);
                    var deep = DeepIndex.Build(document, symbols);
                    var (edit, refusal) = _features.Rename(document, offset, newName, symbols, deep);
                    if (edit is null)
                    {
                        throw new InvalidOperationException(refusal ?? "rename refused");
                    }

                    return edit;
                }

                case "textDocument/documentSymbol":
                {
                    var document = Find(parameters);
                    return document is null ? null : _features.DocumentSymbols(document, SymbolIndex.Build(document));
                }

                case "workspace/symbol":
                {
                    var query = parameters["query"]?.GetValue<string>() ?? string.Empty;
                    var all = Documents.Values.Select(d => (d, SymbolIndex.Build(d)));
                    return _features.WorkspaceSymbols(query, all);
                }

                case "textDocument/formatting":
                {
                    var document = Find(parameters);
                    if (document is null)
                    {
                        return null;
                    }

                    var formatted = Features.Format(document);
                    if (formatted == document.Text)
                    {
                        return new JsonArray();
                    }

                    var lastLine = document.LineCount;
                    return new JsonArray
                    {
                        new JsonObject
                        {
                            ["range"] = new JsonObject
                            {
                                ["start"] = new JsonObject { ["line"] = 0, ["character"] = 0 },
                                ["end"] = new JsonObject { ["line"] = lastLine, ["character"] = 0 },
                            },
                            ["newText"] = formatted,
                        },
                    };
                }

                case "workspace/executeCommand":
                    return null;

                default:
                    _log.WriteLine($"[skip] {method}");
                    return null;
            }
        }

        private static Document? Find(JsonObject parameters)
        {
            var uri = parameters["textDocument"]?["uri"]?.GetValue<string>();
            return uri is not null && Documents.TryGetValue(uri, out var document) ? document : null;
        }

        private static (Document? Document, int Offset) Locate(JsonObject parameters)
        {
            var document = Find(parameters);
            if (document is null)
            {
                return (null, 0);
            }

            var position = parameters["position"];
            var line = position?["line"]?.GetValue<int>() ?? 0;
            var character = position?["character"]?.GetValue<int>() ?? 0;
            return (document, document.OffsetAt(line, character));
        }
    }
}
