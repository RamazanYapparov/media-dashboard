using System.Collections.Immutable;
using System.Runtime.InteropServices;
using MediaDashboard.Core;
using MediaDashboard.Platform.Interop;
using static MediaDashboard.Platform.Interop.CoreAudioNative;

namespace MediaDashboard.Platform;

/// <summary>Lists Audio Outputs and switches the Windows default output through Core Audio.</summary>
public sealed class AudioService : IDisposable
{
    private static readonly ERole[] AllRoles = [ERole.eConsole, ERole.eMultimedia, ERole.eCommunications];

    private readonly IMMDeviceEnumerator _enumerator;
    private readonly NotificationClient _notifications;
    private bool _disposed;

    public AudioService()
    {
        _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorClass();
        _notifications = new NotificationClient(this);
        _enumerator.RegisterEndpointNotificationCallback(_notifications);
    }

    /// <summary>
    /// A render device was added, removed or changed state, or the default render device changed.
    /// Raised on a Core Audio worker thread; may fire several times for one user action (once per role).
    /// </summary>
    public event Action? Changed;

    /// <summary>Active render endpoints, in Windows enumeration order.</summary>
    public ImmutableArray<AudioOutput> GetOutputs()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var collection = _enumerator.EnumAudioEndpoints(EDataFlow.eRender, DEVICE_STATE_ACTIVE);
        try
        {
            uint count = collection.GetCount();
            var outputs = ImmutableArray.CreateBuilder<AudioOutput>((int)count);
            for (uint i = 0; i < count; i++)
            {
                var device = collection.Item(i);
                try
                {
                    outputs.Add(Describe(device));
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
            return outputs.MoveToImmutable();
        }
        finally
        {
            Marshal.ReleaseComObject(collection);
        }
    }

    /// <summary>Id of the default render endpoint for the console role; null when there is no output.</summary>
    public string? GetActiveOutputId()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int hr = _enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out var device);
        if (hr == E_NOTFOUND || device is null) return null;
        try
        {
            Marshal.ThrowExceptionForHR(hr);
            return device.GetId();
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    /// <summary>Makes <paramref name="outputId"/> the default output for every role (console, multimedia, communications).</summary>
    public void SetActive(string outputId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(outputId);
        var policy = (IPolicyConfig)new PolicyConfigClientClass();
        try
        {
            foreach (var role in AllRoles)
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(outputId, role));
        }
        finally
        {
            Marshal.ReleaseComObject(policy);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _enumerator.UnregisterEndpointNotificationCallback(_notifications);
        Marshal.ReleaseComObject(_enumerator);
    }

    private static AudioOutput Describe(IMMDevice device)
    {
        string id = device.GetId();
        var store = device.OpenPropertyStore(STGM_READ);
        try
        {
            string name = ReadString(store, PKEY_Device_FriendlyName) ?? id;
            var kind = ReadUInt32(store, PKEY_AudioEndpoint_FormFactor) switch
            {
                1 => AudioOutputKind.Speakers,
                3 => AudioOutputKind.Headphones,
                5 => AudioOutputKind.Headset,
                8 => AudioOutputKind.DigitalOutput,
                9 => AudioOutputKind.DisplayAudio,
                _ => AudioOutputKind.Other,
            };
            return new AudioOutput(id, name, kind);
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    private static string? ReadString(IPropertyStore store, PROPERTYKEY key)
    {
        if (store.GetValue(ref key, out var value) < 0) return null;
        try
        {
            return value.vt == VT_LPWSTR ? Marshal.PtrToStringUni(value.data1) : null;
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    private static uint? ReadUInt32(IPropertyStore store, PROPERTYKEY key)
    {
        if (store.GetValue(ref key, out var value) < 0) return null;
        try
        {
            // The ulVal union member sits at the start of the 8-byte-aligned data area (little-endian).
            return value.vt == VT_UI4 ? unchecked((uint)(long)value.data1) : null;
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    /// <summary>Called by Core Audio on its own threads; must not block.</summary>
    private sealed class NotificationClient(AudioService owner) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string deviceId, uint newState) => Raise();

        public int OnDeviceAdded(string deviceId) => Raise();

        public int OnDeviceRemoved(string deviceId) => Raise();

        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId) =>
            flow == EDataFlow.eRender ? Raise() : 0;

        public int OnPropertyValueChanged(string deviceId, PROPERTYKEY key) => 0;

        private int Raise()
        {
            // A subscriber exception is turned into a failure HRESULT by the COM callable wrapper.
            owner.Changed?.Invoke();
            return 0;
        }
    }
}
