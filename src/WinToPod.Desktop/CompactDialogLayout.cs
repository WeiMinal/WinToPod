// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinToPod.Desktop;

// Measure text at its final width before choosing a row/group height. Native
// GroupBox AutoSize + a docked wrapping TableLayoutPanel can measure at a stale
// width and then clip the second line. No fixed-height text containers here.
internal sealed class CompactColumn : Panel
{
    private bool _layingOut;
    public CompactColumn() { Margin = Padding.Empty; Padding = Padding.Empty; DoubleBuffered = true; }
    private int S(int value) => (int)Math.Ceiling(value * DeviceDpi / 96d);
    private int ContentWidth(int proposed) => Math.Max(S(80), (proposed > 0 && proposed < 1_000_000 ? proposed : Math.Max(Width, S(200))) - Padding.Horizontal);
    private Size Measure(Control child, int width)
    {
        var available = Math.Max(1, width - child.Margin.Horizontal);
        if (child is Label label)
        {
            var measured = TextRenderer.MeasureText(label.Text.Length == 0 ? " " : label.Text, label.Font,
                new Size(Math.Max(1, available - label.Padding.Horizontal - S(2)), int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);
            return new(available, Math.Max(label.MinimumSize.Height, measured.Height + label.Padding.Vertical + S(3)));
        }
        var preferred = child.GetPreferredSize(new(available, 0));
        if (child is CheckBox or RadioButton)
        {
            var text = TextRenderer.MeasureText(child.Text, child.Font, new Size(Math.Max(1, available - S(22)), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            return new(available, Math.Max(child.MinimumSize.Height, Math.Max(S(16), text.Height + S(3))));
        }
        return new(child is Button ? Math.Min(available, preferred.Width) : available, Math.Max(child.MinimumSize.Height, preferred.Height));
    }
    private bool Participates(Control child) => child.Visible || !Visible;
    public override Size GetPreferredSize(Size proposedSize)
    {
        var width = ContentWidth(proposedSize.Width); var height = Padding.Vertical;
        foreach (Control child in Controls) if (Participates(child)) height += Measure(child, width).Height + child.Margin.Vertical;
        return new(width + Padding.Horizontal, height);
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        if (_layingOut) return; _layingOut = true;
        try
        {
            var width = ContentWidth(Width); var y = Padding.Top;
            foreach (Control child in Controls)
            {
                if (!Participates(child)) continue;
                var size = Measure(child, width); y += child.Margin.Top;
                child.SetBounds(Padding.Left + child.Margin.Left, y, size.Width, size.Height); y += size.Height + child.Margin.Bottom;
            }
            var desired = y + Padding.Bottom;
            if (Height != desired) Height = desired;
        }
        finally { _layingOut = false; }
        base.OnLayout(e);
    }
    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        e.Control!.Dock = DockStyle.None; e.Control.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        e.Control.TextChanged += (_, _) => Relayout();
        e.Control.FontChanged += (_, _) => Relayout();
        e.Control.VisibleChanged += (_, _) => Relayout();
    }
    private void Relayout()
    {
        if (_layingOut || IsDisposed) return;
        Control top = this;
        while (top.Parent is CompactColumn or CompactGroup) top = top.Parent;
        top.PerformLayout();
    }
}

internal sealed class CompactGroup : GroupBox
{
    public CompactColumn Content { get; } = new();
    public CompactGroup() { Padding = new Padding(10, 5, 10, 8); Controls.Add(Content); }
    private int HeaderHeight => Font.Height + Padding.Top + (int)Math.Ceiling(3 * DeviceDpi / 96d);
    public override Size GetPreferredSize(Size proposedSize)
    {
        if (Content is null) return base.GetPreferredSize(proposedSize);
        var width = proposedSize.Width > 0 ? proposedSize.Width : Width;
        return new(width, HeaderHeight + Content.GetPreferredSize(new(Math.Max(1, width - Padding.Horizontal), 0)).Height + Padding.Bottom);
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (Content is null) return;
        var width = Math.Max(1, ClientSize.Width - Padding.Horizontal);
        Content.SetBounds(Padding.Left, HeaderHeight, width, Content.GetPreferredSize(new(width, 0)).Height);
        Content.PerformLayout();
    }
}

internal static class CompactDialogLayout
{
    public static void Fit(Form form, TabControl? tabs = null, CompactColumn? content = null)
    {
        var fitting = false;
        void Reflow()
        {
            if (fitting || form.IsDisposed || !form.IsHandleCreated) return; fitting = true;
            try
            {
                form.PerformLayout();
                var required = 0;
                if (tabs is not null)
                {
                    var width = Math.Max(100, tabs.ClientSize.Width - 8);
                    foreach (TabPage page in tabs.TabPages)
                    {
                        var column = page.Controls.OfType<CompactColumn>().Single();
                        page.Size = tabs.DisplayRectangle.Size;
                        var inner = Math.Max(1, width - page.Padding.Horizontal);
                        column.Width = inner; column.PerformLayout();
                        required = Math.Max(required, column.GetPreferredSize(new(inner, 0)).Height + page.Padding.Vertical);
                    }
                    required += form.ClientSize.Height - Math.Max(0, tabs.DisplayRectangle.Height) + 8;
                }
                else if (content is not null) required = content.GetPreferredSize(new(form.ClientSize.Width, 0)).Height + 4;
                var work = Screen.FromControl(form).WorkingArea;
                var maxClient = Math.Max(200, work.Height - (form.Height - form.ClientSize.Height) - 16);
                if (required > form.ClientSize.Height && form.ClientSize.Height < maxClient)
                {
                    var previous = form.Height;
                    form.ClientSize = new(form.ClientSize.Width, Math.Min(required, maxClient));
                    form.Top = Math.Max(work.Top, Math.Min(work.Bottom - form.Height, form.Top - (form.Height - previous) / 2));
                }
                if (tabs is not null)
                    foreach (TabPage page in tabs.TabPages)
                    {
                        page.Size = tabs.DisplayRectangle.Size;
                        var column = page.Controls.OfType<CompactColumn>().Single(); column.PerformLayout();
                        // A very small work area gets an accessible overflow
                        // fallback instead of silently cutting off controls.
                        page.AutoScroll = column.Bottom + page.Padding.Bottom > page.ClientSize.Height;
                    }
            }
            finally { fitting = false; }
        }
        form.Load += (_, _) => Reflow(); form.Shown += (_, _) => Reflow(); form.DpiChanged += (_, _) => { if (form.IsHandleCreated) form.BeginInvoke((Action)Reflow); };
        if (tabs is not null) tabs.SelectedIndexChanged += (_, _) => Reflow();
    }
}
