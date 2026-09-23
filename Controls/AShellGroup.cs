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

        // 组落地页模板(仅 Apple tab 模式):设置后替代壳内置的默认子项列表页
        public DataTemplate? LandingTemplate
        {
            get { return (DataTemplate?)GetValue(LandingTemplateProperty); }
            set { SetValue(LandingTemplateProperty, value); }
        }

        internal AShellContent? FirstLeaf() => Items.FirstOrDefault();
    }
}
