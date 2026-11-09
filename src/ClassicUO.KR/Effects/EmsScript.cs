// SPDX-License-Identifier: BSD-2-Clause
//
// KR's text particle scripts (.ems) from EffectTexture.uop. A script is one
// particle system of named event sequences, each a block of "key = value"
// lines:
//
//   particlesystem "Generic" 1
//   {
//   MinEmitTime = 0.5                     (applies to the sequence after it)
//       eventsequence "TOP FLITS"
//    {
//         texture = "03000005_fire.tga"
//         emitrate = 10
//         lifetime = 2                     // comments run to end of line
//         initial velocity = XYZ(random(-2,2),random(-1,1),random(1,2))
//         initial scale = 0.0
//       fade so at 0.50 scale = 0          (keyframe at a particle age, seconds)
//        fade so final scale = random(0.0,0.1)
//      }
//   }
//
// Values are numbers, strings, bare words (ALPHA_ONE, Point, true) or calls:
// random(a,b), XYZ(x,y,z), RGBA(r,g,b,a), CelSetup(...). Keys are matched
// case-insensitively with runs of spaces collapsed. Across the 44 scripts in
// the install there are 30 keys; the parser keeps them all and leaves their
// meaning to the simulator.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace ClassicUO.KR.Effects;

/// <summary>A value expression, evaluated afresh (random draws) each time.</summary>
public abstract record EmsExpr
{
    public abstract float Number(Random rng);

    public virtual Vector3 Vector(Random rng)
    {
        float v = Number(rng);

        return new Vector3(v, v, v);
    }

    public virtual Vector4 Color(Random rng)
    {
        float v = Number(rng);

        return new Vector4(v, v, v, v);
    }

    public virtual string? Text => null;
}

public sealed record EmsNumber(float Value) : EmsExpr
{
    public override float Number(Random rng) => Value;
}

public sealed record EmsWord(string Value) : EmsExpr
{
    public override float Number(Random rng) => 0f;

    public override string Text => Value;
}

public sealed record EmsCall(string Name, IReadOnlyList<EmsExpr> Args) : EmsExpr
{
    public override float Number(Random rng)
    {
        if (Name.Equals("random", StringComparison.OrdinalIgnoreCase) && Args.Count >= 2)
        {
            float a = Args[0].Number(rng);
            float b = Args[1].Number(rng);

            return a + ((b - a) * (float)rng.NextDouble());
        }

        return Args.Count > 0 ? Args[0].Number(rng) : 0f;
    }

    public override Vector3 Vector(Random rng) =>
        Name.Equals("XYZ", StringComparison.OrdinalIgnoreCase) && Args.Count >= 3
            ? new Vector3(Args[0].Number(rng), Args[1].Number(rng), Args[2].Number(rng))
            : base.Vector(rng);

    public override Vector4 Color(Random rng) =>
        Name.Equals("RGBA", StringComparison.OrdinalIgnoreCase) && Args.Count >= 4
            ? new Vector4(Args[0].Number(rng), Args[1].Number(rng), Args[2].Number(rng), Args[3].Number(rng))
            : base.Color(rng);

    /// <summary>The arguments as plain numbers (for CelSetup).</summary>
    public float[] Numbers(Random rng)
    {
        float[] result = new float[Args.Count];

        for (int i = 0; i < result.Length; i++)
        {
            result[i] = Args[i].Number(rng);
        }

        return result;
    }
}

/// <summary>One keyframe of a channel: at a particle age (null = initial, +inf = final).</summary>
public sealed record EmsKey(EmsExpr? At, bool Final, EmsExpr Value);

public sealed class EmsSequence
{
    public EmsSequence(string name) => Name = name;

    public string Name { get; }

    /// <summary>Plain properties by normalised key (lower case, single spaces).</summary>
    public Dictionary<string, EmsExpr> Properties { get; } = new(StringComparer.Ordinal);

    /// <summary>Keyframed channels - scale, color, rotation, length - in file order.</summary>
    public Dictionary<string, List<EmsKey>> Channels { get; } = new(StringComparer.Ordinal);

    public EmsExpr? Get(string key) => Properties.TryGetValue(key, out EmsExpr? v) ? v : null;

    public float Number(string key, Random rng, float fallback) => Get(key)?.Number(rng) ?? fallback;

    public string? Text(string key) => Get(key)?.Text;
}

public sealed class EmsScript
{
    public string Name { get; private set; } = "";

    public List<EmsSequence> Sequences { get; } = [];

    /// <summary>
    /// A MinEmitTime before the first sequence, or null. *Hypothesis:* a
    /// sequence whose own MinEmitTime is longer emits only its burst - KR casting Lightning showed lightning.ems's sky flash (its
    /// own MinEmitTime 10, burst 5) only in the first three frames, under
    /// the script's leading "minEmitTime = 1".
    /// </summary>
    public float? SystemEmitTime { get; private set; }

    private static readonly string[] Channels = ["scale", "color", "rotation", "length"];

    public static EmsScript Parse(string text)
    {
        EmsScript script = new();
        EmsSequence? current = null;
        Dictionary<string, EmsExpr> pending = new(StringComparer.Ordinal);

        foreach (string raw in text.Split('\n'))
        {
            string line = raw;
            int comment = line.IndexOf("//", StringComparison.Ordinal);

            if (comment >= 0)
            {
                line = line[..comment];
            }

            line = line.Trim();

            if (line.Length == 0 || line == "{")
            {
                continue;
            }

            if (line == "}")
            {
                current = null;

                continue;
            }

            string lower = line.ToLowerInvariant();

            if (lower.StartsWith("particlesystem", StringComparison.Ordinal))
            {
                script.Name = Quoted(line);

                continue;
            }

            if (lower.StartsWith("eventsequence", StringComparison.Ordinal))
            {
                current = new EmsSequence(Quoted(line));

                foreach ((string k, EmsExpr v) in pending)
                {
                    current.Properties[k] = v;

                    if (script.Sequences.Count == 0 && k == "minemittime" && v is EmsNumber n)
                    {
                        script.SystemEmitTime = n.Value;
                    }
                }

                pending.Clear();
                script.Sequences.Add(current);

                continue;
            }

            int eq = FindAssignment(line);

            if (eq < 0)
            {
                continue;
            }

            string key = Normalise(line[..eq]);
            EmsExpr value = ParseExpr(line[(eq + 1)..]);

            if (current is null)
            {
                pending[key] = value;

                continue;
            }

            if (!TryChannel(key, value, current))
            {
                current.Properties[key] = value;
            }
        }

        return script;
    }

    /// <summary>"initial X", "fade so final X", "fade so at T X" for a known channel X.</summary>
    private static bool TryChannel(string key, EmsExpr value, EmsSequence sequence)
    {
        foreach (string channel in Channels)
        {
            if (!key.EndsWith(" " + channel, StringComparison.Ordinal))
            {
                continue;
            }

            string head = key[..^(channel.Length + 1)];
            EmsKey? k = null;

            if (head == "initial")
            {
                k = new EmsKey(null, false, value);
            }
            else if (head == "fade so final")
            {
                k = new EmsKey(null, true, value);
            }
            else if (head.StartsWith("fade so at ", StringComparison.Ordinal))
            {
                k = new EmsKey(ParseExpr(head["fade so at ".Length..]), false, value);
            }

            if (k is null)
            {
                return false;
            }

            if (!sequence.Channels.TryGetValue(channel, out List<EmsKey>? list))
            {
                sequence.Channels[channel] = list = [];
            }

            list.Add(k);

            return true;
        }

        return false;
    }

    /// <summary>The '=' that separates key from value - not one inside parentheses.</summary>
    private static int FindAssignment(string line)
    {
        int depth = 0;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
            }
            else if (c == '=' && depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static string Normalise(string key)
    {
        StringBuilder sb = new();
        bool space = false;

        foreach (char c in key.Trim().ToLowerInvariant())
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;

                continue;
            }

            if (space && sb.Length > 0)
            {
                sb.Append(' ');
            }

            space = false;
            sb.Append(c);
        }

        return sb.ToString();
    }

    private static string Quoted(string line)
    {
        int a = line.IndexOf('"');
        int b = a >= 0 ? line.IndexOf('"', a + 1) : -1;

        return a >= 0 && b > a ? line[(a + 1)..b] : line;
    }

    public static EmsExpr ParseExpr(string text)
    {
        int pos = 0;

        return ParseExpr(text, ref pos);
    }

    private static EmsExpr ParseExpr(string s, ref int pos)
    {
        SkipSpace(s, ref pos);

        if (pos >= s.Length)
        {
            return new EmsNumber(0f);
        }

        char c = s[pos];

        if (c == '"')
        {
            int end = s.IndexOf('"', pos + 1);
            end = end < 0 ? s.Length : end;
            string str = s[(pos + 1)..end];
            pos = Math.Min(s.Length, end + 1);

            return new EmsWord(str);
        }

        if (char.IsDigit(c) || c == '-' || c == '+' || c == '.')
        {
            int start = pos++;

            while (pos < s.Length && (char.IsDigit(s[pos]) || s[pos] == '.' || s[pos] == 'e' || s[pos] == 'E'))
            {
                pos++;
            }

            return float.TryParse(s[start..pos], NumberStyles.Float, CultureInfo.InvariantCulture, out float f)
                ? new EmsNumber(f)
                : new EmsNumber(0f);
        }

        int wordStart = pos;

        while (pos < s.Length && (char.IsLetterOrDigit(s[pos]) || s[pos] == '_'))
        {
            pos++;
        }

        if (pos == wordStart)
        {
            pos++;

            return new EmsNumber(0f);
        }

        string word = s[wordStart..pos];
        SkipSpace(s, ref pos);

        if (pos < s.Length && s[pos] == '(')
        {
            pos++;
            List<EmsExpr> args = [];

            while (true)
            {
                SkipSpace(s, ref pos);

                // Tolerates a missing ')', which one script has.
                if (pos >= s.Length)
                {
                    break;
                }

                if (s[pos] == ')')
                {
                    pos++;

                    break;
                }

                args.Add(ParseExpr(s, ref pos));
                SkipSpace(s, ref pos);

                if (pos < s.Length && s[pos] == ',')
                {
                    pos++;
                }
            }

            return new EmsCall(word, args);
        }

        return new EmsWord(word);
    }

    private static void SkipSpace(string s, ref int pos)
    {
        while (pos < s.Length && char.IsWhiteSpace(s[pos]))
        {
            pos++;
        }
    }
}
