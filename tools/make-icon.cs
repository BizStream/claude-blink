// Regenerates ClaudeBlink/app.ico (the exe, Start menu and installer icon) from tools/icon-source.jpg.
// Run from the repo root:  dotnet run tools/make-icon.cs [-- preview.png]
// The source is a square artwork of a rounded tile on a white background; the white corners are cut away
// with an anti-aliased rounded mask, found by measuring the tile in the image.
#:package System.Drawing.Common@10.0.0
#:property TargetFramework=net10.0-windows
#:property PublishAot=false

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

int[] sizes = [16, 20, 24, 32, 40, 48, 64, 256];

using var source = new Bitmap(Path.Combine("tools", "icon-source.jpg"));
using var master = MaskTile(source);
var pngs = sizes.Select(s => (s, Png(Scale(master, s)))).ToList();

string outPath = Path.Combine("ClaudeBlink", "app.ico");
using (var f = File.Create(outPath))
using (var w = new BinaryWriter(f))
{
    // ICONDIR, then one ICONDIRENTRY per size, then the PNG images.
    w.Write((short)0); w.Write((short)1); w.Write((short)pngs.Count);
    int offset = 6 + 16 * pngs.Count;
    foreach (var (s, png) in pngs)
    {
        w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
        w.Write((byte)0); w.Write((byte)0);
        w.Write((short)1); w.Write((short)32);
        w.Write(png.Length); w.Write(offset);
        offset += png.Length;
    }
    foreach (var (_, png) in pngs) w.Write(png);
}
Console.WriteLine($"Wrote {outPath}");

// Optional: every size side by side on light and dark.
if (args.Length > 0)
{
    int total = sizes.Sum() + 12 * sizes.Length + 12;
    using var sheet = new Bitmap(total, 2 * (256 + 24));
    using var g = Graphics.FromImage(sheet);
    g.Clear(Color.FromArgb(0xF3, 0xF3, 0xF3));
    using (var dark = new SolidBrush(Color.FromArgb(0x20, 0x20, 0x20))) g.FillRectangle(dark, 0, 256 + 24, total, 256 + 24);
    int x = 12;
    foreach (int s in sizes)
    {
        using var a = Scale(master, s);
        g.DrawImageUnscaled(a, x, 12);
        g.DrawImageUnscaled(a, x, 256 + 24 + 12);
        x += s + 12;
    }
    sheet.Save(args[0], ImageFormat.Png);
}

static bool IsTile(Color c) => c.R + c.G + c.B < 3 * 200;

// Crop to the tile and make everything outside its rounded corners transparent.
static Bitmap MaskTile(Bitmap src)
{
    int midY = src.Height / 2, midX = src.Width / 2;
    int left = 0; while (!IsTile(src.GetPixel(left, midY))) left++;
    int right = src.Width - 1; while (!IsTile(src.GetPixel(right, midY))) right--;
    int top = 0; while (!IsTile(src.GetPixel(midX, top))) top++;
    int bottom = src.Height - 1; while (!IsTile(src.GetPixel(midX, bottom))) bottom--;

    // Along the diagonal, the corner arc sits r(1 - 1/sqrt 2) in from the corner.
    int d = 0; while (!IsTile(src.GetPixel(left + d, top + d))) d++;
    float radius = d / (1 - 1 / MathF.Sqrt(2));

    const float inset = 3; // skip the jpeg's light fringe along the edge
    var tile = RectangleF.FromLTRB(left + inset, top + inset, right + 1 - inset, bottom + 1 - inset);
    int side = (int)MathF.Ceiling(Math.Max(tile.Width, tile.Height));

    var bmp = new Bitmap(side, side, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.Clear(Color.Transparent);
    using var brush = new TextureBrush(src);
    float ox = (side - tile.Width) / 2 - tile.X, oy = (side - tile.Height) / 2 - tile.Y;
    brush.TranslateTransform(ox, oy);
    using var path = RoundRect(new RectangleF(tile.X + ox, tile.Y + oy, tile.Width, tile.Height), radius - inset);
    g.FillPath(brush, path);
    Console.WriteLine($"Tile {left},{top}-{right},{bottom}, corner radius {radius:0}px");
    return bmp;
}

// Downscale in halving steps so small sizes stay smooth rather than aliased.
static Bitmap Scale(Bitmap src, int size)
{
    Bitmap current = src;
    while (current.Width / 2 >= size * 2)
    {
        var half = Resize(current, current.Width / 2);
        if (current != src) current.Dispose();
        current = half;
    }
    var result = Resize(current, size);
    if (current != src) current.Dispose();
    return result;
}

static Bitmap Resize(Bitmap src, int size)
{
    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.Clear(Color.Transparent);
    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.CompositingQuality = CompositingQuality.HighQuality;
    using var attrs = new ImageAttributes();
    attrs.SetWrapMode(WrapMode.TileFlipXY); // no dark halo from sampling past the edge
    g.DrawImage(src, new Rectangle(0, 0, size, size), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
    return bmp;
}

static GraphicsPath RoundRect(RectangleF r, float radius)
{
    float d = radius * 2;
    var p = new GraphicsPath();
    p.AddArc(r.X, r.Y, d, d, 180, 90);
    p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
    p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
    p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
    p.CloseFigure();
    return p;
}

static byte[] Png(Bitmap bmp)
{
    using (bmp)
    using (var ms = new MemoryStream())
    {
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}
