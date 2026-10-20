namespace MediaDashboard.Core;

/// <summary>What kind of device an Audio Output is, as reported by Windows; drives the icon.</summary>
public enum AudioOutputKind { Speakers, Headphones, Headset, DigitalOutput, DisplayAudio, Other }

/// <summary>An available Audio Output.</summary>
/// <param name="Id">Windows endpoint id; stable for the same device on the same port.</param>
/// <param name="Name">Windows friendly name; the user may override it with an Audio Output name in settings.</param>
public sealed record AudioOutput(string Id, string Name, AudioOutputKind Kind);
