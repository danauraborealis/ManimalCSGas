# Port ManimalCSGas to SPT 4.1.3

Update gas client integration and server item/config registration from the 4.0.13 API to SPT 4.1.3.

## Implementation notes

- Target .NET 10 on the server and implement IModMetadata, including HasPrepatcher=false and SptVersion ~4.1.3.
- Use the 4.1.3 OnLoadAsync(CancellationToken) contract and route signatures; retain /csgas/masks behavior and mask-tier configuration.
- Use WTT CommonLib 3.0.6 services to register gas items and buffs.
- Map the client gas cloud, cough animation, prefab repair and volumetric emitter references to the current EFT member names.
- Resolve installed dependencies through SPTPath and make copying into the game an explicit deployment option.

## Validation specific to this mod

Client/server builds passed and /csgas/masks returned a nonempty configuration. The complete mod set loaded and was used for the successful Icebreaker raid. Gas exposure, every mask tier and multiplayer gas synchronization were not separately confirmed by the tester; successful extraction does not establish those behaviors.

## Compatibility and evidence

Target: **SPT 4.1.3**, EFT **0.16.9.5.40743**. This is a source contribution for that environment, not a claim of compatibility with future SPT releases or Fika.

The tester successfully loaded Icebreaker, entered a raid and extracted. In subsequent feedback they confirmed the blowtorch, extraction and doors work, and Black Division bots appeared to behave normally after the SAIN fix. These are user-reported functional observations, not automated coverage of every encounter or performance benchmarks. Ten continuous hours, all quests and multiplayer have not been tested.

The migration used installed SPT 4.1.3 assemblies and these guides:
- https://wiki.sp-tushonka.com/en/modding/SPT_41_Modding/Server_413_Changes
- https://wiki.sp-tushonka.com/en/modding/SPT_41_Modding/client/Class_Name_Mappings
- https://wiki.sp-tushonka.com/en/modding/SPT_41_Modding/server/Mod_Web_Pages

## Build and packaging

Use the .NET 10 SDK and an installed SPT 4.1.3 dependency set. Pass `-p:SPTPath=<installation-root>` to dotnet build; the fallback expects this repository under a development tree. DeployToGame defaults to disabled. Build the client and server projects in Release, and the companion dependency ports first. Proprietary game DLLs are local references and must not be committed.


## Contribution build checks

The publication working copies were built in Release against the installed SPT 4.1.3 assemblies with deployment disabled. Client/server builds passed for Icebreaker, MoreBotsAPI, BlackDiv and ManimalCSGas; the Backport prepatcher and DynamicMaps client also passed. Existing compiler warnings remain in several ports. Fika was not built as part of this contribution gate. These checks validate compilation, not untested gameplay.
