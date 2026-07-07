using System.Net;

namespace AiRecall.Core.Tessdata;

/// <summary>
/// Wird ausgelöst, wenn ein tessdata-Download nach allen Retries fehlschlägt
/// (Spec 0012). Enthält den HTTP-Statuscode für Diagnose.
/// </summary>
public sealed class TessdataDownloadException : Exception
{
    public TessdataDownloadException(string message, HttpStatusCode statusCode)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public TessdataDownloadException(string message, HttpStatusCode statusCode, Exception inner)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }

    /// <summary>HTTP-Statuscode des letzten fehlgeschlagenen Requests.</summary>
    public HttpStatusCode StatusCode { get; }
}