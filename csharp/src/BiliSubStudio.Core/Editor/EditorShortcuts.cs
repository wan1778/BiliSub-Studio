namespace BiliSubStudio.Core.Editor;

/// <summary>
/// Scope in which a shortcut applies. The scope decides whether two bindings
/// sharing the same key + modifier conflict: the same key in two different
/// scopes is two different commands and may coexist.
/// </summary>
public enum EditorShortcutScope
{
    /// <summary>Applies at the app window (only while the app window is active).</summary>
    Application = 0,

    /// <summary>Applies while the Editor page is active.</summary>
    EditorPage = 1,

    /// <summary>Applies while the timeline holds keyboard focus.</summary>
    Timeline = 2,
}

/// <summary>
/// Functional group of a shortcut. Groups are used to section the help dialog
/// and to verify that every binding belongs to a valid group.
/// </summary>
public enum EditorShortcutGroup
{
    /// <summary>Video playback and timeline navigation.</summary>
    PlaybackAndNavigation = 0,

    /// <summary>Timeline editing (split, delete clip).</summary>
    TimelineEditing = 1,

    /// <summary>Timeline zoom and view.</summary>
    ZoomAndView = 2,

    /// <summary>Project operations (save, undo, redo).</summary>
    Project = 3,
}

/// <summary>
/// A shortcut binding in the registry: action id, key, modifiers, display
/// name, group, and scope. The UI never writes its own key list: it only
/// registers id → real behavior, while the registry is the single source of
/// truth for both the default key set and the help dialog.
/// </summary>
/// <param name="Id">Action identifier, used by the UI to register behavior.</param>
/// <param name="Key">Key name (e.g. "Space", "Left", "B", "Wheel").</param>
/// <param name="Modifiers">Modifiers, written in "Ctrl+Alt+Shift+Win" order.</param>
/// <param name="DisplayName">Short description shown to the user.</param>
/// <param name="Group">Functional group of the shortcut.</param>
/// <param name="Scope">Scope in which the shortcut applies.</param>
/// <param name="IsSupported">False when the command has no real behavior yet.</param>
/// <param name="UnsupportedNote">Why it is unsupported, shown to the user.</param>
public sealed record EditorShortcutBinding(
    string Id,
    string Key,
    string Modifiers,
    string DisplayName,
    EditorShortcutGroup Group,
    EditorShortcutScope Scope,
    bool IsSupported = true,
    string? UnsupportedNote = null)
{
    /// <summary>Gesture string shown to the user (e.g. "Ctrl+Shift+Left").</summary>
    public string Gesture => EditorShortcuts.FormatGesture(Key, Modifiers);

    /// <summary>
    /// Whether this binding is a pointer gesture (Ctrl+Wheel) handled by the
    /// timeline's PointerWheelChanged event. A pointer gesture has no key press
    /// to dispatch, so the page must not require a keyboard action for it.
    /// </summary>
    public bool IsPointerGesture => string.Equals(Key, "Wheel", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Registry of the Editor page's shortcut bindings. The registry is the single
/// source of truth: the default key set, the help table, and conflict detection
/// all read from here. Every binding carries an id; the UI registers id →
/// behavior.
/// </summary>
public sealed class EditorShortcutRegistry
{
    private readonly Dictionary<string, EditorShortcutBinding> _byId = new(StringComparer.Ordinal);
    private readonly List<EditorShortcutBinding> _bindings = [];

    /// <summary>Creates an empty registry.</summary>
    public EditorShortcutRegistry()
    {
    }

    /// <summary>Creates a registry from a binding list (registered in order, with conflict checks).</summary>
    /// <param name="bindings">Bindings to register.</param>
    public EditorShortcutRegistry(IEnumerable<EditorShortcutBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        foreach (var binding in bindings) Register(binding);
    }

    /// <summary>All registered bindings, in registration order.</summary>
    public IReadOnlyList<EditorShortcutBinding> Bindings => _bindings;

    /// <summary>Number of registered bindings.</summary>
    public int Count => _bindings.Count;

    /// <summary>Groups that have bindings, in the group's fixed order.</summary>
    public IReadOnlyList<EditorShortcutGroup> Groups
    {
        get
        {
            var groups = new List<EditorShortcutGroup>();
            foreach (var group in Enum.GetValues<EditorShortcutGroup>())
                if (_bindings.Any(binding => binding.Group == group)) groups.Add(group);
            return groups;
        }
    }

    /// <summary>Finds a binding by action id; null when absent.</summary>
    /// <param name="id">Action identifier.</param>
    public EditorShortcutBinding? Find(string? id)
        => id is not null && _byId.TryGetValue(id, out var binding) ? binding : null;

    /// <summary>Whether the registry already has a binding with this id.</summary>
    /// <param name="id">Action identifier.</param>
    public bool Contains(string? id) => id is not null && _byId.ContainsKey(id);

    /// <summary>Bindings belonging to one group.</summary>
    /// <param name="group">Group to fetch.</param>
    public IReadOnlyList<EditorShortcutBinding> InGroup(EditorShortcutGroup group)
        => _bindings.Where(binding => binding.Group == group).ToList();

    /// <summary>
    /// Registers a binding. Throws <see cref="InvalidOperationException"/> when the id
    /// is a duplicate or when key + modifier duplicates an existing binding in the
    /// SAME scope: that is a conflict which must be reported at startup.
    /// </summary>
    /// <param name="binding">Binding to register.</param>
    public void Register(EditorShortcutBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (string.IsNullOrWhiteSpace(binding.Id))
            throw new ArgumentException("Phím tắt phải có id hành động.", nameof(binding));
        if (string.IsNullOrWhiteSpace(binding.Key))
            throw new ArgumentException($"Phím tắt '{binding.Id}' phải có tên phím.", nameof(binding));
        if (_byId.ContainsKey(binding.Id))
            throw new InvalidOperationException($"Phím tắt id bị trùng: '{binding.Id}'.");

        var conflict = _bindings.FirstOrDefault(existing =>
            existing.Scope == binding.Scope
            && string.Equals(existing.Key, binding.Key, StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                EditorShortcuts.CanonicalModifiers(existing.Modifiers),
                EditorShortcuts.CanonicalModifiers(binding.Modifiers),
                StringComparison.OrdinalIgnoreCase));
        if (conflict is not null)
        {
            throw new InvalidOperationException(
                $"Xung đột phím tắt: '{binding.Id}' ({binding.Gesture}) trùng với '{conflict.Id}' "
                + $"({conflict.Gesture}) trong phạm vi {binding.Scope}.");
        }

        _byId[binding.Id] = binding;
        _bindings.Add(binding);
    }
}

/// <summary>
/// The Editor page's default shortcut set plus shared rules: display group
/// names, how to write gesture strings, and how to canonicalize modifiers.
/// </summary>
public static class EditorShortcuts
{
    /// <summary>Action id that toggles preview playback.</summary>
    public const string TogglePlaybackId = "editor.playback.toggle";

    /// <summary>Action id that steps back one frame.</summary>
    public const string StepBackwardId = "editor.timeline.step.backward";

    /// <summary>Action id that steps forward one frame.</summary>
    public const string StepForwardId = "editor.timeline.step.forward";

    /// <summary>Action id that steps back ten frames.</summary>
    public const string StepBackwardTenId = "editor.timeline.step.backward10";

    /// <summary>Action id that steps forward ten frames.</summary>
    public const string StepForwardTenId = "editor.timeline.step.forward10";

    /// <summary>Action id that goes to the timeline start.</summary>
    public const string GoToStartId = "editor.timeline.goToStart";

    /// <summary>Action id that goes to the timeline end.</summary>
    public const string GoToEndId = "editor.timeline.goToEnd";

    /// <summary>Action id that splits a clip at the playhead.</summary>
    public const string SplitAtPlayheadId = "editor.timeline.splitAtPlayhead";

    /// <summary>Action id that deletes the selected clip.</summary>
    public const string RemoveSelectedClipId = "editor.timeline.removeSelectedClip";

    /// <summary>Action id that zooms the timeline at the pointer.</summary>
    public const string ZoomAtPointerId = "editor.timeline.zoomAtPointer";

    /// <summary>Action id that saves the project.</summary>
    public const string SaveProjectId = "editor.project.save";

    /// <summary>Action id that undoes.</summary>
    public const string UndoId = "editor.project.undo";

    /// <summary>Action id that redoes.</summary>
    public const string RedoId = "editor.project.redo";

    private static readonly string[] ModifierOrder = ["Ctrl", "Alt", "Shift", "Win"];

    /// <summary>
    /// Default binding list. This is the single source of truth: the help dialog
    /// reads from the registry built from this list.
    /// </summary>
    public static IReadOnlyList<EditorShortcutBinding> CreateDefaultBindings() =>
    [
        new(
            TogglePlaybackId,
            "Space",
            string.Empty,
            "Phát / tạm dừng preview",
            EditorShortcutGroup.PlaybackAndNavigation,
            EditorShortcutScope.EditorPage),
        new(
            StepBackwardId,
            "Left",
            string.Empty,
            "Lùi một khung hình của media đang tua",
            EditorShortcutGroup.PlaybackAndNavigation,
            EditorShortcutScope.EditorPage),
        new(
            StepForwardId,
            "Right",
            string.Empty,
            "Tiến một khung hình của media đang tua",
            EditorShortcutGroup.PlaybackAndNavigation,
            EditorShortcutScope.EditorPage),
        new(
            StepBackwardTenId,
            "Left",
            "Shift",
            "Lùi mười khung hình của media đang tua",
            EditorShortcutGroup.PlaybackAndNavigation,
            EditorShortcutScope.EditorPage),
        new(
            StepForwardTenId,
            "Right",
            "Shift",
            "Tiến mười khung hình của media đang tua",
            EditorShortcutGroup.PlaybackAndNavigation,
            EditorShortcutScope.EditorPage),
        new(
            GoToStartId,
            "Home",
            string.Empty,
            "Về đầu dải thời gian (0)",
            EditorShortcutGroup.PlaybackAndNavigation,
            EditorShortcutScope.EditorPage),
        new(
            GoToEndId,
            "End",
            string.Empty,
            "Tới cuối dải thời gian",
            EditorShortcutGroup.PlaybackAndNavigation,
            EditorShortcutScope.EditorPage),
        new(
            SplitAtPlayheadId,
            "B",
            "Ctrl",
            "Tách đoạn video tại playhead (giống nút Tách)",
            EditorShortcutGroup.TimelineEditing,
            EditorShortcutScope.EditorPage),
        new(
            RemoveSelectedClipId,
            "Delete",
            string.Empty,
            "Xoá vùng hiệu ứng hoặc clip đang chọn (giống nút Xoá)",
            EditorShortcutGroup.TimelineEditing,
            EditorShortcutScope.EditorPage),
        new(
            ZoomAtPointerId,
            "Wheel",
            "Ctrl",
            "Zoom dải thời gian theo con trỏ (xoáy lên = vào, xoáy xuống = ra)",
            EditorShortcutGroup.ZoomAndView,
            EditorShortcutScope.Timeline),
        new(
            SaveProjectId,
            "S",
            "Ctrl",
            "Lưu project",
            EditorShortcutGroup.Project,
            EditorShortcutScope.EditorPage),
        new(
            UndoId,
            "Z",
            "Ctrl",
            "Hoàn tác thay đổi trên dải thời gian",
            EditorShortcutGroup.Project,
            EditorShortcutScope.EditorPage),
        new(
            RedoId,
            "Y",
            "Ctrl",
            "Làm lại thay đổi trên dải thời gian",
            EditorShortcutGroup.Project,
            EditorShortcutScope.EditorPage),
    ];

    /// <summary>Builds the default registry (conflicts are checked while building).</summary>
    public static EditorShortcutRegistry CreateDefaultRegistry() => new(CreateDefaultBindings());

    /// <summary>Group name shown in the help dialog.</summary>
    /// <param name="group">Group to display.</param>
    public static string GroupName(EditorShortcutGroup group) => group switch
    {
        EditorShortcutGroup.PlaybackAndNavigation => "Phát video và điều hướng",
        EditorShortcutGroup.TimelineEditing => "Chỉnh sửa timeline",
        EditorShortcutGroup.ZoomAndView => "Zoom và hiển thị",
        EditorShortcutGroup.Project => "Dự án",
        _ => group.ToString(),
    };

    /// <summary>
    /// Canonicalizes a modifier string to a fixed order (Ctrl, Alt, Shift, Win) so
    /// comparing two bindings does not depend on the order the user typed.
    /// </summary>
    /// <param name="modifiers">Modifier string, separated by "+".</param>
    public static string CanonicalModifiers(string? modifiers)
    {
        if (string.IsNullOrWhiteSpace(modifiers)) return string.Empty;
        var parts = modifiers.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var known = ModifierOrder.Where(modifier =>
            parts.Any(part => string.Equals(part, modifier, StringComparison.OrdinalIgnoreCase)));
        var extra = parts
            .Where(part => !ModifierOrder.Contains(part, StringComparer.OrdinalIgnoreCase))
            .OrderBy(part => part, StringComparer.OrdinalIgnoreCase);
        return string.Join('+', known.Concat(extra));
    }

    /// <summary>Builds the display gesture string (e.g. "Ctrl+Shift+Left").</summary>
    /// <param name="key">Key name.</param>
    /// <param name="modifiers">Modifier string.</param>
    public static string FormatGesture(string key, string? modifiers)
    {
        var canonical = CanonicalModifiers(modifiers);
        return canonical.Length == 0 ? key : $"{canonical}+{key}";
    }
}
