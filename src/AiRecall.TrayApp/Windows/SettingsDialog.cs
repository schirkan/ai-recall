using System.ComponentModel;
using AiRecall.Core.Configuration;
using AiRecall.Trigger;
using Serilog;

namespace AiRecall.TrayApp.Windows;

/// <summary>
/// Modal settings editor window (Spec 0009). TreeView auf der linken Seite
/// mit allen Config-Sektionen (Top-Level + Sub-Sections). Rechte Seite:
/// dynamisch generierte Form mit Type-spezifischen Editoren (bool/int/string/
/// enum/List) aus <see cref="PropertyEditorFactory"/>.
///
/// Save: ConfigSerializer.SaveAtomic + Hot-Reload-Callback. Cancel verwirft
/// die im Dialog gemachten Änderungen. Reload verwirft und lädt aus Disk.
/// </summary>
public sealed class SettingsDialog : Form
{
    private readonly AppConfig _originalConfig;
    private readonly AppConfig _workingConfig;
    private readonly Action<AppConfig>? _onSave;
    private readonly TreeView _tree;
    private readonly Panel _editorPanel;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly Button _saveButton;
    private readonly Button _cancelButton;
    private readonly Button _reloadButton;
    private readonly Dictionary<string, Control> _editors = new(StringComparer.OrdinalIgnoreCase);
    private ConfigSectionDescriptor? _activeSection;

    public SettingsDialog(AppConfig config, Action<AppConfig>? onSave = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _originalConfig = config;
        _workingConfig = DeepClone(config);
        _onSave = onSave;

        Text = "AiRecall — Settings";
        Width = 900;
        Height = 650;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = true;
        FormBorderStyle = FormBorderStyle.Sizable;
        ShowInTaskbar = false;

        // SplitContainer: links TreeView, rechts Editor-Panel.
        // Bug-Bash 2026-07-06 I-16: SplitterDistance als fixer Pixelwert war
        // zu breit fuer die TreeView (die Section-Namen sind oft nur 4-12
        // Zeichen). Stattdessen FixedPanel=Panel1 setzen, dann berechnet
        // WinForms die Breite selbst, und die TreeView bekommt exakt den
        // Platz, den sie braucht. Der Rest geht an Panel2 (Editor-Panel).
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            // Panel1MinSize als Sicherheitsnetz: TreeView braucht min. ~140 px
            // fuer die laengsten Section-Namen wie "screenRecorder".
            Panel1MinSize = 140,
            // SplitterWidth etwas geraumiger fuers Dragging
            SplitterWidth = 6,
            // Initialer Wert wird in OnShown an aktuelle Form-Groesse angepasst.
            SplitterDistance = 200
        };
        Controls.Add(split);

        _tree = new TreeView
        {
            Dock = DockStyle.Fill,
            HideSelection = false
        };
        _tree.AfterSelect += OnTreeNodeSelected;
        split.Panel1.Controls.Add(_tree);

        _editorPanel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(12)
        };
        split.Panel2.Controls.Add(_editorPanel);

        // Bug-Bash 2026-07-06 I-16: Wenn der User den Splitter zieht oder
        // das Fenster resized, muessen die Editoren ihre Breite mit-anpassen.
        // Resize auf _editorPanel reicht, weil SplitContainer seine Panels
        // bei Splitter-Move ebenfalls resized.
        split.SplitterMoved += (_, _) => RelayoutEditors();
        _editorPanel.Resize += (_, _) => RelayoutEditors();

        // StatusBar + Buttons unten
        _statusLabel = new ToolStripStatusLabel();
        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_statusLabel);
        Controls.Add(statusStrip);
        statusStrip.Dock = DockStyle.Bottom;

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 36,
            Padding = new Padding(4)
        };
        _saveButton = new Button { Text = "💾 Save", Width = 100, Height = 28 };
        _saveButton.Click += OnSaveClicked;
        _cancelButton = new Button { Text = "✖ Cancel", Width = 100, Height = 28 };
        _cancelButton.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        _reloadButton = new Button { Text = "↻ Reload", Width = 100, Height = 28 };
        _reloadButton.Click += (_, _) => ReloadFromDisk();
        buttonPanel.Controls.AddRange(new Control[] { _saveButton, _cancelButton, _reloadButton });
        Controls.Add(buttonPanel);

        // TreeView füllen
        PopulateTree();
        if (_tree.Nodes.Count > 0) _tree.SelectedNode = _tree.Nodes[0];

        UpdateStatus();
    }

    private void PopulateTree()
    {
        _tree.Nodes.Clear();
        _activeSection = null;
        var sections = ConfigSchemaReflection.GetTopLevelSections(_workingConfig);
        foreach (var section in sections)
        {
            var node = new TreeNode(section.DisplayName) { Tag = section };
            _tree.Nodes.Add(node);
            if (section.SubSections.Count > 0)
            {
                foreach (var sub in section.SubSections)
                {
                    var subNode = new TreeNode(sub.DisplayName) { Tag = sub };
                    node.Nodes.Add(subNode);
                }
            }
        }
        _tree.ExpandAll();
    }

    private void OnTreeNodeSelected(object? sender, TreeViewEventArgs e)
    {
        if (e.Node?.Tag is not ConfigSectionDescriptor section) return;

        // Erst Edits der VORHER sichtbaren Sektion in _workingConfig übernehmen,
        // damit sie bei Save nicht verloren gehen.
        if (_activeSection is not null && !ReferenceEquals(_activeSection, section))
        {
            ApplyEditorsToSection(_activeSection);
        }
        _activeSection = section;
        RenderSection(section);
    }

    private void RenderSection(ConfigSectionDescriptor section)
    {
        _editorPanel.Controls.Clear();
        _editors.Clear();

        // Layout-Konstanten. Statt fixer Pixel-X-Offsets berechnen wir
        // Label-Spalte und Editor-Spalte relativ zur aktuellen Panel-Breite,
        // damit beim Splitter-Drag oder Fenster-Resize nichts ueberlappt.
        // Bug-Bash 2026-07-06 I-16: Editor-Spalte war fix 360 px breit, ragte
        // bei grosser rechter Seite in Leere und war bei kleiner rechts
        // abgeschnitten.
        const int padding = 12;
        const int labelColWidth = 200;
        const int editorMinWidth = 240;
        const int rowHeight = 32;

        int y = 8;
        var labelFont = new Font(Font, FontStyle.Bold);
        var labelX = padding;
        var editorX = labelX + labelColWidth + padding;
        var editorWidth = Math.Max(editorMinWidth, _editorPanel.ClientSize.Width - editorX - padding);

        // Section-Header
        var header = new Label
        {
            Text = section.DisplayName,
            Font = new Font(Font.FontFamily, 12, FontStyle.Bold),
            Location = new Point(labelX, y),
            AutoSize = true
        };
        _editorPanel.Controls.Add(header);
        y += 30;

        // Properties
        foreach (var prop in section.Properties)
        {
            var info = PropertyEditorFactory.GetEditor(prop, section.Instance);

            // Label
            var lbl = new Label
            {
                Text = prop.Name + ":",
                Location = new Point(labelX, y + 4),
                Width = labelColWidth,
                Font = labelFont
            };
            _editorPanel.Controls.Add(lbl);

            // Editor
            var editor = CreateEditor(info, prop);
            editor.Location = new Point(editorX, y);
            editor.Width = editorWidth;
            editor.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _editorPanel.Controls.Add(editor);
            _editors[prop.Name] = editor;

            y += rowHeight;
        }
    }

    /// <summary>
    /// Editor-Panel neu layouten, wenn das Form skaliert wird (Splitter
    /// bewegt, Fenster resized). Labels bleiben linksbuendig fix, Editoren
    /// dehnen sich rechts mit.
    /// </summary>
    private void RelayoutEditors()
    {
        if (_activeSection is null) return;
        const int padding = 12;
        const int labelColWidth = 200;
        const int editorMinWidth = 240;
        const int rowHeight = 32;
        const int headerHeight = 30;
        const int startY = 8;

        int editorX = padding + labelColWidth + padding;
        int editorWidth = Math.Max(editorMinWidth, _editorPanel.ClientSize.Width - editorX - padding);

        int y = startY;
        // Index 0 ist der Header
        if (_editorPanel.Controls.Count > 0)
        {
            _editorPanel.Controls[0].Location = new Point(padding, y);
            y += headerHeight;
        }
        int idx = 1;
        foreach (var prop in _activeSection.Properties)
        {
            if (idx >= _editorPanel.Controls.Count) break;
            var lbl = _editorPanel.Controls[idx];
            lbl.Location = new Point(padding, y + 4);
            idx++;
            if (idx >= _editorPanel.Controls.Count) break;
            var editor = _editorPanel.Controls[idx];
            editor.Location = new Point(editorX, y);
            editor.Width = editorWidth;
            idx++;
            y += rowHeight;
        }
    }

    /// <summary>
    /// Beim ersten Anzeigen den Splitter proportional setzen (~30% fuer
    /// TreeView, Rest fuer Editor). FixedPanel=Panel1 sorgt dafuer, dass
    /// der User die TreeView-Groesse nicht versehentlich auf null druecken
    /// kann.
    /// </summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Splitter proportional an aktuelle Form-Groesse anpassen.
        // Wir suchen den SplitContainer rekursiv, weil wir split nicht als
        // Feld halten (kein weiterer Use-Case).
        foreach (Control c in Controls)
        {
            if (c is SplitContainer sc)
            {
                int desired = Math.Max(sc.Panel1MinSize, (int)(sc.ClientSize.Width * 0.30));
                if (desired < sc.ClientSize.Width - 100)
                {
                    sc.SplitterDistance = desired;
                }
                break;
            }
        }
    }

    private static Control CreateEditor(PropertyEditorFactory.EditorInfo info, PropertyDescriptor prop)
    {
        switch (info.Kind)
        {
            case PropertyEditorFactory.EditorKind.CheckBox:
                var cb = new CheckBox { Checked = bool.Parse(info.DisplayText), Text = "Enabled" };
                return cb;

            case PropertyEditorFactory.EditorKind.ComboBox:
                var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
                var enumType = prop.PropertyType;
                foreach (var name in Enum.GetNames(enumType)) combo.Items.Add(name);
                combo.SelectedItem = info.DisplayText;
                return combo;

            case PropertyEditorFactory.EditorKind.TextBox:
                return new TextBox { Text = info.DisplayText };

            case PropertyEditorFactory.EditorKind.ListStringTextBox:
                return new TextBox
                {
                    Text = info.DisplayText,
                    PlaceholderText = "comma-separated values"
                };

            case PropertyEditorFactory.EditorKind.ReadOnly:
            default:
                return new TextBox { Text = info.DisplayText, ReadOnly = true, BackColor = SystemColors.Control };
        }
    }

    private void OnSaveClicked(object? sender, EventArgs e)
    {
        try
        {
            // Edits der aktuell sichtbaren Sektion übernehmen (andere Sections
            // wurden bereits beim TreeView-Wechsel in OnTreeNodeSelected übernommen).
            if (_activeSection is not null) ApplyEditorsToSection(_activeSection);

            // Save atomic to user-config path
            var userPath = UserConfigLocator.GetUserConfigPath();
            ConfigSerializer.SaveAtomic(_workingConfig, userPath);
            Log.Information("SettingsDialog: saved config to {Path}", userPath);

            // Hot-reload via callback
            _onSave?.Invoke(_workingConfig);

            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "SettingsDialog: save failed");
            MessageBox.Show(this, $"Save failed: {ex.Message}", "AiRecall", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ApplyEditorsToSection(ConfigSectionDescriptor section)
    {
        foreach (var prop in section.Properties)
        {
            if (!_editors.TryGetValue(prop.Name, out var control)) continue;
            var info = PropertyEditorFactory.GetEditor(prop, section.Instance);
            if (info.Parser is null) continue;

            try
            {
                var raw = ReadEditorValue(control, info.Kind);
                var value = info.Parser(raw);
                prop.SetValue(section.Instance, value);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not parse value for {Section}.{Property}", section.Name, prop.Name);
            }
        }
    }

    private static string ReadEditorValue(Control control, PropertyEditorFactory.EditorKind kind)
    {
        return kind switch
        {
            PropertyEditorFactory.EditorKind.CheckBox => ((CheckBox)control).Checked ? "true" : "false",
            PropertyEditorFactory.EditorKind.ComboBox => ((ComboBox)control).SelectedItem?.ToString() ?? "",
            PropertyEditorFactory.EditorKind.TextBox or
            PropertyEditorFactory.EditorKind.ListStringTextBox => ((TextBox)control).Text,
            _ => ""
        };
    }

    private void ReloadFromDisk()
    {
        var fresh = UserConfigLocator.LoadOrDefault(msg => Log.Warning("Config: {Msg}", msg));
        // Mutate _workingConfig in-place (keep reference)
        CopyInto(fresh, _workingConfig);
        PopulateTree();
        if (_tree.Nodes.Count > 0) _tree.SelectedNode = _tree.Nodes[0];
        UpdateStatus();
    }

    private static void CopyInto(AppConfig src, AppConfig dst)
    {
        dst.Capture = src.Capture;
        dst.ScreenRecorder = src.ScreenRecorder;
        dst.Ocr = src.Ocr;
        dst.Logging = src.Logging;
        dst.AppReader = src.AppReader;
        dst.Trigger = src.Trigger;
        dst.Conversion = src.Conversion;
    }

    private static AppConfig DeepClone(AppConfig src)
    {
        var json = ConfigSerializer.Serialize(src);
        return ConfigSerializer.Deserialize(json);
    }

    private void UpdateStatus()
    {
        _statusLabel.Text = $"Config: {UserConfigLocator.GetUserConfigPath()} | Apply: Save triggers hot-reload";
    }
}