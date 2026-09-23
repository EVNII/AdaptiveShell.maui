# AdaptiveShell.Maui

Adaptive shell navigation for .NET MAUI. The navigation chrome adapts to the platform and window size — sidebar on large screens, navigation rail on medium screens, bottom navigation on small screens — with support for navigation groups, per-page icons and accent colors.

## Platforms

- Android
- iOS
- Mac Catalyst

## Install

```bash
dotnet add package AdaptiveShell.Maui
```

## Usage

Register the handlers in `MauiProgram.cs`:

```csharp
using AdaptiveShell.Hosting;

var builder = MauiApp.CreateBuilder();
builder
    .UseMauiApp<App>()
    .UseAdaptiveShell();
```

Derive your app shell from `AShell` and declare navigation items in XAML:

```xaml
<a:AShell
    x:Class="MyApp.AppShell"
    xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
    xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
    xmlns:a="clr-namespace:AdaptiveShell.Controls;assembly=AdaptiveShell"
    Title="MyApp">

    <a:AShellContent
        Title="Home"
        Icon="home.svg"
        ContentTemplate="{DataTemplate local:MainPage}" />

    <a:AShellGroup Title="Media" Icon="folder.svg">
        <a:AShellContent
            Title="Music"
            Icon="star.svg"
            ContentTemplate="{DataTemplate local:MusicPage}" />
        <a:AShellContent
            Title="Photos"
            Icon="star.svg"
            ContentTemplate="{DataTemplate local:PhotosPage}" />
    </a:AShellGroup>
</a:AShell>
```

- `AShellContent` — a top-level navigation item hosting a page.
- `AShellGroup` — groups several items behind one entry (flyout/drawer).
- `FullBleed` — lets page content extend under the floating navigation bar; drive it with `AdaptiveTrigger` visual states to react to window size.

See `Example/ExampleAShellApp` for a full sample.

## License

[MIT](LICENSE)
