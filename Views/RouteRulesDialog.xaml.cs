using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Orayo.Models;
using Orayo.Services;

namespace Orayo.Views;

public sealed partial class RouteRulesDialog : ContentDialog
{
    private int? _editingIndex;

    public ObservableCollection<CustomRoutingRule> Rules { get; } = [];

    public RouteRulesDialog()
    {
        InitializeComponent();
        RulesListView.ItemsSource = Rules;
        ResetEditor();
    }

    public void LoadRules(IEnumerable<CustomRoutingRule>? rules)
    {
        Rules.Clear();
        foreach (var rule in RouteRulePresetService.EnsureRules(rules))
        {
            Rules.Add(rule);
        }

        if (Rules.Count > 0)
        {
            RulesListView.SelectedIndex = 0;
        }
        else
        {
            ResetEditor();
        }
    }

    public List<CustomRoutingRule> GetRules()
    {
        var list = new List<CustomRoutingRule>();
        foreach (var rule in Rules)
        {
            list.Add(rule.Clone());
        }
        return list;
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        RulesListView.SelectedItem = null;
        ResetEditor();
    }

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (RulesListView.SelectedItem is CustomRoutingRule rule)
        {
            LoadEditor(rule, Rules.IndexOf(rule));
        }
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (RulesListView.SelectedItem is not CustomRoutingRule rule)
        {
            return;
        }

        var index = Rules.IndexOf(rule);
        Rules.Remove(rule);

        if (Rules.Count == 0)
        {
            ResetEditor();
            return;
        }

        RulesListView.SelectedIndex = index >= Rules.Count ? Rules.Count - 1 : index;
    }

    private void MoveUpButton_Click(object sender, RoutedEventArgs e)
    {
        if (RulesListView.SelectedItem is not CustomRoutingRule rule)
        {
            return;
        }

        var index = Rules.IndexOf(rule);
        if (index > 0)
        {
            Rules.Move(index, index - 1);
            RulesListView.SelectedItem = rule;
        }
    }

    private void MoveDownButton_Click(object sender, RoutedEventArgs e)
    {
        if (RulesListView.SelectedItem is not CustomRoutingRule rule)
        {
            return;
        }

        var index = Rules.IndexOf(rule);
        if (index >= 0 && index < Rules.Count - 1)
        {
            Rules.Move(index, index + 1);
            RulesListView.SelectedItem = rule;
        }
    }

    private void RestoreDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        Rules.Clear();
        foreach (var rule in RouteRulePresetService.CreateDefaultRules())
        {
            Rules.Add(rule);
        }

        RulesListView.SelectedIndex = Rules.Count > 0 ? 0 : -1;
    }

    private void ApplyRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(MatchTextBox.Text))
        {
            return;
        }

        var rule = BuildRuleFromEditor();
        if (_editingIndex.HasValue && _editingIndex.Value >= 0 && _editingIndex.Value < Rules.Count)
        {
            Rules[_editingIndex.Value] = rule;
            RulesListView.SelectedIndex = _editingIndex.Value;
            return;
        }

        Rules.Add(rule);
        RulesListView.SelectedItem = rule;
    }

    private void ClearEditorButton_Click(object sender, RoutedEventArgs e)
    {
        RulesListView.SelectedItem = null;
        ResetEditor();
    }

    private void RulesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RulesListView.SelectedItem is CustomRoutingRule rule)
        {
            LoadEditor(rule, Rules.IndexOf(rule));
        }
    }

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
    }

    private void ResetEditor()
    {
        _editingIndex = null;
        EditorTitleTextBlock.Text = "新增规则";
        NameTextBox.Text = string.Empty;
        TypeComboBox.SelectedItem = "domain";
        MatchTextBox.Text = string.Empty;
        OutboundComboBox.SelectedItem = "proxy";
        EnabledCheckBox.IsChecked = true;
    }

    private void LoadEditor(CustomRoutingRule rule, int index)
    {
        _editingIndex = index;
        EditorTitleTextBlock.Text = "编辑规则";
        NameTextBox.Text = rule.Name;
        TypeComboBox.SelectedItem = string.IsNullOrWhiteSpace(rule.Type) ? "domain" : rule.Type;
        MatchTextBox.Text = rule.Match;
        OutboundComboBox.SelectedItem = string.IsNullOrWhiteSpace(rule.OutboundTag) ? "proxy" : rule.OutboundTag;
        EnabledCheckBox.IsChecked = rule.IsEnabled;
    }

    private CustomRoutingRule BuildRuleFromEditor()
    {
        return new CustomRoutingRule
        {
            Name = NameTextBox.Text.Trim(),
            Type = (TypeComboBox.SelectedItem as string ?? "domain").Trim(),
            Match = MatchTextBox.Text.Trim(),
            OutboundTag = (OutboundComboBox.SelectedItem as string ?? "proxy").Trim(),
            IsEnabled = EnabledCheckBox.IsChecked == true,
        };
    }
}

