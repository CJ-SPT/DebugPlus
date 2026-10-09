using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace DebugPlus.UI
{
    // Shared with the isolated Unity layout fixture; no EFT or runtime dependencies.
    public sealed class CatalogItemView
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";
        public Sprite Icon { get; set; }
    }

    public sealed class ItemGridView : VisualElement
    {
        private const int RowHeight = 174;
        private readonly ListView _rows;
        private readonly Label _empty;
        private readonly Func<string, Task<Sprite>> _loadIcon;
        private readonly Func<string, bool> _isFavorite;
        private IReadOnlyList<CatalogItemView> _items = Array.Empty<CatalogItemView>();
        private int _columns = 3;
        private string _selectedId;
        private ScrollView Scroll => _rows.Q<ScrollView>();
        public event Action<CatalogItemView> Selected;
        public event Action<CatalogItemView> FavoriteToggled;

        public ItemGridView(Func<string, Task<Sprite>> loadIcon, Func<string, bool> isFavorite)
        {
            _loadIcon = loadIcon;
            _isFavorite = isFavorite;
            AddToClassList("item-grid");
            _rows = new ListView { fixedItemHeight = RowHeight, selectionType = SelectionType.None, virtualizationMethod = CollectionVirtualizationMethod.FixedHeight };
            _rows.AddToClassList("item-grid-list");
            _rows.makeItem = MakeRow;
            _rows.bindItem = BindRow;
            Add(_rows);
            WorkbenchScrolling.Configure(_rows);
            _empty = new Label("No items match. Try another name or clear your filters.");
            _empty.AddToClassList("grid-empty"); Add(_empty);
            _rows.RegisterCallback<GeometryChangedEvent>(e =>
            {
                var columns = Mathf.Clamp(Mathf.FloorToInt((e.newRect.width - 14) / 156), 2, 8);
                if (columns == _columns || e.newRect.width <= 0) return;
                var first = Mathf.FloorToInt(Scroll.scrollOffset.y / RowHeight) * _columns;
                _columns = columns; Rebuild();
                Scroll.scrollOffset = new Vector2(0, first / _columns * RowHeight);
            });
        }
        public void SetItems(IReadOnlyList<CatalogItemView> items)
        {
            _items = items ?? Array.Empty<CatalogItemView>();
            Rebuild(); Scroll.scrollOffset = Vector2.zero;
        }
        public void Select(string id)
        {
            _selectedId = id;
            _rows.RefreshItems();
        }
        public void FitViewport(ScrollView page)
        {
            // The outer page scrolls only when its minimum usable layout cannot fit.
            // A fixed grid height wasted the extra space when resizing the workbench.
            void Resize()
            {
                var available = page.contentViewport.resolvedStyle.height;
                var top = worldBound.yMin - page.contentContainer.worldBound.yMin;
                if (float.IsNaN(available) || available <= 0 || float.IsNaN(top)) return;
                var height = Mathf.Max(220, available - top - 56);
                if (Mathf.Abs(resolvedStyle.height - height) > 1) style.height = height;
            }
            EventCallback<GeometryChangedEvent> resize = _ => Resize();
            page.contentViewport.RegisterCallback(resize);
            RegisterCallback(resize);
            RegisterCallback<DetachFromPanelEvent>(_ => page.contentViewport.UnregisterCallback(resize));
            schedule.Execute(Resize).ExecuteLater(1);
        }
        private void Rebuild()
        {
            _rows.itemsSource = Enumerable.Range(0, (_items.Count + _columns - 1) / _columns).ToList();
            _rows.Rebuild();
            _empty.style.display = _items.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }
        private VisualElement MakeRow()
        {
            var row = new VisualElement(); row.AddToClassList("item-grid-row");
            for (var i = 0; i < _columns; i++)
            {
                var card = new VisualElement(); card.AddToClassList("item-tile"); row.Add(card);
                var choose = new Button(() =>
                {
                    if (card.userData is CatalogItemView item) { Select(item.Id); Selected?.Invoke(item); }
                });
                choose.AddToClassList("item-tile-select"); card.Add(choose);
                var art = new VisualElement(); art.AddToClassList("item-art"); choose.Add(art);
                var placeholder = new Label("…") { name = "placeholder" }; placeholder.AddToClassList("item-placeholder"); art.Add(placeholder);
                var image = new Image { name = "icon", scaleMode = ScaleMode.ScaleToFit }; art.Add(image);
                var name = new Label { name = "name" }; name.AddToClassList("item-tile-name"); choose.Add(name);
                var category = new Label { name = "category" }; category.AddToClassList("item-tile-category"); choose.Add(category);
                var favorite = new Button(() =>
                {
                    if (card.userData is CatalogItemView item) { FavoriteToggled?.Invoke(item); _rows.RefreshItems(); }
                }) { name = "favorite" };
                favorite.AddToClassList("item-tile-favorite"); card.Add(favorite);
            }
            return row;
        }
        private void BindRow(VisualElement row, int index)
        {
            for (var column = 0; column < row.childCount; column++)
            {
                var card = row[column]; var offset = index * _columns + column;
                card.userData = offset < _items.Count ? _items[offset] : null;
                card.style.visibility = offset < _items.Count ? Visibility.Visible : Visibility.Hidden;
                if (offset < _items.Count) BindCard(card, _items[offset]);
            }
        }
        private async void BindCard(VisualElement card, CatalogItemView item)
        {
            card.EnableInClassList("selected", item.Id == _selectedId);
            card.Q<Label>("name").text = item.Name;
            card.Q<Label>("category").text = item.Category;
            card.Q<Button>().tooltip = item.Name + "\n" + item.Category + "\nSelect to configure spawning";
            var favorite = card.Q<Button>("favorite");
            var saved = _isFavorite?.Invoke(item.Id) == true;
            favorite.text = saved ? "★" : "☆";
            favorite.tooltip = saved ? "Remove from favorites" : "Save to favorites";
            favorite.EnableInClassList("saved", saved);
            var icon = card.Q<Image>("icon"); icon.sprite = item.Icon;
            var placeholder = card.Q<Label>("placeholder");
            placeholder.style.display = item.Icon ? DisplayStyle.None : DisplayStyle.Flex;
            placeholder.text = "…";
            if (item.Icon || _loadIcon == null) return;
            try
            {
                var sprite = await _loadIcon(item.Id);
                item.Icon = sprite;
                if (!ReferenceEquals(card.userData, item)) return;
                icon.sprite = sprite;
                placeholder.style.display = sprite ? DisplayStyle.None : DisplayStyle.Flex;
                placeholder.text = "No image";
            }
            catch { if (ReferenceEquals(card.userData, item)) placeholder.text = "No image"; }
        }
    }
}
