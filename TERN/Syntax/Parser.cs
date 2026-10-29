using System;
using System.Collections.Generic;

namespace TERN.Syntax
{
    /// <summary>
    /// Recursive descent parser producing a small, immutable-ish AST.
    /// Errors are reported into a <see cref="DiagnosticBag"/> and recovered from so a
    /// single typo does not hide the rest of the file.
    /// </summary>
    public sealed class Parser
    {
        private readonly List<Token> _tokens;
        private readonly DiagnosticBag _diagnostics;
        private readonly string? _name;
        private int _index;
        private int _loopDepth;
        private int _functionDepth;

        public Parser(List<Token> tokens, DiagnosticBag diagnostics, string? name = null)
        {
            _tokens = tokens;
            _diagnostics = diagnostics;
            _name = name;
        }

        public static BlockStmt ParseProgram(string source, DiagnosticBag diagnostics, string? name = null)
        {
            var tokens = Lexer.Tokenize(source, diagnostics, name);
            return new Parser(tokens, diagnostics, name).ParseProgramBody();
        }

        /// <summary>Parses a single expression, used for string interpolation parts.</summary>
        public static Expr? ParseExpressionFragment(string source, int offset, DiagnosticBag diagnostics, string? name = null)
        {
            var tokens = Lexer.Tokenize(source, diagnostics, name);

            if (tokens.Count > 0)
            {
                var first = tokens[0];
                tokens[0] = new Token(first.Kind, first.Text, first.Offset + offset,
                    first.Line, first.Column + offset, first.Literal, first.Parts);
            }

            var parser = new Parser(tokens, diagnostics, name);
            var expression = parser.ParseExpression();
            return expression;
        }

        private Token Current => _tokens[Math.Min(_index, _tokens.Count - 1)];

        private Token Previous => _tokens[Math.Max(0, Math.Min(_index - 1, _tokens.Count - 1))];

        private Token PeekAt(int offset) =>
            _tokens[Math.Min(_index + offset, _tokens.Count - 1)];

        private Token Peek(int distance)
        {
            var target = _index + distance;
            return _tokens[Math.Min(target, _tokens.Count - 1)];
        }

        private bool Check(TokenKind kind) => Current.Kind == kind;

        private Token Advance()
        {
            var token = Current;
            if (_index < _tokens.Count - 1)
            {
                _index++;
            }

            return token;
        }

        private bool Match(params TokenKind[] kinds)
        {
            foreach (var kind in kinds)
            {
                if (Current.Kind == kind)
                {
                    Advance();
                    return true;
                }
            }

            return false;
        }

        private Token Expect(TokenKind kind, string what)
        {
            if (Current.Kind == kind)
            {
                return Advance();
            }

            Error($"expected {what} but found '{Describe(Current)}'", Current, "TRN0100");

            // Return a synthetic token so the parser can keep going.
            return new Token(kind, string.Empty, Current.Offset, Current.Line, Current.Column);
        }

        private static string Describe(Token token) =>
            token.Kind == TokenKind.EndOfFile ? "end of file" : token.Text;

        private void Error(string message, Token token, string code = "TRN0101")
        {
            _diagnostics.Error(code, message, token.Position, _name);
        }

        /// <summary>Consumes a statement terminator, allowing newline-terminated statements.</summary>
        private void EndStatement()
        {
            if (Match(TokenKind.Semicolon))
            {
                return;
            }

            if (Current.Kind is TokenKind.RightBrace or TokenKind.EndOfFile)
            {
                return;
            }

            if (Current.Line > Previous.Line)
            {
                return;
            }

            Error($"expected ';' but found '{Describe(Current)}'", Current, "TRN0102");
        }

        public BlockStmt ParseProgramBody()
        {
            var statements = new List<Stmt>();

            while (!Check(TokenKind.EndOfFile))
            {
                var before = _index;
                var statement = ParseStatement();
                if (statement is not null)
                {
                    statements.Add(statement);
                }

                if (_index == before)
                {
                    // No progress: skip a token to avoid an infinite loop.
                    Advance();
                }
            }

            return new BlockStmt { Statements = statements, Offset = 0, Line = 1 };
        }

        private Stmt? ParseStatement()
        {
            var token = Current;

            switch (token.Kind)
            {
                case TokenKind.Semicolon:
                    Advance();
                    return new EmptyStmt { Offset = token.Offset, Line = token.Line };

                case TokenKind.Let:
                case TokenKind.Const:
                    return ParseLet(false);

                case TokenKind.Fn:
                    if (Peek(1).Kind == TokenKind.Identifier)
                    {
                        return ParseFunctionDeclaration();
                    }

                    return ParseExpressionStatement();

                case TokenKind.Struct:
                    return ParseStructDeclaration();

                case TokenKind.If:
                    return ParseIf();

                case TokenKind.While:
                    return ParseWhile();

                case TokenKind.Loop:
                    return ParseLoop();

                case TokenKind.Do:
                    return ParseDoWhile();

                case TokenKind.For:
                    return ParseFor();

                case TokenKind.Break:
                    Advance();
                    if (_loopDepth == 0)
                    {
                        Error("'break' outside of a loop", token, "TRN0103");
                    }

                    EndStatement();
                    return new BreakStmt { Offset = token.Offset, Line = token.Line };

                case TokenKind.Continue:
                    Advance();
                    if (_loopDepth == 0)
                    {
                        Error("'continue' outside of a loop", token, "TRN0104");
                    }

                    EndStatement();
                    return new ContinueStmt { Offset = token.Offset, Line = token.Line };

                case TokenKind.Return:
                    return ParseReturn();

                case TokenKind.Throw:
                    Advance();
                    var thrown = ParseExpression();
                    EndStatement();
                    return new ThrowStmt { Value = thrown, Offset = token.Offset, Line = token.Line };

                case TokenKind.Try:
                    return ParseTry();

                case TokenKind.Import:
                    return ParseImport();

                case TokenKind.LeftBrace:
                    return ParseBlock();

                default:
                    return ParseExpressionStatement();
            }
        }

        private Stmt ParseLet(bool consumeKeyword = true)
        {
            var keyword = Advance();
            var isConst = keyword.Kind == TokenKind.Const;

            if (!Check(TokenKind.Identifier))
            {
                Error($"expected a variable name after '{keyword.Text}'", Current, "TRN0105");
                EndStatement();
                return new EmptyStmt { Offset = keyword.Offset, Line = keyword.Line };
            }

            var name = Advance();

            Expr? initializer = null;
            if (Match(TokenKind.Equal))
            {
                initializer = ParseExpression();
            }
            else if (isConst)
            {
                Error($"constant '{name.Text}' requires an initializer", name, "TRN0106");
            }

            EndStatement();
            return new LetStmt
            {
                Name = name.Text,
                IsConst = isConst,
                Initializer = initializer,
                Offset = keyword.Offset,
                Line = keyword.Line,
            };
        }

        private Stmt ParseFunctionDeclaration()
        {
            var keyword = Advance(); // fn
            var name = Expect(TokenKind.Identifier, "a function name");
            var function = ParseFunctionRest(name.Text, keyword);
            return new FunctionDeclStmt { Function = function, Offset = keyword.Offset, Line = keyword.Line };
        }

        private FunctionLiteral ParseFunctionRest(string? name, Token start)
        {
            var parameters = ParseParameterList();
            _functionDepth++;

            BlockStmt? body = null;
            Expr? expressionBody = null;

            if (Match(TokenKind.Arrow))
            {
                expressionBody = ParseExpression();
            }
            else if (Check(TokenKind.LeftBrace))
            {
                body = ParseBlock();
            }
            else
            {
                Error("expected '{' or '=>' to start the function body", Current, "TRN0107");
            }

            _functionDepth--;

            return new FunctionLiteral
            {
                Name = name,
                Parameters = parameters,
                Body = body,
                ExpressionBody = expressionBody,
                Offset = start.Offset,
                Line = start.Line,
            };
        }

        private List<Parameter> ParseParameterList()
        {
            var parameters = new List<Parameter>();
            Expect(TokenKind.LeftParen, "'('");
            var seenRest = false;

            while (!Check(TokenKind.RightParen) && !Check(TokenKind.EndOfFile))
            {
                if (seenRest)
                {
                    Error("no parameter may follow a rest parameter", Current, "TRN0108");
                }

                if (Match(TokenKind.DotDotDot))
                {
                    var rest = Expect(TokenKind.Identifier, "a parameter name");
                    parameters.Add(new Parameter
                    {
                        Name = rest.Text,
                        IsRest = true,
                        Offset = rest.Offset,
                    });
                    seenRest = true;
                }
                else
                {
                    var name = Expect(TokenKind.Identifier, "a parameter name");
                    Expr? defaultValue = null;
                    if (Match(TokenKind.Equal))
                    {
                        defaultValue = ParseExpression();
                    }

                    parameters.Add(new Parameter
                    {
                        Name = name.Text,
                        Default = defaultValue,
                        Offset = name.Offset,
                    });
                }

                if (!Match(TokenKind.Comma))
                {
                    break;
                }
            }

            Expect(TokenKind.RightParen, "')'");
            return parameters;
        }

        private Stmt ParseStructDeclaration()
        {
            var keyword = Advance();
            var name = Expect(TokenKind.Identifier, "a struct name");
            Expect(TokenKind.LeftBrace, "'{'");

            var fields = new List<StructField>();
            var methods = new List<FunctionLiteral>();

            while (!Check(TokenKind.RightBrace) && !Check(TokenKind.EndOfFile))
            {
                if (Match(TokenKind.Semicolon) || Match(TokenKind.Comma))
                {
                    continue;
                }

                if (Match(TokenKind.Fn))
                {
                    var methodName = Expect(TokenKind.Identifier, "a method name");
                    var function = ParseFunctionRest(methodName.Text, keyword);
                    methods.Add(function);
                    continue;
                }

                var fieldName = Expect(TokenKind.Identifier, "a field name");
                Expr? defaultValue = null;
                if (Match(TokenKind.Equal))
                {
                    defaultValue = ParseExpression();
                }

                fields.Add(new StructField
                {
                    Name = fieldName.Text,
                    Default = defaultValue,
                    Offset = fieldName.Offset,
                });

                Match(TokenKind.Comma);
                Match(TokenKind.Semicolon);
            }

            Expect(TokenKind.RightBrace, "'}'");

            return new StructDeclStmt
            {
                Name = name.Text,
                Fields = fields,
                Methods = methods,
                Offset = keyword.Offset,
                Line = keyword.Line,
            };
        }

        private Stmt ParseIf()
        {
            var keyword = Advance();
            var condition = ParseExpression();
            var then = ParseBodyStatement();
            Stmt? otherwise = null;

            if (Match(TokenKind.Else))
            {
                otherwise = Check(TokenKind.If) ? ParseIf() : ParseBodyStatement();
            }

            return new IfStmt
            {
                Condition = condition,
                Then = then,
                Else = otherwise,
                Offset = keyword.Offset,
                Line = keyword.Line,
            };
        }

        private Stmt ParseWhile()
        {
            var keyword = Advance();
            var condition = ParseExpression();
            _loopDepth++;
            var body = ParseBodyStatement();
            _loopDepth--;

            return new WhileStmt
            {
                Condition = condition,
                Body = body,
                Offset = keyword.Offset,
                Line = keyword.Line,
            };
        }

        private Stmt ParseLoop()
        {
            var keyword = Advance();
            _loopDepth++;
            var body = ParseBodyStatement();
            _loopDepth--;

            return new LoopStmt { Body = body, Offset = keyword.Offset, Line = keyword.Line };
        }

        private Stmt ParseDoWhile()
        {
            var keyword = Advance();
            _loopDepth++;
            var body = ParseBodyStatement();
            _loopDepth--;

            Expect(TokenKind.While, "'while'");
            var condition = ParseExpression();
            EndStatement();

            return new DoWhileStmt
            {
                Body = body,
                Condition = condition,
                Offset = keyword.Offset,
                Line = keyword.Line,
            };
        }

        private Stmt ParseFor()
        {
            var keyword = Advance();

            if (Check(TokenKind.LeftParen))
            {
                return ParseCStyleFor(keyword);
            }

            var names = new List<string>();
            var firstName = Expect(TokenKind.Identifier, "a loop variable");
            names.Add(firstName.Text);

            while (Match(TokenKind.Comma))
            {
                var next = Expect(TokenKind.Identifier, "a loop variable");
                names.Add(next.Text);
            }

            Expect(TokenKind.In, "'in'");
            var iterable = ParseExpression();

            _loopDepth++;
            var body = ParseBodyStatement();
            _loopDepth--;

            return new ForInStmt
            {
                Names = names,
                Iterable = iterable,
                Body = body,
                Offset = keyword.Offset,
                Line = keyword.Line,
            };
        }

        private Stmt ParseCStyleFor(Token keyword)
        {
            Expect(TokenKind.LeftParen, "'('");

            Stmt? initializer = null;
            if (!Check(TokenKind.Semicolon))
            {
                initializer = Check(TokenKind.Let) || Check(TokenKind.Const)
                    ? ParseLet(false)
                    : ParseExpressionStatement();
            }
            else
            {
                Advance();
            }

            Expr? condition = null;
            if (!Check(TokenKind.Semicolon))
            {
                condition = ParseExpression();
            }

            Expect(TokenKind.Semicolon, "';'");

            Expr? increment = null;
            if (!Check(TokenKind.RightParen))
            {
                increment = ParseExpression();
            }

            Expect(TokenKind.RightParen, "')'");

            _loopDepth++;
            var body = ParseBodyStatement();
            _loopDepth--;

            return new ForStmt
            {
                Initializer = initializer,
                Condition = condition,
                Increment = increment,
                Body = body,
                Offset = keyword.Offset,
                Line = keyword.Line,
            };
        }

        private Stmt ParseReturn()
        {
            var keyword = Advance();

            if (_functionDepth == 0)
            {
                Error("'return' outside of a function", keyword, "TRN0109");
            }

            Expr? value = null;
            if (!Check(TokenKind.Semicolon) && !Check(TokenKind.RightBrace) && !Check(TokenKind.EndOfFile) &&
                Current.Line == keyword.Line)
            {
                value = ParseExpression();
            }

            EndStatement();
            return new ReturnStmt { Value = value, Offset = keyword.Offset, Line = keyword.Line };
        }

        private Stmt ParseTry()
        {
            var keyword = Advance();
            var body = ParseBlock();

            string? catchName = null;
            Stmt? catchBody = null;
            Stmt? finallyBody = null;

            if (Match(TokenKind.Catch))
            {
                if (Match(TokenKind.LeftParen))
                {
                    var name = Expect(TokenKind.Identifier, "an error name");
                    catchName = name.Text;
                    Expect(TokenKind.RightParen, "')'");
                }

                catchBody = ParseBlock();
            }

            if (Match(TokenKind.Finally))
            {
                finallyBody = ParseBlock();
            }

            if (catchBody is null && finallyBody is null)
            {
                Error("'try' requires a 'catch' or 'finally' block", keyword, "TRN0110");
            }

            return new TryStmt
            {
                Body = body,
                CatchName = catchName,
                Catch = catchBody,
                Finally = finallyBody,
                Offset = keyword.Offset,
                Line = keyword.Line,
            };
        }

        private Stmt ParseImport()
        {
            var keyword = Advance();
            var path = Expect(TokenKind.String, "a module path");

            if (path.Parts is not null && path.Parts.Count > 1)
            {
                Error("module paths cannot be interpolated", path, "TRN0111");
            }

            string? alias = null;
            if (Match(TokenKind.As))
            {
                alias = Expect(TokenKind.Identifier, "an alias").Text;
            }

            EndStatement();
            return new ImportStmt
            {
                Path = path.Parts is { Count: 1 } ? path.Parts[0].Text : path.Text,
                Alias = alias,
                Offset = keyword.Offset,
                Line = keyword.Line,
            };
        }

        private Stmt ParseBodyStatement()
        {
            if (Check(TokenKind.LeftBrace))
            {
                return ParseBlock();
            }

            var before = _index;
            var statement = ParseStatement();
            if (_index == before)
            {
                Advance();
            }

            return statement ?? new EmptyStmt { Offset = Current.Offset, Line = Current.Line };
        }

        private BlockStmt ParseBlock()
        {
            var start = Expect(TokenKind.LeftBrace, "'{'");
            var statements = new List<Stmt>();

            while (!Check(TokenKind.RightBrace) && !Check(TokenKind.EndOfFile))
            {
                var before = _index;
                var statement = ParseStatement();
                if (statement is not null)
                {
                    statements.Add(statement);
                }

                if (_index == before)
                {
                    Advance();
                }
            }

            Expect(TokenKind.RightBrace, "'}'");
            return new BlockStmt { Statements = statements, Offset = start.Offset, Line = start.Line };
        }

        private Stmt ParseExpressionStatement()
        {
            var expression = ParseExpression();
            EndStatement();
            return new ExpressionStmt { Expression = expression, Offset = expression.Offset, Line = expression.Line };
        }

        // ------------------------------------------------------------- expressions

        public Expr ParseExpression() => ParseAssignment();

        private Expr ParseAssignment()
        {
            var left = ParseTernary();

            var op = Current.Kind;
            if (op is TokenKind.Equal or TokenKind.PlusEqual or TokenKind.MinusEqual or TokenKind.StarEqual
                or TokenKind.SlashEqual or TokenKind.PercentEqual or TokenKind.StarStarEqual
                or TokenKind.AmpEqual or TokenKind.PipeEqual or TokenKind.CaretEqual
                or TokenKind.ShiftLeftEqual or TokenKind.ShiftRightEqual)
            {
                var token = Advance();

                if (left is not (IdentifierExpr or MemberExpr or IndexExpr))
                {
                    Error("invalid assignment target", token, "TRN0112");
                }

                var value = ParseAssignment();
                return new AssignExpr
                {
                    Target = left,
                    Operator = op,
                    Value = value,
                    Offset = left.Offset,
                    Line = left.Line,
                };
            }

            return left;
        }

        private Expr ParseTernary()
        {
            var condition = ParseRange();

            if (Match(TokenKind.Question))
            {
                var whenTrue = ParseAssignment();
                Expect(TokenKind.Colon, "':'");
                var whenFalse = ParseTernary();
                return new TernaryExpr
                {
                    Condition = condition,
                    WhenTrue = whenTrue,
                    WhenFalse = whenFalse,
                    Offset = condition.Offset,
                    Line = condition.Line,
                };
            }

            return condition;
        }

        private Expr ParseRange()
        {
            var start = ParseNullCoalescing();

            if (Check(TokenKind.DotDot) || Check(TokenKind.DotDotLess))
            {
                var inclusive = Current.Kind == TokenKind.DotDot;
                Advance();

                // `xs[2..]` leaves the upper bound open; only slices may do that.
                if (Check(TokenKind.RightBracket))
                {
                    return new RangeExpr
                    {
                        Start = start,
                        End = null,
                        Inclusive = inclusive,
                        Offset = start.Offset,
                        Line = start.Line,
                    };
                }

                var end = ParseNullCoalescing();
                return new RangeExpr
                {
                    Start = start,
                    End = end,
                    Inclusive = inclusive,
                    Offset = start.Offset,
                    Line = start.Line,
                };
            }

            return start;
        }

        private Expr ParseNullCoalescing() => ParseBinary(ParseLogicalOr, TokenKind.QuestionQuestion);

        private Expr ParseBinary(Func<Expr> parseOperand, params TokenKind[] operators)
        {
            var left = parseOperand();

            while (true)
            {
                var matched = false;
                foreach (var candidate in operators)
                {
                    if (Current.Kind == candidate)
                    {
                        matched = true;
                        break;
                    }
                }

                if (!matched)
                {
                    return left;
                }

                var token = Advance();
                var right = parseOperand();
                left = new BinaryExpr
                {
                    Operator = token.Kind,
                    Left = left,
                    Right = right,
                    Offset = left.Offset,
                    Line = left.Line,
                };
            }
        }

        private Expr ParseLogicalOr() => ParseBinary(ParseLogicalAnd, TokenKind.PipePipe, TokenKind.Or);

        private Expr ParseLogicalAnd() => ParseBinary(ParseEquality, TokenKind.AmpAmp, TokenKind.And);

        private Expr ParseEquality() => ParseBinary(ParseComparison, TokenKind.EqualEqual, TokenKind.BangEqual);

        private Expr ParseComparison() => ParseBinary(ParseMembership, TokenKind.Less, TokenKind.LessEqual,
            TokenKind.Greater, TokenKind.GreaterEqual);

        private Expr ParseMembership()
        {
            var left = ParseBitwiseOr();

            while (true)
            {
                // `a in b` and its negation `a not in b`.
                if (Check(TokenKind.Not) && PeekAt(1).Kind == TokenKind.In)
                {
                    Advance();
                    Advance();
                    var item = ParseRange();
                    left = NegateMembership(new BinaryExpr
                    {
                        Operator = TokenKind.In,
                        Left = left,
                        Right = item,
                        Offset = left.Offset,
                        Line = left.Line,
                    });
                    continue;
                }

                if (Check(TokenKind.In))
                {
                    Advance();
                    // Ranges bind tighter than `in`: `3 in 0..<5` tests the whole range.
                    var container = ParseRange();
                    left = new BinaryExpr
                    {
                        Operator = TokenKind.In,
                        Left = left,
                        Right = container,
                        Offset = left.Offset,
                        Line = left.Line,
                    };
                    continue;
                }

                return left;
            }
        }

        private static Expr NegateMembership(Expr expression) => new UnaryExpr
        {
            Operator = TokenKind.Bang,
            Operand = expression,
            Offset = expression.Offset,
            Line = expression.Line,
        };

        private Expr ParseBitwiseOr() => ParseBinary(ParseBitwiseXor, TokenKind.Pipe);

        private Expr ParseBitwiseXor() => ParseBinary(ParseBitwiseAnd, TokenKind.Caret);

        private Expr ParseBitwiseAnd() => ParseBinary(ParseShift, TokenKind.Amp);

        private Expr ParseShift() => ParseBinary(ParseAdditive, TokenKind.ShiftLeft, TokenKind.ShiftRight);

        private Expr ParseAdditive() => ParseBinary(ParseMultiplicative, TokenKind.Plus, TokenKind.Minus);

        private Expr ParseMultiplicative() => ParseBinary(ParsePower, TokenKind.Star, TokenKind.Slash, TokenKind.Percent);

        private Expr ParsePower()
        {
            var left = ParseUnary();

            if (Match(TokenKind.StarStar))
            {
                var right = ParsePower(); // right associative
                return new BinaryExpr
                {
                    Operator = TokenKind.StarStar,
                    Left = left,
                    Right = right,
                    Offset = left.Offset,
                    Line = left.Line,
                };
            }

            return left;
        }

        private Expr ParseUnary()
        {
            var token = Current;

            if (token.Kind is TokenKind.Bang or TokenKind.Not or TokenKind.Minus or TokenKind.Plus
                or TokenKind.Tilde or TokenKind.TypeOf)
            {
                Advance();
                var operand = ParseUnary();
                return new UnaryExpr
                {
                    Operator = token.Kind,
                    Operand = operand,
                    Offset = token.Offset,
                    Line = token.Line,
                };
            }

            if (token.Kind is TokenKind.PlusPlus or TokenKind.MinusMinus)
            {
                Advance();
                var operand = ParseUnary();

                if (operand is not (IdentifierExpr or MemberExpr or IndexExpr))
                {
                    Error("increment requires a variable", token, "TRN0113");
                }

                return new IncrementExpr
                {
                    Target = operand,
                    Delta = token.Kind == TokenKind.PlusPlus ? 1 : -1,
                    Prefix = true,
                    Offset = token.Offset,
                    Line = token.Line,
                };
            }

            return ParsePostfix();
        }

        private Expr ParsePostfix()
        {
            var expression = ParsePrimary();

            while (true)
            {
                if (Match(TokenKind.LeftParen))
                {
                    var arguments = new List<Expr>();

                    while (!Check(TokenKind.RightParen) && !Check(TokenKind.EndOfFile))
                    {
                        arguments.Add(ParseExpression());
                        if (!Match(TokenKind.Comma))
                        {
                            break;
                        }
                    }

                    Expect(TokenKind.RightParen, "')'");
                    expression = new CallExpr
                    {
                        Callee = expression,
                        Arguments = arguments,
                        Offset = expression.Offset,
                        Line = expression.Line,
                    };
                    continue;
                }

                if (Match(TokenKind.LeftBracket))
                {
                    // Open-ended slices: xs[..2] and xs[..<2].
                    if (Check(TokenKind.DotDot) || Check(TokenKind.DotDotLess))
                    {
                        var openInclusive = Current.Kind == TokenKind.DotDot;
                        Advance();
                        Expr? openEnd = Check(TokenKind.RightBracket) ? null : ParseExpression();
                        Expect(TokenKind.RightBracket, "']'");
                        expression = new SliceExpr
                        {
                            Target = expression,
                            Start = null,
                            End = openEnd,
                            Inclusive = openInclusive,
                            Offset = expression.Offset,
                            Line = expression.Line,
                        };
                        continue;
                    }

                    var index = ParseExpression();
                    Expect(TokenKind.RightBracket, "']'");

                    // xs[2..] parses as a range with no upper bound.
                    if (index is RangeExpr { End: null } openRange)
                    {
                        expression = new SliceExpr
                        {
                            Target = expression,
                            Start = openRange.Start,
                            End = null,
                            Inclusive = openRange.Inclusive,
                            Offset = expression.Offset,
                            Line = expression.Line,
                        };
                        continue;
                    }

                    expression = new IndexExpr
                    {
                        Target = expression,
                        Index = index,
                        Offset = expression.Offset,
                        Line = expression.Line,
                    };
                    continue;
                }

                if (Match(TokenKind.Dot) || Match(TokenKind.QuestionDot))
                {
                    var safe = Previous.Kind == TokenKind.QuestionDot;
                    var name = Expect(TokenKind.Identifier, "a member name");
                    expression = new MemberExpr
                    {
                        Target = expression,
                        Name = name.Text,
                        Safe = safe,
                        Offset = expression.Offset,
                        Line = expression.Line,
                    };
                    continue;
                }

                if (Check(TokenKind.PlusPlus) || Check(TokenKind.MinusMinus))
                {
                    var token = Advance();

                    if (expression is not (IdentifierExpr or MemberExpr or IndexExpr))
                    {
                        Error("increment requires a variable", token, "TRN0113");
                        break;
                    }

                    expression = new IncrementExpr
                    {
                        Target = expression,
                        Delta = token.Kind == TokenKind.PlusPlus ? 1 : -1,
                        Prefix = false,
                        Offset = expression.Offset,
                        Line = expression.Line,
                    };
                    continue;
                }

                break;
            }

            return expression;
        }

        private Expr ParsePrimary()
        {
            var token = Current;

            switch (token.Kind)
            {
                case TokenKind.Number:
                    Advance();
                    return new LiteralExpr { Value = token.Literal, Offset = token.Offset, Line = token.Line };

                case TokenKind.String:
                    Advance();
                    return BuildString(token);

                case TokenKind.True:
                    Advance();
                    return new LiteralExpr { Value = true, Offset = token.Offset, Line = token.Line };

                case TokenKind.False:
                    Advance();
                    return new LiteralExpr { Value = false, Offset = token.Offset, Line = token.Line };

                case TokenKind.Nil:
                    Advance();
                    return new LiteralExpr { Value = null, Offset = token.Offset, Line = token.Line };

                case TokenKind.This:
                    Advance();
                    return new ThisExpr { Offset = token.Offset, Line = token.Line };

                case TokenKind.Identifier:
                    Advance();
                    return new IdentifierExpr { Name = token.Text, Offset = token.Offset, Line = token.Line };

                case TokenKind.LeftParen:
                    if (LooksLikeLambda())
                    {
                        var parameters = ParseParameterList();
                        Expect(TokenKind.Arrow, "'=>'");
                        var body = ParseExpression();
                        return new LambdaExpr
                        {
                            Function = new FunctionLiteral
                            {
                                Parameters = parameters,
                                ExpressionBody = body,
                                Offset = token.Offset,
                                Line = token.Line,
                            },
                            Offset = token.Offset,
                            Line = token.Line,
                        };
                    }

                    Advance();
                    var inner = ParseExpression();
                    Expect(TokenKind.RightParen, "')'");
                    return inner;

                case TokenKind.Fn:
                    Advance();
                    return new LambdaExpr
                    {
                        Function = ParseFunctionRest(null, token),
                        Offset = token.Offset,
                        Line = token.Line,
                    };

                case TokenKind.LeftBracket:
                    return ParseListLiteral();

                case TokenKind.LeftBrace:
                    return ParseMapLiteral();

                case TokenKind.Match:
                    return ParseMatch();

                case TokenKind.If:
                    // `if` used as an expression: if cond { a } else { b }
                    return ParseIfExpression();

                case TokenKind.DotDotDot:
                    Error("spread is only valid inside a list or call", token, "TRN0114");
                    Advance();
                    return new LiteralExpr { Value = null, Offset = token.Offset, Line = token.Line };

                default:
                    Error($"expected an expression but found '{Describe(token)}'", token, "TRN0115");
                    Advance();
                    return new LiteralExpr { Value = null, Offset = token.Offset, Line = token.Line };
            }
        }

        private Expr ParseIfExpression()
        {
            var keyword = Advance();
            var condition = ParseExpression();
            var thenBranch = ParseBodyStatement();
            Stmt? elseBranch = null;

            if (Match(TokenKind.Else))
            {
                elseBranch = Check(TokenKind.If) ? ParseIf() : ParseBodyStatement();
            }

            elseBranch ??= new BlockStmt
            {
                Statements = new List<Stmt>
                {
                    new ExpressionStmt
                    {
                        Expression = new LiteralExpr { Value = null, Offset = keyword.Offset, Line = keyword.Line },
                        Offset = keyword.Offset,
                        Line = keyword.Line,
                    },
                },
                Offset = keyword.Offset,
                Line = keyword.Line,
            };

            return new IfExpr
            {
                Condition = condition,
                Then = thenBranch,
                Else = elseBranch,
                Offset = keyword.Offset,
                Line = keyword.Line,
            };
        }

        private Expr BuildString(Token token)
        {
            var parts = token.Parts;
            if (parts is null || parts.Count == 0)
            {
                return new LiteralExpr { Value = string.Empty, Offset = token.Offset, Line = token.Line };
            }

            if (parts.Count == 1 && !parts[0].IsExpression)
            {
                return new LiteralExpr { Value = parts[0].Text, Offset = token.Offset, Line = token.Line };
            }

            var expressions = new List<Expr?>(parts.Count);
            foreach (var part in parts)
            {
                expressions.Add(part.IsExpression
                    ? ParseExpressionFragment(part.Text, part.Offset, _diagnostics, _name)
                    : null);
            }

            return new InterpolatedStringExpr
            {
                Parts = parts,
                Expressions = expressions,
                Offset = token.Offset,
                Line = token.Line,
            };
        }

        private Expr ParseListLiteral()
        {
            var start = Advance(); // [
            var items = new List<Expr>();

            while (!Check(TokenKind.RightBracket) && !Check(TokenKind.EndOfFile))
            {
                items.Add(ParseExpression());
                if (!Match(TokenKind.Comma))
                {
                    break;
                }
            }

            Expect(TokenKind.RightBracket, "']'");
            return new ListExpr { Items = items, Offset = start.Offset, Line = start.Line };
        }

        private Expr ParseMapLiteral()
        {
            var start = Advance(); // {
            var keys = new List<Expr>();
            var values = new List<Expr>();

            while (!Check(TokenKind.RightBrace) && !Check(TokenKind.EndOfFile))
            {
                // Allow bare identifier keys: { name: "tern" }
                Expr key;
                if (Check(TokenKind.Identifier) && Peek(1).Kind == TokenKind.Colon)
                {
                    var identifier = Advance();
                    key = new LiteralExpr { Value = identifier.Text, Offset = identifier.Offset, Line = identifier.Line };
                }
                else if (Check(TokenKind.String) && Peek(1).Kind == TokenKind.Colon)
                {
                    key = ParsePrimary();
                }
                else
                {
                    key = ParseExpression();
                }

                Expect(TokenKind.Colon, "':'");
                var value = ParseExpression();
                keys.Add(key);
                values.Add(value);

                if (!Match(TokenKind.Comma))
                {
                    break;
                }
            }

            Expect(TokenKind.RightBrace, "'}'");
            return new MapExpr { Keys = keys, Values = values, Offset = start.Offset, Line = start.Line };
        }

        private Expr ParseMatch()
        {
            var keyword = Advance();
            Expr subject;

            if (Match(TokenKind.LeftParen))
            {
                subject = ParseExpression();
                Expect(TokenKind.RightParen, "')'");
            }
            else
            {
                subject = ParseExpression();
            }

            Expect(TokenKind.LeftBrace, "'{'");

            var arms = new List<MatchArm>();

            while (!Check(TokenKind.RightBrace) && !Check(TokenKind.EndOfFile))
            {
                var armOffset = Current.Offset;
                var pattern = ParsePattern();
                Expr? guard = null;

                if (Match(TokenKind.If))
                {
                    guard = ParseExpression();
                }

                Expect(TokenKind.Arrow, "'=>'");

                Stmt? body = null;
                Expr? expressionBody = null;

                if (Check(TokenKind.LeftBrace))
                {
                    body = ParseBlock();
                }
                else
                {
                    expressionBody = ParseExpression();
                }

                arms.Add(new MatchArm
                {
                    Pattern = pattern,
                    Guard = guard,
                    Body = body,
                    ExpressionBody = expressionBody,
                    Offset = armOffset,
                });

                if (!Match(TokenKind.Comma) && !Match(TokenKind.Semicolon))
                {
                    if (!Check(TokenKind.RightBrace))
                    {
                        // Newline separated arms are allowed.
                        if (Current.Line <= Previous.Line)
                        {
                            Error("expected ',' or '}' after match arm", Current, "TRN0116");
                            Advance();
                        }
                    }
                }
            }

            Expect(TokenKind.RightBrace, "'}'");

            if (arms.Count == 0)
            {
                Error("match requires at least one arm", keyword, "TRN0117");
            }

            return new MatchExpr { Subject = subject, Arms = arms, Offset = keyword.Offset, Line = keyword.Line };
        }

        private Pattern ParsePattern()
        {
            var first = ParseSinglePattern();
            if (!Check(TokenKind.Pipe))
            {
                return first;
            }

            var alternatives = new List<Pattern> { first };
            while (Match(TokenKind.Pipe))
            {
                alternatives.Add(ParseSinglePattern());
            }

            return new OrPattern { Alternatives = alternatives, Offset = first.Offset };
        }

        private Pattern ParseSinglePattern()
        {
            var token = Current;

            switch (token.Kind)
            {
                case TokenKind.Number:
                case TokenKind.String:
                case TokenKind.True:
                case TokenKind.False:
                case TokenKind.Nil:
                {
                    string? literalText = null;
                    var value = token.Kind switch
                    {
                        TokenKind.True => (object?)true,
                        TokenKind.False => false,
                        TokenKind.Nil => null,
                        TokenKind.String => token.Parts is { Count: 1 } single && !single[0].IsExpression
                            ? single[0].Text
                            : null,
                        _ => token.Literal,
                    };

                    Advance();

                    if (Match(TokenKind.DotDot) || Match(TokenKind.DotDotLess))
                    {
                        var inclusive = Previous.Kind == TokenKind.DotDot;
                        var end = ParseNumberOrIdentifier();
                        return new RangePattern
                        {
                            Start = new LiteralExpr { Value = value, Offset = token.Offset, Line = token.Line },
                            End = end,
                            Inclusive = inclusive,
                            Offset = token.Offset,
                        };
                    }

                    _ = literalText;
                    return new LiteralPattern { Value = value, Offset = token.Offset };
                }

                case TokenKind.Minus:
                {
                    Advance();
                    var number = Expect(TokenKind.Number, "a number");
                    var value = number.Literal switch
                    {
                        long l => (object)(-l),
                        double d => -d,
                        _ => 0L,
                    };

                    return new LiteralPattern { Value = value, Offset = token.Offset };
                }

                case TokenKind.Identifier:
                {
                    Advance();
                    if (token.Text == "_")
                    {
                        return new WildcardPattern { Offset = token.Offset };
                    }

                    return new BindingPattern { Name = token.Text, Offset = token.Offset };
                }

                default:
                    break;
            }

            if (token.Kind == TokenKind.LeftBracket)
            {
                Advance();
                var elements = new List<Pattern>();
                string? restName = null;

                while (!Check(TokenKind.RightBracket) && !Check(TokenKind.EndOfFile))
                {
                    if (Match(TokenKind.DotDotDot))
                    {
                        restName = Expect(TokenKind.Identifier, "a rest name").Text;
                        Match(TokenKind.Comma);
                        continue;
                    }

                    elements.Add(ParseSinglePattern());
                    if (!Match(TokenKind.Comma))
                    {
                        break;
                    }
                }

                Expect(TokenKind.RightBracket, "']'");
                return new ListPattern { Elements = elements, RestName = restName, Offset = token.Offset };
            }

            Error($"invalid pattern starting with '{Describe(token)}'", token, "TRN0118");
            Advance();
            return new WildcardPattern { Offset = token.Offset };
        }

        private Expr ParseNumberOrIdentifier()
        {
            var token = Current;

            if (token.Kind == TokenKind.Number)
            {
                Advance();
                return new LiteralExpr { Value = token.Literal, Offset = token.Offset, Line = token.Line };
            }

            if (token.Kind == TokenKind.Identifier)
            {
                Advance();
                return new IdentifierExpr { Name = token.Text, Offset = token.Offset, Line = token.Line };
            }

            Error("expected a number or identifier", token, "TRN0119");
            return new LiteralExpr { Value = 0L, Offset = token.Offset, Line = token.Line };
        }

        /// <summary>Cheap lookahead: does a '(' at the cursor start a lambda parameter list?</summary>
        private bool LooksLikeLambda()
        {
            var depth = 0;
            for (var i = _index; i < _tokens.Count; i++)
            {
                switch (_tokens[i].Kind)
                {
                    case TokenKind.LeftParen:
                        depth++;
                        break;
                    case TokenKind.RightParen:
                        depth--;
                        if (depth == 0)
                        {
                            return i + 1 < _tokens.Count && _tokens[i + 1].Kind == TokenKind.Arrow;
                        }

                        break;
                    case TokenKind.EndOfFile:
                        return false;
                }
            }

            return false;
        }
    }
}
