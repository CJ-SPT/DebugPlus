using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.UIElements;

namespace DebugPlus.UI
{
    // Shared by the real workbench and the native Unity layout fixture.
    public sealed class LoggingOptionView
    {
        public string Name, Hint;
        public Func<bool> Read;
        public Action<bool> Write;
    }
    public sealed class LoggingSourceView
    {
        public string Name;
        public Func<bool> Read;
        public Action<bool> Write;
        public Func<string> ReadLevel;
        public Action<string> WriteLevel;
    }
    public sealed class LoggingEntryView
    {
        public string Time, Source, Level, Message, Detail, Text;
        public object Entry;
    }
    public sealed class LoggingView : VisualElement
    {
        private static readonly List<string> Levels = new List<string> { "Trace", "Debug", "Info", "Warning", "Error", "Fatal" };
        private readonly Func<IReadOnlyList<LoggingSourceView>> _sources;
        private readonly Func<LoggingEntryView[]> _snapshot;
        private readonly Func<string> _status;
        private readonly List<Action> _sync = new List<Action>();
        private readonly Label _statusLabel, _counts;
        private readonly TextField _search, _sourceSearch, _detail;
        private readonly DropdownField _source, _severity;
        private readonly Toggle _pause;
        private readonly ListView _categories, _messages;
        private List<LoggingSourceView> _shownSources = new List<LoggingSourceView>();
        private List<LoggingEntryView> _shown = new List<LoggingEntryView>();
        private LoggingEntryView[] _entries = Array.Empty<LoggingEntryView>();
        private string _sourceSignature;
        public LoggingView(IEnumerable<LoggingOptionView> options, Func<IReadOnlyList<LoggingSourceView>> sources,
            Func<LoggingEntryView[]> snapshot, Func<string> status, Action reset, Action clear,
            Action<string> copy, Action<IReadOnlyList<LoggingEntryView>, bool> export)
        {
            _sources = sources; _snapshot = snapshot; _status = status;
            AddToClassList("logging-view");
            var controls = new Foldout { text = "Capture controls · saved automatically", value = true }; Add(controls);
            var toggles = Row(controls); toggles.AddToClassList("logging-options");
            foreach (var option in options)
            {
                var toggle = new Toggle(option.Name) { tooltip = option.Hint }; toggles.Add(toggle);
                toggle.RegisterValueChangedCallback(e => option.Write(e.newValue));
                _sync.Add(() => toggle.SetValueWithoutNotify(option.Read()));
            }
            var sourceRow = Row(controls); sourceRow.AddToClassList("logging-filter-row");
            _sourceSearch = new TextField("Find native category") { name = "logging-category-search" }; _sourceSearch.AddToClassList("logging-search"); sourceRow.Add(_sourceSearch);
            sourceRow.Add(new Button(reset) { text = "Restore defaults" });
            _categories = new ListView { fixedItemHeight = 30, selectionType = SelectionType.None, virtualizationMethod = CollectionVirtualizationMethod.FixedHeight };
            _categories.AddToClassList("logging-categories"); controls.Add(_categories);
            _categories.makeItem = () =>
            {
                var row = new VisualElement(); row.AddToClassList("logging-category-row");
                var enabled = new Toggle(); enabled.AddToClassList("logging-category-toggle"); row.Add(enabled);
                var level = new DropdownField(Levels, 2); level.style.width = 120; row.Add(level);
                enabled.RegisterValueChangedCallback(e => { if (row.userData is LoggingSourceView s) s.Write(e.newValue); });
                level.RegisterValueChangedCallback(e => { if (row.userData is LoggingSourceView s) s.WriteLevel(e.newValue); });
                return row;
            };
            _categories.bindItem = (row, i) =>
            {
                var source = _shownSources[i]; row.userData = source;
                var enabled = row.Q<Toggle>(); enabled.label = source.Name; enabled.SetValueWithoutNotify(source.Read());
                row.Q<DropdownField>().SetValueWithoutNotify(source.ReadLevel());
            };
            _sourceSearch.RegisterValueChangedCallback(_ => RefreshSources());
            var unavailable = new Label("Unavailable in this build: audio debug factory (no-op), AI file logger (editor only). Native audio and AI categories above remain usable.");
            unavailable.AddToClassList("muted"); unavailable.AddToClassList("wrap"); controls.Add(unavailable);
            _statusLabel = new Label(); _statusLabel.AddToClassList("logging-status"); Add(_statusLabel);
            var filters = Row(this); filters.AddToClassList("logging-filter-row");
            _search = new TextField("Search logs") { name = "logging-search" }; _search.AddToClassList("logging-search"); filters.Add(_search);
            _source = new DropdownField(new List<string> { "All sources" }, 0); _source.style.width = 170; filters.Add(_source);
            _severity = new DropdownField(new[] { "All levels" }.Concat(Levels).ToList(), 0); _severity.style.width = 130; filters.Add(_severity);
            _pause = new Toggle("Pause display") { name = "logging-pause" }; filters.Add(_pause);
            _search.RegisterValueChangedCallback(_ => Filter()); _source.RegisterValueChangedCallback(_ => Filter()); _severity.RegisterValueChangedCallback(_ => Filter());
            _counts = new Label(); _counts.AddToClassList("muted"); Add(_counts);
            _messages = new ListView { name = "logging-messages", fixedItemHeight = 26, selectionType = SelectionType.Single, virtualizationMethod = CollectionVirtualizationMethod.FixedHeight };
            _messages.AddToClassList("logging-messages"); Add(_messages);
            _messages.makeItem = () => { var label = new Label(); label.AddToClassList("logging-line"); label.enableRichText = false; return label; };
            _messages.bindItem = (row, i) => { var entry = _shown[i]; ((Label)row).text = entry.Time + "  " + entry.Level + "  [" + entry.Source + "]  " + entry.Message.Replace('\n', ' ').Replace('\r', ' '); };
            _messages.selectionChanged += selected => { var entry = selected.OfType<LoggingEntryView>().FirstOrDefault(); _detail.SetValueWithoutNotify(entry?.Text ?? ""); };
            _detail = new TextField { name = "logging-detail", multiline = true, isReadOnly = true }; _detail.AddToClassList("logging-detail");
            var actions = Row(this);
            actions.Add(new Button(() => { clear(); _entries = Array.Empty<LoggingEntryView>(); _detail.SetValueWithoutNotify(""); Filter(); }) { name = "logging-clear", text = "Clear history" });
            actions.Add(new Button(() => copy(_detail.value)) { text = "Copy selected" });
            Button detailButton = null;
            detailButton = new Button(() => {
                var show = _detail.style.display == DisplayStyle.None;
                _detail.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
                detailButton.text = show ? "Hide details" : "Show details";
            }) { text = "Show details" }; actions.Add(detailButton);
            actions.Add(new Button(() => export(_shown.ToArray(), false)) { text = "Export filtered text" });
            actions.Add(new Button(() => export(_shown.ToArray(), true)) { text = "Export filtered JSON" });
            _detail.style.display = DisplayStyle.None; Add(_detail);
            WorkbenchScrolling.Configure(this);
            Refresh();
        }
        public void FitViewport(ScrollView page)
        {
            var horizontal = page.horizontalScrollerVisibility;
            page.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            void Resize()
            {
                var viewport = page.contentViewport.resolvedStyle.height;
                var width = page.contentViewport.resolvedStyle.width;
                if (!float.IsNaN(width) && width > 0 && UnityEngine.Mathf.Abs(resolvedStyle.width - width) > 1) style.width = width;
                var total = resolvedStyle.height;
                var current = _messages.resolvedStyle.height;
                if (float.IsNaN(viewport) || float.IsNaN(total) || viewport <= 0) return;
                var target = UnityEngine.Mathf.Max(104, viewport - (total - current) - 4);
                if (UnityEngine.Mathf.Abs(target - current) > 1) _messages.style.height = target;
            }
            EventCallback<GeometryChangedEvent> resize = _ => Resize();
            page.contentViewport.RegisterCallback(resize);
            RegisterCallback(resize);
            RegisterCallback<DetachFromPanelEvent>(_ => { page.contentViewport.UnregisterCallback(resize); page.horizontalScrollerVisibility = horizontal; });
        }
        private static VisualElement Row(VisualElement parent) { var row = new VisualElement(); row.AddToClassList("row"); parent.Add(row); return row; }
        private void RefreshSources()
        {
            _shownSources = _sources().Where(s => s.Name.IndexOf(_sourceSearch.value ?? "", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            _categories.itemsSource = _shownSources; _categories.RefreshItems();
        }
        public void Refresh()
        {
            foreach (var sync in _sync) sync();
            var signature = string.Join("|", _sources().Select(s => s.Name));
            if (_sourceSignature != signature) { _sourceSignature = signature; RefreshSources(); }
            else _categories.RefreshItems();
            _statusLabel.text = _status();
            if (_pause.value) return;
            _entries = _snapshot();
            _source.choices = new[] { "All sources" }.Concat(_entries.Select(e => e.Source).Distinct().OrderBy(s => s)).ToList();
            Filter();
        }
        private void Filter()
        {
            var text = _search.value ?? "";
            _shown = _entries.Where(e => (_source.value == "All sources" || e.Source == _source.value)
                && (_severity.value == "All levels" || e.Level == _severity.value)
                && e.Text.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            _messages.itemsSource = _shown; _messages.RefreshItems();
            _counts.text = _shown.Count + " shown · " + _entries.Length + " retained · newest at bottom";
        }
    }
}
