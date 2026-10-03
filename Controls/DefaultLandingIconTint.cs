#if ANDROID || IOS || MACCATALYST
using System.ComponentModel;
using Microsoft.Maui.Platform;

namespace AdaptiveShell.Controls;

// Default landing rows use SVG navigation symbols as templates. Raster, remote and
// stream images keep their original colors; user-provided LandingTemplate is untouched.
internal sealed class DefaultLandingIconTint : Behavior<Image>
{
    readonly Label _title;
    Image? _image;
    bool _tinted;

    internal DefaultLandingIconTint(Label title) => _title = title;

    protected override void OnAttachedTo(Image image)
    {
        base.OnAttachedTo(image);
        _image = image;
        image.HandlerChanged += OnHandlerChanged;
        image.Loaded += OnLoaded;
        image.PropertyChanged += OnImageChanged;
        _title.HandlerChanged += OnHandlerChanged;
        _title.PropertyChanged += OnTitleChanged;
        ScheduleTint();
    }

    protected override void OnDetachingFrom(Image image)
    {
        image.HandlerChanged -= OnHandlerChanged;
        image.Loaded -= OnLoaded;
        image.PropertyChanged -= OnImageChanged;
        _title.HandlerChanged -= OnHandlerChanged;
        _title.PropertyChanged -= OnTitleChanged;
        _image = null;
        base.OnDetachingFrom(image);
    }

    void OnHandlerChanged(object? sender, EventArgs e) => ScheduleTint();
    void OnLoaded(object? sender, EventArgs e) => ScheduleTint();
    void OnTitleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Label.TextColor))
            ScheduleTint();
    }

    void OnImageChanged(object? sender, PropertyChangedEventArgs e)
    {
        // MAUI sets IsLoading=false after applying the asynchronously loaded native image.
        if (e.PropertyName == nameof(Image.Source)
            || (e.PropertyName == nameof(Image.IsLoading) && _image?.IsLoading == false))
            ScheduleTint();
    }

    void ScheduleTint() => _image?.Dispatcher.Dispatch(ApplyTint);

    void ApplyTint()
    {
        if (_image is not { } image)
            return;

        bool symbol = image.Source is FileImageSource file
            && file.File?.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) == true;
#if ANDROID
        if (image.Handler?.PlatformView is not global::Android.Widget.ImageView nativeImage)
            return;
        if (!symbol)
        {
            if (_tinted)
                nativeImage.ImageTintList = null;
            _tinted = false;
            return;
        }
        if (_title.Handler?.PlatformView is global::Android.Widget.TextView nativeTitle)
        {
            nativeImage.ImageTintList = global::Android.Content.Res.ColorStateList.ValueOf(
                new global::Android.Graphics.Color(nativeTitle.CurrentTextColor));
            _tinted = true;
        }
#elif IOS || MACCATALYST
        if (image.Handler?.PlatformView is not UIKit.UIImageView nativeImage
            || nativeImage.Image is not { } nativeSource)
            return;
        if (!symbol)
        {
            if (_tinted && nativeSource.RenderingMode == UIKit.UIImageRenderingMode.AlwaysTemplate)
            {
                using var original = nativeSource.ImageWithRenderingMode(UIKit.UIImageRenderingMode.AlwaysOriginal);
                nativeImage.Image = original;
            }
            _tinted = false;
            return;
        }
        if (_title.Handler?.PlatformView is UIKit.UILabel nativeTitle && nativeTitle.TextColor is { } color)
        {
            if (nativeSource.RenderingMode != UIKit.UIImageRenderingMode.AlwaysTemplate)
            {
                using var template = nativeSource.ImageWithRenderingMode(UIKit.UIImageRenderingMode.AlwaysTemplate);
                nativeImage.Image = template;
            }
            nativeImage.TintColor = color;
            _tinted = true;
        }
#endif
    }
}
#endif
