// SPDX-License-Identifier: MIT

namespace Fahrenheit.Mods.Parry;

/// <summary>
///     SPECULATIVE alpha11 adapter (no upstream reference implementation yet).
///     <para/>
///     Fahrenheit alpha11 (main @ d6a2fa3) redesigned <see cref="FhMethodHandle{T}"/> into a
///     stack-only <c>ref struct</c> that cannot be stored in a field. Installing a hook moved to
///     <c>new FhMethodHandle&lt;T&gt;(new FhMethodLocation(...)).hook(owner, handler)</c> and
///     call-through to <c>handle.chain_from(handler).fnptr</c>.
///     <para/>
///     This wrapper restores a storable handle by keeping the location inputs + handler and
///     reconstructing the transient ref struct on demand. Keeping the handler delegate in a field
///     guarantees both reference identity for <see cref="FhMethodHandle{T}.chain_from"/> and
///     keep-alive of the installed detour. The framework's MethodTable additionally tracks the hook.
///     <para/>
///     Supports three location forms: FFX.exe + runtime offset (the common case), an arbitrary
///     module + runtime offset, and an arbitrary module + exported function name (e.g.
///     <c>shell32.dll!ShellExecuteW</c>).
///     <para/>
///     NOTE: At d6a2fa3 the new core API has no migrated reference mod (the in-tree mods still use
///     the removed alpha10 signatures). This adapter encodes the inferred intended usage. Revisit
///     once upstream ships a tagged alpha11 release plus a migrated example mod.
/// </summary>
internal sealed class ParryHook<T> where T : Delegate
{
    private readonly FhModule _owner;
    private readonly string   _module;
    private readonly nint     _offset;   // used when _fnName is null
    private readonly string?  _fnName;   // used when non-null: resolve by export name
    private readonly T        _handler;

    /// <summary>Hook an <c>FFX.exe</c> function by runtime offset (the common case).</summary>
    public ParryHook(FhModule owner, nint ffxOffset, T handler)
        : this(owner, "FFX.exe", ffxOffset, null, handler) { }

    /// <summary>Hook a function in <paramref name="module"/> by runtime offset.</summary>
    public ParryHook(FhModule owner, string module, nint offset, T handler)
        : this(owner, module, offset, null, handler) { }

    /// <summary>
    ///     Hook an exported function in <paramref name="module"/> by name
    ///     (e.g. <c>shell32.dll</c> / <c>ShellExecuteW</c>).
    /// </summary>
    public ParryHook(FhModule owner, string module, string fnName, T handler)
        : this(owner, module, 0, fnName, handler) { }

    private ParryHook(FhModule owner, string module, nint offset, string? fnName, T handler)
    {
        _owner   = owner;
        _module  = module;
        _offset  = offset;
        _fnName  = fnName;
        _handler = handler;
    }

    private FhMethodHandle<T> resolve()
        => _fnName is not null
            ? new FhMethodHandle<T>(new FhMethodLocation(_module, _fnName))
            : new FhMethodHandle<T>(new FhMethodLocation(_module, _offset));

    /// <summary>
    ///     Inserts this hook into the target function's chain. Mirrors the alpha10
    ///     <c>FhMethodHandle.hook()</c> call site.
    /// </summary>
    public bool install() => resolve().hook(_owner, _handler);

    /// <summary>
    ///     The continuation of the call chain immediately after this hook — i.e. the original game
    ///     function when this is the sole hook on the target. Mirrors the alpha10 <c>orig_fptr</c>.
    ///     Reconstructed per access because the underlying handle is a <c>ref struct</c>.
    /// </summary>
    public T orig
        => resolve().chain_from(_handler).fnptr
           ?? throw new InvalidOperationException(
               $"Parry: no chain continuation for {_module}!{_fnName ?? ("0x" + _offset.ToString("X"))} (hook not installed?).");
}
