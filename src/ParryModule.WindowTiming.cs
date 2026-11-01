// SPDX-License-Identifier: MIT

namespace Fahrenheit.Mods.Parry;

public sealed partial class ParryModule
{
    private bool is_parry_slot_window_open(int slotIndex)
        => _debugFrameIndex < _parryExpiryTick[slotIndex];

    private void clear_parry_slot_windows()
    {
        Array.Clear(_parryExpiryTick);
        Array.Clear(_parryArmedAttackerId);
    }

    private void arm_parry_slot_windows(uint partyMask, int windowDurationTicks)
    {
        ulong expiry = _debugFrameIndex + (ulong)windowDurationTicks;
        for (int i = 0; i < PartyActorCapacity; i++)
        {
            if ((partyMask & (1u << i)) != 0)
            {
                _parryExpiryTick[i] = expiry;
                _parryArmedAttackerId[i] = _runtime.CurrentAttackerId;
            }
        }
    }

    private void tick_parry_window()
    {
        // The press update precedes native impact processing, so it must retain its first sample.
        if (_runtime.InputState == ParryInputState.Open && _runtime.ParryWindowActive
            && _runtime.WindowOpenFrame != _debugFrameIndex)
        {
            _runtime.ParryWindowElapsedTicks++;
            _runtime.ParryWindowRemainingTicks--;
            if (_runtime.ParryWindowRemainingTicks <= 0)
            {
                transition_to_whiff_lockout();
            }
        }
        else if (_runtime.InputState == ParryInputState.WhiffLockout)
        {
            _runtime.WhiffLockoutRemainingTicks--;
            if (_runtime.WhiffLockoutRemainingTicks <= 0)
            {
                transition_whiff_lockout_to_ready();
            }
        }
    }
}
