using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DebugPlus.Common;
using DebugPlus.Runtime;
using EFT.InputSystem;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using Cursor = UnityEngine.Cursor;

namespace DebugPlus.UI;

/// <summary>Original UI Toolkit workbench. Views submit requests; they never mutate game state.</summary>
public sealed partial class WorkbenchWindow : MonoBehaviour
{
    public static bool CapturingInput { get; private set; }
    private static readonly string[] Pages = { "Overview", "Player", "Health", "Items", "Kits", "Teleport", "Bots", "World", "Diagnostics", "Logging", "Settings" };
    private static readonly string[] Descriptions = {
        "Your session, tools, and recent activity in one place.",
        "Control survival systems and inspect your selected player.",
        "Build precise injury scenarios with native health and effect controls.",
        "Search the catalog and create exactly the items you need.",
        "Capture equipment, stage complete loadouts, and review replacements.",
        "Move through your current map with coordinates and saved locations.",
        "Create encounters and control the active AI population.",
        "Inspect doors and shape time and weather for repeatable tests.",
        "Inspect the current scene and export evidence from your session.",
        "Reveal hidden client logs, filter recent messages, and export evidence.",
        "Make this workspace yours. Preferences persist between sessions."
    };
    private static readonly Dictionary<CommandKind, string> CommandPages = new()
    {
        [CommandKind.GodMode] = "Player", [CommandKind.Stamina] = "Player", [CommandKind.Oxygen] = "Player",
        [CommandKind.Energy] = "Player", [CommandKind.Hydration] = "Player", [CommandKind.RefillVitals] = "Player",
        [CommandKind.RepairGear] = "Player", [CommandKind.ClearMalfunction] = "Player",
        [CommandKind.SetHealth] = "Health", [CommandKind.Heal] = "Health", [CommandKind.Damage] = "Health",
        [CommandKind.RestoreLimb] = "Health", [CommandKind.Recover] = "Health", [CommandKind.AddEffect] = "Health",
        [CommandKind.RemoveEffect] = "Health", [CommandKind.RemoveAllEffects] = "Health",
        [CommandKind.SpawnItem] = "Items", [CommandKind.SpawnKit] = "Kits", [CommandKind.EquipKit] = "Kits",
        [CommandKind.Teleport] = "Teleport", [CommandKind.TeleportBack] = "Teleport",
        [CommandKind.SpawnBots] = "Bots", [CommandKind.PauseBots] = "Bots", [CommandKind.ResumeBots] = "Bots",
        [CommandKind.HealBots] = "Bots", [CommandKind.KillBots] = "Bots", [CommandKind.DespawnBots] = "Bots",
        [CommandKind.Door] = "World", [CommandKind.SetTime] = "World", [CommandKind.SetWeather] = "World",
        [CommandKind.RestoreEnvironment] = "World", [CommandKind.StartRaid] = "Diagnostics", [CommandKind.ReloadBackend] = "Diagnostics"
    };
    private WorkbenchRuntime Runtime => WorkbenchRuntime.Instance;
    private AssetBundle _bundle;
    private UIDocument _document;
    private PanelSettings _panel;
    private VisualElement _window;
    private ScrollView _content;
    private DropdownField _target;
    private TextField _search;
    private Label _details;
    private Label _feedback;
    private readonly List<string> _targetIds = new();
    private readonly Dictionary<VisualElement, (Func<string> Reason, string Hint)> _availability = new();
    private readonly List<Tuple<DateTime, string, string>> _recent = new();
    private readonly List<Action> _refreshers = new();
    private string _selectedTarget;
    private string _page = "Overview";
    private string _session;
    private bool _visible;
    private bool _loading;
    private bool _busy;
    private CursorLockMode _previousLock;
    private bool _previousCursor;
    private float _refreshAt;
    private float _smoothedFrame;
    private Harmony _inputHarmony;
    private KitDocument _kit;
    private string _kitPreviewHash;
    private string _kitPreviewTarget;
    private string _kitPreviewId;
    private bool _kitPreviewFoundInRaid;
    private int _viewGeneration;
    private string _itemQuery = "";
    private string _itemCategory = "All categories";
    private string _selectedItemId;
    private bool _favoriteItemsOnly;
    private string TargetId => _selectedTarget ?? Runtime?.LocalProfileId;

    private void Awake()
    {
        _inputHarmony = new Harmony("com.cj.debugplus.workbench.input");
        _inputHarmony.Patch(AccessTools.Method(typeof(InputManager), nameof(InputManager.DispatchInput)),
            prefix: new HarmonyMethod(typeof(WorkbenchWindow), nameof(CaptureGameInput)));
        SceneManager.activeSceneChanged += OnSceneChanged;
    }

    private static void CaptureGameInput(List<ECommand> __0, float[] __1)
    {
        if (!CapturingInput) return;
        __0?.Clear();
        if (__1 != null) Array.Clear(__1, 0, __1.Length);
    }

    private void Update()
    {
        if (Runtime == null || Runtime.IsHeadless) return;
        var key = Enum.TryParse(Runtime.Preferences.ToggleKey, true, out KeyCode parsed) ? parsed : KeyCode.F10;
        if (Input.GetKeyDown(key)) Toggle();
        if (!_visible) return;
        _smoothedFrame = Mathf.Lerp(_smoothedFrame, Time.unscaledDeltaTime * 1000, .08f);
        if (Input.GetKeyDown(KeyCode.Escape)) { Close(); return; }
        if (_session != Runtime.SessionId) { Close(); return; }
        if (Time.unscaledTime < _refreshAt) return;
        _refreshAt = Time.unscaledTime + .5f;
        RefreshLive();
    }

    private void LateUpdate()
    {
        if (!CapturingInput) return;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    public void Toggle()
    {
        if (_visible) { Close(); return; }
        if (Runtime == null || Runtime.IsHeadless || _loading) return;
        if (_document == null && !LoadAssets()) return;
        _previousLock = Cursor.lockState;
        _previousCursor = Cursor.visible;
        _visible = CapturingInput = true;
        if (_session != Runtime.SessionId) _selectedTarget = Runtime.LocalProfileId;
        _session = Runtime.SessionId;
        _document.rootVisualElement.pickingMode = PickingMode.Position;
        _window.style.display = DisplayStyle.Flex;
        RefreshTargets();
        ApplyGeometry();
        ShowPage(Pages.Contains(Runtime.Preferences.ActivePage) ? Runtime.Preferences.ActivePage : "Overview");
        RefreshLive();
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    private bool LoadAssets()
    {
        _loading = true;
        try
        {
            var directory = Path.GetDirectoryName(typeof(WorkbenchWindow).Assembly.Location);
            var path = Path.Combine(directory, "DebugPlus", "debugplus.workbench");
            if (!File.Exists(path)) path = Path.Combine(directory, "debugplus.workbench");
            if (!File.Exists(path)) throw new FileNotFoundException("Workbench asset bundle is missing. Install the matching DebugPlus assets alongside the plugin.", path);
            _bundle = AssetBundle.LoadFromFile(path);
            if (_bundle == null) throw new InvalidDataException("Workbench asset bundle could not be loaded.");
            var tree = _bundle.LoadAsset<VisualTreeAsset>("Workbench.uxml");
            var style = _bundle.LoadAsset<StyleSheet>("Workbench.uss");
            var settings = _bundle.LoadAsset<PanelSettings>("WorkbenchPanel.asset");
            if (tree == null || style == null || settings == null) throw new InvalidDataException("Workbench assets do not match this plugin.");
            _panel = Instantiate(settings);
            _panel.sortingOrder = 32000;
            _panel.scaleMode = PanelScaleMode.ConstantPixelSize;
            _document = gameObject.AddComponent<UIDocument>();
            _document.panelSettings = _panel;
            _document.sortingOrder = 32000;
            tree.CloneTree(_document.rootVisualElement);
            _document.rootVisualElement.styleSheets.Add(style);
            // Native dropdowns attach beside the document. Give this dedicated
            // panel the same user stylesheet without changing the default theme.
            void ApplyPanelStyle()
            {
                var root = _document.rootVisualElement.panel?.visualTree;
                if (root != null && !root.styleSheets.Contains(style)) root.styleSheets.Add(style);
            }
            _document.rootVisualElement.RegisterCallback<AttachToPanelEvent>(_ => ApplyPanelStyle());
            ApplyPanelStyle();
            _document.rootVisualElement.style.width = Length.Percent(100);
            _document.rootVisualElement.style.height = Length.Percent(100);
            _document.rootVisualElement.pickingMode = PickingMode.Ignore;
            _window = _document.rootVisualElement.Q("workbench");
            WorkbenchScrolling.Configure(_window);
            _window.style.display = DisplayStyle.None;
            _content = _window.Q<ScrollView>("content");
            _target = _window.Q<DropdownField>("target");
            _search = _window.Q<TextField>("command-search");
            _details = _window.Q<Label>("context-details");
            _feedback = _window.Q<Label>("feedback-message");
            _window.Q<Button>("close").clicked += Close;
            _target.RegisterValueChangedCallback(e =>
            {
                var index = _target.choices.IndexOf(e.newValue);
                if (index >= 0 && index < _targetIds.Count) _selectedTarget = _targetIds[index];
                _kitPreviewHash = null;
                ShowPage(_page);
                RefreshLive();
            });
            _search.RegisterValueChangedCallback(e => { if (string.IsNullOrWhiteSpace(e.newValue)) ShowPage(_page); else ShowSearch(e.newValue); });
            BuildNavigation();
            _window.RegisterCallback<ChangeEvent<string>>(_ => RefreshAvailability());
            _window.RegisterCallback<ChangeEvent<float>>(_ => RefreshAvailability());
            _window.RegisterCallback<ChangeEvent<int>>(_ => RefreshAvailability());
            _window.RegisterCallback<ChangeEvent<bool>>(_ => RefreshAvailability());
            ConfigurePointerDrag(_window.Q("titlebar"), false);
            ConfigurePointerDrag(_window.Q("resize-handle"), true);
            return true;
        }
        catch (Exception ex)
        {
            Runtime.Report(ex.Message, CommandStatus.Rejected);
            if (_document != null) Destroy(_document);
            if (_panel != null) Destroy(_panel);
            if (_bundle != null) _bundle.Unload(false);
            _document = null;
            _panel = null;
            _bundle = null;
            return false;
        }
        finally { _loading = false; }
    }

    public void Close()
    {
        if (!_visible) return;
        // Native dropdown popups are siblings of the document, so hiding the
        // workbench alone would leave a menu over the game with input released.
        var panelRoot = _document?.rootVisualElement.panel?.visualTree;
        if (panelRoot != null)
            foreach (var popup in panelRoot.Query<VisualElement>(className: "unity-base-dropdown").ToList()) popup.RemoveFromHierarchy();
        _visible = CapturingInput = false;
        if (_document != null) _document.rootVisualElement.pickingMode = PickingMode.Ignore;
        if (_window != null) { _window.style.display = DisplayStyle.None; _window.Blur(); }
        Cursor.lockState = _previousLock;
        Cursor.visible = _previousCursor;
        Runtime?.SavePreferences();
    }

    private void OnApplicationFocus(bool focused) { if (!focused) Close(); }
    private void OnSceneChanged(Scene previous, Scene current) => Close();
    private void OnDisable() => Close();
    private void OnDestroy()
    {
        Close();
        SceneManager.activeSceneChanged -= OnSceneChanged;
        _inputHarmony?.UnpatchSelf();
        if (_panel != null) Destroy(_panel);
        if (_bundle != null) _bundle.Unload(false);
    }

    private void ApplyGeometry()
    {
        var p = Runtime.Preferences;
        p.UiScale = Mathf.Clamp(p.UiScale <= 0 ? 1 : p.UiScale, .7f, 1.6f);
        _panel.scale = p.UiScale;
        var screenW = Screen.width / p.UiScale;
        var screenH = Screen.height / p.UiScale;
        var minW = Mathf.Min(950, screenW - 20);
        var minH = Mathf.Min(560, screenH - 20);
        p.UiWidth = Mathf.Clamp(p.UiWidth <= 0 ? 1340 : p.UiWidth, minW, screenW - 20);
        p.UiHeight = Mathf.Clamp(p.UiHeight <= 0 ? 840 : p.UiHeight, minH, screenH - 20);
        p.UiX = Mathf.Clamp(p.UiX <= 0 ? (screenW - p.UiWidth) / 2 : p.UiX, 10, Mathf.Max(10, screenW - p.UiWidth - 10));
        p.UiY = Mathf.Clamp(p.UiY <= 0 ? (screenH - p.UiHeight) / 2 : p.UiY, 10, Mathf.Max(10, screenH - p.UiHeight - 10));
        _window.style.left = p.UiX;
        _window.style.top = p.UiY;
        _window.style.width = p.UiWidth;
        _window.style.height = p.UiHeight;
        _window.Q("context-title").parent.style.display = p.UiWidth < 1100 || _page is "Items" or "Kits" or "Logging" ? DisplayStyle.None : DisplayStyle.Flex;
    }

    private void ConfigurePointerDrag(VisualElement grip, bool resize)
    {
        var dragging = false;
        var start = Vector2.zero;
        var initial = Vector2.zero;
        grip.RegisterCallback<PointerDownEvent>(e =>
        {
            if (e.button != 0 || e.target is Button) return;
            dragging = true;
            start = e.position;
            var p = Runtime.Preferences;
            initial = resize ? new Vector2(p.UiWidth, p.UiHeight) : new Vector2(p.UiX, p.UiY);
            grip.CapturePointer(e.pointerId);
            e.StopPropagation();
        });
        grip.RegisterCallback<PointerMoveEvent>(e =>
        {
            if (!dragging) return;
            var next = initial + (Vector2)e.position - start;
            var p = Runtime.Preferences;
            if (resize) { p.UiWidth = next.x; p.UiHeight = next.y; }
            else { p.UiX = next.x; p.UiY = next.y; }
            ApplyGeometry();
            e.StopPropagation();
        });
        grip.RegisterCallback<PointerUpEvent>(e =>
        {
            if (!dragging) return;
            dragging = false;
            grip.ReleasePointer(e.pointerId);
            Runtime.SavePreferences();
        });
        grip.RegisterCallback<PointerCaptureOutEvent>(_ => dragging = false);
    }

    private void BuildNavigation()
    {
        var nav = _window.Q("navigation");
        nav.Clear();
        for (var i = 0; i < Pages.Length; i++)
        {
            var page = Pages[i];
            var button = new Button(() => { _search.SetValueWithoutNotify(""); ShowPage(page); }) { text = page, name = "nav-" + page };
            button.AddToClassList("nav-button");
            var slot = new VisualElement(); slot.AddToClassList("nav-slot"); slot.Add(button); nav.Add(slot);
        }
    }

    private void RefreshTargets()
    {
        var targets = Runtime.Targets;
        var ids = targets.Select(t => t.Id).ToList();
        var labels = TargetLabels(targets);
        if (ids.Count == 0) { ids.Add(Runtime.LocalProfileId); labels.Add("Your PMC"); }
        if (!string.IsNullOrEmpty(TargetId) && !ids.Contains(TargetId)) { ids.Insert(0, TargetId); labels.Insert(0, "Selected player unavailable"); }
        var index = Math.Max(0, ids.IndexOf(TargetId));
        _targetIds.Clear();
        _targetIds.AddRange(ids);
        _target.choices = labels;
        _target.SetValueWithoutNotify(labels[index]);
        _selectedTarget = ids[index];
    }

    private void RefreshLive()
    {
        if (Runtime == null || _window == null) return;
        RefreshTargets();
        var unavailablePage = Runtime.PageUnavailable(_page, TargetId);
        if (_page != "Overview" && string.IsNullOrWhiteSpace(_search.value) && unavailablePage.Length > 0)
        {
            ShowPage("Overview"); ShowFeedback(CommandStatus.Rejected, unavailablePage); return;
        }
        _window.Q<Label>("session-label").text = Runtime.InRaid ? (Runtime.IsCoop ? "CO-OP" : "SOLO") + "  /  " + Runtime.MapName : "MAIN MENU";
        _window.Q<Label>("authority-label").text = Runtime.IsAuthorized ? (Runtime.IsCoop && !Runtime.CoopReady ? "Waiting for connection" : "Administrator") : "View only";
        _window.Q<Label>("authority-label").tooltip = Runtime.Status;
        _window.Q<Label>("shortcut-hint").text = Runtime.Preferences.ToggleKey.ToUpperInvariant() + "   TOGGLE WORKBENCH   ·   ESC   CLOSE";
        _window.Q<Label>("performance-label").text = $"{_smoothedFrame:0.0} MS  /  {(1000 / Mathf.Max(1, _smoothedFrame)):0} FPS";
        _details.text = _page is "World" or "Bots" or "Diagnostics" ? Runtime.InspectWorld(_page == "Diagnostics") : Runtime.InspectPlayer(TargetId, false);
        foreach (var refresh in _refreshers) refresh();
        RefreshAvailability();
        var log = _window.Q<ScrollView>("activity");
        log.Clear();
        foreach (var entry in Runtime.Activity.Reverse().Take(30))
        {
            var label = new Label($"{entry.Time:HH:mm:ss}  {entry.Status.ToString().ToUpperInvariant()}  {entry.Message}");
            label.AddToClassList("log-entry");
            label.AddToClassList(entry.Status.ToString().ToLowerInvariant());
            log.Add(label);
        }
    }

    private void BeginPage(string title, string description)
    {
        _viewGeneration++;
        _content.Clear();
        _content.scrollOffset = Vector2.zero;
        _availability.Clear();
        _refreshers.Clear();
        _window.Q<Label>("page-title").text = title;
        _window.Q<Label>("page-description").text = description;
        _window.Q<Label>("page-kicker").text = "YOUR WORKBENCH";
        ApplyGeometry();
        _window.Q<Label>("context-title").text = _page is "Bots" or "World" or "Diagnostics" ? "Session inspection" : "Selected player";
    }

    private void ShowPage(string page)
    {
        if (Runtime.PageUnavailable(page, TargetId).Length > 0)
        {
            ShowFeedback(CommandStatus.Rejected, Runtime.PageUnavailable(page, TargetId));
            page = "Overview";
        }
        _page = page;
        Runtime.Preferences.ActivePage = page;
        BeginPage(page, Descriptions[Math.Max(0, Array.IndexOf(Pages, page))]);
        foreach (var name in Pages) _window.Q<Button>("nav-" + name).EnableInClassList("selected", name == page);
        if (Runtime.Policy?.Authorized != true && page != "Logging") Notice(_content, "You're browsing in view-only mode. Your server administrator can enable access for your profile in server settings.");
        else if (!Runtime.InRaid && page is not "Overview" and not "Items" and not "Kits" and not "Settings" and not "Diagnostics" and not "Logging") Notice(_content, "These controls become available in an active raid.");
        switch (page)
        {
            case "Overview": Overview(); break;
            case "Player": Player(); break;
            case "Health": Health(); break;
            case "Items": Items(); break;
            case "Kits": Kits(); break;
            case "Teleport": Teleport(); break;
            case "Bots": Bots(); break;
            case "World": World(); break;
            case "Diagnostics": Diagnostics(); break;
            case "Logging": LoggingPage(); break;
            case "Settings": Settings(); break;
        }
        WorkbenchScrolling.Configure(_content);
        RefreshLive();
    }

    private void ShowSearch(string term)
    {
        BeginPage("Find a tool", "Choose a result to open its controls.");
        var found = 0;
        foreach (var page in Pages.Where(p => p.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0))
        {
            var selected = page;
            PageLink(_content, page + "  →", selected);
            found++;
        }
        foreach (var pair in CommandPages.Where(p => Humanize(p.Key.ToString()).IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 || p.Key.ToString().IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0))
        {
            var selected = pair.Value;
            PageLink(_content, Humanize(pair.Key.ToString()) + "  /  " + pair.Value, selected, pair.Key);
            found++;
        }
        if (found == 0) Notice(_content, "No matching tools. Try health, spawn, stamina, door, weather, or teleport.");
        RefreshAvailability();
    }

    private void Overview()
    {
        var session = Card("Session at a glance", "Use the target selector above before changing a player.");
        var state = Text(session, "", "metric");
        _refreshers.Add(() => state.text = Runtime.InRaid ? Runtime.MapName + "  ·  " + (Runtime.IsCoop ? "Fika session" : "Solo raid") : "Main menu  ·  Inventory tools ready");
        Text(session, "Choose a player above, then open a tool below.", "muted");
        var quick = Card("Start a test", "Open a focused tool and configure your scenario.");
        var row = Row(quick);
        foreach (var page in new[] { "Player", "Health", "Items", "Kits", "Teleport", "Bots" })
        {
            var selected = page;
            PageLink(row, page, selected);
        }
        var favorites = Card("Favorite tools", "Use the star beside a command to keep its page here.");
        foreach (var favorite in Runtime.Preferences.FavoriteCommands)
            if (Enum.TryParse(favorite, out CommandKind kind) && CommandPages.TryGetValue(kind, out var page))
            {
                var selected = page;
                PageLink(favorites, "★  " + Humanize(kind.ToString()), selected, kind);
            }
        if (Runtime.Preferences.FavoriteCommands.Count == 0) Text(favorites, "No favorites yet.", "empty");
        var recent = Card("Recent actions", "Open the relevant controls to repeat an action with fresh parameters.");
        foreach (var entry in _recent.AsEnumerable().Reverse().Take(8))
        {
            var selected = entry.Item3;
            PageLink(recent, $"{entry.Item1:HH:mm}  {entry.Item2}", selected);
        }
        if (_recent.Count == 0) Text(recent, "Your actions will appear here.", "empty");
    }

    private void Player()
    {
        var survival = Card("Survival overrides", "Overrides reset each raid. Enable and disable are explicit commands to the selected player.");
        GateSection(survival, () => ActionUnavailable(CommandKind.GodMode));
        foreach (var kind in new[] { CommandKind.GodMode, CommandKind.Stamina, CommandKind.Oxygen, CommandKind.Energy, CommandKind.Hydration })
        {
            var command = kind;
            var row = Row(survival);
            var caption = Text(row, kind switch { CommandKind.Stamina => "Unlimited body / arm stamina", CommandKind.Oxygen => "Unlimited oxygen", CommandKind.Energy => "Hold energy", CommandKind.Hydration => "Hold hydration", _ => "God mode" });
            caption.style.width = 222;
            Action(row, "Enable", command, () => new CommandArguments { Enabled = true }, true, true);
            Action(row, "Disable", command, () => new CommandArguments { Enabled = false }, true, false);
        }
        var maintenance = Card("Recovery & equipment", "God mode prevents ordinary damage. Debug health commands still apply.");
        GateSection(maintenance, () => ActionUnavailable(CommandKind.RepairGear));
        Action(maintenance, "Refill energy and hydration", CommandKind.RefillVitals);
        Action(maintenance, "Repair equipped gear", CommandKind.RepairGear);
        Action(maintenance, "Clear weapon malfunction", CommandKind.ClearMalfunction);
    }

    private void Health()
    {
        var health = Card("Body-part health", "Inspect live health at the right. Health commands use the native controller and respect its limits.");
        GateSection(health, () => ActionUnavailable(CommandKind.SetHealth));
        var healthFields = FormRow(health);
        var part = Choice(healthFields, "Body part", new[] { "Head", "Chest", "Stomach", "Left Arm", "Right Arm", "Left Leg", "Right Leg" }, "Chest");
        var amount = Number(healthFields, "Health / damage", 35);
        Func<CommandArguments> args = () => new CommandArguments { BodyPart = part.value.Replace(" ", ""), Value = amount.value };
        var row = Row(health);
        Action(row, "Set health", CommandKind.SetHealth, args);
        Action(row, "Heal", CommandKind.Heal, args);
        Action(row, "Damage", CommandKind.Damage, args);
        Action(health, "Restore selected limb", CommandKind.RestoreLimb, args);
        Action(health, "Full recovery", CommandKind.Recover);
        var effects = Card("Health effects", "Duration is in seconds. Native effects may restrict body parts, intensity, or removal.");
        GateSection(effects, () => ActionUnavailable(CommandKind.AddEffect));
        var effect = Choice(effects, "Effect", new[] { "Light Bleeding", "Heavy Bleeding", "Fracture", "Pain", "Tremor", "Exhaustion" }, "Light Bleeding");
        var effectFields = FormRow(effects);
        var duration = Number(effectFields, "Duration", 60);
        var strength = Number(effectFields, "Intensity", 1);
        Func<CommandArguments> effectArgs = () => new CommandArguments { BodyPart = part.value.Replace(" ", ""), Effect = effect.value.Replace(" ", ""), Duration = duration.value, Strength = strength.value };
        Action(effects, "Add effect", CommandKind.AddEffect, effectArgs);
        Action(effects, "Remove selected effect", CommandKind.RemoveEffect, effectArgs);
        Action(effects, "Remove all effects", CommandKind.RemoveAllEffects);
    }

    private void Items()
    {
        var layout = new VisualElement(); layout.AddToClassList("catalog-layout"); _content.Add(layout);
        var browser = new VisualElement(); browser.AddToClassList("catalog-pane"); layout.Add(browser);
        var query = Field(browser, "Search items", _itemQuery);
        query.tooltip = "Search by item name. Template IDs also work if you have one.";
        var filters = Row(browser); filters.AddToClassList("catalog-toolbar");
        var categories = new[] { "All categories" }.Concat(Runtime.Catalog.Select(i => i.Category ?? "Other").Distinct().OrderBy(v => v)).ToArray();
        if (!categories.Contains(_itemCategory)) _itemCategory = categories[0];
        var category = Choice(filters, "Category", categories, _itemCategory);
        var favorites = Check(filters, "Favorites", _favoriteItemsOnly);
        var countLabel = Text(browser, "", "muted");
        var grid = new ItemGridView(Runtime.LoadItemIcon, id => Runtime.Preferences.FavoriteItems.Contains(id)); browser.Add(grid);
        grid.FitViewport(_content);
        var spawn = new VisualElement(); spawn.AddToClassList("spawn-pane"); layout.Add(spawn);
        var art = new Image { scaleMode = ScaleMode.ScaleToFit }; art.AddToClassList("selected-item-art"); spawn.Add(art);
        var title = Text(spawn, "Choose an item", "selected-item-title");
        var subtitle = Text(spawn, "Select a card to set its quantity and spawn it.", "card-description");
        var quantity = Integer(spawn, "Quantity", 1);
        quantity.RegisterValueChangedCallback(e => quantity.SetValueWithoutNotify(Mathf.Clamp(e.newValue, 1, Protocol.MaxCount)));
        var options = new Foldout { text = "Condition & raid status", value = false };
        var condition = new Slider("Condition (%)", 0, 100) { value = 100, showInputField = true }; options.Add(condition);
        var fir = Check(options, "Found in raid", false);
        var advanced = new Foldout { text = "Technical details", value = false }; advanced.AddToClassList("technical");
        var template = Field(advanced, "Template", ""); template.isReadOnly = true;
        PlainButton(advanced, "Copy template ID", () => { if (_selectedItemId != null) GUIUtility.systemCopyBuffer = _selectedItemId; });
        CatalogItemView selected = null;
        async void SelectItem(CatalogItemView item)
        {
            selected = item; _selectedItemId = item.Id; grid.Select(item.Id);
            title.text = item.Name; subtitle.text = item.Category; art.sprite = item.Icon;
            template.SetValueWithoutNotify(item.Id);
            RefreshLive();
            try { var sprite = await Runtime.LoadItemIcon(item.Id); if (selected == item) art.sprite = sprite; }
            catch { /* A missing thumbnail does not prevent item selection. */ }
        }
        grid.Selected += SelectItem;
        void Filter()
        {
            _itemQuery = query.value; _itemCategory = category.value; _favoriteItemsOnly = favorites.value;
            var matches = Runtime.Catalog.Where(i => (category.value == "All categories" || i.Category == category.value)
                && (!favorites.value || Runtime.Preferences.FavoriteItems.Contains(i.Id))
                && ((i.Name ?? "").IndexOf(query.value, StringComparison.OrdinalIgnoreCase) >= 0 || i.Id.IndexOf(query.value, StringComparison.OrdinalIgnoreCase) >= 0))
                .Select(i => new CatalogItemView { Id = i.Id, Name = i.Name, Category = i.Category, Icon = i.Icon }).ToArray();
            grid.SetItems(matches); grid.Select(_selectedItemId);
            countLabel.text = matches.Length + " items · select a card · star your favorites";
        }
        grid.FavoriteToggled += item =>
        {
            if (!Runtime.Preferences.FavoriteItems.Remove(item.Id)) Runtime.Preferences.FavoriteItems.Add(item.Id);
            Runtime.SavePreferences(); if (favorites.value) Filter();
        };
        query.RegisterValueChangedCallback(_ => Filter()); category.RegisterValueChangedCallback(_ => Filter()); favorites.RegisterValueChangedCallback(_ => Filter());
        var toolbar = Row(browser);
        PlainButton(toolbar, "Clear filters", () => { query.SetValueWithoutNotify(""); category.SetValueWithoutNotify("All categories"); favorites.SetValueWithoutNotify(false); Filter(); });
        var generation = _viewGeneration;
        var loading = false;
        async void Reload()
        {
            if (loading) return;
            loading = true; countLabel.text = "Loading the item catalog…";
            try { await Runtime.RefreshCatalog(); if (_viewGeneration == generation) { if (Runtime.Catalog.Count > 0) ShowPage("Items"); else countLabel.text = "Catalog is not ready yet. Try Refresh catalog once the profile has loaded."; } }
            catch (Exception ex) { if (_viewGeneration == generation) { countLabel.text = "Catalog unavailable. Try refresh."; ShowFeedback(CommandStatus.Rejected, ex.Message); } }
            finally { loading = false; }
        }
        PlainButton(toolbar, "Refresh catalog", Reload);
        Func<CommandArguments> args = () => new CommandArguments { TemplateId = selected?.Id ?? "", Count = quantity.value, Value = condition.value, FoundInRaid = fir.value, Destination = Runtime.InRaid ? "Inventory" : "Stash" };
        var spawnButton = Action(spawn, Runtime.InRaid ? "Add to inventory" : "Add to stash", CommandKind.SpawnItem, args, false, false, "primary");
        var groundButton = Runtime.InRaid ? Action(spawn, "Place on the ground", CommandKind.SpawnItem, () => { var a = args(); a.Destination = "Ground"; return a; }, true, false) : null;
        var conditionSummary = Text(spawn, "100% condition · Not found in raid", "card-description");
        void RefreshCondition() => conditionSummary.text = condition.value.ToString("0") + "% condition · " + (fir.value ? "Found in raid" : "Not found in raid");
        condition.RegisterValueChangedCallback(_ => RefreshCondition()); fir.RegisterValueChangedCallback(_ => RefreshCondition());
        spawn.Add(options);
        Text(spawn, Runtime.InRaid ? "Added to the selected player's inventory. Ground loot is a separate option." : "Added to your PMC stash. Make room first if it is full.", "card-description");
        spawn.Add(advanced);
        // Keep editable values available for correction when only their input is invalid.
        BindAvailability(quantity, () => selected == null ? "Select an item first." : ActionUnavailable(CommandKind.SpawnItem));
        BindAvailability(options, () => selected == null ? "Select an item first." : ActionUnavailable(CommandKind.SpawnItem));
        BindAvailability(advanced, () => selected == null ? "Select an item first." : "");
        Filter();
        var previous = Runtime.Catalog.FirstOrDefault(i => i.Id == _selectedItemId);
        if (previous != null) SelectItem(new CatalogItemView { Id = previous.Id, Name = previous.Name, Category = previous.Category, Icon = previous.Icon });
        if (Runtime.Catalog.Count == 0) { countLabel.text = "Loading the item catalog…"; browser.schedule.Execute(Reload).ExecuteLater(1); }
    }
    private void Kits()
    {
        Runtime.RepairSavedKitSlots();
        _kit ??= Runtime.Kits.FirstOrDefault(k => ItemTrees.SelectEquipment(k.Items).Count > 0) ?? Runtime.Kits.FirstOrDefault();
        _kitPreviewHash = null;
        var library = Card("Kit library", "Only included equipment slots are replaced. Pockets, secure containers, and special slots are excluded when capturing.");
        foreach (var kit in Runtime.Kits)
        {
            var selected = kit;
            var usableCount = ItemTrees.SelectEquipment(kit.Items).Count;
            PlainButton(library, (_kit?.Id == kit.Id ? "●  " : "○  ") + kit.Name + "  ·  " + (usableCount == 0 ? "Empty loadout" : usableCount + " equipment items"), () => { _kit = selected; _kitPreviewHash = null; ShowPage("Kits"); });
        }
        if (Runtime.Kits.Count == 0) Text(library, "No saved kits. Capture your equipment or import a kit below.", "empty");
        var captureName = Field(library, "New kit name", "Field test kit");
        void CaptureEquipment()
        {
            try
            {
                var items = Runtime.CaptureKit(TargetId);
                if (items.Count == 0) throw new InvalidOperationException("No equipment was available to capture.");
                _kit = new KitDocument { Name = captureName.value.Trim(), Items = items };
                Runtime.SaveKit(_kit);
                ShowPage("Kits");
                ShowFeedback(CommandStatus.Completed, "Saved " + _kit.Name + ".");
            }
            catch (Exception ex) { ShowFeedback(CommandStatus.Rejected, ex.Message); }
        }
        var capture = PlainButton(library, "Capture selected equipment", CaptureEquipment);
        BindAvailability(capture, () => _busy ? "Wait for the current command to finish." : string.IsNullOrWhiteSpace(captureName.value) ? "Enter a name for this kit." : Runtime.CaptureUnavailable(TargetId));
        var native = Card("Native builds", "Import a weapon or equipment build as an editable DebugPlus kit.");
        var browse = PlainButton(native, "Browse native builds", () =>
        {
            try
            {
                var builds = Runtime.NativeBuilds();
                if (builds.Count == 0) { ShowFeedback(CommandStatus.Completed, "No native builds are available for this profile."); return; }
                foreach (var build in builds)
                {
                    var selected = build;
                    PlainButton(native, build.Name + "  ·  " + build.Items.Count + " items", () => { _kit = selected; Runtime.SaveKit(_kit); ShowPage("Kits"); });
                }
            }
            catch (Exception ex) { ShowFeedback(CommandStatus.Rejected, ex.Message); }
        });
        BindAvailability(browse, () => Runtime.HasNativeBuilds ? "" : "Save a weapon or equipment build in the game first.");
        if (_kit != null)
        {
            var selected = Card("Selected kit", "Spawn creates the kit without replacing equipment. Equipping requires a fresh replacement preview.");
            var selectedKit = _kit;
            var clearEquipment = ItemTrees.SelectEquipment(selectedKit.Items).Count == 0;
            var included = new HashSet<string>(_kit.Items.Where(i => string.IsNullOrEmpty(i.ParentId) && !ItemTrees.ExcludedSlots.Contains(i.SlotId)).Select(i => i.Id));
            var selectionRevision = 0;
            Button replace = null;
            Label preview = null;
            List<DebugItemRecord> SelectedItems() => ItemTrees.SelectEquipment(selectedKit.Items, included);
            var inclusionToggles = new List<Toggle>();
            var name = Field(selected, "Kit name", _kit.Name);
            Text(selected, _kit.Items.Count + " items including nested contents", "muted");
            Text(selected, "Included equipment slots", "section-title");
            var equipment = new VisualElement(); equipment.AddToClassList("kit-equipment"); selected.Add(equipment);
            foreach (var root in _kit.Items.Where(i => string.IsNullOrEmpty(i.ParentId)))
            {
                var rootId = root.Id;
                var card = new VisualElement(); card.AddToClassList("kit-equipment-card"); equipment.Add(card);
                var label = string.IsNullOrEmpty(root.SlotId) ? "Loose item" : WorkbenchRuntime.FriendlySlot(root.SlotId);
                var protectedSlot = ItemTrees.ExcludedSlots.Contains(root.SlotId);
                var include = Check(card, label, !protectedSlot);
                if (!protectedSlot) inclusionToggles.Add(include);
                if (protectedSlot) { include.SetEnabled(false); include.tooltip = "This personal equipment slot is excluded from kit spawning and replacement."; card.AddToClassList("excluded"); }
                var icon = new Image { scaleMode = ScaleMode.ScaleToFit }; card.Add(icon);
                SetIcon(icon, root.TemplateId);
                Text(card, Runtime.ItemName(root.TemplateId), "wrap");
                var children = new List<DebugItemRecord>();
                void Children(string parent)
                {
                    foreach (var child in selectedKit.Items.Where(i => i.ParentId == parent)) { children.Add(child); Children(child.Id); }
                }
                Children(rootId);
                if (children.Count > 0)
                {
                    var contents = new Foldout { text = children.Count + " attachments / contents", value = false }; card.Add(contents);
                    var populated = false;
                    contents.RegisterValueChangedCallback(e =>
                    {
                        if (!e.newValue || populated) return;
                        populated = true;
                        foreach (var child in children) Text(contents, Runtime.ItemName(child.TemplateId), "wrap");
                    });
                }
                include.RegisterValueChangedCallback(e =>
                {
                    if (e.newValue) included.Add(rootId); else included.Remove(rootId);
                    card.EnableInClassList("excluded", !e.newValue);
                    selectionRevision++; _kitPreviewHash = null;
                    replace?.SetEnabled(false);
                    if (preview != null) preview.text = WorkbenchAvailability.KitPreviewPrompt(SelectedItems().Count > 0, clearEquipment);
                });
            }
            var row = Row(selected);
            var rename = PlainButton(row, "Save name", () => { _kit.Name = name.value.Trim(); Runtime.SaveKit(_kit); ShowPage("Kits"); });
            BindAvailability(rename, () => string.IsNullOrWhiteSpace(name.value) ? "Enter a kit name." : "");
            PlainButton(row, "Export JSON", () => SafeReport(() => Runtime.ExportKit(_kit)));
            PlainButton(row, "Delete saved kit", () => { Runtime.DeleteKit(_kit.Id); _kit = null; ShowPage("Kits"); }, "danger");
            var fir = Check(selected, "Found in raid", false);
            fir.RegisterValueChangedCallback(_ =>
            {
                selectionRevision++;
                _kitPreviewHash = null;
                replace?.SetEnabled(false);
                if (preview != null) preview.text = WorkbenchAvailability.KitPreviewPrompt(SelectedItems().Count > 0, clearEquipment);
            });
            var selectionSummary = Text(selected, "", "selection-note");
            _refreshers.Add(() => selectionSummary.text = selectedKit.Name + (clearEquipment ? " · Empty loadout: remove worn equipment" : " · " + SelectedItems().Count + " equipment items included"));
            if (inclusionToggles.Count > 0)
                PlainButton(selected, "Include all equipment", () => { foreach (var toggle in inclusionToggles) toggle.value = true; RefreshLive(); });
            else
            {
                Text(selected, "Applying this empty kit removes and deletes worn gear, including contents. Pockets, secure container, special slots and your personal dogtag are kept. Preview lists exactly what will be deleted.", "notice");
            }
            if (!clearEquipment)
            {
            Action(selected, Runtime.InRaid ? "Spawn kit in inventory" : "Spawn kit in stash", CommandKind.SpawnKit, () => new CommandArguments { Items = SelectedItems(), FoundInRaid = fir.value, Destination = Runtime.InRaid ? "Inventory" : "Stash" }, false);
            ExplainAvailability(selected, "Kit spawning", () => ActionUnavailable(CommandKind.SpawnKit, new CommandArguments { Items = SelectedItems() }));
            if (Runtime.InRaid) Action(selected, "Spawn kit as ground loot", CommandKind.SpawnKit, () => new CommandArguments { Items = SelectedItems(), FoundInRaid = fir.value, Destination = "Ground" });
            }
            preview = Text(selected, WorkbenchAvailability.KitPreviewPrompt(SelectedItems().Count > 0, clearEquipment), "preview");
            var previewButton = PlainButton(selected, clearEquipment ? "Preview removing worn equipment" : "Preview equipment replacement", async () =>
            {
                if (_busy) return;
                var requestedTarget = TargetId;
                var requestedRevision = selectionRevision;
                var requestedView = _viewGeneration;
                var requestedFoundInRaid = fir.value;
                _kitPreviewHash = null;
                _busy = true;
                try
                {
                    replace.SetEnabled(false);
                    RefreshLive();
                    preview.text = "Requesting the selected player's authoritative equipment preview…";
                    var result = await Runtime.PreviewReplacementAsync(requestedTarget, SelectedItems(), requestedFoundInRaid, clearEquipment);
                    if (TargetId != requestedTarget || _kit?.Id != selectedKit.Id || requestedRevision != selectionRevision || requestedView != _viewGeneration || fir.value != requestedFoundInRaid)
                    { ShowFeedback(CommandStatus.Rejected, "Selection changed while preparing the preview. Preview the current selection again."); return; }
                    _kitPreviewTarget = requestedTarget;
                    _kitPreviewId = selectedKit.Id;
                    _kitPreviewFoundInRaid = requestedFoundInRaid;
                    _kitPreviewHash = result.Hash;
                    preview.text = "Preview: " + selectedKit.Name + "\n" + Runtime.ReadablePreview(result.Summary);
                    replace.SetEnabled(!string.IsNullOrEmpty(_kitPreviewHash));
                    ShowFeedback(CommandStatus.Completed, "Preview ready: " + selectedKit.Name + ". Review the equipment listed before confirming.");
                    Runtime.Report("Kit preview ready: " + selectedKit.Name);
                    _content.ScrollTo(preview);
                }
                catch (Exception ex) { _kitPreviewHash = null; preview.text = ex.Message; replace.SetEnabled(false); ShowFeedback(CommandStatus.Rejected, ex.Message); Runtime.Report("Kit preview failed: " + ex.Message, CommandStatus.Rejected); Plugin.Log.LogError(ex); }
                finally { _busy = false; if (_visible) RefreshLive(); }
            });
            BindAvailability(previewButton, () => ActionUnavailable(CommandKind.PreviewKit, new CommandArguments { Items = SelectedItems(), ClearEquipment = clearEquipment }));
            ExplainAvailability(selected, "Kit preview", () => ActionUnavailable(CommandKind.PreviewKit, new CommandArguments { Items = SelectedItems(), ClearEquipment = clearEquipment }));
            replace = PlainButton(selected, clearEquipment ? "Remove and delete worn equipment" : "Replace and delete displaced equipment", async () =>
            {
                if (string.IsNullOrEmpty(_kitPreviewHash) || TargetId != _kitPreviewTarget || _kit.Id != _kitPreviewId || fir.value != _kitPreviewFoundInRaid) { ShowFeedback(CommandStatus.Rejected, "Preview this kit, target, and found-in-raid status again before replacing equipment."); return; }
                var hash = _kitPreviewHash;
                _kitPreviewHash = null;
                replace.SetEnabled(false);
                await Execute(CommandKind.EquipKit, new CommandArguments { Items = SelectedItems(), ConfirmReplacement = true, ExpectedInventoryHash = hash, FoundInRaid = fir.value, ClearEquipment = clearEquipment });
            }, "danger");
            replace.SetEnabled(false);
            BindAvailability(replace, () =>
            {
                var reason = ActionUnavailable(CommandKind.EquipKit, new CommandArguments { Items = SelectedItems(), ExpectedInventoryHash = _kitPreviewHash ?? "", ConfirmReplacement = true, ClearEquipment = clearEquipment });
                return reason.Length > 0 ? reason : TargetId != _kitPreviewTarget || _kit?.Id != _kitPreviewId || fir.value != _kitPreviewFoundInRaid ? "The selection changed. Preview this replacement again." : "";
            });
        }
        var import = Card("Import a kit", "Enter the absolute path to a DebugPlus kit JSON file.");
        var path = Field(import, "File path", "");
        path.AddToClassList("wide-field");
        var importButton = PlainButton(import, "Import JSON", () =>
        {
            try { _kit = Runtime.ImportKit(path.value.Trim().Trim('"')); ShowPage("Kits"); }
            catch (Exception ex) { ShowFeedback(CommandStatus.Rejected, ex.Message); }
        });
        BindAvailability(importButton, () => string.IsNullOrWhiteSpace(path.value) ? "Enter the path to a kit file first." : "");
    }

    private void Teleport()
    {
        var coords = Card("Move around the map", "Move to where you're looking, return to your previous position, or use saved places below.");
        GateSection(coords, () => ActionUnavailable(CommandKind.Teleport));
        var location = Runtime.PlayerLocation(TargetId);
        var manual = new Foldout { text = "Manual coordinates", value = false }; coords.Add(manual);
        var coordinates = FormRow(manual); coordinates.AddToClassList("coordinate-row");
        var x = Number(coordinates, "X", location.Position.x); var y = Number(coordinates, "Y", location.Position.y); var z = Number(coordinates, "Z", location.Position.z); var yaw = Number(coordinates, "Yaw (degrees)", location.Yaw);
        yaw.AddToClassList("coordinate-yaw");
        var exact = Check(manual, "Exact placement (skip clearance check)", false);
        Func<CommandArguments> args = () => new CommandArguments { X = x.value, Y = y.value, Z = z.value, Yaw = yaw.value, Exact = exact.value, MapId = Runtime.MapId };
        Action(manual, "Go to these coordinates", CommandKind.Teleport, args, true, true, "primary");
        Action(coords, "Teleport to aimed surface", CommandKind.Teleport, () => new CommandArguments { Action = "surface", MapId = Runtime.MapId });
        Action(coords, "Return to previous position", CommandKind.TeleportBack);
        var destinations = Card("Players & spawn points", "Choose the player being moved in the header, then select a destination below.");
        var players = Runtime.Targets.Where(p => !p.IsBot).ToList();
        var playerNames = TargetLabels(players);
        if (playerNames.Count == 0) playerNames.Add("No active players");
        var destination = Choice(destinations, "Destination player", playerNames, playerNames[0]);
        Action(destinations, "Teleport target to player", CommandKind.Teleport, () => new CommandArguments { Action = "to-player", ObjectId = players.Count == 0 ? "" : players[Math.Max(0, playerNames.IndexOf(destination.value))].Id, MapId = Runtime.MapId });
        Action(destinations, "Bring target to you", CommandKind.Teleport, () => new CommandArguments { Action = "bring-player", ObjectId = Runtime.LocalProfileId, MapId = Runtime.MapId });
        var points = Runtime.SpawnPoints.ToList();
        var pointNames = points.Select(p => p.Name + " · " + p.Position).ToList();
        if (pointNames.Count == 0) pointNames.Add("No spawn points available");
        var spawnpoint = Choice(destinations, "Map spawn point", pointNames, pointNames[0]);
        spawnpoint.AddToClassList("wide-field");
        Action(destinations, "Teleport to spawn point", CommandKind.Teleport, () => new CommandArguments { Action = "spawnpoint", ObjectId = points.Count == 0 ? "" : points[Math.Max(0, pointNames.IndexOf(spawnpoint.value))].Id, MapId = Runtime.MapId });
        var bookmarks = Card("Saved places", "Save a location on this map, then return with one click.");
        var name = Field(bookmarks, "Bookmark name", "Test position");
        var saveCurrent = PlainButton(bookmarks, "Save current position", () =>
        {
            if (!Runtime.InRaid) { ShowFeedback(CommandStatus.Rejected, "Enter a raid before saving a place."); return; }
            var here = Runtime.PlayerLocation(TargetId);
            Runtime.SaveBookmark(new TeleportBookmark { Name = name.value.Trim(), MapId = Runtime.MapId, X = here.Position.x, Y = here.Position.y, Z = here.Position.z, Yaw = here.Yaw });
            ShowPage("Teleport");
        }, "primary");
        var saveCoordinates = PlainButton(bookmarks, "Save entered coordinates", () =>
        {
            if (!Runtime.InRaid) { ShowFeedback(CommandStatus.Rejected, "Enter a raid before saving a map bookmark."); return; }
            Runtime.SaveBookmark(new TeleportBookmark { Name = name.value.Trim(), MapId = Runtime.MapId, X = x.value, Y = y.value, Z = z.value, Yaw = yaw.value });
            ShowPage("Teleport");
        });
        BindAvailability(saveCurrent, () => string.IsNullOrWhiteSpace(name.value) ? "Name this saved place first." : ActionUnavailable(CommandKind.Teleport));
        BindAvailability(saveCoordinates, () => string.IsNullOrWhiteSpace(name.value) ? "Name this saved place first." : !new[] { x.value, y.value, z.value, yaw.value }.All(Commands.Finite) ? "Enter valid coordinates." : ActionUnavailable(CommandKind.Teleport));
        foreach (var bookmark in BookmarkStore.ForMap(Runtime.Bookmarks, Runtime.MapId))
        {
            var saved = bookmark;
            var row = Row(bookmarks);
            Action(row, saved.Name, CommandKind.Teleport, () => new CommandArguments { X = saved.X, Y = saved.Y, Z = saved.Z, Yaw = saved.Yaw, MapId = saved.MapId }, true, false);
            PlainButton(row, "Load", () => { x.value = saved.X; y.value = saved.Y; z.value = saved.Z; yaw.value = saved.Yaw; name.value = saved.Name; });
            PlainButton(row, "Delete", () => { Runtime.DeleteBookmark(saved); ShowPage("Teleport"); });
        }
    }

    private void Bots()
    {
        var spawn = Card("Spawn bots", "Choose a role, difficulty, and area for your encounter.");
        GateSection(spawn, () => ActionUnavailable(CommandKind.SpawnBots));
        var roles = Runtime.BotRoles.ToList();
        var roleNames = roles.Select(r => r.Name).ToList();
        var botFields = FormRow(spawn);
        var role = Choice(botFields, "Bot type", roleNames, roles.FirstOrDefault(r => r.Id == "assault")?.Name ?? roleNames[0]);
        var difficulty = Choice(botFields, "Difficulty", new[] { "Easy", "Normal", "Hard", "Impossible" }, "Normal");
        var spawnFields = FormRow(spawn);
        var count = Integer(spawnFields, "How many", 1);
        count.RegisterValueChangedCallback(e => count.SetValueWithoutNotify(Mathf.Clamp(e.newValue, 1, Protocol.MaxCount)));
        var zones = Runtime.BotZones.ToList();
        var zoneNames = new[] { "Near you" }.Concat(zones.Select(p => p.Name)).ToList();
        var zone = Choice(spawnFields, "Spawn area", zoneNames, zoneNames[0]);
        Action(spawn, "Spawn bots", CommandKind.SpawnBots, () => new CommandArguments { Role = roles[Math.Max(0, roleNames.IndexOf(role.value))].Id, Difficulty = difficulty.value.ToLowerInvariant(), Count = count.value, Action = "zone", ObjectId = zone.value == zoneNames[0] ? "" : zones[Math.Max(0, zoneNames.IndexOf(zone.value) - 1)].Id, MapId = Runtime.MapId }, true, true, "primary");
        var control = Card("Active bots", "Select a bot below. Choose All bots only when you want to affect the whole population.");
        GateSection(control, () => ActionUnavailable(CommandKind.PauseBots, new CommandArguments { ObjectId = "all" }));
        string selectedBot = Runtime.Targets.FirstOrDefault(t => t.IsBot && t.Id == TargetId)?.Id;
        var selection = Text(control, "Choose a bot", "selection-note");
        PlainButton(control, "Select all bots", () => { selectedBot = "all"; RefreshLive(); });
        var filter = Field(control, "Find a bot", "");
        var botList = new ScrollView(); botList.AddToClassList("picker-list"); control.Add(botList);
        var botButtons = new Dictionary<string, Button>();
        var actions = new List<Button>();
        void RefreshBots()
        {
            var bots = Runtime.Targets.Where(t => t.IsBot).ToList();
            if (bots.Count != botButtons.Count || bots.Any(b => !botButtons.ContainsKey(b.Id)))
            {
                botList.Clear(); botButtons.Clear();
                foreach (var bot in bots)
                {
                    var chosen = bot;
                    botButtons[bot.Id] = PlainButton(botList, bot.Name, () => { selectedBot = chosen.Id; RefreshLive(); }, "picker-button");
                }
            }
            var labels = TargetLabels(bots);
            for (var i = 0; i < bots.Count; i++)
            {
                var button = botButtons[bots[i].Id]; button.text = labels[i];
                button.EnableInClassList("selected", bots[i].Id == selectedBot);
                button.style.display = bots[i].Name.IndexOf(filter.value, StringComparison.OrdinalIgnoreCase) >= 0 ? DisplayStyle.Flex : DisplayStyle.None;
            }
            var target = bots.FirstOrDefault(b => b.Id == selectedBot);
            selection.text = selectedBot == "all" ? "Selected: all " + bots.Count + " bots" : target != null ? "Selected: " + target.Name : bots.Count == 0 ? "No live bots in this raid" : "Choose a bot or select all bots";
            if (target != null) _details.text = Runtime.InspectPlayer(target.Id, false);
        }
        filter.RegisterValueChangedCallback(_ => RefreshBots());
        _refreshers.Add(RefreshBots);
        foreach (var kind in new[] { CommandKind.PauseBots, CommandKind.ResumeBots, CommandKind.HealBots, CommandKind.KillBots, CommandKind.DespawnBots })
        {
            var command = kind;
            actions.Add(Action(control, Humanize(kind.ToString()), command, () => new CommandArguments { ObjectId = selectedBot ?? "" }, true, true, kind is CommandKind.KillBots or CommandKind.DespawnBots ? "danger" : ""));
        }
    }
    private void World()
    {
        var doors = Card("Doors", "Use the door you're looking at, or choose one nearby. Distance is measured from you.");
        var selectedDoor = Runtime.AimedDoor();
        var chosen = Text(doors, selectedDoor?.Name ?? "Choose a door below, or aim at one before opening the workbench.", "selection-note");
        var choose = Row(doors);
        PlainButton(choose, "Use aimed door", () => { selectedDoor = Runtime.AimedDoor(); chosen.text = selectedDoor?.Name ?? "No door in view. Close the workbench, aim at a door, then open it again."; RefreshLive(); });
        var filter = Field(doors, "Find nearby door", "");
        var nearby = new ScrollView(); nearby.AddToClassList("picker-list"); doors.Add(nearby);
        var candidates = Runtime.NearbyDoors();
        void RefreshDoors()
        {
            nearby.Clear();
            foreach (var candidate in candidates.Where(d => d.Name.IndexOf(filter.value, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                var door = candidate;
                var button = PlainButton(nearby, door.Name, () => { selectedDoor = door; chosen.text = door.Name; RefreshDoors(); RefreshLive(); }, "picker-button");
                button.EnableInClassList("selected", door.Id == selectedDoor?.Id);
                button.tooltip = "Position " + door.Position;
            }
            if (candidates.Count == 0) Text(nearby, "No nearby doors available.", "empty");
        }
        PlainButton(choose, "Refresh nearby doors", () => { candidates = Runtime.NearbyDoors(); RefreshDoors(); });
        filter.RegisterValueChangedCallback(_ => RefreshDoors()); RefreshDoors();
        var row = Row(doors);
        var doorActions = new List<Button>();
        foreach (var operation in new[] { "Open", "Close", "Lock", "Unlock", "Breach" })
        {
            var selected = operation;
            doorActions.Add(Action(row, operation, CommandKind.Door, () => new CommandArguments { ObjectId = selectedDoor?.Id ?? "", Action = selected }, true, false));
        }
        var advanced = new Foldout { text = "Technical details", value = false }; advanced.AddToClassList("technical"); doors.Add(advanced);
        PlainButton(advanced, "Copy selected door ID", () => { if (selectedDoor != null) GUIUtility.systemCopyBuffer = selectedDoor.Id; });
        var time = Card("Time of day", "Set the current daylight time and optionally hold it for a repeatable scene.");
        GateSection(time, () => ActionUnavailable(CommandKind.SetTime));
        var hours = new Slider("Hour", 0, 23.99f) { value = 12, showInputField = true }; time.Add(hours);
        var frozen = Check(time, "Freeze daylight", false);
        Action(time, "Apply time", CommandKind.SetTime, () => new CommandArguments { Value = hours.value, Enabled = frozen.value });
        var weather = Card("Weather", "Use normalized controls to compose a scene, or start from a preset.");
        GateSection(weather, () => ActionUnavailable(CommandKind.SetWeather));
        var weatherFields = FormRow(weather);
        var clouds = Slider(weatherFields, "Clouds", 0); var rain = Slider(weatherFields, "Rain", 0); var fog = Slider(weatherFields, "Fog", 0); var wind = Slider(weatherFields, "Wind", .1f); var thunder = Slider(weatherFields, "Thunder", 0);
        var presets = Row(weather);
        PlainButton(presets, "Clear", () => { clouds.value = rain.value = fog.value = thunder.value = 0; wind.value = .1f; });
        PlainButton(presets, "Overcast", () => { clouds.value = .8f; rain.value = 0; fog.value = .15f; wind.value = .3f; thunder.value = 0; });
        PlainButton(presets, "Storm", () => { clouds.value = 1; rain.value = .8f; fog.value = .2f; wind.value = .8f; thunder.value = 1; });
        Action(weather, "Apply weather", CommandKind.SetWeather, () => new CommandArguments { Weather = new WeatherValues { Clouds = clouds.value, Rain = rain.value, Fog = fog.value, Wind = wind.value, Thunder = thunder.value } });
        Action(_content, "Restore normal time and weather", CommandKind.RestoreEnvironment);
    }

    private void Diagnostics()
    {
        var inspect = Card("Scene & player inspection", "Copy the current inspector contents or export a complete diagnostic report.");
        PlainButton(inspect, "Copy scene inspection", () => { GUIUtility.systemCopyBuffer = Runtime.InspectWorld(); ShowFeedback(CommandStatus.Completed, "Scene inspection copied."); });
        PlainButton(inspect, "Copy selected player inspection", () => { GUIUtility.systemCopyBuffer = Runtime.InspectPlayer(TargetId); ShowFeedback(CommandStatus.Completed, "Player inspection copied."); });
        PlainButton(inspect, "Export diagnostic report", () => SafeReport(Runtime.ExportDiagnostics), "primary");
        var logs = Card("Command history", "Search outcomes and messages from this session.");
        var filter = Field(logs, "Filter", "");
        var entries = new ScrollView(); entries.style.height = 260; logs.Add(entries);
        void Refresh()
        {
            entries.Clear();
            foreach (var entry in Runtime.Activity.Reverse().Where(e => (e.Message ?? "").IndexOf(filter.value, StringComparison.OrdinalIgnoreCase) >= 0 || e.Status.ToString().IndexOf(filter.value, StringComparison.OrdinalIgnoreCase) >= 0).Take(150))
            {
                var label = Text(entries, $"{entry.Time:HH:mm:ss}  {entry.Status}  {entry.Message}", "log-entry");
                label.AddToClassList(entry.Status.ToString().ToLowerInvariant());
            }
        }
        filter.RegisterValueChangedCallback(_ => Refresh());
        _refreshers.Add(Refresh);
        var legacy = Card("Existing debug tools", "Weapon inspection remains available through the existing debug controls and the player inspector.");
        Action(legacy, "Reload backend data", CommandKind.ReloadBackend, null, false);
    }

    private void Settings()
    {
        var appearance = Card("Appearance & input", "Window position, dimensions, scale, and tool preferences are saved automatically.");
        var scale = new Slider("UI scale", .7f, 1.6f) { value = Runtime.Preferences.UiScale, showInputField = true }; appearance.Add(scale);
        scale.RegisterValueChangedCallback(e => { Runtime.Preferences.UiScale = e.newValue; ApplyGeometry(); Runtime.SavePreferences(); });
        var shortcut = Choice(appearance, "Toggle key", new[] { "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12", "Insert", "Home" }, Runtime.Preferences.ToggleKey);
        shortcut.RegisterValueChangedCallback(e => { Runtime.Preferences.ToggleKey = e.newValue; Runtime.SavePreferences(); });
        PlainButton(appearance, "Reset window position & size", () =>
        {
            var p = Runtime.Preferences;
            p.UiX = p.UiY = p.UiWidth = p.UiHeight = 0;
            p.UiScale = 1;
            ApplyGeometry();
            Runtime.SavePreferences();
            ShowPage("Settings");
        });
        var access = Card("Server access", "Your server administrator controls who can use these tools.");
        Text(access, Runtime.Status, "notice");
        var technical = new Foldout { text = "Technical details for server setup", value = false }; technical.AddToClassList("technical"); access.Add(technical);
        Text(technical, "Your profile ID: " + Runtime.LocalProfileId, "wrap");
        PlainButton(technical, "Copy profile ID", () => { GUIUtility.systemCopyBuffer = Runtime.LocalProfileId; ShowFeedback(CommandStatus.Completed, "Profile ID copied."); });
        PlainButton(access, "Refresh permissions", () => { Runtime.RefreshAccess(); ShowFeedback(CommandStatus.Pending, "Refreshing server permissions…"); });
        var about = Card("DebugPlus workbench", "An original UI Toolkit interface for deliberate, visible debugging actions.");
        Text(about, "Version " + Protocol.ModVersion + "  ·  Protocol " + Protocol.Version, "muted");
        Text(about, "Overrides reset between raids. Kits, bookmarks, favorites, and this window persist. Opening the workbench does not pause the raid or enable protection.", "wrap");
    }

    private Button Action(VisualElement parent, string title, CommandKind kind, Func<CommandArguments> args = null, bool raid = true, bool favorite = true, string style = "")
    {
        var row = new VisualElement(); row.AddToClassList("action-row"); parent.Add(row);
        var button = PlainButton(row, title, async () =>
        {
            try { await Execute(kind, args?.Invoke() ?? new CommandArguments()); }
            catch (Exception ex) { ShowFeedback(CommandStatus.Rejected, ex.Message); }
        }, style);
        button.AddToClassList("action-main");
        BindAvailability(button, () => ActionUnavailable(kind, args?.Invoke() ?? new CommandArguments()));
        if (favorite)
        {
            var star = PlainButton(row, "☆", () => { });
            star.AddToClassList("favorite-button");
            void Refresh()
            {
                var saved = Runtime.Preferences.FavoriteCommands.Contains(kind.ToString());
                star.text = saved ? "★" : "☆";
                star.EnableInClassList("saved", saved);
                star.tooltip = saved ? "Remove favorite" : "Favorite this tool";
            }
            star.clicked += () =>
            {
                if (!Runtime.Preferences.FavoriteCommands.Remove(kind.ToString())) Runtime.Preferences.FavoriteCommands.Add(kind.ToString());
                Runtime.SavePreferences(); Refresh();
            };
            Refresh();
        }
        return button;
    }

    private async Task Execute(CommandKind kind, CommandArguments args)
    {
        var unavailable = ActionUnavailable(kind, args);
        if (unavailable.Length > 0) { ShowFeedback(_busy ? CommandStatus.Pending : CommandStatus.Rejected, unavailable); return; }
        _busy = true;
        var target = TargetId;
        try
        {
            ShowFeedback(CommandStatus.Pending, Humanize(kind.ToString()) + " is pending…");
            RefreshLive();
            var result = await Runtime.Submit(kind, args, target);
            ShowFeedback(result.Status, result.Message);
            _recent.Add(Tuple.Create(DateTime.Now, Humanize(kind.ToString()), CommandPages.TryGetValue(kind, out var page) ? page : _page));
            if (_recent.Count > 30) _recent.RemoveAt(0);
        }
        catch (Exception ex) { Runtime.Report(ex.Message, CommandStatus.Uncertain); ShowFeedback(CommandStatus.Uncertain, ex.Message); }
        finally { _busy = false; if (_visible) RefreshLive(); }
    }

    private void ShowFeedback(CommandStatus status, string message)
    {
        if (_feedback == null) return;
        _feedback.text = message;
        _window.Q<Label>("feedback-status").text = status.ToString().ToUpperInvariant();
        var panel = _window.Q("feedback");
        panel.EnableInClassList("rejected", status == CommandStatus.Rejected);
        panel.EnableInClassList("uncertain", status == CommandStatus.Uncertain);
    }

    private string ActionUnavailable(CommandKind kind, CommandArguments args = null)
    {
        if (args != null && kind is CommandKind.SpawnKit or CommandKind.PreviewKit or CommandKind.EquipKit)
            return WorkbenchAvailability.KitAction(kind, args, _busy);
        return _busy ? "Wait for the current command to finish." : Runtime.CommandUnavailable(kind, args, TargetId);
    }

    private void BindAvailability(VisualElement element, Func<string> reason)
    {
        if (_availability.TryGetValue(element, out var previous))
            _availability[element] = (() => { var first = previous.Reason(); return first.Length > 0 ? first : reason(); }, previous.Hint);
        else _availability[element] = (reason, element.tooltip ?? "");
    }
    private void GateSection(VisualElement section, Func<string> reason)
    {
        var explanation = Text(section, "", "unavailable-reason");
        BindAvailability(section, reason);
        _refreshers.Add(() => { explanation.text = reason(); explanation.style.display = explanation.text.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None; });
    }
    private void ExplainAvailability(VisualElement parent, string action, Func<string> reason)
    {
        var explanation = Text(parent, "", "unavailable-reason");
        string previous = null;
        _refreshers.Add(() =>
        {
            var current = reason();
            explanation.text = current;
            explanation.style.display = current.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            if (current == previous) return;
            previous = current;
            if (current.Length > 0) Plugin.Log.LogInfo(action + " unavailable: " + current);
        });
    }
    private static void SetAvailability(VisualElement element, string reason, string hint = "")
    {
        element.SetEnabled(string.IsNullOrEmpty(reason));
        element.tooltip = string.IsNullOrEmpty(reason) ? hint : reason;
        if (element.parent != null && (element.parent.ClassListContains("action-row") || element.parent.ClassListContains("nav-slot"))) element.parent.tooltip = element.tooltip;
    }
    private void RefreshAvailability()
    {
        if (Runtime == null || _window == null) return;
        foreach (var page in Pages)
            SetAvailability(_window.Q<Button>("nav-" + page), Runtime.PageUnavailable(page, TargetId));
        foreach (var binding in _availability)
            SetAvailability(binding.Key, binding.Value.Reason(), binding.Value.Hint);
        var note = _window.Q<Label>("availability-note");
        if (note != null)
        {
            note.text = !Runtime.IsAuthorized ? "View-only access. Ask your server administrator to enable commands for your profile."
                : !Runtime.InRaid ? "Player, Health, Teleport, Bots and World become available in an active raid."
                : Runtime.IsCoop && !Runtime.CoopReady ? "Multiplayer controls unavailable: " + Runtime.CoopStatus
                : !Runtime.Availability(TargetId).TargetAlive ? "Select a live player to use Player, Health and Teleport." : "";
            note.style.display = note.text.Length == 0 ? DisplayStyle.None : DisplayStyle.Flex;
        }
        _target.SetEnabled(!_busy && _targetIds.Count > 1);
        _target.tooltip = _busy ? "Wait for the current command to finish." : _targetIds.Count > 1 ? "Choose the player these tools affect." : "No other player is available to select.";
    }
    private Button PageLink(VisualElement parent, string title, string page, CommandKind? command = null)
    {
        var button = PlainButton(parent, title, () => { _search.SetValueWithoutNotify(""); ShowPage(page); });
        BindAvailability(button, () =>
        {
            var reason = Runtime.PageUnavailable(page, TargetId);
            return reason.Length > 0 || !command.HasValue ? reason : Runtime.CommandUnavailable(command.Value, null, TargetId);
        });
        return button;
    }

    private void SafeReport(Func<string> operation)
    {
        try { ShowFeedback(CommandStatus.Completed, operation()); }
        catch (Exception ex) { ShowFeedback(CommandStatus.Rejected, ex.Message); }
    }
    private VisualElement Card(string title, string description)
    {
        var card = new VisualElement(); card.AddToClassList("card"); _content.Add(card);
        Text(card, title, "section-title");
        if (!string.IsNullOrEmpty(description)) Text(card, description, "card-description");
        return card;
    }
    private static Label Text(VisualElement parent, string value, string style = "") { var label = new Label(value); if (style != "") label.AddToClassList(style); parent.Add(label); return label; }
    private static void Notice(VisualElement parent, string value) => Text(parent, value, "notice");
    private static VisualElement Row(VisualElement parent) { var row = new VisualElement(); row.AddToClassList("row"); parent.Add(row); return row; }
    private static VisualElement FormRow(VisualElement parent) { var row = Row(parent); row.AddToClassList("form-row"); return row; }
    private Button PlainButton(VisualElement parent, string title, Action action, string style = "")
    {
        Button button = null;
        button = new Button(() =>
        {
            try
            {
                if (_availability.TryGetValue(button, out var binding)) { var reason = binding.Reason(); if (reason.Length > 0) { ShowFeedback(CommandStatus.Rejected, reason); return; } }
                action(); RefreshAvailability();
            }
            catch (Exception ex) { Runtime?.Report(ex.Message, CommandStatus.Rejected); ShowFeedback(CommandStatus.Rejected, ex.Message); }
        }) { text = title };
        if (style != "") button.AddToClassList(style);
        parent.Add(button);
        return button;
    }
    private static TextField Field(VisualElement parent, string title, string value) { var field = new TextField(title) { value = value }; parent.Add(field); return field; }
    private static FloatField Number(VisualElement parent, string title, float value) { var field = new FloatField(title) { value = value }; parent.Add(field); return field; }
    private static IntegerField Integer(VisualElement parent, string title, int value) { var field = new IntegerField(title) { value = value }; parent.Add(field); return field; }
    private static Toggle Check(VisualElement parent, string title, bool value) { var field = new Toggle(title) { value = value }; parent.Add(field); return field; }
    private static Slider Slider(VisualElement parent, string title, float value) { var field = new Slider(title, 0, 1) { value = value, showInputField = true }; parent.Add(field); return field; }
    private static DropdownField Choice(VisualElement parent, string title, IEnumerable<string> values, string selected)
    {
        var choices = values.ToList(); if (!choices.Contains(selected)) choices.Add(selected);
        var field = new DropdownField(title, choices, Math.Max(0, choices.IndexOf(selected))); parent.Add(field); return field;
    }
    private async void SetIcon(Image image, string templateId)
    {
        try { var sprite = await Runtime.LoadItemIcon(templateId); if (image.panel != null) image.sprite = sprite; }
        catch { /* Keep the item name visible when a thumbnail is unavailable. */ }
    }
    private List<string> TargetLabels(IEnumerable<TargetInfo> targets)
    {
        var seen = new Dictionary<string, int>();
        return targets.Select(t =>
        {
            var label = t.Name + (t.Id == Runtime.LocalProfileId ? " · You" : t.IsBot ? " · Bot" : "");
            seen.TryGetValue(label, out var count); seen[label] = ++count;
            return count > 1 ? label + " (" + count + ")" : label;
        }).ToList();
    }
    private static string Humanize(string name) => System.Text.RegularExpressions.Regex.Replace(name, "([a-z])([A-Z])", "$1 $2");
}
