# Atlas

Valheim mod (BepInEx + Jotunn) for worlds without a map: recording discoveries at a cartography
table updates a hand-inked atlas in the style of a classic fantasy novel, shown with the map key;
reading the table opens the game's interactive map for placing pins. Client-side.

Players: install it from a mod manager. Features and controls are on the mod page, whose source is
[`package/README.md`](package/README.md).

## Building

Requires the .NET SDK (8+) and a Valheim install. The build locates Valheim through the Steam
registry entry; override with `-p:VALHEIM_INSTALL="<path>"`.

```
dotnet build -c Release                           # build bin/Release/Atlas.dll
dotnet build -c Release -t:Package                # also write dist/Atlas-<version>.zip
```

The first build generates publicized Valheim assemblies in `Valheim_Data/Managed/publicized_assemblies`.

The build touches nothing outside the repo. To test in game, install the zip with a mod manager
(e.g. import it into a [Gale](https://github.com/Kesomannen/gale) profile).

## Preview renders

`tools/preview/` renders the atlas offline from the minimap cache of a local world, so drawing
changes can be checked without starting the game. It needs a Valheim install (override the Steam
path with `$env:VALHEIM_INSTALL`), a world you have explored with the map on (its cache lives in
`%USERPROFILE%\AppData\LocalLow\IronGate\Valheim\worlds_local\<world>`) and a Gale profile with
BepInEx, whose core DLLs the script copies beside the preview. From the repo root, in PowerShell:

```
# Build the mod and the preview, then render views of a world into tools/preview/img/
.\tools\preview\run.ps1 -World Velen -Views overview,start:0:0:600:1,tilesMeadows:-48:192:4

# Add pins drawn into the sheet (GLYPH:x:z, append :x for a checked pin) and the Sacrificial Stones
.\tools\preview\run.ps1 -Views start:0:0:600:1 -Pin 'House:205:-119','Fire:158:-158:x' -Temple '-111:263'

# The pin catalogue (every glyph, plain and checked, at sheet size and 4x); run.ps1 must have run once
.\tools\preview\out\Preview.exe --pincat tools\preview\img\pincat.png

# The sea mark catalogue (sea figures with their washes, the boil and fish stand-ins, sea waves, lake lines, at sheet size and 4x)
.\tools\preview\out\Preview.exe --seacat tools\preview\img\seacat.png

# All published samples into tools/preview/img/; -Publish also replaces those in screenshots/
.\tools\preview\samples.ps1
.\tools\preview\samples.ps1 -Publish
```

Views are comma-separated:

| View | Renders |
| --- | --- |
| `overview` | The whole sheet |
| `NAME:x:z:width:scale` | A `width`-pixel crop of the sheet centred on world `x`, `z`, magnified `scale` times |
| `tilesNAME:x:z:zoom` | Close-up tiles (`zoom` 2 or 4) round world `x`, `z`, as the map shows them zoomed in |

`-Alt 'NAME:x:z:radius'` forces a biome variant there (e.g. `'Wolf Mountain:-7500:3000:900'`),
since the preview cannot compute them. Output is written to `tools/preview/img/`, which is ignored
by git. The preview cannot run the world generator, so rivers, Deep North woods and other
generator-driven details differ from the game.

## Project layout

| Path | Purpose |
| --- | --- |
| `src/` | Plugin, the atlas controller and view, the cartography table patches, the recorded snapshot |
| `src/Ink/` | The ink drawing: world data from vanilla's minimap, the pen rasterizer, distance fields, glyphs, disk cache |
| `assets/translations/` | Player-facing text, one JSON file per Valheim language |
| `tools/make_icon.ps1` | Generates `package/icon.png` from game-icons.net |
| `tools/preview/` | Offline preview of the drawing from a local world's minimap cache; a developer tool, not part of the mod or its package |
| `screenshots/` | Sample renders from the preview (`tools/preview/samples.ps1 -Publish`) |
| `package/` | Mod page README and icon shipped in the package |

## Releases

CI (`.github/workflows/build.yml`) builds the package on every push and pull request. Pushing a
`vX.Y.Z` tag checks that the tag matches the version in `Atlas.csproj` and `src/AtlasPlugin.cs`
and has a `CHANGELOG.md` section, then creates a GitHub release and publishes to Hexium.

## License

Code: [MIT](LICENSE). The icon is "Treasure map" by Lorc from [game-icons.net](https://game-icons.net),
licensed under [CC BY 3.0](https://creativecommons.org/licenses/by/3.0/).
