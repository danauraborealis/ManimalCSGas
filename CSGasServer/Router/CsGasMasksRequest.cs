using SPTarkov.Server.Core.Models.Utils;

namespace CSGas.Router;

// body is ignored — the masks route is a plain fetch, but StaticRouter wants a type
public record CsGasMasksRequest : IRequestData;
