using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Utils;

namespace CSGas.Router;

// serves config/gas_masks.json to clients. the client must NEVER read server files from
// disk (Fika/headless — separate machines), so mask tpls travel over this route instead.
[Injectable]
public class CsGasMasksRouter(JsonUtil jsonUtil, CsGasMaskConfigStore store)
    : StaticRouter(
        jsonUtil,
        [
            new RouteAction<CsGasMasksRequest>(
                "/csgas/masks",
                (url, info, sessionId, output) => new ValueTask<string>(store.Json)
            ),
        ]
    ) { }
