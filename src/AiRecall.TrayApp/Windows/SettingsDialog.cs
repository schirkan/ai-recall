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

        // SplitContainer: links TreeView, rechts Editor-Panel
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 220
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

        int y = 8;
        var labelFont = new Font(Font, FontStyle.Bold);

        // Section-Header
        var header = new Label
        {
            Text = section.DisplayName,
            Font = new Font(Font.FontFamily, 12, FontStyle.Bold),
            Location = new Point(8, y),
            AutoSize = true
        };
        _editorPanel.Controls.Add(header);
        y += 30;

        // Properties
        foreach (var prop in section.Properties)
        {
            var info = PropertyEditorFactory.GetEditor(prop);

            // Label
            var lbl = new Label
            {
                Text = prop.Name + ":",
                Location = new Point(8, y + 4),
                Width = 200,
                Font = labelFont
            };
            _editorPanel.Controls.Add(lbl);

            // Editor
            var editor = CreateEditor(info, prop);
            editor.Location = new Point(220, y);
            editor.Width = 360;
            _editorPanel.Controls.Add(editor);
            _editors[prop.Name] = editor;

            y += 32;
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
            var info = PropertyEditorFactory.GetEditor(prop);
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