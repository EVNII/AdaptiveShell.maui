using AdaptiveShell.Controls;

namespace ExampleAShellApp
{
    public partial class AppShell : AShell
    {
        public AppShell()
        {
           InitializeComponent();
#if ASHELL_NAVIGATION_OVERFLOW_SAMPLE
           // Opt-in fixture for short-window Android navigation tests. Keep the
           // normal sample and its identifiers intact for the default UI suite.
           for (int position = 4; position <= 12; position++)
           {
               Items.Add(CreateOverflowContent($"overflow-top-{position:00}",
                   $"Navigation {position:00}"));
           }

           var media = Items.OfType<AShellGroup>().Single(group => group.AutomationId == "media");
           for (int position = 3; position <= 18; position++)
           {
               media.Items.Add(CreateOverflowContent($"overflow-child-{position:00}",
                   $"Media {position:00}"));
           }
#endif
        }

#if ASHELL_NAVIGATION_OVERFLOW_SAMPLE
        static AShellContent CreateOverflowContent(string identity, string title) => new()
        {
            Title = title,
            AutomationId = identity,
            Icon = "star.svg",
            ContentTemplate = new DataTemplate(() => new NavigationOverflowPage(identity))
        };
#endif
    }

#if ASHELL_NAVIGATION_OVERFLOW_SAMPLE
    sealed class NavigationOverflowPage(string identity) : MainPage(identity);
#endif
}
