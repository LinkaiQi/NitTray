using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace DisplayDial.Services;

internal static class IconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static Icon CreateTrayIcon(int size = 32)
    {
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Soft yellow sun body.
            var bodyRect = new RectangleF(size * 0.30f, size * 0.30f, size * 0.40f, size * 0.40f);
            using var body = new SolidBrush(Color.FromArgb(255, 255, 196, 35));
            g.FillEllipse(body, bodyRect);

            // Eight rays around the body.
            using var rayPen = new Pen(Color.FromArgb(255, 255, 196, 35), Math.Max(2f, size / 16f))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };

            var cx = size / 2f;
            var cy = size / 2f;
            var innerRadius = size * 0.27f;
            var outerRadius = size * 0.46f;

            for (var i = 0; i < 8; i++)
            {
                var theta = i * (Math.PI / 4.0);
                var ix = cx + innerRadius * (float)Math.Cos(theta);
                var iy = cy + innerRadius * (float)Math.Sin(theta);
                var ox = cx + outerRadius * (float)Math.Cos(theta);
                var oy = cy + outerRadius * (float)Math.Sin(theta);
                g.DrawLine(rayPen, ix, iy, ox, oy);
            }
        }

        var hIcon = bitmap.GetHicon();
        try
        {
            using var sourceIcon = Icon.FromHandle(hIcon);
            return (Icon)sourceIcon.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }
}
