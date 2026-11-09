using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Renderer.Effects
{
    internal class BasicUOEffect : Effect
    {
        public BasicUOEffect(GraphicsDevice graphicsDevice) : base(graphicsDevice, Resources.GetUOShader().ToArray())
        {
            MatrixTransform = Parameters["MatrixTransform"];
            WorldMatrix = Parameters["WorldMatrix"];
            Viewport = Parameters["Viewport"];
            Brighlight = Parameters["Brightlight"];
            CircleOfTransparencyRadius = Parameters["CircleOfTransparencyRadius"];
            WaterTime = Parameters["WaterTime"];
            WaterOrigin = Parameters["WaterOrigin"];
            WaterTextured = Parameters["WaterTextured"];
            KrTerrainTable = Parameters["KrTerrainTable"];
            KrGrunge = Parameters["KrGrunge"];
            KrTerrainHue = Parameters["KrTerrainHue"];

            CurrentTechnique = Techniques["HueTechnique"];
            Pass = CurrentTechnique.Passes[0];
        }

        public EffectParameter MatrixTransform { get; }
        public EffectParameter WorldMatrix { get; }
        public EffectParameter Viewport { get; }
        public EffectParameter Brighlight { get; }
        public EffectParameter CircleOfTransparencyRadius { get; }
        public EffectParameter WaterTime { get; }
        public EffectParameter WaterOrigin { get; }
        public EffectParameter WaterTextured { get; }
        public EffectParameter KrTerrainTable { get; }
        public EffectParameter KrGrunge { get; }
        public EffectParameter KrTerrainHue { get; }
        public EffectPass Pass { get; }
    }
}
