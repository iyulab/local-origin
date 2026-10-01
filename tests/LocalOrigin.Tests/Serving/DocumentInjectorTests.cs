using System.Text;
using LocalOrigin.AspNetCore;

namespace LocalOrigin.Tests.Serving;

public sealed class DocumentInjectorTests
{
    private const string Tag = "<script>1</script>";

    private static string Served(string document) => Encoding.UTF8.GetString(DocumentInjector.Inject(Encoding.UTF8.GetBytes(document), Tag).Body);

    [Fact]
    public void Markup_goes_after_the_doctype_so_the_page_stays_in_standards_mode()
    {
        Assert.Equal("<!DOCTYPE html>" + Tag + "<p>hi</p>", Served("<!DOCTYPE html><p>hi</p>"));
    }

    [Fact]
    public void Leading_whitespace_and_comments_before_the_doctype_are_kept_in_front()
    {
        Assert.Equal("  <!-- a -->\n<!doctype html>" + Tag + "x", Served("  <!-- a -->\n<!doctype html>x"));
    }

    [Fact]
    public void A_document_without_a_doctype_gets_the_markup_first()
    {
        Assert.Equal(Tag + "<p>x</p>", Served("<p>x</p>"));
    }

    [Fact]
    public void The_stored_bytes_are_unchanged_and_follow_the_markup_verbatim()
    {
        var stored = Encoding.UTF8.GetBytes("<!doctype html><p>한글</p>");
        var copy = stored.ToArray();

        var served = DocumentInjector.Inject(stored, Tag);

        Assert.Equal(copy, stored);
        Assert.Equal(stored.Length + Tag.Length, served.Body.Length);
        Assert.Equal("utf-8", served.Charset);
    }

    [Fact]
    public void A_utf8_byte_order_mark_stays_first()
    {
        var stored = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("<!doctype html>x")).ToArray();

        var served = DocumentInjector.Inject(stored, Tag);

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, served.Body[..3]);
        Assert.Equal("<!doctype html>" + Tag + "x", Encoding.UTF8.GetString(served.Body[3..]));
        Assert.Equal("utf-8", served.Charset);
    }

    [Fact]
    public void A_declared_charset_is_the_one_served()
    {
        var stored = Encoding.Latin1.GetBytes("<!doctype html><meta charset=\"windows-1252\"><p>café</p>");

        var served = DocumentInjector.Inject(stored, Tag);

        Assert.Equal("windows-1252", served.Charset);
        Assert.Equal(stored[^5..], served.Body[^5..]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_utf16_document_is_injected_in_its_own_encoding(bool littleEndian)
    {
        var encoding = littleEndian ? Encoding.Unicode : Encoding.BigEndianUnicode;
        var stored = encoding.GetPreamble().Concat(encoding.GetBytes("<!doctype html><p>한</p>")).ToArray();

        var served = DocumentInjector.Inject(stored, Tag);

        Assert.Equal(littleEndian ? "utf-16le" : "utf-16be", served.Charset);
        Assert.Equal("<!doctype html>" + Tag + "<p>한</p>", encoding.GetString(served.Body[2..]));
    }

    [Fact]
    public void Markup_that_is_not_ascii_is_refused()
    {
        Assert.Throws<ArgumentException>(() => DocumentInjector.Inject("x"u8, "<script>'é'</script>"));
    }
}
