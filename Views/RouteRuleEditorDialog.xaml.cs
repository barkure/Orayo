using Microsoft.UI.Xaml.Controls;
using Orayo.Models;

namespace Orayo.Views;

public sealed partial class RouteRuleEditorDialog : ContentDialog
{
    public CustomRoutingRule Rule { get; private set; } = new();

    public RouteRuleEditorDialog()
    {
        InitializeComponent();
        TypeComboBox.SelectedItem = "domain";
        OutboundComboBox.SelectedItem = "proxy";
    }

    public void Configure(CustomRoutingRule rule, bool isEdit)
    {
        Rule = rule.Clone();
        Title = isEdit ? "编辑路由规则" : "新增路由规则";
        NameTextBox.Text = Rule.Name;
        TypeComboBox.SelectedItem = string.IsNullOrWhiteSpace(Rule.Type) ? "domain" : Rule.Type;
        MatchTextBox.Text = Rule.Match;
        OutboundComboBox.SelectedItem = string.IsNullOrWhiteSpace(Rule.OutboundTag) ? "proxy" : Rule.OutboundTag;
    }

    private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(MatchTextBox.Text))
        {
            args.Cancel = true;
            return;
        }

        Rule.Name = NameTextBox.Text.Trim();
        Rule.Type = (TypeComboBox.SelectedItem as string ?? "domain").Trim();
        Rule.Match = MatchTextBox.Text.Trim();
        Rule.OutboundTag = (OutboundComboBox.SelectedItem as string ?? "proxy").Trim();
        Rule.IsEnabled = true;
    }
}

