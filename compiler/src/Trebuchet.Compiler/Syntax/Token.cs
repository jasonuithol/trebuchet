namespace Trebuchet.Compiler.Syntax;

public enum TokenKind
{
    // layout
    Newline,
    Indent,
    Dedent,
    EndOfFile,

    // literals and names
    Identifier,     // lowerCamel
    TypeName,       // UpperCamel
    Integer,
    Float,
    String,

    // keywords
    KwModule, KwUse, KwRecord, KwEntity, KwUnion, KwFn, KwHandler, KwService, KwScoped,
    KwShape, KwRoot, KwMatch, KwWith, KwIf, KwThen, KwElse, KwInit, KwAnd, KwOr, KwNot,
    KwTrue, KwFalse, KwPrivate, KwExtern, KwResource, KwSupervise,

    // punctuation
    LParen, RParen, LBracket, RBracket, LBrace, RBrace,
    Comma, Colon, Dot, Backslash, Bang, Question, Dash,
    Arrow,        // ->
    FatArrow,     // =>
    Assign,       // =
    Plus, Star, Slash, Percent,
    Eq, Neq, Lt, Le, Gt, Ge,
}

/// <summary>A source comment. Text excludes the leading slashes.</summary>
public sealed record Comment(int Line, string Text, bool OwnLine);

public readonly record struct Position(int Line, int Column)
{
    public override string ToString() => $"{Line}:{Column}";
}

public readonly record struct Token(TokenKind Kind, string Text, Position Start)
{
    public override string ToString() => Kind switch
    {
        TokenKind.Newline => "NEWLINE",
        TokenKind.Indent => "INDENT",
        TokenKind.Dedent => "DEDENT",
        TokenKind.EndOfFile => "EOF",
        _ => $"{Kind}({Text})",
    };
}

public sealed class SyntaxException : Exception
{
    public Position Position { get; }
    public string? File { get; }

    public SyntaxException(Position position, string message, string? file = null)
        : base(message)
    {
        Position = position;
        File = file;
    }

    public override string ToString() =>
        File is null ? $"{Position}: {Message}" : $"{File}:{Position}: {Message}";
}
