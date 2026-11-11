using System;
using Manimal.CSGas.Gas;

namespace Manimal.CSGas.Api
{
    // for other mods, by reflection after a soft dependency on com.manimal.csgas. everything is a
    // const, a method over strings, or an Action of strings, so no type of ours has to be bound.
    // Named Gas while the namespace Manimal.CSGas.Gas exists: refer to it as Manimal.CSGas.Api.Gas.
    public static class Gas
    {
        public const string ApiVersion = "1.0";

        public const string CsGasTpl = CSGasConstants.CsGasTpl;
        public const string Rda68Tpl = CSGasConstants.Rda68Tpl;
        public const string Rda78Tpl = CSGasConstants.Rda78Tpl;

        // the buff each vaccine gas gives infected: RDA-68 enrage, RDA-78 weakness
        public const string EnrageBuff = CSGasConstants.EnrageBuff;
        public const string WeaknessBuff = CSGasConstants.WeaknessBuff;

        // how long after leaving the cloud an infected still counts as dosed
        public const float WindowSeconds = GasExposure.WindowSeconds;

        // true while that profile is inside, or within WindowSeconds of having been inside, that gas
        public static bool WasGassed(string profileId, string buffName) => GasExposure.WasGassed(profileId, buffName);

        // (profile id, buff name), once each time an infected goes from clean to dosed
        public static event Action<string, string> InfectedDosed
        {
            add => GasExposure.Dosed += value;
            remove => GasExposure.Dosed -= value;
        }
    }
}
