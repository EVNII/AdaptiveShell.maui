using AdaptiveShell.Hosting;
using Microsoft.Extensions.Logging;

namespace ExampleAShellApp
{
#if MAC_BUTTON_FILLED
    sealed class MacFilledDiagnosticButtonHandler : Microsoft.Maui.Handlers.ButtonHandler
    {
        protected override UIKit.UIButton CreatePlatformView()
        {
            var button = base.CreatePlatformView();
            button.Configuration = UIKit.UIButtonConfiguration.FilledButtonConfiguration;
            return button;
        }
    }
#endif
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

            #if MAC_BUTTON_FILLED
            builder.ConfigureMauiHandlers(handlers =>
                handlers.AddHandler<Button, MacFilledDiagnosticButtonHandler>());
#endif
#if MAC_BUTTON_EXACT_BACKGROUND
            Microsoft.Maui.Handlers.ButtonHandler.Mapper.AppendToMapping(nameof(Microsoft.Maui.IView.Background), (handler, button) =>
            {
                if (UIKit.UIDevice.CurrentDevice.UserInterfaceIdiom != UIKit.UIUserInterfaceIdiom.Mac ||
                    button.Background is not Microsoft.Maui.Graphics.SolidPaint paint ||
                    handler.PlatformView.Configuration is not { } configuration)
                    return;
                var background = configuration.Background;
                background.BackgroundColor = Microsoft.Maui.Platform.ColorExtensions.ToPlatform(paint.Color);
                background.BackgroundColorTransformer = null;
                configuration.Background = background;
                handler.PlatformView.Configuration = configuration;
            });
#endif
            return builder.Build();
        }
    }
}
