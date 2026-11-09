// SPDX-License-Identifier: BSD-2-Clause
//
// Runs a parsed .ems particle script. This is a reading of the script keys, each a *hypothesis*
// judged against the KR client:
//
//   MinEmitTime     how long the sequence emits, seconds
//   emitrate        particles per second while emitting
//   burst           particles emitted at once when the sequence starts
//   numparticles    cap on live particles
//   lifetime        seconds a particle lives
//   chaosLocation   spawn position, units from the effect origin (z up)
//   initial velocity, gravity   units per second, per second squared
//   initial thrust  times the initial velocity
//   ScaleMultiplier times the scale channel = sprite size in units
//   scale / color / rotation    keyframed over a particle's age; "fade so at
//                   T" is T seconds into its life, "final" is its death
//   source/destBlendMode        ALPHA_ONE + ALPHA_ONE is additive
//   CelAnimate      CelSetup(frames, u, v, columns, rows, fps, loop)
//
// One unit is taken as one tile edge.

using System;
using System.Collections.Generic;
using System.Numerics;

namespace ClassicUO.KR.Effects;

/// <summary>How a particle combines with what is behind it.</summary>
public enum KrBlend
{
    /// <summary>Source alpha over the background.</summary>
    Alpha,

    /// <summary>Source added to the background (ONE, ONE): glows, black is clear.</summary>
    Additive,

    /// <summary>Background times source (ZERO, SRCCOLOR): darkens, white is clear.</summary>
    Multiply,

    /// <summary>
    /// Source times what the background lacks (INVDESTCOLOR, with the
    /// background kept: DESTALPHA, 1 here): a screen, lightening only. The
    /// fountain's water sheet (00005950_Fountain, alpha flags 4363).
    /// </summary>
    Screen,
}

/// <summary>A particle ready to draw.</summary>
public readonly record struct EmsSprite(
    string Texture, Vector3 Position, float Size, float RotationDegrees, Vector4 Color,
    KrBlend Blend, int Frame, int Columns, int Rows, Vector2 Origin = default, bool HasOrigin = false,
    Vector3 Scene = default, float Length = 0f, Vector3 Emitter = default, string? Glow = null)
{
    // Glow: a texture added over this one (a NIF material's glow map), drawn
    // additively at the particle's alpha, untinted.

    // Emitter: where a NIF particle's emitter stood in its scene when it was
    // born; Scene - Emitter is the particle's own travel from it.

    // Length: an Axial script particle's extent along its axis (straight up),
    // with Size its width; 0 for a square sprite. torchflame.ems is Axial:
    // KR stands its sideways 03003019_torch.tga up along the length.

    // Scene: a NIF particle's position in its scene's own space, for a caller
    // that places the scene itself (a static's scene, through KR's camera).

    // Origin: for a world-space particle, where the effect stood in the
    // world (pixels) when it was born; Position is then relative to that
    // rather than to where the effect is now. That is what leaves a trail
    // behind a moving wisp.

    public bool Additive => Blend == KrBlend.Additive;
}

public sealed class EmsEffect : IKrEffect
{
    /// <summary>Unused: script particles follow their effect.</summary>
    public Vector2 Origin { get; set; }

    private readonly List<Emitter> _emitters = [];

    public EmsEffect(EmsScript script, int seed)
    {
        Random rng = new(seed);

        foreach (EmsSequence s in script.Sequences)
        {
            _emitters.Add(new Emitter(s, new Random(rng.Next()), script.SystemEmitTime));
        }
    }

    public float Age { get; private set; }

    /// <summary>True once every sequence has stopped emitting and its particles have died.</summary>
    public bool Finished
    {
        get
        {
            foreach (Emitter e in _emitters)
            {
                if (!e.Finished)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Emit for ever rather than for each sequence's MinEmitTime: a static's
    /// script (torchflame.ems: flames 1.5 s, the system 8 s) burns steadily in
    /// KR; played once and restarted, ours flickered for 1.5 s in every 8.
    /// </summary>
    public bool Continuous { get; set; }

    /// <summary>Shrinks or grows the whole effect about its origin (a static's flame size).</summary>
    public float SizeMultiplier { get; set; } = 1f;

    /// <summary>
    /// Gives each emitting sequence its first particle now rather than after
    /// one emit interval: arrow.ems emits once a second, so an arrow's flight,
    /// well under a second, showed nothing.
    /// </summary>
    public void Prime()
    {
        foreach (Emitter e in _emitters)
        {
            e.Prime();
        }
    }

    public void Update(float seconds)
    {
        Age += seconds;

        foreach (Emitter e in _emitters)
        {
            e.Update(seconds, Continuous);
        }
    }

    public IEnumerable<EmsSprite> Sprites()
    {
        foreach (Emitter e in _emitters)
        {
            foreach (EmsSprite s in e.Sprites())
            {
                yield return SizeMultiplier == 1f
                    ? s
                    : s with
                    {
                        Position = s.Position * SizeMultiplier,
                        Size = s.Size * SizeMultiplier,
                        Length = s.Length * SizeMultiplier,
                    };
            }
        }
    }

    private sealed class Particle
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Age;
        public float Life;
        public (float T, float V)[] Scale = [];
        public (float T, Vector4 V)[] Color = [];
        public (float T, float V)[] Rotation = [];
        public (float T, float V)[] Length = [];
    }

    private sealed class Emitter
    {
        private readonly EmsSequence _s;
        private readonly Random _rng;
        private readonly bool _axial;
        private readonly List<Particle> _live = [];
        private readonly float _emitFor;
        private readonly float _rate;
        private readonly int _cap;
        private readonly Vector3 _gravity;
        private readonly float _multiplier;
        private readonly string _texture;
        private readonly KrBlend _blend;
        private readonly int _frames, _columns, _rows;
        private readonly float _fps;
        private readonly bool _loop;
        private float _age;
        private int _emitted;

        public Emitter(EmsSequence s, Random rng, float? systemEmitTime = null)
        {
            _s = s;
            _rng = rng;
            // A sequence that would emit longer than the script's leading
            // MinEmitTime emits only its burst: lightning.ems's sky flash
            // (MinEmitTime 10, burst 5, 5 a second) showed in KR's first
            // three frames of a cast and never after.
            float emitFor = s.Number("minemittime", rng, 0.5f);
            _emitFor = systemEmitTime is { } system && emitFor > system ? 0f : emitFor;
            _axial = s.Text("type")?.Trim().Equals("Axial", StringComparison.OrdinalIgnoreCase) == true;
            _rate = s.Number("emitrate", rng, 0f);
            _cap = (int)s.Number("numparticles", rng, 500f);
            _gravity = s.Get("gravity")?.Vector(rng) ?? Vector3.Zero;
            _multiplier = s.Number("scalemultiplier", rng, 1f);
            _texture = s.Text("texture") ?? "";
            _blend = string.Equals(s.Text("destblendmode"), "ALPHA_ONE", StringComparison.OrdinalIgnoreCase)
                ? KrBlend.Additive
                : string.Equals(s.Text("destblendmode"), "ALPHA_SRCCOLOR", StringComparison.OrdinalIgnoreCase)
                    ? KrBlend.Multiply
                    : KrBlend.Alpha;

            _columns = _rows = 1;
            _frames = 1;

            if (s.Get("celanimate") is EmsCall cel)
            {
                float[] a = cel.Numbers(rng);

                if (a.Length >= 7)
                {
                    _frames = Math.Max(1, (int)a[0]);
                    _columns = Math.Max(1, (int)a[3]);
                    _rows = Math.Max(1, (int)a[4]);
                    _fps = a[5];
                    _loop = a[6] != 0;
                }
            }

            int burst = (int)MathF.Round(s.Number("burst", rng, 0f));

            for (int i = 0; i < burst; i++)
            {
                Spawn();
            }
        }

        public bool Finished => _age >= _emitFor && _live.Count == 0;

        /// <summary>One particle now, not counted, so the regular emission keeps its own timing.</summary>
        public void Prime()
        {
            if (_rate > 0f && _live.Count == 0)
            {
                Spawn();
            }
        }

        public void Update(float dt, bool continuous = false)
        {
            _age += dt;

            // From the total time, not by summing fractions, so 30 steps of
            // 1/60 s emit exactly what 0.5 s should.
            int due = (int)((_rate * (continuous ? _age : Math.Min(_age, _emitFor))) + 1e-3f);

            while (_emitted < due)
            {
                _emitted++;
                Spawn();
            }

            for (int i = _live.Count - 1; i >= 0; i--)
            {
                Particle p = _live[i];
                p.Age += dt;

                if (p.Age >= p.Life)
                {
                    _live.RemoveAt(i);

                    continue;
                }

                p.Velocity += _gravity * dt;
                p.Position += p.Velocity * dt;
            }
        }

        private void Spawn()
        {
            if (_live.Count >= _cap)
            {
                return;
            }

            Particle p = new()
            {
                Life = Math.Max(0.01f, _s.Number("lifetime", _rng, 1f)),
                Position = _s.Get("chaoslocation")?.Vector(_rng) ?? Vector3.Zero,
                Velocity = _s.Get("initial velocity")?.Vector(_rng) ?? Vector3.Zero,
            };

            // Thrust: *hypothesis* a multiplier on the initial velocity.
            // Invisibility's bubbles (velocity (0, -0.04, -0.4), thrust 10)
            // fall about 4.9 units a second in KR; at -0.4 alone they barely
            // moved, and as an acceleration of 10 they fell half as far.
            float thrust = _s.Number("initial thrust", _rng, 1f);
            p.Velocity *= thrust;

            p.Scale = Keys(p.Life, "scale", e => e.Number(_rng), 1f);
            p.Color = Keys(p.Life, "color", e => e.Color(_rng), Vector4.One);
            p.Rotation = Keys(p.Life, "rotation", e => e.Vector(_rng).Y, 0f);

            if (_axial)
            {
                p.Length = Keys(p.Life, "length", e => e.Number(_rng), 1f);
            }

            _live.Add(p);
        }

        /// <summary>A channel's keyframes for one particle, drawn once and sorted by time.</summary>
        private (float T, TV V)[] Keys<TV>(float life, string channel, Func<EmsExpr, TV> value, TV fallback)
        {
            if (!_s.Channels.TryGetValue(channel, out List<EmsKey>? keys) || keys.Count == 0)
            {
                return [(0f, fallback)];
            }

            List<(float, TV)> result = [];

            foreach (EmsKey k in keys)
            {
                float t = k.Final ? life : k.At is null ? 0f : Math.Min(life, k.At.Number(_rng));
                result.Add((t, value(k.Value)));
            }

            result.Sort((a, b) => a.Item1.CompareTo(b.Item1));

            return result.ToArray();
        }

        private static float At((float T, float V)[] keys, float t)
        {
            if (t <= keys[0].T)
            {
                return keys[0].V;
            }

            for (int i = 1; i < keys.Length; i++)
            {
                if (t <= keys[i].T)
                {
                    float span = keys[i].T - keys[i - 1].T;
                    float f = span <= 0f ? 1f : (t - keys[i - 1].T) / span;

                    return keys[i - 1].V + ((keys[i].V - keys[i - 1].V) * f);
                }
            }

            return keys[^1].V;
        }

        private static Vector4 At((float T, Vector4 V)[] keys, float t)
        {
            if (t <= keys[0].T)
            {
                return keys[0].V;
            }

            for (int i = 1; i < keys.Length; i++)
            {
                if (t <= keys[i].T)
                {
                    float span = keys[i].T - keys[i - 1].T;
                    float f = span <= 0f ? 1f : (t - keys[i - 1].T) / span;

                    return Vector4.Lerp(keys[i - 1].V, keys[i].V, f);
                }
            }

            return keys[^1].V;
        }

        public IEnumerable<EmsSprite> Sprites()
        {
            foreach (Particle p in _live)
            {
                int frame = 0;

                if (_frames > 1 && _fps > 0f)
                {
                    int f = (int)(p.Age * _fps);
                    frame = _loop ? f % _frames : Math.Min(f, _frames - 1);
                }

                yield return new EmsSprite(
                    _texture, p.Position, At(p.Scale, p.Age) * _multiplier, At(p.Rotation, p.Age),
                    At(p.Color, p.Age), _blend, frame, _columns, _rows,
                    Length: _axial ? At(p.Length, p.Age) * _multiplier : 0f);
            }
        }
    }
}
