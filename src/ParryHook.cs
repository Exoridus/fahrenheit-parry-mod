// SPDX-License-Identifier: MIT

namespace Fahrenheit.Mods.Parry;

/// <summary>
///     SPECULATIVE alpha11 adapter (no upstream reference implementation yet).
///     <para/>
///     Fahrenheit alpha11 (main @ d6a2fa3) redesigned <see cref="FhMethodHandle{T}"/> into a
///     stack-only <c>ref struct</c> that cannot be stored in a field. Installing a hook moved to
///     <c>new FhMethodHandle&lt;T&gt;(new FhMethodLocation(module, offset)).hook(owner, handler)</c>
///     and call-through to <c>handle.chain_from(handler).fnptr</c>.
///     <para/>
///     This wrapper restores a storable handle by keeping the <c>(module-offset, handler)</c> pair
///     and reconstructing the transient ref struct on demand. Keeping the handler delegate in a
///     field guarantees both reference identity for <see cref="FhMethodHandle{T}.chain_from"/> and
///     keep-alive of the installed detour. The framework's MethodTable additionally tracks the
///     installed hook.
///     <para/>
///     NOTE: At d6a2fa3 the new core API has no migrated reference mod (the in-tree mods still use
///     the removed alpha10 signatures). This adapter encodes the inferred intended usage. Revisit
///     once upstream ships a tagged alpha11 release plus a migrated example mod.
/// </summary>
internal sealed class ParryHook<T> where T : Delegate
{
    private const string ModuleName = "FFX.exe";

    private readonly FhModule _owner;
    private readonly nint     _offset;
    private readonly T        _handler;

    public ParryHook(FhModule owner, nint ffxOffset, T handler)
    {
        _owner   = owner;
        _offset  = ffxOffset;
        _handler = handler;
    }

    /// <summary>
    ///     Inserts this hook into the target function's chain. Mirrors the alpha10
    ///     <c>FhMethodHandle.hook()</c> call site.
    /// </summary>
    public bool install()
        => new FhMethodHandle<T>(new FhMethodLocation(ModuleName, _offset)).hook(_owner, _handler);

    /// <summary>
    ///     The continuation of the call chain immediately after this hook — i.e. the original game
    ///     function when this is the sole hook on the target. Mirrors the alpha10 <c>orig_fptr</c>.
    ///     Reconstructed per access because the underlying handle is a <c>ref struct</c>.
    /// </summary>
    public T orig
        => new FhMethodHandle<T>(new FhMethodLocation(ModuleName, _offset)).chain_from(_handler).fnptr
           ?? throw new InvalidOperationException(
               $"Parry: no chain continuation for {ModuleName}+0x{_offset:X} (hook not installed?).");
}
