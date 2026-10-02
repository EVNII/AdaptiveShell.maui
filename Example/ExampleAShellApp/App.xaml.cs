using Microsoft.Extensions.DependencyInjection;

namespace ExampleAShellApp
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            #if MAC_BUTTON_OFFICIAL_SHELL
            var shell = new Shell { Title = "ExampleAShellApp", FlyoutBehavior = FlyoutBehavior.Flyout };
            var home = new ShellContent
            {
                Title = "Home",
                AutomationId = "home",
                ContentTemplate = new DataTemplate(typeof(MainPage))
            };
            shell.Items.Add(home);
            return new Window(shell);
#else
            return new Window(new AppShell());
#endif
        }
    }
}