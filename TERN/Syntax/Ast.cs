using System.Collections.Generic;

namespace TERN.Syntax
{
    public abstract class Node
    {
        public int Offset { get; init; }
        public int Line { get; init; }
    }

    public abstract class Expr : Node
    {
        public abstract string Describe();
    }

    public abstract class Stmt : Node
    {
    }

    // ---------------------------------------------------------------- expressions

    public sealed class LiteralExpr : Expr
    {
        public required object? Value { get; init; }

        public override string Describe() => Value?.ToString() ?? "nil";
    }

    public sealed class InterpolatedStringExpr : Expr
    {
        public required List<StringPart> Parts { get; init; }
        public required List<Expr?> Expressions { get; init; }

        public override string Describe() => "interpolated string";
    }

    public sealed class IdentifierExpr : Expr
    {
        public required string Name { get; init; }

        public override string Describe() => Name;
    }

    public sealed class ThisExpr : Expr
    {
        public override string Describe() => "this";
    }

    public sealed class UnaryExpr : Expr
    {
        public required TokenKind Operator { get; init; }
        public required Expr Operand { get; init; }

        public override string Describe() => $"({Operator} {Operand.Describe()})";
    }

    public sealed class BinaryExpr : Expr
    {
        public required TokenKind Operator { get; init; }
        public required Expr Left { get; init; }
        public required Expr Right { get; init; }

        public override string Describe() => $"({Left.Describe()} {Operator} {Right.Describe()})";
    }

    public sealed class TernaryExpr : Expr
    {
        public required Expr Condition { get; init; }
        public required Expr WhenTrue { get; init; }
        public required Expr WhenFalse { get; init; }

        public override string Describe() => "(?:)";
    }

    public sealed class CallExpr : Expr
    {
        public required Expr Callee { get; init; }
        public required List<Expr> Arguments { get; init; }

        public override string Describe() => $"{Callee.Describe()}(...)";
    }

    public sealed class MemberExpr : Expr
    {
        public required Expr Target { get; init; }
        public required string Name { get; init; }

        /// <summary>True for the optional form <c>a?.b</c>, which yields nil when a is nil.</summary>
        public bool Safe { get; init; }

        public override string Describe() => $"{Target.Describe()}{(Safe ? "?." : ".")}{Name}";
    }

    public sealed class IndexExpr : Expr
    {
        public required Expr Target { get; init; }
        public required Expr Index { get; init; }

        public override string Describe() => $"{Target.Describe()}[...]";
    }

    public sealed class RangeExpr : Expr
    {
        public required Expr Start { get; init; }

        /// <summary>Null for an open ended slice such as <c>xs[2..]</c>.</summary>
        public Expr? End { get; init; }
        public required bool Inclusive { get; init; }

        public override string Describe() => $"{Start.Describe()}..{End?.Describe()}";
    }

    /// <summary>A slice with an optional (open) lower or upper bound: <c>xs[..2]</c>, <c>xs[2..]</c>.</summary>
    public sealed class SliceExpr : Expr
    {
        public required Expr Target { get; init; }
        public Expr? Start { get; init; }
        public Expr? End { get; init; }
        public required bool Inclusive { get; init; }

        public override string Describe() => $"{Target.Describe()}[{Start?.Describe()}..{End?.Describe()}]";
    }

    public sealed class AssignExpr : Expr
    {
        public required Expr Target { get; init; }
        public required TokenKind Operator { get; init; }   // Equal for plain assignment
        public required Expr Value { get; init; }

        public override string Describe() => $"{Target.Describe()} = ...";
    }

    public sealed class IncrementExpr : Expr
    {
        public required Expr Target { get; init; }
        public required int Delta { get; init; }
        public required bool Prefix { get; init; }

        public override string Describe() => $"{Target.Describe()}{(Delta > 0 ? "++" : "--")}";
    }

    public sealed class LambdaExpr : Expr
    {
        public required FunctionLiteral Function { get; init; }

        public override string Describe() => "fn";
    }

    public sealed class ListExpr : Expr
    {
        public required List<Expr> Items { get; init; }

        public override string Describe() => "[...]";
    }

    public sealed class MapExpr : Expr
    {
        public required List<Expr> Keys { get; init; }
        public required List<Expr> Values { get; init; }

        public override string Describe() => "{...}";
    }

    public sealed class IfExpr : Expr
    {
        public required Expr Condition { get; init; }
        public required Stmt Then { get; init; }
        public required Stmt Else { get; init; }

        public override string Describe() => "if";
    }

    public sealed class MatchExpr : Expr
    {
        public required Expr Subject { get; init; }
        public required List<MatchArm> Arms { get; init; }

        public override string Describe() => "match";
    }

    public sealed class MatchArm
    {
        public required Pattern Pattern { get; init; }
        public Expr? Guard { get; init; }
        public Stmt? Body { get; init; }
        public Expr? ExpressionBody { get; init; }
        public int Offset { get; init; }
    }

    // ------------------------------------------------------------------ patterns

    public abstract class Pattern
    {
        public int Offset { get; init; }
    }

    public sealed class LiteralPattern : Pattern
    {
        public required object? Value { get; init; }
    }

    public sealed class RangePattern : Pattern
    {
        public required Expr Start { get; init; }
        public required Expr End { get; init; }
        public required bool Inclusive { get; init; }
    }

    public sealed class WildcardPattern : Pattern
    {
    }

    public sealed class BindingPattern : Pattern
    {
        public required string Name { get; init; }
    }

    public sealed class OrPattern : Pattern
    {
        public required List<Pattern> Alternatives { get; init; }
    }

    public sealed class ListPattern : Pattern
    {
        public required List<Pattern> Elements { get; init; }
        public required string? RestName { get; init; }
    }

    // ----------------------------------------------------------------- functions

    public sealed class Parameter
    {
        public required string Name { get; init; }
        public Expr? Default { get; init; }
        public bool IsRest { get; init; }
        public int Offset { get; init; }
    }

    public sealed class FunctionLiteral
    {
        public string? Name { get; init; }
        public required List<Parameter> Parameters { get; init; }
        public BlockStmt? Body { get; init; }
        public Expr? ExpressionBody { get; init; }
        public int Offset { get; init; }
        public int Line { get; init; }
    }

    // ---------------------------------------------------------------- statements

    public sealed class ExpressionStmt : Stmt
    {
        public required Expr Expression { get; init; }
    }

    public sealed class LetStmt : Stmt
    {
        public required string Name { get; init; }
        public required bool IsConst { get; init; }
        public required Expr? Initializer { get; init; }
    }

    public sealed class FunctionDeclStmt : Stmt
    {
        public required FunctionLiteral Function { get; init; }
    }

    public sealed class StructDeclStmt : Stmt
    {
        public required string Name { get; init; }
        public required List<StructField> Fields { get; init; }
        public required List<FunctionLiteral> Methods { get; init; }
    }

    public sealed class StructField
    {
        public required string Name { get; init; }
        public Expr? Default { get; init; }
        public int Offset { get; init; }
    }

    public sealed class BlockStmt : Stmt
    {
        public required List<Stmt> Statements { get; init; }
    }

    public sealed class IfStmt : Stmt
    {
        public required Expr Condition { get; init; }
        public required Stmt Then { get; init; }
        public Stmt? Else { get; init; }
    }

    public sealed class WhileStmt : Stmt
    {
        public required Expr Condition { get; init; }
        public required Stmt Body { get; init; }
    }

    public sealed class DoWhileStmt : Stmt
    {
        public required Stmt Body { get; init; }
        public required Expr Condition { get; init; }
    }

    public sealed class LoopStmt : Stmt
    {
        public required Stmt Body { get; init; }
    }

    public sealed class ForInStmt : Stmt
    {
        public required List<string> Names { get; init; }
        public required Expr Iterable { get; init; }
        public required Stmt Body { get; init; }
    }

    public sealed class ForStmt : Stmt
    {
        public required Stmt? Initializer { get; init; }
        public required Expr? Condition { get; init; }
        public required Expr? Increment { get; init; }
        public required Stmt Body { get; init; }
    }

    public sealed class BreakStmt : Stmt
    {
    }

    public sealed class ContinueStmt : Stmt
    {
    }

    public sealed class ReturnStmt : Stmt
    {
        public Expr? Value { get; init; }
    }

    public sealed class ThrowStmt : Stmt
    {
        public required Expr Value { get; init; }
    }

    public sealed class TryStmt : Stmt
    {
        public required Stmt Body { get; init; }
        public string? CatchName { get; init; }
        public Stmt? Catch { get; init; }
        public Stmt? Finally { get; init; }
    }

    public sealed class ImportStmt : Stmt
    {
        public required string Path { get; init; }
        public string? Alias { get; init; }
    }

    public sealed class EmptyStmt : Stmt
    {
    }
}
