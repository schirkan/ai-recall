using AiRecall.AppReader.Documents;

namespace AiRecall.Core.Tests.AppReaders;

/// <summary>
/// Tests fuer den COM-Filename-Match-Helper in <see cref="OfficeComInterop"/>.
/// Verhindert, dass eine andere Office-Instanz (z. B. zweites Word-Fenster mit
/// einem anderen Dokument) einen falschen Pfad liefert (Martin 2026-07-04).
/// </summary>
public class OfficeComInteropFilenameMatchTests
{
    [Fact]
    public void NullExpected_AlwaysTrue()
    {
        Assert.True(OfficeComInterop.MatchesExpectedFilename(@"C:\any\path\file.docx", null));
    }

    [Fact]
    public void EmptyExpected_AlwaysTrue()
    {
        Assert.True(OfficeComInterop.MatchesExpectedFilename(@"C:\any\path\file.docx", string.Empty));
    }

    [Fact]
    public void MatchingFilename_True()
    {
        Assert.True(OfficeComInterop.MatchesExpectedFilename(@"C:\Users\Martin\Doc.docx", "Doc.docx"));
    }

    [Fact]
    public void MatchingFilename_CaseInsensitive_True()
    {
        Assert.True(OfficeComInterop.MatchesExpectedFilename(@"C:\Users\Martin\DOC.DOCX", "doc.docx"));
    }

    [Fact]
    public void DifferentFilename_False()
    {
        // Andere Office-Instanz aktiv (anderes Dokument) → Mismatch → null
        Assert.False(OfficeComInterop.MatchesExpectedFilename(@"C:\Users\Martin\OtherDoc.docx", "Doc.docx"));
    }

    [Fact]
    public void EmptyFullPath_False()
    {
        Assert.False(OfficeComInterop.MatchesExpectedFilename(string.Empty, "Doc.docx"));
    }

    [Fact]
    public void NullFullPath_False()
    {
        Assert.False(OfficeComInterop.MatchesExpectedFilename(null, "Doc.docx"));
    }

    [Fact]
    public void UnsavedDoc_FilenameIsDocument1_False()
    {
        // Unsaved Word-Doc: COM liefert "Document1" als Filename,
        // aber unser ParseTitle hat daraus "(untitled)" gemacht → null-expected
        // → kein Match erzwungen. Hier: wenn doch "Document1" als expected
        // uebergeben wird, muss COM ebenfalls "Document1" liefern.
        Assert.True(OfficeComInterop.MatchesExpectedFilename(@"C:\Users\X\Document1.docx", "Document1.docx"));
    }
}