namespace AiRecall.Core.Audio;

/// <summary>
/// Provider fuer verfuegbare Audio-Devices (Input + Loopback).
/// Wrapper um NAudio <c>MMDeviceEnumerator</c> fuer Testbarkeit.
/// </summary>
public interface IAudioDeviceProvider
{
    /// <summary>Liefert alle Input-Devices (Mikrofone).</summary>
    IReadOnlyList<AudioDeviceInfo> EnumerateInputDevices();

    /// <summary>Liefert alle Loopback-faehigen Output-Devices (Speaker).</summary>
    IReadOnlyList<AudioDeviceInfo> EnumerateLoopbackDevices();

    /// <summary>Liefert das Default-Input-Device (oder null).</summary>
    AudioDeviceInfo? GetDefaultInputDevice();

    /// <summary>Liefert das Default-Loopback-Device (oder null).</summary>
    AudioDeviceInfo? GetDefaultLoopbackDevice();
}