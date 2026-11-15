using System.Drawing;
using System.Windows.Forms;

namespace Beg1nnnerOS.Desktop;

/// <summary>Меню значка в трее в цветах текущей темы панели (вместо системного).</summary>
public sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    private static Color H(string hex) => ColorTranslator.FromHtml(hex);
    public static Color Bg => H(ChromeTheme.Current.Panel);
    public static Color Fg => H(ChromeTheme.Current.Fg);
    public static Color Muted => H(ChromeTheme.Current.Muted);
    public static Color Hover => H(ChromeTheme.Mix(ChromeTheme.Current.Panel, ChromeTheme.Current.Accent, ChromeTheme.Current.Light ? 0.10 : 0.16));
    public static Color Line => H(ChromeTheme.Mix(ChromeTheme.Current.Panel, ChromeTheme.Current.Fg, 0.12));
    public static Color Accent => H(ChromeTheme.Current.Accent);

    public DarkMenuRenderer() : base(new Palette()) { RoundedEdges = false; }

    /// <summary>Применяет оформление к меню и всем пунктам (вызывать заново при смене темы).</summary>
    public static void Apply(ContextMenuStrip menu)
    {
        menu.Renderer = new DarkMenuRenderer();
        menu.BackColor = Bg;
        menu.ForeColor = Fg;
        menu.ShowImageMargin = true;
        menu.Padding = new Padding(4, 6, 4, 6);
        if (menu.Font.Name != "Segoe UI") menu.Font = new Font("Segoe UI", 9.75f);
        foreach (ToolStripItem it in menu.Items)
        {
            it.ForeColor = Fg;
            if (it is ToolStripMenuItem) it.Padding = new Padding(4, 5, 4, 5);
        }
        menu.Invalidate();
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Fg : Muted;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var r = new Rectangle(2, 0, e.Item.Width - 4, e.Item.Height);
        using var b = new SolidBrush(e.Item.Selected && e.Item.Enabled ? Hover : Bg);
        e.Graphics.FillRectangle(b, r);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.Height / 2;
        using var p = new Pen(Line);
        e.Graphics.DrawLine(p, 8, y, e.Item.Width - 8, y);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var r = e.ImageRectangle;
        using var p = new Pen(Accent, 2f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var cx = r.Left + r.Width / 2f; var cy = r.Top + r.Height / 2f;
        e.Graphics.DrawLines(p, new[] { new PointF(cx - 5, cy), new PointF(cx - 1.5f, cy + 3.5f), new PointF(cx + 5, cy - 4) });
    }

    private sealed class Palette : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Bg;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Line;
        public override Color CheckBackground => Bg;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
    }
}
