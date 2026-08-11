using System.Drawing.Text;
using System.Runtime.InteropServices;
using KevinZonda.KTerm.Configuration;
using KevinZonda.KTerm.Interop;

namespace KevinZonda.KTerm;

internal sealed class SettingsForm : Form
{
    private static readonly Color SurfaceColor = Color.FromArgb(23, 27, 34);
    private static readonly Color FieldColor = Color.FromArgb(12, 15, 20);
    private static readonly Color BorderColor = Color.FromArgb(58, 67, 81);
    private static readonly Color PrimaryColor = Color.FromArgb(76, 111, 153);

    private readonly ComboBox _fontFamily = new();
    private readonly NumericUpDown _fontSize = new();
    private readonly NumericUpDown _lineHeight = new();
    private readonly Label _preview = new();
    private readonly List<Font> _previewFonts = [];

    internal SettingsForm(AppSettings settings)
    {
        Text = "KTerm Settings";
        BackColor = SurfaceColor;
        ForeColor = Color.FromArgb(216, 222, 233);
        ClientSize = new Size(520, 350);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;

        Controls.Add(CreateLayout());
        PopulateFonts();
        ApplyValues(settings);
        UpdatePreview();
    }

    internal AppSettings Settings => AppSettings.Normalize(new AppSettings
    {
        Font = new FontSettings
        {
            Family = _fontFamily.Text,
            Size = decimal.ToDouble(_fontSize.Value),
            LineHeight = decimal.ToDouble(_lineHeight.Value)
        }
    });

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        var enabled = 1;
        NativeMethods.DwmSetWindowAttribute(
            Handle,
            NativeMethods.DwmUseImmersiveDarkMode,
            ref enabled,
            Marshal.SizeOf<int>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            foreach (var previewFont in _previewFonts)
            {
                previewFont.Dispose();
            }

            _previewFonts.Clear();
        }
    }

    private Control CreateLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(22, 18, 22, 18),
            ColumnCount = 1,
            RowCount = 5,
            BackColor = SurfaceColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var heading = new Label
        {
            AutoSize = true,
            Text = "Terminal font",
            Font = new Font(Font.FontFamily, 15, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 16)
        };
        root.Controls.Add(heading, 0, 0);

        var fields = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 4,
            Margin = new Padding(0)
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        fields.Controls.Add(CreateLabel("Font family"), 0, 0);
        fields.SetColumnSpan(fields.GetControlFromPosition(0, 0)!, 2);

        ConfigureField(_fontFamily);
        _fontFamily.DropDownStyle = ComboBoxStyle.DropDown;
        _fontFamily.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _fontFamily.AutoCompleteSource = AutoCompleteSource.ListItems;
        _fontFamily.Margin = new Padding(0, 5, 0, 14);
        fields.Controls.Add(_fontFamily, 0, 1);
        fields.SetColumnSpan(_fontFamily, 2);

        fields.Controls.Add(CreateLabel("Font size (px)"), 0, 2);
        var lineHeightLabel = CreateLabel("Line height");
        lineHeightLabel.Margin = new Padding(10, 0, 0, 0);
        fields.Controls.Add(lineHeightLabel, 1, 2);

        ConfigureNumber(_fontSize, 8, 72, 1, 0);
        _fontSize.Margin = new Padding(0, 5, 5, 16);
        fields.Controls.Add(_fontSize, 0, 3);
        ConfigureNumber(_lineHeight, 0.8m, 2, 0.01m, 2);
        _lineHeight.Margin = new Padding(10, 5, 0, 16);
        fields.Controls.Add(_lineHeight, 1, 3);
        root.Controls.Add(fields, 0, 1);

        var previewPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = FieldColor,
            Padding = new Padding(14),
            Margin = new Padding(0, 0, 0, 12)
        };
        previewPanel.Paint += (_, eventArgs) =>
            ControlPaint.DrawBorder(eventArgs.Graphics, previewPanel.ClientRectangle, BorderColor, ButtonBorderStyle.Solid);
        _preview.Dock = DockStyle.Fill;
        _preview.Text = "PS C:\\> echo KTerm AaBb 0123";
        _preview.TextAlign = ContentAlignment.MiddleLeft;
        _preview.AutoEllipsis = true;
        previewPanel.Controls.Add(_preview);
        root.Controls.Add(previewPanel, 0, 2);

        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0)
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var defaults = CreateButton("Restore defaults");
        defaults.AutoSize = true;
        defaults.Click += (_, _) =>
        {
            ApplyValues(AppSettings.Normalize(null));
            UpdatePreview();
        };
        actions.Controls.Add(defaults, 0, 0);

        var commitButtons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0)
        };
        var cancel = CreateButton("Cancel");
        cancel.DialogResult = DialogResult.Cancel;
        var save = CreateButton("Save", primary: true);
        save.DialogResult = DialogResult.OK;
        commitButtons.Controls.Add(cancel);
        commitButtons.Controls.Add(save);
        actions.Controls.Add(commitButtons, 2, 0);
        root.Controls.Add(actions, 0, 4);

        AcceptButton = save;
        CancelButton = cancel;
        _fontFamily.TextChanged += (_, _) => UpdatePreview();
        _fontSize.ValueChanged += (_, _) => UpdatePreview();
        _lineHeight.ValueChanged += (_, _) => UpdatePreview();
        return root;
    }

    private void PopulateFonts()
    {
        using var installed = new InstalledFontCollection();
        _fontFamily.Items.AddRange(installed.Families
            .Select(family => family.Name)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .Cast<object>()
            .ToArray());
    }

    private void ApplyValues(AppSettings settings)
    {
        var normalized = AppSettings.Normalize(settings);
        _fontFamily.Text = normalized.Font.Family;
        _fontSize.Value = (decimal)normalized.Font.Size;
        _lineHeight.Value = (decimal)normalized.Font.LineHeight;
    }

    private void UpdatePreview()
    {
        var previewFont = CreatePreviewFont();
        _previewFonts.Add(previewFont);
        _preview.Font = previewFont;
    }

    private Font CreatePreviewFont()
    {
        var size = decimal.ToSingle(_fontSize.Value);
        foreach (var candidate in _fontFamily.Text.Split(','))
        {
            var family = candidate.Trim().Trim('\'', '"');
            if (family.Length == 0 || string.Equals(family, "monospace", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                return new Font(family, size, FontStyle.Regular, GraphicsUnit.Pixel);
            }
            catch (ArgumentException)
            {
            }
        }

        return new Font(FontFamily.GenericMonospace, size, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    private static Label CreateLabel(string text) => new()
    {
        AutoSize = true,
        Text = text,
        ForeColor = Color.FromArgb(174, 183, 197),
        Margin = new Padding(0)
    };

    private static void ConfigureField(ComboBox field)
    {
        field.Dock = DockStyle.Fill;
        field.BackColor = FieldColor;
        field.ForeColor = Color.FromArgb(229, 233, 240);
        field.FlatStyle = FlatStyle.Flat;
    }

    private static void ConfigureNumber(
        NumericUpDown field,
        decimal minimum,
        decimal maximum,
        decimal increment,
        int decimalPlaces)
    {
        field.Dock = DockStyle.Fill;
        field.Minimum = minimum;
        field.Maximum = maximum;
        field.Increment = increment;
        field.DecimalPlaces = decimalPlaces;
        field.BackColor = FieldColor;
        field.ForeColor = Color.FromArgb(229, 233, 240);
        field.BorderStyle = BorderStyle.FixedSingle;
    }

    private static Button CreateButton(string text, bool primary = false)
    {
        var button = new Button
        {
            AutoSize = false,
            Size = new Size(primary ? 84 : 112, 34),
            Text = text,
            BackColor = primary ? PrimaryColor : Color.FromArgb(37, 44, 54),
            ForeColor = Color.FromArgb(229, 233, 240),
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(6, 0, 0, 0)
        };
        button.FlatAppearance.BorderColor = primary ? Color.FromArgb(98, 137, 184) : BorderColor;
        return button;
    }
}
