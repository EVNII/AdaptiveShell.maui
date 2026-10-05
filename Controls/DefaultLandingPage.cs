using System.Globalization;
using Microsoft.Maui;

namespace AdaptiveShell.Controls;

internal static class DefaultLandingPage
{
    static readonly IValueConverter HasIcon = new HasIconConverter();

    internal static Page Create(AShellGroup group, Action<AShellContent> navigate)
    {
        if (group.LandingTemplate is not null)
        {
            return group.LandingTemplate.CreateContent() as Page
                ?? throw new InvalidOperationException("AShellGroup.LandingTemplate must create a Page.");
        }

        var layout = new VerticalStackLayout
        {
            Padding = new Thickness(20, 12),
            Spacing = 4,
        };
        foreach (var child in group.Items)
        {
            layout.Add(CreateRow(child, navigate));
        }

        var page = new ContentPage
        {
            Content = new ScrollView
            {
                AutomationId = $"landing-{group.AutomationId ?? group.Title}-body",
                Content = layout,
            },
        };
        page.SetBinding(Page.TitleProperty, new Binding(nameof(AShellItem.Title), source: group));
        return page;
    }

    static View CreateRow(AShellContent child, Action<AShellContent> navigate)
    {
        var row = new HorizontalStackLayout
        {
            Spacing = 14,
            Padding = new Thickness(4, 12),
            AutomationId = $"landing-{child.AutomationId ?? child.Title}",
        };
        var title = new Label
        {
            AutomationId = $"{row.AutomationId}-title",
            FontSize = 17,
            VerticalOptions = LayoutOptions.Center,
        };
        title.SetBinding(Label.TextProperty, new Binding(nameof(AShellItem.Title), source: child));

        var icon = new Image
        {
            AutomationId = $"{row.AutomationId}-icon",
            WidthRequest = 24,
            HeightRequest = 24,
            VerticalOptions = LayoutOptions.Center,
        };
        icon.SetBinding(Image.SourceProperty, new Binding(nameof(AShellItem.Icon), source: child));
        icon.SetBinding(VisualElement.IsVisibleProperty,
            new Binding(nameof(AShellItem.Icon), source: child, converter: HasIcon));
        AutomationProperties.SetIsInAccessibleTree(icon, true);
#if ANDROID || IOS || MACCATALYST
        icon.Behaviors.Add(new DefaultLandingIconTint(title));
#endif
        row.Add(icon);
        row.Add(title);

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => navigate(child);
        row.GestureRecognizers.Add(tap);
        return row;
    }

    sealed class HasIconConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not null;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
