using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using SkiaSharp;
using Vintagestory.API.MathTools;

namespace UniversalDisplayLib
{
    /// <summary>
    /// FROST, STORM GRIME, SNOW AND RAIN PICTURES, ALL DRAWN HERE IN CODE. Frost, grime and snow are made for each
    /// pane: one fixed 32x32 picture stretched over a pane squashes its pixels on a small pane and blows them up on a
    /// big one; these are drawn for the pane's own size - TexelsPerBlock a block - so the edges sit at the pane's edges
    /// and every pane has the same pixels. TexelsPerBlock is 32, the game's own pixel size, half a voxel. The SHAPES
    /// are worked out on a 64-a-block grid (Fine), where their sizes were tuned, and brought down.
    ///
    /// FROST creeps in from the edges and corners as milky ice, sixteen steps; SNOW is packed along the bottom and
    /// banked into the corners with flakes higher up, growing in over sixteen steps like the frost. Each pane size +
    /// pattern draws its randomness once (seeded, so every player sees the same) and every step uses it, so step n+1
    /// grows out of step n. Pixels as BGRA ints (ColorUtil.ToRgba(a, r, g, b)).
    /// </summary>
    public static partial class PaneArt
    {
        public const int TexelsPerBlock = 32;
        /// <summary>The shapes' grid: Fine x Fine of it to a picture pixel (64 a block, where the sizes were tuned).</summary>
        const int Fine = 2;
        const int FineTexelsPerBlock = TexelsPerBlock * Fine;
        public const int FrostSteps = 16;
        public const int SnowSteps = 16;
        const double Scale = 2.0;          // fine-grid texels per texel of the old 32-a-pane pictures
        const int MaxSize = 256;

        /// <summary>The picture size, in texels, for a pane this many blocks across and up.</summary>
        public static (int w, int h) SizeFor(float widthBlocks, float heightBlocks) =>
            (Math.Clamp((int)Math.Round(widthBlocks * TexelsPerBlock), 1, MaxSize), Math.Clamp((int)Math.Round(heightBlocks * TexelsPerBlock), 1, MaxSize));

        static double Lerp(double a, double b, double t) => a + (b - a) * t;

        /// <summary>A picture (BGRA ints as made here) as PNG bytes - RGBA, 8 bits, no filtering - for BitmapCreateFromPng.</summary>
        public static byte[] ToPng(int[] bgra, int w, int h)
        {
            var raw = new byte[h * (w * 4 + 1)];
            for (int y = 0, o = 0; y < h; y++)
            {
                raw[o++] = 0;   // filter: none
                for (int x = 0; x < w; x++)
                {
                    int v = bgra[y * w + x];
                    raw[o++] = (byte)(v >> 16);   // r
                    raw[o++] = (byte)(v >> 8);    // g
                    raw[o++] = (byte)v;           // b
                    raw[o++] = (byte)(v >> 24);   // a
                }
            }
            byte[] packed;
            using (var ms = new MemoryStream())
            {
                using (var z = new ZLibStream(ms, CompressionLevel.Fastest, true)) z.Write(raw, 0, raw.Length);
                packed = ms.ToArray();
            }
            using var png = new MemoryStream();
            png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            var ihdr = new byte[13];
            WriteBig(ihdr, 0, w); WriteBig(ihdr, 4, h);
            ihdr[8] = 8; ihdr[9] = 6;   // 8 bits, RGBA
            Chunk(png, "IHDR", ihdr);
            Chunk(png, "IDAT", packed);
            Chunk(png, "IEND", Array.Empty<byte>());
            return png.ToArray();
        }

        static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4];
            WriteBig(len, 0, data.Length);
            s.Write(len);
            byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            s.Write(typeBytes);
            s.Write(data);
            uint crc = Crc(Crc(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
            var c = new byte[4];
            WriteBig(c, 0, (int)crc);
            s.Write(c);
        }

        static void WriteBig(byte[] b, int at, int v)
        {
            b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v;
        }

        static readonly uint[] crcTable = MakeCrcTable();

        static uint[] MakeCrcTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        static uint Crc(uint crc, byte[] data)
        {
            foreach (byte b in data) crc = crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        // ---- the game's own textures ----
        // IN THE GAME'S STYLE. The frost, ice, rust, grime, mold and snow COLOURS come pixel for pixel from the game's
        // own textures - the frost overlay it puts on blocks, packed glacier ice, the rust / grime / mold overlays,
        // lying snow - read from the game's assets when the client starts (LoadGameTextures), so a texture pack that
        // changes them changes these too. Only the SHAPES (where it has crept to) are worked out here.

        /// <summary>A texture read from the game, RGBA 8-bit unpremultiplied; tiles over any size.</summary>
        sealed class GameTile
        {
            public int W = 1, H = 1;
            public byte[] Rgba;
            public GameTile(byte r, byte g, byte b, byte a) { Rgba = new[] { r, g, b, a }; }   // a stand-in colour
            public GameTile(int w, int h, byte[] rgba) { W = w; H = h; Rgba = rgba; }
            public (double r, double g, double b, double a) At(int x, int y)
            {
                int i = (((y % H) + H) % H * W + ((x % W) + W) % W) * 4;
                return (Rgba[i], Rgba[i + 1], Rgba[i + 2], Rgba[i + 3] / 255.0);
            }
        }

        // stand-ins (each texture's average) until the game's are read, or if one is missing
        static GameTile[] frostTiles = { new(200, 210, 212, 80) };
        static GameTile[] iceTiles = { new(176, 212, 235, 255) };
        static GameTile[] moldTiles = { new(20, 19, 12, 100) };
        static GameTile rustTile = new(120, 81, 66, 179), grimeTile = new(45, 23, 0, 175);
        static GameTile[] snowTiles = { new(236, 240, 246, 255) };

        /// <summary>
        /// Client start: reads the game textures the pictures are coloured from. <paramref name="read"/> hands back a file's
        /// bytes by its path in the game's assets ("textures/block/...png"), or null. Pictures made before this use the
        /// stand-in colours - so the texture caches are cleared here too.
        /// </summary>
        public static void LoadGameTextures(Func<string, byte[]> read)
        {
            GameTile[] Many(string format, int count, GameTile[] fallback)
            {
                var found = new List<GameTile>();
                for (int i = 1; i <= count; i++) { GameTile t = Decode(read(string.Format(format, i))); if (t != null) found.Add(t); }
                return found.Count > 0 ? found.ToArray() : fallback;
            }
            frostTiles = Many("textures/block/liquid/frost/frost{0}.png", 3, frostTiles);
            iceTiles = Many("textures/block/liquid/ice/packedglacier{0}.png", 4, iceTiles);
            var mold = new List<GameTile>(Many("textures/block/overlay/mold/mold{0}.png", 3, Array.Empty<GameTile>()));
            GameTile spotty = Decode(read("textures/block/overlay/spottymold/spottymold1.png"));
            if (spotty != null) mold.Add(spotty);
            if (mold.Count > 0) moldTiles = mold.ToArray();
            rustTile = Decode(read("textures/block/overlay/rust.png")) ?? rustTile;
            grimeTile = Decode(read("textures/block/overlay/grime.png")) ?? grimeTile;
            snowTiles = Many("textures/block/liquid/snow/normal{0}.png", 6, snowTiles);
        }

        static GameTile Decode(byte[] png)
        {
            if (png == null || png.Length == 0) return null;
            try
            {
                SKImageInfo bounds = SKBitmap.DecodeBounds(png);
                if (bounds.Width <= 0 || bounds.Height <= 0) return null;
                using SKBitmap bmp = SKBitmap.Decode(png, new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
                return bmp == null ? null : new GameTile(bmp.Width, bmp.Height, bmp.Bytes);
            }
            catch (Exception) { return null; }
        }

        /// <summary>A steady random 0-1 for a texel - the ragged, pixel-by-pixel edge (no smooth ramp, no outline).</summary>
        static double Hash01(int x, int y, int seed)
        {
            uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(seed * 83492791);
            h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
            return (h & 0xFFFFFF) / (double)0x1000000;
        }

        // ---- the vanilla-style finish ----
        // CLOSER TO VANILLA. Vanilla textures read as detailed because no two neighbouring texels are the same shade,
        // the shapes are lit from one side and dark gaps separate them. One pass gives every glass picture that: each
        // texel's RELIEF (its thickness, and its brightness where it is thick) lit from the top left - a face rising
        // toward the bottom right is lighter, one falling darker -, GRAIN, a faint dark RIM just past an edge on the
        // shaded side, GLINTS on the brightest texels (frost, snow) or dark SPECKS (grime).
        readonly record struct Finish(double Light, double Grain, double Rim, double Glint, bool DarkSpecks);

        static readonly Finish FrostFinish = new(0.9, 0.08, 0.35, 0.03, false);
        static readonly Finish GrimeFinish = new(0.8, 0.12, 0.3, 0, true);
        static readonly Finish SnowFinish = new(0.7, 0.06, 0, 0.05, false);   // no rim: it made the drift and flakes look dirty

        /// <summary>A picture (BGRA ints as made here) through the vanilla-style finish - a new array, the same size.</summary>
        static int[] VanillaFinish(int[] px, int w, int h, Finish f, int seed)
        {
            var A = new double[w * h];
            var L = new double[w * h];
            for (int i = 0; i < w * h; i++)
            {
                int v = px[i];
                A[i] = (v >> 24 & 0xFF) / 255.0;
                L[i] = ((v >> 16 & 0xFF) + (v >> 8 & 0xFF) + (v & 0xFF)) / 765.0;
            }
            double Rel(int x, int y, double here) => x < 0 || x >= w || y < 0 || y >= h ? here : 0.65 * A[y * w + x] + 0.35 * L[y * w + x] * A[y * w + x];
            var output = new int[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, v = px[i];
                double a = A[i], r = v >> 16 & 0xFF, g = v >> 8 & 0xFF, b = v & 0xFF;
                double here = 0.65 * a + 0.35 * L[i] * a;
                double light = (Rel(x + 1, y + 1, here) - Rel(x - 1, y - 1, here)) * 0.5
                             + (Rel(x + 1, y, here) - Rel(x - 1, y, here)) * 0.25 + (Rel(x, y + 1, here) - Rel(x, y - 1, here)) * 0.25;
                double shade = 1 + f.Light * light + f.Grain * (Hash01(x, y, seed) * 2 - 1);
                r = Math.Clamp(r * shade, 0, 255); g = Math.Clamp(g * shade, 0, 255); b = Math.Clamp(b * shade, 0, 255);
                if (f.Glint > 0 && a > 140 / 255.0 && L[i] > 0.6 && Hash01(x, y, seed + 7) < f.Glint)
                {
                    r = Math.Min(255, r * 1.25 + 25); g = Math.Min(255, g * 1.25 + 25); b = Math.Min(255, b * 1.25 + 25);
                }
                if (f.DarkSpecks && a > 140 / 255.0 && Hash01(x, y, seed + 9) < 0.05) { r *= 0.6; g *= 0.6; b *= 0.6; }
                if (f.Rim > 0 && a < 90 / 255.0)
                {
                    double up = Math.Max(y > 0 ? A[i - w] : 0, Math.Max(x > 0 ? A[i - 1] : 0, x > 0 && y > 0 ? A[i - w - 1] : 0));
                    if (up > 0.45)
                    {
                        int nw = px[Math.Max(0, y - 1) * w + Math.Max(0, x - 1)];
                        double rr = (nw >> 16 & 0xFF) * 0.45, rg = (nw >> 8 & 0xFF) * 0.45, rb = (nw & 0xFF) * 0.45;
                        double ra = f.Rim * up, na = a + ra * (1 - a);
                        r = (r * a + rr * ra * (1 - a)) / na; g = (g * a + rg * ra * (1 - a)) / na; b = (b * a + rb * ra * (1 - a)) / na;
                        a = na;
                    }
                }
                if (a < 0.02) continue;
                output[i] = ColorUtil.ToRgba((int)(a * 255), (int)r, (int)g, (int)b);
            }
            return output;
        }

        // ---- frost ----
        // CRYSTALLINE FROST: every texel of a pane has a FREEZE ORDER and step n of FrostSteps freezes the texels in
        // that order, so the frost grows in from the frame and, going back down, the last texels to freeze are the
        // first to clear.
        //
        // CLEARING AT RANDOM: the ORDER is the frame distance read at a wobbled spot (ragged channels, not a ring) +
        // three sizes of noise + a few LATE SPOTS that freeze last, then RANKED: step n freezes n/16 of the pane, and
        // step 16 reaches every texel - the coldest weather (FullFrostAt -25 C) always ices the whole pane over. The
        // order is worked out on a grid twice as fine as the picture (Fine) - the sizes below were tuned there - and
        // read at every other point.
        //
        // THE GAME'S FROST TURNING TO ICE: thin frost is the game's own frost overlay, grainy and see- through; the
        // THICKER it is (by the cold - FrostGlass.FrostThickTarget, -1 C to -25 C, drawn as FrostThickLevels pictures
        // the layer fades between) and the longer a part has been frozen, the more it turns to the game's packed
        // glacier ice and the harder it is to see through. It always forms to the WHOLE pane (the steps are only the
        // forming), with a couple of thin spots (FrostThinSpots) that frost over like the rest, only thinner.

        sealed class FrostPattern
        {
            /// <summary>The freeze order, ranked 0-1: step n of FrostSteps freezes the texels below n / FrostSteps.</summary>
            public float[,] Order;
            /// <summary>How far in from the edges / corners each texel is, in fine TEXELS (0 at the frame).</summary>
            public float[,] Edge;
        }

        const double IceThickTexels = 16.0;    // how far in from the frame (fine texels) the ice is thicker: four voxels
        const double IceMostAlpha = 0.95;      // the coldest is hard to see through, but never quite a wall
        const double WobbleTexels = 12.0;      // how far the frame distance is read off its spot - the raggedness of the front
        const double EdgeJitter = 0.05;        // how ragged the creeping edge is, pixel by pixel
        /// <summary>How many thicknesses of frost are drawn (0 thinnest to FrostThickLevels - 1 solid ice).</summary>
        public const int FrostThickLevels = 8;
        const int FrostThinSpots = 2;          // the small thinner spots on a full-sized pane (fewer on a small one; 0 = none)
        const double ThinSpotAlpha = 0.35;     // how much of the frost's strength a thin spot keeps at its middle

        static readonly Dictionary<(int, int, int, bool), FrostPattern> frostPatterns = new();
        const double FingerReachTexels = 13.0;   // the longest a crystal finger runs in: about three voxels

        /// <summary>Smooth random values over a w x h grid, varying over about <paramref name="cell"/> texels.</summary>
        static float[,] ValueNoise(int w, int h, int cell, Random rnd)
        {
            int gw = w / cell + 2, gh = h / cell + 2;
            var grid = new double[gw, gh];
            for (int i = 0; i < gw; i++)
            for (int j = 0; j < gh; j++) grid[i, j] = rnd.NextDouble();
            var noise = new float[w, h];
            for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                double fx = x / (double)cell, fy = y / (double)cell;
                int ix = (int)fx, iy = (int)fy;
                double tx = fx - ix, ty = fy - iy;
                tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty);
                double a = grid[ix, iy] * (1 - tx) + grid[ix + 1, iy] * tx;
                double b = grid[ix, iy + 1] * (1 - tx) + grid[ix + 1, iy + 1] * tx;
                noise[x, y] = (float)(a * (1 - ty) + b * ty);
            }
            return noise;
        }

        /// <summary>How far in from the edges and corners (x, y) is, 0 at the frame, about 1 in the middle.</summary>
        static double FrameDistance(int w, int h, double x, double y, double half, double[] cornerWeight)
        {
            double edge = Math.Min(Math.Min(x, y), Math.Min(w - 1 - x, h - 1 - y)) / half;
            double corner = double.MaxValue;
            for (int c = 0; c < 4; c++)
            {
                double dx = x - (c % 2 == 0 ? 0 : w - 1), dy = y - (c < 2 ? 0 : h - 1);
                corner = Math.Min(corner, Math.Sqrt(dx * dx + dy * dy) / (half * 1.6) * cornerWeight[c]);
            }
            return Math.Min(edge, corner * 0.85);
        }

        /// <param name="fingers">The crystal fingers running in ahead of the front - the frost's; the storm grime has none (they drew thin dark twigs).</param>
        static FrostPattern FrostPatternFor(int w, int h, int pattern, bool fingers = true)
        {
            lock (frostPatterns)
            {
                if (frostPatterns.TryGetValue((w, h, pattern, fingers), out var known)) return known;
            }
            var rnd = new Random(2112026 + pattern * 7 + w * 131 + h * 7919);
            double half = 0.5 * Math.Min(w, h);
            var cornerWeight = new double[4];
            for (int c = 0; c < 4; c++) cornerWeight[c] = 0.7 + rnd.NextDouble() * 0.5;   // some corners lead more than others
            float[,] large = ValueNoise(w, h, 14, rnd), mid = ValueNoise(w, h, 6, rnd), fine = ValueNoise(w, h, 3, rnd);
            float[,] wobbleX = ValueNoise(w, h, 10, rnd), wobbleY = ValueNoise(w, h, 10, rnd);
            // late spots: places that freeze last, so the clear glass left is scattered rather than one hole in the middle
            var spots = new List<(double x, double y, double r, double late)>();
            int spotCount = Math.Max(3, w * h / 450);
            for (int s = 0; s < spotCount; s++)
                spots.Add((half * 0.5 + rnd.NextDouble() * Math.Max(1, w - half), half * 0.5 + rnd.NextDouble() * Math.Max(1, h - half),
                           3 + rnd.NextDouble() * 6, 0.3 + rnd.NextDouble() * 0.35));
            var order = new float[w, h];
            var edgeIn = new float[w, h];
            for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                double wx = Math.Clamp(x + (wobbleX[x, y] - 0.5) * WobbleTexels, 0, w - 1);
                double wy = Math.Clamp(y + (wobbleY[x, y] - 0.5) * WobbleTexels, 0, h - 1);
                double noise = 0.55 * large[x, y] + 0.3 * mid[x, y] + 0.15 * fine[x, y] - 0.5;
                double late = 0;
                foreach (var (sx, sy, r, amount) in spots)
                {
                    double d = Math.Sqrt((x - sx) * (x - sx) + (y - sy) * (y - sy)) / r;
                    if (d < 1) { double t = 1 - d; late = Math.Max(late, amount * t * t * (3 - 2 * t)); }
                }
                order[x, y] = (float)(FrameDistance(w, h, wx, wy, half, cornerWeight) * 0.65 + noise * 0.8 + late);
                edgeIn[x, y] = (float)(FrameDistance(w, h, x, y, half, cornerWeight) * half);
            }
            // crystal fingers: branching paths in from the edges that freeze ahead of the front
            int fingerCount = fingers ? (int)(6.0 * (w + h) / 112) + 2 : 0;
            for (int f = 0; f < fingerCount; f++)
            {
                int side = rnd.Next(4);
                (double x, double y, double ang) = side switch
                {
                    0 => (rnd.NextDouble() * w, 0.0, Math.PI / 2),
                    1 => (rnd.NextDouble() * w, h - 1.0, -Math.PI / 2),
                    2 => (0.0, rnd.NextDouble() * h, 0.0),
                    _ => (w - 1.0, rnd.NextDouble() * h, Math.PI)
                };
                ang += rnd.NextDouble() * 1.2 - 0.6;
                var stack = new Stack<(double x, double y, double ang, double length, double lead)>();
                // the same length on every pane - as far as on a bi-fold leaf (they were up to half the pane)
                stack.Push((x, y, ang, (0.5 + rnd.NextDouble() * 0.5) * Math.Min(half, FingerReachTexels), 0.38));
                while (stack.Count > 0)
                {
                    var (bx, by, bang, length, lead) = stack.Pop();
                    for (int i = 0; i < (int)length; i++)
                    {
                        int ix = (int)bx, iy = (int)by;
                        if (ix >= 0 && ix < w && iy >= 0 && iy < h) order[ix, iy] -= (float)(lead * (1 - i / length));
                        bx += Math.Cos(bang); by += Math.Sin(bang);
                        bang += rnd.NextDouble() * 0.3 - 0.15;
                        if (rnd.NextDouble() < 0.06 && length > 6)
                            stack.Push((bx, by, bang + (rnd.Next(2) == 0 ? -1 : 1) * (0.5 + rnd.NextDouble() * 0.4), length * 0.5, lead * 0.8));
                    }
                }
            }
            // ranked: each step freezes the same share of the pane, and the last step reaches every texel
            var ranked = new (float order, int x, int y)[w * h];
            for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++) ranked[x * h + y] = (order[x, y], x, y);
            Array.Sort(ranked, (a, b) => a.order.CompareTo(b.order));
            for (int i = 0; i < ranked.Length; i++) order[ranked[i].x, ranked[i].y] = (i + 0.5f) / ranked.Length;

            var p = new FrostPattern { Order = order, Edge = edgeIn };
            lock (frostPatterns) frostPatterns[(w, h, pattern, fingers)] = p;
            return p;
        }

        /// <summary>
        /// Frost forming step <paramref name="step"/> (1-FrostSteps) at thickness <paramref name="thick"/> (0 to
        /// FrostThickLevels - 1) of pattern <paramref name="pattern"/> for a w x h pane, BGRA.
        /// </summary>
        public static int[] Frost(int w, int h, int step, int pattern, int thick) => CoverPixels(w, h, step, pattern, thick, 1);

        /// <summary>The milky frost cover (alpha times <paramref name="cover"/>) as a finished picture: BGRA, through VanillaFinish.</summary>
        static int[] CoverPixels(int w, int h, int step, int pattern, int thick, double cover)
        {
            var (a, r, g, b) = FrostCover(w, h, step, pattern, thick, cover);
            var pixels = new int[w * h];
            for (int i = 0; i < w * h; i++)
            {
                if (a[i] < 0.02) continue;
                pixels[i] = ColorUtil.ToRgba((int)(a[i] * 255), (int)r[i], (int)g[i], (int)b[i]);
            }
            return VanillaFinish(pixels, w, h, FrostFinish, pattern * 7 + 21);
        }

        /// <summary>
        /// The milky frost cover of a w x h pane - alpha and colour per texel (alpha 0 where it has not frozen yet),
        /// the alpha times <paramref name="cover"/>. The normal frost is this at cover 1; the Christmas frost lays its
        /// flakes over it, with the cover only once it is colder.
        /// </summary>
        static (double[] a, double[] r, double[] g, double[] b) FrostCover(int w, int h, int step, int pattern, int thick, double cover)
        {
            FrostPattern p = FrostPatternFor(w * Fine, h * Fine, pattern);
            GameTile frost = frostTiles[pattern % frostTiles.Length], ice = iceTiles[pattern % iceTiles.Length];
            double front = step / (double)FrostSteps * 1.02;
            double cold = Math.Clamp(thick / (double)(FrostThickLevels - 1), 0, 1);
            var gaps = FrostGapsFor(w, h, pattern);
            double[] ca = new double[w * h], cr = new double[w * h], cg = new double[w * h], cb = new double[w * h];
            if (cover <= 0) return (ca, cr, cg, cb);
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double into = front - p.Order[x * Fine, y * Fine] + (Hash01(x, y, pattern * 7 + 1) - 0.5) * EdgeJitter;
                if (into <= 0) continue;
                double thin = ThinSpot(gaps, x, y, pattern);   // 0 frost as usual - 1 the middle of a thin spot
                double depth = Math.Min(1.0, into / 0.3);       // frozen longer: more ice
                double rim = Math.Pow(Math.Clamp(1 - p.Edge[x * Fine, y * Fine] / IceThickTexels, 0, 1), 0.9);
                var f = frost.At(x, y);
                var i = ice.At(x, y);
                double k = cold * (0.35 + 0.65 * depth) * (0.7 + 0.3 * rim);   // light frost = the game's frost; the cold makes ice
                double aFrost = f.a * (0.7 + 0.5 * cold), aIce = 0.25 + 0.65 * cold;
                k *= 1 - 0.6 * thin;                                    // a thin spot stays more frost than ice
                int n = y * w + x;
                ca[n] = Math.Min(IceMostAlpha, aFrost + (aIce - aFrost) * k) * (1 - (1 - ThinSpotAlpha) * thin) * cover;
                cr[n] = Lerp(f.r, i.r, k); cg[n] = Lerp(f.g, i.g, k); cb[n] = Lerp(f.b, i.b, k);
            }
            return (ca, cr, cg, cb);
        }

        /// <summary>The thin spots: centre and radius in picture pixels, away from the frame - seeded per size and pattern.</summary>
        static List<(double x, double y, double r)> FrostGapsFor(int w, int h, int pattern)
        {
            var gaps = new List<(double, double, double)>();
            int count = Math.Min(FrostThinSpots, (int)Math.Round(FrostThinSpots * w * h / 800.0));   // 2 on a block-sized pane, 1 on a leaf
            var rnd = new Random(1112026 + pattern * 19 + w * 131 + h * 7919);
            for (int i = 0; i < count; i++)
                gaps.Add((w * (0.2 + 0.6 * rnd.NextDouble()), h * (0.2 + 0.6 * rnd.NextDouble()), 1.5 + rnd.NextDouble() * 1.5));
            return gaps;
        }

        /// <summary>How far into a thin spot (x, y) is: 0 outside, 1 at its middle, a ragged rim between.</summary>
        static double ThinSpot(List<(double x, double y, double r)> gaps, int x, int y, int pattern)
        {
            double most = 0;
            foreach (var (gx, gy, r) in gaps)
            {
                double dx = x + 0.5 - gx, dy = y + 0.5 - gy;
                double d = Math.Sqrt(dx * dx + dy * dy) + (Hash01(x, y, pattern * 7 + 3) - 0.5) * 1.2;
                most = Math.Max(most, Math.Clamp((r + 1 - d) / 2, 0, 1));
            }
            return most;
        }

        // ---- temporal storm grime ----
        // RUST AND MOLD. Grows like the frost - a ranked order of its own (no crystal fingers), in from the frame - so
        // the storm's strength sets how far in it gets, and as the storm ends it goes back down the steps: the last
        // grime to come is the first to clear. The game's RUST overlay, made near opaque, darkened by its GRIME overlay
        // where it has sat longest, with the game's MOLD overlays in clusters, most by the frame.

        public const int GrimeSteps = 16;
        /// <summary>
        /// COVERS THE WHOLE WINDOW, THE STORM SETS HOW THICK: it forms to the whole pane in any storm, drawn at one of
        /// GrimeThickLevels thicknesses by the storm's strength (WindWeather.StormGrimeThickness), with soft PATCHES of
        /// thinner dust (and no mold) here and there.
        /// </summary>
        public const int GrimeThickLevels = 8;
        // OPAQUE WITH SEE-THROUGH LIGHT DUST: the dust is solid at every storm strength (it was 0.35 + 0.65 x
        // strength); the thin patches are the light dust you see out through - fewer of them the stronger the storm -
        // and the strength still adds mold.
        const double ThinDustAlpha = 0.3;   // how much of the dust a light-dust patch keeps at its thinnest

        static readonly Dictionary<(int, int, int), float[,]> grimeClusters = new();
        static readonly Dictionary<(int, int, int), float[,]> grimeThinPatches = new();
        const int GrimeOrderSeed = 100;   // the grime's order is the frost's maker on its own pattern numbers

        /// <summary>Where the mold clusters gather (fine grid): soft noise, seeded per size and pattern.</summary>
        static float[,] GrimeClustersFor(int w, int h, int pattern)
        {
            lock (grimeClusters)
            {
                if (grimeClusters.TryGetValue((w, h, pattern), out var known)) return known;
            }
            float[,] cluster = ValueNoise(w, h, 8, new Random(31102026 + pattern * 13 + w * 131 + h * 7919));
            lock (grimeClusters) grimeClusters[(w, h, pattern)] = cluster;
            return cluster;
        }

        /// <summary>Where the dust lies thinner (fine grid): broad soft noise, seeded per size and pattern.</summary>
        static float[,] GrimeThinFor(int w, int h, int pattern)
        {
            lock (grimeThinPatches)
            {
                if (grimeThinPatches.TryGetValue((w, h, pattern), out var known)) return known;
            }
            // smaller patches with ragged edges: a broad noise roughened by a fine one
            var rnd = new Random(2112027 + pattern * 29 + w * 131 + h * 7919);
            float[,] broad = ValueNoise(w, h, 7, rnd), rough = ValueNoise(w, h, 2, rnd);
            var thin = new float[w, h];
            for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++) thin[x, y] = 0.68f * broad[x, y] + 0.32f * rough[x, y];
            lock (grimeThinPatches) grimeThinPatches[(w, h, pattern)] = thin;
            return thin;
        }

        static double Smooth01(double t) { t = Math.Clamp(t, 0, 1); return t * t * (3 - 2 * t); }

        /// <summary>
        /// Temporal storm grime forming step <paramref name="step"/> (1-GrimeSteps) at thickness <paramref name="thick"/> (0 to
        /// GrimeThickLevels - 1) of pattern <paramref name="pattern"/> for a w x h pane, BGRA.
        /// </summary>
        public static int[] StormGrime(int w, int h, int step, int pattern, int thick)
        {
            FrostPattern order = FrostPatternFor(w * Fine, h * Fine, pattern + GrimeOrderSeed, fingers: false);
            float[,] clusters = GrimeClustersFor(w * Fine, h * Fine, pattern);
            float[,] thinPatches = GrimeThinFor(w * Fine, h * Fine, pattern);
            double strength = Math.Clamp(thick / (double)(GrimeThickLevels - 1), 0, 1);
            GameTile mold = moldTiles[pattern % moldTiles.Length];
            double front = step / (double)GrimeSteps * 1.02;
            var pixels = new int[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double into = front - order.Order[x * Fine, y * Fine] + (Hash01(x, y, pattern * 7 + 2) - 0.5) * EdgeJitter;
                if (into <= 0) continue;
                double depth = Math.Min(1.0, into / 0.35);      // grime there longer is thicker
                double rim = Math.Pow(Math.Clamp(1 - order.Edge[x * Fine, y * Fine] / IceThickTexels, 0, 1), 0.9);
                var rust = rustTile.At(x, y);
                var grime = grimeTile.At(x, y);
                // 0 - 1 in a light-dust patch: a ragged edge pixel by pixel, grains of dust left inside it, and its thinness uneven
                double thin = Smooth01((thinPatches[x * Fine, y * Fine] + (Hash01(x, y, pattern * 7 + 4) - 0.5) * 0.12
                                        - (0.53 + 0.08 * strength)) / 0.1);
                if (Hash01(x, y, pattern * 7 + 5) < 0.3) thin *= 0.35;
                double keep = ThinDustAlpha * (0.6 + 0.9 * Hash01(x, y, pattern * 7 + 6));
                double a = (0.92 + 0.08 * rust.a) * (1 - (1 - keep) * thin);
                double dark = grime.a * 0.45 * depth * (1 - thin);
                double r = Lerp(rust.r, grime.r, dark), g = Lerp(rust.g, grime.g, dark), b = Lerp(rust.b, grime.b, dark);
                // the stronger the storm, the more mold patches - a lower threshold opens more of the same soft
                // clusters
                double cluster = Smooth01((clusters[x * Fine, y * Fine] - (0.8 - 0.2 * depth - 0.14 * rim - 0.3 * strength)) / 0.22) * depth;
                var m = mold.At(x, y);
                double ma = m.a * Math.Min(1.0, cluster * 1.4) * (1 - thin) * (0.5 + 0.5 * strength);
                r = Lerp(r, m.r, ma); g = Lerp(g, m.g, ma); b = Lerp(b, m.b, ma);
                a += (1 - a) * ma;
                pixels[y * w + x] = ColorUtil.ToRgba((int)(a * 255), (int)r, (int)g, (int)b);
            }
            return VanillaFinish(pixels, w, h, GrimeFinish, pattern * 7 + 41);
        }

        /// <summary>
        /// A picture worked out on the fine grid (2w x 2h) brought down to w x h: each pixel the strongest alpha of its 2x2
        /// (a one-texel flake keeps its strength), coloured from <paramref name="tiles"/> - the game's snow - at its spot.
        /// </summary>
        static int[] FromFine(int[] fine, int w, int h, GameTile[] tiles, int pattern)
        {
            GameTile tile = tiles[pattern % tiles.Length];
            var pixels = new int[w * h];
            int fw = w * Fine;
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int most = 0;
                for (int j = 0; j < Fine; j++)
                for (int i = 0; i < Fine; i++)
                    most = Math.Max(most, (fine[(y * Fine + j) * fw + x * Fine + i] >> 24) & 0xFF);
                if (most == 0) continue;
                var c = tile.At(x, y);
                pixels[y * w + x] = ColorUtil.ToRgba(most, (int)c.r, (int)c.g, (int)c.b);
            }
            return pixels;
        }

        // ---- snow ----

        // CREEPING SNOW: like the frost, every texel has a SNOW ORDER - how far up the drift, the bottom-corner banks
        // or the side strips it is, as a share of how far they reach at full snow , plus blotchy noise - and step n of
        // SnowSteps covers the texels in that order. So the snow starts as patches along the bottom edge and in the
        // corners and builds up into the drift; melting takes it back down. Flakes stuck higher up come one by one.

        sealed class SnowPattern
        {
            public float[,] Order;
            public double[,] Grain;
            public List<(int x, int y, bool big)> Flakes;
        }

        static readonly Dictionary<(int, int, int), SnowPattern> snowPatterns = new();

        static SnowPattern SnowPatternFor(int w, int h, int pattern)
        {
            lock (snowPatterns)
            {
                if (snowPatterns.TryGetValue((w, h, pattern), out var known)) return known;
            }
            var rnd = new Random(29102027 + pattern * 17 + w * 131 + h * 7919);
            var waves = new (double f, double ph, double a)[3];
            var corners = new[] { 0.8 + rnd.NextDouble() * 0.5, 0.8 + rnd.NextDouble() * 0.5 };
            var side = new double[Math.Max(w, h)];
            var p = new SnowPattern { Order = new float[w, h], Grain = new double[w, h], Flakes = new() };
            for (int i = 0; i < 3; i++) waves[i] = (0.6 + rnd.NextDouble(), rnd.NextDouble() * 2 * Math.PI, 0.6 + rnd.NextDouble() * 0.8);
            for (int i = 0; i < side.Length; i++) side[i] = 0.6 + rnd.NextDouble() * 0.8;
            for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++) p.Grain[x, y] = rnd.NextDouble();
            // flakes by the pane's area
            int most = (int)Math.Round(30.0 * w * h / (FineTexelsPerBlock * FineTexelsPerBlock)) + 1;
            int top = Math.Max(3, h - (int)(8 * Scale));
            for (int i = 0; i < most; i++)
                p.Flakes.Add((rnd.Next(1, Math.Max(2, w - 2)), rnd.Next(1, top), rnd.NextDouble() < 0.3));
            float[,] blot = ValueNoise(w, h, 6, rnd);
            float[,] fine = ValueNoise(w, h, 2, rnd);

            // how far the drift, banks and side strips reach at full snow
            double depth = 3.8 * Scale, bank = 6.2 * Scale, rim = 2.0 * Scale;
            for (int x = 0; x < w; x++)
            {
                double wave = 0;
                foreach (var (f, ph, a) in waves) wave += a * Math.Sin(f * x / (double)w * 2 * Math.PI + ph);
                wave /= 3.0;
                double reach = depth * (1.0 + 0.35 * wave);
                for (int c = 0; c < 2; c++)
                {
                    double d = Math.Abs(x - (c == 0 ? 0 : w - 1)) / (w * 0.35);
                    if (d < 1) reach = Math.Max(reach, bank * corners[c] * (1 - d) * (1 - d));
                }
                int edgeDist = Math.Min(x, w - 1 - x);
                for (int y = 0; y < h; y++)
                {
                    int hh = h - 1 - y;     // height above the bottom edge
                    double up = hh / reach;
                    // up the sides only over the lower part of the pane, out from the frame and up it as it builds
                    double sides = edgeDist / (rim * side[y % side.Length]) + hh / SideSnowUpTexels;
                    p.Order[x, y] = (float)(Math.Min(up, sides * 1.05) * 0.9 + (blot[x, y] - 0.5) * 0.24 + (fine[x, y] - 0.5) * 0.08);
                }
            }
            lock (snowPatterns) snowPatterns[(w, h, pattern)] = p;
            return p;
        }

        const double SideSnowUpTexels = 12.0;   // how far up the sides the snow reaches at full: three voxels

        /// <summary>
        /// SNOW ON A SHUTTER BOARD: scattered PATCHES of snow over the board's face, more and a little bigger each step
        /// (a line along the top looks glitchy on a swinging leaf). Nothing at the bottom.
        /// </summary>
        public static int[] BoardSnow(int w, int h, int step, int pattern) =>
            VanillaFinish(FromFine(BoardSnowFine(w * Fine, h * Fine, step, pattern), w, h, snowTiles, pattern), w, h, SnowFinish, pattern * 7 + 61);

        static int[] BoardSnowFine(int w, int h, int step, int pattern)
        {
            // PATCHES OF SNOW scattered over the board - soft irregular clumps, more of them and a little bigger as it
            // snows; nothing in a line or a drift. The same clumps every step (seeded per size + pattern), so a step
            // only adds and grows them.
            var rnd = new Random(31102026 + pattern * 13 + w * 131 + h * 7919);
            double area = w * h / (FineTexelsPerBlock * (double)FineTexelsPerBlock);
            double t = step / (double)SnowSteps;
            // patches coming one by one over the steps
            int most = (int)Math.Round(22.0 * area) + 1;
            int shown = (int)Math.Round(22.0 * t * area) + 1;
            double grow = 0.7 + 0.3 * t;
            var alpha = new double[w, h];
            var shade = new int[w, h];
            for (int i = 0; i < most; i++)
            {
                double cx = rnd.NextDouble() * w, cy = rnd.NextDouble() * h;
                double rx = (1.5 + rnd.NextDouble() * 2.5) * grow, ry = (1.0 + rnd.NextDouble() * 1.2) * grow;
                double p1 = rnd.NextDouble() * 6.28, p2 = rnd.NextDouble() * 6.28;
                int tone = rnd.Next(10);
                if (i >= shown) continue;   // drawn the same randomness every step
                for (int x = (int)(cx - rx - 2); x <= (int)(cx + rx + 2); x++)
                for (int y = (int)(cy - ry - 2); y <= (int)(cy + ry + 2); y++)
                {
                    if (x < 0 || y < 0 || x >= w || y >= h) continue;
                    double dx = (x + 0.5 - cx) / rx, dy = (y + 0.5 - cy) / ry;
                    double ang = Math.Atan2(dy, dx);
                    double edge = 1 + 0.25 * Math.Sin(3 * ang + p1) + 0.15 * Math.Sin(5 * ang + p2);   // lumpy outline
                    double d = Math.Sqrt(dx * dx + dy * dy) / edge;
                    if (d >= 1.15) continue;
                    double a = d < 0.75 ? 0.92 : d < 1.0 ? 0.7 : 0.35;
                    if (a > alpha[x, y]) { alpha[x, y] = a; shade[x, y] = tone; }
                }
            }
            var pixels = new int[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (alpha[x, y] <= 0) continue;
                int tone = shade[x, y];
                pixels[y * w + x] = ColorUtil.ToRgba((int)(alpha[x, y] * 255), 242 - tone, 246 - tone, 252 - tone / 2);
            }
            return pixels;
        }

        /// <summary>
        /// Snow step <paramref name="step"/> (1-SnowSteps) of pattern <paramref name="pattern"/> for a w x h pane,
        /// BGRA: the shape worked out on the fine grid, coloured by the game's own lying snow.
        /// </summary>
        public static int[] Snow(int w, int h, int step, int pattern) =>
            VanillaFinish(FromFine(SnowFine(w * Fine, h * Fine, step, pattern), w, h, snowTiles, pattern), w, h, SnowFinish, pattern * 7 + 61);

        static int[] SnowFine(int w, int h, int step, int pattern)
        {
            SnowPattern p = SnowPatternFor(w, h, pattern);
            double t = step / (double)SnowSteps;
            double front = t * 1.05 - 0.04;
            var alpha = new double[w, h];
            for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                double into = front - p.Order[x, y];
                if (into <= 0) continue;
                double ramp = Math.Min(1.0, into / 0.05);       // the growing edge is thin and soft
                double settled = Math.Min(1.0, into / 0.25);    // snow lying longer is thicker
                alpha[x, y] = ramp * (0.45 + 0.47 * settled) * (0.9 + 0.1 * p.Grain[x, y]);
            }
            // flakes stuck on the glass: more as it snows, a few two texels wide
            int flakes = (int)Math.Round(25.0 * t * w * h / (FineTexelsPerBlock * FineTexelsPerBlock));
            for (int i = 0; i < Math.Min(flakes, p.Flakes.Count); i++)
            {
                var (fx, fy, big) = p.Flakes[i];
                alpha[fx, fy] = Math.Max(alpha[fx, fy], 0.8);
                if (big && fx + 1 < w && fy + 1 < h)
                {
                    alpha[fx + 1, fy] = Math.Max(alpha[fx + 1, fy], 0.55);
                    alpha[fx, fy + 1] = Math.Max(alpha[fx, fy + 1], 0.55);
                }
            }
            var pixels = new int[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double a = alpha[x, y];
                if (a < 0.03) continue;
                int shade = a > 0.7 ? 0 : 8;   // softer edges a touch greyer
                pixels[y * w + x] = ColorUtil.ToRgba((int)(Math.Min(1.0, a) * 255), 240 - shade, 244 - shade, 250 - shade / 2);
            }
            return pixels;
        }

        // ---- rain ----
        // RUNNING RAIN: RainLevels strengths x RainPatterns layouts x BEUniversalDisplay.DropFrames frames, RainSize
        // square, one pixel the game's own size, flipped by the glass layer while it rains on the glass. Vertical streaks
        // of different lengths and strengths, one pixel wide, each led by a brighter bead with a glint just above it and
        // fading up behind it. They are spread evenly across the width, ordered so even light rain covers the whole block:
        // a pane only shows the part of the picture behind it, and clumped streaks would leave some panes with none.
        //
        // Drops come and go: each LANDS (one frame of a brighter pixel), sits as a one-pixel bead, then fades. Its next
        // life is somewhere else, at a staggered time, so at any moment some land, some sit and some clear. They lie a
        // little thicker in patches, never touch each other and never sit on a streak's line.
        //
        // Streaks run 2 or 4 pixels a frame, a whole number of heights over the loop, so it has no jump. The picture wraps
        // top to bottom and side to side, so the blocks of a big window run on into each other. A heavier level keeps
        // everything of a lighter one. Each frame is made the first time it is drawn (GlassLayerRenderer.RainTexture).
        public const int RainSize = 32;
        public const int RainLevels = 3, RainPatterns = 3;
        const int RainLives = 2;   // of each drop, a loop
        static readonly int[] RainStreakCounts = { 4, 7, 10 }, RainDropCounts = { 6, 16, 28 };   // light, medium, heavy
        static readonly (int r, int g, int b) RainTrail = (140, 180, 232), RainBead = (184, 212, 248), RainGlint = (222, 236, 255);
        static readonly Finish RainFinish = new(0.6, 0.04, 0, 0.15, false);   // soft light, light grain, more glints, no rim

        sealed record RainStreak(int X, int Start, int Speed, int Length, double Strength);
        sealed record RainDropLife(int X, int Y, int Start, int Hold, int Fade, double Strength);
        sealed record RainLayout(List<RainStreak> Streaks, List<List<RainDropLife>> Drops);

        static readonly Dictionary<int, RainLayout> rainLayouts = new();

        static int RainWrap(int v) => (v % RainSize + RainSize) % RainSize;

        /// <summary>0..n-1 ordered so every first few are spread out across the width: each next one in the widest gap left.</summary>
        static List<int> SpreadOrder(int n)
        {
            var order = new List<int> { 0 };
            while (order.Count < n)
            {
                var taken = new List<int>(order);
                taken.Sort();
                int size = -1, start = -1;
                for (int i = 0; i < taken.Count; i++)
                {
                    int gap = ((taken[(i + 1) % taken.Count] - taken[i]) % n + n) % n;
                    if (gap == 0) gap = n;
                    if (gap > size || gap == size && taken[i] > start) { size = gap; start = taken[i]; }
                }
                order.Add((start + size / 2) % n);
            }
            return order;
        }

        static RainLayout RainLayoutFor(int pattern)
        {
            if (rainLayouts.TryGetValue(pattern, out RainLayout known)) return known;
            var rnd = new Random(7310 + 101 * (pattern + 1));

            // where the water lies thicker: a few soft blobs, so the glass is not covered evenly
            var blobs = new (double x, double y, double r)[3];
            for (int i = 0; i < blobs.Length; i++)
                blobs[i] = (rnd.NextDouble() * RainSize, rnd.NextDouble() * RainSize, 5 + rnd.NextDouble() * 5);
            double Weight(int x, int y)
            {
                double w = 0.7;
                foreach (var (bx, by, r) in blobs)
                {
                    double dx = Math.Abs(x - bx), dy = Math.Abs(y - by);
                    dx = Math.Min(dx, RainSize - dx);
                    dy = Math.Min(dy, RainSize - dy);
                    w += Math.Exp(-(dx * dx + dy * dy) / (2 * r * r));
                }
                return w;
            }

            int bands = RainStreakCounts[RainLevels - 1];
            double width = RainSize / (double)bands;
            int shift = rnd.Next(bands), slide = rnd.Next((int)width);   // slid sideways per pattern, so the patterns differ
            var columns = new List<int>();
            foreach (int band in SpreadOrder(bands))
                columns.Add(((int)(((band + shift) % bands) * width + 0.5 * width) + slide) % RainSize);   // mid-band: never side by side

            var streaks = new List<RainStreak>();
            for (int i = 0; i < columns.Count; i++)
            {
                bool quick = i % 3 == 2;
                int start = rnd.Next(RainSize);
                int length = quick ? rnd.Next(6, 11) : rnd.Next(10, 22);
                streaks.Add(new RainStreak(columns[i], start, quick ? 4 : 2, length, 0.3 + rnd.NextDouble() * 0.16));
            }

            int frames = BEUniversalDisplay.DropFrames;
            int span = frames / RainLives;   // each life fits in its own share of the loop, so lives never overlap
            int Near(int d) => Math.Min(RainWrap(d), RainWrap(-d));
            var taken = new List<(int x, int y)>();
            bool Free(int x, int y)
            {
                foreach (int c in columns) if (Near(x - c) == 0) return false;
                foreach (var (tx, ty) in taken) if (Near(x - tx) <= 1 && Near(y - ty) <= 1) return false;
                return true;
            }
            var drops = new List<List<RainDropLife>>();
            for (int n = 0; n < RainDropCounts[RainLevels - 1]; n++)
            {
                int offset = rnd.Next(frames);
                var lives = new List<RainDropLife>();
                for (int life = 0; life < RainLives; life++)
                {
                    int px = -1, py = -1;
                    for (int tries = 0; tries < 2000; tries++)
                    {
                        int x = rnd.Next(RainSize), y = rnd.Next(RainSize);
                        // the clumping gives way before the spacing
                        if (Free(x, y) && (tries > 1000 || rnd.NextDouble() * 1.6 <= Weight(x, y))) { px = x; py = y; break; }
                    }
                    if (px < 0) continue;   // no room left on the picture: one drop fewer
                    taken.Add((px, py));
                    int hold = rnd.Next(6, 11);
                    int fade = rnd.Next(3, Math.Min(4, span - 2 - hold) + 1);
                    lives.Add(new RainDropLife(px, py, (offset + life * span) % frames, hold, fade, 0.3 + rnd.NextDouble() * 0.2));
                }
                drops.Add(lives);
            }
            return rainLayouts[pattern] = new RainLayout(streaks, drops);
        }

        static void RainPut(int[] px, int x, int y, (int r, int g, int b) colour, double a)
        {
            int i = RainWrap(y) * RainSize + RainWrap(x), v = px[i];
            a = Math.Clamp(a, 0, 1);
            double a0 = (v >> 24 & 0xFF) / 255.0, total = a + a0 * (1 - a);
            if (total <= 0) return;
            int Mix(int c, int c0) => (int)((c * a + c0 * a0 * (1 - a)) / total);
            px[i] = ColorUtil.ToRgba((int)(total * 255), Mix(colour.r, v >> 16 & 0xFF), Mix(colour.g, v >> 8 & 0xFF), Mix(colour.b, v & 0xFF));
        }

        /// <summary>One frame of the rain, RainSize square, BGRA. <paramref name="level"/> 0 light to 2 heavy; <paramref name="pattern"/> and <paramref name="frame"/> from 0.</summary>
        public static int[] Rain(int level, int pattern, int frame)
        {
            RainLayout layout = RainLayoutFor(pattern);
            var px = new int[RainSize * RainSize];
            int frames = BEUniversalDisplay.DropFrames;
            for (int n = 0; n < RainDropCounts[level]; n++)
                foreach (RainDropLife life in layout.Drops[n])
                {
                    int t = ((frame - life.Start) % frames + frames) % frames;
                    if (t >= 1 + life.Hold + life.Fade) continue;
                    if (t == 0) { RainPut(px, life.X, life.Y, RainGlint, Math.Min(1.0, life.Strength * 1.5)); continue; }   // landing
                    double a = t > life.Hold ? life.Strength * (1 - (t - life.Hold) / (life.Fade + 1.0)) : life.Strength;
                    RainPut(px, life.X, life.Y, RainBead, a);
                }
            for (int n = 0; n < RainStreakCounts[level]; n++)
            {
                RainStreak s = layout.Streaks[n];
                int head = s.Start + frame * s.Speed;
                for (int k = 1; k <= s.Length; k++)
                    RainPut(px, s.X, head - k, RainTrail, s.Strength * Math.Pow(1 - k / (s.Length + 1.0), 1.3));
                RainPut(px, s.X, head, RainBead, s.Strength + 0.2);
                RainPut(px, s.X, head - 1, RainGlint, s.Strength * 0.6);
            }
            return VanillaFinish(px, RainSize, RainSize, RainFinish, (pattern + 1) * 7 + 81);
        }

        // ---- damp wood ----
        // RAIN SOAKS THE WOOD, CREEPING LIKE THE FROST: a dark see-through film over the outside wood of a window or
        // shutter, in DampSteps. The wood goes wet first along streaks where the water runs down, then in patches that
        // spread and join until it is soaked; it dries back the same way. A slightly darker edge at the wet front.
        // Laid once per block (DampSize square, tiling), each block one of DampPatterns, flipped or not (GlassLayer),
        // so the patches never sit at the same height in every block.

        public const int DampSize = 32;
        public const int DampSteps = 16, DampPatterns = 3;
        static readonly (int r, int g, int b) DampColour = (30, 21, 12);
        const double DampAlpha = 0.42;       // how much darker soaked wood is
        const double DampFrontRamp = 0.12;   // how soft the wet front is
        const double DampEdge = 0.3;         // how much darker the wet front is than the soaked wood behind it

        static readonly Dictionary<int, (float[] order, float[] detail, float[] run)> dampPatterns = new();

        /// <summary>Smooth value noise that tiles over DampSize: cellsX x cellsY cells, wrapped at the edges.</summary>
        static float[] TilingNoise(int cellsX, int cellsY, Random rnd)
        {
            var grid = new double[cellsX * cellsY];
            for (int i = 0; i < grid.Length; i++) grid[i] = rnd.NextDouble();
            var noise = new float[DampSize * DampSize];
            for (int y = 0; y < DampSize; y++)
                for (int x = 0; x < DampSize; x++)
                {
                    double fx = x * cellsX / (double)DampSize, fy = y * cellsY / (double)DampSize;
                    int x0 = (int)fx, y0 = (int)fy, x1 = (x0 + 1) % cellsX, y1 = (y0 + 1) % cellsY;
                    double tx = Smooth01(fx - x0), ty = Smooth01(fy - y0);
                    double top = Lerp(grid[y0 * cellsX + x0], grid[y0 * cellsX + x1], tx);
                    double bottom = Lerp(grid[y1 * cellsX + x0], grid[y1 * cellsX + x1], tx);
                    noise[y * DampSize + x] = (float)Lerp(top, bottom, ty);
                }
            return noise;
        }

        static (float[] order, float[] detail, float[] run) DampPatternFor(int pattern)
        {
            if (dampPatterns.TryGetValue(pattern, out var known)) return known;
            var rnd = new Random(7112026 + 131 * (pattern + 1));
            float[] blot = TilingNoise(4, 4, rnd), fine = TilingNoise(8, 8, rnd);
            float[] run = TilingNoise(16, 2, rnd);      // narrow and tall: where the water runs down
            float[] detail = TilingNoise(16, 16, rnd);
            var order = new float[blot.Length];
            for (int i = 0; i < order.Length; i++) order[i] = (float)(0.45 * blot[i] + 0.35 * (1 - run[i]) + 0.2 * fine[i]);
            // ranked, so step n soaks n / DampSteps of the wood whatever the noise came out as
            var rank = new int[order.Length];
            for (int i = 0; i < rank.Length; i++) rank[i] = i;
            Array.Sort(rank, (p, q) => order[p].CompareTo(order[q]));
            var ranked = new float[order.Length];
            for (int r = 0; r < rank.Length; r++) ranked[rank[r]] = r / (float)(rank.Length - 1);
            return dampPatterns[pattern] = (ranked, detail, run);
        }

        /// <summary>The damp at <paramref name="step"/> (1 to DampSteps), DampSize square, BGRA.</summary>
        public static int[] Damp(int step, int pattern)
        {
            var (order, detail, run) = DampPatternFor(pattern);
            var px = new int[DampSize * DampSize];
            double front = step / (double)DampSteps * (1 + DampFrontRamp);
            for (int y = 0; y < DampSize; y++)
                for (int x = 0; x < DampSize; x++)
                {
                    int i = y * DampSize + x;
                    double soaked = Smooth01((front - order[i]) / DampFrontRamp);
                    if (soaked <= 0) continue;
                    double edge = step < DampSteps ? 4 * soaked * (1 - soaked) : 0;   // the wet front
                    double a = soaked * DampAlpha * (1 + DampEdge * edge)
                               * (0.85 + 0.15 * detail[i] + 0.1 * (run[i] - 0.5)) * (0.96 + 0.08 * Hash01(x, y, pattern * 19 + 3));
                    px[i] = ColorUtil.ToRgba((int)(Math.Clamp(a, 0, 1) * 255), DampColour.r, DampColour.g, DampColour.b);
                }
            return px;
        }
    }
}
