using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Vermilune.UI.Controls;

public partial class SkinPreview3D : UserControl
{
    private sealed class TextureData
    {
        public byte[] Pixels { get; init; } = Array.Empty<byte>();
        public int Width { get; init; }
        public int Height { get; init; }
    }

    private sealed class Face
    {
        public Point3D[] Points { get; init; } = Array.Empty<Point3D>();
        public Rect Crop { get; init; }
        public TextureData Texture { get; init; } = new();
        public double NX { get; init; }
        public double NY { get; init; }
        public double NZ { get; init; }
        public (double U, double V)[] UVs { get; init; } = Array.Empty<(double, double)>();
    }

    private readonly struct Point3D
    {
        public Point3D(double x, double y, double z) { X = x; Y = y; Z = z; }
        public double X { get; }
        public double Y { get; }
        public double Z { get; }
    }

    private readonly List<Face> _faces = new();
    private BitmapSource? _skin;
    private BitmapSource? _cape;
    private bool _slim;
    private double _angleX = 6;
    private double _angleY = -24;
    private Point _lastMouse;
    private bool _dragging;
    private bool _built;

    public SkinPreview3D()
    {
        InitializeComponent();
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        MouseLeave += (_, _) => { _dragging = false; Cursor = Cursors.Arrow; };
        Cursor = Cursors.SizeAll;
        SizeChanged += (_, _) => Render();
        Loaded += (_, _) => { _built = true; Rebuild(); };
    }

    public void SetTextures(BitmapSource? skin, BitmapSource? cape, bool slim)
    {
        _skin = skin;
        _cape = cape;
        _slim = slim;
        Rebuild();
    }

    public void SetView(double yaw, double pitch)
    {
        _angleY = yaw;
        _angleX = Math.Clamp(pitch, -55, 55);
        Rebuild();
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _lastMouse = e.GetPosition(this);
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(this);
        _angleY += (pos.X - _lastMouse.X) * 0.55;
        _angleX = Math.Clamp(_angleX + (pos.Y - _lastMouse.Y) * 0.45, -55, 55);
        _lastMouse = pos;
        Render();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        ReleaseMouseCapture();
    }

    private void Rebuild()
    {
        _faces.Clear();
        if (!_built && ActualWidth <= 0) return;

        var texture = PrepareTexture(_skin ?? CreateDefaultSkin());
        var armW = _slim ? 3.0 : 4.0;
        var armX = 4 + armW / 2.0;

        // 直接使用 SkinPilot 的 skinGeometry 分区与贴图盒，不再手写 UV。
        AddSkinBox(new Point3D(0, 10, 0), 8, 8, 8, 0, 0, 8, 8, 8, texture);
        AddSkinBox(new Point3D(0, 0, 0), 8, 12, 4, 16, 16, 8, 12, 4, texture);
        AddSkinBox(new Point3D(-armX, 0, 0), armW, 12, 4, 40, 16, armW, 12, 4, texture);
        AddSkinBox(new Point3D(armX, 0, 0), armW, 12, 4, 32, 48, armW, 12, 4, texture);
        AddSkinBox(new Point3D(-2, -12, 0), 4, 12, 4, 0, 16, 4, 12, 4, texture);
        AddSkinBox(new Point3D(2, -12, 0), 4, 12, 4, 16, 48, 4, 12, 4, texture);

        // 第二层：帽子、外套、袖子、裤腿。膨胀量也与 SkinPilot 一致。
        AddSkinBox(new Point3D(0, 10, 0), 8, 8, 8, 32, 0, 8, 8, 8, texture, 0.28);
        AddSkinBox(new Point3D(0, 0, 0), 8, 12, 4, 16, 32, 8, 12, 4, texture, 0.176);
        AddSkinBox(new Point3D(-armX, 0, 0), armW, 12, 4, 40, 32, armW, 12, 4, texture, 0.176);
        AddSkinBox(new Point3D(armX, 0, 0), armW, 12, 4, 48, 48, armW, 12, 4, texture, 0.176);
        AddSkinBox(new Point3D(-2, -12, 0), 4, 12, 4, 0, 32, 4, 12, 4, texture, 0.176);
        AddSkinBox(new Point3D(2, -12, 0), 4, 12, 4, 0, 48, 4, 12, 4, texture, 0.176);

        if (_cape is not null)
        {
            var capeTexture = PrepareTexture(_cape);
            // 与 Minecraft 披风模型一致：10×16 的平面，正面/背面各用披风贴图的前后区域。
            AddCapePlane(new[]
            {
                new Point3D(-5, -10, -2.2), new Point3D(5, -10, -2.2),
                new Point3D(5, 6, -2.2), new Point3D(-5, 6, -2.2)
            }, R(12, 1, 10, 16), capeTexture, 1);
            AddCapePlane(new[]
            {
                new Point3D(5, -10, -2.35), new Point3D(-5, -10, -2.35),
                new Point3D(-5, 6, -2.35), new Point3D(5, 6, -2.35)
            }, R(1, 1, 10, 16), capeTexture, -1);
        }

        Hint.Text = _cape is null ? "拖动旋转 · 无披风" : "拖动旋转";
        Render();
    }

    private static Rect R(double x, double y, double w, double h) => new(x, y, w, h);

    // SkinPilot skinGeometry.getSkinFaceUvRectangle 的等价实现。
    private void AddSkinBox(Point3D center, double w, double h, double d,
        int u, int v, double tw, double th, double td, TextureData texture, double inflate = 0)
    {
        var crops = new[]
        {
            R(u + td, v + td, tw, th),                 // front
            R(u + td * 2 + tw, v + td, tw, th),        // back
            R(u + td + tw, v + td, td, th),            // +X = 角色左侧
            R(u, v + td, td, th),                      // -X = 角色右侧
            R(u + td, v, tw, td),                      // top
            R(u + td + tw, v, tw, td)                  // bottom
        };
        AddBox(center, w + inflate * 2, h + inflate * 2, d + inflate * 2, crops, texture);
    }

    private void AddCapePlane(Point3D[] points, Rect crop, TextureData texture, double nz)
    {
        var u0 = crop.X / texture.Width;
        var u1 = crop.Right / texture.Width;
        var v0 = crop.Y / texture.Height;
        var v1 = crop.Bottom / texture.Height;
        var uvs = new[] { (u0, v1), (u1, v1), (u1, v0), (u0, v0) };
        _faces.Add(new Face
        {
            Points = points,
            Crop = crop,
            Texture = texture,
            NX = 0,
            NY = 0,
            NZ = nz,
            UVs = uvs
        });
    }

    private void AddBox(Point3D center, double w, double h, double d, Rect[] crops, TextureData texture)
    {
        var hw = w / 2;
        var hh = h / 2;
        var hd = d / 2;

        void AddFace(Point3D[] points, Rect crop, TextureData tex)
        {
            if (crop.Width <= 0 || crop.Height <= 0) return;
            var mx = points.Average(p => p.X) - center.X;
            var my = points.Average(p => p.Y) - center.Y;
            var mz = points.Average(p => p.Z) - center.Z;
            var len = Math.Sqrt(mx * mx + my * my + mz * mz);
            if (len < 0.0001) return;
            var u0 = crop.X / texture.Width;
            var u1 = crop.Right / texture.Width;
            var v0 = crop.Y / texture.Height;
            var v1 = crop.Bottom / texture.Height;
            var uvs = mz < -0.001
                ? new[] { (u1, v1), (u0, v1), (u0, v0), (u1, v0) }
                : new[] { (u0, v1), (u1, v1), (u1, v0), (u0, v0) };
            _faces.Add(new Face
            {
                Points = points,
                Crop = crop,
                Texture = tex,
                NX = mx / len,
                NY = my / len,
                NZ = mz / len,
                UVs = uvs
            });
        }

        AddFace(new[]
        {
            new Point3D(center.X - hw, center.Y - hh, center.Z + hd),
            new Point3D(center.X + hw, center.Y - hh, center.Z + hd),
            new Point3D(center.X + hw, center.Y + hh, center.Z + hd),
            new Point3D(center.X - hw, center.Y + hh, center.Z + hd)
        }, crops[0], texture);
        AddFace(new[]
        {
            new Point3D(center.X - hw, center.Y - hh, center.Z - hd),
            new Point3D(center.X + hw, center.Y - hh, center.Z - hd),
            new Point3D(center.X + hw, center.Y + hh, center.Z - hd),
            new Point3D(center.X - hw, center.Y + hh, center.Z - hd)
        }, crops[1], texture);
        AddFace(new[]
        {
            new Point3D(center.X + hw, center.Y - hh, center.Z + hd),
            new Point3D(center.X + hw, center.Y - hh, center.Z - hd),
            new Point3D(center.X + hw, center.Y + hh, center.Z - hd),
            new Point3D(center.X + hw, center.Y + hh, center.Z + hd)
        }, crops[2], texture);
        AddFace(new[]
        {
            new Point3D(center.X - hw, center.Y - hh, center.Z - hd),
            new Point3D(center.X - hw, center.Y - hh, center.Z + hd),
            new Point3D(center.X - hw, center.Y + hh, center.Z + hd),
            new Point3D(center.X - hw, center.Y + hh, center.Z - hd)
        }, crops[3], texture);
        AddFace(new[]
        {
            new Point3D(center.X - hw, center.Y + hh, center.Z + hd),
            new Point3D(center.X + hw, center.Y + hh, center.Z + hd),
            new Point3D(center.X + hw, center.Y + hh, center.Z - hd),
            new Point3D(center.X - hw, center.Y + hh, center.Z - hd)
        }, crops[4], texture);
        AddFace(new[]
        {
            new Point3D(center.X - hw, center.Y - hh, center.Z - hd),
            new Point3D(center.X + hw, center.Y - hh, center.Z - hd),
            new Point3D(center.X + hw, center.Y - hh, center.Z + hd),
            new Point3D(center.X - hw, center.Y - hh, center.Z + hd)
        }, crops[5], texture);
    }

    private void AddFaceRaw(Point3D[] points, Rect crop, TextureData texture)
    {
        if (crop.Width <= 0 || crop.Height <= 0) return;
        _faces.Add(new Face { Points = points, Crop = crop, Texture = texture });
    }

    private void Render()
    {
        var width = (int)Math.Round(ActualWidth);
        var height = (int)Math.Round(ActualHeight);
        if (width < 8 || height < 8) return;

        var pixels = new byte[width * height * 4];
        var depth = new float[width * height];
        Array.Fill(depth, float.MaxValue);
        var radX = _angleX * Math.PI / 180;
        var radY = _angleY * Math.PI / 180;
        var cosX = Math.Cos(radX);
        var sinX = Math.Sin(radX);
        var cosY = Math.Cos(radY);
        var sinY = Math.Sin(radY);
        var focal = height * 1.45;
        var cx = width / 2.0;
        var cy = height / 2.0;
        const double camZ = 70;

        foreach (var face in _faces)
        {
            var mx = face.Points.Average(p => p.X);
            var my = face.Points.Average(p => p.Y);
            var mz = face.Points.Average(p => p.Z);
            var mcx = mx * cosY + mz * sinY;
            var mcz = -mx * sinY + mz * cosY;
            var mcy = my * cosX - mcz * sinX;
            mcz = my * sinX + mcz * cosX;
            var nx1 = face.NX * cosY + face.NZ * sinY;
            var nz1 = -face.NX * sinY + face.NZ * cosY;
            var ny1 = face.NY * cosX - nz1 * sinX;
            nz1 = face.NY * sinX + nz1 * cosX;
            if (nx1 * (0 - mcx) + ny1 * (-2 - mcy) + nz1 * (camZ - mcz) <= 0) continue;

            var projected = new (double X, double Y, double Z)[4];
            var uvs = face.UVs;

            var ok = true;
            for (var i = 0; i < 4; i++)
            {
                var p = face.Points[i];
                var x1 = p.X * cosY + p.Z * sinY;
                var z1 = -p.X * sinY + p.Z * cosY;
                var y1 = p.Y * cosX - z1 * sinX;
                var z2 = p.Y * sinX + z1 * cosX;
                var d = camZ - z2;
                if (d < 0.1) { ok = false; break; }
                projected[i] = (cx + x1 * focal / d, cy - (y1 + 2) * focal / d, d);
            }
            if (!ok) continue;

            var normal = Normal(face.Points[0], face.Points[1], face.Points[2]);
            var light = Math.Abs(normal.X * -0.45 + normal.Y * -0.7 + normal.Z * -0.55);
            var shade = 0.62 + 0.38 * light;

            DrawTriangle(0, 1, 2);
            DrawTriangle(0, 2, 3);

            void DrawTriangle(int a, int b, int c)
            {
                var pa = projected[a];
                var pb = projected[b];
                var pc = projected[c];
                var area = Edge(pa.X, pa.Y, pb.X, pb.Y, pc.X, pc.Y);
                if (Math.Abs(area) < 0.0001) return;
                var ua = uvs[a]; var ub = uvs[b]; var uc = uvs[c];
                if (area < 0)
                {
                    (pb, pc) = (pc, pb);
                    (ub, uc) = (uc, ub);
                    area = -area;
                }
                var minX = Math.Max(0, (int)Math.Floor(Math.Min(pa.X, Math.Min(pb.X, pc.X))));
                var maxX = Math.Min(width - 1, (int)Math.Ceiling(Math.Max(pa.X, Math.Max(pb.X, pc.X))));
                var minY = Math.Max(0, (int)Math.Floor(Math.Min(pa.Y, Math.Min(pb.Y, pc.Y))));
                var maxY = Math.Min(height - 1, (int)Math.Ceiling(Math.Max(pa.Y, Math.Max(pb.Y, pc.Y))));
                for (var y = minY; y <= maxY; y++)
                for (var x = minX; x <= maxX; x++)
                {
                    var px = x + 0.5;
                    var py = y + 0.5;
                    var w0 = Edge(pb.X, pb.Y, pc.X, pc.Y, px, py) / area;
                    var w1 = Edge(pc.X, pc.Y, pa.X, pa.Y, px, py) / area;
                    var w2 = 1 - w0 - w1;
                    if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                    var z = pa.Z * w0 + pb.Z * w1 + pc.Z * w2;
                    var index = y * width + x;
                    if (z >= depth[index]) continue;
                    var u = ua.U * w0 + ub.U * w1 + uc.U * w2;
                    var v = ua.V * w0 + ub.V * w1 + uc.V * w2;
                    var minTx = (int)Math.Floor(face.Crop.X);
                    var maxTx = Math.Max(minTx, (int)Math.Ceiling(face.Crop.Right) - 1);
                    var minTy = (int)Math.Floor(face.Crop.Y);
                    var maxTy = Math.Max(minTy, (int)Math.Ceiling(face.Crop.Bottom) - 1);
                    var tx = Math.Clamp((int)(u * face.Texture.Width), minTx, Math.Min(maxTx, face.Texture.Width - 1));
                    var ty = Math.Clamp((int)(v * face.Texture.Height), minTy, Math.Min(maxTy, face.Texture.Height - 1));
                    var ti = (ty * face.Texture.Width + tx) * 4;
                    var alpha = face.Texture.Pixels[ti + 3] / 255.0;
                    if (alpha <= 0.01) continue;
                    depth[index] = (float)z;
                    var di = index * 4;
                    var srcR = face.Texture.Pixels[ti] * shade;
                    var srcG = face.Texture.Pixels[ti + 1] * shade;
                    var srcB = face.Texture.Pixels[ti + 2] * shade;
                    var oldA = pixels[di + 3] / 255.0;
                    var outA = alpha + oldA * (1 - alpha);
                    if (outA <= 0.001) continue;
                    pixels[di] = (byte)Math.Min(255, (srcR * alpha + pixels[di] * oldA * (1 - alpha)) / outA);
                    pixels[di + 1] = (byte)Math.Min(255, (srcG * alpha + pixels[di + 1] * oldA * (1 - alpha)) / outA);
                    pixels[di + 2] = (byte)Math.Min(255, (srcB * alpha + pixels[di + 2] * oldA * (1 - alpha)) / outA);
                    pixels[di + 3] = (byte)Math.Min(255, outA * 255);
                }
            }
        }

        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        PreviewImage.Source = bitmap;
    }

    private static double Edge(double ax, double ay, double bx, double by, double px, double py) =>
        (px - ax) * (by - ay) - (py - ay) * (bx - ax);

    private static Point3D Normal(Point3D a, Point3D b, Point3D c)
    {
        var ux = b.X - a.X; var uy = b.Y - a.Y; var uz = b.Z - a.Z;
        var vx = c.X - a.X; var vy = c.Y - a.Y; var vz = c.Z - a.Z;
        var nx = uy * vz - uz * vy;
        var ny = uz * vx - ux * vz;
        var nz = ux * vy - uy * vx;
        var len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        return len < 0.0001 ? new Point3D(0, 0, 1) : new Point3D(nx / len, ny / len, nz / len);
    }

    private static TextureData PrepareTexture(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var pixels = new byte[width * height * 4];
        converted.CopyPixels(pixels, width * 4, 0);
        if (width >= 64 && height >= 64) { /* 保持原始贴图，不把外层烘进基础层 */ }
        return new TextureData { Pixels = pixels, Width = width, Height = height };
    }

    private static void BakeOverlays(byte[] pixels, int width, int height)
    {
        Overlay(pixels, width, height, 40, 8, 8, 8, 8, 8);
        Overlay(pixels, width, height, 56, 8, 24, 8, 8, 8);
        Overlay(pixels, width, height, 32, 8, 0, 8, 8, 8);
        Overlay(pixels, width, height, 48, 8, 16, 8, 8, 8);
        Overlay(pixels, width, height, 40, 0, 8, 0, 8, 8);

        Overlay(pixels, width, height, 20, 36, 20, 20, 8, 12);
        Overlay(pixels, width, height, 32, 36, 32, 20, 8, 12);
        Overlay(pixels, width, height, 16, 36, 16, 20, 4, 12);
        Overlay(pixels, width, height, 28, 36, 28, 20, 4, 12);
        Overlay(pixels, width, height, 20, 32, 20, 16, 8, 4);
        Overlay(pixels, width, height, 28, 32, 28, 16, 8, 4);

        Overlay(pixels, width, height, 44, 36, 44, 20, 4, 12);
        Overlay(pixels, width, height, 52, 36, 44, 32, 4, 12);
        Overlay(pixels, width, height, 40, 36, 40, 20, 4, 12);
        Overlay(pixels, width, height, 48, 36, 48, 20, 4, 12);
        Overlay(pixels, width, height, 44, 32, 44, 16, 4, 4);
        Overlay(pixels, width, height, 48, 32, 48, 16, 4, 4);

        Overlay(pixels, width, height, 4, 36, 4, 20, 4, 12);
        Overlay(pixels, width, height, 12, 36, 12, 20, 4, 12);
        Overlay(pixels, width, height, 0, 36, 0, 20, 4, 12);
        Overlay(pixels, width, height, 8, 36, 8, 20, 4, 12);
        Overlay(pixels, width, height, 4, 32, 4, 16, 4, 4);
        Overlay(pixels, width, height, 8, 32, 8, 16, 4, 4);

        Overlay(pixels, width, height, 52, 52, 36, 52, 4, 12);
        Overlay(pixels, width, height, 60, 52, 44, 52, 4, 12);
        Overlay(pixels, width, height, 48, 52, 32, 52, 4, 12);
        Overlay(pixels, width, height, 56, 52, 40, 52, 4, 12);
        Overlay(pixels, width, height, 52, 48, 36, 48, 4, 4);
        Overlay(pixels, width, height, 56, 48, 40, 48, 4, 4);

        Overlay(pixels, width, height, 4, 52, 20, 52, 4, 12);
        Overlay(pixels, width, height, 12, 52, 28, 52, 4, 12);
        Overlay(pixels, width, height, 0, 52, 16, 52, 4, 12);
        Overlay(pixels, width, height, 8, 52, 24, 52, 4, 12);
        Overlay(pixels, width, height, 4, 48, 20, 48, 4, 4);
        Overlay(pixels, width, height, 8, 48, 24, 48, 4, 4);
    }

    private static void Overlay(byte[] pixels, int width, int height,
        int sx, int sy, int dx, int dy, int w, int h)
    {
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var srcX = sx + x;
            var srcY = sy + y;
            var dstX = dx + x;
            var dstY = dy + y;
            if (srcX < 0 || srcY < 0 || srcX >= width || srcY >= height) continue;
            if (dstX < 0 || dstY < 0 || dstX >= width || dstY >= height) continue;
            var si = (srcY * width + srcX) * 4;
            var di = (dstY * width + dstX) * 4;
            var sa = pixels[si + 3] / 255.0;
            if (sa <= 0) continue;
            var da = pixels[di + 3] / 255.0;
            var oa = sa + da * (1 - sa);
            if (oa <= 0) continue;
            pixels[di] = (byte)((pixels[si] * sa + pixels[di] * da * (1 - sa)) / oa);
            pixels[di + 1] = (byte)((pixels[si + 1] * sa + pixels[di + 1] * da * (1 - sa)) / oa);
            pixels[di + 2] = (byte)((pixels[si + 2] * sa + pixels[di + 2] * da * (1 - sa)) / oa);
            pixels[di + 3] = (byte)(oa * 255);
        }
    }

    private static BitmapSource CreateDefaultSkin() => CreateDefaultSkinCore();

    private static BitmapSource CreateDefaultSkinCore()
    {
        const int size = 64;
        var pixels = new byte[size * size * 4];
        void Fill(int x, int y, int w, int h, byte r, byte g, byte b)
        {
            for (var yy = y; yy < y + h; yy++)
            for (var xx = x; xx < x + w; xx++)
            {
                if (xx < 0 || yy < 0 || xx >= size || yy >= size) continue;
                var i = (yy * size + xx) * 4;
                pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = 255;
            }
        }
        Fill(8, 8, 8, 8, 198, 134, 66);
        Fill(8, 0, 8, 8, 58, 38, 24);
        Fill(20, 20, 8, 12, 44, 122, 182);
        Fill(44, 20, 4, 12, 198, 134, 66);
        Fill(36, 52, 4, 12, 198, 134, 66);
        Fill(4, 20, 4, 12, 46, 58, 92);
        Fill(20, 52, 4, 12, 46, 58, 92);
        Fill(0, 8, 8, 8, 58, 38, 24);
        Fill(16, 8, 8, 8, 58, 38, 24);
        Fill(24, 8, 8, 8, 58, 38, 24);
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
