using System;
using System.Collections.Generic;
using System.Text;

namespace AdaptiveShell.Controls
{
    public class AShellContent : AShellItem, IAShellContentController
    {
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
        public Page? _page { get; set; } = null;

        Page IAShellContentController.page
        {
            get
            {
                if (_page == null && ContentTemplate != null)
                {
                    _page = (Page)ContentTemplate.CreateContent();
                }

                if (_page == null)
                {
                    throw new InvalidOperationException();
                }

                if (_page.Parent is null)
                {
                    // Page 的 Parent 必须是 Page,挂到最近的祖先 Page(即 AShell)上,
                    // 保证资源/样式解析链 Page -> AShell -> Window -> Application 连通
                    Element? ancestor = Parent;
                    while (ancestor is not null and not Page)
                    {
                        ancestor = ancestor.Parent;
                    }

                    if (ancestor is Page ownerPage)
                    {
                        ownerPage.AddLogicalChild(_page);
                    }
                }

                return _page;
            }
        }

        public AShellContent()
        {
        }


    }
}
