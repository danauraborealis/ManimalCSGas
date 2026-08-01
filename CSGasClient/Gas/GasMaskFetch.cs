using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SPT.Common.Http;

namespace Manimal.CSGas.Gas
{
    // pulls the gas-mask tier lists from the server's config/gas_masks.json via the
    // /csgas/masks route — the client must never read server files from disk
    // (Fika/headless run on separate machines). failure keeps the compiled-in defaults,
    // so the mod stays functional against an older server dll too.
    internal static class GasMaskFetch
    {
        // properties, not fields — Newtonsoft fills either, but fields trip CS0649
        private class MasksPayload
        {
            public List<string> fullMasks { get; set; }
            public List<string> respirators { get; set; }
        }

        public static void Begin()
        {
            // off the main thread — GetJson is blocking (GoldenPick bridge pattern)
            Task.Run(() =>
            {
                try
                {
                    var json = RequestHandler.GetJson("/csgas/masks");
                    var payload = JsonConvert.DeserializeObject<MasksPayload>(json);
                    if (payload == null)
                    {
                        Plugin.LogSource?.LogWarning("[CSGas] /csgas/masks returned nothing parseable — keeping built-in mask defaults");
                        return;
                    }

                    var full = new HashSet<string>();
                    var resp = new HashSet<string>();
                    if (payload.fullMasks != null) foreach (var t in payload.fullMasks) AddTrimmed(full, t);
                    if (payload.respirators != null) foreach (var t in payload.respirators) AddTrimmed(resp, t);
                    // F12 extras stay additive on top of the server config
                    foreach (var t in Plugin.GasMaskExtraTpls.Value.Split(',')) AddTrimmed(full, t);
                    foreach (var t in Plugin.GasRespiratorExtraTpls.Value.Split(',')) AddTrimmed(resp, t);

                    // atomic swaps — readers on the main thread see old or new set, never partial
                    CSGasConstants.FullBlockTpls = full;
                    CSGasConstants.RespiratorTpls = resp;
                    Plugin.LogSource?.LogInfo($"[CSGas] mask config loaded from server: {full.Count} full masks, {resp.Count} respirators");
                }
                catch (Exception e)
                {
                    Plugin.LogSource?.LogWarning("[CSGas] mask config fetch failed — keeping built-in defaults: " + e.Message);
                }
            });
        }

        private static void AddTrimmed(HashSet<string> set, string tpl)
        {
            if (string.IsNullOrWhiteSpace(tpl)) return;
            set.Add(tpl.Trim());
        }
    }
}
