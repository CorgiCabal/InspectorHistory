using System.Collections.Generic;
using System.Linq;
using CorgiCabal.InspectorHistory;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;

namespace CorgiCabal.Editor
{
    public sealed class InspectorHistory : EditorWindow
    {
        const int k_MaxSnapshots = 6;
        const int FAV_COUNT_PER_ROW = 3;
        const string SAVED_OBJECTS_EDITOR_PREF = "InspectorHistoryIds";

        static readonly EditorPrefInt _maxHistory = new(12, "InspectorHistory_MaxHistory");
        static readonly EditorPrefInt _maxFavs    = new(6,  "InspectorHistory_MaxFavs");

        static readonly int[]    k_FavOptions     = { 3, 6, 9, 12, 15 };
        static readonly string[] k_FavLabels      = { "3", "6", "9", "12", "15" };
        static readonly int[]    k_HistoryOptions = { 6, 8, 10, 12, 15, 18, 20, 25, 30, 40, 50 };
        static readonly string[] k_HistoryLabels  = { "6", "8", "10", "12", "15", "18", "20", "25", "30", "40", "50" };

        // Cached in OnEnable/OnDisable so selection-tracking callbacks can reach
        // the window without calling GetWindow (which would create one if closed).
        static InspectorHistory _instance;

        // history[0..historyIndex-1] = back stack
        // history[historyIndex]      = current selection
        // history[historyIndex+1..]  = forward stack
        static readonly List<Object> history = new();
        static int historyIndex = -1;
        static Object lastRecordedSelection;

        // These two always work as a pair:
        //   isRestoringSelection: suppress the next spurious OnSelectionChanged.
        //   selectionToRestore:   the Object to re-apply when restoring.
        static Object selectionToRestore;
        static bool isRestoringSelection;

        // Unity fires selectionChanged once at editor startup with the already-selected
        // object. This flag skips that first event.
        static bool _skipFirstSelection;

        static Object[] favorites = new Object[6]; // sized from _maxFavs at load

        static readonly List<HistorySnapshot> undoSnapshots = new(4);
        static bool _pendingEditorPrefsSave;
        static bool _needsLoad;
        static GUIContent _revealIcon;
        static GUIStyle _iconButtonStyle;

        static readonly Dictionary<Object, bool> _isPrefabCache = new(16);
        static readonly Dictionary<Object, ItemTag> _tagCache = new(16);
        static readonly HashSet<Object> _dedupScratch = new(16);
        static readonly GUIContent _currentItemContent = new();
        static readonly Rect[] _favoriteRowRects = new Rect[FAV_COUNT_PER_ROW];
        static GUIStyle _centeredMiniLabel;
        static Texture2D bgTex;

        // *** History filter toggles — excluded types are purged on toggle and not re-added ***
        static readonly EditorPrefBool _showScene  = new(true, "InspectorHistory_ShowScene");
        static readonly EditorPrefBool _showSO     = new(true, "InspectorHistory_ShowSO");
        static readonly EditorPrefBool _showPrefab = new(true, "InspectorHistory_ShowPrefab");

        enum ObjFilterType { Scene, SO, Prefab }

        ReorderableList _historyList;

        [System.Serializable]
        sealed class HistorySave
        {
            public string[] objectGids;
            public int currentIndex;
            public int favCount; // 0 = legacy save, fall back to 6
        }

        /// <summary>
        /// In-session undo snapshot — stores Object refs directly, no slow GID API
        /// </summary>
        sealed class HistorySnapshot
        {
            public List<Object> history;
            public int historyIndex;
            public Object[] favorites;
            public Object selectionToRestore;
        }

        sealed class Content
        {
            public static readonly GUIContent sortHistory = new("Sort", "Alphabetize History");
            public static readonly GUIContent clearHistory = new("Clear", "Forget History");
            public static readonly GUIContent buttonBack = new("Back", "Shortcut: ALT + Left Arrow");
            public static readonly GUIContent buttonForward = new("Forward", "Shortcut: ALT + Right Arrow");
            public static readonly GUIContent helpbox = new("?",
                "Back / Forward\n" +
                "  ALT + click — jump to end / start\n" +
                "  ALT + Left / Right Arrow — keyboard shortcut\n" +
                "\n" +
                "Favorites\n" +
                "  Left-click empty slot — assign current selection\n" +
                "  Left-click filled slot — select it\n" +
                "  Middle-click — clear slot\n" +
                "  Drag from slot — use in Inspector, scene, etc.\n" +
                "  Drag onto slot — assign from Project / Hierarchy\n" +
                "\n" +
                "History\n" +
                "  Click — select & navigate\n" +
                "  Middle-click — remove from list");
            public static readonly GUIContent ping = new("Ping",
                "Ping the primary Inspector's object in the Project/Scene. " +
                "If multiple Inspectors are open, only 1 is the Primary one.");
            public static readonly GUIStyle styleCurrent = new(EditorStyles.whiteBoldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 14,
            };
        }

        [MenuItem("Window/Inspector History/Window", priority = 100)]
        public static void ShowWindow()
        {
            var w = GetWindow<InspectorHistory>("Inspector History", focus: true);
            w.SetSize();
        }

        [InitializeOnLoadMethod]
        static void InitializeSelectionTracking()
        {
            _skipFirstSelection = true;
            _isPrefabCache.Clear();
            _tagCache.Clear();
            _instance = null;

            Selection.selectionChanged -= OnSelectionChanged;
            Selection.selectionChanged += OnSelectionChanged;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            PrefabStage.prefabStageClosing -= OnPrefabStageClosed;
            PrefabStage.prefabStageClosing += OnPrefabStageClosed;
            EditorApplication.projectChanged -= OnProjectChanged;
            EditorApplication.projectChanged += OnProjectChanged;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;

            _needsLoad = true;
        }

        /// <summary>
        /// Suppress the auto-selection Unity fires when Prefab Mode closes (prevent recording a duplicate).
        /// We do NOT suppress on open — entering Prefab Mode fires a real selection that ResolvePrefabStageObject
        /// converts to the prefab asset, which we record correctly.
        /// </summary>
        static void OnPrefabStageClosed(PrefabStage _) => isRestoringSelection = true;

        /// <summary>
        /// Flush any pending deferred EditorPrefs write before the domain reloads.
        /// Without this, a selection made just before compilation is lost: the delayCall
        /// queued by SaveHistory() is destroyed mid-flight and the EditorPrefs are stale.
        /// </summary>
        static void OnBeforeAssemblyReload()
        {
            if (_pendingEditorPrefsSave)
            {
                _pendingEditorPrefsSave = false;
                WriteToEditorPrefs(BuildSave());
            }
        }
        static void OnProjectChanged() { _isPrefabCache.Clear(); _tagCache.Clear(); }

        static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                // Unity fires a spurious selection change on play-mode exit.
                // Suppress it and restore our saved selection once the editor has settled.
                isRestoringSelection = true;
                _needsLoad = true;
            }
        }

        static void OnSelectionChanged()
        {
            if (_instance == null) return;

            // Skip the spurious first event Unity fires at editor startup.
            if (_skipFirstSelection) { _skipFirstSelection = false; return; }

            // Restoring after domain reload or play-mode exit: reapply saved selection.
            if (isRestoringSelection)
            {
                isRestoringSelection = false;
                if (selectionToRestore != null)
                    Selection.activeObject = selectionToRestore;
                return;
            }

            var current = ResolvePrefabStageObject(Selection.activeObject);
            if (current == lastRecordedSelection) return;

            Record(current);
            lastRecordedSelection = current;
            if (current != null) selectionToRestore = current;

            _instance.Repaint();
            if (history.Count > 0) SaveHistory();
        }

        static Object ResolvePrefabStageObject(Object obj)
        {
            // When in Prefab Mode, Selection.activeObject is a transient stage GameObject
            // that is destroyed on domain reload. Record the persistent prefab asset instead.
            if (obj is GameObject && StageUtility.GetCurrentStage() is PrefabStage stage)
                return AssetDatabase.LoadAssetAtPath<GameObject>(stage.assetPath);
            return obj;
        }

        static void Record(Object obj)
        {
            // Always append to the end — forward items (navigated-past objects) are preserved
            // as older history rather than discarded, so going back then selecting something
            // new doesn't erase recent entries.
            if (obj != null && !IsFilteredOut(obj))
            {
                history.Add(obj);
                while (history.Count > _maxHistory.GetVal())
                    history.RemoveAt(0);
                historyIndex = history.Count - 1;
            }

            CleanHistory(skipItem: null);
            Deduplicate();
        }

        [MenuItem("Window/Inspector History/Back &_LEFT")]
        static void BackShortcut()
        {
            if (_instance != null) GoBack(false, _instance);
        }

        [MenuItem("Window/Inspector History/Forward &_RIGHT")]
        static void ForwardShortcut()
        {
            if (_instance != null) GoForward(false, _instance);
        }

        static void GoBack(bool alt, EditorWindow window)
        {
            if (historyIndex <= 0) return;
            int target = alt ? 0 : historyIndex - 1;
            historyIndex = target;
            ApplyNavigation(window);
        }

        static void GoForward(bool alt, EditorWindow window)
        {
            if (historyIndex >= history.Count - 1) return;
            int target = alt ? history.Count - 1 : historyIndex + 1;
            historyIndex = target;
            ApplyNavigation(window);
        }

        static void ApplyNavigation(EditorWindow window)
        {
            var target = history[historyIndex];
            selectionToRestore = target;
            lastRecordedSelection = target;
            Selection.activeObject = target;
            SaveHistory();
            window.Repaint();
        }

        static void LoadFromEditorPrefs()
        {
            // Save layout: [history items] [FAVORITES_COUNT favorites] [selectionToRestore]
            // currentIndex stores which history slot is current.
            var save = JsonUtility.FromJson<HistorySave>(EditorPrefs.GetString(SAVED_OBJECTS_EDITOR_PREF));
            RestoreFromSave(save);
            if (selectionToRestore != null)
                Selection.activeObject = selectionToRestore;
        }

        static void RestoreFromSave(HistorySave save)
        {
            if (save == null || save.objectGids == null) return;

            var objs = ResolveFromGlobalIds(save.objectGids);
            int savedFavCount = save.favCount > 0 ? save.favCount : 6;
            int navCount = objs.Length - savedFavCount - 1;
            if (navCount < 0) return;

            history.Clear();
            history.AddRange(objs.Take(navCount).Where(o => o != null));
            historyIndex = Mathf.Clamp(save.currentIndex, -1, history.Count - 1);

            int targetFavCount = _maxFavs.GetVal();
            favorites = new Object[targetFavCount];
            for (int i = 0; i < Mathf.Min(savedFavCount, targetFavCount); i++)
                favorites[i] = (navCount + i) < objs.Length ? objs[navCount + i] : null;

            var restoreIdx = navCount + savedFavCount;
            selectionToRestore = restoreIdx < objs.Length ? objs[restoreIdx] : null;
        }

        static void SaveHistory()
        {
            // Undo snapshots use direct Object references — no slow GID API needed in-session.
            var snap = new HistorySnapshot
            {
                history = new List<Object>(history),
                historyIndex = historyIndex,
                favorites = (Object[])favorites.Clone(),
                selectionToRestore = selectionToRestore,
            };

            if (undoSnapshots.Count >= k_MaxSnapshots)
                undoSnapshots.RemoveAt(0);
            undoSnapshots.Add(snap);

            // Debounce: rapid selections register only one deferred write.
            // BuildSave() (GetGlobalObjectIdsSlow) is deferred out of the hot path.
            if (!_pendingEditorPrefsSave)
            {
                _pendingEditorPrefsSave = true;
                EditorApplication.delayCall += () =>
                {
                    _pendingEditorPrefsSave = false;
                    WriteToEditorPrefs(BuildSave());
                };
            }
        }

        static void RestoreFromSnapshot(HistorySnapshot snap)
        {
            history.Clear();
            history.AddRange(snap.history);
            historyIndex = snap.historyIndex;
            favorites = (Object[])snap.favorites.Clone();
            selectionToRestore = snap.selectionToRestore;
        }

        static HistorySave BuildSave()
        {
            var combined = new List<Object>(history.Count + favorites.Length + 1);
            combined.AddRange(history);
            combined.AddRange(favorites);
            combined.Add(selectionToRestore);

            return new HistorySave
            {
                objectGids = GetGlobalIds(combined.ToArray()).Select(id => id.ToString()).ToArray(),
                currentIndex = historyIndex,
                favCount = favorites.Length,
            };
        }

        static void WriteToEditorPrefs(HistorySave save)
        {
            EditorPrefs.SetString(SAVED_OBJECTS_EDITOR_PREF, JsonUtility.ToJson(save));
        }

        static void CleanHistory(Object skipItem)
        {
            // Remove destroyed (null) objects and any occurrence of skipItem.
            // Adjusts historyIndex to compensate for removed entries.
            // Note: historyIndex is NOT skipped — a destroyed object at the current
            // position must be cleaned up too; Clamp below re-seats the index.
            for (int i = history.Count - 1; i >= 0; i--)
            {
                var o = history[i];
                if (!o || o == skipItem)
                {
                    EvictCaches(o);
                    history.RemoveAt(i);
                    if (i < historyIndex) historyIndex--;
                }
            }
            historyIndex = Mathf.Clamp(historyIndex, -1, history.Count - 1);
        }

        static void EvictCaches(Object obj)
        {
            _isPrefabCache.Remove(obj);
            _tagCache.Remove(obj);
        }

        static bool IsFilteredOut(Object obj)
        {
            if (!EditorUtility.IsPersistent(obj) && !_showScene.GetVal()) return true;
            if (obj is ScriptableObject && EditorUtility.IsPersistent(obj) && !_showSO.GetVal()) return true;
            if (IsPrefab(obj) && !_showPrefab.GetVal()) return true;
            return false;
        }

        static bool MatchesFilterType(Object obj, ObjFilterType type)
        {
            if (obj == null) return false;
            return type switch
            {
                ObjFilterType.Scene  => !EditorUtility.IsPersistent(obj),
                ObjFilterType.SO     => obj is ScriptableObject && EditorUtility.IsPersistent(obj),
                ObjFilterType.Prefab => IsPrefab(obj),
                _                    => false,
            };
        }

        static void PurgeByType(ObjFilterType type)
        {
            for (int i = history.Count - 1; i >= 0; i--)
            {
                if (!MatchesFilterType(history[i], type)) continue;
                EvictCaches(history[i]);
                history.RemoveAt(i);
                if (i < historyIndex) historyIndex--;
            }
            historyIndex = Mathf.Clamp(historyIndex, -1, history.Count - 1);
            SaveHistory();
            _instance?.Repaint();
        }

        static void Deduplicate()
        {
            _dedupScratch.Clear();
            for (int i = history.Count - 1; i >= 0; i--)
            {
                if (i == historyIndex)
                {
                    _dedupScratch.Add(history[i]);
                    continue;
                }
                if (!_dedupScratch.Add(history[i]))
                {
                    history.RemoveAt(i);
                    if (i < historyIndex) historyIndex--;
                }
            }
            _dedupScratch.Clear();
            historyIndex = Mathf.Clamp(historyIndex, -1, history.Count - 1);
        }

        void OnEnable()
        {
            _instance = this;
            _needsLoad = true;
        }

        void BuildHistoryList()
        {
            _historyList = new ReorderableList(history, typeof(Object),
                draggable: false, displayHeader: false,
                displayAddButton: false, displayRemoveButton: false)
            {
                elementHeight = EditorGUIUtility.singleLineHeight + 4f,

                drawElementBackgroundCallback = (rect, index, _, _) =>
                {
                    int h = history.Count - 1 - index;
                    Color tint = h == historyIndex
                        ? new Color(1f, .92f, .16f, .25f)
                        : h > historyIndex
                            ? new Color(1f, 1f, 1f, .18f)
                            : new Color(.08f, .08f, .08f, .25f);
                    EditorGUI.DrawRect(rect, tint);
                },

                drawElementCallback = (rect, index, _, _) =>
                {
                    int h = history.Count - 1 - index;
                    var obj = history[h];

                    if (IsClicked(rect, 2))
                    {
                        var removed = obj;
                        EditorApplication.delayCall += () =>
                        {
                            if (h >= history.Count) return;
                            EvictCaches(removed);
                            history.RemoveAt(h);
                            if (h < historyIndex) historyIndex--;
                            historyIndex = Mathf.Clamp(historyIndex, -1, history.Count - 1);
                            CleanHistory(null);
                            Deduplicate();
                            SaveHistory();
                            _instance?.Repaint();
                        };
                        return;
                    }

                    rect.y += 2f; rect.height -= 4f;

                    // Carve right-to-left: tag strip first (rightmost), then F button.
                    // For in-scene objects the F button is omitted and the tag absorbs its space.
                    const float TAG_W = 38f;
                    const float F_BTN_W = 17f;
                    const float PAD = 2f;
                    bool isSceneObj = obj != null && !EditorUtility.IsPersistent(obj);
                    var tagRect = BorrowWidth(ref rect, isSceneObj ? TAG_W + PAD + F_BTN_W : TAG_W, PAD);
                    var fRect   = isSceneObj ? default : BorrowWidth(ref rect, F_BTN_W, PAD);

                    // Main content.
                    if (h == historyIndex)
                    {
                        _currentItemContent.text = obj != null ? obj.name : "None";
                        _currentItemContent.image = obj != null ? EditorGUIUtility.ObjectContent(obj, obj.GetType()).image : null;
                        EditorGUI.LabelField(rect, _currentItemContent, Content.styleCurrent);
                    }
                    else
                    {
                        using (new EditorGUI.DisabledScope(true))
                        {
                            // Passing typeof(Object) for Texture bypasses Unity's large-thumbnail
                            // rendering path (which stretches the row) while preserving the normal
                            // ObjectField chrome (darker background + picker button).
                            var displayType = (obj is Texture || obj is Sprite) ? typeof(Object) : obj?.GetType() ?? typeof(Object);
                            EditorGUI.ObjectField(rect, obj, displayType, true);
                        }
                    }

                    // F button — reveal asset in OS file browser (not shown for in-scene objects).
                    _revealIcon ??= new GUIContent(EditorGUIUtility.IconContent("Folder Icon")) { tooltip = "Reveal in Finder" };
                    _iconButtonStyle ??= new GUIStyle(GUI.skin.button) { padding = new RectOffset(1, 1, 1, 1) };
                    if (!isSceneObj && GUI.Button(fRect, _revealIcon, _iconButtonStyle))
                        EditorUtility.RevealInFinder(AssetDatabase.GetAssetPath(obj));

                    // Type tag — right-side color strip with label.
                    var tag = GetItemTag(obj);
                    if (tag.HasValue) DrawItemTag(tagRect, tag);
                },

                onSelectCallback = l =>
                {
                    int h = history.Count - 1 - l.index;
                    historyIndex = h;
                    ApplyNavigation(this);
                    EditorGUIUtility.PingObject(history[h]);
                    l.index = -1;
                },
            };
        }

        void OnDisable()
        {
            if (_instance == this) _instance = null;
        }

        void SetSize()
        {
            minSize = new Vector2(400, 50);
            maxSize = new Vector2(800, 375);
        }

        void OnGUI()
        {
            if (_needsLoad)
            {
                _needsLoad = false;
                SetSize();
                bgTex = CreateGradient(4, 4, Color.clear, new Color(1f, .92f, .16f, .15f));
                BuildHistoryList();
                LoadFromEditorPrefs();
            }

            if (!bgTex)
                bgTex = CreateGradient(4, 4, Color.clear, new Color(1f, .92f, .16f, .15f));
            GUI.DrawTexture(new Rect(0, 0, position.width, position.height), bgTex, ScaleMode.StretchToFill);

            // Safety cleanup each repaint: removes destroyed objects, deduplicates.
            CleanHistory(skipItem: null);
            Deduplicate();

            const int HEIGHT = 26;
            EditorGUILayout.BeginHorizontal();

            GUI.enabled = historyIndex > 0;
            if (GUILayout.Button(Content.buttonBack, GUILayout.Height(HEIGHT)))
                GoBack(Event.current.alt, this);

            GUI.enabled = historyIndex < history.Count - 1;
            if (GUILayout.Button(Content.buttonForward, GUILayout.Height(HEIGHT)))
                GoForward(Event.current.alt, this);

            GUI.enabled = Selection.activeObject != null;
            if (GUILayout.Button(Content.ping, GUILayout.Height(HEIGHT)))
            {
                EditorWindow.GetWindow(System.Type.GetType("UnityEditor.ProjectBrowser,UnityEditor"));
                EditorGUIUtility.PingObject(Selection.activeObject);
            }

            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            GUILayout.Space(4);
            if (position.height < 40)
                return;

            var rect = NextLineZeroWidthRect(false, false);
            DrawSettingsRow(rect);
            FavoriteButtons();
            DrawFilterRow();
            DrawHistory();
        }

        void DrawHistory()
        {
            if (position.height < 97)
                return;

            var lastR = GUILayoutUtility.GetLastRect();
            var bgColor = GUI.backgroundColor;

            const float MINI_BTN_W = 43f;
            const float MINI_BTN_H = 18f;
            var clearRect = new Rect(lastR.xMax - MINI_BTN_W, lastR.yMin + MINI_BTN_H, MINI_BTN_W, MINI_BTN_H);
            var sortRect = new Rect(clearRect.xMin - MINI_BTN_W, clearRect.yMin, MINI_BTN_W, MINI_BTN_H);

            GUI.enabled = history.Count > 0;
            GUI.backgroundColor = new Color(1f, .92f, .016f, .2f);
            if (GUI.Button(clearRect, Content.clearHistory))
            {
                history.Clear();
                historyIndex = -1;
            }
            if (GUI.Button(sortRect, Content.sortHistory))
            {
                var current = historyIndex >= 0 ? history[historyIndex] : null;
                history.Sort((a, b) => System.StringComparer.CurrentCultureIgnoreCase.Compare(b.name, a.name));
                historyIndex = current != null ? history.IndexOf(current) : -1;
            }
            GUI.backgroundColor = bgColor;
            GUI.enabled = true;

            _historyList.DoLayoutList();
        }

        static void DrawFilterRow()
        {
            var rect = NextLineZeroWidthRect(false, false);
            // ReorderableList insets element rects by its default padding on the right;
            // match that so the [?][Undo] columns sit flush with [F][Tag] below.
            const float LIST_PAD = 6f;
            rect.width -= LIST_PAD;

            // Carve right side: [?][Undo] — widths mirror the [F][Tag] columns in the history rows below.
            const float UNDO_W = 38f; // matches TAG_W
            const float HELP_W = 17f; // matches F_BTN_W
            const float PAD = 2f;
            var undoRect = BorrowWidth(ref rect, UNDO_W, PAD);
            var helpRect = BorrowWidth(ref rect, HELP_W, PAD);

            // 3 filter toggles in the remaining left space
            float cbW = rect.width / 3f;
            var sceneRect  = new Rect(rect.x,           rect.y, cbW, rect.height);
            var soRect     = new Rect(rect.x + cbW,     rect.y, cbW, rect.height);
            var prefabRect = new Rect(rect.x + cbW * 2f, rect.y, cbW, rect.height);

            const string k_PurgeNote = " Disabling removes existing entries of this type from history.";
            bool showScene = _showScene.GetVal();
            bool newShowScene = EditorGUI.ToggleLeft(sceneRect, new GUIContent("Scene", "Show scene objects in history." + k_PurgeNote), showScene);
            if (newShowScene != showScene) { _showScene.SetVal(newShowScene); if (!newShowScene) PurgeByType(ObjFilterType.Scene); }

            bool showSO = _showSO.GetVal();
            bool newShowSO = EditorGUI.ToggleLeft(soRect, new GUIContent("S.O.", "Show ScriptableObjects in history." + k_PurgeNote), showSO);
            if (newShowSO != showSO) { _showSO.SetVal(newShowSO); if (!newShowSO) PurgeByType(ObjFilterType.SO); }

            bool showPrefab = _showPrefab.GetVal();
            bool newShowPrefab = EditorGUI.ToggleLeft(prefabRect, new GUIContent("Prefab", "Show prefab assets in history." + k_PurgeNote), showPrefab);
            if (newShowPrefab != showPrefab) { _showPrefab.SetVal(newShowPrefab); if (!newShowPrefab) PurgeByType(ObjFilterType.Prefab); }

            // "?" helpbox — visible but passive (no button affordance)
            helpRect.width -= 1;
            EditorGUI.DrawRect(helpRect, new Color(.45f, .45f, .45f, .55f));
            GUI.Label(helpRect, Content.helpbox, _centeredMiniLabel ??= new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter });

            // Undo button
            GUI.enabled = undoSnapshots.Count > 1;
            if (GUI.Button(undoRect, "Undo"))
            {
                undoSnapshots.RemoveAt(undoSnapshots.Count - 1);
                if (undoSnapshots.Count > 0)
                    RestoreFromSnapshot(undoSnapshots[^1]);
            }
            GUI.enabled = true;
        }

        static void FavoriteButtons()
        {
            // Ensure array size matches current pref (handles cross-session pref changes).
            int targetCount = _maxFavs.GetVal();
            if (favorites.Length != targetCount)
                ResizeFavorites(targetCount);

            bool anyChanged = false;
            Object selectionBefore = Selection.activeObject;

            const float STAR_W = 14f;
            for (int i = 0; i < favorites.Length; i++)
            {
                if (i % FAV_COUNT_PER_ROW == 0)
                {
                    var rowRect   = NextLineZeroWidthRect(false, false);
                    var starRect  = new Rect(rowRect.x, rowRect.y, STAR_W, rowRect.height);
                    var slotsRect = new Rect(rowRect.x + STAR_W, rowRect.y, rowRect.width - STAR_W, rowRect.height);
                    GUI.Label(starRect, "\u2B50", _centeredMiniLabel ??= new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter });
                    DivideIntoThree(slotsRect, out _favoriteRowRects[0], out _favoriteRowRects[1], out _favoriteRowRects[2]);
                }

                favorites[i] = ClickFavorite(_favoriteRowRects[i % FAV_COUNT_PER_ROW], favorites[i], out bool changed);
                anyChanged |= changed;
            }

            // Save if favorites changed without a selection change (selection change already saves).
            if (anyChanged && selectionBefore == Selection.activeObject)
                SaveHistory();
        }

        static Object ClickFavorite(Rect r, Object favorite, out bool changed)
        {
            Object original = favorite;

            // Accept drag-and-drop into this slot.
            if (Event.current.type == EventType.DragPerform && r.Contains(Event.current.mousePosition))
            {
                DragAndDrop.AcceptDrag();
                if (DragAndDrop.objectReferences.Length > 0)
                    favorite = DragAndDrop.objectReferences[0];
                Event.current.Use();
            }

            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && r.Contains(Event.current.mousePosition))
            {
                if (favorite == null)
                {
                    favorite = lastRecordedSelection; // click empty slot: assign current selection
                }
                else
                {
                    // Prepare drag (fires if mouse moves); also select on plain click.
                    DragAndDrop.PrepareStartDrag();
                    DragAndDrop.objectReferences = new Object[] { favorite };
                    DragAndDrop.StartDrag("Dragging Favorite");
                    Selection.activeObject = favorite;
                }
            }

            if (IsClicked(r, 2)) // middle-click to clear
                favorite = null;

            if (favorite == null)
            {
                favorite = (Object)EditorGUI.ObjectField(r, null, typeof(Object), allowSceneObjects: true);
            }
            else
            {
                favorite = (Object)EditorGUI.ObjectField(r, favorite, favorite.GetType(), allowSceneObjects: true);
                // Overlay invisible label so Unity shows the asset path as a tooltip on hover.
                if (favorite != null)
                {
                    string path = AssetDatabase.GetAssetPath(favorite);
                    if (!string.IsNullOrEmpty(path))
                        GUI.Label(r, new GUIContent(string.Empty, path));
                }
            }

            changed = favorite != original;
            return favorite;
        }

        static void DrawSettingsRow(Rect rect)
        {
            const float PAD = 2f;
            const float SECTION_GAP = 16f;
            float halfW = rect.width / 2f;

            // Favorites max popup — left half
            const float FAV_LABEL_W = 66f;
            var favR         = new Rect(rect.x, rect.y, halfW - SECTION_GAP / 2f, rect.height);
            var favLabelRect = new Rect(favR.x, favR.y, FAV_LABEL_W, favR.height);
            var favPopupRect = new Rect(favR.x + FAV_LABEL_W + PAD, favR.y, favR.width - FAV_LABEL_W - PAD, favR.height);
            GUI.Label(favLabelRect, "Favorites:");
            int currentFavs = _maxFavs.GetVal();
            int favIdx    = FindOptionIndex(k_FavOptions, currentFavs, 1);
            int newFavIdx = EditorGUI.Popup(favPopupRect, favIdx, k_FavLabels);
            if (newFavIdx != favIdx)
            {
                _maxFavs.SetVal(k_FavOptions[newFavIdx]);
                ResizeFavorites(k_FavOptions[newFavIdx]);
                SaveHistory();
            }

            // History max popup — right half
            const float HIST_LABEL_W = 50f;
            var histR         = new Rect(rect.x + halfW + SECTION_GAP / 2f, rect.y, halfW - SECTION_GAP / 2f, rect.height);
            var histLabelRect = new Rect(histR.x, histR.y, HIST_LABEL_W, histR.height);
            var histPopupRect = new Rect(histR.x + HIST_LABEL_W + PAD, histR.y, histR.width - HIST_LABEL_W - PAD, histR.height);
            GUI.Label(histLabelRect, "History:");
            int currentHist = _maxHistory.GetVal();
            int histIdx    = FindOptionIndex(k_HistoryOptions, currentHist, 3);
            int newHistIdx = EditorGUI.Popup(histPopupRect, histIdx, k_HistoryLabels);
            if (newHistIdx != histIdx)
            {
                int newMax = k_HistoryOptions[newHistIdx];
                _maxHistory.SetVal(newMax);
                while (history.Count > newMax)
                    history.RemoveAt(0);
                historyIndex = Mathf.Clamp(historyIndex, -1, history.Count - 1);
                SaveHistory();
            }
        }

        static void ResizeFavorites(int newCount)
        {
            if (favorites.Length == newCount) return;
            var arr = new Object[newCount];
            System.Array.Copy(favorites, arr, Mathf.Min(favorites.Length, newCount));
            favorites = arr;
        }

        static int FindOptionIndex(int[] options, int value, int defaultIdx)
        {
            for (int i = 0; i < options.Length; i++)
                if (options[i] == value) return i;
            return defaultIdx;
        }

        static bool IsPrefab(Object o)
        {
            if (!o) return false;
            if (_isPrefabCache.TryGetValue(o, out bool v)) return v;
            return _isPrefabCache[o] = PrefabUtility.IsPartOfPrefabAsset(o);
        }

        // *** History item type tag — right-side color strip ***
        readonly struct ItemTag
        {
            public readonly Color darkColor;
            public readonly float lightAlpha;
            public readonly string label;

            public bool HasValue => label != null;
            public static ItemTag None => default;

            public ItemTag(Color darkColor, float lightAlpha, string label)
            {
                this.darkColor = darkColor;
                this.lightAlpha = lightAlpha;
                this.label = label;
            }
        }

        static ItemTag GetItemTag(Object obj)
        {
            if (!obj) return ItemTag.None;
            if (_tagCache.TryGetValue(obj, out var cached)) return cached;
            return _tagCache[obj] = ComputeItemTag(obj);
        }

        static ItemTag ComputeItemTag(Object obj)
        {
            if (!EditorUtility.IsPersistent(obj))
                return new ItemTag(new Color(.3f, .55f, 1f, .45f), .2f, "Scene");
            if (obj is DefaultAsset && AssetDatabase.IsValidFolder(AssetDatabase.GetAssetPath(obj)))
                return new ItemTag(new Color(.85f, .72f, .2f, .45f), .25f, "Folder");
            if (obj is SceneAsset)
                return new ItemTag(new Color(.55f, .55f, .55f, .45f), .2f, "Scene");
            if (IsPrefab(obj))
                return new ItemTag(new Color(.65f, .3f, 1f, .45f), .2f, "Prefab");
            if (obj is Shader)
                return new ItemTag(new Color(.85f, .3f, .6f, .45f), .2f, "Shader");
            if (obj is Material)
                return new ItemTag(new Color(.2f, .72f, .65f, .45f), .2f, "Mat");
            if (obj is Sprite)
                return new ItemTag(new Color(.6f, .3f, .9f, .45f), .2f, "Sprite");
            if (obj is Texture)
                return new ItemTag(new Color(.85f, .5f, .2f, .45f), .2f, "Tex");
            if (obj is AudioClip)
                return new ItemTag(new Color(.3f, .8f, .75f, .45f), .2f, "Audio");
            if (obj is AnimationClip)
                return new ItemTag(new Color(.2f, .75f, .7f, .45f), .2f, "Clip");
            if (obj is UnityEditor.Animations.AnimatorController)
                return new ItemTag(new Color(.45f, .65f, .62f, .45f), .2f, "A-Ctrl");
            if (obj is Mesh)
                return new ItemTag(new Color(.5f, .75f, .55f, .45f), .2f, "Mesh");
            if (obj is MonoScript)
                return new ItemTag(new Color(.35f, .75f, .35f, .45f), .2f, "Script");
            if (obj is ScriptableObject)
                return new ItemTag(new Color(.8f, .35f, .35f, .45f), .2f, "S.O.");
            return ItemTag.None;
        }

        static void DrawItemTag(Rect rect, ItemTag tag)
        {
            var color = EditorGUIUtility.isProSkin
                ? tag.darkColor
                : new Color(tag.darkColor.r, tag.darkColor.g, tag.darkColor.b, tag.lightAlpha);
            EditorGUI.DrawRect(rect, color);
            EditorGUI.LabelField(rect, tag.label, _centeredMiniLabel ??= new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter });
        }

        // *** Utility methods (no external dependencies) ***

        static bool IsClicked(Rect clickRect, int mouseButton)
        {
            var ce = Event.current;
            if (clickRect.Contains(ce.mousePosition)
                && ce.type == EventType.MouseUp
                && (mouseButton == -1 || ce.button == mouseButton))
            {
                GUI.changed = true;
                ce.Use();
                if (ce.button == 2)
                    GUIUtility.keyboardControl = 0;
                return true;
            }
            return false;
        }

        static Rect BorrowWidth(ref Rect r, float width, float pad = 0f)
        {
            r.width -= width + pad;
            return new Rect(r.xMax + pad, r.y, width, r.height);
        }

        static Rect NextLineZeroWidthRect(bool extraBefore, bool extraAfter)
        {
            float line = EditorGUIUtility.singleLineHeight;
            float sp   = EditorGUIUtility.standardVerticalSpacing;
            if (!extraBefore && !extraAfter)
                return GUILayoutUtility.GetRect(0, line);
            if (extraBefore && !extraAfter)
            {
                var r = GUILayoutUtility.GetRect(0, line + sp);
                r.yMin += sp;
                return r;
            }
            if (!extraBefore && extraAfter)
            {
                var r = GUILayoutUtility.GetRect(0, line + sp);
                r.height -= sp;
                return r;
            }
            {
                var r = GUILayoutUtility.GetRect(0, line + 2 * sp);
                r.yMin += sp;
                r.height -= sp;
                return r;
            }
        }

        static void DivideIntoThree(Rect r, out Rect r1, out Rect r2, out Rect r3,
            float biasFirst01 = .33f, float biasSecond01 = .33f)
        {
            r1 = r2 = r3 = r;
            r1.width = r.width * biasFirst01;
            r2.xMin = r.xMin + r1.width;
            r2.width = r.width * biasSecond01;
            r3.xMin = r2.xMax;
        }

        static Texture2D CreateGradient(int width, int height, Color topLeft, Color bottomRight)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    float t = ((float)x / (width - 1) + (float)y / (height - 1)) * 0.5f;
                    tex.SetPixel(x, y, Color.Lerp(topLeft, bottomRight, t));
                }
            tex.Apply();
            return tex;
        }

        static GlobalObjectId[] GetGlobalIds(Object[] objs)
        {
            var output = new GlobalObjectId[objs.Length];
            GlobalObjectId.GetGlobalObjectIdsSlow(objs, output);
            return output;
        }

        static Object[] ResolveFromGlobalIds(string[] gids)
        {
            var objs = new Object[gids.Length];
            var ids  = new GlobalObjectId[gids.Length];
            for (int i = 0; i < gids.Length; i++)
                GlobalObjectId.TryParse(gids[i], out ids[i]);
            GlobalObjectId.GlobalObjectIdentifiersToObjectsSlow(ids, objs);
            return objs;
        }
    }
}
