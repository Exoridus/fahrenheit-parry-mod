namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule {
    private interface IParryBattleAdapter {
        Btl* GetBattle();
        BtlWindow* GetCurrentWindow();
        Chr* GetPlayerCharacters();
        Chr* GetMonsterCharacters();
    }

    private sealed class FfxParryBattleAdapter : IParryBattleAdapter {
        public Btl* GetBattle() => FhFfx.Globals.Battle.btl;
        public BtlWindow* GetCurrentWindow() => FhFfx.Globals.Battle.cur_window;
        public Chr* GetPlayerCharacters() => FhFfx.Globals.Battle.player_characters;
        public Chr* GetMonsterCharacters() => FhFfx.Globals.Battle.monster_characters;
    }

    private static readonly IParryBattleAdapter _battleAdapter = new FfxParryBattleAdapter();
}
