# ArmsRace (developer notes)

BepInEx plugin for **SULFUR**, published on Thunderstore as **ArmsRace**. The plugin GUID is
`kumo.sulfur.random_weapon_per_level`; it names the config file and the Harmony ID, so it must
never change. The build output is `Arms Race.dll`, the file name every release has shipped.

The player-facing store page is [`Thunderstore/README.md`](Thunderstore/README.md).

## Building

Machine-specific paths live only in `Directory.Build.props`: the SULFUR install, the Gale
profile that supplies the BepInEx core DLLs, and the `Thunderstore/` folder. Override any of
them on the command line, e.g. `-p:SulfurGameDir=...` or `-p:SulfurGaleProfileDir=...`.

```powershell
dotnet build RandomWeaponPerLevel.csproj -c Release
```

With `-p:DeployToSulfurProfile=true` the build also copies the DLL into `Thunderstore/` and
into the profile's `BepInEx/plugins/Arms Race/`, together with `Thunderstore/lang/`.

The game adds optional parameters to methods from time to time. Source that calls them keeps
compiling, but a DLL built against the previous game build no longer binds
(`MissingMethodException` at run time). Every release must therefore be built against the game
build it targets.

## Layout

```text
Class1.cs                  the whole plugin
Thunderstore/              package contents: manifest, store README, CHANGELOG, icon, lang/
Thunderstore/lang/         SULFUR Config translations (config names and descriptions only)
```
