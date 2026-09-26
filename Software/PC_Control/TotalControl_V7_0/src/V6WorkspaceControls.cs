using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

internal sealed class V6ModuleCard : Panel
{
    private readonly Label _state;
    private readonly Label _subtitle;
    private readonly Button _focus;
    private readonly Button _popout;
    private readonly Panel _body;
    private bool _focused;
    private bool _detached;

    public readonly V6ModuleId ModuleId;

    public event Action<V6ModuleCard> FocusRequested;
    public event Action<V6ModuleCard> PopoutRequested;
    public event Action<V6ModuleCard> HideRequested;

    public V6ModuleCard(
        V6ModuleId moduleId,
        string title,
        string subtitle,
        Control content)
    {
        ModuleId = moduleId;
        BackColor = V6Theme.Surface;
        Margin = new Padding(6);
        Padding = new Padding(1);
        BorderStyle = BorderStyle.FixedSingle;
        MinimumSize = new Size(300, 220);

        TableLayoutPanel cardShell = new TableLayoutPanel();
        cardShell.Dock = DockStyle.Fill;
        cardShell.Margin = Padding.Empty;
        cardShell.Padding = Padding.Empty;
        cardShell.ColumnCount = 1;
        cardShell.RowCount = 2;
        cardShell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        cardShell.RowStyles.Add(new RowStyle(SizeType.Absolute, 54F));
        cardShell.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        Controls.Add(cardShell);

        Panel header = new Panel();
        header.Dock = DockStyle.Fill;
        header.Margin = Padding.Empty;
        header.BackColor = Color.White;
        cardShell.Controls.Add(header, 0, 0);

        Label titleLabel = new Label();
        titleLabel.Text = title;
        titleLabel.Font = new Font(
            "Segoe UI",
            10.5F,
            FontStyle.Bold);
        titleLabel.ForeColor = V6Theme.Ink;
        titleLabel.AutoSize = true;
        titleLabel.Location = new Point(12, 7);
        header.Controls.Add(titleLabel);

        _subtitle = new Label();
        _subtitle.Text = subtitle;
        _subtitle.Font = new Font("Segoe UI", 8F);
        _subtitle.ForeColor = V6Theme.Muted;
        _subtitle.AutoEllipsis = true;
        _subtitle.Location = new Point(13, 31);
        _subtitle.Size = new Size(360, 18);
        _subtitle.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
        header.Controls.Add(_subtitle);

        Button hide = HeaderButton("Hide", 48);
        hide.Dock = DockStyle.Right;
        hide.Click += delegate
        {
            Action<V6ModuleCard> handler = HideRequested;
            if (handler != null)
                handler(this);
        };
        header.Controls.Add(hide);

        _popout = HeaderButton("Float", 48);
        _popout.Dock = DockStyle.Right;
        _popout.Click += delegate
        {
            Action<V6ModuleCard> handler = PopoutRequested;
            if (handler != null)
                handler(this);
        };
        header.Controls.Add(_popout);

        _focus = HeaderButton("Focus", 48);
        _focus.Dock = DockStyle.Right;
        _focus.Click += delegate
        {
            Action<V6ModuleCard> handler = FocusRequested;
            if (handler != null)
                handler(this);
        };
        header.Controls.Add(_focus);

        _state = new Label();
        _state.Text = "Standby";
        _state.TextAlign = ContentAlignment.MiddleCenter;
        _state.AutoSize = false;
        _state.Size = new Size(86, 24);
        _state.Location = new Point(210, 6);
        _state.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _state.BackColor = Color.FromArgb(239, 242, 241);
        _state.ForeColor = V6Theme.Gray;
        header.Controls.Add(_state);

        _body = new Panel();
        _body.Dock = DockStyle.Fill;
        _body.Margin = Padding.Empty;
        _body.BackColor = V6Theme.Surface;
        cardShell.Controls.Add(_body, 0, 1);

        if (content != null)
        {
            content.Dock = DockStyle.Fill;
            _body.Controls.Add(content);
        }

        header.Resize += delegate
        {
            _state.Left = Math.Max(
                150,
                header.ClientSize.Width - 242);
            _subtitle.Width = Math.Max(
                80,
                _state.Left - _subtitle.Left - 8);
        };
    }

    public bool IsFocused
    {
        get { return _focused; }
    }

    public bool IsDetached
    {
        get { return _detached; }
    }

    public void SetFocused(bool value)
    {
        _focused = value;
        _focus.Text = value ? "Back" : "Focus";
    }

    public void SetDetached(bool value)
    {
        _detached = value;
        _popout.Text = value ? "Dock" : "Float";
    }

    public void SetState(string text, Color color)
    {
        _state.Text = text;
        _state.ForeColor = color;
        _state.BackColor = Blend(color, Color.White, 0.86F);
    }

    public void SetSubtitle(string text)
    {
        _subtitle.Text = text ?? "";
    }

    private static Button HeaderButton(string text, int width)
    {
        Button button = new Button();
        button.Text = text;
        button.Width = width;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = Color.White;
        button.ForeColor = V6Theme.Muted;
        button.Cursor = Cursors.Hand;
        return button;
    }

    private static Color Blend(Color front, Color back, float backAmount)
    {
        float frontAmount = 1.0F - backAmount;
        return Color.FromArgb(
            (int)(front.R * frontAmount + back.R * backAmount),
            (int)(front.G * frontAmount + back.G * backAmount),
            (int)(front.B * frontAmount + back.B * backAmount));
    }
}

internal sealed class V6SignalChart : Panel
{
    private readonly object _sync = new object();
    private readonly List<double> _values = new List<double>();
    private int _capacity = 240;
    private double _threshold;

    public V6SignalChart()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(248, 251, 250);
        ForeColor = V6Theme.Green;
        MinimumSize = new Size(180, 100);
        ResizeRedraw = true;
    }

    public int Capacity
    {
        get { return _capacity; }
        set { _capacity = Math.Max(20, value); }
    }

    public double Threshold
    {
        get { return _threshold; }
        set
        {
            _threshold = Math.Max(0.0, value);
            Invalidate();
        }
    }

    public void AddValue(double value)
    {
        if (value < 0 || double.IsNaN(value) || double.IsInfinity(value))
            return;

        lock (_sync)
        {
            _values.Add(value);
            while (_values.Count > _capacity)
                _values.RemoveAt(0);
        }

        Invalidate();
    }

    public void ClearValues()
    {
        lock (_sync)
        {
            _values.Clear();
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        Rectangle plot = new Rectangle(
            58,
            12,
            Math.Max(1, ClientSize.Width - 70),
            Math.Max(1, ClientSize.Height - 34));

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(BackColor);

        using (Pen grid = new Pen(Color.FromArgb(226, 233, 230)))
        {
            for (int i = 0; i <= 4; i++)
            {
                int y = plot.Top + plot.Height * i / 4;
                e.Graphics.DrawLine(grid, plot.Left, y, plot.Right, y);
            }
        }

        double[] snapshot;
        lock (_sync)
        {
            snapshot = _values.ToArray();
        }

        double minimum = 0.0;
        double maximum = 1.0;

        if (snapshot.Length > 0)
        {
            double dataMinimum = snapshot[0];
            double dataMaximum = snapshot[0];
            for (int i = 1; i < snapshot.Length; i++)
            {
                dataMinimum = Math.Min(dataMinimum, snapshot[i]);
                dataMaximum = Math.Max(dataMaximum, snapshot[i]);
            }

            double dataRange = Math.Max(0.0, dataMaximum - dataMinimum);
            double padding = Math.Max(
                5.0,
                Math.Max(Math.Abs(dataMaximum), 1.0) * 0.08);
            padding = Math.Max(padding, dataRange * 0.12);

            minimum = Math.Max(0.0, dataMinimum - padding);
            maximum = dataMaximum + padding;

            // Keep zero visible when the signal is actually close to zero,
            // but do not flatten a 900-count trace just because the configured
            // threshold is 15000.
            if (dataMinimum <= Math.Max(100.0, dataMaximum * 0.25))
                minimum = 0.0;

            double minimumSpan = Math.Max(10.0, dataMaximum * 0.10);
            if (maximum - minimum < minimumSpan)
            {
                double center = (maximum + minimum) * 0.5;
                minimum = Math.Max(0.0, center - minimumSpan * 0.5);
                maximum = minimum + minimumSpan;
            }
        }
        else if (_threshold > 0.0)
        {
            maximum = _threshold * 1.08;
        }

        double scale = Math.Max(1.0, maximum - minimum);

        using (Font font = new Font("Segoe UI", 8F))
        using (Brush brush = new SolidBrush(V6Theme.Muted))
        {
            e.Graphics.DrawString(
                maximum.ToString("0", CultureInfo.InvariantCulture),
                font,
                brush,
                2,
                plot.Top - 4);
            e.Graphics.DrawString(
                minimum.ToString("0", CultureInfo.InvariantCulture),
                font,
                brush,
                2,
                plot.Bottom - 8);
            e.Graphics.DrawString(
                "Last " + snapshot.Length.ToString() + " points",
                font,
                brush,
                Math.Max(plot.Left, plot.Right - 78),
                plot.Bottom + 4);
        }

        if (_threshold > 0 && _threshold >= minimum && _threshold <= maximum)
        {
            float y = plot.Bottom -
                (float)((_threshold - minimum) / scale) * plot.Height;
            using (Pen thresholdPen = new Pen(V6Theme.Amber, 1.2F))
            {
                thresholdPen.DashStyle = DashStyle.Dash;
                e.Graphics.DrawLine(
                    thresholdPen,
                    plot.Left,
                    y,
                    plot.Right,
                    y);
            }
        }
        else if (_threshold > 0 && snapshot.Length > 0)
        {
            string direction = _threshold > maximum ? "↑" : "↓";
            string text = "Threshold " +
                _threshold.ToString("0", CultureInfo.InvariantCulture) +
                " " + direction + "(out of range)";
            using (Font font = new Font("Segoe UI", 8F))
            using (Brush brush = new SolidBrush(V6Theme.Amber))
            {
                SizeF size = e.Graphics.MeasureString(text, font);
                e.Graphics.DrawString(
                    text,
                    font,
                    brush,
                    Math.Max(plot.Left, plot.Right - size.Width),
                    plot.Top + 3);
            }
        }

        if (snapshot.Length < 2)
            return;

        PointF[] points = new PointF[snapshot.Length];
        for (int i = 0; i < snapshot.Length; i++)
        {
            float x = plot.Left +
                (snapshot.Length == 1
                    ? 0
                    : (float)i * plot.Width / (snapshot.Length - 1));
            float y = plot.Bottom -
                (float)((snapshot[i] - minimum) / scale) * plot.Height;
            points[i] = new PointF(x, y);
        }

        using (Pen signal = new Pen(ForeColor, 1.8F))
        {
            e.Graphics.DrawLines(signal, points);
        }
    }
}

internal sealed class V6LogPanel : UserControl
{
    private readonly TextBox _box;

    public V6LogPanel(V6LogService log)
    {
        BackColor = V6Theme.Surface;

        _box = new TextBox();
        _box.Dock = DockStyle.Fill;
        _box.Multiline = true;
        _box.ReadOnly = true;
        _box.ScrollBars = ScrollBars.Vertical;
        _box.BackColor = Color.FromArgb(249, 251, 250);
        _box.BorderStyle = BorderStyle.None;
        _box.Font = new Font("Consolas", 8.5F);
        Controls.Add(_box);

        if (log != null)
        {
            log.Logged += delegate(string line, V6LogLevel level)
            {
                Append(line);
            };
        }
    }

    public void Append(string line)
    {
        if (IsDisposed || Disposing)
            return;

        if (InvokeRequired)
        {
            BeginInvoke(new MethodInvoker(delegate { Append(line); }));
            return;
        }

        _box.AppendText(line + Environment.NewLine);

        if (_box.TextLength > 200000)
        {
            _box.Select(0, 50000);
            _box.SelectedText = "";
        }
    }
}
