using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using SdvMods.Core.Localization;

namespace SdvMods.App.Services;

/// <summary>
/// Translates the literal texts written in the XAML (buttons, headers, hints) when a control is first shown, so the
/// XAML can stay in plain Spanish. Texts coming from the view-model are translated at the source with Loc.T / Loc.F.
/// </summary>
public static class Localizer
{
    static bool _installed;

    public static void Install()
    {
        if (_installed || Loc.Language == "es") return;
        _installed = true;
        Control.LoadedEvent.AddClassHandler<Control>((c, _) => Translate(c));
    }

    static string? Tr(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var t = Loc.T(text);
        return t == text ? null : t; // only touch real translations: never replace a binding by an identical literal
    }

    static void Translate(Control c)
    {
        switch (c)
        {
            case TextBlock tb:
                if (Tr(tb.Text) is { } a) tb.Text = a;
                break;
            case TextBox box:
                if (Tr(box.PlaceholderText) is { } p) box.PlaceholderText = p;
                break;
        }
        if (c is HeaderedContentControl h && h.Header is string hs && Tr(hs) is { } ht) h.Header = ht;
        if (c is ContentControl cc && cc is not HeaderedContentControl && cc.Content is string s && Tr(s) is { } t) cc.Content = t;
        if (c is HeaderedContentControl hc && hc.Content is string s2 && Tr(s2) is { } t2) hc.Content = t2;
    }
}
