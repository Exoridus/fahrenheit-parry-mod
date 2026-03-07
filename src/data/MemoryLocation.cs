namespace Fahrenheit.Mods.Parry;

/// <summary>
/// Immutable descriptor for a memory location relative to the game module base.
/// Optional pointer offsets represent a pointer chain dereference path.
/// </summary>
internal readonly struct MemoryLocation
{
    public string Name { get; }
    public int BaseOffset { get; }
    public IReadOnlyList<int> PointerOffsets { get; }

    public MemoryLocation(string name, int baseOffset, params int[] pointerOffsets)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "Unnamed" : name.Trim();
        BaseOffset = baseOffset;
        PointerOffsets = pointerOffsets is { Length: > 0 } ? [.. pointerOffsets] : Array.Empty<int>();
    }

    public bool HasPointerChain => PointerOffsets.Count > 0;

    public override string ToString() =>
        HasPointerChain
            ? $"{Name}: 0x{BaseOffset:X8} -> [{string.Join(", ", PointerOffsets.Select(x => $"0x{x:X}"))}]"
            : $"{Name}: 0x{BaseOffset:X8}";
}
