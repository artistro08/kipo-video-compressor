using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Kipo;

/// <summary>
/// Icon shown at the start of each queue row: a video while the file is in play,
/// a green checkmark once it's done; hidden while the spinner shows. Used from x:Bind function bindings.
/// </summary>
public static class RowIcon
{
    private const string VideoGlyph = "";
    private const string CheckGlyph = "";

    public static string Glyph(bool isDone) => isDone ? CheckGlyph : VideoGlyph;

    public static string Name(bool isDone) => isDone ? "Done" : "Video";

    public static Visibility Hidden(bool hide) => hide ? Visibility.Collapsed : Visibility.Visible;

    public static Brush Foreground(bool isDone) =>
        (Brush)Application.Current.Resources[isDone ? "SystemFillColorSuccessBrush" : "TextFillColorSecondaryBrush"];
}
