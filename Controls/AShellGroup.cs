using System.Collections.ObjectModel;
using System.Linq;

namespace AdaptiveShell.Controls
{
    // 导航分组:把多个 AShellContent 组织为一个可折叠的组
    // (iOS/Mac 为 UITabGroup,Windows 为 NavigationViewItem 嵌套,Android 扁平化)
    [ContentProperty(nameof(Items))]
    public class AShellGroup : AShellItem
    {
        public static readonly BindableProperty LandingTemplateProperty =
            BindableProperty.Create(nameof(LandingTemplate), typeof(DataTemplate), typeof(AShellGroup), null);

        readonly ObservableCollection<AShellContent> _items;

        public AShellGroup()
        {
            _items = new AShellItemCollection<AShellContent>(this);
        }

        public ObservableCollection<AShellContent> Items => _items;

        // 紧凑导航形态下的组落地页模板(Apple 和 Android)。
        public DataTemplate? LandingTemplate
        {
            get { return (DataTemplate?)GetValue(LandingTemplateProperty); }
            set { SetValue(LandingTemplateProperty, value); }
        }

        internal AShellContent? FirstLeaf() => Items.FirstOrDefault();

        protected override void OnParentSet()
        {
            base.OnParentSet();
            foreach (var content in _items)
            {
                content.UpdatePageOwner();
            }
        }
    }
}
