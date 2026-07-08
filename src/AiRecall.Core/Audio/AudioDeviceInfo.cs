namespace AiRecall.Core.Audio;

/// <summary>
/// Beschreibung eines Audio-Devices (Input oder Loopback).
/// <see cref="DeviceId"/> ist der stabile Schluessel fuer Persistenz,
/// <see cref="FriendlyName"/> kann sich beim Replug aendern.
/// </summary>
public sealed record AudioDeviceInfo(
    string DeviceId,
    string FriendlyName,
    string InterfaceName);