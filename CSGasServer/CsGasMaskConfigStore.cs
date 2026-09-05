using Path = System.IO.Path;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using System.Reflection;
using SPTarkov.DI.Annotations;

namespace CSGas;

// holds the raw json of config/gas_masks.json for the /csgas/masks route. loaded at
// OnLoad; falls back to the compiled-in defaults if the file is missing/broken so the
// route always answers something the client can parse.
[Injectable(InjectionType.Singleton)]
public class CsGasMaskConfigStore
{
    private const string FallbackJson =
        "{\"fullMasks\":[\"5b432c305acfc40019478128\",\"60363c0c92ec1c31037959f5\",\"689b880fff8b4adc420f5b56\"],"
        + "\"respirators\":[\"59e7715586f7742ee5789605\",\"689b404db49f27df1c0873f6\"]}";

    public string Json { get; private set; } = FallbackJson;

    public void Load()
    {
        try
        {
            var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            var path = Path.Combine(dir, "config", "gas_masks.json");
            if (File.Exists(path))
            {
                Json = File.ReadAllText(path);
            }
        }
        catch
        {
            Json = FallbackJson;
        }
    }
}
