using System.Runtime.CompilerServices;

namespace AdaptiveShell.Controls
{
    public class AShellContent : AShellItem, IAShellContentController
    {
        Page? _cachedPage;
        Page? _pageOwner;

        public static readonly BindableProperty ContentTemplateProperty =
            BindableProperty.Create(nameof(ContentTemplate), typeof(DataTemplate), typeof(AShellContent), null);

        public static readonly BindableProperty FullBleedProperty =
            BindableProperty.Create(
                nameof(FullBleed), typeof(bool), typeof(AShellContent), false,
                propertyChanged: (bindable, _, _) =>
                    ((AShellContent)bindable).FullBleedChanged?.Invoke(bindable, EventArgs.Empty));

        internal event EventHandler? FullBleedChanged;

        public DataTemplate ContentTemplate {
            get { return (DataTemplate)GetValue(ContentTemplateProperty);  }
            set { SetValue(ContentTemplateProperty, value); }
        }

        // true: 内容铺满全屏,延伸到悬浮导航栏之下(仅 Apple 平台悬浮 chrome 有遮挡,其他平台无效果)
        public bool FullBleed {
            get { return (bool)GetValue(FullBleedProperty); }
            set { SetValue(FullBleedProperty, value); }
        }
        // Keep the existing public member for source/binary compatibility. All changes now
        // pass through the same ownership and native cache invalidation path.
        public Page? _page
        {
            get => _cachedPage;
            set => SetCachedPage(value, notify: true);
        }

        internal Page? CachedPage => _cachedPage;

        Page IAShellContentController.page
        {
            get
            {
                if (_cachedPage is null && ContentTemplate is not null)
                {
                    var page = ContentTemplate.CreateContent() as Page
                        ?? throw new InvalidOperationException("AShellContent.ContentTemplate must create a Page.");
                    SetCachedPage(page, notify: false);
                }

                if (_cachedPage is null)
                {
                    throw new InvalidOperationException("Set AShellContent.ContentTemplate before navigating to this item.");
                }

                UpdatePageOwner();
                return _cachedPage;
            }
        }

        protected override void OnParentSet()
        {
            base.OnParentSet();
            UpdatePageOwner();
        }

        protected override void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            // MAUI raises PropertyChanged before the BindableProperty change callback.
            // Invalidate here so native mappers can already create the replacement page.
            if (propertyName == nameof(ContentTemplate))
            {
                SetCachedPage(null, notify: false);
            }
            base.OnPropertyChanged(propertyName);
        }

        internal void UpdatePageOwner()
        {
            Element? ancestor = Parent;
            while (ancestor is not null and not Page)
            {
                ancestor = ancestor.Parent;
            }
            var owner = ancestor as Page;
            if (_cachedPage is null || ReferenceEquals(_pageOwner, owner))
            {
                return;
            }

            DetachPage();
            if (owner is not null)
            {
                if (_cachedPage.Parent is not null)
                {
                    throw new InvalidOperationException("The content page already belongs to another parent.");
                }
                owner.AddLogicalChild(_cachedPage);
                _pageOwner = owner;
            }
        }

        void SetCachedPage(Page? page, bool notify)
        {
            if (ReferenceEquals(_cachedPage, page))
            {
                return;
            }
            if (page?.Parent is not null)
            {
                throw new InvalidOperationException("The content page already belongs to another parent.");
            }
            DetachPage();
            _cachedPage = page;
            UpdatePageOwner();
            if (notify)
            {
                OnPropertyChanged(nameof(_page));
            }
        }

        void DetachPage()
        {
            if (_cachedPage is not null && ReferenceEquals(_cachedPage.Parent, _pageOwner))
            {
                _pageOwner?.RemoveLogicalChild(_cachedPage);
            }
            _pageOwner = null;
        }
    }
}
