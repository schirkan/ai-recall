using AiRecall.Core.Audio;

namespace AiRecall.Core.Tests.Audio;

public class AudioConfigTests
{
    [Fact]
    public void Defaults_ArePrivacyFirst()
    {
        var cfg = new AudioConfig();
        Assert.False(cfg.Enabled);
        Assert.Equal(16000, cfg.SampleRate);
        Assert.Equal(16, cfg.BitsPerSample);
        Assert.Equal("audio", cfg.StorageRoot);
        Assert.Equal(30, cfg.MinMeetingDurationSeconds);
    }

    [Fact]
    public void DefaultMicDeviceId_IsEmpty()
    {
        var cfg = new AudioConfig();
        Assert.Equal("", cfg.MicDeviceId);
        Assert.Equal("", cfg.LoopbackDeviceId);
    }

    [Fact]
    public void AudioConfig_PropertiesRoundtrip()
    {
        var cfg = new AudioConfig
        {
            Enabled = true,
            MicDeviceId = "mic-123",
            LoopbackDeviceId = "loop-456",
            SampleRate = 48000,
            BitsPerSample = 24,
            StorageRoot = "C:/data/audio",
            MinMeetingDurationSeconds = 60
        };

        Assert.True(cfg.Enabled);
        Assert.Equal("mic-123", cfg.MicDeviceId);
        Assert.Equal("loop-456", cfg.LoopbackDeviceId);
        Assert.Equal(48000, cfg.SampleRate);
        Assert.Equal(24, cfg.BitsPerSample);
        Assert.Equal("C:/data/audio", cfg.StorageRoot);
        Assert.Equal(60, cfg.MinMeetingDurationSeconds);
    }
}

public class AudioDeviceInfoTests
{
    [Fact]
    public void Constructor_StoresAllProperties()
    {
        var dev = new AudioDeviceInfo("dev-1", "My Mic", "USB Audio");
        Assert.Equal("dev-1", dev.DeviceId);
        Assert.Equal("My Mic", dev.FriendlyName);
        Assert.Equal("USB Audio", dev.InterfaceName);
    }

    [Fact]
    public void Equality_IsValueBased()
    {
        var a = new AudioDeviceInfo("id-1", "Name", "Interface");
        var b = new AudioDeviceInfo("id-1", "Name", "Interface");
        Assert.Equal(a, b);
    }
}

public class AudioFormatTests
{
    [Fact]
    public void Default_HasWhisperCompatibleFormat()
    {
        var fmt = AudioFormat.Default;
        Assert.Equal(16000, fmt.SampleRate);
        Assert.Equal(16, fmt.BitsPerSample);
        Assert.Equal(1, fmt.Channels);
    }

    [Fact]
    public void Format_PropertiesRoundtrip()
    {
        var fmt = new AudioFormat(SampleRate: 48000, BitsPerSample: 24, Channels: 2);
        Assert.Equal(48000, fmt.SampleRate);
        Assert.Equal(24, fmt.BitsPerSample);
        Assert.Equal(2, fmt.Channels);
    }
}

public class RecordingStateTests
{
    [Fact]
    public void AllStates_AreDefined()
    {
        var values = Enum.GetValues<RecordingState>();
        Assert.Contains(RecordingState.Created, values);
        Assert.Contains(RecordingState.Recording, values);
        Assert.Contains(RecordingState.Recorded, values);
        Assert.Contains(RecordingState.Failed, values);
    }
}

public class MeetingRecordingPathsTests
{
    [Fact]
    public void AllExist_FalseWhenFolderMissing()
    {
        var paths = new MeetingRecordingPaths(
            Folder: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()),
            MicPath: "mic.wav",
            LoopbackPath: "loop.wav",
            MetadataPath: "meta.md");
        Assert.False(paths.AllExist);
    }

    [Fact]
    public void AllExist_TrueWhenAllFilesPresent()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "AiRecall-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var mic = Path.Combine(tempDir, "mic.wav");
            var loop = Path.Combine(tempDir, "loop.wav");
            var meta = Path.Combine(tempDir, "meta.md");
            File.WriteAllText(mic, "x");
            File.WriteAllText(loop, "x");
            File.WriteAllText(meta, "x");

            var paths = new MeetingRecordingPaths(tempDir, mic, loop, meta);
            Assert.True(paths.AllExist);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}