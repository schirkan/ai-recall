using AiRecall.Core.Audio;

namespace AiRecall.Core.Tests.Audio;

/// <summary>
/// Integration-Tests fuer AudioDeviceProvider. Diese Tests benoetigen ein
/// funktionierendes Audio-System (Windows) und werden mit dem Trait
/// <c>Integration</c> markiert, damit sie in Sandboxes ohne Audio-Hardware
/// uebersprungen werden koennen.
/// </summary>
public class AudioDeviceProviderTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void EnumerateInputDevices_DoesNotThrow()
    {
        var provider = new AudioDeviceProvider();
        var devices = provider.EnumerateInputDevices();
        Assert.NotNull(devices);
        // Es kann 0 sein in einer Sandbox ohne Mic, aber die Methode darf nicht werfen
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void EnumerateLoopbackDevices_DoesNotThrow()
    {
        var provider = new AudioDeviceProvider();
        var devices = provider.EnumerateLoopbackDevices();
        Assert.NotNull(devices);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void GetDefaultInputDevice_DoesNotThrow()
    {
        var provider = new AudioDeviceProvider();
        var device = provider.GetDefaultInputDevice();
        // device kann null sein — kein Assert.NotNull
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void GetDefaultLoopbackDevice_DoesNotThrow()
    {
        var provider = new AudioDeviceProvider();
        var device = provider.GetDefaultLoopbackDevice();
    }
}

public class FakeAudioDeviceProviderTests
{
    /// <summary>
    /// Test mit einem Fake-Provider, der immer 2 Input- und 1 Loopback-Device liefert.
    /// Verifiziert das Public-API-Verhalten ohne Hardware-Zugriff.
    /// </summary>
    [Fact]
    public void FakeProvider_ReturnsExpectedDevices()
    {
        IAudioDeviceProvider fake = new FakeDeviceProvider(
            inputDevices: new[]
            {
                new AudioDeviceInfo("mic-1", "Default Mic", "USB"),
                new AudioDeviceInfo("mic-2", "Headset Mic", "Bluetooth")
            },
            loopbackDevices: new[]
            {
                new AudioDeviceInfo("loop-1", "Default Speakers", "USB")
            },
            defaultInputId: "mic-1",
            defaultLoopbackId: "loop-1");

        Assert.Equal(2, fake.EnumerateInputDevices().Count);
        Assert.Single(fake.EnumerateLoopbackDevices());
        Assert.Equal("mic-1", fake.GetDefaultInputDevice()?.DeviceId);
        Assert.Equal("loop-1", fake.GetDefaultLoopbackDevice()?.DeviceId);
    }

    [Fact]
    public void FakeProvider_WithEmptyLists_ReturnsEmpty()
    {
        IAudioDeviceProvider fake = new FakeDeviceProvider();
        Assert.Empty(fake.EnumerateInputDevices());
        Assert.Empty(fake.EnumerateLoopbackDevices());
        Assert.Null(fake.GetDefaultInputDevice());
        Assert.Null(fake.GetDefaultLoopbackDevice());
    }
}

/// <summary>Test-Double fuer <see cref="IAudioDeviceProvider"/>.</summary>
internal sealed class FakeDeviceProvider : IAudioDeviceProvider
{
    private readonly IReadOnlyList<AudioDeviceInfo> _inputs;
    private readonly IReadOnlyList<AudioDeviceInfo> _loops;
    private readonly string? _defaultInputId;
    private readonly string? _defaultLoopId;

    public FakeDeviceProvider(
        IReadOnlyList<AudioDeviceInfo>? inputDevices = null,
        IReadOnlyList<AudioDeviceInfo>? loopbackDevices = null,
        string? defaultInputId = null,
        string? defaultLoopbackId = null)
    {
        _inputs = inputDevices ?? Array.Empty<AudioDeviceInfo>();
        _loops = loopbackDevices ?? Array.Empty<AudioDeviceInfo>();
        _defaultInputId = defaultInputId;
        _defaultLoopId = defaultLoopbackId;
    }

    public IReadOnlyList<AudioDeviceInfo> EnumerateInputDevices() => _inputs;
    public IReadOnlyList<AudioDeviceInfo> EnumerateLoopbackDevices() => _loops;
    public AudioDeviceInfo? GetDefaultInputDevice() =>
        _inputs.FirstOrDefault(d => d.DeviceId == _defaultInputId);
    public AudioDeviceInfo? GetDefaultLoopbackDevice() =>
        _loops.FirstOrDefault(d => d.DeviceId == _defaultLoopId);
}