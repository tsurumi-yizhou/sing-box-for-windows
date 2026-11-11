using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SFW.Controls;

/// <summary>
/// The read-only file preview used by the report detail page. The preview chrome
/// lives in TextPreview.xaml; callers only hand over the text and whether it was
/// truncated.
/// </summary>
public sealed partial class TextPreview : UserControl
{
    public TextPreview() => InitializeComponent();

    public TextPreview(string text, bool truncated) : this() => SetText(text, truncated);

    /// <summary>
    /// Sets the preview body. A truncated preview keeps SFM's warning under the
    /// text so the missing head of the file is never mistaken for the whole log.
    /// </summary>
    public void SetText(string text, bool truncated)
    {
        PreviewBox.Text = text;
        TruncationNote.Visibility = truncated ? Visibility.Visible : Visibility.Collapsed;
    }
}
