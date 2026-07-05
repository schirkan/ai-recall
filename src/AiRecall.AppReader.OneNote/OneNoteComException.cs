using System.Runtime.InteropServices;

namespace AiRecall.AppReader.OneNote;

/// <summary>
/// Wrapper fuer COM-Fehler bei OneNote-Late-Binding-Aufrufen (Spec 0010).
/// Klassifiziert COM-HRESULTs in retry-faehig vs. fatal — siehe
/// <see cref="ClassifyHResult"/> fuer die Tabellen.
///
/// Quelle der HRESULT-Klassifikation: OneMore-AddIn-Produktionscode
/// (<see href="https://github.com/stevencohn/OneMore"/>) kombiniert mit
/// generischen COM-RPC-Fehlern aus MSDN.
/// </summary>
internal sealed class OneNoteComException : Exception
{
    /// <summary>
    /// COM-HRESULT (z. B. <c>0x80042001</c>). Verdeckt bewusst die
    /// <see cref="Exception.HResult"/>-Property mit dem Original-Wert
    /// aus der inner-Exception (nicht dem CLR-transformierten Wert).
    /// </summary>
    public new int HResult { get; }

    /// <summary>
    /// <c>true</c>, wenn ein Retry mit frischem COM-Objekt sinnvoll ist;
    /// <c>false</c> bei fatalen Schema-/Daten-Fehlern.
    /// </summary>
    public bool IsRetryable { get; }

    public OneNoteComException(string message, int hresult, Exception? inner = null)
        : base(message, inner)
    {
        HResult = hresult;
        IsRetryable = ClassifyHResult(hresult);
    }

    public OneNoteComException(Exception inner)
        : this(inner.Message, inner.HResult, inner)
    {
    }

    /// <summary>
    /// Klassifiziert COM-HRESULTs. Unwrap nach uint fuer konsistenten Vergleich.
    ///
    /// <para><b>Fatal (kein Retry):</b></para>
    /// <list type="bullet">
    /// <item><c>0x80042001</c> — <c>hrXmlIsInvalid</c>, fehlerhaftes OneNote-XML-Schema</item>
    /// <item><c>0x800706BA</c> — <c>hrRpcFailed2</c>, RPC-Server abgestuerzt</item>
    /// </list>
    ///
    /// <para><b>Transient (Retry):</b></para>
    /// <list type="bullet">
    /// <item><c>0x80010108</c> — <c>hrRpcUnavailable</c>, RPC-Channel kurz nicht verfuegbar</item>
    /// <item><c>0x80004021</c> — <c>hrCOMBusy</c>, COM-Server ausgelastet</item>
    /// <item><c>0x80010109</c> — <c>hrServerCallRetried</c>, Server hat selbst retry'd</item>
    /// <item><c>0x8001010E</c> — <c>hrObjectMissing</c>, RCW wurde orphaned</item>
    /// </list>
    /// </summary>
    private static bool ClassifyHResult(int hr)
    {
        var u = unchecked((uint)hr);
        return u switch
        {
            0x80042001u => false,   // hrXmlIsInvalid
            0x800706BAu => false,   // hrRpcFailed2

            0x80010108u => true,    // hrRpcUnavailable
            0x80004021u => true,    // hrCOMBusy
            0x80010109u => true,    // hrServerCallRetried
            0x8001010Eu => true,    // hrObjectMissing
            _ => false,
        };
    }
}
