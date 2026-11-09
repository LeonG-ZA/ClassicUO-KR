// SPDX-License-Identifier: BSD-2-Clause
// Entry-name patterns for the KR texture packages.
//
// None of these names can be read out of a package: UOP entries are keyed by
// a hash of their path. They were recovered by hashing candidate patterns
// against the entry keys until every key in a package was accounted for.

namespace ClassicUO.KR.Textures;

public static class KrTexturePackages
{
    /// <summary>
    /// Texture.uop: the world art, including every terrain ground texture.
    /// 9,348 entries in the 2.53 assets.
    /// </summary>
    public const string WorldArtPackage = "Texture.uop";

    /// <summary>
    /// TerrainTexture.uop. The name is misleading: its 20 entries are not
    /// terrain ground. Ids 1-17 are KR's per-object light textures (candle,
    /// sconce, window, mushroom...), 21 the noise the night ambient is drawn
    /// with, the rest sky art. Terrain ground art lives in Texture.uop under
    /// <see cref="WorldArtEntryName"/>.
    /// </summary>
    public const string TerrainTexturePackage = "TerrainTexture.uop";

    /// <summary>
    /// build/terraintexture/{id:D8}.dds. The 2.53 assets ship ids 1 to 17 and
    /// 20 to 22; 18 and 19 are absent.
    /// </summary>
    public static string TerrainTextureEntryName(int id) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"build/terraintexture/{id:D8}.dds");

    /// <summary>build/worldart/{artId:D8}.dds, the pattern Texture.uop uses.</summary>
    public static string WorldArtEntryName(int artId) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"build/worldart/{artId:D8}.dds");
}
