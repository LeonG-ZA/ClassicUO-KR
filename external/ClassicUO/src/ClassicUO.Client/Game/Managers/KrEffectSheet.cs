// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicUO.Assets;
using ClassicUO.Game.Data;
using ClassicUO.Utility;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Game.Managers
{
    /// <summary>
    /// "-krfxsheet [first]": plays each of KrEffectFallback's table entries on
    /// the player, its 2D effect and then the KR particle effect it is taken
    /// for, and saves a few frames of each from the world render target (no
    /// gumps, no lighting) to Data/Client/EffectSheet, with a NNN.json for each,
    /// to compare them side by side. Effects run client-side only.
    /// </summary>
    internal static class KrEffectSheet
    {
        private const int CropSize = 280;
        private const int Frames = 6;
        private const uint SampleMs = 50;
        private const uint MaxMs = 4000;
        private const uint GapMs = 300;

        private enum Phase { Idle, Flat, Gap, Kr }

        private readonly record struct Job(int Graphic, int Speed, int Duration, int Hue, int Render, int Effect, int Layer);

        private static List<Job> _jobs;
        private static int _index;
        private static Phase _phase;
        private static uint _phaseStart, _lastSample;
        private static readonly List<Color[]> _samples = [];
        private static string _folder;
        private static uint _flatMs;
        private static bool _krPlayed;
        private static int _cropW, _cropH;

        public static void Start(World world, string[] args)
        {
            if (args.Length > 1 && args[1] == "stop")
            {
                _jobs = null;
                GameActions.Print(world, "KR fx sheet: stopped.", 0x0035);

                return;
            }

            if (world.Player == null)
            {
                return;
            }

            _jobs = KrEffectFallback.Entries
                .Select(e => new Job(e.Graphic, e.Speed, e.Duration, Math.Max(e.Hue, 0), Math.Max(e.Render, 0), e.Effect, e.Layer))
                .Distinct()
                // Without a KR scene the 2D effect plays in both clients.
                .Where(j => KrEffectProvider.Instance?.Effects.SceneForServerEffect(j.Effect) != null)
                .OrderBy(j => j.Effect).ThenBy(j => j.Graphic)
                .ToList();
            _index = args.Length > 1 && int.TryParse(args[1], out int first) ? Math.Clamp(first, 0, _jobs.Count) : 0;

            // -krfxsheet <first> <count>: only those rows, the rest left as captured.
            if (args.Length > 2 && int.TryParse(args[2], out int count) && _index + count < _jobs.Count)
            {
                _jobs.RemoveRange(_index + Math.Max(count, 0), _jobs.Count - _index - Math.Max(count, 0));
            }
            _folder = FileSystemHelper.CreateFolderIfNotExists(CUOEnviroment.ExecutablePath, "Data", "Client", "EffectSheet");
            _phase = Phase.Idle;
            _phaseStart = Time.Ticks;
            GameActions.Print(world, $"KR fx sheet: {_jobs.Count - _index} effects to {_folder}. Stand still; -krfxsheet stop ends it.", 0x0035);
        }

        /// <summary>Each frame, once the world is drawn into its render target.</summary>
        public static void AfterWorldDraw(World world, GraphicsDevice device, RenderTarget2D target)
        {
            if (_jobs == null || world?.Player == null || target == null)
            {
                return;
            }

            uint now = Time.Ticks;
            uint elapsed = now - _phaseStart;

            switch (_phase)
            {
                case Phase.Idle:
                    if (_index >= _jobs.Count)
                    {
                        GameActions.Print(world, $"KR fx sheet: done, {_jobs.Count} effects.", 0x0035);
                        _jobs = null;

                        return;
                    }

                    if (world.HasEffects || KrParticles.EffectCount > 0)
                    {
                        if (elapsed < MaxMs)
                        {
                            return;
                        }
                    }

                    Job j = _jobs[_index];
                    var p = world.Player;
                    bool attached = j.Layer != 255;

                    world.SpawnEffect(
                        attached ? GraphicEffectType.FixedFrom : GraphicEffectType.FixedXYZ,
                        attached ? p.Serial : 0, 0, (ushort)j.Graphic, (ushort)j.Hue,
                        p.X, p.Y, p.Z, p.X, p.Y, p.Z, (byte)j.Speed, j.Duration, false, false, false,
                        (GraphicEffectBlendMode)j.Render);
                    Begin(Phase.Flat, now);

                    break;

                case Phase.Flat:
                    Sample(device, target, now);

                    if (elapsed > SampleMs && !world.HasEffects || elapsed >= MaxMs)
                    {
                        _flatMs = elapsed;
                        Save(device, "2d");
                        Begin(Phase.Gap, now);
                    }

                    break;

                case Phase.Gap:
                    if (elapsed >= GapMs)
                    {
                        Job k = _jobs[_index];
                        var m = world.Player;
                        _krPlayed = KrParticles.TrySpawn(k.Effect, k.Layer != 255 ? m.Serial : 0, m.X, m.Y, m.Z, (byte)k.Layer, (ushort)k.Graphic, k.Duration);
                        Begin(Phase.Kr, now);
                    }

                    break;

                case Phase.Kr:
                    if (_krPlayed)
                    {
                        Sample(device, target, now);
                    }

                    if (!_krPlayed || elapsed > SampleMs && KrParticles.EffectCount == 0 || elapsed >= MaxMs)
                    {
                        Job d = _jobs[_index];
                        Save(device, "kr");
                        // One file per effect, so a run resumed from an index adds to
                        // the last. By hand: the client's System.Text.Json has no
                        // reflection serializer.
                        string scene = KrEffectProvider.Instance?.Effects.SceneForServerEffect(d.Effect);
                        string sceneJson = scene == null ? "null" : $"\"{scene.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
                        File.WriteAllText(
                            Path.Combine(_folder, $"{_index:D3}.json"),
                            $"{{\"index\": {_index}, \"graphic\": {d.Graphic}, \"speed\": {d.Speed}, \"duration\": {d.Duration}, " +
                            $"\"hue\": {d.Hue}, \"render\": {d.Render}, \"effect\": {d.Effect}, \"layer\": {d.Layer}, " +
                            $"\"scene\": {sceneJson}, \"played\": {(_krPlayed ? "true" : "false")}, \"ms2d\": {_flatMs}, \"msKr\": {elapsed}}}");
                        _index++;
                        Begin(Phase.Idle, now);

                        if (_index % 10 == 0)
                        {
                            GameActions.Print(world, $"KR fx sheet: {_index} of {_jobs.Count}", 0x0035);
                        }
                    }

                    break;
            }
        }

        private static void Begin(Phase phase, uint now)
        {
            _phase = phase;
            _phaseStart = now;
            _lastSample = 0;
            _samples.Clear();
        }

        private static void Sample(GraphicsDevice device, RenderTarget2D target, uint now)
        {
            if (_lastSample != 0 && now - _lastSample < SampleMs)
            {
                return;
            }

            _lastSample = now;
            Rectangle crop = Crop(target);
            (_cropW, _cropH) = (crop.Width, crop.Height);
            Color[] data = new Color[crop.Width * crop.Height];
            target.GetData(0, crop, data, 0, data.Length);

            // The world target's alpha is whatever the blends leave; shown as drawn.
            for (int i = 0; i < data.Length; i++)
            {
                data[i].A = 255;
            }
            _samples.Add(data);
        }

        /// <summary>Round the player: the camera keeps them at the target's centre; effects rise above the feet.</summary>
        private static Rectangle Crop(RenderTarget2D target)
        {
            int w = Math.Min(CropSize, target.Width), h = Math.Min(CropSize, target.Height);
            int x = Math.Clamp(target.Width / 2 - w / 2, 0, target.Width - w);
            int y = Math.Clamp(target.Height / 2 - h / 2 - 30, 0, target.Height - h);

            return new Rectangle(x, y, w, h);
        }

        private static void Save(GraphicsDevice device, string kind)
        {
            for (int i = 0; i < Frames && _samples.Count > 0; i++)
            {
                int at = _samples.Count == 1 ? 0 : i * (_samples.Count - 1) / (Frames - 1);
                Color[] data = _samples[at];
                using Texture2D texture = new(device, _cropW, _cropH, false, SurfaceFormat.Color);
                texture.SetData(data);

                using FileStream stream = File.Create(Path.Combine(_folder, $"{_index:D3}_{kind}_{i}.png"));
                texture.SaveAsPng(stream, _cropW, _cropH);
            }
        }
    }
}
