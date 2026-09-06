using System.Reflection;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Mod;

namespace CSGas;

// forge-compliant: GUID lowercase reverse-domain, must match the BepInEx plugin GUID.
// Name + Author are letters/numbers only. Version is read from the assembly so it
// only gets bumped in one place (Directory.Build.props at the repo root).
public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.manimal.csgas";
    public string Name { get; init; } = "CSGas";
    public string Author { get; init; } = "Manimal";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } =
        new(typeof(ModMetadata).Assembly.GetName().Version!.ToString(3));
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.5");
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } = new()
    {
        { "com.wtt.commonlib", new SemanticVersioning.Range("~3.0.6") }
    };
    public string? Url { get; init; } = "https://github.com/WelcomeToTarkov/ManimalCSGas";
    public bool HasPrepatcher { get; init; } = false;
    public string License { get; init; } = "MIT";
}

[Injectable(TypePriority = OnLoadOrder.Preload + 10)]
public class CSGasServer(
    WTTServerCommonLib.WTTServerCommonLib wttCommon,
    CsGasMaskConfigStore maskConfig) : IOnLoad
{
    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        // gas-mask tiers config, served to clients via /csgas/masks
        cancellationToken.ThrowIfCancellationRequested();
        await maskConfig.LoadAsync(cancellationToken);
        // registers everything under ServerModFiles/db/CustomItems — the cs gas grenade
        // json rides in as an M18 clone until the 1.0 assets/behavior land
        var assembly = Assembly.GetExecutingAssembly();
        await wttCommon.CustomItemServiceExtended.CreateCustomItems(assembly);
        // registers db/CustomBuffs — the CSGasExposure stim debuff (tremor + stamina) the
        // client applies via TryDoExternalBuff while the player stands in the gas
        await wttCommon.CustomBuffService.CreateCustomBuffs(assembly);
    }
}
