// SPDX-License-Identifier: BSD-2-Clause
// AnimationSequence.uop: per-body animation metadata, keyed by body id under
// build/animationsequence/{body:D8}.bin.
//
// This is the table that answers "which KR action do I play for this UO action,
// how many frames does it have, and how fast does it run".
//
// Header (24 bytes)
//   0x00 u32 body id
//   0x04 16 bytes, zero in every file seen
//   0x14 u32 action count
//
// Then `action count` variable-length records:
//   +0x00 u32 action index (equals the record's position)
//   +0x04 i32 frames per direction, 0 when KR has no art for this action
//   +0x08 i32 fallback action, -1 when this action has its own art
//   +0x0C i32 unknown, -1 in every record seen
//   +0x10 f32 playback rate in frames per second
//   +0x14 byte[16] unknown; 0x80 when the action is present, 0 when it is not
//   +0x24 u32 event count
//   +0x28 `event count` variable-length events (see KrAnimationEvent)
//
// Records are stored in order, so a record's position in the file is its action
// index. The u32 at +0x00 repeats that index, but ten records across the 2.53
// assets are zero-filled placeholders whose index field reads 0, so the
// position is the authoritative value.
//
// A trailing section follows the records and is not yet decoded; it is kept
// verbatim in TrailingData. It always begins with a u32 count followed by the
// bytes 0x00 0xFF, which is how the end of the record list can be confirmed.
//

using System.Buffers.Binary;

namespace ClassicUO.KR.Animations;

/// <summary>
/// Known animation event types. Type 6 carries 16 bytes of still-undecoded
/// per-frame data (signed byte pairs, one entry per frame of the direction);
/// type 16 carries a sound trigger.
/// </summary>
public enum KrAnimationEventType
{
    PerFrameData = 6,
    Sound = 16
}

/// <summary>
/// A timed event on an animation, e.g. the footfalls on a walk cycle. For body
/// 400 action 0 these are frames 1 and 5 with sound ids 299 and 300.
/// </summary>
/// <remarks>
/// Records are variable-length: 12 bytes of header followed by a payload whose
/// size depends on the type (16 bytes for type 6, 20 bytes for type 16). Every
/// sequence in the 2.53.0.2 assets parses exactly with this rule.
/// </remarks>
public readonly struct KrAnimationEvent
{
    public const int HeaderSize = 12;

    private readonly byte[] _payload;

    /// <summary>Frame the event applies to; for type 6 this is a 0-based frame index.</summary>
    public readonly uint Frame;

    /// <summary>Unknown; 1 in every record seen.</summary>
    public readonly uint Unknown;

    public readonly uint RawType;

    public KrAnimationEvent(uint frame, uint unknown, uint rawType, byte[] payload)
    {
        Frame = frame;
        Unknown = unknown;
        RawType = rawType;
        _payload = payload;
    }

    public KrAnimationEventType Type => (KrAnimationEventType)RawType;

    public ReadOnlySpan<byte> Payload => _payload;

    public bool IsSound => Type == KrAnimationEventType.Sound;

    /// <summary>Sound id for a <see cref="KrAnimationEventType.Sound"/> event, else 0.</summary>
    public uint SoundId =>
        IsSound && _payload.Length >= 16 ? BinaryPrimitives.ReadUInt32LittleEndian(_payload.AsSpan(12)) : 0;

    /// <summary>Volume scale for a sound event; 1.0 in every record seen.</summary>
    public float Volume =>
        IsSound && _payload.Length >= 20
            ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(_payload.AsSpan(16)))
            : 0f;

    /// <summary>Total on-disk size of a record of the given type, or -1 when unknown.</summary>
    public static int SizeOf(uint rawType) => rawType switch
    {
        (uint)KrAnimationEventType.PerFrameData => 28,
        (uint)KrAnimationEventType.Sound => 32,
        _ => -1
    };
}

public sealed class KrAnimationAction
{
    public KrAnimationAction(
        int action,
        int rawIndex,
        int framesPerDirection,
        int fallbackAction,
        int unknown,
        float framesPerSecond,
        byte[] flags,
        KrAnimationEvent[] events)
    {
        Action = action;
        RawIndex = rawIndex;
        FramesPerDirection = framesPerDirection;
        FallbackAction = fallbackAction;
        Unknown = unknown;
        FramesPerSecond = framesPerSecond;
        Flags = flags;
        Events = events;
    }

    /// <summary>The action index, taken from the record's position in the file.</summary>
    public int Action { get; }

    /// <summary>The index the record itself carries; 0 in a few placeholder records.</summary>
    public int RawIndex { get; }

    /// <summary>Frames in each of the five stored directions; 0 when KR has no art.</summary>
    public int FramesPerDirection { get; }

    /// <summary>Action to substitute when this one has no art; -1 when it does.</summary>
    public int FallbackAction { get; }

    public int Unknown { get; }

    public float FramesPerSecond { get; }

    public byte[] Flags { get; }

    public KrAnimationEvent[] Events { get; }

    /// <summary>True when KR ships art for this action.</summary>
    public bool HasArt => FallbackAction < 0 && FramesPerDirection > 0;

    public int TotalFrames => FramesPerDirection * AmouAnimation.StoredDirectionCount;
}

public sealed class KrAnimationSequence
{
    private const int HeaderSize = 0x18;
    private const int RecordHeaderSize = 0x28;

    private readonly KrAnimationAction[] _actions;

    private KrAnimationSequence(int bodyId, KrAnimationAction[] actions, byte[] trailingData, bool enhanced = false)
    {
        Enhanced = enhanced;
        BodyId = bodyId;
        _actions = actions;
        TrailingData = trailingData;
    }

    public int BodyId { get; }

    /// <summary>Read in the Enhanced Client's layout: its flag bytes hold no KR seat.</summary>
    public bool Enhanced { get; }

    public IReadOnlyList<KrAnimationAction> Actions => _actions;

    /// <summary>The not-yet-decoded section after the action records.</summary>
    public byte[] TrailingData { get; }

    public static KrAnimationSequence Parse(byte[] data)
    {
        if (data.Length < HeaderSize)
        {
            throw new InvalidDataException($"animation sequence truncated ({data.Length} bytes).");
        }

        ReadOnlySpan<byte> s = data;

        int bodyId = BinaryPrimitives.ReadInt32LittleEndian(s);
        int count = BinaryPrimitives.ReadInt32LittleEndian(s.Slice(0x14));

        if (count < 0 || count > 4096)
        {
            throw new InvalidDataException($"animation sequence for body {bodyId}: implausible action count {count}.");
        }

        // KR files always list actions; none at 0x14 is the Enhanced Client's layout.
        if (count == 0 && TryParseEnhanced(data, bodyId) is { } enhanced)
        {
            return enhanced;
        }

        KrAnimationAction[] actions = new KrAnimationAction[count];
        int offset = HeaderSize;

        for (int i = 0; i < count; i++)
        {
            if (offset + RecordHeaderSize > data.Length)
            {
                throw new InvalidDataException(
                    $"animation sequence for body {bodyId}: record {i} runs past the end of the file.");
            }

            ReadOnlySpan<byte> r = s.Slice(offset);

            int action = BinaryPrimitives.ReadInt32LittleEndian(r);
            int framesPerDirection = BinaryPrimitives.ReadInt32LittleEndian(r.Slice(0x04));
            int fallback = BinaryPrimitives.ReadInt32LittleEndian(r.Slice(0x08));
            int unknown = BinaryPrimitives.ReadInt32LittleEndian(r.Slice(0x0C));
            float fps = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(r.Slice(0x10)));
            byte[] flags = r.Slice(0x14, 16).ToArray();
            int eventCount = BinaryPrimitives.ReadInt32LittleEndian(r.Slice(0x24));

            offset += RecordHeaderSize;

            if (eventCount < 0 || eventCount > 256)
            {
                throw new InvalidDataException(
                    $"animation sequence for body {bodyId}: record {i} has a bad event count ({eventCount}).");
            }

            KrAnimationEvent[] events = new KrAnimationEvent[eventCount];

            for (int e = 0; e < eventCount; e++)
            {
                if (offset + KrAnimationEvent.HeaderSize > data.Length)
                {
                    throw new InvalidDataException(
                        $"animation sequence for body {bodyId}: event {e} of record {i} runs past the end of the file.");
                }

                ReadOnlySpan<byte> ev = s.Slice(offset);

                uint frame = BinaryPrimitives.ReadUInt32LittleEndian(ev);
                uint unknownEvent = BinaryPrimitives.ReadUInt32LittleEndian(ev.Slice(0x04));
                uint rawType = BinaryPrimitives.ReadUInt32LittleEndian(ev.Slice(0x08));
                int size = KrAnimationEvent.SizeOf(rawType);

                if (size < 0)
                {
                    throw new InvalidDataException(
                        $"animation sequence for body {bodyId}: record {i} event {e} has unknown type {rawType}.");
                }

                if (offset + size > data.Length)
                {
                    throw new InvalidDataException(
                        $"animation sequence for body {bodyId}: event {e} of record {i} runs past the end of the file.");
                }

                events[e] = new KrAnimationEvent(
                    frame,
                    unknownEvent,
                    rawType,
                    ev.Slice(KrAnimationEvent.HeaderSize, size - KrAnimationEvent.HeaderSize).ToArray());

                offset += size;
            }

            actions[i] = new KrAnimationAction(i, action, framesPerDirection, fallback, unknown, fps, flags, events);
        }

        return new KrAnimationSequence(bodyId, actions, data[offset..]);
    }

    /// <summary>
    /// The Enhanced Client's layout: the action count at 0x34, records from
    /// 0x38. A record is action, frames per direction, fallback, fps, 16 flag
    /// bytes, 32 unknown bytes, the event count (+0x40), its events, then
    /// a count and that many u32s. Events are KR's sound record with type 17 for KR's 16.
    /// Null when the file does not read this way.
    /// </summary>
    private static KrAnimationSequence? TryParseEnhanced(byte[] data, int bodyId)
    {
        const int EcHeader = 0x38, EcRecord = 0x44, EcSound = 17, EventSize = 32;

        if (data.Length < EcHeader)
        {
            return null;
        }

        ReadOnlySpan<byte> s = data;
        int count = BinaryPrimitives.ReadInt32LittleEndian(s.Slice(0x34));

        if (count <= 0 || count > 4096)
        {
            return null;
        }

        KrAnimationAction[] actions = new KrAnimationAction[count];
        int offset = EcHeader;

        for (int i = 0; i < count; i++)
        {
            if (offset + EcRecord > data.Length)
            {
                return null;
            }

            ReadOnlySpan<byte> r = s.Slice(offset);
            int action = BinaryPrimitives.ReadInt32LittleEndian(r);
            int framesPerDirection = BinaryPrimitives.ReadInt32LittleEndian(r.Slice(0x04));
            int fallback = BinaryPrimitives.ReadInt32LittleEndian(r.Slice(0x08));
            float fps = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(r.Slice(0x0C)));
            byte[] flags = r.Slice(0x10, 16).ToArray();
            int eventCount = BinaryPrimitives.ReadInt32LittleEndian(r.Slice(0x40));
            offset += EcRecord;

            if (eventCount < 0 || eventCount > 256 || offset + eventCount * EventSize > data.Length)
            {
                return null;
            }

            KrAnimationEvent[] events = new KrAnimationEvent[eventCount];

            for (int e = 0; e < eventCount; e++)
            {
                ReadOnlySpan<byte> ev = s.Slice(offset);

                if (BinaryPrimitives.ReadUInt32LittleEndian(ev.Slice(0x08)) != EcSound)
                {
                    return null;
                }

                events[e] = new KrAnimationEvent(
                    BinaryPrimitives.ReadUInt32LittleEndian(ev),
                    BinaryPrimitives.ReadUInt32LittleEndian(ev.Slice(0x04)),
                    (uint)KrAnimationEventType.Sound,
                    ev.Slice(KrAnimationEvent.HeaderSize, EventSize - KrAnimationEvent.HeaderSize).ToArray());
                offset += EventSize;
            }

            // Then a count and that many u32s (0 or 1 seen; walks have 1).
            if (offset + 4 > data.Length)
            {
                return null;
            }

            int extra = BinaryPrimitives.ReadInt32LittleEndian(s.Slice(offset));

            if (extra < 0 || extra > 64 || offset + 4 + (extra * 4) > data.Length)
            {
                return null;
            }

            offset += 4 + (extra * 4);
            actions[i] = new KrAnimationAction(i, action, framesPerDirection, fallback, -1, fps, flags, events);
        }

        return new KrAnimationSequence(bodyId, actions, data[offset..], enhanced: true);
    }

    public bool TryGetAction(int action, out KrAnimationAction result)
    {
        if ((uint)action < (uint)_actions.Length)
        {
            result = _actions[action];

            return true;
        }

        result = null!;

        return false;
    }

    /// <summary>
    /// Resolves an action to one KR actually has art for, following the
    /// fallback chain. Returns -1 when nothing in the chain has art.
    /// </summary>
    public int ResolveAction(int action)
    {
        for (int hops = 0; hops < 8; hops++)
        {
            if (!TryGetAction(action, out KrAnimationAction entry))
            {
                return -1;
            }

            if (entry.HasArt)
            {
                return action;
            }

            if (entry.FallbackAction < 0 || entry.FallbackAction == action)
            {
                return -1;
            }

            action = entry.FallbackAction;
        }

        return -1;
    }
}
