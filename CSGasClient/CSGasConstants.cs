using System.Collections.Generic;
using UnityEngine;

namespace Manimal.CSGas
{
    // what differs between the gases: the cloud colour, and what infected get for walking into it
    internal sealed class GasVariant
    {
        public static readonly GasVariant Cs = new GasVariant(Color.white, null);
        // yellowish green, the 78 a little greener so the two read apart in the air
        public static readonly GasVariant Rda68 = new GasVariant(new Color(0.86f, 0.90f, 0.30f), CSGasConstants.EnrageBuff);
        public static readonly GasVariant Rda78 = new GasVariant(new Color(0.66f, 0.88f, 0.28f), CSGasConstants.WeaknessBuff);

        public readonly Color Tint;
        public readonly string InfectedBuff;

        private GasVariant(Color tint, string infectedBuff)
        {
            Tint = tint;
            InfectedBuff = infectedBuff;
        }
    }

    internal static class CSGasConstants
    {
        // must match the key in ServerModFiles/db/CustomItems/cs_gas_grenade.json —
        // patches use this to gate "is this our grenade" checks
        public const string CsGasTpl = "6a5d6a5f4ed8c025a0a2cff0";

        // the 1.3 halloween vaccine grenades (retail ids, so quests and barters written for retail
        // resolve). same canister and same tear gas on people; each also doses infected with its buff
        public const string Rda68Tpl = "6a9854e0788619fe8502a6b7";
        public const string Rda78Tpl = "6a9854955358fa10380b8509";
        // retail buff names, registered server-side in db/CustomBuffs/cs_gas_buffs.json
        public const string EnrageBuff = "Buffs_ZombieGasEnrage";
        public const string WeaknessBuff = "Buffs_ZombieGasWeakness";

        // null for anything that isnt one of our grenades
        public static GasVariant VariantOf(string tpl)
        {
            switch (tpl)
            {
                case CsGasTpl: return GasVariant.Cs;
                case Rda68Tpl: return GasVariant.Rda68;
                case Rda78Tpl: return GasVariant.Rda78;
                default: return null;
            }
        }

        // base template we cloned (M18 smoke grenade) — handy for comparing behavior
        public const string M18Tpl = "617aa4dd8166f034d57de9c5";

        // stim-buff key registered server-side via WTT CustomBuffService
        // (ServerModFiles/db/CustomBuffs/cs_gas_buffs.json) — tremor + stamina debuff
        public const string GasBuffName = "CSGasExposure";

        // two protection tiers, mirroring (and extending) 1.0's TearGasBlockingItem:
        // full gas masks seal the eyes too → block everything. respirators only cover
        // the airway → block the health/cough side but the eyes still burn. these are the
        // compiled-in DEFAULTS — the authoritative list is the server's
        // config/gas_masks.json, fetched via /csgas/masks and swapped in at startup
        // (GasMaskFetch). F12 extra-tpl strings stay additive on top of either source.
        public const string Gp5Tpl = "5b432c305acfc40019478128";
        public const string Gp7Tpl = "60363c0c92ec1c31037959f5";
        public const string AvonM53Tpl = "689b880fff8b4adc420f5b56";        // WTT-ContentBackport
        public const string RespiratorTpl = "59e7715586f7742ee5789605";
        public const string SotrRespiratorTpl = "689b404db49f27df1c0873f6"; // WTT-ContentBackport

        // mutable on purpose: GasMaskFetch atomically swaps in the server-config sets
        public static HashSet<string> FullBlockTpls = new HashSet<string> { Gp5Tpl, Gp7Tpl, AvonM53Tpl };
        public static HashSet<string> RespiratorTpls = new HashSet<string> { RespiratorTpl, SotrRespiratorTpl };
    }
}
