using AdaptiveShell.Hosting;
using Microsoft.Extensions.Logging;

namespace ExampleAShellApp
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .UseAdaptiveShell()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });

#if DEBUG
    		builder.Logging.AddDebug();
#endif

#if MACCATALYST
            // Mac's default bordered configuration renders a system gray background.
            // Initialize the filled configuration before MAUI maps either color;
            // the existing handler continues to map text, images, states, and themes.
            Microsoft.Maui.Handlers.ButtonHandler.Mapper.PrependToMapping(
                nameof(Microsoft.Maui.IView.Background), InitializeMacButtonConfiguration);
            Microsoft.Maui.Handlers.ButtonHandler.Mapper.PrependToMapping(
                nameof(Microsoft.Maui.ITextStyle.TextColor), InitializeMacButtonConfiguration);
#endif

            return builder.Build();
        }

#if MACCATALYST
        static void InitializeMacButtonConfiguration(Microsoft.Maui.Handlers.IButtonHandler handler, Microsoft.Maui.IButton button)
        {
            if (UIKit.UIDevice.CurrentDevice.UserInterfaceIdiom == UIKit.UIUserInterfaceIdiom.Mac &&
                button.Background is Microsoft.Maui.Graphics.SolidPaint &&
                handler.PlatformView.Configuration is null)
            {
                handler.PlatformView.Configuration = UIKit.UIButtonConfiguration.FilledButtonConfiguration;
            }
        }
#endif
    }
}
