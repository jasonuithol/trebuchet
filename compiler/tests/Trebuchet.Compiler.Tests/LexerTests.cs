using Trebuchet.Compiler.Syntax;
using Xunit;

namespace Trebuchet.Compiler.Tests;

public class LexerTests
{
    private static string Kinds(string src) =>
        string.Join(" ", Lexer.Tokenize(src).Select(t => t.Kind switch
        {
            TokenKind.Newline => "NL",
            TokenKind.Indent => "IN",
            TokenKind.Dedent => "DE",
            TokenKind.EndOfFile => "EOF",
            _ => t.Text,
        }));

    [Fact]
    public void EmitsIndentAndDedentForNestedLines()
    {
        var src = "a\n  b\n    c\n  d\ne\n";
        Assert.Equal("a NL IN b NL IN c NL DE d NL DE e NL EOF", Kinds(src));
    }

    [Fact]
    public void BlankAndCommentLinesDoNotAffectIndentation()
    {
        var src = "a\n\n  // comment\n  b\n\n";
        Assert.Equal("a NL IN b NL DE EOF", Kinds(src));
    }

    [Fact]
    public void ClosesAllOpenBlocksAtEndOfFile()
    {
        Assert.Equal("a NL IN b NL IN c NL DE DE EOF", Kinds("a\n  b\n    c"));
    }

    [Fact]
    public void NewlinesInsideBracketsAreIgnored()
    {
        var src = "f(a,\n    b)\nc\n";
        Assert.Equal("f ( a , b ) NL c NL EOF", Kinds(src));
    }

    [Fact]
    public void RejectsTabsForIndentation()
    {
        var ex = Assert.Throws<SyntaxException>(() => Lexer.Tokenize("a\n\tb\n"));
        Assert.Contains("tabs", ex.Message);
    }

    [Fact]
    public void RejectsDedentToUnknownLevel()
    {
        var ex = Assert.Throws<SyntaxException>(() => Lexer.Tokenize("a\n    b\n  c\n"));
        Assert.Contains("unindent", ex.Message);
    }

    [Fact]
    public void DistinguishesTypeNamesKeywordsAndIdentifiers()
    {
        var toks = Lexer.Tokenize("fn Order order with");
        Assert.Equal(new[] { TokenKind.KwFn, TokenKind.TypeName, TokenKind.Identifier, TokenKind.KwWith, TokenKind.Newline, TokenKind.EndOfFile },
            toks.Select(t => t.Kind).ToArray());
    }

    [Fact]
    public void LexesTwoCharacterOperators()
    {
        Assert.Equal("-> => == != <= >= - = < > NL EOF", Kinds("-> => == != <= >= - = < >"));
    }

    [Fact]
    public void UnescapesStrings()
    {
        var t = Lexer.Tokenize("\"a\\n\\\"b\\\"\"")[0];
        Assert.Equal(TokenKind.String, t.Kind);
        Assert.Equal("a\n\"b\"", t.Text);
    }
}
