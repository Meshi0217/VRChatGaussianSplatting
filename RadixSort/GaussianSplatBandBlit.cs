using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting
{
    /// <summary>
    /// Rasterizes a material pass (or a black clear) over only the texel rows/columns that hold live
    /// data, instead of a fullscreen Graphics.Blit over the whole bucket-sized target. The combined
    /// textures and the render-order texture are sized for the worst-case bucket while a frame's
    /// live region is usually far smaller, and every consumer culls out-of-range ids BEFORE reading,
    /// so texels outside the band are never read and need neither clearing nor writing.
    /// </summary>
    public static class GaussianSplatBandBlit
    {
        // CI/debug escape hatch: forces the legacy fullscreen path.
        public static bool ForceFullscreen;

        static Material _clearMaterial;

        // Texel rows covering block-layout indices [0, count): 16 ids per 4x4 block, block rows
        // fill in index order, so the live region is the first ceil(blocks / blocksPerRow) * 4 rows.
        public static int BlockLayoutRows(int count, int width, int height)
        {
            int blocksPerRow = Mathf.Max(1, width >> 2);
            int blockRows = (Mathf.Max(0, count) + 16 * blocksPerRow - 1) / (16 * blocksPerRow);
            return Mathf.Clamp(blockRows * 4, 0, height);
        }

        public static void Blit(RenderTexture target, Material material, int pass, int rows)
        {
            Draw(target, material, pass, rows, target.width);
        }

        // Rect variant for the Morton-layout render-order texture, where indices [0, NextPOT(count))
        // occupy a power-of-two rectangle at the origin.
        public static void Blit(RenderTexture target, Material material, int pass, int rows, int columns)
        {
            Draw(target, material, pass, rows, columns);
        }

        // Reproduces the legacy Graphics.Blit(Texture2D.blackTexture, target) clear on the band only.
        public static void Clear(RenderTexture target, int rows)
        {
            if (ForceFullscreen || rows >= target.height)
            {
                Graphics.Blit(Texture2D.blackTexture, target);
                return;
            }
            if (rows <= 0)
            {
                return;
            }
            // Hidden/BlitCopy is what Graphics.Blit itself uses, so it is present in every build;
            // sampling the black texture writes the identical (0,0,0,0) the legacy clear wrote.
            if (_clearMaterial == null)
            {
                Shader shader = Shader.Find("Hidden/BlitCopy");
                if (shader == null)
                {
                    Graphics.Blit(Texture2D.blackTexture, target);
                    return;
                }
                _clearMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            _clearMaterial.mainTexture = Texture2D.blackTexture;
            Draw(target, _clearMaterial, 0, rows, target.width);
        }

        static void Draw(RenderTexture target, Material material, int pass, int rows, int columns)
        {
            int height = target.height;
            if (ForceFullscreen || (rows >= height && columns >= target.width))
            {
                Graphics.Blit(null, target, material, pass);
                return;
            }
            if (rows <= 0 || columns <= 0)
            {
                return;
            }
            float v = Mathf.Min(1.0f, (float)rows / height);
            float u = Mathf.Min(1.0f, (float)columns / target.width);
            RenderTexture previous = RenderTexture.active;
            Graphics.SetRenderTarget(target);
            GL.PushMatrix();
            GL.LoadOrtho();
            if (material.SetPass(pass))
            {
                // Ortho (0,0) maps to texel row 0 (the CI band checks pin this orientation); the
                // quad covers texel rows [0, rows) x columns [0, columns) and nothing else.
                GL.Begin(GL.QUADS);
                GL.TexCoord2(0f, 0f); GL.Vertex3(0f, 0f, 0f);
                GL.TexCoord2(u, 0f); GL.Vertex3(u, 0f, 0f);
                GL.TexCoord2(u, v); GL.Vertex3(u, v, 0f);
                GL.TexCoord2(0f, v); GL.Vertex3(0f, v, 0f);
                GL.End();
            }
            GL.PopMatrix();
            RenderTexture.active = previous;
        }
    }
}
