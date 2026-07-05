using AiRecall.AppReader.OneNote;
using AiRecall.Core.Configuration;

namespace AiRecall.Core.Tests.AppReaders;

/// <summary>
/// Tests fuer <see cref="OneNotePageXmlToMarkdown"/> (Spec 0010, Cluster 3).
/// Mapping OneNote-XML (xs2013) → Markdown. Alle Tests sind pure-function
/// (keine COM-Calls, kein IO).
/// </summary>
public class OneNotePageXmlToMarkdownTests
{
    private const string Ns = "http://schemas.microsoft.com/office/onenote/2013/onenote";

    private static OneNoteConfig DefaultConfig() => new();   // IncludeImages=false, IncludeTags=true (Default)
    private static OneNoteConfig AllIncluded() => new() { IncludeImages = true, IncludeTags = true };
    private static OneNoteConfig AllExcluded() => new() { IncludeImages = false, IncludeTags = false };

    private static string WrapInNotebook(params string[] pages) =>
        $"<one:Notebook xmlns:one=\"{Ns}\" ID=\"NB1\" name=\"Test\"><one:Section ID=\"SEC1\" name=\"Test\">{string.Join("", pages)}</one:Section></one:Notebook>";

    // ============================================================================
    // ConvertBody — Basis
    // ============================================================================

    [Fact]
    public void ConvertBody_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, OneNotePageXmlToMarkdown.ConvertBody("",      DefaultConfig()));
        Assert.Equal(string.Empty, OneNotePageXmlToMarkdown.ConvertBody(null!,   DefaultConfig()));
        Assert.Equal(string.Empty, OneNotePageXmlToMarkdown.ConvertBody("not xml", DefaultConfig()));
    }

    [Fact]
    public void ConvertBody_SingleTextRun_ReturnsPlainText()
    {
        var xml = WrapInNotebook(
            """<one:Page ID="P1" name="Page"><one:Outline><one:OE><one:T>Hallo Welt</one:T></one:OE></one:Outline></one:Page>""");

        var md = OneNotePageXmlToMarkdown.ConvertBody(xml, DefaultConfig());

        Assert.Contains("Hallo Welt", md);
    }

    [Fact]
    public void ConvertBody_MultipleTextRuns_AreConcatenated()
    {
        var xml = """
            <one:Page xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote" ID="P1" name="P">
              <one:Outline><one:OE>
                <one:T>Erster </one:T>
                <one:T>zweiter </one:T>
                <one:T>dritter</one:T>
              </one:OE></one:Outline>
            </one:Page>
            """;

        var md = OneNotePageXmlToMarkdown.ConvertBody(xml, DefaultConfig());

        Assert.Contains("Erster zweiter dritter", md);
    }

    [Fact]
    public void ConvertBody_DecodesHtmlEntities()
    {
        var xml = """
            <one:Page xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote" ID="P1" name="P">
              <one:Outline><one:OE><one:T>AT&amp;T &lt;3 &quot;test&quot;</one:T></one:OE></one:Outline>
            </one:Page>
            """;

        var md = OneNotePageXmlToMarkdown.ConvertBody(xml, DefaultConfig());

        Assert.Contains("AT&T <3 \"test\"", md);
        Assert.DoesNotContain("&amp;", md);
    }

    // ============================================================================
    // ConvertBody — Images
    // ============================================================================

    [Fact]
    public void ConvertBody_ImageIncludeImagesFalse_IsOmitted()
    {
        var xml = """
            <one:Page xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote" ID="P1" name="P">
              <one:Image alt="cat" src="C:\oneimg.png"/>
              <one:Outline><one:OE><one:T>nach image</one:T></one:OE></one:Outline>
            </one:Page>
            """;

        var md = OneNotePageXmlToMarkdown.ConvertBody(xml, DefaultConfig());

        Assert.DoesNotContain("![cat]", md);
        Assert.Contains("nach image", md);
    }

    [Fact]
    public void ConvertBody_ImageIncludeImagesTrue_RendersAsMarkdown()
    {
        var xml = """
            <one:Page xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote" ID="P1" name="P">
              <one:Image alt="cat" src="C:\Users\Alice\Pictures\cat.png"/>
              <one:Outline><one:OE><one:T>nach image</one:T></one:OE></one:Outline>
            </one:Page>
            """;

        var md = OneNotePageXmlToMarkdown.ConvertBody(xml, AllIncluded());

        Assert.Contains("![cat](cat.png)", md);
    }

    // ============================================================================
    // ConvertBody — Tags
    // ============================================================================

    [Fact]
    public void ConvertBody_ToDoTagEmpty_RendersAsOpenBracket()
    {
        var xml = """
            <one:Page xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote" ID="P1" name="P">
              <one:Outline><one:OE>
                <one:T>Buy milk</one:T>
                <one:Tag type="to-do:empty"/>
              </one:OE></one:Outline>
            </one:Page>
            """;

        var md = OneNotePageXmlToMarkdown.ConvertBody(xml, DefaultConfig());

        // Default: IncludeTags=true → Tag rendert
        Assert.Contains("Buy milk", md);
        Assert.Contains("[ ]", md);
    }

    [Fact]
    public void ConvertBody_ToDoTagComplete_RendersAsXBracket()
    {
        var xml = """
            <one:Page xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote" ID="P1" name="P">
              <one:Outline><one:OE>
                <one:T>Done thing</one:T>
                <one:Tag type="to-do:complete"/>
              </one:OE></one:Outline>
            </one:Page>
            """;

        var md = OneNotePageXmlToMarkdown.ConvertBody(xml, DefaultConfig());

        Assert.Contains("[x]", md);
    }

    [Fact]
    public void ConvertBody_CustomTag_RendersAsHashTag()
    {
        var xml = """
            <one:Page xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote" ID="P1" name="P">
              <one:Tag type="priority"/>
              <one:Outline><one:OE><one:T>Body</one:T></one:OE></one:Outline>
            </one:Page>
            """;

        var md = OneNotePageXmlToMarkdown.ConvertBody(xml, DefaultConfig());

        Assert.Contains("#priority", md);
    }

    [Fact]
    public void ConvertBody_TagsIncludeTagsFalse_AreOmitted()
    {
        var xml = """
            <one:Page xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote" ID="P1" name="P">
              <one:Outline><one:OE>
                <one:T>Body</one:T>
                <one:Tag type="to-do:empty"/>
              </one:OE></one:Outline>
            </one:Page>
            """;

        var md = OneNotePageXmlToMarkdown.ConvertBody(xml, AllExcluded());

        Assert.DoesNotContain("[ ]", md);
        Assert.Contains("Body", md);
    }

    // ============================================================================
    // ConvertBody — InkContent + InsertedFile
    // ============================================================================

    [Fact]
    public void ConvertBody_InkContent_RendersAsHandschriftlich()
    {
        var xml = """
            <one:Page xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote" ID="P1" name="P">
              <one:Outline><one:OE>
                <one:T>Text</one:T>
                <one:InkContent/>
              </one:OE></one:Outline>
            </one:Page>
            """;

        var md = OneNotePageXmlToMarkdown.ConvertBody(xml, DefaultConfig());

        Assert.Contains("Text", md);
        Assert.Contains("*(handschriftlich)*", md);
    }

    [Fact]
    public void ConvertBody_InsertedFileAtPageLevel_RendersAsAttachedFile()
    {
        var xml = """
            <one:Page xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote" ID="P1" name="P">
              <one:InsertedFile path="C:\Users\Alice\Documents\report.pdf"/>
              <one:Outline><one:OE><one:T>Body</one:T></one:OE></one:Outline>
            </one:Page>
            """;

        var md = OneNotePageXmlToMarkdown.ConvertBody(xml, DefaultConfig());

        Assert.Contains("*Attached File:* `report.pdf`", md);
        Assert.DoesNotContain("C:\\Users\\Alice", md);  // Pfad nicht im Output
    }

    // ============================================================================
    // ConvertBody — Tables
    // ============================================================================

    [Fact]
    public void ConvertBody_TableOneRow_RendersHeaderAndSeparator()
    {
        var xml = """
            <one:Page xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote" ID="P1" name="P">
              <one:Table>
                <one:Row><one:Cell><one:OE><one:T>A</one:T></one:OE></one:Cell><one:Cell><one:OE><one:T>B</one:T></one:OE></one:Cell></one:Row>
              </one:Table>
            </one:Page>
            """;

        var md = OneNotePageXmlToMarkdown.ConvertBody(xml, DefaultConfig());

        Assert.Contains("| A | B |", md);
        Assert.Contains("| --- |", md);
    }

    [Fact]
    public void ConvertBody_TableMultipleRows_HeaderPlusBody()
    {
        var xml = """
            <one:Page xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote" ID="P1" name="P">
              <one:Table>
                <one:Row>
                  <one:Cell><one:OE><one:T>Name</one:T></one:OE></one:Cell>
                  <one:Cell><one:OE><one:T>Age</one:T></one:OE></one:Cell>
                </one:Row>
                <one:Row>
                  <one:Cell><one:OE><one:T>Alice</one:T></one:OE></one:Cell>
                  <one:Cell><one:OE><one:T>40</one:T></one:OE></one:Cell>
                </one:Row>
                <one:Row>
                  <one:Cell><one:OE><one:T>Bob</one:T></one:OE></one:Cell>
                  <one:Cell><one:OE><one:T>33</one:T></one:OE></one:Cell>
                </one:Row>
              </one:Table>
            </one:Page>
            """;

        var md = OneNotePageXmlToMarkdown.ConvertBody(xml, DefaultConfig());

        Assert.Contains("| Name | Age |", md);
        Assert.Contains("| Alice | 40 |", md);
        Assert.Contains("| Bob | 33 |", md);
    }

    // ============================================================================
    // ConvertBody — Bullets & Nested OE
    // ============================================================================

    [Fact]
    public void ConvertBody_BulletStyleOe_PrefixedWithDash()
    {
        var xml = """
            <one:Page xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote" ID="P1" name="P">
              <one:Outline><one:OE style="list:Bullet">
                <one:T>Item 1</one:T>
              </one:OE></one:Outline>
            </one:Page>
            """;

        var md = OneNotePageXmlToMarkdown.ConvertBody(xml, DefaultConfig());

        // Bullet hat "- " prefix
        Assert.Contains("- Item 1", md);
    }

    [Fact]
    public void ConvertBody_NonBulletOe_NoPrefix()
    {
        var xml = """
            <one:Page xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote" ID="P1" name="P">
              <one:Outline><one:OE>
                <one:T>Fliesstext</one:T>
              </one:OE></one:Outline>
            </one:Page>
            """;

        var md = OneNotePageXmlToMarkdown.ConvertBody(xml, DefaultConfig());

        Assert.DoesNotContain("- Fliesstext", md);
        Assert.Contains("Fliesstext", md);
    }

    [Fact]
    public void ConvertBody_NestedBulletOe_IncreasesIndent()
    {
        var xml = """
            <one:Page xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote" ID="P1" name="P">
              <one:Outline><one:OE style="list:Bullet">
                <one:T>Top</one:T>
                <one:OE style="list:Bullet">
                  <one:T>Sub</one:T>
                </one:OE>
              </one:OE></one:Outline>
            </one:Page>
            """;

        var md = OneNotePageXmlToMarkdown.ConvertBody(xml, DefaultConfig());

        Assert.Contains("- Top", md);
        Assert.Contains("  - Sub", md);  // 2 Spaces indent
    }

    // ============================================================================
    // ConvertBody — Edge cases
    // ============================================================================

    [Fact]
    public void ConvertBody_MultipleOutlines_AreSeparatedByBlankLine()
    {
        var xml = """
            <one:Page xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote" ID="P1" name="P">
              <one:Outline><one:OE><one:T>Erste Outline</one:T></one:OE></one:Outline>
              <one:Outline><one:OE><one:T>Zweite Outline</one:T></one:OE></one:Outline>
            </one:Page>
            """;

        var md = OneNotePageXmlToMarkdown.ConvertBody(xml, DefaultConfig());

        Assert.Contains("Erste Outline", md);
        Assert.Contains("Zweite Outline", md);
        Assert.Contains("\n\n", md);   // Block-Separator zwischen Outlines
    }

    // ============================================================================
    // ExtractPageTitle
    // ============================================================================

    [Fact]
    public void ExtractPageTitle_ValidXml_ReturnsNameAttribute()
    {
        const string xml = """
            <one:Page xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote" ID="P1" name="My Page Title"/>
            """;
        Assert.Equal("My Page Title", OneNotePageXmlToMarkdown.ExtractPageTitle(xml));
    }

    [Fact]
    public void ExtractPageTitle_NullOrEmptyOrInvalid_ReturnsNull()
    {
        Assert.Null(OneNotePageXmlToMarkdown.ExtractPageTitle(null!));
        Assert.Null(OneNotePageXmlToMarkdown.ExtractPageTitle(""));
        Assert.Null(OneNotePageXmlToMarkdown.ExtractPageTitle("not valid xml"));
    }

    [Fact]
    public void ExtractPageTitle_NoNameAttribute_ReturnsNull()
    {
        const string xml = """
            <one:Page xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote" ID="P1"/>
            """;
        Assert.Null(OneNotePageXmlToMarkdown.ExtractPageTitle(xml));
    }

    // ============================================================================
    // ExtractLastModified
    // ============================================================================

    [Fact]
    public void ExtractLastModified_ValidXml_ReturnsAttribute()
    {
        const string xml = """
            <one:Page xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote" ID="P1" lastModifiedTime="2026-07-05T12:34:56Z"/>
            """;
        Assert.Equal("2026-07-05T12:34:56Z", OneNotePageXmlToMarkdown.ExtractLastModified(xml));
    }

    [Fact]
    public void ExtractLastModified_InvalidXml_ReturnsNull()
    {
        Assert.Null(OneNotePageXmlToMarkdown.ExtractLastModified(null!));
        Assert.Null(OneNotePageXmlToMarkdown.ExtractLastModified(""));
        Assert.Null(OneNotePageXmlToMarkdown.ExtractLastModified("not valid xml"));
    }

    // ============================================================================
    // ExtractInsertedFileNames
    // ============================================================================

    [Fact]
    public void ExtractInsertedFileNames_MultiplePaths_ReturnsDedupedFilenames()
    {
        const string xml = """
            <one:Page xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote" ID="P1" name="P">
              <one:InsertedFile path="C:\Users\Alice\report.pdf"/>
              <one:InsertedFile path="C:\Users\Bob\spec.docx"/>
              <one:InsertedFile path="C:\Users\Alice\report.pdf"/>
            </one:Page>
            """;

        var names = OneNotePageXmlToMarkdown.ExtractInsertedFileNames(xml);

        Assert.Equal(2, names.Count);
        Assert.Contains("report.pdf", names);
        Assert.Contains("spec.docx",  names);
    }

    [Fact]
    public void ExtractInsertedFileNames_NoPathAttr_Ignored()
    {
        const string xml = """
            <one:Page xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote" ID="P1" name="P">
              <one:InsertedFile/>
            </one:Page>
            """;
        Assert.Empty(OneNotePageXmlToMarkdown.ExtractInsertedFileNames(xml));
    }

    [Fact]
    public void ExtractInsertedFileNames_InvalidOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(OneNotePageXmlToMarkdown.ExtractInsertedFileNames(null!));
        Assert.Empty(OneNotePageXmlToMarkdown.ExtractInsertedFileNames(""));
        Assert.Empty(OneNotePageXmlToMarkdown.ExtractInsertedFileNames("not valid xml"));
    }

    [Fact]
    public void ExtractInsertedFileNames_MixedPathsWithSlashes_ParsesCorrectly()
    {
        const string xml = """
            <one:Page xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote" ID="P1" name="P">
              <one:InsertedFile path="C:/Windows/System32/foo.dll"/>
              <one:InsertedFile path="D:\\backup\\bar.txt"/>
            </one:Page>
            """;
        var names = OneNotePageXmlToMarkdown.ExtractInsertedFileNames(xml);
        Assert.Equal(2, names.Count);
        Assert.Contains("foo.dll", names);
        Assert.Contains("bar.txt", names);
    }

    // ============================================================================
    // OneNoteHierarchyInfo — PageIdShort
    // ============================================================================

    [Fact]
    public void HierarchyInfo_PageIdShort_ReturnsFirst8CharsAfterStrippingDashes()
    {
        var info = new OneNoteHierarchyInfo(
            PageId: "AB12CD34-1234-5678-90AB-CDEF12345678",
            PageTitle: "T", SectionId: "S", SectionTitle: "ST",
            NotebookId: "N", NotebookTitle: "NB",
            LastModified: DateTime.MinValue);

        Assert.Equal("AB12CD34", info.PageIdShort);
    }

    [Fact]
    public void HierarchyInfo_PageIdShort_LessThan8Chars_ReturnsAll()
    {
        var info = new OneNoteHierarchyInfo(
            PageId: "ABC",
            PageTitle: "T", SectionId: "S", SectionTitle: "ST",
            NotebookId: "N", NotebookTitle: "NB",
            LastModified: DateTime.MinValue);

        Assert.Equal("ABC", info.PageIdShort);
    }

    [Fact]
    public void HierarchyInfo_HasMinimumInfo_ChecksPageId()
    {
        var withId = new OneNoteHierarchyInfo("P", "", "", "", "", "", DateTime.MinValue);
        Assert.True(withId.HasMinimumInfo);

        var withoutId = new OneNoteHierarchyInfo("", "", "", "", "", "", DateTime.MinValue);
        Assert.False(withoutId.HasMinimumInfo);
    }
}
