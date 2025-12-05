//using CommunityToolkit.Labs.WinUI.MarkdownTextBlock;  // this is updated in the latest version
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml.Controls;

namespace Audibly.App.Views.ContentDialogs
{
    public sealed partial class ChangelogContentDialog : ContentDialog
    {
        // This is what Config="{x:Bind MarkdownConfig}" in XAML binds to
        public MarkdownConfig MarkdownConfig = MarkdownConfig.Default;

        // This is what Text="{x:Bind ChangelogText, Mode=OneWay}" binds to
        public string ChangelogText { get; }

        public string Subtitle { get; set; } = string.Empty;

        public ChangelogContentDialog()
        {
            InitializeComponent();

            // Whatever your source of text is; if this doesn't exist you can
            // temporarily hard-code something like:
            // ChangelogText = "# Changelog\n\nThis is some markdown.";
            ChangelogText = Changelog.Text;
        }
    }
}
