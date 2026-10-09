using UnityEngine.UIElements;

namespace DebugPlus.UI
{
    // Shared by the runtime and native layout fixtures. Keep Unity's wheel routing,
    // including nested scrolling, while moving three default lines per wheel unit.
    public static class WorkbenchScrolling
    {
        public const float WheelStep = 54;

        public static void Configure(VisualElement root)
        {
            if (root is ScrollView scroll) ConfigureScroll(scroll);
            root.Query<ScrollView>().ForEach(ConfigureScroll);
        }

        private static void ConfigureScroll(ScrollView scroll)
        {
            scroll.mouseWheelScrollSize = WheelStep;
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            scroll.verticalScrollerVisibility = ScrollerVisibility.Auto;
        }
    }
}
