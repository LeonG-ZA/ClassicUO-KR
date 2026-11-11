# ClassicUO-KR

An Ultima Online client built on [ClassicUO](https://github.com/ClassicUO/ClassicUO)
that can draw the world with art from your own Kingdom Reborn or Enhanced Client
install. Networking, gumps, UI, input and macros stay ClassicUO.

**No game assets are included.** This repository holds code, numbers and a small
amount of original art. Every asset is read at run time from installs you
already have.

This is an unofficial fan project. It is not affiliated with or endorsed by
Electronic Arts or Broadsword. Ultima Online is a trademark of its owners.

## Layout

| Path | What it is |
|---|---|
| `src/ClassicUO.KR/` | The asset library: UOP containers, animations, world data and effects |
| `external/ClassicUO/` | ClassicUO with this project's integration applied |
| `tests/ClassicUO.KR.Tests/` | Tests; the ones that need game files skip themselves without them |
| `tools/KrAssetTool/` | Command-line tool for inspecting the file formats |
| `tools/flame-tuner/` | Browser tool for placing flames on light sources |
| `tools/modernuo-setweather/` | A `[SetWeather` test command for ModernUO servers |

## Building

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build ClassicUO-KR.slnx -c Release
dotnet build external/ClassicUO/src/ClassicUO.Client/ClassicUO.Client.csproj -c Release
```

To run the tests against real files, set `UOKR_PATH`, `EC_PATH` and `UO_PATH` to
your installs:

```bash
dotnet test tests/ClassicUO.KR.Tests/ClassicUO.KR.Tests.csproj -c Release
```

## Running

The client is built to `external/ClassicUO/bin/Release/net10.0`. Run `cuo.exe`
once to create `settings.json` beside it, then edit these values:

```json
{
  "ultimaonlinedirectory": "path to your 2D client",
  "clientversion": "7.0.107.76",
  "ip": "127.0.0.1",
  "port": 2593,
  "kr_path": "path to your Kingdom Reborn install",
  "ec_path": "path to your Enhanced Client install",
  "kr_animations": true,
  "kr_terrain": true,
  "kr_statics": true,
  "kr_particle_effects": true
}
```

The 2D client's files are always required. `kr_path` and `ec_path` are optional:

- With only `kr_path`, the Kingdom Reborn art is used.
- With only `ec_path`, the Enhanced Client art is used. Turn on `ec_humans`,
  `ec_terrain`, `ec_statics` and `ec_effects` too.
- With both, the Kingdom Reborn art is used, and each `ec_` setting switches
  that part over to the Enhanced Client.
- With neither, the client behaves like ClassicUO.

Anything an install has no art for falls back to the 2D art.

### You don't need Kingdom Reborn

The Enhanced Client still carries the Kingdom Reborn art, so an Enhanced Client
install on its own is enough. The one thing it lacks is the torch, candle and
wall-sconce flames, which it ships empty. When `kr_path` is left empty, this
project draws its own flames in their place.

### Torches, lanterns and candles held in the hand

Kingdom Reborn never shipped left-hand animations for holding a torch, lantern
or candle. With only `kr_path` set, this project draws its own versions on the
character. When `ec_path` is set and the Enhanced Client draws the characters
(with `ec_humans` on, or with no `kr_path` at all), the Enhanced Client's own
versions are used instead.

## License

Code in this repository is under the BSD 2-Clause license (see `LICENSE`).
`external/ClassicUO` is under its own BSD 2-Clause license, and the libraries in
`external/ClassicUO/external` keep their own licenses.
