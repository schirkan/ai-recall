using NAudio.CoreAudioApi;

namespace AiRecall.Core.Audio;

/// <summary>
/// NAudio-basierte Implementierung von <see cref="IAudioDeviceProvider"/>.
/// Verwendet <see cref="MMDeviceEnumerator"/> fuer Input- und Loopback-Devices.
/// </summary>
public sealed class AudioDeviceProvider : IAudioDeviceProvider
{
    /// <summary>
    /// Erstellt einen neuen Provider. Die zugrundeliegende <see cref="MMDeviceEnumerator"/>
    /// wird bei jedem Aufruf neu erstellt (kein internes Caching), damit die Liste
    /// aktuell ist. Falls Aufrufe teuer werden, kann ein Cache eingefuegt werden.
    /// </summary>
    public AudioDeviceProvider() { }

    public IReadOnlyList<AudioDeviceInfo> EnumerateInputDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        try
        {
            return devices.Select(MapDevice).ToArray();
        }
        finally
        {
            foreach (var d in devices) d.Dispose();
        }
    }

    public IReadOnlyList<AudioDeviceInfo> EnumerateLoopbackDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        try
        {
            return devices.Select(MapDevice).ToArray();
        }
        finally
        {
            foreach (var d in devices) d.Dispose();
        }
    }

    public AudioDeviceInfo? GetDefaultInputDevice()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var dev = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            return dev == null ? null : MapDevice(dev);
        }
        catch
        {
            return null;
        }
    }

    public AudioDeviceInfo? GetDefaultLoopbackDevice()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var dev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return dev == null ? null : MapDevice(dev);
        }
        catch
        {
            return null;
        }
    }

    private static AudioDeviceInfo MapDevice(MMDevice d)
    {
        // NAudio 2.2.1 bietet DeviceFriendlyName als direkte Property auf MMDevice
        // (= DEVPKEY_Device_FriendlyName). Fallback auf FriendlyName (CoreAudio-Name).
        var friendlyName = !string.IsNullOrEmpty(d.DeviceFriendlyName)
            ? d.DeviceFriendlyName
            : d.FriendlyName;

        // DEVPKEY_Device_Interface ist in NAudio 2.2.1 nicht als eigene Property
        // verfuegbar; ueber PropertyStore mit PKEY_Device_InterfaceKey versuchen,
        // sonst leerer String.
        var interfaceName = TryReadStringProperty(d, PropertyKeys.PKEY_Device_InterfaceKey) ?? "";

        return new AudioDeviceInfo(
            DeviceId: d.ID,
            FriendlyName: friendlyName,
            InterfaceName: interfaceName);
    }

    /// <summary>
    /// Liest einen String-Wert aus dem Property-Store des Devices.
    /// Gibt <c>null</c> zurueck, wenn der Key nicht existiert oder der Wert kein String ist.
    /// </summary>
    private static string? TryReadStringProperty(MMDevice d, PropertyKey key)
    {
        try
        {
            var store = d.Properties;
            if (!store.Contains(key)) return null;
            return store[key].Value as string;
        }
        catch
        {
            return null;
        }
    }
}