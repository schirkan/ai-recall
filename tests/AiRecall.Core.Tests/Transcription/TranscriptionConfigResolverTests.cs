using AiRecall.Core.Configuration;
using AiRecall.Transcription;

namespace AiRecall.Core.Tests.Transcription;

/// <summary>
/// Tests fuer <see cref="TranscriptionConfigResolver"/> (Spec 0013 v0.3 §5.4,
/// Provider-Selection-Logik). Testet Config-Resolution OHNE echte Provider-
/// Instanzen (die kommen in 3c/3d).
/// </summary>
public class TranscriptionConfigResolverTests
{
    [Fact]
    public void ResolveProviderName_KnownAzure_ReturnsAzureSpeech()
    {
        var cfg = new TranscriptionConfig { Provider = "azure-speech" };
        Assert.Equal("azure-speech", TranscriptionConfigResolver.ResolveProviderName(cfg));
    }

    [Fact]
    public void ResolveProviderName_KnownDeepgram_ReturnsDeepgram()
    {
        var cfg = new TranscriptionConfig { Provider = "deepgram" };
        Assert.Equal("deepgram", TranscriptionConfigResolver.ResolveProviderName(cfg));
    }

    [Fact]
    public void ResolveProviderName_UnknownValue_FallsBackToDefault()
    {
        var cfg = new TranscriptionConfig { Provider = "whisper-local" };
        Assert.Equal(TranscriptionConfigResolver.DefaultProviderName,
            TranscriptionConfigResolver.ResolveProviderName(cfg));
    }

    [Fact]
    public void ResolveProviderName_CaseInsensitive()
    {
        var cfg = new TranscriptionConfig { Provider = "DEEPGRAM" };
        Assert.Equal("deepgram", TranscriptionConfigResolver.ResolveProviderName(cfg));
    }

    [Fact]
    public void ResolveProviderName_EmptyString_FallsBackToDefault()
    {
        var cfg = new TranscriptionConfig { Provider = "" };
        Assert.Equal(TranscriptionConfigResolver.DefaultProviderName,
            TranscriptionConfigResolver.ResolveProviderName(cfg));
    }

    [Fact]
    public void ResolveOptions_AzureSpeech_HasNoEndpointOverride()
    {
        var cfg = new TranscriptionConfig
        {
            Provider = "azure-speech",
            ApiKey = "k",
            DefaultLanguage = "deu",
            MaxSpeakers = 4,
        };
        var opts = TranscriptionConfigResolver.ResolveOptions(cfg);
        Assert.Equal("deu", opts.Language);
        Assert.True(opts.DiarizationRequired);
        Assert.Equal(4, opts.MaxSpeakers);
        Assert.Equal("k", opts.ApiKey);
        Assert.Null(opts.EndpointOverride); // Azure: Region-basiert
    }

    [Fact]
    public void ResolveOptions_Deepgram_UsesConfiguredEndpoint()
    {
        var cfg = new TranscriptionConfig
        {
            Provider = "deepgram",
            ApiKey = "k",
            DeepgramEndpoint = "https://eu.deepgram.example/v1",
        };
        var opts = TranscriptionConfigResolver.ResolveOptions(cfg);
        Assert.Equal("https://eu.deepgram.example/v1", opts.EndpointOverride);
    }

    [Fact]
    public void ResolveOptions_Deepgram_DefaultEndpoint_WhenEmpty()
    {
        var cfg = new TranscriptionConfig { Provider = "deepgram", DeepgramEndpoint = "" };
        var opts = TranscriptionConfigResolver.ResolveOptions(cfg);
        Assert.Equal("https://api.deepgram.com", opts.EndpointOverride);
    }

    [Fact]
    public void ResolveOptions_MaxSpeakers_FallbackTo8_WhenZeroOrNegative()
    {
        var cfg1 = new TranscriptionConfig { MaxSpeakers = 0 };
        var cfg2 = new TranscriptionConfig { MaxSpeakers = -3 };
        Assert.Equal(8, TranscriptionConfigResolver.ResolveOptions(cfg1).MaxSpeakers);
        Assert.Equal(8, TranscriptionConfigResolver.ResolveOptions(cfg2).MaxSpeakers);
    }

    [Fact]
    public void ResolveOptions_Language_FallbackToDeu_WhenEmpty()
    {
        var cfg = new TranscriptionConfig { DefaultLanguage = "" };
        Assert.Equal("deu", TranscriptionConfigResolver.ResolveOptions(cfg).Language);
    }

    [Fact]
    public void ResolveOptions_DiarizationRequired_AlwaysTrue()
    {
        // MVP 3: Diarization ist Pflicht (Spec §6)
        var cfg1 = new TranscriptionConfig { Provider = "azure-speech" };
        var cfg2 = new TranscriptionConfig { Provider = "deepgram" };
        Assert.True(TranscriptionConfigResolver.ResolveOptions(cfg1).DiarizationRequired);
        Assert.True(TranscriptionConfigResolver.ResolveOptions(cfg2).DiarizationRequired);
    }

    [Fact]
    public void ResolveProviderName_NullConfig_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => TranscriptionConfigResolver.ResolveProviderName(null!));
    }
}
