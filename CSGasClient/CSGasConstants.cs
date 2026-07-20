using System.Collections.Generic;

namespace Manimal.CSGas
{
    internal static class CSGasConstants
    {
        // must match the key in ServerModFiles/db/CustomItems/cs_gas_grenade.json —
        // patches use this to gate "is this our grenade" checks
        public const string CsGasTpl = "6a5d6a5f4ed8c025a0a2cff0";

        // base template we cloned (M18 smoke grenade) — handy for comparing behavior
        public const string M18Tpl = "617aa4dd8166f034d57de9c5";

        // stim-buff key registered server-side via WTT CustomBuffService
        // (ServerModFiles/db/CustomBuffs/cs_gas_buffs.json) — tremor + stamina debuff
        public const string GasBuffName = "CSGasExposure";

        // two protection tiers, mirroring (and extending) 1.0's TearGasBlockingItem:
        // full gas masks seal the eyes too → block everything. respirators only cover
        // the airway → block the health/cough side but the eyes still burn (sanding +
        // contusion stay). tpls verified against the 4.0 server db + WTT-ContentBackport.
        // Plugin.Awake appends extra tpls from config to either set.
        public const string Gp5Tpl = "5b432c305acfc40019478128";
        public const string Gp7Tpl = "60363c0c92ec1c31037959f5";
        public const string AvonM53Tpl = "689b880fff8b4adc420f5b56";        // WTT-ContentBackport
        public const string RespiratorTpl = "59e7715586f7742ee5789605";
        public const string SotrRespiratorTpl = "689b404db49f27df1c0873f6"; // WTT-ContentBackport

        public static readonly HashSet<string> FullBlockTpls = new HashSet<string> { Gp5Tpl, Gp7Tpl, AvonM53Tpl };
        public static readonly HashSet<string> RespiratorTpls = new HashSet<string> { RespiratorTpl, SotrRespiratorTpl };
    }
}
