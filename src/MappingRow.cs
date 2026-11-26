using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace KeyHoldMacro;

internal sealed class MappingRow : Grid
{
    private static readonly string[] ColumnNames = { "序号", "启用", "触发按键", "屏幕 X", "屏幕 Y", "备注", "个性化" };
    private static readonly string[] Keys = new[] { "" }.Concat(KeyCatalog.Keys.Keys).ToArray();
    private readonly int index;
    private readonly Action changed;
    private readonly RadioButton selection = new() { GroupName = "MappingSelection", MinWidth = 0 };
    private readonly CheckBox enabled = new() { MinWidth = 0 };
    private readonly ComboBox key = new() { MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox x = new() { MinWidth = 0 };
    private readonly TextBox y = new() { MinWidth = 0 };
    private readonly TextBox notes = new() { MinWidth = 0, MaxLength = 100 };
    private readonly Button customize = new();
    private readonly Control[] controls;
    private Macro personalization;
    private bool loading;

    internal MappingRow(int index, Action<int> select, Action changed, Action<int> edit, Style buttonStyle)
    {
        this.index = index;
        this.changed = changed;
        ConfigureColumns(this);
        selection.Content = (index + 1).ToString();
        selection.Click += (_, _) => select(index);
        GotFocus += (_, _) => select(index);
        key.ItemsSource = Keys;
        enabled.Checked += (_, _) => Changed();
        enabled.Unchecked += (_, _) => Changed();
        key.SelectionChanged += (_, _) => Changed();
        x.TextChanging += (_, _) => Changed();
        y.TextChanging += (_, _) => Changed();
        notes.TextChanging += (_, _) => Changed();
        customize.Style = buttonStyle;
        customize.Click += (_, _) => edit(index);
        controls = new Control[] { selection, enabled, key, x, y, notes, customize };
        for (int column = 0; column < controls.Length; column++)
        {
            Put(this, controls[column], column);
            AutomationProperties.SetAutomationId(controls[column], $"Mapping{index + 1}Column{column + 1}");
        }
    }

    private static void ConfigureColumns(Grid grid)
    {
        grid.ColumnSpacing = 8;
        foreach (double width in new double[] { 54, 45, 116, 74, 74 })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(126) });
    }

    private static void Put(Grid grid, FrameworkElement element, int column)
    {
        SetColumn(element, column);
        element.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(element);
    }

    internal static Grid Header()
    {
        var header = new Grid { MinHeight = 22 };
        ConfigureColumns(header);
        for (int i = 0; i < ColumnNames.Length; i++)
            Put(header, new TextBlock { FontSize = 12 }, i);
        return header;
    }

    internal static void LocalizeHeader(Grid header)
    {
        for (int i = 0; i < ColumnNames.Length; i++)
            ((TextBlock)header.Children[i]).Text = L.T(ColumnNames[i]);
    }

    internal void Localize()
    {
        for (int column = 0; column < controls.Length; column++)
            AutomationProperties.SetName(controls[column], L.F("第 {0} 行 · {1}", index + 1, L.T(ColumnNames[column])));
        UpdateCustomize();
    }

    internal void Select() => selection.IsChecked = true;

    internal void SetEditable(bool value)
    {
        foreach (Control control in controls) control.IsEnabled = value;
    }

    internal void Load(Macro macro)
    {
        loading = true;
        try
        {
            personalization = macro?.Clone();
            enabled.IsChecked = macro?.Enabled ?? false;
            key.SelectedItem = macro?.Key ?? "";
            SetPoint(macro?.X ?? 0, macro?.Y ?? 0);
            notes.Text = macro?.Label ?? "";
            UpdateCustomize();
        }
        finally { loading = false; }
    }

    internal void SetPoint(int screenX, int screenY)
    {
        x.Text = screenX.ToString();
        y.Text = screenY.ToString();
    }

    internal void RecordKey(string name)
    {
        key.SelectedItem = name;
        enabled.IsChecked = true;
    }

    internal Macro Read(bool forEditor = false)
    {
        string trigger = key.SelectedItem as string;
        if (string.IsNullOrEmpty(trigger))
        {
            if (enabled.IsChecked == true && !forEditor)
                throw new ArgumentException(L.F("第 {0} 行已启用，请先选择宏按键。", index + 1));
            return null;
        }
        if (!int.TryParse(x.Text, out int screenX) || !int.TryParse(y.Text, out int screenY))
            throw new ArgumentException(forEditor ? L.T("请先填写有效的原坐标。") :
                L.F("第 {0} 行坐标必须是整数。", index + 1));
        Macro macro = personalization?.Clone() ?? new Macro();
        macro.Enabled = enabled.IsChecked == true;
        macro.Key = trigger;
        macro.X = screenX;
        macro.Y = screenY;
        macro.Label = notes.Text;
        return macro;
    }

    private void Changed()
    {
        if (!loading) changed();
    }

    private void UpdateCustomize()
    {
        customize.Content = personalization == null ||
            (personalization.MouseMode == "Hold" && personalization.BeforeActions.Count + personalization.AfterActions.Count == 0)
            ? L.T("设置")
            : (personalization.MouseMode == "Click" ? L.T("点击") : L.T("按住")) + " · " +
              personalization.BeforeActions.Count + "/" + personalization.AfterActions.Count;
    }
}
