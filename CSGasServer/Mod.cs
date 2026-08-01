using System.Reflection;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Mod;

namespace CSGas;

// forge-compliant: GUID lowercase reverse-domain, must match the BepInEx plugin GUID.
// Name + Author are letters/numbers only. Version is read from the assembly so it
// only gets bumped in one place (Directory.Build.props at the repo root).
public record ModMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "com.manimal.csgas";
    public override string Name { get; init; } = "CSGas";
    public override string Author { get; init; } = "Manimal";
    public override List<string>? Contributors { get; init; }
    public override SemanticVersioning.Version Version { get; init; } =
        new(typeof(ModMetadata).Assembly.GetName().Version!.ToString(3));
    public override SemanticVersioning.Range SptVersion { get; init; } = new("~4.0.0");
    public override List<string>? Incompatibilities { get; init; }
    public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } = new()
    {
        { "com.wtt.commonlib", new SemanticVersioning.Range("~2.0.20") }
    };
    public override string? Url { get; init; } = "https://github.com/WelcomeToTarkov/ManimalCSGas";
    public override bool? IsBundleMod { get; init; } = true;
    public override string License { get; init; } = "MIT";
}

[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 2)]
public class CSGasServer(
    WTTServerCommonLib.WTTServerCommonLib wttCommon,
    CsGasMaskConfigStore maskConfig) : IOnLoad
{
    public async Task OnLoad()
    {
        // gas-mask tiers config, served to clients via /csgas/masks
        maskConfig.Load();
        // registers everything under ServerModFiles/db/CustomItems — the cs gas grenade
        // json rides in as an M18 clone until the 1.0 assets/behavior land
        var assembly = Assembly.GetExecutingAssembly();
        await wttCommon.CustomItemServiceExtended.CreateCustomItems(assembly);
        // registers db/CustomBuffs — the CSGasExposure stim debuff (tremor + stamina) the
        // client applies via TryDoExternalBuff while the player stands in the gas
        await wttCommon.CustomBuffService.CreateCustomBuffs(assembly);
    }
}
