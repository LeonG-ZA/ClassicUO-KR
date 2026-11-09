using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StbRectPackSharp;
using System;
using System.Collections.Generic;

namespace ClassicUO.Renderer
{
    public class TextureAtlas : IDisposable
    {
        private readonly int _width,
            _height;
        private readonly SurfaceFormat _format;
        private readonly GraphicsDevice _device;
        private readonly List<Texture2D> _textureList;
        private Packer _packer;

        public TextureAtlas(GraphicsDevice device, int width, int height, SurfaceFormat format)
        {
            _device = device;
            _width = width;
            _height = height;
            _format = format;

            _textureList = new List<Texture2D>();
        }

        public int TexturesCount => _textureList.Count;

        /// <param name="padding">
        /// Width of a transparent gutter reserved around the sprite, in
        /// pixels. Sprites sampled with anything other than point filtering
        /// need one, or the filter reaches past the sprite's edge and picks up
        /// whichever neighbour the packer happened to put there. The returned
        /// rectangle is the sprite itself, never the gutter.
        /// </param>
        /// <param name="premultiplyAlpha">
        /// Multiply each pixel's colour by its own alpha on the way in. The
        /// client's blend state is the premultiplied one, and filtering only
        /// composes correctly over premultiplied colour, so anti-aliased art
        /// (which is all of KR's) needs this. Art with binary alpha - all of
        /// the 2D art - is unaffected either way and does not ask for it.
        /// </param>
        public unsafe Texture2D AddSprite(
            ReadOnlySpan<uint> pixels,
            int width,
            int height,
            out Rectangle pr,
            int padding = 0,
            bool premultiplyAlpha = false
        )
        {
            var index = _textureList.Count - 1;

            if (index < 0)
            {
                index = 0;
                CreateNewTexture2D();
            }

            int paddedWidth = width + padding * 2;
            int paddedHeight = height + padding * 2;

            while (!_packer.PackRect(paddedWidth, paddedHeight, out pr))
            {
                CreateNewTexture2D();
                index = _textureList.Count - 1;
            }

            Texture2D texture = _textureList[index];

            if (padding == 0 && !premultiplyAlpha)
            {
                fixed (uint* src = pixels)
                {
                    texture.SetDataPointerEXT(0, pr, (IntPtr)src, sizeof(uint) * width * height);
                }
            }
            else
            {
                // The gutter is transparent black, which is the identity for a
                // premultiplied filter: an edge texel fades out towards it
                // instead of picking up whatever the packer put next door.
                int count = paddedWidth * paddedHeight;
                uint[] buffer = System.Buffers.ArrayPool<uint>.Shared.Rent(count);

                try
                {
                    Array.Clear(buffer, 0, count);

                    for (int y = 0; y < height; y++)
                    {
                        var row = buffer.AsSpan((y + padding) * paddedWidth + padding, width);
                        pixels.Slice(y * width, width).CopyTo(row);

                        if (premultiplyAlpha)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                uint p = row[x];
                                uint a = p >> 24;

                                if (a == 255)
                                {
                                    continue;
                                }

                                if (a == 0)
                                {
                                    row[x] = 0;
                                    continue;
                                }

                                uint b = ((p >> 16) & 0xFF) * a / 255;
                                uint g = ((p >> 8) & 0xFF) * a / 255;
                                uint r = (p & 0xFF) * a / 255;

                                row[x] = (a << 24) | (b << 16) | (g << 8) | r;
                            }
                        }
                    }

                    fixed (uint* src = buffer)
                    {
                        texture.SetDataPointerEXT(0, pr, (IntPtr)src, sizeof(uint) * count);
                    }
                }
                finally
                {
                    System.Buffers.ArrayPool<uint>.Shared.Return(buffer);
                }

                pr = new Rectangle(pr.X + padding, pr.Y + padding, width, height);
            }

            return texture;
        }

        private void CreateNewTexture2D()
        {
            Utility.Logging.Log.Trace($"creating texture: {_width}x{_height} {_format}");
            Texture2D texture = new Texture2D(_device, _width, _height, false, _format);
            _textureList.Add(texture);

            _packer?.Dispose();
            _packer = new Packer(_width, _height);
        }

        public void SaveImages(string name)
        {
            for (int i = 0, count = TexturesCount; i < count; ++i)
            {
                var texture = _textureList[i];

                using (var stream = System.IO.File.Create($"atlas/{name}_atlas_{i}.png"))
                {
                    texture.SaveAsPng(stream, texture.Width, texture.Height);
                }
            }
        }

        public void Dispose()
        {
            foreach (Texture2D texture in _textureList)
            {
                if (!texture.IsDisposed)
                {
                    texture.Dispose();
                }
            }

            _packer.Dispose();
            _textureList.Clear();
        }
    }
}
