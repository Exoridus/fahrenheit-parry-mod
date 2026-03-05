namespace Fahrenheit.Mods.Parry;

internal static class ImGuiNativeExtra {
    [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
    public static extern void igSetWindowFontScale(float scale);
}
