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

## Project layout

| Path | Purpose |
| --- | --- |
| `src/` | Plugin, the atlas controller and view, the cartography table patches, the recorded snapshot |
| `src/Ink/` | The ink drawing: world data from vanilla's minimap, the pen rasterizer, distance fields, glyphs, disk cache |
| `assets/translations/` | Player-facing text, one JSON file per Valheim language |
| `tools/make_icon.ps1` | Generates `package/icon.png` from game-icons.net |
| `package/` | Mod page README and icon shipped in the package |

## Releases

CI (`.github/workflows/build.yml`) builds the package on every push and pull request. Pushing a
`vX.Y.Z` tag checks that the tag matches the version in `Atlas.csproj` and `src/AtlasPlugin.cs`
and has a `CHANGELOG.md` section, then creates a GitHub release and publishes to Hexium.

## License

Code: [MIT](LICENSE). The icon is "Treasure map" by Lorc from [game-icons.net](https://game-icons.net),
licensed under [CC BY 3.0](https://creativecommons.org/licenses/by/3.0/).
