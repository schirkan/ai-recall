using AiRecall.AppReader.OneNote;

namespace AiRecall.Core.Tests.AppReaders;

/// <summary>
/// Tests fuer die COM-unabhaengigen Helpers in <see cref="OneNoteComInterop"/>:
/// XML-Parser <see cref="OneNoteComInterop.ParseIsCurrentlyViewed"/> und
/// <see cref="OneNoteComInterop.ParseSelfHierarchyXml"/>. Diese Methoden sind
/// <c>internal</c> und werden via <c>InternalsVisibleTo</c> sichtbar gemacht.
///
/// <para>Die eigentlichen COM-Calls (GetActiveObject, GetHierarchy, …) erfordern
/// installiertes OneNote und werden mit <c>[Trait("Integration", "OneNote")]</c>
/// markiert, falls spaeter hinzugefuegt.</para>
/// </summary>
public class OneNoteComInteropTests
{
    private const string Ns = "http://schemas.microsoft.com/office/onenote/2013/onenote";

    private const string SampleNotebookWithCurrentlyViewed = """
        <?xml version="1.0" encoding="utf-16"?>
        <one:Notebooks xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote">
          <one:Notebook ID="NB1" name="Work Notebook">
            <one:Section ID="SEC1" name="Active Section">
              <one:Page ID="PG-ACTIVE-1234-5678-90AB-CDEF00000001"
                        name="Active Page"
                        lastModifiedTime="2026-07-05T18:34:12Z"
                        isCurrentlyViewed="true"/>
              <one:Page ID="PG-OTHER-1234-5678-90AB-CDEF00000002"
                        name="Other Page"
                        lastModifiedTime="2026-07-04T10:00:00Z"/>
            </one:Section>
            <one:Section ID="SEC2" name="Archive Section">
              <one:Page ID="PG-ARCHIVE-1234-5678-90AB-CDEF00000003"
                        name="Old Page"
                        lastModifiedTime="2025-01-01T00:00:00Z"
                        isCurrentlyViewed="false"/>
            </one:Section>
          </one:Notebook>
        </one:Notebooks>
        """;

    // ============================================================================
    // ParseIsCurrentlyViewed
    // ============================================================================

    [Fact]
    public void ParseIsCurrentlyViewed_FindsActivePage_ReturnsHierarchy()
    {
        var info = OneNoteComInterop.ParseIsCurrentlyViewed(SampleNotebookWithCurrentlyViewed);

        Assert.NotNull(info);
        Assert.Equal("PG-ACTIVE-1234-5678-90AB-CDEF00000001", info!.PageId);
        Assert.Equal("Active Page",                          info.PageTitle);
        Assert.Equal("SEC1",                                 info.SectionId);
        Assert.Equal("Active Section",                       info.SectionTitle);
        Assert.Equal("NB1",                                  info.NotebookId);
        Assert.Equal("Work Notebook",                        info.NotebookTitle);
        Assert.Equal(new DateTime(2026, 7, 5, 18, 34, 12, DateTimeKind.Utc), info.LastModified.ToUniversalTime());
    }

    [Fact]
    public void ParseIsCurrentlyViewed_NoCurrentlyViewed_ReturnsNull()
    {
        const string xml = """
            <one:Notebooks xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote">
              <one:Notebook ID="NB1" name="X">
                <one:Section ID="SEC1" name="Y">
                  <one:Page ID="PG1" name="NoActive" isCurrentlyViewed="false"/>
                </one:Section>
              </one:Notebook>
            </one:Notebooks>
            """;
        Assert.Null(OneNoteComInterop.ParseIsCurrentlyViewed(xml));
    }

    [Fact]
    public void ParseIsCurrentlyViewed_InvalidXml_ReturnsNull()
    {
        Assert.Null(OneNoteComInterop.ParseIsCurrentlyViewed("<not valid xml"));
        Assert.Null(OneNoteComInterop.ParseIsCurrentlyViewed(string.Empty));
        Assert.Null(OneNoteComInterop.ParseIsCurrentlyViewed(""));
    }

    [Fact]
    public void ParseIsCurrentlyViewed_MissingIDOnPage_ReturnsNull()
    {
        const string xml = """
            <one:Notebooks xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote">
              <one:Notebook ID="NB1" name="X">
                <one:Section ID="SEC1" name="Y">
                  <one:Page name="NoID" isCurrentlyViewed="true"/>
                </one:Section>
              </one:Notebook>
            </one:Notebooks>
            """;
        Assert.Null(OneNoteComInterop.ParseIsCurrentlyViewed(xml));
    }

    // ============================================================================
    // ParseSelfHierarchyXml
    // ============================================================================

    [Fact]
    public void ParseSelfHierarchyXml_FullHierarchy_BuildsCompleteRecord()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-16"?>
            <one:Notebook xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote"
                          ID="NB42" name="My Notebook">
              <one:Section ID="SEC42" name="My Section">
                <one:Page ID="PG42-1234-5678-90AB-CDEF99999999"
                          name="My Page"
                          lastModifiedTime="2026-07-05T12:00:00Z">
                  <one:Outline><one:OE><one:T>content</one:T></one:OE></one:Outline>
                </one:Page>
              </one:Section>
            </one:Notebook>
            """;

        var info = OneNoteComInterop.ParseSelfHierarchyXml(xml, "fallback");

        Assert.NotNull(info);
        Assert.Equal("PG42-1234-5678-90AB-CDEF99999999", info!.PageId);
        Assert.Equal("My Page",     info.PageTitle);
        Assert.Equal("SEC42",       info.SectionId);
        Assert.Equal("My Section",  info.SectionTitle);
        Assert.Equal("NB42",        info.NotebookId);
        Assert.Equal("My Notebook", info.NotebookTitle);
        Assert.Equal(new DateTime(2026, 7, 5, 12, 0, 0, DateTimeKind.Utc), info.LastModified.ToUniversalTime());
    }

    [Fact]
    public void ParseSelfHierarchyXml_NoPage_UsesFallbackPageId()
    {
        // Edge case: Root ist Page-less (kann bei UWP oder einem Fehler passieren)
        const string xml = """
            <?xml version="1.0"?>
            <one:Notebook xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote"
                          ID="NB1" name="Empty">
            </one:Notebook>
            """;

        var info = OneNoteComInterop.ParseSelfHierarchyXml(xml, "FALLBACK-GUID");

        Assert.NotNull(info);
        Assert.Equal("FALLBACK-GUID", info!.PageId);
        Assert.Equal(string.Empty, info.PageTitle);
        Assert.Equal(string.Empty, info.SectionId);
    }

    [Fact]
    public void ParseSelfHierarchyXml_InvalidXml_ReturnsMinimalRecordWithFallback()
    {
        var info = OneNoteComInterop.ParseSelfHierarchyXml("not valid xml", "FALLBACK-PG");

        Assert.NotNull(info);
        Assert.Equal("FALLBACK-PG", info!.PageId);
        Assert.Equal(string.Empty, info.PageTitle);
    }

    [Fact]
    public void ParseSelfHierarchyXml_NoNamespaces_StillParsesButReturnsMinimal()
    {
        // Wenn das XML OneNote-Namespace fehlt, schlaegt der XPath fehl
        // und wir fallen auf MinimalRecord zurueck.
        const string xml = """
            <root><Page ID="P1" name="X"/></root>
            """;

        var info = OneNoteComInterop.ParseSelfHierarchyXml(xml, "FB-PID");

        Assert.NotNull(info);
        // ohne Namespace erkennt //one:Page nichts, daher Fallback
        Assert.Equal("FB-PID", info!.PageId);
    }
}
