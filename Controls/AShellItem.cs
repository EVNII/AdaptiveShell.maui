namespace AdaptiveShell.Controls
{
    // AShell 导航项基类:叶子页面(AShellContent)与分组(AShellGroup)的共同抽象。
    // 继承 ContentView 以兼容 VisualStateManager(其附加属性要求 VisualElement)
    public abstract class AShellItem : ContentView
    {
        public static readonly BindableProperty TitleProperty =
            BindableProperty.Create(nameof(Title), typeof(string), typeof(AShellItem), "Item");

        public static readonly BindableProperty IconProperty =
            BindableProperty.Create(nameof(Icon), typeof(ImageSource), typeof(AShellItem), null);

        public string Title
        {
            get { return (string)GetValue(TitleProperty); }
            set { SetValue(TitleProperty, value); }
        }

        public ImageSource? Icon
        {
            get { return (ImageSource?)GetValue(IconProperty); }
            set { SetValue(IconProperty, value); }
        }
    }
}
