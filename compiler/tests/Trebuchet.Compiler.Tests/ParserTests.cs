using Trebuchet.Compiler.Syntax;
using Xunit;

namespace Trebuchet.Compiler.Tests;

public class ParserTests
{
    private static SourceFile Parse(string src) => Parser.ParseFile(src);

    private static Expr ParseExpr(string src)
    {
        var file = Parse($"fn f() -> Unit\n  {src}\n");
        var fn = (FnDecl)file.Decls[0];
        return ((ExprStmt)fn.Body.Stmts[0]).Value;
    }

    [Fact]
    public void ParsesModuleHeaderAndUses()
    {
        var f = Parse("module a.b.c\n\nuse x.y\nuse z\n");
        Assert.Equal("a.b.c", f.Module!.ToString());
        Assert.Equal(new[] { "x.y", "z" }, f.Uses.Select(u => u.Name.ToString()));
    }

    [Fact]
    public void ParsesInlineAndBlockRecords()
    {
        var f = Parse("record A(x: Int)\n\nrecord B\n  y: Vector[Line]\n  z: Map[String, Int]\n");
        var a = (RecordDecl)f.Decls[0];
        var b = (RecordDecl)f.Decls[1];
        Assert.True(a.Inline);
        Assert.Single(a.Fields);
        Assert.False(b.Inline);
        Assert.Equal(2, b.Fields.Count);
        var z = (NamedType)b.Fields[1].Type;
        Assert.Equal("Map", z.Name);
        Assert.Equal(2, z.Args.Count);
    }

    [Fact]
    public void ParsesFieldInit()
    {
        var f = Parse("record P\n  name: String\n    init => value.trim()\n");
        var r = (RecordDecl)f.Decls[0];
        Assert.IsType<CallExpr>(r.Fields[0].Init);
    }

    [Fact]
    public void ParsesSignatureWithEffects()
    {
        var f = Parse("fn fetch(id: UserId) -> Result[User, E] ! Nondet Suspend\n  x\n");
        var fn = (FnDecl)f.Decls[0];
        Assert.Equal(new[] { "Nondet", "Suspend" }, fn.Signature.Effects);
        Assert.Equal("Result", ((NamedType)fn.Signature.Return).Name);
    }

    [Fact]
    public void EffectsAreOptionalAndPureIsExplicit()
    {
        var f = Parse("fn a() -> Int\n  1\n\nfn b() -> Int ! Pure\n  2\n\nfn c() -> Int ! Nondet\n  3\n");
        var a = (FnDecl)f.Decls[0];
        var b = (FnDecl)f.Decls[1];
        var c = (FnDecl)f.Decls[2];
        Assert.Null(a.Signature.Effects);
        Assert.False(a.Signature.IsPure);
        Assert.True(b.Signature.IsPure);
        Assert.Empty(b.Signature.Effects!);
        Assert.Equal(new[] { "Nondet" }, c.Signature.Effects);
    }

    [Fact]
    public void ShapeMembersMustDeclareEffects()
    {
        var ex = Assert.Throws<SyntaxException>(() => Parse("shape Clock\n  fn now() -> Instant\n"));
        Assert.Contains("cannot be inferred", ex.Message);
        var ok = Parse("shape Clock\n  fn now() -> Instant ! Nondet\n  fn zero() -> Instant ! Pure\n");
        Assert.Equal(2, ((ShapeDecl)ok.Decls[0]).Members.Count);
    }

    [Fact]
    public void PureHandlerIsRejected()
    {
        var ex = Assert.Throws<SyntaxException>(() => Parse("handler h(x: Int) -> Int ! Pure\n  x\n"));
        Assert.Contains("cannot be Pure", ex.Message);
        var mixed = Assert.Throws<SyntaxException>(() => Parse("fn f() -> Int ! Pure Nondet\n  1\n"));
        Assert.Contains("cannot be combined", mixed.Message);
    }

    [Fact]
    public void ParsesFunctionTypedDependency()
    {
        var f = Parse("service S(ids: fn() -> Id ! Nondet, open: fn(Path) -> Stream ! Nondet Suspend)\n  fn m() -> Unit\n    x\n");
        var s = (ServiceDecl)f.Decls[0];
        var ids = (FnType)s.Dependencies[0].Type;
        Assert.Empty(ids.Params);
        Assert.Equal(new[] { "Nondet" }, ids.Effects);
        var open = (FnType)s.Dependencies[1].Type;
        Assert.Single(open.Params);
        Assert.Equal(new[] { "Nondet", "Suspend" }, open.Effects);
    }

    [Fact]
    public void BinaryPrecedence()
    {
        var e = (BinaryExpr)ParseExpr("a + b * c == d and not e or f");
        Assert.Equal("or", e.Op);
        var and = (BinaryExpr)e.Left;
        Assert.Equal("and", and.Op);
        var cmp = (BinaryExpr)and.Left;
        Assert.Equal("==", cmp.Op);
        var add = (BinaryExpr)cmp.Left;
        Assert.Equal("+", add.Op);
        Assert.Equal("*", ((BinaryExpr)add.Right).Op);
        Assert.Equal("not", ((UnaryExpr)and.Right).Op);
    }

    [Fact]
    public void TrailingChildrenBecomeBlockArgs()
    {
        var e = (CallExpr)ParseExpr("fold(xs, 0)\n    \\acc, x -> acc + x");
        Assert.True(e.HasParens);
        Assert.Equal(2, e.Args.Count);
        Assert.Single(e.BlockArgs);
        Assert.IsType<LambdaExpr>(e.BlockArgs[0].Value);
    }

    [Fact]
    public void BareHeadWithSequenceChildrenIsACall()
    {
        var e = (CallExpr)ParseExpr("ok\n    - A(1)\n    - B(2)");
        Assert.False(e.HasParens);
        Assert.Equal("ok", ((NameExpr)e.Callee).Name);
        var list = (ListLit)e.BlockArgs[0].Value;
        Assert.True(list.IsBlock);
        Assert.Equal(2, list.Items.Count);
    }

    [Fact]
    public void ParsesBlockAndInlineWith()
    {
        var block = (WithExpr)ParseExpr("o with\n    a.b.c: 1\n    d: 2");
        Assert.True(block.IsBlock);
        Assert.Equal(new[] { "a", "b", "c" }, block.Fields[0].Path);
        var inline = (WithExpr)ParseExpr("o with { status: s }");
        Assert.False(inline.IsBlock);
    }

    [Fact]
    public void ParsesIfChain()
    {
        var e = (IfExpr)ParseExpr("if a then\n    x\n  else if b then\n    y\n  else\n    z");
        Assert.False(e.Inline);
        var nested = (IfExpr)((ExprStmt)e.Else!.Stmts[0]).Value;
        Assert.NotNull(nested.Else);
        var inline = (IfExpr)ParseExpr("if a then x else y");
        Assert.True(inline.Inline);
    }

    [Fact]
    public void ParsesMatchWithInlineAndBlockArms()
    {
        var e = (MatchExpr)ParseExpr("match ev\n    A(x) => f(x)\n    B =>\n      g\n    _ => h");
        Assert.Equal(3, e.Arms.Count);
        Assert.True(e.Arms[0].Inline);
        Assert.False(e.Arms[1].Inline);
        Assert.IsType<WildcardPattern>(e.Arms[2].Pattern);
    }

    [Fact]
    public void ParsesLambdaForms()
    {
        Assert.Empty(((LambdaExpr)ParseExpr("\\-> x")).Params);
        var typed = (LambdaExpr)ParseExpr("\\(a: Int, b: Int) -> a + b");
        Assert.True(typed.TypedParams);
        var block = (LambdaExpr)ParseExpr("\\a ->\n    f(a)");
        Assert.False(block.Inline);
    }

    [Fact]
    public void ParsesTypeArgumentsOnCall()
    {
        var e = (CallExpr)ParseExpr("json.decode[RoomEvent](payload)");
        Assert.Single(e.TypeArgs);
        Assert.Equal("RoomEvent", ((NamedType)e.TypeArgs[0]).Name);
    }

    [Fact]
    public void ParsesPropagateAndMemberChain()
    {
        var e = (PropagateExpr)ParseExpr("repo.load(id)?");
        var call = (CallExpr)e.Inner;
        Assert.Equal("load", ((MemberExpr)call.Callee).Name);
    }

    [Fact]
    public void ParsesRootWithNestedEntriesAndMapItems()
    {
        var f = Parse("root main\n  clock: SystemClock\n  repo: PgStore\n    config: cfg()\n  rooms:\n    - A(1): B(2)\n");
        var root = (RootDecl)f.Decls[0];
        Assert.Equal(3, root.Entries.Count);
        var repo = (CallExpr)root.Entries[1].Value;
        Assert.False(repo.HasParens);
        Assert.Equal("config", repo.BlockArgs[0].Name);
        var rooms = (MapLit)root.Entries[2].Value;
        Assert.True(rooms.IsBlock);
    }

    [Fact]
    public void ReportsPositionOnError()
    {
        var ex = Assert.Throws<SyntaxException>(() => Parse("record\n"));
        Assert.Equal(1, ex.Position.Line);
        Assert.Contains("record name", ex.Message);
    }
}
