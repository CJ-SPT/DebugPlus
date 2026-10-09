using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using DebugPlus.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace DebugPlus.AssetBuild
{
    /// <summary>Builds only the original DebugPlus assets staged in this isolated SDK folder.</summary>
    public static class WorkbenchAssetBuilder
    {
        private const string Root = "Assets/Mods/DebugPlus.Workbench.Assets/";
        private const string RaidUnavailableReason = "Enter an active raid to use this tool.";
        private const string MenuAvailabilityNote = "Player, Health, Teleport, Bots and World become available in an active raid.";
        private static readonly HashSet<string> RaidPages = new HashSet<string> { "Player", "Health", "Teleport", "Bots", "World" };
        public static void Build()
        {
            var arguments = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(arguments, "-debugPlusOutput");
            if (index < 0 || index + 1 >= arguments.Length) throw new ArgumentException("Missing -debugPlusOutput argument.");
            var output = Path.GetFullPath(arguments[index + 1]);
            Directory.CreateDirectory(output);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            var tree = Required<VisualTreeAsset>("Workbench.uxml");
            var style = Required<StyleSheet>("Workbench.uss");
            var theme = Required<ThemeStyleSheet>("Workbench.tss");
            var clone = tree.CloneTree();
            clone.styleSheets.Add(style);
            var nodes = new[] { "workbench", "titlebar", "close", "session-label", "command-search", "target", "navigation", "availability-note", "page-title", "page-description", "content", "context-details", "authority-label", "feedback-message", "activity", "resize-handle" };
            foreach (var name in nodes) if (clone.Q(name) == null) throw new InvalidDataException("Missing workbench node: " + name);

            var font = AssetDatabase.LoadAssetAtPath<FontAsset>(Root + "WorkbenchFont.asset");
            if (font == null)
            {
                var source = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                if (source == null) source = Resources.GetBuiltinResource<Font>("Arial.ttf");
                font = FontAsset.CreateFontAsset(source);
                font.name = "DebugPlus Workbench Font";
                font.atlasPopulationMode = AtlasPopulationMode.Dynamic;
                font.isMultiAtlasTexturesEnabled = true;
                AssetDatabase.CreateAsset(font, Root + "WorkbenchFont.asset");
                if (font.material != null) AssetDatabase.AddObjectToAsset(font.material, font);
                foreach (var texture in font.atlasTextures) if (texture != null) AssetDatabase.AddObjectToAsset(texture, font);
                EditorUtility.SetDirty(font);
            }
            var text = AssetDatabase.LoadAssetAtPath<PanelTextSettings>(Root + "WorkbenchText.asset");
            if (text == null) { text = ScriptableObject.CreateInstance<PanelTextSettings>(); AssetDatabase.CreateAsset(text, Root + "WorkbenchText.asset"); }
            text.defaultFontAsset = font;
            EditorUtility.SetDirty(text);
            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(Root + "WorkbenchPanel.asset");
            if (panel == null) { panel = ScriptableObject.CreateInstance<PanelSettings>(); AssetDatabase.CreateAsset(panel, Root + "WorkbenchPanel.asset"); }
            panel.themeStyleSheet = theme;
            panel.textSettings = text;
            panel.scaleMode = PanelScaleMode.ConstantPixelSize;
            panel.scale = 1;
            panel.sortingOrder = 32000;
            panel.referenceResolution = new Vector2Int(1920, 1080);
            var serialized = new SerializedObject(panel);
            AssignShader(serialized, "m_AtlasBlitShader", "Hidden/Internal-UIRAtlasBlitCopy");
            AssignShader(serialized, "m_RuntimeShader", "Hidden/Internal-UIRDefault");
            AssignShader(serialized, "m_RuntimeWorldShader", "Hidden/Internal-UIRDefaultWorld");
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(panel);
            AssetDatabase.SaveAssets();
            var bundle = new AssetBundleBuild
            {
                assetBundleName = "debugplus.workbench",
                assetNames = new[] { Root + "Workbench.uxml", Root + "Workbench.uss", Root + "Workbench.tss", Root + "WorkbenchPanel.asset", Root + "WorkbenchText.asset", Root + "WorkbenchFont.asset" }
            };
            var manifest = BuildPipeline.BuildAssetBundles(output, new[] { bundle }, BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode, BuildTarget.StandaloneWindows64);
            if (manifest == null || !File.Exists(Path.Combine(output, bundle.assetBundleName))) throw new InvalidOperationException("DebugPlus asset bundle build failed.");
            var dependencies = AssetDatabase.GetDependencies(bundle.assetNames, true);
            var foreignModAssets = dependencies.Where(p => p.StartsWith("Assets/Mods/", StringComparison.Ordinal) && !p.StartsWith(Root, StringComparison.Ordinal)).ToArray();
            if (foreignModAssets.Length > 0) throw new InvalidDataException("Unexpected external mod assets: " + string.Join(", ", foreignModAssets));
            File.WriteAllText(Path.Combine(output, "asset-validation.txt"), "Unity " + Application.unityVersion + "\nOriginal DebugPlus UXML and USS imported successfully.\nRequired named nodes: " + nodes.Length + "\nForeign mod dependencies: 0\nFont: " + font.name + "\nBundle: " + bundle.assetBundleName + "\nDependencies:\n" + string.Join("\n", dependencies));
            Debug.Log("DebugPlus workbench asset validation and bundle build completed.");
            if (arguments.Contains("-debugPlusPreview")) RenderPreviews(output, Required<VisualTreeAsset>("Workbench.uxml"), Required<StyleSheet>("Workbench.uss"), Required<PanelSettings>("WorkbenchPanel.asset"));
        }

        private static T Required<T>(string name) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(Root + name);
            if (asset == null) throw new InvalidDataException("Could not import " + Root + name);
            return asset;
        }

        private static void RenderPreviews(string output, VisualTreeAsset tree, StyleSheet style, PanelSettings source)
        {
            var utility = typeof(PanelSettings).Assembly.GetType("UnityEngine.UIElements.UIElementsRuntimeUtility", true);
            var update = utility.GetMethod("UpdateRuntimePanels", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var repaint = utility.GetMethod("RepaintOffscreenPanels", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var report = new StringBuilder("Native UI Toolkit layout fixtures. These are not live-game acceptance.\nIcons are original procedural silhouettes, not game assets.\n");
            foreach (var resolution in new[] { new Vector2Int(1920, 1080), new Vector2Int(2560, 1440), new Vector2Int(3440, 1440) })
            {
                RenderVariant(output, tree, style, source, update, repaint, resolution, "Overview", 1340, 840, 1, "", report);
                RenderVariant(output, tree, style, source, update, repaint, resolution, "Items", 1340, 840, 1, "", report);
                RenderVariant(output, tree, style, source, update, repaint, resolution, "Logging", 1340, 940, 1, "", report);
            }
            RenderVariant(output, tree, style, source, update, repaint, new Vector2Int(1920, 1080), "Items", 960, 800, 1.25f, "-narrow-scaled", report);
            RenderVariant(output, tree, style, source, update, repaint, new Vector2Int(2560, 1440), "Items", 1800, 1200, 1, "-expanded", report);
            RenderVariant(output, tree, style, source, update, repaint, new Vector2Int(1920, 1080), "Logging", 960, 800, 1.25f, "-narrow-scaled", report);
            RenderVariant(output, tree, style, source, update, repaint, new Vector2Int(1920, 1080), "Logging", 950, 560, 1, "-minimum", report);
            RenderVariant(output, tree, style, source, update, repaint, new Vector2Int(1920, 1080), "Items", 1340, 840, 1, "-dropdown", report);
            RenderVariant(output, tree, style, source, update, repaint, new Vector2Int(1920, 1080), "Overview", 1340, 840, 1, "", report, true);
            foreach (var page in new[] { "Player", "Health", "Kits", "Teleport", "Bots", "World", "Diagnostics", "Settings" })
            {
                RenderVariant(output, tree, style, source, update, repaint, new Vector2Int(1920, 1080), page, 1340, 840, 1, "", report);
                RenderVariant(output, tree, style, source, update, repaint, new Vector2Int(1920, 1080), page, 960, 800, 1.25f, "-narrow-scaled", report);
                RenderVariant(output, tree, style, source, update, repaint, new Vector2Int(2560, 1440), page, 1800, 1200, 1, "-expanded", report);
            }
            File.WriteAllText(Path.Combine(output, "visual-layout-validation.txt"), report.ToString());
            Debug.Log("DebugPlus native UI Toolkit offscreen previews exported (fixture data, not live-game acceptance).");
        }

        private static void RenderVariant(string output, VisualTreeAsset tree, StyleSheet style, PanelSettings source, MethodInfo update, MethodInfo repaint,
            Vector2Int resolution, string page, float width, float height, float scale, string suffix, StringBuilder report, bool mainMenu = false)
        {
            var panel = UnityEngine.Object.Instantiate(source);
            var render = new RenderTexture(resolution.x, resolution.y, 24, RenderTextureFormat.ARGB32);
            render.Create();
            panel.targetTexture = render;
            panel.clearColor = true;
            panel.colorClearValue = new Color(.045f, .055f, .075f, 1);
            panel.scale = scale;
            var host = new GameObject("DebugPlus offline visual validation");
            var icons = new List<Sprite>();
            try
            {
                var document = host.AddComponent<UIDocument>();
                document.panelSettings = panel;
                tree.CloneTree(document.rootVisualElement);
                document.rootVisualElement.styleSheets.Add(style);
                document.rootVisualElement.panel.visualTree.styleSheets.Add(style);
                var window = document.rootVisualElement.Q("workbench");
                window.style.left = (resolution.x / scale - width) / 2;
                window.style.top = (resolution.y / scale - height) / 2;
                window.style.width = width;
                window.style.height = height;
                if (width < 1100 || page == "Items" || page == "Logging" || page == "Kits") window.Q<Label>("context-title").parent.style.display = DisplayStyle.None;
                var navigation = window.Q("navigation");
                foreach (var name in new[] { "Overview", "Player", "Health", "Items", "Kits", "Teleport", "Bots", "World", "Diagnostics", "Logging", "Settings" })
                {
                    var button = new Button { text = name };
                    button.AddToClassList("nav-button");
                    if (name == page) button.AddToClassList("selected");
                    var unavailable = mainMenu && RaidPages.Contains(name);
                    button.SetEnabled(!unavailable);
                    button.tooltip = unavailable ? RaidUnavailableReason : "";
                    var slot = new VisualElement { tooltip = button.tooltip };
                    slot.AddToClassList("nav-slot");
                    slot.Add(button);
                    navigation.Add(slot);
                }
                var availability = window.Q<Label>("availability-note");
                availability.text = mainMenu ? MenuAvailabilityNote : "";
                availability.style.display = mainMenu ? DisplayStyle.Flex : DisplayStyle.None;
                window.Q<Label>("session-label").text = mainMenu ? "Main menu" : "Solo · Factory";
                window.Q<DropdownField>("target").choices = new List<string> { "Operator (You)" };
                window.Q<DropdownField>("target").value = "Operator (You)";
                window.Q<DropdownField>("target").SetEnabled(false);
                window.Q<DropdownField>("target").tooltip = "No other player is available to select.";
                window.Q<Label>("authority-label").text = "Administrator access confirmed";
                window.Q<Label>("page-title").text = page;
                var kicker = window.Q<Label>("page-kicker");
                if (kicker != null) kicker.text = "YOUR WORKSPACE";
                window.Q<Label>("page-description").text = page == "Logging" ? "Reveal hidden client logs, filter recent messages, and export evidence." : page == "Items" ? "Pick an item, choose a quantity, and add it to your inventory." : "Your session, tools, and recent activity in one place.";
                if (page == "Teleport") window.Q<Label>("page-description").text = "Move through your current map with coordinates and saved locations.";
                if (page == "World") window.Q<Label>("page-description").text = "Inspect doors and shape time and weather for repeatable tests.";
                if (page == "Items") window.Q<Label>("feedback-message").text = "Offline layout fixture · original schematic icons; game icons load in game.";
                window.Q<Label>("context-details").text = page == "Items"
                    ? "Salewa first aid kit\n\nMedical supplies\n\nA favorite item, ready to add to your inventory.\n\nOFFLINE LAYOUT FIXTURE\nOriginal schematic icons; game icons load in-game."
                    : "Operator\n\nFactory\n\nHead        35 / 35\nChest        85 / 85\nStomach   70 / 70\nLeft arm    60 / 60\nRight arm  60 / 60\nLeft leg      65 / 65\nRight leg    65 / 65\n\nNo active health effects\n\nOFFLINE LAYOUT FIXTURE";
                window.Q<Label>("context-title").text = page == "Items" ? "Selected item" : "Selected player";
                if (mainMenu) window.Q<Label>("context-details").text = "Operator\n\nMain menu\nInventory tools ready\n\nRaid tools become available after entering a raid.\n\nOFFLINE LAYOUT FIXTURE";
                var content = window.Q<ScrollView>("content");
                ItemGridView grid = null;
                if (page == "Items") grid = PopulateItemFixture(content, icons);
                else if (page == "Logging") PopulateLoggingFixture(content);
                else if (page == "Overview") PopulateOverviewFixture(content, mainMenu);
                else PopulateFormFixture(content, page);
                WorkbenchScrolling.Configure(window);
                for (var frame = 0; frame < 18; frame++) { update.Invoke(null, null); repaint.Invoke(null, null); }
                var interactionReport = new StringBuilder();
                string layoutFailure = null;
                if (grid != null) ExerciseItemGrid(grid, update, repaint, interactionReport);
                if (page == "Logging") ExerciseLogging(content.Q<LoggingView>(), update, repaint, interactionReport);
                if (content.verticalScroller.highValue > 54) ExerciseScroll(content, update, repaint, interactionReport);
                if (grid != null)
                {
                    var fixture = (ItemFixture)grid.userData;
                    var searchInput = fixture.Search.Q(className: "unity-base-field__input");
                    var categoryInput = fixture.Category.Q(className: "unity-base-field__input");
                    if (Mathf.Abs(searchInput.worldBound.xMin - categoryInput.worldBound.xMin) > 1 || Mathf.Abs(searchInput.worldBound.xMax - categoryInput.worldBound.xMax) > 1)
                        throw new InvalidDataException("Item search/category input edges do not align: search=" + searchInput.worldBound + "; category=" + categoryInput.worldBound + "; fields=" + fixture.Search.worldBound + "; " + fixture.Category.worldBound);
                }
                if (page != "Items" && page != "Logging" && page != "Overview")
                {
                    foreach (var field in content.Query<VisualElement>(className: "unity-base-field").ToList().Where(f => f is FloatField || f is TextField || f is DropdownField || f is Slider))
                    {
                        if (field.GetFirstAncestorOfType<Slider>() != null || field.GetFirstAncestorOfType<Scroller>() != null) continue;
                        var card = field.parent;
                        while (card != null && !card.ClassListContains("card")) card = card.parent;
                        if (card == null) continue;
                        var input = field.Q(className: "unity-base-field__input");
                        if (card == null || field.worldBound.xMin < card.worldBound.xMin || field.worldBound.xMax > card.worldBound.xMax || input == null || input.worldBound.width < 64 || input.worldBound.xMax > field.worldBound.xMax + 1)
                            throw new InvalidDataException("Compact form control is clipped: " + field + " " + field.worldBound + "; input=" + input?.worldBound + "; card=" + card?.worldBound);
                        if (field.worldBound.width > (field.ClassListContains("wide-field") ? 641 : 421) * scale) throw new InvalidDataException("Compact form control stretched across the page: " + field);
                    }
                    interactionReport.AppendLine("  Compact forms: bounded field widths and readable inputs inside their cards passed.");
                }
                foreach (var button in content.Query<Button>().ToList())
                {
                    if (button.GetFirstAncestorOfType<Scroller>() != null || button.worldBound.width == 0) continue;
                    if (button.worldBound.xMin < content.contentViewport.worldBound.xMin - 1 || button.worldBound.xMax > content.contentViewport.worldBound.xMax + 1)
                        throw new InvalidDataException("Action button extends outside the page: " + button.text);
                    if (button.resolvedStyle.backgroundColor.r > .5f && !button.ClassListContains("item-tile-favorite"))
                        throw new InvalidDataException("Action button fell back to the light default theme: " + button.text);
                    if (button.text == "Copy profile ID" && button.worldBound.width > 200)
                        throw new InvalidDataException("A short action inside a foldout stretched across the card.");
                }
                var tabButtons = navigation.Query<Button>().ToList();
                var tabBounds = navigation.worldBound;
                var overflow = tabButtons.Where(b => b.worldBound.xMin < tabBounds.xMin - 1 || b.worldBound.xMax > tabBounds.xMax + 1 || b.worldBound.yMin < tabBounds.yMin - 1 || b.worldBound.yMax > tabBounds.yMax + 1).ToList();
                var tabRows = tabButtons.Select(b => Mathf.RoundToInt(b.worldBound.yMin)).Distinct().Count();
                var tallestTab = tabButtons.Max(b => b.worldBound.height);
                var variant = (mainMenu ? "menu" : page.ToLowerInvariant()) + "-" + resolution.x + "x" + resolution.y + suffix;
                report.AppendLine(variant + ": " + tabButtons.Count + " tabs, " + tabRows + " row(s), " + overflow.Count + " overflowing tabs, tallest tab " + tallestTab.ToString("F1") + "px; window " + width + "x" + height + ", scale " + scale + ".");
                if (tallestTab > 64) layoutFailure = "Navigation tab height expanded beyond its compact layout in " + variant + ".";
                report.Append(interactionReport);
                if (overflow.Count != 0) throw new InvalidDataException("Navigation overflow in " + variant + ": " + string.Join(", ", overflow.Select(b => b.text)));
                foreach (var tab in tabButtons)
                {
                    var unavailable = mainMenu && RaidPages.Contains(tab.text);
                    if (tab.enabledSelf == unavailable || tab.enabledInHierarchy == unavailable) throw new InvalidDataException("Incorrect native tab availability: " + tab.text);
                    if (!tab.parent.ClassListContains("nav-slot") || !tab.parent.enabledInHierarchy) throw new InvalidDataException("Tab tooltip wrapper must remain enabled: " + tab.text);
                    var expectedReason = unavailable ? RaidUnavailableReason : "";
                    if (tab.tooltip != expectedReason || tab.parent.tooltip != expectedReason) throw new InvalidDataException("Missing or incorrect tab unavailable reason: " + tab.text);
                }
                if (mainMenu)
                {
                    if (availability.text != MenuAvailabilityNote || availability.resolvedStyle.display == DisplayStyle.None) throw new InvalidDataException("The main-menu availability explanation is missing.");
                    report.AppendLine("  Main-menu native availability passed: Player, Health, Teleport, Bots, World disabled; Overview, Items, Kits, Diagnostics, Settings enabled. Disabled-tab reason strings exist on enabled nav-slot wrappers. Availability note is visible.");
                }
                if (grid != null)
                {
                    foreach (var name in new[] { "catalog-layout", "catalog-pane", "spawn-pane", "catalog-toolbar" })
                    {
                        var element = content.Q<VisualElement>(className: name);
                        report.AppendLine("  " + name + ": " + element.worldBound.ToString("F1") + "; layout=" + element.layout.ToString("F1"));
                    }
                    var catalogPane = content.Q<VisualElement>(className: "catalog-pane");
                    var spawnPane = content.Q<VisualElement>(className: "spawn-pane");
                    var categoryField = ((ItemFixture)grid.userData).Category;
                    var searchField = ((ItemFixture)grid.userData).Search;
                    report.AppendLine("  content=" + content.worldBound.ToString("F1") + "; search=" + searchField.worldBound.ToString("F1") + "; category=" + categoryField.worldBound.ToString("F1") + "; grid=" + grid.worldBound.ToString("F1"));
                    var paneOverlap = catalogPane.worldBound.xMax > spawnPane.worldBound.xMin + 1;
                    var categoryOverlap = categoryField.worldBound.xMax > catalogPane.worldBound.xMax + 1;
                    var categoryInput = categoryField.Q(className: "unity-base-field__input");
                    var categoryInputOverflow = categoryInput == null || categoryInput.worldBound.xMax > catalogPane.worldBound.xMax + 1;
                    var primary = spawnPane.Q<Button>("fixture-primary-spawn");
                    var viewport = content.contentViewport.worldBound;
                    var primaryClipped = primary.worldBound.yMin < viewport.yMin - 1 || primary.worldBound.yMax > viewport.yMax + 1;
                    report.AppendLine("  Catalog/spawn overlap=" + paneOverlap + "; category outside catalog=" + categoryOverlap + "; category input outside catalog=" + categoryInputOverflow + "; primary button clipped=" + primaryClipped + ".");
                    var labels = grid.Query<Label>().ToList().Where(l => !string.IsNullOrWhiteSpace(l.text) && l.resolvedStyle.display != DisplayStyle.None && l.resolvedStyle.visibility == Visibility.Visible && l.worldBound.width > 0).ToList();
                    var widthOverflow = grid.worldBound.xMax > content.worldBound.xMax + 1 || grid.worldBound.xMin < content.worldBound.xMin - 1;
                    var labelOverflow = labels.Count(l => l.parent != null && (l.worldBound.xMin < l.parent.worldBound.xMin - 1 || l.worldBound.xMax > l.parent.worldBound.xMax + 1));
                    report.AppendLine("  Shared ItemGridView: " + labels.Count + " rendered labels; horizontal grid overflow=" + widthOverflow + "; labels outside parent bounds=" + labelOverflow + ".");
                    if (widthOverflow || labelOverflow != 0 || labels.Count == 0 || paneOverlap || categoryOverlap || categoryInputOverflow || primaryClipped) layoutFailure = "Item grid failed layout bounds checks in " + variant + ".";
                }
                if (suffix == "-dropdown") ExerciseDropdown(((ItemFixture)grid.userData).Category, update, repaint, report);
                void SavePreview(string name)
                {
                    var previous = RenderTexture.active;
                    RenderTexture.active = render;
                    var texture = new Texture2D(resolution.x, resolution.y, TextureFormat.RGB24, false);
                    texture.ReadPixels(new Rect(0, 0, resolution.x, resolution.y), 0, 0);
                    texture.Apply();
                    File.WriteAllBytes(Path.Combine(output, "workbench-" + name + ".png"), texture.EncodeToPNG());
                    RenderTexture.active = previous;
                    UnityEngine.Object.DestroyImmediate(texture);
                }
                SavePreview(variant);
                if (suffix == "-dropdown")
                {
                    var field = ((ItemFixture)grid.userData).Category;
                    var menu = field.panel.visualTree.Q(className: "unity-base-dropdown");
                    var menuContent = menu.Q<ScrollView>().contentContainer;
                    using (var move = NavigationMoveEvent.GetPooled(NavigationMoveEvent.Direction.Down, EventModifiers.None))
                    { move.target = menuContent; menuContent.SendEvent(move); }
                    using (var submit = NavigationSubmitEvent.GetPooled())
                    { submit.target = menuContent; menuContent.SendEvent(submit); }
                    if (field.index <= 0 || menu.panel != null) throw new InvalidDataException("Dropdown keyboard selection did not apply or close the menu.");
                    report.AppendLine("  Native dropdown navigation/submit selects a choice and dismisses the popup.");
                }
                if (resolution.x == 1920 && (suffix == "" || suffix == "-minimum"))
                {
                    foreach (var foldout in content.Query<Foldout>().ToList()) foldout.value = true;
                    var activity = window.Q<Foldout>("activity-foldout"); activity.value = true;
                    var activityList = window.Q<ScrollView>("activity");
                    for (var i = 0; i < 30; i++) { var entry = new Label("14:32 Completed: diagnostic action " + i); entry.AddToClassList("log-entry"); activityList.Add(entry); }
                    for (var frame = 0; frame < 12; frame++) { update.Invoke(null, null); repaint.Invoke(null, null); }
                    var bottom = window.Q(className: "bottom-bar");
                    if (bottom.worldBound.yMax > window.worldBound.yMax + 1 || content.contentViewport.worldBound.height < 48)
                        throw new InvalidDataException("Expanded activity obscures page content or window controls: " + variant + "; viewport=" + content.contentViewport.worldBound + "; bottom=" + bottom.worldBound);
                    ExerciseScroll(activityList, update, repaint, report);
                    content.scrollOffset = new Vector2(0, content.verticalScroller.highValue);
                    for (var frame = 0; frame < 12; frame++) { update.Invoke(null, null); repaint.Invoke(null, null); }
                    SavePreview(variant + "-bottom-expanded");
                }
                if (layoutFailure != null)
                {
                    File.WriteAllText(Path.Combine(output, "visual-layout-validation.txt"), report.ToString());
                    throw new InvalidDataException(layoutFailure);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
                UnityEngine.Object.DestroyImmediate(panel);
                render.Release();
                UnityEngine.Object.DestroyImmediate(render);
                foreach (var icon in icons)
                {
                    UnityEngine.Object.DestroyImmediate(icon.texture);
                    UnityEngine.Object.DestroyImmediate(icon);
                }
            }
        }

        private static void ExerciseScroll(ScrollView scroll, MethodInfo update, MethodInfo repaint, StringBuilder report)
        {
            void Tick() { for (var frame = 0; frame < 6; frame++) { update.Invoke(null, null); repaint.Invoke(null, null); } }
            scroll.scrollOffset = Vector2.zero; Tick();
            var bar = scroll.verticalScroller;
            var thumb = bar.Q(className: "unity-base-slider__dragger");
            if (bar.resolvedStyle.display == DisplayStyle.None || thumb == null || thumb.worldBound.width < 8 || thumb.worldBound.height < 20 || thumb.worldBound.xMin < bar.worldBound.xMin - 1 || thumb.worldBound.xMax > bar.worldBound.xMax + 1)
                throw new InvalidDataException("Scrollbar thumb is hidden or clipped: " + bar.worldBound + "; thumb=" + thumb?.worldBound);
            var parent = scroll.GetFirstAncestorOfType<ScrollView>();
            var parentOffset = parent?.scrollOffset ?? Vector2.zero;
            using (var wheel = WheelEvent.GetPooled(new Event { type = EventType.ScrollWheel, delta = new Vector2(0, 1), mousePosition = scroll.contentViewport.worldBound.center }))
            {
                wheel.target = scroll.contentContainer;
                scroll.contentContainer.SendEvent(wheel);
            }
            Tick();
            if (Mathf.Abs(scroll.scrollOffset.y - Mathf.Min(54, bar.highValue)) > 1 || (parent != null && parent.scrollOffset != parentOffset))
                throw new InvalidDataException("Wheel must move the hovered scroll region by 54px without moving its parent: " + scroll.scrollOffset);
            scroll.scrollOffset = new Vector2(0, bar.highValue); Tick();
            if (thumb.worldBound.yMax > bar.worldBound.yMax + 1) throw new InvalidDataException("Scrollbar thumb escapes its track at the end.");
            scroll.scrollOffset = Vector2.zero; Tick();
            report.AppendLine("  Native wheel event: 54px step, isolated nested scrolling, visible thumb and end-of-track bounds passed.");
        }

        private static void ExerciseDropdown(DropdownField field, MethodInfo update, MethodInfo repaint, StringBuilder report)
        {
            field.choices = Enumerable.Range(0, 40).Select(i => "Category " + i).ToList();
            field.index = 0;
            typeof(DropdownField).GetMethod("ShowMenu", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(field, null);
            for (var frame = 0; frame < 12; frame++) { update.Invoke(null, null); repaint.Invoke(null, null); }
            var popup = field.panel.visualTree.Q(className: "unity-base-dropdown");
            var outer = popup?.Q(className: "unity-base-dropdown__container-outer");
            var label = popup?.Q<Label>(className: "unity-base-dropdown__label");
            if (outer == null || outer.resolvedStyle.backgroundColor.r > .2f || label == null || label.resolvedStyle.color.r < .7f)
                throw new InvalidDataException("Native dropdown did not inherit the dark workbench theme.");
            var scroll = popup.Q<ScrollView>();
            if (scroll.worldBound.height < 100 || scroll.worldBound.height > 362 || outer.worldBound.yMax > field.panel.visualTree.worldBound.yMax + 1)
                throw new InvalidDataException("Dropdown extends past its maximum height or the screen: scroll=" + scroll.worldBound + "; outer=" + outer.worldBound + "; root=" + field.panel.visualTree.worldBound);
            ExerciseScroll(scroll, update, repaint, report);
            if (popup.panel == null || scroll.worldBound.height < 100) throw new InvalidDataException("Dropdown closed or collapsed while scrolling.");
            report.AppendLine("  Actual native dropdown: dark background, readable text, bounded height, visible scrollbar and wheel scrolling passed.");
        }

        private static void ExerciseLogging(LoggingView view, MethodInfo update, MethodInfo repaint, StringBuilder report)
        {
            void Tick() { for (var frame = 0; frame < 6; frame++) { update.Invoke(null, null); repaint.Invoke(null, null); } }
            var list = view.Q<ListView>("logging-messages");
            var search = view.Q<TextField>("logging-search");
            if (list.itemsSource.Count != 100) throw new InvalidDataException("Logging fixture did not bind messages.");
            search.value = "message 99"; Tick();
            if (list.itemsSource.Count != 1) throw new InvalidDataException("Logging text filter failed.");
            list.SetSelection(0); Tick();
            if (!view.Q<TextField>("logging-detail").value.Contains("message 99")) throw new InvalidDataException("Logging selection details failed.");
            search.value = ""; Tick();
            view.Q<Toggle>("logging-pause").value = true; view.Refresh(); Tick();
            if (list.itemsSource.Count != 100) throw new InvalidDataException("Pause lost the displayed snapshot.");
            view.Q<Toggle>("logging-pause").value = false;
            var categories = view.Q<TextField>("logging-category-search"); categories.value = "inventory"; Tick();
            var row = view.Q<VisualElement>(className: "logging-category-row");
            if (row == null || row.Q<Toggle>().label != "inventory") throw new InvalidDataException("Native category filter failed.");
            var toggle = row.Q<Toggle>(); var level = row.Q<DropdownField>();
            var checkmark = toggle.Q(className: "unity-toggle__checkmark");
            if (checkmark == null || checkmark.worldBound.width < 10 || checkmark.worldBound.height < 10) throw new InvalidDataException("Native category checkbox is not visible.");
            if (toggle.worldBound.xMax > level.worldBound.xMin + 1 || level.worldBound.yMax > row.worldBound.yMax + 2)
                throw new InvalidDataException("Native category controls overlap.");
            categories.value = ""; view.Refresh(); Tick();
            ExerciseScroll(view.Q<ListView>(className: "logging-categories").Q<ScrollView>(), update, repaint, report);
            ExerciseScroll(list.Q<ScrollView>(), update, repaint, report);
            var messageScroll = list.Q<ScrollView>(); messageScroll.scrollOffset = new Vector2(0, 108); Tick();
            view.Refresh(); Tick();
            if (Mathf.Abs(messageScroll.scrollOffset.y - 108) > 1) throw new InvalidDataException("Logging refresh resets the user's scroll position.");
            messageScroll.scrollOffset = Vector2.zero; Tick();
            var detailButton = view.Query<Button>().ToList().Single(b => b.text == "Show details");
            typeof(Clickable).GetMethod("Invoke", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(detailButton.clickable, new object[] { null }); Tick();
            if (view.Q<TextField>("logging-detail").resolvedStyle.display == DisplayStyle.None) throw new InvalidDataException("Logging details cannot be opened.");
            typeof(Clickable).GetMethod("Invoke", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(detailButton.clickable, new object[] { null }); Tick();
            if (Mathf.Abs(categories.Q(className: "unity-base-field__input").worldBound.xMin - search.Q(className: "unity-base-field__input").worldBound.xMin) > 1)
                throw new InvalidDataException("Logging search fields do not align.");
            var clear = view.Q<Button>("logging-clear");
            var page = view.GetFirstAncestorOfType<ScrollView>();
            page.ScrollTo(clear); Tick();
            var viewport = view.GetFirstAncestorOfType<ScrollView>().contentViewport.worldBound;
            if (clear.worldBound.yMax > viewport.yMax + 1) throw new InvalidDataException("Logging action buttons are clipped: button=" + clear.worldBound + "; viewport=" + viewport + "; view=" + view.worldBound + "; list=" + list.worldBound);
            report.AppendLine("  Shared LoggingView: native category/text filters, selection details, paused snapshot, category row bounds, and visible action buttons passed.");
            if (page.horizontalScroller.resolvedStyle.display != DisplayStyle.None) throw new InvalidDataException("Logging page must not show an unnecessary horizontal scrollbar.");
            page.scrollOffset = Vector2.zero; Tick();
        }

        private static void PopulateLoggingFixture(VisualElement content)
        {
            var enabled = new HashSet<string> { "Unity info", "Errors", "Enable native gate", "inventory" };
            var categories = new[] { "aiData", "aiMoveData", "anim-events-emitter", "application", "assets", "backend", "exfiltration", "inventory", "player", "spatial-audio", "spawns" };
            var levels = categories.ToDictionary(name => name, _ => "Debug");
            var entries = Enumerable.Range(0, 100).Select(i => new LoggingEntryView { Time = "14:32:" + (i % 60).ToString("00"),
                Source = i % 2 == 0 ? "inventory" : "Unity", Level = i % 4 == 0 ? "Warning" : "Info",
                Message = i % 2 == 0 ? "Stash item added at grid position (4, 6)." : "Client scene initialized.", Text = "Offline layout fixture message " + i }).ToArray();
            var view = new LoggingView(new[] { "Unity info", "Warnings", "Errors", "Exceptions", "Assertions", "Enable native gate", "AI diagnostics" }
                .Select(name => new LoggingOptionView { Name = name, Hint = "Saved capture selection", Read = () => enabled.Contains(name), Write = value => { if (value) enabled.Add(name); else enabled.Remove(name); } }),
                () => categories.Select(name => new LoggingSourceView { Name = name, Read = () => enabled.Contains(name), Write = value => { if (value) enabled.Add(name); else enabled.Remove(name); },
                    ReadLevel = () => levels[name], WriteLevel = value => levels[name] = value }).ToArray(),
                () => entries, () => "Native logging gate: enabled · AI diagnostics: disabled\nCapture hooks ready · File output ready · Evicted: 0 · Truncated: 0 · File dropped: 0\nBepInEx/LogOutput/DebugPlus/client.log",
                () => enabled.Clear(), () => entries = Array.Empty<LoggingEntryView>(), _ => { }, (_, __) => { });
            content.Add(view);
            view.FitViewport((ScrollView)content);
        }

        private static void PopulateFormFixture(VisualElement content, string page)
        {
            VisualElement Row(VisualElement parent)
            {
                var row = new VisualElement(); row.AddToClassList("form-row"); parent.Add(row); return row;
            }
            void Action(VisualElement parent, string title)
            {
                var row = new VisualElement(); row.AddToClassList("action-row"); parent.Add(row);
                var button = new Button { text = title }; button.AddToClassList("action-main"); row.Add(button);
                var favorite = new Button { text = "☆" }; favorite.AddToClassList("favorite-button"); row.Add(favorite);
            }
            DropdownField Choice(VisualElement parent, string label, params string[] values)
            {
                var field = new DropdownField(label, values.ToList(), 0); parent.Add(field); return field;
            }
            if (page == "Player")
            {
                var card = PreviewCard(content, "Survival overrides", "Overrides reset each raid. Enable and disable are explicit commands to the selected player.");
                foreach (var name in new[] { "God mode", "Unlimited body / arm stamina", "Unlimited oxygen", "Hold energy", "Hold hydration" })
                {
                    var row = Row(card); var label = new Label(name); label.style.width = 222; row.Add(label);
                    Action(row, "Enable"); Action(row, "Disable");
                }
                card = PreviewCard(content, "Recovery & equipment", "God mode prevents ordinary damage. Debug health commands still apply.");
                foreach (var name in new[] { "Refill energy and hydration", "Repair equipped gear", "Clear weapon malfunction" }) Action(card, name);
            }
            else if (page == "Health")
            {
                var card = PreviewCard(content, "Body-part health", "Inspect live health at the right. Health commands use the native controller and respect its limits.");
                var row = Row(card); Choice(row, "Body part", "Chest", "Head", "Left Arm", "Right Arm"); row.Add(new FloatField("Health / damage") { value = 35 });
                row = Row(card); foreach (var name in new[] { "Set health", "Heal", "Damage" }) Action(row, name);
                Action(card, "Restore selected limb"); Action(card, "Full recovery");
                card = PreviewCard(content, "Health effects", "Duration is in seconds. Native effects may restrict body parts, intensity, or removal.");
                Choice(card, "Effect", "Light Bleeding", "Heavy Bleeding", "Fracture");
                row = Row(card); row.Add(new FloatField("Duration") { value = 60 }); row.Add(new FloatField("Intensity") { value = 1 });
                foreach (var name in new[] { "Add effect", "Remove selected effect", "Remove all effects" }) Action(card, name);
            }
            else if (page == "Bots")
            {
                var card = PreviewCard(content, "Spawn bots", "Choose a role, difficulty, and area for your encounter.");
                var row = Row(card); Choice(row, "Bot type", "Scav", "PMC", "Boss guard"); Choice(row, "Difficulty", "Impossible", "Normal");
                row = Row(card); row.Add(new IntegerField("How many") { value = 12 }); Choice(row, "Spawn area", "Near you", "Factory main hall"); Action(card, "Spawn bots");
                card = PreviewCard(content, "Active bots", "Select a bot below. Choose All bots only when you want to affect the whole population.");
                card.Add(new Label("Selected: Operator · Bot")); card.Add(new Button { text = "Select all bots" }); card.Add(new TextField("Find a bot"));
                var list = new ScrollView(); list.AddToClassList("picker-list"); card.Add(list);
                for (var i = 0; i < 12; i++) { var button = new Button { text = "Operator " + i + " · Bot" }; button.AddToClassList("picker-button"); list.Add(button); }
                foreach (var name in new[] { "Pause Bots", "Resume Bots", "Heal Bots", "Kill Bots", "Despawn Bots" }) Action(card, name);
            }
            else if (page == "Kits")
            {
                var card = PreviewCard(content, "Kit library", "Only included equipment slots are replaced. Pockets, secure containers, and special slots are excluded when capturing.");
                card.Add(new Button { text = "●  Field test kit · 18 items" }); card.Add(new TextField("New kit name") { value = "Field test kit" }); card.Add(new Button { text = "Capture selected equipment" });
                card = PreviewCard(content, "Native builds", "Import a weapon or equipment build as an editable DebugPlus kit."); card.Add(new Button { text = "Browse native builds" });
                card = PreviewCard(content, "Selected kit", "Spawn creates the kit without replacing equipment. Equipping requires a fresh replacement preview.");
                card.Add(new TextField("Kit name") { value = "Field test kit" });
                var equipment = new VisualElement(); equipment.AddToClassList("kit-equipment"); card.Add(equipment);
                foreach (var slot in new[] { "Primary weapon", "Tactical rig", "Body armor", "Backpack" })
                {
                    var item = new VisualElement(); item.AddToClassList("kit-equipment-card"); equipment.Add(item); item.Add(new Toggle(slot) { value = true });
                    var label = new Label("Long equipment name with attachments"); label.AddToClassList("wrap"); item.Add(label);
                    var attachments = new Foldout { text = "4 attachments / contents", value = true }; item.Add(attachments); attachments.Add(new Label("Magazine · scope · mount"));
                }
                var row = Row(card); foreach (var name in new[] { "Save name", "Export JSON", "Delete saved kit" }) row.Add(new Button { text = name });
                card.Add(new Toggle("Found in raid")); Action(card, "Spawn kit in inventory"); Action(card, "Spawn kit as ground loot");
                var notice = new Label("Preview required before replacement."); notice.AddToClassList("preview"); card.Add(notice);
                card.Add(new Button { text = "Preview equipment replacement" });
                var replace = new Button { text = "Replace and delete displaced equipment" }; replace.AddToClassList("danger"); replace.SetEnabled(false); card.Add(replace);
                card = PreviewCard(content, "Import a kit", "Enter the absolute path to a DebugPlus kit JSON file.");
                var path = new TextField("File path") { value = "F:/SPT 4.1.x/BepInEx/config/DebugPlus/kits/Field test kit.json" }; path.AddToClassList("wide-field"); card.Add(path); card.Add(new Button { text = "Import JSON" });
            }
            else if (page == "Diagnostics")
            {
                var card = PreviewCard(content, "Scene & player inspection", "Copy the current inspector contents or export a complete diagnostic report.");
                foreach (var name in new[] { "Copy scene inspection", "Copy selected player inspection", "Export diagnostic report" }) card.Add(new Button { text = name });
                card = PreviewCard(content, "Command history", "Search outcomes and messages from this session."); card.Add(new TextField("Filter"));
                var list = new ScrollView(); list.style.height = 260; card.Add(list);
                for (var i = 0; i < 30; i++) { var label = new Label("14:32 Completed: Test command " + i); label.AddToClassList("log-entry"); list.Add(label); }
                card = PreviewCard(content, "Existing debug tools", "Weapon inspection remains available through the existing debug controls and the player inspector."); Action(card, "Reload backend data");
            }
            else if (page == "Settings")
            {
                var card = PreviewCard(content, "Appearance & input", "Window position, dimensions, scale, and tool preferences are saved automatically.");
                card.Add(new Slider("UI scale", .7f, 1.6f) { value = 1, showInputField = true }); Choice(card, "Toggle key", "F10", "Insert", "Home"); card.Add(new Button { text = "Reset window position & size" });
                card = PreviewCard(content, "Server access", "Your server administrator controls who can use these tools.");
                var status = new Label("Administrator access confirmed"); status.AddToClassList("notice"); card.Add(status);
                var details = new Foldout { text = "Technical details for server setup", value = true }; details.AddToClassList("technical"); card.Add(details);
                details.Add(new Label("Your profile ID: 0123456789abcdef01234567")); details.Add(new Button { text = "Copy profile ID" }); card.Add(new Button { text = "Refresh permissions" });
                card = PreviewCard(content, "DebugPlus workbench", "An original UI Toolkit interface for deliberate, visible debugging actions.");
                var about = new Label("Overrides reset between raids. Kits, bookmarks, favorites, and this window persist. Opening the workbench does not pause the raid or enable protection."); about.AddToClassList("wrap"); card.Add(about);
            }
            else if (page == "Teleport")
            {
                var card = PreviewCard(content, "Move around the map", "Move to where you're looking, return to your previous position, or use saved places below.");
                var manual = new Foldout { text = "Manual coordinates", value = true }; card.Add(manual);
                var coordinates = Row(manual); coordinates.AddToClassList("coordinate-row");
                foreach (var axis in new[] { "X", "Y", "Z" }) coordinates.Add(new FloatField(axis) { value = -1234.567f });
                var yaw = new FloatField("Yaw (degrees)") { value = 180 }; yaw.AddToClassList("coordinate-yaw"); coordinates.Add(yaw);
                manual.Add(new Toggle("Exact placement (skip clearance check)"));
                Action(manual, "Go to these coordinates");
                Action(card, "Teleport to aimed surface"); Action(card, "Return to previous position");
                card = PreviewCard(content, "Players & spawn points", "Choose the player being moved in the header, then select a destination below.");
                card.Add(new DropdownField("Destination player", new List<string> { "Operator · You" }, 0));
                Action(card, "Teleport target to player");
                card = PreviewCard(content, "Saved places", "Save a location on this map, then return with one click.");
                card.Add(new TextField("Bookmark name") { value = "Test position" });
                card.Add(new Button { text = "Save current position" });
            }
            else
            {
                var card = PreviewCard(content, "Time of day", "Set the current daylight time and optionally hold it for a repeatable scene.");
                card.Add(new Slider("Hour", 0, 23.99f) { value = 12, showInputField = true });
                card.Add(new Toggle("Freeze daylight")); Action(card, "Apply time");
                card = PreviewCard(content, "Weather", "Use normalized controls to compose a scene, or start from a preset.");
                var weather = Row(card);
                foreach (var name in new[] { "Clouds", "Rain", "Fog", "Wind", "Thunder" }) weather.Add(new Slider(name, 0, 1) { value = .1f, showInputField = true });
                var presets = Row(card);
                foreach (var name in new[] { "Clear", "Overcast", "Storm" }) presets.Add(new Button { text = name });
                Action(card, "Apply weather"); Action(content, "Restore normal time and weather");
            }
        }

        private static void PopulateOverviewFixture(VisualElement content, bool mainMenu)
        {
            var card = PreviewCard(content, "Session at a glance", "Use the player selector above before changing a player.");
            var metric = new Label(mainMenu ? "Main menu · Inventory tools ready" : "Factory · Solo raid"); metric.AddToClassList("metric"); card.Add(metric);
            var row = new VisualElement(); row.AddToClassList("row"); card.Add(row);
            foreach (var name in new[] { "Player", "Health", "Items", "Kits", "Teleport", "Bots" })
            {
                var button = new Button { text = name };
                var unavailable = mainMenu && RaidPages.Contains(name);
                button.SetEnabled(!unavailable);
                button.tooltip = unavailable ? RaidUnavailableReason : "";
                row.Add(button);
            }
            card = PreviewCard(content, "Favorite tools", "Keep your most-used tools within reach.");
            foreach (var name in new[] { "★  God mode", "★  Add items", "★  Teleport" })
            {
                var button = new Button { text = name };
                var unavailable = mainMenu && name != "★  Add items";
                button.SetEnabled(!unavailable);
                button.tooltip = unavailable ? RaidUnavailableReason : "";
                card.Add(button);
            }
            card = PreviewCard(content, "Recent actions", "Pick up where you left off.");
            foreach (var name in new[] { "14:32  Refilled health", "14:29  Added a field kit" })
            {
                var button = new Button { text = name };
                var unavailable = mainMenu && name.Contains("health");
                button.SetEnabled(!unavailable);
                button.tooltip = unavailable ? RaidUnavailableReason : "";
                card.Add(button);
            }
        }

        private static ItemGridView PopulateItemFixture(VisualElement content, List<Sprite> icons)
        {
            var layout = new VisualElement(); layout.AddToClassList("catalog-layout"); content.Add(layout);
            var browser = new VisualElement(); browser.AddToClassList("catalog-pane"); layout.Add(browser);
            var search = new TextField("Search items") { value = "" }; browser.Add(search);
            var filters = new VisualElement(); filters.AddToClassList("row"); filters.AddToClassList("catalog-toolbar"); browser.Add(filters);
            var categories = new DropdownField("Category", new List<string> { "All categories", "Weapons", "Medical supplies", "Equipment", "Provisions", "Ammunition" }, 0); filters.Add(categories);
            var favorites = new Toggle("Favorites"); filters.Add(favorites);
            var count = new Label(); count.AddToClassList("muted"); browser.Add(count);
            var samples = new[]
            {
                new[] { "AK-74N assault rifle", "Weapons" }, new[] { "Salewa first aid kit", "Medical supplies" },
                new[] { "Field backpack", "Equipment" }, new[] { "Tactical helmet", "Equipment" },
                new[] { "5.45×39 mm ammunition", "Ammunition" }, new[] { "Water bottle", "Provisions" },
                new[] { "6B23 body armor", "Equipment" }, new[] { "Army bandage", "Medical supplies" },
                new[] { "Compact submachine gun", "Weapons" }, new[] { "Emergency medical kit", "Medical supplies" },
                new[] { "Expedition backpack", "Equipment" }, new[] { "Lightweight combat helmet", "Equipment" }
            };
            var items = new List<CatalogItemView>();
            for (var i = 0; i < samples.Length; i++)
            {
                var sprite = ProceduralFixtureIcon(i % 8);
                icons.Add(sprite);
                items.Add(new CatalogItemView { Id = "fixture-" + i, Name = samples[i][0], Category = samples[i][1], Icon = sprite });
            }
            var saved = new HashSet<string> { "fixture-1", "fixture-2" };
            var grid = new ItemGridView(id => Task.FromResult(items.FirstOrDefault(i => i.Id == id)?.Icon), saved.Contains);
            browser.Add(grid);
            grid.FitViewport((ScrollView)content);
            var toolbar = new VisualElement(); toolbar.AddToClassList("row"); browser.Add(toolbar);
            toolbar.Add(new Button(() => { search.value = ""; categories.index = 0; favorites.value = false; }) { text = "Clear filters" });
            toolbar.Add(new Button { text = "Refresh catalog" });
            var spawn = new VisualElement(); spawn.AddToClassList("spawn-pane"); layout.Add(spawn);
            var art = new Image { scaleMode = ScaleMode.ScaleToFit }; art.AddToClassList("selected-item-art"); spawn.Add(art);
            var title = new Label(); title.AddToClassList("selected-item-title"); spawn.Add(title);
            var subtitle = new Label(); subtitle.AddToClassList("card-description"); spawn.Add(subtitle);
            spawn.Add(new IntegerField("Quantity") { value = 1 });
            var buttonRow = new VisualElement(); buttonRow.AddToClassList("action-row"); spawn.Add(buttonRow);
            var button = new Button { text = "Add to inventory", name = "fixture-primary-spawn" }; button.AddToClassList("primary"); button.AddToClassList("action-main"); buttonRow.Add(button);
            var groundRow = new VisualElement(); groundRow.AddToClassList("action-row"); spawn.Add(groundRow);
            var ground = new Button { text = "Place on the ground" }; ground.AddToClassList("action-main"); groundRow.Add(ground);
            var summary = new Label("100% condition · Not found in raid"); summary.AddToClassList("card-description"); spawn.Add(summary);
            var options = new Foldout { text = "Condition & raid status", value = false }; spawn.Add(options);
            options.Add(new Slider("Condition (%)", 0, 100) { value = 100, showInputField = true });
            options.Add(new Toggle("Found in raid") { value = false });
            var note = new Label("Added to the selected player's inventory. Ground loot is a separate option."); note.AddToClassList("card-description"); spawn.Add(note);
            var technical = new Foldout { text = "Technical details", value = false }; technical.AddToClassList("technical"); spawn.Add(technical);
            var state = new ItemFixture { Items = items, Saved = saved, Search = search, Category = categories, Favorites = favorites, Title = title };
            grid.userData = state;
            void Select(CatalogItemView item)
            {
                state.SelectedId = item.Id;
                grid.Select(item.Id);
                art.sprite = item.Icon;
                title.text = item.Name;
                subtitle.text = item.Category;
            }
            void Filter()
            {
                var matches = items.Where(i => i.Name.IndexOf(search.value, StringComparison.OrdinalIgnoreCase) >= 0
                    && (categories.index == 0 || categories.value == i.Category) && (!favorites.value || saved.Contains(i.Id))).ToArray();
                grid.SetItems(matches);
                grid.Select(state.SelectedId);
                count.text = matches.Length + " items · select a card · star your favorites";
            }
            grid.Selected += Select;
            grid.FavoriteToggled += item => { if (!saved.Remove(item.Id)) saved.Add(item.Id); Filter(); };
            search.RegisterValueChangedCallback(_ => Filter());
            categories.RegisterValueChangedCallback(_ => Filter());
            favorites.RegisterValueChangedCallback(_ => Filter());
            Filter();
            Select(items[1]);
            return grid;
        }

        private sealed class ItemFixture
        {
            public List<CatalogItemView> Items;
            public HashSet<string> Saved;
            public TextField Search;
            public DropdownField Category;
            public Toggle Favorites;
            public Label Title;
            public string SelectedId;
        }

        private static void ExerciseItemGrid(ItemGridView grid, MethodInfo update, MethodInfo repaint, StringBuilder report)
        {
            var state = (ItemFixture)grid.userData;
            void Tick() { for (var frame = 0; frame < 6; frame++) { update.Invoke(null, null); repaint.Invoke(null, null); } }
            List<VisualElement> Cards() => grid.Query<VisualElement>(className: "item-tile").ToList().Where(c => c.userData is CatalogItemView && c.resolvedStyle.visibility == Visibility.Visible).ToList();
            void Bindings()
            {
                foreach (var card in Cards())
                {
                    var item = (CatalogItemView)card.userData;
                    if (card.Q<Label>("name").text != item.Name || card.Q<Label>("category").text != item.Category || card.Q<Image>("icon").sprite != item.Icon)
                        throw new InvalidDataException("Recycled item card contains stale labels or icon: " + item.Id);
                    if (card.ClassListContains("selected") != (item.Id == state.SelectedId)) throw new InvalidDataException("Item selection highlight is stale: " + item.Id);
                    if (card.Q<Button>("favorite").text != (state.Saved.Contains(item.Id) ? "★" : "☆")) throw new InvalidDataException("Item favorite indicator is stale: " + item.Id);
                }
            }
            var choose = Cards().First().Q<Button>();
            var collectionRow = grid.Q<VisualElement>(className: "unity-collection-view__item");
            var pseudo = typeof(VisualElement).GetProperty("pseudoStates", BindingFlags.NonPublic | BindingFlags.Instance);
            if (collectionRow == null || pseudo == null) throw new InvalidDataException("Cannot validate item row hover state.");
            var previousPseudo = pseudo.GetValue(collectionRow);
            pseudo.SetValue(collectionRow, Enum.ToObject(pseudo.PropertyType, Convert.ToInt32(previousPseudo) | Convert.ToInt32(Enum.Parse(pseudo.PropertyType, "Hover"))));
            Tick();
            if (collectionRow.resolvedStyle.backgroundColor.a > .001f) throw new InvalidDataException("Item row hover paints a full-row background.");
            pseudo.SetValue(collectionRow, previousPseudo); Tick();
            report.AppendLine("  Native item row hover remains transparent; individual cards retain their own hover state.");
            typeof(Clickable).GetMethod("Invoke", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(choose.clickable, new object[] { null });
            Tick();
            if (state.SelectedId != "fixture-0" || state.Title.text != state.Items[0].Name) throw new InvalidDataException("Native item selection event did not update the spawn pane.");
            Bindings();
            state.Search.value = "Salewa"; Tick();
            if (Cards().Count != 1 || ((CatalogItemView)Cards()[0].userData).Id != "fixture-1") throw new InvalidDataException("Name filter did not isolate the matching item.");
            state.Favorites.value = true; state.Category.value = "Medical supplies"; Tick(); Bindings();
            if (Cards().Count != 1) throw new InvalidDataException("Combined category/name/favorite filters returned the wrong items.");
            state.Search.value = "missing fixture item"; Tick();
            if (Cards().Count != 0 || grid.Q<Label>(className: "grid-empty").resolvedStyle.display == DisplayStyle.None) throw new InvalidDataException("Empty catalog state is not visible.");
            state.Search.value = ""; state.Favorites.value = false; state.Category.index = 0; Tick();
            var stress = Enumerable.Range(0, 72).Select(i => new CatalogItemView { Id = "fixture-scroll-" + i, Name = "Scroll fixture item " + i, Category = state.Items[i % state.Items.Count].Category, Icon = state.Items[i % state.Items.Count].Icon }).ToArray();
            state.SelectedId = null;
            grid.Select(null);
            grid.SetItems(stress); Tick();
            var initialCards = new HashSet<VisualElement>(Cards());
            var list = grid.Q<ListView>();
            var scroll = list.Q<ScrollView>();
            scroll.scrollOffset = new Vector2(0, 10000); Tick(); Bindings();
            var scrolled = scroll.scrollOffset.y;
            var recycledCards = Cards().Count(initialCards.Contains);
            if (scrolled <= 0 || recycledCards == 0) throw new InvalidDataException("Item fixture did not exercise scrolling and recycled cards.");
            ExerciseScroll(scroll, update, repaint, report);
            grid.SetItems(state.Items);
            scroll.scrollOffset = Vector2.zero; Tick(); Bindings();
            var selected = Cards().First(c => ((CatalogItemView)c.userData).Id == "fixture-1").Q<Button>();
            typeof(Clickable).GetMethod("Invoke", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(selected.clickable, new object[] { null });
            Tick(); Bindings();
            report.AppendLine("  Item interactions passed: native selection, name/category/favorite filters, empty state, icon/label/favorite bindings on " + recycledCards + " reused cards after scrolling " + scrolled.ToString("F0") + "px, restored selection.");
        }

        private static Sprite ProceduralFixtureIcon(int kind)
        {
            const int size = 128;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = Enumerable.Repeat(new Color32(0, 0, 0, 0), size * size).ToArray();
            var metal = new Color32(135, 170, 189, 255);
            var dark = new Color32(68, 96, 112, 255);
            var canvas = new Color32(115, 135, 104, 255);
            var red = new Color32(182, 99, 98, 255);
            var light = new Color32(221, 233, 231, 255);
            void Rect(int x, int y, int width, int height, Color32 color)
            {
                for (var yy = Mathf.Max(0, y); yy < Mathf.Min(size, y + height); yy++)
                    for (var xx = Mathf.Max(0, x); xx < Mathf.Min(size, x + width); xx++) pixels[yy * size + xx] = color;
            }
            void Ellipse(int x, int y, int radiusX, int radiusY, Color32 color)
            {
                for (var yy = y - radiusY; yy <= y + radiusY; yy++)
                    for (var xx = x - radiusX; xx <= x + radiusX; xx++)
                        if (xx >= 0 && yy >= 0 && xx < size && yy < size && (xx - x) * (xx - x) / (float)(radiusX * radiusX) + (yy - y) * (yy - y) / (float)(radiusY * radiusY) <= 1) pixels[yy * size + xx] = color;
            }
            switch (kind)
            {
                case 0: Rect(13, 57, 24, 18, canvas); Rect(34, 63, 58, 12, metal); Rect(85, 65, 32, 5, dark); Rect(54, 40, 9, 25, dark); Rect(70, 38, 11, 26, canvas); Rect(74, 76, 5, 7, metal); break;
                case 1: Rect(28, 31, 72, 66, red); Rect(36, 89, 56, 8, dark); Rect(57, 48, 14, 35, light); Rect(46, 59, 36, 13, light); break;
                case 2: Ellipse(64, 74, 35, 35, canvas); Rect(29, 23, 70, 52, canvas); Rect(40, 31, 48, 26, dark); Rect(50, 101, 28, 5, dark); Rect(22, 37, 8, 35, dark); Rect(99, 37, 8, 35, dark); break;
                case 3: Ellipse(64, 65, 41, 36, canvas); Rect(21, 37, 86, 20, canvas); Rect(27, 35, 74, 7, dark); Rect(57, 50, 14, 16, dark); break;
                case 4: for (var i = 0; i < 5; i++) { Rect(29 + i * 15, 27, 10, 47, new Color32(191, 164, 97, 255)); Ellipse(34 + i * 15, 76, 5, 14, metal); } break;
                case 5: Rect(50, 99, 28, 8, dark); Rect(48, 84, 32, 15, metal); Ellipse(64, 79, 22, 14, metal); Rect(42, 28, 44, 52, metal); Rect(42, 45, 44, 27, light); break;
                case 6: Rect(37, 22, 54, 74, canvas); Rect(24, 31, 80, 45, canvas); Rect(35, 91, 17, 17, dark); Rect(76, 91, 17, 17, dark); Rect(43, 48, 42, 30, dark); Rect(40, 32, 48, 6, dark); break;
                default: Rect(30, 33, 68, 57, light); Ellipse(30, 62, 12, 28, light); Ellipse(98, 62, 12, 28, light); Rect(50, 35, 7, 54, metal); Rect(66, 35, 7, 54, metal); Rect(59, 49, 10, 29, red); Rect(49, 58, 30, 10, red); break;
            }
            texture.SetPixels32(pixels);
            texture.Apply();
            texture.name = "Original offline fixture silhouette " + kind;
            return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(.5f, .5f), 100);
        }

        private static VisualElement PreviewCard(VisualElement parent, string title, string description)
        {
            var card = new VisualElement(); card.AddToClassList("card"); parent.Add(card);
            var heading = new Label(title); heading.AddToClassList("section-title"); card.Add(heading);
            var text = new Label(description); text.AddToClassList("card-description"); card.Add(text);
            return card;
        }
        private static void AssignShader(SerializedObject panel, string property, string name)
        {
            var field = panel.FindProperty(property);
            if (field == null) return;
            // Shader.Find is unsafe in a shared SDK: another mod can contain a serialized
            // shader with the same name. Resolve only Unity's own resources by path.
            var shader = new[] { "Resources/unity_builtin_extra", "Library/unity default resources", "Library/unity editor resources" }
                .SelectMany(AssetDatabase.LoadAllAssetsAtPath).OfType<Shader>().FirstOrDefault(s => s.name == name);
            if (shader == null)
            {
                var found = Shader.Find(name);
                var path = found == null ? "" : AssetDatabase.GetAssetPath(found);
                if (found != null && !path.StartsWith("Assets/", StringComparison.Ordinal)) shader = found;
            }
            field.objectReferenceValue = null;
            if (shader != null) field.objectReferenceValue = shader;
            if (field.objectReferenceValue == null) throw new InvalidDataException("Required UI Toolkit shader is unavailable: " + name);
        }
    }
}
