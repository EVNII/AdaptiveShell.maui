#if MACCATALYST
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using UIKit;

namespace ExampleAShellApp;

// Sample diagnostics only: observe the existing MAUI handler without changing its configuration.
internal static class MacButtonStyleDiagnostics
{
    private static readonly ConditionalWeakTable<Button, Registration> Registrations = new();
    private static readonly object Gate = new();

    public static void Attach(Button button)
    {
        ArgumentNullException.ThrowIfNull(button);
        lock (Gate)
        {
            if (!Registrations.TryGetValue(button, out _))
                Registrations.Add(button, new Registration(button));
        }
    }

    private sealed class Registration
    {
        private readonly WeakReference<Button> _button;
        private readonly Application? _application;

        public Registration(Button button)
        {
            _button = new(button);
            _application = Application.Current;
            button.Loaded += OnLoaded;
            button.HandlerChanged += OnHandlerChanged;
            button.PropertyChanged += OnPropertyChanged;
            if (_application is not null)
                _application.RequestedThemeChanged += OnRequestedThemeChanged;
            Queue("attach");
        }

        private void OnLoaded(object? sender, EventArgs args) => Queue("loaded");
        private void OnHandlerChanged(object? sender, EventArgs args) => Queue("handler-changed");
        private void OnRequestedThemeChanged(object? sender, AppThemeChangedEventArgs args) => Queue("theme-changed");

        private void OnPropertyChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is nameof(Button.BackgroundColor) or nameof(Button.TextColor)
                or nameof(Button.Text) or nameof(Button.IsEnabled))
                Queue("property-" + args.PropertyName);
        }

        private void Queue(string phase)
        {
            if (!_button.TryGetTarget(out var button))
            {
                if (_application is not null)
                    _application.RequestedThemeChanged -= OnRequestedThemeChanged;
                return;
            }

            // Queue behind the property mapper, then capture again after UIKit has laid out.
            button.Dispatcher.Dispatch(() =>
            {
                Dump(phase + ".after-mapping");
                button.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(300),
                    () => Dump(phase + ".after-300ms"));
            });
        }

        private void Dump(string phase)
        {
            if (!_button.TryGetTarget(out var button))
                return;

            try
            {
                var native = button.Handler?.PlatformView as UIButton;
                var configuration = native?.Configuration;
                var background = configuration?.Background;
                var frame = native?.Frame;
                var record = new
                {
                    utc = DateTimeOffset.UtcNow.ToString("O"),
                    phase,
                    label = button.AutomationId ?? button.StyleId ?? "Button",
                    theme = Application.Current?.RequestedTheme.ToString(),
                    userTheme = Application.Current?.UserAppTheme.ToString(),
                    maui = new
                    {
                        backgroundColor = MauiColor(button.BackgroundColor),
                        textColor = MauiColor(button.TextColor),
                        enabled = button.IsEnabled,
                        title = button.Text,
                        handler = button.Handler?.GetType().FullName,
                        ancestors = MauiAncestors(button)
                    },
                    native = native is null ? null : new
                    {
                        type = native.GetType().FullName,
                        nativeClass = native.Class.Name,
                        buttonType = native.ButtonType.ToString(),
                        deviceIdiom = UIDevice.CurrentDevice.UserInterfaceIdiom.ToString(),
                        idiom = native.TraitCollection.UserInterfaceIdiom.ToString(),
                        behavioralStyle = native.BehavioralStyle.ToString(),
                        preferredBehavioralStyle = native.PreferredBehavioralStyle.ToString(),
                        enabled = native.Enabled,
                        title = native.CurrentTitle,
                        backgroundColor = NativeColor(native.BackgroundColor, native),
                        titleColor = NativeColor(native.CurrentTitleColor, native),
                        hasConfiguration = configuration is not null,
                        configurationTitle = configuration?.Title,
                        baseBackgroundColor = NativeColor(configuration?.BaseBackgroundColor, native),
                        baseForegroundColor = NativeColor(configuration?.BaseForegroundColor, native),
                        configurationBackgroundColor = NativeColor(background?.BackgroundColor, native),
                        hasBackgroundColorTransformer = background?.BackgroundColorTransformer is not null,
                        backgroundCustomView = background?.CustomView?.GetType().FullName,
                        macIdiomStyle = configuration?.MacIdiomStyle.ToString(),
                        frame = frame.HasValue ? new
                        {
                            x = (double)frame.Value.X,
                            y = (double)frame.Value.Y,
                            width = (double)frame.Value.Width,
                            height = (double)frame.Value.Height
                        } : null,
                        ancestors = NativeAncestors(native)
                    }
                };

                var line = JsonSerializer.Serialize(record);
                lock (Gate)
                    File.AppendAllText(Path.Combine(FileSystem.CacheDirectory, "ashell-button-style.jsonl"),
                        line + Environment.NewLine);
                Console.WriteLine("ASHELL_BUTTON_STYLE " + line);
            }
            catch (Exception error)
            {
                // Avoid printing paths or other exception details into public CI logs.
                Console.WriteLine("ASHELL_BUTTON_STYLE_ERROR " + error.GetType().Name);
            }
        }
    }

    private static string? MauiColor(Microsoft.Maui.Graphics.Color? color) => color?.ToHex();

    private static object? NativeColor(UIColor? color, UIButton button)
    {
        if (color is null)
            return null;
        var resolved = color.GetResolvedColor(button.TraitCollection);
        resolved.GetRGBA(out var red, out var green, out var blue, out var alpha);
        return new
        {
            red = (double)red,
            green = (double)green,
            blue = (double)blue,
            alpha = (double)alpha
        };
    }

    private static string[] MauiAncestors(Element button)
    {
        var names = new List<string>();
        for (Element? current = button.Parent; current is not null && names.Count < 16; current = current.Parent)
            names.Add(current.GetType().FullName ?? current.GetType().Name);
        return names.ToArray();
    }

    private static string[] NativeAncestors(UIView button)
    {
        var names = new List<string>();
        for (UIView? current = button.Superview; current is not null && names.Count < 16; current = current.Superview)
            names.Add(current.Class.Name);
        return names.ToArray();
    }
}
#endif
