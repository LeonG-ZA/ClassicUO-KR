// SPDX-License-Identifier: BSD-2-Clause
// "AMOU" mobile animation frames, as stored in AnimationFrame1-6.uop under
// build/animationframe/{body:D6}/{action:D2}.bin (zlib-inflated).
//
// File header (40 bytes, little-endian)
//   0x00 char[4] "AMOU"
//   0x04 u32     version (1)
//   0x08 u32     total size
//   0x0C u32     body id (matches the folder name)
//   0x10 i16 x4  overall bounds x1, y1, x2, y2, relative to the mobile's feet
//   0x18 u32     palette colour count (256)
//   0x1C u32     palette offset (0x28)
//   0x20 u32     frame count
//   0x24 u32     frame-table offset (0x428 = 0x28 + 256*4)
//
// Palette: count * 4 bytes of R, G, B, unused.
//
// Frame table: frameCount * 16 bytes, which the client binary-searches:
//   0x0 u16 action id, 0x2 u16 frame index (1-based),
//   0x4 i16 x1, 0x6 i16 y1, 0x8 i16 x2, 0xA i16 y2,
//   0xC u32 pixel-data offset, relative to this entry's own address.
//
// NOTE: the field at 0x00 is the *action* id, not a direction - it equals the
// file's own action number in every body/action pair checked, and never varies
// within a file. Directions are instead a flat split of the frame index: every
// frame count observed is a multiple of 5, and the frames are stored
// direction-major, so
//
//     framesPerDirection = frameCount / 5
//     direction          = (index - 1) / framesPerDirection
//     frameInDirection   = (index - 1) % framesPerDirection
//
// Verified on body 7 (armoured orc): frames 1-11 are one complete walk cycle in
// a single direction, 12-22 the same cycle in the next direction, and frames
// 1, 12, 23, 34, 45 are the same pose seen from five different angles. As in
// the 2D client, the remaining three of the eight on-screen directions are
// produced by mirroring.

using System.Buffers.Binary;

namespace ClassicUO.KR.Animations;

public readonly struct AmouFrameInfo
{
    public const int RecordSize = 16;

    /// <summary>Field at 0x00: the action id, constant across a file.</summary>
    public readonly ushort Action;

    /// <summary>Field at 0x02: 1-based frame index across all stored directions.</summary>
    public readonly ushort Index;

    public readonly short X1;
    public readonly short Y1;
    public readonly short X2;
    public readonly short Y2;

    /// <summary>Absolute offset of the frame's pixel data within the inflated file.</summary>
    public readonly int PixelOffset;

    public AmouFrameInfo(ushort action, ushort index, short x1, short y1, short x2, short y2, int pixelOffset)
    {
        Action = action;
        Index = index;
        X1 = x1;
        Y1 = y1;
        X2 = x2;
        Y2 = y2;
        PixelOffset = pixelOffset;
    }

    public int Width => X2 - X1;

    public int Height => Y2 - Y1;

    /// <summary>X of the mobile's anchor (its feet) relative to the frame's top-left corner.</summary>
    public int CenterX => -X1;

    /// <summary>Y of the mobile's anchor (its feet) relative to the frame's top-left corner.</summary>
    public int CenterY => -Y1;

    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>A decoded frame: straight (non-premultiplied) RGBA8888, row-major, top-down.</summary>
public sealed class AmouFrameBitmap
{
    public AmouFrameBitmap(int width, int height, int centerX, int centerY, uint[] pixels)
    {
        Width = width;
        Height = height;
        CenterX = centerX;
        CenterY = centerY;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>X of the mobile's anchor point relative to the bitmap's top-left.</summary>
    public int CenterX { get; }

    /// <summary>Y of the mobile's anchor point relative to the bitmap's top-left.</summary>
    public int CenterY { get; }

    /// <summary>Packed as (A &lt;&lt; 24) | (B &lt;&lt; 16) | (G &lt;&lt; 8) | R, matching FNA's SurfaceFormat.Color.</summary>
    public uint[] Pixels { get; }
}

public sealed class AmouAnimation
{
    public const uint Magic = 0x554F4D41; // "AMOU"

    /// <summary>KR stores five directions and mirrors the rest, exactly as the 2D client does.</summary>
    public const int StoredDirectionCount = 5;

    private readonly byte[] _data;
    private readonly AmouFrameInfo[] _frames;

    /// <summary>Palette entries as (B &lt;&lt; 16) | (G &lt;&lt; 8) | R; alpha is supplied per pixel.</summary>
    private readonly uint[] _palette;

    private AmouAnimation(
        byte[] data,
        uint version,
        uint bodyId,
        short x1,
        short y1,
        short x2,
        short y2,
        uint[] palette,
        AmouFrameInfo[] frames)
    {
        _data = data;
        Version = version;
        BodyId = bodyId;
        BoundsX1 = x1;
        BoundsY1 = y1;
        BoundsX2 = x2;
        BoundsY2 = y2;
        _palette = palette;
        _frames = frames;
        Action = frames.Length != 0 ? frames[0].Action : (ushort)0;
    }

    public uint Version { get; }

    public uint BodyId { get; }

    /// <summary>The action id the frame table carries (constant across the file).</summary>
    public ushort Action { get; }

    public short BoundsX1 { get; }

    public short BoundsY1 { get; }

    public short BoundsX2 { get; }

    public short BoundsY2 { get; }

    /// <summary>All frames, in stored order: direction-major, then frame within the direction.</summary>
    public IReadOnlyList<AmouFrameInfo> Frames => _frames;

    /// <summary>
    /// Frames per stored direction. Zero when the table is not a clean multiple
    /// of <see cref="StoredDirectionCount"/>, which would mean the layout
    /// assumption does not hold for this file.
    /// </summary>
    public int FramesPerDirection =>
        _frames.Length != 0 && _frames.Length % StoredDirectionCount == 0
            ? _frames.Length / StoredDirectionCount
            : 0;

    /// <summary>True when the frame table splits cleanly into five directions.</summary>
    public bool HasRegularDirections => FramesPerDirection != 0;

    public static bool IsAmou(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(data) == Magic;

    public static AmouAnimation Parse(byte[] data)
    {
        if (!IsAmou(data))
        {
            throw new InvalidDataException("not an AMOU animation file.");
        }

        if (data.Length < 40)
        {
            throw new InvalidDataException($"AMOU file truncated ({data.Length} bytes).");
        }

        ReadOnlySpan<byte> s = data;

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(0x04));
        uint bodyId = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(0x0C));
        short x1 = BinaryPrimitives.ReadInt16LittleEndian(s.Slice(0x10));
        short y1 = BinaryPrimitives.ReadInt16LittleEndian(s.Slice(0x12));
        short x2 = BinaryPrimitives.ReadInt16LittleEndian(s.Slice(0x14));
        short y2 = BinaryPrimitives.ReadInt16LittleEndian(s.Slice(0x16));
        int paletteCount = BinaryPrimitives.ReadInt32LittleEndian(s.Slice(0x18));
        int paletteOffset = BinaryPrimitives.ReadInt32LittleEndian(s.Slice(0x1C));
        int frameCount = BinaryPrimitives.ReadInt32LittleEndian(s.Slice(0x20));
        int frameTableOffset = BinaryPrimitives.ReadInt32LittleEndian(s.Slice(0x24));

        if (paletteCount < 0 || paletteOffset < 0 || (long)paletteOffset + ((long)paletteCount * 4) > data.Length)
        {
            throw new InvalidDataException($"AMOU palette out of range (count {paletteCount}, offset {paletteOffset}).");
        }

        if (frameCount < 0 || frameTableOffset < 0 ||
            (long)frameTableOffset + ((long)frameCount * AmouFrameInfo.RecordSize) > data.Length)
        {
            throw new InvalidDataException($"AMOU frame table out of range (count {frameCount}, offset {frameTableOffset}).");
        }

        uint[] palette = new uint[paletteCount];

        for (int i = 0; i < paletteCount; i++)
        {
            int p = paletteOffset + (i * 4);
            // Stored R, G, B, unused -> packed (B << 16) | (G << 8) | R.
            //
            // This was read as B, G, R until a comparison against the real KR
            // client showed every creature coming out blue where KR draws it
            // tan or red. The giveaway is that the mistake is invisible on the
            // human bodies, whose skin is pure grey, which is why it survived
            // the earlier verification passes.
            palette[i] = (uint)(data[p + 2] << 16) | (uint)(data[p + 1] << 8) | data[p];
        }

        AmouFrameInfo[] frames = new AmouFrameInfo[frameCount];

        for (int i = 0; i < frameCount; i++)
        {
            int e = frameTableOffset + (i * AmouFrameInfo.RecordSize);
            ReadOnlySpan<byte> f = s.Slice(e, AmouFrameInfo.RecordSize);

            uint relative = BinaryPrimitives.ReadUInt32LittleEndian(f.Slice(0x0C));

            frames[i] = new AmouFrameInfo(
                BinaryPrimitives.ReadUInt16LittleEndian(f),
                BinaryPrimitives.ReadUInt16LittleEndian(f.Slice(0x02)),
                BinaryPrimitives.ReadInt16LittleEndian(f.Slice(0x04)),
                BinaryPrimitives.ReadInt16LittleEndian(f.Slice(0x06)),
                BinaryPrimitives.ReadInt16LittleEndian(f.Slice(0x08)),
                BinaryPrimitives.ReadInt16LittleEndian(f.Slice(0x0A)),
                e + (int)relative);
        }

        return new AmouAnimation(data, version, bodyId, x1, y1, x2, y2, palette, frames);
    }

    /// <summary>
    /// Looks up a frame by stored direction (0..4) and frame within that
    /// direction (0-based).
    /// </summary>
    public bool TryGetFrameInfo(int direction, int frame, out AmouFrameInfo info)
    {
        int perDirection = FramesPerDirection;

        if (perDirection == 0 ||
            (uint)direction >= StoredDirectionCount ||
            (uint)frame >= (uint)perDirection)
        {
            info = default;

            return false;
        }

        return TryGetFrameByIndex((direction * perDirection) + frame + 1, out info);
    }

    /// <summary>
    /// Binary-searches the frame table by its 1-based stored index - the same
    /// lookup the client does.
    /// </summary>
    public bool TryGetFrameByIndex(int index, out AmouFrameInfo info)
    {
        int lo = 0;
        int hi = _frames.Length - 1;

        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            int key = _frames[mid].Index;

            if (key == index)
            {
                info = _frames[mid];

                return true;
            }

            if (key < index)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        info = default;

        return false;
    }

    public AmouFrameBitmap? Decode(int direction, int frame) =>
        TryGetFrameInfo(direction, frame, out AmouFrameInfo info) ? Decode(info) : null;

    public AmouFrameBitmap DecodeByIndex(int index) =>
        TryGetFrameByIndex(index, out AmouFrameInfo info)
            ? Decode(info)
            : throw new ArgumentOutOfRangeException(nameof(index), $"no frame with index {index}.");

    public AmouFrameBitmap Decode(in AmouFrameInfo info) => AmouDecoder.Decode(_data, info, _palette);
}
