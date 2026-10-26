namespace Poe2Verlay;

/// <summary>Look shared by the inventory and stash windows: dark, game-like, owner-drawn list headers.</summary>
internal static class DarkUi
{
    public static readonly Color Back = Color.FromArgb(22, 20, 18);
    public static readonly Color Panel = Color.FromArgb(34, 31, 27);
    public static readonly Color Header = Color.FromArgb(44, 40, 35);
    public static readonly Color Text = Color.FromArgb(228, 224, 216);
    public static readonly Color Dim = Color.FromArgb(150, 146, 138);
    public static readonly Color Gold = Color.FromArgb(240, 200, 90);
    public static readonly Color Up = Color.FromArgb(120, 215, 120);
    public static readonly Color Down = Color.FromArgb(235, 120, 110);
    public static readonly Color Selected = Color.FromArgb(72, 62, 44);

    public static void StyleWindow(Form form, string title)
    {
        form.Text = title;
        form.BackColor = Back;
        form.ForeColor = Text;
        form.Font = new Font("Segoe UI", 9.5f);
        form.StartPosition = FormStartPosition.Manual;
        form.TopMost = true;
        form.ShowInTaskbar = true;
        form.KeyPreview = true;
        try { form.Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { /* default icon */ }
    }

    public static ListView List()
    {
        var list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            BorderStyle = BorderStyle.None,
            BackColor = Back,
            ForeColor = Text,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            OwnerDraw = true,
            ShowItemToolTips = true,
        };
        list.DrawColumnHeader += (_, e) =>
        {
            using var bg = new SolidBrush(Header);
            e.Graphics.FillRectangle(bg, e.Bounds);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                        (e.Header?.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text, list.Font, Rectangle.Inflate(e.Bounds, -6, 0), Gold, flags);
        };
        // Selected rows: the system highlight is near-white, draw a dark one instead.
        list.DrawItem += (_, e) => e.DrawDefault = !e.Item.Selected;
        list.DrawSubItem += (_, e) =>
        {
            if (e.Item is not { Selected: true } || e.SubItem == null)
            {
                e.DrawDefault = true;
                return;
            }
            using var bg = new SolidBrush(Selected);
            e.Graphics.FillRectangle(bg, e.Bounds);
            var right = list.Columns[e.ColumnIndex].TextAlign == HorizontalAlignment.Right;
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                        (right ? TextFormatFlags.Right : TextFormatFlags.Left);
            TextRenderer.DrawText(e.Graphics, e.SubItem.Text, list.Font, Rectangle.Inflate(e.Bounds, -4, 0), e.SubItem.ForeColor, flags);
        };
        // Dark scroll bars (Windows 10 1809+; older ones keep the light theme).
        list.HandleCreated += (_, _) => Native.SetWindowTheme(list.Handle, "DarkMode_Explorer", null);
        // -2 = LVSCW_AUTOSIZE_USEHEADER: the last column takes exactly the remaining width.
        list.Resize += (_, _) => FillLastColumn(list);
        return list;
    }

    public static void FillLastColumn(ListView list)
    {
        if (list.Columns.Count > 0) list.Columns[^1].Width = -2;
    }

    public static void Columns(ListView list, params (string Text, int Width, bool Right)[] columns)
    {
        list.Columns.Clear();
        foreach (var (text, width, right) in columns)
            list.Columns.Add(text, list.LogicalToDeviceUnits(width), right ? HorizontalAlignment.Right : HorizontalAlignment.Left);
    }

    public static Button Button(string text)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Text,
            BackColor = Panel,
            Margin = new Padding(0, 0, 4, 0),
            Padding = new Padding(10, 3, 10, 3),
            Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderColor = Header;
        return b;
    }

    public static void SetActive(Button b, bool active)
    {
        b.BackColor = active ? Header : Back;
        b.ForeColor = active ? Gold : Dim;
        b.FlatAppearance.BorderColor = active ? Gold : Header;
    }

    public static Label Summary()
    {
        var label = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 74,
            BackColor = Panel,
            ForeColor = Text,
            Padding = new Padding(10, 6, 10, 6),
            Font = new Font("Segoe UI", 10f),
        };
        label.Resize += (_, _) => FitHeight(label);
        label.TextChanged += (_, _) => FitHeight(label);
        return label;
    }

    static void FitHeight(Label label)
    {
        var inner = Math.Max(50, label.ClientSize.Width - label.Padding.Horizontal);
        var size = TextRenderer.MeasureText(label.Text, label.Font, new Size(inner, int.MaxValue), TextFormatFlags.WordBreak);
        var height = size.Height + label.Padding.Vertical + 4;
        if (label.Height != height) label.Height = height;
    }

    public static ListViewItem Row(params string[] cells)
    {
        var item = new ListViewItem(cells[0]) { UseItemStyleForSubItems = false, ToolTipText = cells[0] };
        foreach (var c in cells.Skip(1)) item.SubItems.Add(c);
        return item;
    }

    public static void Tint(ListViewItem row, Trade trade)
    {
        var c = trade switch
        {
            Trade.Low or Trade.FewListings => Color.FromArgb(255, 170, 70),
            Trade.VeryLow => Color.FromArgb(240, 95, 95),
            Trade.Random => Color.FromArgb(150, 170, 210),
            Trade.NoPrice => Dim,
            _ => Text,
        };
        foreach (ListViewItem.ListViewSubItem sub in row.SubItems)
        {
            sub.ForeColor = c;
            sub.BackColor = Back;
        }
    }

    /// <summary>Name, plus base/class in brackets for things without a price.</summary>
    public static string ItemName(string name, string? detail, bool priced) =>
        !priced && detail != null && detail != name ? $"{name}  ({detail})" : name;

    public static void PlaceWindow(Form form, string saved, int x, int y, int w, int h)
    {
        var parts = saved.Split(',');
        if (parts.Length == 4 && parts.All(p => int.TryParse(p, out _)))
        {
            var r = new Rectangle(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]));
            if (Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(r)))
            {
                form.Bounds = r;
                return;
            }
        }
        var screen = Screen.PrimaryScreen!.WorkingArea;
        form.Bounds = new Rectangle(screen.Left + form.LogicalToDeviceUnits(x), screen.Top + form.LogicalToDeviceUnits(y),
            form.LogicalToDeviceUnits(w), Math.Min(form.LogicalToDeviceUnits(h), screen.Height - 140));
    }
}
