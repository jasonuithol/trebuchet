namespace Trebuchet.Compiler.Syntax;

public abstract record Node(Position Pos);

// ---------------------------------------------------------------- file

public sealed record QualifiedName(IReadOnlyList<string> Parts)
{
    public override string ToString() => string.Join(".", Parts);
}

public sealed record UseDecl(Position Pos, QualifiedName Name) : Node(Pos);

public sealed record SourceFile(
    Position Pos,
    QualifiedName? Module,
    IReadOnlyList<UseDecl> Uses,
    IReadOnlyList<Decl> Decls) : Node(Pos)
{
    /// <summary>Comments in source order, for the printer. Empty when the file was built by hand.</summary>
    public IReadOnlyList<Comment> Comments { get; init; } = Array.Empty<Comment>();
}

// ---------------------------------------------------------------- types

public abstract record TypeRef(Position Pos) : Node(Pos);

public sealed record NamedType(Position Pos, string Name, IReadOnlyList<TypeRef> Args) : TypeRef(Pos);

/// <summary>A tuple type, <c>(A, B)</c>. Always two or more items.</summary>
public sealed record TupleType(Position Pos, IReadOnlyList<TypeRef> Items) : TypeRef(Pos);

/// <summary>A function type. <see cref="Effects"/> null means unspecified; empty means Pure.</summary>
public sealed record FnType(
    Position Pos,
    IReadOnlyList<TypeRef> Params,
    TypeRef Return,
    IReadOnlyList<string>? Effects) : TypeRef(Pos);

// ---------------------------------------------------------------- declarations

public abstract record Decl(Position Pos, bool IsPrivate) : Node(Pos);

public sealed record Param(Position Pos, string Name, TypeRef Type) : Node(Pos);

/// <summary>A record field. <see cref="Init"/> is the optional validation expression.</summary>
public sealed record FieldDecl(Position Pos, string Name, TypeRef Type, Expr? Init) : Node(Pos);

/// <summary>record or entity. <see cref="Inline"/> means it was written as <c>record X(a: A)</c>.</summary>
public sealed record RecordDecl(
    Position Pos,
    bool IsPrivate,
    bool IsEntity,
    string Name,
    IReadOnlyList<FieldDecl> Fields,
    bool Inline,
    IReadOnlyList<string>? TypeParams = null) : Decl(Pos, IsPrivate)
{
    public IReadOnlyList<string> TypeParams { get; init; } = TypeParams ?? Array.Empty<string>();
}

public sealed record Variant(Position Pos, string Name, IReadOnlyList<Param> Params) : Node(Pos);

public sealed record UnionDecl(
    Position Pos,
    bool IsPrivate,
    string Name,
    IReadOnlyList<Variant> Variants,
    IReadOnlyList<string>? TypeParams = null) : Decl(Pos, IsPrivate)
{
    public IReadOnlyList<string> TypeParams { get; init; } = TypeParams ?? Array.Empty<string>();
}

/// <summary>
/// A function signature. <see cref="Effects"/> is null when no '!' clause was written,
/// meaning the effects are to be inferred; an empty list with <see cref="IsPure"/> set
/// is the explicit assertion of no effects.
/// </summary>
public sealed record FnSignature(
    Position Pos,
    string Name,
    IReadOnlyList<Param> Params,
    TypeRef Return,
    IReadOnlyList<string>? Effects,
    bool IsPure,
    IReadOnlyList<string>? TypeParams = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? TypeConstraints = null) : Node(Pos)
{
    public IReadOnlyList<string> TypeParams { get; init; } = TypeParams ?? Array.Empty<string>();
}

public enum FnKind { Fn, Handler }

public sealed record FnDecl(
    Position Pos,
    bool IsPrivate,
    FnKind Kind,
    FnSignature Signature,
    Block Body) : Decl(Pos, IsPrivate);

/// <summary>A host binding line inside an extern: the symbol to call for one target.</summary>
public sealed record ExternBinding(Position Pos, string Target, string Symbol) : Node(Pos);

/// <summary>A catch line inside an extern: a host exception type mapped to an error variant carrying the message.</summary>
public sealed record ExternCatch(Position Pos, string Target, string ExceptionType, string Variant) : Node(Pos);

/// <summary>A function implemented by the host. Effects must be declared; there is no body to infer from.</summary>
public sealed record ExternDecl(
    Position Pos,
    bool IsPrivate,
    FnSignature Signature,
    IReadOnlyList<ExternBinding> Bindings,
    IReadOnlyList<ExternCatch> Catches) : Decl(Pos, IsPrivate);

/// <summary>A service. <see cref="IsResource"/> means it owns external state and defines release.</summary>
public sealed record ServiceDecl(
    Position Pos,
    bool IsPrivate,
    bool Scoped,
    string Name,
    IReadOnlyList<Param> Dependencies,
    IReadOnlyList<FnDecl> Methods,
    bool IsResource = false) : Decl(Pos, IsPrivate);

/// <summary>
/// A shape. Without type parameters it is a structural interface over services. With one,
/// <c>shape Monoid[T]</c>, it is a shape over a type: a type class whose members mention T and
/// whose instances are declared with <c>instance Monoid[Money]</c>.
/// </summary>
public sealed record ShapeDecl(
    Position Pos,
    bool IsPrivate,
    string Name,
    IReadOnlyList<FnSignature> Members,
    IReadOnlyList<string>? TypeParams = null) : Decl(Pos, IsPrivate);

/// <summary>An instance of a shape over a type: the shape's members implemented for <see cref="Target"/>.</summary>
public sealed record InstanceDecl(
    Position Pos,
    bool IsPrivate,
    string Shape,
    TypeRef Target,
    IReadOnlyList<FnDecl> Methods) : Decl(Pos, IsPrivate);

public sealed record RootDecl(
    Position Pos,
    bool IsPrivate,
    string Name,
    IReadOnlyList<Arg> Entries) : Decl(Pos, IsPrivate);

// ---------------------------------------------------------------- statements

public sealed record Block(Position Pos, IReadOnlyList<Stmt> Stmts) : Node(Pos);

public abstract record Stmt(Position Pos) : Node(Pos);

public sealed record BindingStmt(Position Pos, string Name, Expr Value) : Stmt(Pos);

/// <summary>A binding through an irrefutable pattern: <c>(a, b) = pair</c>, <c>[...xs] = v</c>, <c>whole @ (a, _) = pair</c>.</summary>
public sealed record DestructureStmt(Position Pos, Pattern Pattern, Expr Value) : Stmt(Pos);

/// <summary>use x = expr: binds a fresh resource, released at the end of the enclosing block.</summary>
public sealed record UseStmt(Position Pos, string Name, Expr Value) : Stmt(Pos);

public sealed record ExprStmt(Position Pos, Expr Value) : Stmt(Pos);

// ---------------------------------------------------------------- expressions

public abstract record Expr(Position Pos) : Node(Pos);

/// <summary>An argument to a call, or an entry in a composition root. Name is null for positional.</summary>
public sealed record Arg(Position Pos, string? Name, Expr Value) : Node(Pos);

public sealed record NameExpr(Position Pos, string Name) : Expr(Pos);

public sealed record TypeNameExpr(Position Pos, string Name) : Expr(Pos);

public sealed record IntLit(Position Pos, string Text) : Expr(Pos);

public sealed record FloatLit(Position Pos, string Text) : Expr(Pos);

public sealed record StringLit(Position Pos, string Value) : Expr(Pos);

public sealed record BoolLit(Position Pos, bool Value) : Expr(Pos);

/// <summary>[a, b] inline, or a block of "- item" lines when <see cref="IsBlock"/>.</summary>
public sealed record ListLit(Position Pos, IReadOnlyList<Expr> Items, bool IsBlock) : Expr(Pos);

/// <summary>A tuple literal, <c>(a, b)</c>. Always two or more items; one item in parentheses is just grouping.</summary>
public sealed record TupleLit(Position Pos, IReadOnlyList<Expr> Items) : Expr(Pos);

public sealed record MapEntry(Position Pos, Expr Key, Expr Value) : Node(Pos);

/// <summary>{k: v} inline, or a block of "- k: v" lines when <see cref="IsBlock"/>.</summary>
public sealed record MapLit(Position Pos, IReadOnlyList<MapEntry> Entries, bool IsBlock) : Expr(Pos);

public sealed record MemberExpr(Position Pos, Expr Target, string Name) : Expr(Pos);

/// <summary>
/// A call. <see cref="HasParens"/> is false for a bare head like <c>ok</c> whose only
/// arguments are its indented children. <see cref="BlockArgs"/> are the children.
/// </summary>
public sealed record CallExpr(
    Position Pos,
    Expr Callee,
    IReadOnlyList<TypeRef> TypeArgs,
    IReadOnlyList<Arg> Args,
    IReadOnlyList<Arg> BlockArgs,
    bool HasParens) : Expr(Pos);

public sealed record UnaryExpr(Position Pos, string Op, Expr Operand) : Expr(Pos);

public sealed record BinaryExpr(Position Pos, string Op, Expr Left, Expr Right) : Expr(Pos);

/// <summary>Postfix ? on a Result.</summary>
public sealed record PropagateExpr(Position Pos, Expr Inner) : Expr(Pos);

public sealed record LambdaParam(Position Pos, string Name, TypeRef? Type) : Node(Pos);

public sealed record LambdaExpr(
    Position Pos,
    IReadOnlyList<LambdaParam> Params,
    bool TypedParams,
    Block Body,
    bool Inline) : Expr(Pos);

/// <summary>
/// if/then/else. <see cref="Inline"/> means the whole thing was on one line.
/// An else-if chain is an Else block whose single statement is another non-inline IfExpr.
/// </summary>
public sealed record IfExpr(
    Position Pos,
    Expr Condition,
    Block Then,
    Block? Else,
    bool Inline) : Expr(Pos);

public abstract record Pattern(Position Pos) : Node(Pos);

/// <summary>
/// A constructor pattern over a variant or a record. <see cref="Args"/> are positional and
/// must cover every field unless <see cref="Rest"/> (<c>...</c>) ignores the remainder;
/// <see cref="Named"/> entries, <c>field: pattern</c>, pick fields by name and may omit any.
/// </summary>
public sealed record VariantPattern(Position Pos, string Name, IReadOnlyList<Pattern> Args,
    IReadOnlyList<(string Field, Pattern Pattern)>? Named = null, bool Rest = false) : Pattern(Pos)
{
    public IReadOnlyList<(string Field, Pattern Pattern)> NamedArgs => Named ?? Array.Empty<(string, Pattern)>();
}

public sealed record BindPattern(Position Pos, string Name) : Pattern(Pos);

public sealed record WildcardPattern(Position Pos) : Pattern(Pos);

public sealed record LiteralPattern(Position Pos, Expr Literal) : Pattern(Pos);

/// <summary>A vector pattern: fixed items, then optionally <c>...rest</c> binding (or <c>..._</c> ignoring) the remainder.</summary>
public sealed record ListPattern(Position Pos, IReadOnlyList<Pattern> Items, Pattern? Rest) : Pattern(Pos);

/// <summary>A tuple pattern, <c>(a, b)</c>.</summary>
public sealed record TuplePattern(Position Pos, IReadOnlyList<Pattern> Items) : Pattern(Pos);

/// <summary>An as-pattern, <c>name @ pattern</c>: binds the whole value and matches the inner pattern.</summary>
public sealed record AsPattern(Position Pos, string Name, Pattern Inner) : Pattern(Pos);

/// <summary>A match arm. <see cref="Guard"/> is the optional <c>if</c> condition between the pattern and <c>=&gt;</c>.</summary>
public sealed record MatchArm(Position Pos, Pattern Pattern, Block Body, bool Inline, Expr? Guard = null) : Node(Pos);

public sealed record MatchExpr(Position Pos, Expr Scrutinee, IReadOnlyList<MatchArm> Arms) : Expr(Pos);

public sealed record WithField(Position Pos, IReadOnlyList<string> Path, Expr Value) : Node(Pos);

/// <summary>x with { a: 1 } inline, or the block form when <see cref="IsBlock"/>.</summary>
public sealed record WithExpr(
    Position Pos,
    Expr Target,
    IReadOnlyList<WithField> Fields,
    bool IsBlock) : Expr(Pos);
