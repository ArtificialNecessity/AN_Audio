namespace AN.Audio.Midi.Platforms.MacOS;

#pragma warning disable AN0100

/// <summary>One CoreMIDI source as enumerated right now. The EndpointRef is a transient handle; the UniqueId (and hence Key) is stable (D30).</summary>
internal sealed record CoreMidi_MidiInDeviceEntry(
    CoreMidi_EndpointRef Source,
    CoreMidi_UniqueId UniqueId,
    string Name,                             // kMIDIPropertyDisplayName (device + endpoint when the endpoint name is not unique)
    string? Manufacturer,
    string? Model,
    MidiInput_DeviceKey Key,
    MidiInput_DeviceTypeId FallbackTypeId,
    CoreMidi_EndpointRef PairedDestination)  // D32: first destination of the same entity; Null for virtual endpoints
{
    public bool HasPairedOutput => !PairedDestination.IsNull;

    public MidiInput_DeviceInfo ToDeviceInfo(MidiInput_DeviceIdentity? identity = null) =>
        new(Key, identity is null ? FallbackTypeId : MidiInput_DeviceTypeId.FromIdentity(identity), Name, identity, HasPairedOutput);
}

/// <summary>Enumerates CoreMIDI sources and builds stable keys (SPEC-30 D30–D32). Excludes Offline endpoints (D34).</summary>
internal static unsafe class CoreMidi_MidiInEnumerator
{
    private const string KeyPrefix = "coremidi-uid:";

    public static List<CoreMidi_MidiInDeviceEntry> EnumerateInputs()
    {
        nuint count = CoreMidi_Interop.MIDIGetNumberOfSources();
        var result = new List<CoreMidi_MidiInDeviceEntry>((int)count);

        for (nuint i = 0; i < count; i++)
        {
            var source = new CoreMidi_EndpointRef(CoreMidi_Interop.MIDIGetSource(i));
            if (source.IsNull) continue;   // vanished between count and here

            // CoreMIDI remembers unplugged devices in the setup and marks them Offline; they are not usable.
            if (CoreMidi_Interop.GetIntegerProperty(source.AsObject, CoreMidi_PropertyKeys.Offline) is > 0) continue;

            int? uid = CoreMidi_Interop.GetIntegerProperty(source.AsObject, CoreMidi_PropertyKeys.UniqueId);
            if (uid is null or 0) continue;   // every real endpoint has one; without it we have no stable identity

            string name = CoreMidi_Interop.GetStringProperty(source.AsObject, CoreMidi_PropertyKeys.DisplayName)
                       ?? CoreMidi_Interop.GetStringProperty(source.AsObject, CoreMidi_PropertyKeys.Name)
                       ?? $"MIDI source {uid.Value}";
            // Manufacturer/Model live on the device and are inherited by the endpoint; absent for many virtual endpoints.
            string? manufacturer = CoreMidi_Interop.GetStringProperty(source.AsObject, CoreMidi_PropertyKeys.Manufacturer);
            string? model = CoreMidi_Interop.GetStringProperty(source.AsObject, CoreMidi_PropertyKeys.Model);

            result.Add(new CoreMidi_MidiInDeviceEntry(
                Source: source,
                UniqueId: new CoreMidi_UniqueId(uid.Value),
                Name: name,
                Manufacturer: manufacturer,
                Model: model,
                Key: new MidiInput_DeviceKey(KeyPrefix + uid.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                FallbackTypeId: MidiInput_DeviceTypeId.UnknownFromDriverStrings(name, manufacturer, model),
                PairedDestination: FindPairedDestination(source)));
        }
        return result;
    }

    /// <summary>D32: the first destination owned by the same entity as <paramref name="source"/>; Null when the source has no entity (virtual).</summary>
    public static CoreMidi_EndpointRef FindPairedDestination(CoreMidi_EndpointRef source)
    {
        uint entity = 0;
        if (CoreMidi_Interop.MIDIEndpointGetEntity(source.Value, &entity) != CoreMidi_Status.NoError || entity == 0) return new CoreMidi_EndpointRef(0);
        if (CoreMidi_Interop.MIDIEntityGetNumberOfDestinations(entity) == 0) return new CoreMidi_EndpointRef(0);
        return new CoreMidi_EndpointRef(CoreMidi_Interop.MIDIEntityGetDestination(entity, 0));
    }

    /// <summary>Re-resolve a destination by the SOURCE's current entity at send time (refs can go stale across unplug/replug).</summary>
    public static CoreMidi_EndpointRef ResolveDestinationForSend(CoreMidi_MidiInDeviceEntry entry)
    {
        uint obj = 0; CoreMidi_ObjectType type;
        if (CoreMidi_Interop.MIDIObjectFindByUniqueID(entry.UniqueId.Value, &obj, &type) != CoreMidi_Status.NoError || obj == 0) return new CoreMidi_EndpointRef(0);
        return FindPairedDestination(new CoreMidi_EndpointRef(obj));
    }
}

#pragma warning restore AN0100