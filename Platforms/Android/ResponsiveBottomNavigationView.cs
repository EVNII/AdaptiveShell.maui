using Android.Content;
using Android.Views;
using Google.Android.Material.BottomNavigation;

namespace AdaptiveShell.Platforms.Android
{
    // The containing HorizontalScrollView provides the viewport; native item
    // measurements determine whether this bar needs more horizontal space.
    internal sealed class ResponsiveBottomNavigationView : BottomNavigationView
    {
        public ResponsiveBottomNavigationView(Context context) : base(context)
        {
            ItemHorizontalTranslationEnabled = false;
        }

        // These are also queried while the native base constructor builds its menu.
        public override int MaxItemCount => int.MaxValue;
#pragma warning disable CS0672 // Pinned Material marks this constructor-time hook as internal.
        public override int CollapsedMaxItemCount => int.MaxValue;
#pragma warning restore CS0672

        protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
        {
            var menu = MenuViewGroup;
            int itemWidth = Resources!.GetDimensionPixelSize(
                Resource.Dimension.design_bottom_navigation_item_min_width);
            int maxItemWidth = Resources.GetDimensionPixelSize(
                Resource.Dimension.design_bottom_navigation_active_item_max_width);
            int visibleItems = 0;

            for (int index = 0; index < menu.ChildCount; index++)
            {
                var item = menu.GetChildAt(index)!;
                if (item.Visibility == ViewStates.Gone)
                    continue;

                int itemHeightSpec = ViewGroup.GetChildMeasureSpec(
                    heightMeasureSpec,
                    PaddingTop + PaddingBottom + menu.PaddingTop + menu.PaddingBottom,
                    item.LayoutParameters?.Height ?? ViewGroup.LayoutParams.WrapContent);
                item.Measure(MeasureSpec.MakeMeasureSpec(maxItemWidth, MeasureSpecMode.AtMost),
                    itemHeightSpec);
                itemWidth = Math.Max(itemWidth, item.MeasuredWidth);
                visibleItems++;
            }

            int requiredWidth = itemWidth * visibleItems + PaddingLeft + PaddingRight
                + menu.PaddingLeft + menu.PaddingRight;
            int viewportWidth = MeasureSpec.GetSize(widthMeasureSpec);
            base.OnMeasure(MeasureSpec.MakeMeasureSpec(Math.Max(requiredWidth, viewportWidth),
                MeasureSpecMode.Exactly), heightMeasureSpec);
        }
    }
}
