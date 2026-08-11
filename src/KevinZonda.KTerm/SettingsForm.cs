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
    private readonly TabControl _tabs = new();
    private readonly ComboBox _shellProfile = new();
    private readonly TextBox _shellExecutable = new();
    private readonly TextBox _shellArguments = new();
    private readonly Button _shellBrowse = new();
    private readonly List<Font> _previewFonts = [];
    private TabPage? _shellPage;
    private bool _applyingShellValues;

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
        PopulateShellProfiles();
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
        },
        Shell = SelectedShellSettings()
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

    protected override void OnFormClosing(FormClosingEventArgs eventArgs)
    {
        var shell = SelectedShellSettings();
        if (DialogResult == DialogResult.OK && shell.Profile != ShellProfileCatalog.AutoId)
        {
            var executable = Environment.ExpandEnvironmentVariables(shell.Executable ?? string.Empty);
            if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            {
                eventArgs.Cancel = true;
                if (_shellPage is not null)
                {
                    _tabs.SelectedTab = _shellPage;
                }

                MessageBox.Show(
                    this,
                    "Enter the full path to an existing shell executable, or choose one with Browse.",
                    "KTerm shell settings",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
        }

        base.OnFormClosing(eventArgs);
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

        _tabs.Dock = DockStyle.Fill;
        _tabs.Margin = new Padding(0, 0, 0, 14);
        _tabs.TabPages.Add(CreateFontPage());
        _tabs.TabPages.Add(CreateThemePage());
        _shellPage = CreateShellPage();
        _tabs.TabPages.Add(_shellPage);
        root.Controls.Add(_tabs, 0, 0);
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

    private TabPage CreateShellPage()
    {
        var page = new TabPage("Shell")
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
            RowCount = 7,
            BackColor = SurfaceColor,
            Margin = new Padding(0)
        };
        for (var row = 0; row < 6; row++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(CreateLabel("Shell profile"), 0, 0);

        ConfigureField(_shellProfile);
        _shellProfile.DropDownStyle = ComboBoxStyle.DropDownList;
        _shellProfile.DisplayMember = nameof(ShellProfileDefinition.DisplayName);
        _shellProfile.Margin = new Padding(0, 5, 0, 12);
        _shellProfile.SelectedIndexChanged += (_, _) => HandleShellProfileChanged();
        layout.Controls.Add(_shellProfile, 0, 1);

        layout.Controls.Add(CreateLabel("Executable"), 0, 2);
        var executableRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 5, 0, 12)
        };
        executableRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        executableRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        ConfigureField(_shellExecutable);
        _shellExecutable.MaxLength = 1_024;
        _shellExecutable.Margin = new Padding(0, 0, 6, 0);
        executableRow.Controls.Add(_shellExecutable, 0, 0);
        ConfigureButton(_shellBrowse, "Browse...");
        _shellBrowse.Size = new Size(88, 28);
        _shellBrowse.Margin = new Padding(0);
        _shellBrowse.Click += (_, _) => BrowseForShell();
        executableRow.Controls.Add(_shellBrowse, 1, 0);
        layout.Controls.Add(executableRow, 0, 3);

        layout.Controls.Add(CreateLabel("Arguments"), 0, 4);
        ConfigureField(_shellArguments);
        _shellArguments.MaxLength = 4_096;
        _shellArguments.Margin = new Padding(0, 5, 0, 12);
        layout.Controls.Add(_shellArguments, 0, 5);

        var note = CreateLabel(
            "For profiles other than Auto, enter the executable path manually. " +
            "Changes apply to new tabs and splits.");
        note.Dock = DockStyle.Top;
        layout.Controls.Add(note, 0, 6);
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

    private void PopulateShellProfiles()
    {
        _shellProfile.Items.AddRange(ShellProfileCatalog.All.Cast<object>().ToArray());
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

        ApplyShellValues(normalized.Shell);
    }

    private void ApplyShellValues(ShellSettings settings)
    {
        var normalized = ShellSettings.Normalize(settings);
        var profile = ShellProfileCatalog.Find(normalized.Profile);
        _applyingShellValues = true;
        try
        {
            _shellProfile.SelectedItem = _shellProfile.Items
                .Cast<ShellProfileDefinition>()
                .First(candidate => candidate.Id == profile.Id);
            _shellExecutable.Text = profile.Id == ShellProfileCatalog.AutoId
                ? string.Empty
                : normalized.Executable ?? string.Empty;
            _shellArguments.Text = normalized.Arguments ?? profile.DefaultArguments;
            _shellExecutable.ReadOnly = profile.Id == ShellProfileCatalog.AutoId;
            _shellBrowse.Enabled = profile.Id != ShellProfileCatalog.AutoId;
        }
        finally
        {
            _applyingShellValues = false;
        }
    }

    private void HandleShellProfileChanged()
    {
        if (_applyingShellValues || _shellProfile.SelectedItem is not ShellProfileDefinition profile)
        {
            return;
        }

        _applyingShellValues = true;
        try
        {
            _shellExecutable.ReadOnly = profile.Id == ShellProfileCatalog.AutoId;
            _shellBrowse.Enabled = profile.Id != ShellProfileCatalog.AutoId;
            _shellExecutable.Clear();
            _shellArguments.Text = profile.DefaultArguments;
        }
        finally
        {
            _applyingShellValues = false;
        }
    }

    private ShellProfileDefinition SelectedShellProfile() =>
        _shellProfile.SelectedItem as ShellProfileDefinition ?? ShellProfileCatalog.All[0];

    private ShellSettings SelectedShellSettings()
    {
        var profile = SelectedShellProfile();
        return new ShellSettings
        {
            Profile = profile.Id,
            Executable = profile.Id == ShellProfileCatalog.AutoId ? null : _shellExecutable.Text,
            Arguments = profile.Id == ShellProfileCatalog.AutoId
                ? null
                : _shellArguments.Text
        };
    }

    private void BrowseForShell()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select shell executable",
            Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (File.Exists(_shellExecutable.Text))
        {
            dialog.FileName = _shellExecutable.Text;
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _shellExecutable.Text = dialog.FileName;
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

    private static void ConfigureField(TextBox field)
    {
        field.Dock = DockStyle.Fill;
        field.BackColor = FieldColor;
        field.ForeColor = Color.FromArgb(229, 233, 240);
        field.BorderStyle = BorderStyle.FixedSingle;
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
        var button = new Button();
        ConfigureButton(button, text, primary);
        button.Size = new Size(primary ? 84 : 112, 34);
        button.Margin = new Padding(6, 0, 0, 0);
        return button;
    }

    private static void ConfigureButton(Button button, string text, bool primary = false)
    {
        button.AutoSize = false;
        button.Text = text;
        button.BackColor = primary ? PrimaryColor : Color.FromArgb(37, 44, 54);
        button.ForeColor = Color.FromArgb(229, 233, 240);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = primary ? Color.FromArgb(98, 137, 184) : BorderColor;
    }
}
