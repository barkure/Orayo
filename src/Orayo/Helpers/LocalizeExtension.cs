using System;
using System.Globalization;
using Microsoft.UI.Xaml.Markup;

namespace Orayo.Helpers;

[MarkupExtensionReturnType(ReturnType = typeof(string))]
public class LocalizeExtension : MarkupExtension
{
    public string Key { get; set; } = string.Empty;

    protected override object ProvideValue()
    {
        return Strings.ResourceManager.GetString(Key, CultureInfo.CurrentUICulture) ?? Key;
    }
}
