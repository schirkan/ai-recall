using System.Runtime.InteropServices;

namespace AiRecall.AppReader.OneNote;

/// <summary>
/// P/Invoke-Bindings fuer OneNote-COM-Init (Spec 0010).
///
/// <c>Marshal.GetActiveObject</c> ist im .NET 8 SDK 8.0.422 nicht direkt
/// verfuegbar — P/Invoke auf <c>oleaut32.dll</c> ist der robuste Weg
/// (gleicher Workaround wie in <c>AiRecall.AppReader.Outlook.OutlookComInterop</c>
/// und <c>AiRecall.AppReader.Documents.OfficeComInterop</c>).
/// </summary>
internal static class OneNoteComNative
{
    /// <summary>
    /// P/Invoke fuer <c>GetActiveObject</c> aus <c>oleaut32.dll</c>.
    /// Liefert einen RCW auf das aktive COM-Objekt mit der angegebenen CLSID.
    /// </summary>
    [DllImport("oleaut32.dll", PreserveSig = false)]
    public static extern void GetActiveObject(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rclsid,
        IntPtr pvReserved,
        [MarshalAs(UnmanagedType.Interface)] out object ppunk);
}
