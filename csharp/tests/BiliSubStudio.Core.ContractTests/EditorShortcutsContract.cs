using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.ContractTests;

// The Editor page's shortcut system: a single binding registry in Core is the source of truth for
// both the default keys and the help dialog. The registry must have enough bindings, detect key +
// modifier conflicts inside the same scope, and allow the same key in two different scopes.
internal static class EditorShortcutsContract
{
    public static Task RunAsync()
    {
        var registry = EditorShortcuts.CreateDefaultRegistry();

        // Enough default bindings: keys, modifiers, groups and scopes.
        Expect(registry, EditorShortcuts.TogglePlaybackId, "Space", "", EditorShortcutGroup.PlaybackAndNavigation, EditorShortcutScope.EditorPage);
        Expect(registry, EditorShortcuts.StepBackwardId, "Left", "", EditorShortcutGroup.PlaybackAndNavigation, EditorShortcutScope.EditorPage);
        Expect(registry, EditorShortcuts.StepForwardId, "Right", "", EditorShortcutGroup.PlaybackAndNavigation, EditorShortcutScope.EditorPage);
        Expect(registry, EditorShortcuts.StepBackwardTenId, "Left", "Shift", EditorShortcutGroup.PlaybackAndNavigation, EditorShortcutScope.EditorPage);
        Expect(registry, EditorShortcuts.StepForwardTenId, "Right", "Shift", EditorShortcutGroup.PlaybackAndNavigation, EditorShortcutScope.EditorPage);
        Expect(registry, EditorShortcuts.GoToStartId, "Home", "", EditorShortcutGroup.PlaybackAndNavigation, EditorShortcutScope.EditorPage);
        Expect(registry, EditorShortcuts.GoToEndId, "End", "", EditorShortcutGroup.PlaybackAndNavigation, EditorShortcutScope.EditorPage);
        Expect(registry, EditorShortcuts.SplitAtPlayheadId, "B", "Ctrl", EditorShortcutGroup.TimelineEditing, EditorShortcutScope.EditorPage);
        Expect(registry, EditorShortcuts.RemoveSelectedClipId, "Delete", "", EditorShortcutGroup.TimelineEditing, EditorShortcutScope.EditorPage);
        Expect(registry, EditorShortcuts.ZoomAtPointerId, "Wheel", "Ctrl", EditorShortcutGroup.ZoomAndView, EditorShortcutScope.Timeline);
        Expect(registry, EditorShortcuts.SaveProjectId, "S", "Ctrl", EditorShortcutGroup.Project, EditorShortcutScope.EditorPage);

        // Ctrl+Wheel is a pointer gesture handled by the timeline's wheel event, not a key press, so
        // it must not demand a keyboard action; the page's registry audit skips it explicitly.
        var zoomAtPointer = registry.Find(EditorShortcuts.ZoomAtPointerId);
        Require(zoomAtPointer is not null && zoomAtPointer.IsPointerGesture,
            "the Ctrl+wheel zoom binding must be classified as a pointer gesture");
        var removeSelectedClip = registry.Find(EditorShortcuts.RemoveSelectedClipId);
        Require(removeSelectedClip is not null && !removeSelectedClip.IsPointerGesture,
            "keyboard bindings must not be classified as pointer gestures");

        // Undo/redo have real behaviour (phase 4): the binding must be reported as supported and must
        // not keep a "not supported" explanation.
        var undo = registry.Find(EditorShortcuts.UndoId);
        Require(undo is not null, "the undo binding must be declared");
        Require(undo!.IsSupported, "the undo binding must claim to be supported");
        Require(string.IsNullOrWhiteSpace(undo.UnsupportedNote),
            "a supported undo binding must not keep an unsupported note");
        Expect(registry, EditorShortcuts.RedoId, "Y", "Ctrl", EditorShortcutGroup.Project, EditorShortcutScope.EditorPage);
        var redo = registry.Find(EditorShortcuts.RedoId);
        Require(redo is not null && redo.IsSupported, "the redo binding must claim to be supported");
        Require(string.IsNullOrWhiteSpace(redo!.UnsupportedNote),
            "a supported redo binding must not keep an unsupported note");

        // Ids are unique and each binding belongs to a valid group.
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in registry.Bindings)
        {
            Require(ids.Add(binding.Id), $"the shortcut id '{binding.Id}' is duplicated");
            Require(Enum.IsDefined(binding.Group), $"the shortcut '{binding.Id}' has an invalid group");
            Require(Enum.IsDefined(binding.Scope), $"the shortcut '{binding.Id}' has an invalid scope");
            Require(!string.IsNullOrWhiteSpace(binding.DisplayName), $"the shortcut '{binding.Id}' has no display name");
        }

        // The help dialog groups match exactly the four declared groups.
        var groups = registry.Groups;
        Equal(4, groups.Count);
        Equal(EditorShortcutGroup.PlaybackAndNavigation, groups[0]);
        Equal(EditorShortcutGroup.TimelineEditing, groups[1]);
        Equal(EditorShortcutGroup.ZoomAndView, groups[2]);
        Equal(EditorShortcutGroup.Project, groups[3]);
        Require(registry.InGroup(EditorShortcutGroup.PlaybackAndNavigation).Count == 7,
            "the playback group must own the playback and navigation bindings");
        Require(registry.InGroup(EditorShortcutGroup.TimelineEditing).Count == 2,
            "the timeline group must own the split and remove bindings");
        Require(registry.InGroup(EditorShortcutGroup.ZoomAndView).Count == 1,
            "the zoom group must own the pointer zoom binding");
        Require(registry.InGroup(EditorShortcutGroup.Project).Count == 3,
            "the project group must own save, undo and redo");

        // Conflict detection: the same key + modifier in the same scope must be an error.
        var conflicting = new EditorShortcutRegistry();
        conflicting.Register(new EditorShortcutBinding(
            "test.first", "Space", string.Empty, "Test", EditorShortcutGroup.PlaybackAndNavigation, EditorShortcutScope.EditorPage));
        var conflict = Throws(() => conflicting.Register(new EditorShortcutBinding(
            "test.second", "space", string.Empty, "Test", EditorShortcutGroup.PlaybackAndNavigation, EditorShortcutScope.EditorPage)));
        Require(conflict.Contains("Xung đột phím tắt", StringComparison.Ordinal),
            "a duplicated key in the same scope must be reported as a conflict");

        // The same key + modifier in a different scope is a different command: valid.
        conflicting.Register(new EditorShortcutBinding(
            "test.third", "Space", string.Empty, "Test", EditorShortcutGroup.PlaybackAndNavigation, EditorShortcutScope.Timeline));
        Equal(2, conflicting.Count);

        // Modifiers written in a different order are still one shortcut.
        var normalized = new EditorShortcutRegistry();
        normalized.Register(new EditorShortcutBinding(
            "test.fourth", "Left", "Shift+Ctrl", "Test", EditorShortcutGroup.PlaybackAndNavigation, EditorShortcutScope.EditorPage));
        var normalizedConflict = Throws(() => normalized.Register(new EditorShortcutBinding(
            "test.fifth", "Left", "Ctrl+Shift", "Test", EditorShortcutGroup.PlaybackAndNavigation, EditorShortcutScope.EditorPage)));
        Require(normalizedConflict.Contains("Xung đột phím tắt", StringComparison.Ordinal),
            "the modifier order must not hide a conflict");

        // A duplicate id must also be an error.
        var duplicateId = Throws(() => normalized.Register(new EditorShortcutBinding(
            "test.fourth", "Right", string.Empty, "Test", EditorShortcutGroup.PlaybackAndNavigation, EditorShortcutScope.EditorPage)));
        Require(duplicateId.Contains("id bị trùng", StringComparison.Ordinal),
            "a duplicated shortcut id must be reported");

        // The default key set must not contradict itself.
        Equal(EditorShortcuts.CreateDefaultBindings().Count, EditorShortcuts.CreateDefaultRegistry().Count);

        // The displayed key string and modifier normalisation.
        Equal("Ctrl+Shift+Left", EditorShortcuts.FormatGesture("Left", "Shift+Ctrl"));
        Equal("Ctrl+Alt+Shift+Win", EditorShortcuts.CanonicalModifiers("Win+Shift+Alt+Ctrl"));
        Equal("Space", EditorShortcuts.FormatGesture("Space", string.Empty));
        Equal("Phát video và điều hướng", EditorShortcuts.GroupName(EditorShortcutGroup.PlaybackAndNavigation));
        Equal("Chỉnh sửa timeline", EditorShortcuts.GroupName(EditorShortcutGroup.TimelineEditing));
        Equal("Zoom và hiển thị", EditorShortcuts.GroupName(EditorShortcutGroup.ZoomAndView));
        Equal("Dự án", EditorShortcuts.GroupName(EditorShortcutGroup.Project));

        return Task.CompletedTask;
    }

    private static void Expect(
        EditorShortcutRegistry registry,
        string id,
        string key,
        string modifiers,
        EditorShortcutGroup group,
        EditorShortcutScope scope)
    {
        var binding = registry.Find(id);
        Require(binding is not null, $"the shortcut '{id}' is missing");
        Equal(key, binding!.Key);
        Equal(modifiers, EditorShortcuts.CanonicalModifiers(binding.Modifiers));
        Equal(group, binding.Group);
        Equal(scope, binding.Scope);
    }

    private static string Throws(Action action)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException error)
        {
            return error.Message;
        }
        throw new InvalidOperationException("the registry did not report the expected conflict");
    }

    private static void Equal(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidOperationException($"expected '{expected}' but got '{actual}'");
    }

    private static void Equal(int expected, int actual)
    {
        if (expected != actual) throw new InvalidOperationException($"expected {expected} but got {actual}");
    }

    private static void Equal(EditorShortcutGroup expected, EditorShortcutGroup actual)
    {
        if (expected != actual) throw new InvalidOperationException($"expected {expected} but got {actual}");
    }

    private static void Equal(EditorShortcutScope expected, EditorShortcutScope actual)
    {
        if (expected != actual) throw new InvalidOperationException($"expected {expected} but got {actual}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
