using SlotWeave;

public class Mod : IMod
{
    private readonly IModInterface modInterface;

    public Mod(IModInterface modInterface)
    {
        this.modInterface = modInterface;
        modInterface.Logger.Information("Mod loaded!");

        // 声明式 [Patch] — 自动发现，无需手动注册
        // Patches/PrefixExample.cs、ReplaceExample.cs 中的类会自动生效
    }

    public void Dispose() { }
}
