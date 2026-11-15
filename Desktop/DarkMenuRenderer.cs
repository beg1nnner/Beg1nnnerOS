using System.Drawing;
using System.Windows.Forms;

namespace Beg1nnnerOS.Desktop;

/// <summary>Тёмное меню значка в трее в цветах панели (вместо светлого системного).</summary>
public sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public static readonly Color Bg = Color.FromArgb(0x11, 0x11, 0x16);
    public static readonly Color Fg = Color.FromArgb(0xF4, 0xF4, 0xF7);
    public static readonly Color Muted = Color.FromArgb(0x9D, 0x9D, 0xAE);
    public static readonly Color Hover = Color.FromArgb(0x24, 0x22, 0x33);
    public static readonly Color Line = Color.FromArgb(0x26, 0x26, 0x31);
    public static readonly Color Accent = Color.FromArgb(0x8B, 0x5C, 0xF6);

    public DarkMenuRenderer() : base(new Palette()) { RoundedEdges = false; }

    /// <summary>Применяет оформление к меню и всем пунктам.</summary>
    public static void Apply(ContextMenuStrip menu)
    {
        menu.Renderer = new DarkMenuRenderer();
        menu.BackColor = Bg;
        menu.ForeColor = Fg;
        menu.ShowImageMargin = true;
        menu.Padding = new Padding(4, 6, 4, 6);
        menu.Font = new Font("Segoe UI", 9.75f);
        foreach (ToolStripItem it in menu.Items)
        {
            it.ForeColor = Fg;
            if (it is ToolStripMenuItem) it.Padding = new Padding(4, 5, 4, 5);
        }
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
        // галочка — акцентным цветом
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
