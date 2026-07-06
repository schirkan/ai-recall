using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AiRecall.Core.Configuration;
using AiRecall.Core.Tessdata;
using Xunit;

namespace AiRecall.Core.Tests;

/// <summary>
/// Tests für <see cref="TessdataManager"/> (Spec 0012). HttpClient wird über
/// ein <see cref="StubHttpMessageHandler"/> gemockt.
/// </summary>
public class TessdataManagerTests : IDisposable
{
  private readonly string _tempDir;

  public TessdataManagerTests()
  {
    _tempDir = Path.Combine(Path.GetTempPath(), "AiRecall-TessdataTests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(_tempDir);
  }

  public void Dispose()
  {
    try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
  }

  private static AppConfig MakeConfig(IEnumerable<string> languages, string tessDataPath, bool auto = true, string engine = "tesseract")
  {
    return new AppConfig
    {
      Ocr = new OcrConfig
      {
        Engine = engine,
        Languages = languages.ToList(),
        TessDataPath = tessDataPath,
        AutoDownloadTessdata = auto
      }
    };
  }

  [Fact]
  public void FindMissingLanguages_AllPresent_ReturnsEmpty()
  {
    File.WriteAllBytes(Path.Combine(_tempDir, "eng.traineddata"), new byte[] { 0x01 });
    File.WriteAllBytes(Path.Combine(_tempDir, "deu.traineddata"), new byte[] { 0x01 });
    var mgr = new TessdataManager();
    var cfg = MakeConfig(new[] { "eng", "deu" }, _tempDir);

    var missing = mgr.FindMissingLanguages(cfg);

    Assert.Empty(missing);
  }

  [Fact]
  public void FindMissingLanguages_OneMissing_ReturnsOnlyThatOne()
  {
    File.WriteAllBytes(Path.Combine(_tempDir, "eng.traineddata"), new byte[] { 0x01 });
    // deu.traineddata fehlt absichtlich
    var mgr = new TessdataManager();
    var cfg = MakeConfig(new[] { "eng", "deu" }, _tempDir);

    var missing = mgr.FindMissingLanguages(cfg);

    Assert.Single(missing);
    Assert.Equal("deu", missing[0].Code);
    Assert.Equal("deu.traineddata", missing[0].FileName);
  }

  [Fact]
  public void FindMissingLanguages_EngineIsNotTesseract_ReturnsEmpty()
  {
    var mgr = new TessdataManager();
    var cfg = MakeConfig(new[] { "eng" }, _tempDir, engine: "noop");

    Assert.Empty(mgr.FindMissingLanguages(cfg));
  }

  [Fact]
  public void FindMissingLanguages_AutoDownloadDisabled_ReturnsEmpty()
  {
    var mgr = new TessdataManager();
    var cfg = MakeConfig(new[] { "eng" }, _tempDir, auto: false);

    Assert.Empty(mgr.FindMissingLanguages(cfg));
  }

  [Fact]
  public void FindMissingLanguages_OsdCodeIsIgnored()
  {
    // osd ist ein Script-Code, soll nie per First-Run heruntergeladen werden.
    var mgr = new TessdataManager();
    var cfg = MakeConfig(new[] { "eng", "osd" }, _tempDir);

    var missing = mgr.FindMissingLanguages(cfg);

    Assert.Single(missing);
    Assert.Equal("eng", missing[0].Code);
  }

  [Fact]
  public async Task DownloadAsync_WritesFileAndReportsProgress()
  {
    var payload = new byte[] { 0x10, 0x20, 0x30, 0x40, 0x50 };
    var handler = new StubHttpMessageHandler(req =>
    {
      Assert.EndsWith("/eng.traineddata", req.RequestUri!.ToString());
      return new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new ByteArrayContent(payload)
      };
    });
    var progressReports = new List<TessdataDownloadProgress>();
    var progress = new Progress<TessdataDownloadProgress>(p => progressReports.Add(p));

    var mgr = new TessdataManager(handler, TessdataManager.DefaultBaseUrl);
    await mgr.DownloadAsync(new[] { "eng" }, _tempDir, progress, CancellationToken.None);

    Assert.True(File.Exists(Path.Combine(_tempDir, "eng.traineddata")));
    Assert.Equal(payload, File.ReadAllBytes(Path.Combine(_tempDir, "eng.traineddata")));
    Assert.NotEmpty(progressReports);
    Assert.Equal(payload.LongLength, progressReports[^1].TotalBytesReceived);
    Assert.Equal(1, progressReports[^1].CompletedCount);
    Assert.Equal(1, progressReports[^1].TotalCount);
  }

  [Fact]
  public async Task DownloadAsync_404AfterRetries_Throws()
  {
    var handler = new StubHttpMessageHandler(_ =>
        new HttpResponseMessage(HttpStatusCode.NotFound));
    var mgr = new TessdataManager(handler, TessdataManager.DefaultBaseUrl);

    var ex = await Assert.ThrowsAsync<TessdataDownloadException>(() =>
        mgr.DownloadAsync(new[] { "eng" }, _tempDir, progress: null, CancellationToken.None));
    Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
  }

  [Fact]
  public async Task DownloadAsync_5xxThenSucceeds_RetriesAndWrites()
  {
    var calls = 0;
    var handler = new StubHttpMessageHandler(_ =>
    {
      calls++;
      if (calls < 3) return new HttpResponseMessage(HttpStatusCode.BadGateway);
      return new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new ByteArrayContent(new byte[] { 0xAB })
      };
    });
    var mgr = new TessdataManager(handler, TessdataManager.DefaultBaseUrl);

    await mgr.DownloadAsync(new[] { "deu" }, _tempDir, progress: null, CancellationToken.None);

    Assert.Equal(3, calls);
    Assert.True(File.Exists(Path.Combine(_tempDir, "deu.traineddata")));
  }

  [Fact]
  public void ResolveTargetDirectory_FallsBackToLocalAppData_WhenNoConfiguredPathExists()
  {
    var mgr = new TessdataManager();
    var ocr = new OcrConfig { TessDataPath = "non-existent-" + Guid.NewGuid().ToString("N") };

    var target = mgr.ResolveTargetDirectory(ocr);

    var expected = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AiRecall", "tessdata");
    Assert.Equal(expected, target);
    Assert.True(Directory.Exists(target));
  }

  /// <summary>Einfaches HttpMessageHandler-Stub, das eine Factory pro Request auswertet.</summary>
  private sealed class StubHttpMessageHandler : HttpMessageHandler
  {
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => _responder = responder;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(_responder(request));
  }
}