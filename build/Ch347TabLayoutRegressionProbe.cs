using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Compile this helper against the WPF assemblies and invoke Run after arranging
// the offscreen page. It only inspects existing visuals; it never opens a device.
public static class Ch347TabLayoutRegressionProbe
{
    public static string[] Run(object page, double dpiScale)
    {
        if (dpiScale <= 0 || double.IsInfinity(dpiScale) || double.IsNaN(dpiScale))
            throw new ArgumentOutOfRangeException("dpiScale");

        FrameworkElement owner = page as FrameworkElement;
        if (owner == null) throw new ArgumentException("Expected a WPF page.", "page");
        TabControl tabs = owner.FindName("ProtocolTabs") as TabControl;
        if (tabs == null) throw new InvalidOperationException("ProtocolTabs was not found.");
        tabs.ApplyTemplate();

        FrameworkElement root = FindRoot(tabs);
        if (root.RenderSize.Width <= 0 || root.RenderSize.Height <= 0)
            throw new InvalidOperationException("Arrange the preview surface before checking tabs.");

        int pixelWidth = (int)Math.Ceiling(root.RenderSize.Width * dpiScale);
        int pixelHeight = (int)Math.Ceiling(root.RenderSize.Height * dpiScale);
        RenderTargetBitmap bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight,
            96 * dpiScale, 96 * dpiScale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        byte[] pixels = new byte[pixelWidth * pixelHeight * 4];
        bitmap.CopyPixels(pixels, pixelWidth * 4, 0);

        List<string> reports = new List<string>();
        List<string> failures = new List<string>();
        var tabNames = new List<string> { "I2cTab", "SpiTab" };
        if (owner.FindName("JtagTab") is TabItem) tabNames.Add("JtagTab");
        foreach (string name in tabNames)
        {
            TabItem tab = owner.FindName(name) as TabItem;
            if (tab == null) throw new InvalidOperationException(name + " was not found.");
            tab.ApplyTemplate();
            Border chrome = tab.Template.FindName("Chrome", tab) as Border;
            if (chrome == null) throw new InvalidOperationException(name + " has no Chrome border.");
            Panel host = FindItemsHost(tab);
            if (host == null) throw new InvalidOperationException(name + " has no items host.");

            Rect chromeInHost = chrome.TransformToAncestor(host)
                .TransformBounds(new Rect(chrome.RenderSize));
            Rect hostBounds = new Rect(host.RenderSize);
            Rect slot = LayoutInformation.GetLayoutSlot(tab);
            string state = name + (tab.IsSelected ? " selected" : " unselected");
            string geometry = string.Format(CultureInfo.InvariantCulture,
                "{0}: Chrome={1}; host={2} {3}; slot={4}; margin={5}",
                state, chromeInHost, host.GetType().Name, hostBounds, slot, tab.Margin);
            reports.Add(geometry);
            if (!Contains(hostBounds, chromeInHost))
                failures.Add(geometry + "; Chrome exceeds the header host.");

            Rect bottom = new Rect(chrome.RenderSize.Width * 0.3,
                Math.Max(0, chrome.RenderSize.Height - chrome.BorderThickness.Bottom + 0.01),
                chrome.RenderSize.Width * 0.4,
                Math.Max(0.01, chrome.BorderThickness.Bottom - 0.02));
            for (Visual current = chrome; current != null; current = VisualTreeHelper.GetParent(current) as Visual)
            {
                Geometry clip = VisualTreeHelper.GetClip(current);
                if (clip == null) continue;
                Rect stripInAncestor = ReferenceEquals(chrome, current) ? bottom :
                    chrome.TransformToAncestor(current).TransformBounds(bottom);
                if (clip.FillContainsWithDetail(new RectangleGeometry(stripInAncestor)) != IntersectionDetail.FullyContains)
                    failures.Add(state + ": bottom border is cut by " + current.GetType().Name +
                        " clip=" + clip.Bounds + "; border=" + stripInAncestor + ".");
            }

            Rect chromeInRoot = chrome.TransformToAncestor(root)
                .TransformBounds(new Rect(chrome.RenderSize));
            int matched;
            int sampled;
            CheckBottomPixels(pixels, pixelWidth, pixelHeight, chromeInRoot,
                chrome.BorderThickness.Bottom, dpiScale, out matched, out sampled);
            string raster = string.Format(CultureInfo.InvariantCulture,
                "{0}: visible bottom border in {1}/{2} central pixel columns at {3:0.##}% raster scale.",
                state, matched, sampled, dpiScale * 100);
            reports.Add(raster);
            if (sampled == 0 || matched < sampled * 0.9)
                failures.Add(raster + " Expected at least 90%.");
        }

        if (failures.Count != 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, failures.ToArray()));
        return reports.ToArray();
    }

    private static FrameworkElement FindRoot(Visual visual)
    {
        Visual root = visual;
        for (Visual parent = VisualTreeHelper.GetParent(root) as Visual; parent != null;
            parent = VisualTreeHelper.GetParent(root) as Visual)
            root = parent;
        FrameworkElement result = root as FrameworkElement;
        if (result == null) throw new InvalidOperationException("The preview root must be a FrameworkElement.");
        return result;
    }

    private static Panel FindItemsHost(Visual visual)
    {
        for (Visual current = VisualTreeHelper.GetParent(visual) as Visual; current != null;
            current = VisualTreeHelper.GetParent(current) as Visual)
        {
            Panel panel = current as Panel;
            if (panel != null && panel.IsItemsHost) return panel;
        }
        return null;
    }

    private static bool Contains(Rect outer, Rect inner)
    {
        const double tolerance = 0.05;
        outer.Inflate(tolerance, tolerance);
        return outer.Contains(inner);
    }

    private static void CheckBottomPixels(byte[] pixels, int width, int height, Rect chrome,
        double thickness, double scale, out int matched, out int sampled)
    {
        // Sample only the straight middle of the border, away from the rounded
        // corners and header text. A border pixel must differ from both the
        // inner fill and the adjacent tab-strip fill. A clipped edge fails even
        // when the normal border is translucent or the selected brush changes.
        int firstX = Math.Max(0, (int)Math.Ceiling((chrome.X + chrome.Width * 0.3) * scale));
        int lastX = Math.Min(width - 1, (int)Math.Floor((chrome.X + chrome.Width * 0.7) * scale));
        int firstY = Math.Max(0, (int)Math.Floor((chrome.Bottom - thickness) * scale));
        int lastY = Math.Min(height - 1, (int)Math.Ceiling(chrome.Bottom * scale) - 1);
        int insideY = (int)Math.Floor((chrome.Bottom - thickness - 2) * scale);
        int outsideY = (int)Math.Ceiling(chrome.Bottom * scale) + 1;
        matched = 0;
        sampled = 0;
        if (insideY < 0 || outsideY >= height || thickness <= 0) return;
        for (int x = firstX; x <= lastX; x++)
        {
            sampled++;
            for (int y = firstY; y <= lastY; y++)
            {
                if (Difference(pixels, width, x, y, insideY) >= 8 &&
                    Difference(pixels, width, x, y, outsideY) >= 8)
                {
                    matched++;
                    break;
                }
            }
        }
    }

    private static int Difference(byte[] pixels, int width, int x, int firstY, int secondY)
    {
        int first = (firstY * width + x) * 4;
        int second = (secondY * width + x) * 4;
        int difference = 0;
        for (int channel = 0; channel < 3; channel++)
            difference = Math.Max(difference, Math.Abs(pixels[first + channel] - pixels[second + channel]));
        return difference;
    }
}
