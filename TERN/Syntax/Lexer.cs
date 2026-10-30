using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TERN.Syntax
{
    public enum TokenKind
    {
        EndOfFile,
        Error,

        Identifier,
        Number,
        String,

        // literals & keywords
        Let,
        Const,
        Fn,
        Return,
        If,
        Else,
        While,
        For,
        In,
        Break,
        Continue,
        Struct,
        Match,
        Try,
        Catch,
        Finally,
        Throw,
        Import,
        As,
        Nil,
        True,
        False,
        Loop,
        This,
        Do,
        And,
        Or,
        Not,
        TypeOf,

        // punctuation
        LeftParen,
        RightParen,
        LeftBrace,
        RightBrace,
        LeftBracket,
        RightBracket,
        Comma,
        Semicolon,
        Colon,
        Dot,
        Arrow,        // =>
        DotDot,       // ..
        DotDotLess,   // ..<
        DotDotDot,    // ...
        Question,
        QuestionQuestion,
        QuestionDot,
        At,

        // operators
        Plus,
        Minus,
        Star,
        Slash,
        Percent,
        StarStar,
        Equal,
        EqualEqual,
        BangEqual,
        Less,
        LessEqual,
        Greater,
        GreaterEqual,
        Bang,
        AmpAmp,
        PipePipe,
        Amp,
        Pipe,
        Caret,
        Tilde,
        ShiftLeft,
        ShiftRight,

        // compound assignment
        PlusEqual,
        MinusEqual,
        StarEqual,
        SlashEqual,
        PercentEqual,
        StarStarEqual,
        AmpEqual,
        PipeEqual,
        CaretEqual,
        ShiftLeftEqual,
        ShiftRightEqual,
        PlusPlus,
        MinusMinus,
    }

    /// <summary>One part of an interpolated string literal.</summary>
    public readonly struct StringPart
    {
        public readonly string Text;
        public readonly bool IsExpression;
        public readonly int Offset;

        public StringPart(string text, bool isExpression, int offset)
        {
            Text = text;
            IsExpression = isExpression;
            Offset = offset;
        }
    }

    public readonly struct Token
    {
        public readonly TokenKind Kind;
        public readonly string Text;
        public readonly int Offset;
        public readonly int Line;
        public readonly int Column;
        public readonly object? Literal;
        public readonly List<StringPart>? Parts;

        public Token(TokenKind kind, string text, int offset, int line, int column,
            object? literal = null, List<StringPart>? parts = null)
        {
            Kind = kind;
            Text = text;
            Offset = offset;
            Line = line;
            Column = column;
            Literal = literal;
            Parts = parts;
        }

        public SourcePosition Position => new(Offset, Line, Column);

        public override string ToString() => $"{Kind} '{Text}'";
    }

    /// <summary>
    /// Hand written lexer. It is allocation-light: text is stored as a string slice
    /// only when a token actually needs one, and interpolated strings are split into
    /// literal/expression parts so the parser can re-enter it for sub-expressions.
    /// </summary>
    public sealed class Lexer
    {
        private static readonly Dictionary<string, TokenKind> Keywords = new(StringComparer.Ordinal)
        {
            ["let"] = TokenKind.Let,
            ["const"] = TokenKind.Const,
            ["fn"] = TokenKind.Fn,
            ["return"] = TokenKind.Return,
            ["if"] = TokenKind.If,
            ["else"] = TokenKind.Else,
            ["while"] = TokenKind.While,
            ["for"] = TokenKind.For,
            ["in"] = TokenKind.In,
            ["break"] = TokenKind.Break,
            ["continue"] = TokenKind.Continue,
            ["struct"] = TokenKind.Struct,
            ["match"] = TokenKind.Match,
            ["try"] = TokenKind.Try,
            ["catch"] = TokenKind.Catch,
            ["finally"] = TokenKind.Finally,
            ["throw"] = TokenKind.Throw,
            ["import"] = TokenKind.Import,
            ["as"] = TokenKind.As,
            ["nil"] = TokenKind.Nil,
            ["true"] = TokenKind.True,
            ["false"] = TokenKind.False,
            ["loop"] = TokenKind.Loop,
            ["this"] = TokenKind.This,
            ["do"] = TokenKind.Do,
            ["and"] = TokenKind.And,
            ["or"] = TokenKind.Or,
            ["not"] = TokenKind.Not,
            ["typeof"] = TokenKind.TypeOf,
        };

        private readonly string _source;
        private readonly string? _name;
        private readonly DiagnosticBag _diagnostics;
        private readonly List<Token> _tokens = new();

        private int _offset;
        private int _line = 1;
        private int _column = 1;

        public Lexer(string source, DiagnosticBag diagnostics, string? name = null)
        {
            _source = source;
            _diagnostics = diagnostics;
            _name = name;
        }

        public static List<Token> Tokenize(string source, DiagnosticBag diagnostics, string? name = null)
            => new Lexer(source, diagnostics, name).Run();

        private bool AtEnd => _offset >= _source.Length;

        private char Current => _offset < _source.Length ? _source[_offset] : '\0';

        private char Peek(int distance = 1)
        {
            var index = _offset + distance;
            return index < _source.Length ? _source[index] : '\0';
        }

        private void Advance()
        {
            if (AtEnd)
            {
                return;
            }

            if (_source[_offset] == '\n')
            {
                _line++;
                _column = 1;
            }
            else
            {
                _column++;
            }

            _offset++;
        }

        public List<Token> Run()
        {
            while (true)
            {
                SkipTrivia();

                if (AtEnd)
                {
                    Emit(TokenKind.EndOfFile, string.Empty, _offset, _line, _column);
                    return _tokens;
                }

                var start = _offset;
                var startLine = _line;
                var startColumn = _column;

                var c = Current;

                if (c == '\0')
                {
                    Emit(TokenKind.EndOfFile, string.Empty, start, startLine, startColumn);
                    return _tokens;
                }

                if (char.IsLetter(c) || c == '_')
                {
                    LexIdentifier(start, startLine, startColumn);
                    continue;
                }

                if (char.IsDigit(c))
                {
                    LexNumber(start, startLine, startColumn);
                    continue;
                }

                if (c == '"')
                {
                    LexString(start, startLine, startColumn);
                    continue;
                }

                if (LexOperator(start, startLine, startColumn))
                {
                    continue;
                }

                _diagnostics.Error("TRN0001", $"unexpected character '{c}'",
                    new SourcePosition(start, startLine, startColumn), _name);
                Advance();
            }
        }

        private void SkipTrivia()
        {
            while (!AtEnd)
            {
                var c = Current;

                if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\f' || c == '\v')
                {
                    Advance();
                    continue;
                }

                if (c == '/' && Peek() == '/')
                {
                    while (!AtEnd && Current != '\n')
                    {
                        Advance();
                    }

                    continue;
                }

                if (c == '/' && Peek() == '*')
                {
                    var start = _offset;
                    var startLine = _line;
                    var startColumn = _column;
                    Advance();
                    Advance();

                    var depth = 1;
                    while (!AtEnd && depth > 0)
                    {
                        if (Current == '/' && Peek() == '*')
                        {
                            depth++;
                            Advance();
                            Advance();
                            continue;
                        }

                        if (Current == '*' && Peek() == '/')
                        {
                            depth--;
                            Advance();
                            Advance();
                            continue;
                        }

                        Advance();
                    }

                    if (depth > 0)
                    {
                        _diagnostics.Error("TRN0002", "unterminated block comment",
                            new SourcePosition(start, startLine, startColumn), _name);
                    }

                    continue;
                }

                break;
            }
        }

        private void Emit(TokenKind kind, string text, int offset, int line, int column,
            object? literal = null, List<StringPart>? parts = null)
        {
            _tokens.Add(new Token(kind, text, offset, line, column, literal, parts));
        }

        private string Slice(int start) => _source.Substring(start, _offset - start);

        private void LexIdentifier(int start, int line, int column)
        {
            while (!AtEnd && (char.IsLetterOrDigit(Current) || Current == '_'))
            {
                Advance();
            }

            var text = Slice(start);

            if (Keywords.TryGetValue(text, out var keyword))
            {
                Emit(keyword, text, start, line, column);
                return;
            }

            Emit(TokenKind.Identifier, text, start, line, column);
        }

        private void LexNumber(int start, int line, int column)
        {
            var isFloat = false;

            if (Current == '0' && (Peek() == 'x' || Peek() == 'X'))
            {
                Advance();
                Advance();
                var digitsStart = _offset;
                while (!AtEnd && (Uri.IsHexDigit(Current) || Current == '_'))
                {
                    Advance();
                }

                var hex = _source.AsSpan(digitsStart, _offset - digitsStart).ToString().Replace("_", string.Empty);
                if (hex.Length == 0)
                {
                    _diagnostics.Error("TRN0003", "invalid hexadecimal literal",
                        new SourcePosition(start, line, column), _name);
                    Emit(TokenKind.Number, "0", start, line, column, 0L);
                    return;
                }

                EmitInteger(start, line, column, hex, 16);
                return;
            }

            if (Current == '0' && (Peek() == 'b' || Peek() == 'B'))
            {
                Advance();
                Advance();
                var digitsStart = _offset;
                while (!AtEnd && (Current == '0' || Current == '1' || Current == '_'))
                {
                    Advance();
                }

                var bits = _source.AsSpan(digitsStart, _offset - digitsStart).ToString().Replace("_", string.Empty);
                if (bits.Length == 0)
                {
                    _diagnostics.Error("TRN0004", "invalid binary literal",
                        new SourcePosition(start, line, column), _name);
                    Emit(TokenKind.Number, "0", start, line, column, 0L);
                    return;
                }

                EmitInteger(start, line, column, bits, 2);
                return;
            }

            while (!AtEnd && (char.IsDigit(Current) || Current == '_'))
            {
                Advance();
            }

            if (Current == '.' && char.IsDigit(Peek()))
            {
                isFloat = true;
                Advance();
                while (!AtEnd && (char.IsDigit(Current) || Current == '_'))
                {
                    Advance();
                }
            }

            if (Current == 'e' || Current == 'E')
            {
                var save = _offset;
                Advance();
                if (Current == '+' || Current == '-')
                {
                    Advance();
                }

                if (char.IsDigit(Current))
                {
                    isFloat = true;
                    while (!AtEnd && char.IsDigit(Current))
                    {
                        Advance();
                    }
                }
                else
                {
                    // Not an exponent after all; rewind.
                    _offset = save;
                }
            }

            var raw = Slice(start).Replace("_", string.Empty);

            if (isFloat)
            {
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    _diagnostics.Error("TRN0005", $"invalid number literal '{raw}'",
                        new SourcePosition(start, line, column), _name);
                    value = 0;
                }

                Emit(TokenKind.Number, raw, start, line, column, value);
                return;
            }

            if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            {
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var fallback))
                {
                    Emit(TokenKind.Number, raw, start, line, column, fallback);
                    return;
                }

                _diagnostics.Error("TRN0005", $"integer literal '{raw}' is out of range",
                    new SourcePosition(start, line, column), _name);
                integer = 0;
            }

            Emit(TokenKind.Number, raw, start, line, column, integer);
        }

        private void EmitInteger(int start, int line, int column, string digits, int radix)
        {
            try
            {
                var value = Convert.ToInt64(digits, radix);
                Emit(TokenKind.Number, Slice(start), start, line, column, value);
            }
            catch (Exception)
            {
                _diagnostics.Error("TRN0006", "integer literal is out of range",
                    new SourcePosition(start, line, column), _name);
                Emit(TokenKind.Number, Slice(start), start, line, column, 0L);
            }
        }

        private void LexString(int start, int line, int column)
        {
            Advance(); // opening quote

            var parts = new List<StringPart>();
            var builder = new StringBuilder();
            var partStart = _offset;
            var terminated = false;

            while (!AtEnd)
            {
                var c = Current;

                if (c == '"')
                {
                    Advance();
                    terminated = true;
                    break;
                }

                if (c == '\n')
                {
                    break;
                }

                if (c == '\\')
                {
                    Advance();
                    AppendEscape(builder, start, line, column);
                    continue;
                }

                if (c == '$' && Peek() == '{')
                {
                    if (builder.Length > 0)
                    {
                        parts.Add(new StringPart(builder.ToString(), false, partStart));
                        builder.Clear();
                    }

                    Advance();
                    Advance();

                    var exprStart = _offset;
                    var depth = 1;
                    while (!AtEnd && depth > 0)
                    {
                        var inner = Current;

                        if (inner == '"')
                        {
                            // Skip a nested string literal wholesale.
                            Advance();
                            while (!AtEnd && Current != '"')
                            {
                                if (Current == '\\')
                                {
                                    Advance();
                                }

                                Advance();
                            }

                            if (!AtEnd)
                            {
                                Advance();
                            }

                            continue;
                        }

                        if (inner == '{')
                        {
                            depth++;
                        }
                        else if (inner == '}')
                        {
                            depth--;
                            if (depth == 0)
                            {
                                break;
                            }
                        }

                        Advance();
                    }

                    if (AtEnd || depth != 0)
                    {
                        _diagnostics.Error("TRN0007", "unterminated string interpolation",
                            new SourcePosition(start, line, column), _name);
                        break;
                    }

                    var exprText = _source.Substring(exprStart, _offset - exprStart);
                    parts.Add(new StringPart(exprText, true, exprStart));
                    Advance(); // closing brace
                    partStart = _offset;
                    continue;
                }

                builder.Append(c);
                Advance();
            }

            if (builder.Length > 0)
            {
                parts.Add(new StringPart(builder.ToString(), false, partStart));
            }

            if (!terminated)
            {
                _diagnostics.Error("TRN0008", "unterminated string literal",
                    new SourcePosition(start, line, column), _name);
            }

            Emit(TokenKind.String, Slice(start), start, line, column, null, parts);
        }

        private void AppendEscape(StringBuilder builder, int start, int line, int column)
        {
            if (AtEnd)
            {
                return;
            }

            var c = Current;
            switch (c)
            {
                case 'n': builder.Append('\n'); Advance(); break;
                case 't': builder.Append('\t'); Advance(); break;
                case 'r': builder.Append('\r'); Advance(); break;
                case '0': builder.Append('\0'); Advance(); break;
                case 'e': builder.Append('\u001b'); Advance(); break;
                case '\\': builder.Append('\\'); Advance(); break;
                case '"': builder.Append('"'); Advance(); break;
                case '\'': builder.Append('\''); Advance(); break;
                case '$': builder.Append('$'); Advance(); break;
                case '{': builder.Append('{'); Advance(); break;
                case '}': builder.Append('}'); Advance(); break;
                case 'u': AppendUnicodeEscape(builder, start, line, column); break;
                default:
                    _diagnostics.Warning("TRN0009", $"unknown escape sequence '\\{c}'",
                        new SourcePosition(start, line, column), _name);
                    builder.Append(c);
                    Advance();
                    break;
            }
        }

        private void AppendUnicodeEscape(StringBuilder builder, int start, int line, int column)
        {
            Advance(); // 'u'

            if (Current == '{')
            {
                Advance();
                var digits = new StringBuilder();
                while (!AtEnd && Current != '}')
                {
                    digits.Append(Current);
                    Advance();
                }

                if (!AtEnd)
                {
                    Advance();
                }

                if (int.TryParse(digits.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code) &&
                    code >= 0 && code <= 0x10FFFF)
                {
                    builder.Append(char.ConvertFromUtf32(code));
                    return;
                }

                _diagnostics.Error("TRN0010", "invalid unicode escape",
                    new SourcePosition(start, line, column), _name);
                return;
            }

            var hex = new StringBuilder(4);
            for (var i = 0; i < 4 && !AtEnd; i++)
            {
                if (!Uri.IsHexDigit(Current))
                {
                    break;
                }

                hex.Append(Current);
                Advance();
            }

            if (hex.Length == 4 && int.TryParse(hex.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                builder.Append((char)value);
                return;
            }

            _diagnostics.Error("TRN0010", "invalid unicode escape",
                new SourcePosition(start, line, column), _name);
        }

        private bool LexOperator(int start, int line, int column)
        {
            var c = Current;
            var next = Peek();
            var third = Peek(2);

            void Emit2(TokenKind kind, int length)
            {
                for (var i = 0; i < length; i++)
                {
                    Advance();
                }

                Emit(kind, Slice(start), start, line, column);
            }

            switch (c)
            {
                case '(': Emit2(TokenKind.LeftParen, 1); return true;
                case ')': Emit2(TokenKind.RightParen, 1); return true;
                case '{': Emit2(TokenKind.LeftBrace, 1); return true;
                case '}': Emit2(TokenKind.RightBrace, 1); return true;
                case '[': Emit2(TokenKind.LeftBracket, 1); return true;
                case ']': Emit2(TokenKind.RightBracket, 1); return true;
                case ',': Emit2(TokenKind.Comma, 1); return true;
                case ';': Emit2(TokenKind.Semicolon, 1); return true;
                case ':': Emit2(TokenKind.Colon, 1); return true;
                case '@': Emit2(TokenKind.At, 1); return true;
                case '~': Emit2(TokenKind.Tilde, 1); return true;

                case '.':
                    if (next == '.' && third == '.')
                    {
                        Emit2(TokenKind.DotDotDot, 3);
                        return true;
                    }

                    if (next == '.')
                    {
                        Emit2(third == '<' ? TokenKind.DotDotLess : TokenKind.DotDot, third == '<' ? 3 : 2);
                        return true;
                    }

                    Emit2(TokenKind.Dot, 1);
                    return true;

                case '=':
                    if (next == '=')
                    {
                        Emit2(TokenKind.EqualEqual, 2);
                        return true;
                    }

                    if (next == '>')
                    {
                        Emit2(TokenKind.Arrow, 2);
                        return true;
                    }

                    Emit2(TokenKind.Equal, 1);
                    return true;

                case '!':
                    if (next == '=')
                    {
                        Emit2(TokenKind.BangEqual, 2);
                        return true;
                    }

                    Emit2(TokenKind.Bang, 1);
                    return true;

                case '+':
                    if (next == '+') { Emit2(TokenKind.PlusPlus, 2); return true; }
                    if (next == '=') { Emit2(TokenKind.PlusEqual, 2); return true; }
                    Emit2(TokenKind.Plus, 1);
                    return true;

                case '-':
                    if (next == '-') { Emit2(TokenKind.MinusMinus, 2); return true; }
                    if (next == '=') { Emit2(TokenKind.MinusEqual, 2); return true; }
                    Emit2(TokenKind.Minus, 1);
                    return true;

                case '*':
                    if (next == '*') { Emit2(third == '=' ? TokenKind.StarStarEqual : TokenKind.StarStar, third == '=' ? 3 : 2); return true; }
                    if (next == '=') { Emit2(TokenKind.StarEqual, 2); return true; }
                    Emit2(TokenKind.Star, 1);
                    return true;

                case '/':
                    if (next == '=') { Emit2(TokenKind.SlashEqual, 2); return true; }
                    Emit2(TokenKind.Slash, 1);
                    return true;

                case '%':
                    if (next == '=') { Emit2(TokenKind.PercentEqual, 2); return true; }
                    Emit2(TokenKind.Percent, 1);
                    return true;

                case '<':
                    if (next == '<') { Emit2(third == '=' ? TokenKind.ShiftLeftEqual : TokenKind.ShiftLeft, third == '=' ? 3 : 2); return true; }
                    if (next == '=') { Emit2(TokenKind.LessEqual, 2); return true; }
                    Emit2(TokenKind.Less, 1);
                    return true;

                case '>':
                    if (next == '>') { Emit2(third == '=' ? TokenKind.ShiftRightEqual : TokenKind.ShiftRight, third == '=' ? 3 : 2); return true; }
                    if (next == '=') { Emit2(TokenKind.GreaterEqual, 2); return true; }
                    Emit2(TokenKind.Greater, 1);
                    return true;

                case '&':
                    if (next == '&') { Emit2(TokenKind.AmpAmp, 2); return true; }
                    if (next == '=') { Emit2(TokenKind.AmpEqual, 2); return true; }
                    Emit2(TokenKind.Amp, 1);
                    return true;

                case '|':
                    if (next == '|') { Emit2(TokenKind.PipePipe, 2); return true; }
                    if (next == '=') { Emit2(TokenKind.PipeEqual, 2); return true; }
                    Emit2(TokenKind.Pipe, 1);
                    return true;

                case '^':
                    if (next == '=') { Emit2(TokenKind.CaretEqual, 2); return true; }
                    Emit2(TokenKind.Caret, 1);
                    return true;

                case '?':
                    if (next == '?') { Emit2(TokenKind.QuestionQuestion, 2); return true; }
                    if (next == '.') { Emit2(TokenKind.QuestionDot, 2); return true; }
                    Emit2(TokenKind.Question, 1);
                    return true;

                default:
                    return false;
            }
        }
    }
}
