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
    private readonly ComboBox _themeName = new();
    private readonly Panel _themePreview = new();
    private readonly List<Font> _previewFonts = [];

    internal SettingsForm(AppSettings settings)
    {
        Text = "KTerm Settings";
        BackColor = SurfaceColor;
        ForeColor = Color.FromArgb(216, 222, 233);
        ClientSize = new Size(520, 380);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;

        Controls.Add(CreateLayout());
        PopulateFonts();
        PopulateThemes();
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
        },
        Theme = new ThemeSettings
        {
            Name = _themeName.Text
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
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 2,
            BackColor = SurfaceColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 14)
        };
        tabs.TabPages.Add(CreateFontPage());
        tabs.TabPages.Add(CreateThemePage());
        root.Controls.Add(tabs, 0, 0);
        root.Controls.Add(CreateActions(), 0, 1);

        _fontFamily.TextChanged += (_, _) => UpdatePreview();
        _fontSize.ValueChanged += (_, _) => UpdatePreview();
        _lineHeight.ValueChanged += (_, _) => UpdatePreview();
        return root;
    }

    private TabPage CreateFontPage()
    {
        var page = new TabPage("Font")
        {
            BackColor = SurfaceColor,
            ForeColor = ForeColor,
            Padding = new Padding(16),
            UseVisualStyleBackColor = false
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = SurfaceColor,
            Margin = new Padding(0)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

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
        layout.Controls.Add(fields, 0, 0);

        var previewPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = FieldColor,
            Padding = new Padding(14),
            Margin = new Padding(0)
        };
        previewPanel.Paint += (_, eventArgs) =>
            ControlPaint.DrawBorder(eventArgs.Graphics, previewPanel.ClientRectangle, BorderColor, ButtonBorderStyle.Solid);
        _preview.Dock = DockStyle.Fill;
        _preview.Text = "PS C:\\> echo KTerm AaBb 0123";
        _preview.TextAlign = ContentAlignment.MiddleLeft;
        _preview.AutoEllipsis = true;
        previewPanel.Controls.Add(_preview);
        layout.Controls.Add(previewPanel, 0, 1);
        page.Controls.Add(layout);
        return page;
    }

    private TabPage CreateThemePage()
    {
        var page = new TabPage("Theme")
        {
            BackColor = SurfaceColor,
            ForeColor = ForeColor,
            Padding = new Padding(16),
            UseVisualStyleBackColor = false
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = SurfaceColor,
            Margin = new Padding(0)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(CreateLabel("Color scheme"), 0, 0);

        ConfigureField(_themeName);
        _themeName.DropDownStyle = ComboBoxStyle.DropDownList;
        _themeName.Margin = new Padding(0, 5, 0, 16);
        _themeName.SelectedIndexChanged += (_, _) => _themePreview.Invalidate();
        layout.Controls.Add(_themeName, 0, 1);

        _themePreview.Dock = DockStyle.Fill;
        _themePreview.Margin = new Padding(0);
        _themePreview.Paint += PaintThemePreview;
        layout.Controls.Add(_themePreview, 0, 2);
        page.Controls.Add(layout);
        return page;
    }

    private Control CreateActions()
    {
        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
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

        AcceptButton = save;
        CancelButton = cancel;
        return actions;
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

    private void PopulateThemes()
    {
        _themeName.Items.AddRange(TerminalThemeCatalog.All
            .Select(theme => theme.Name)
            .Cast<object>()
            .ToArray());
    }

    private void ApplyValues(AppSettings settings)
    {
        var normalized = AppSettings.Normalize(settings);
        _fontFamily.Text = normalized.Font.Family;
        _fontSize.Value = (decimal)normalized.Font.Size;
        _lineHeight.Value = (decimal)normalized.Font.LineHeight;
        _themeName.SelectedItem = normalized.Theme.Name;
        if (_themeName.SelectedIndex < 0)
        {
            _themeName.SelectedIndex = 0;
        }
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

    private void PaintThemePreview(object? sender, PaintEventArgs eventArgs)
    {
        var theme = TerminalThemeCatalog.Find(_themeName.Text);
        var bounds = _themePreview.ClientRectangle;
        using var background = new SolidBrush(ColorTranslator.FromHtml(theme.Background));
        eventArgs.Graphics.FillRectangle(background, bounds);
        ControlPaint.DrawBorder(eventArgs.Graphics, bounds, BorderColor, ButtonBorderStyle.Solid);

        var textBounds = new Rectangle(16, 14, Math.Max(0, bounds.Width - 32), 28);
        TextRenderer.DrawText(
            eventArgs.Graphics,
            "PS C:\\> npm test  AaBb 0123",
            Font,
            textBounds,
            ColorTranslator.FromHtml(theme.Foreground),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        const int gap = 4;
        const int columns = 8;
        var swatchWidth = Math.Max(8, (bounds.Width - 32 - ((columns - 1) * gap)) / columns);
        const int swatchHeight = 18;
        var swatchTop = Math.Max(48, bounds.Height - (swatchHeight * 2) - gap - 16);
        for (var index = 0; index < theme.AnsiColors.Count; index++)
        {
            var row = index / columns;
            var column = index % columns;
            var swatchBounds = new Rectangle(
                16 + (column * (swatchWidth + gap)),
                swatchTop + (row * (swatchHeight + gap)),
                swatchWidth,
                swatchHeight);
            using var swatch = new SolidBrush(ColorTranslator.FromHtml(theme.AnsiColors[index]));
            eventArgs.Graphics.FillRectangle(swatch, swatchBounds);
        }
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
