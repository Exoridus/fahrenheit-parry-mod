namespace Fahrenheit.Mods.Parry;

/// <summary>
/// Review queue for externally mined offsets before they are promoted into <see cref="ExternalMemoryOffsetMap"/>.
/// Keep this list small and focused. Promote validated entries and remove them from here.
/// </summary>
internal enum OffsetValidationState
{
    Unverified,
    Verified,
    Rejected,
}

internal sealed record ValidatedOffsetCandidate(
    string Key,
    int Offset,
    OffsetValidationState State,
    string Source,
    string Note);

internal static class MemoryCandidates
{
    // Example candidates sourced from local research/mining outputs.
    // Set State=Verified only after runtime checks pass in your battle/save/load scenarios.
    internal static readonly ValidatedOffsetCandidate[] Entries = [
        new(
            Key: "FinsTransitionBase",
            Offset: 0x00F25B60,
            State: OffsetValidationState.Unverified,
            Source: "FFXSpeedrunMod/Resources/MemoryLocations.cs",
            Note: "Transition memory base used heavily by speedrun tooling."),
        new(
            Key: "EventFileStart",
            Offset: 0x00F270B8,
            State: OffsetValidationState.Unverified,
            Source: "FFX_TAS_Python/memory/main.py",
            Note: "Candidate pointer base for event-related file state."),
        new(
            Key: "UnknownFunction_007B10D0",
            Offset: 0x003B10D0,
            State: OffsetValidationState.Unverified,
            Source: "Discord modding-technical",
            Note: "Function mention from reversing discussion; signature and semantics not yet confirmed.")
    ];
}
