// SPDX-License-Identifier: BSD-2-Clause
//
// Frame spike recorder, while the KR renderer's stutter is chased: times the
// main sections of each frame and, when one runs past SpikeMs, appends the
// breakdown to frame-spikes.txt beside the client. The file is written off
// the game thread, so recording a spike does not cause one.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace ClassicUO.Game.Managers
{
    internal static class FrameProbe
    {
        public const double SpikeMs = 25.0;

        public enum Section
        {
            Update,
            Fill,
            MeshBuild,
            DrawLists,
            Particles,
            Lights,
            MeshLand,
            MeshStatic,
            MeshMulti,
            MeshUpload,
            GpuDecide,
            CpuResolve,
            Count
        }

        private static readonly Stopwatch _clock = Stopwatch.StartNew();
        private static readonly double[] _ms = new double[(int)Section.Count];
        private static readonly ConcurrentQueue<string> _pending = new();
        private static double _frameStart = -1;
        private static int _meshBuilds;
        private static int _gen0, _gen1, _gen2;
        private static int _frames;
        private static bool _writing;

        public static double Now => _clock.Elapsed.TotalMilliseconds;

        public static void Add(Section s, double startMs) => _ms[(int)s] += Now - startMs;

        public static void MeshBuilt() => _meshBuilds++;

        private static int _gpuTiles, _cpuTiles;

        public static void LandTile(bool gpu)
        {
            if (gpu)
            {
                _gpuTiles++;
            }
            else
            {
                _cpuTiles++;
            }
        }

        /// <summary>Called once a frame, at the start of Update: closes the previous frame.</summary>
        public static void FrameBoundary()
        {
            double now = Now;
            _frames++;

            if (_frameStart >= 0)
            {
                double total = now - _frameStart;
                int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);

                if (total > SpikeMs && _frames > 120)
                {
                    StringBuilder line = new();
                    line.Append($"{DateTime.Now:HH:mm:ss.fff} frame {total,6:0.0} ms |");

                    for (int i = 0; i < (int)Section.Count; i++)
                    {
                        line.Append($" {(Section)i} {_ms[i]:0.0}");
                    }

                    double known = 0;

                    foreach (double v in _ms)
                    {
                        known += v;
                    }

                    line.Append($" | other {total - known:0.0} | meshes {_meshBuilds} | land gpu {_gpuTiles} cpu {_cpuTiles} | GC {g0 - _gen0}/{g1 - _gen1}/{g2 - _gen2}");
                    _pending.Enqueue(line.ToString());
                    Flush();
                }

                _gen0 = g0;
                _gen1 = g1;
                _gen2 = g2;
            }

            Array.Clear(_ms);
            _meshBuilds = 0;
            _gpuTiles = _cpuTiles = 0;
            _frameStart = now;
        }

        private static void Flush()
        {
            if (_writing)
            {
                return;
            }

            _writing = true;

            Task.Run(() =>
            {
                try
                {
                    StringBuilder text = new();

                    while (_pending.TryDequeue(out string l))
                    {
                        text.AppendLine(l);
                    }

                    File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "frame-spikes.txt"), text.ToString());
                }
                catch
                {
                    // Diagnostics only.
                }
                finally
                {
                    _writing = false;
                }
            });
        }
    }
}
