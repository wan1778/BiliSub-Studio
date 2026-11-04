using BiliSubStudio.App.Services;
using BiliSubStudio.Core.Editor;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace BiliSubStudio.App.Pages;

/// <summary>
/// The Editor page shortcut system: the binding registry lives in Core
/// (<see cref="EditorShortcuts"/>), and this page only registers the
/// id → real-behavior mapping and dispatches keyboard keys.
///
/// Three mandatory rules:
/// 1. Text-input boxes (TextBox/SearchBox/...) keep all of their keys: Space
///    types a space, Delete/Backspace deletes a character.
/// 2. While a dialog is open, commands must not touch the timeline behind it.
/// 3. Keys only take effect while the Editor page is active and the app window
///    is active (the operating system does not send keys to an inactive window).
/// </summary>
public sealed partial class EditorPage
{
    private EditorShortcutRegistry _shortcutRegistry = new();
    private readonly Dictionary<string, Func<Task>> _shortcutActions = new(StringComparer.Ordinal);
    private bool _shortcutsReady;
    private int _editorDialogDepth;

    /// <summary>The shortcut registry currently in use by the Editor page.</summary>
    internal EditorShortcutRegistry ShortcutRegistry => _shortcutRegistry;

    /// <summary>Whether an Editor page dialog is currently open.</summary>
    internal bool EditorDialogOpen => _editorDialogDepth > 0;

    /// <summary>
    /// Builds the registry and registers the id → real-behavior mapping. Each
    /// behavior calls the page's existing command (the Split button, the Remove
    /// button, play/pause of the player, saving the project) rather than
    /// rewriting the logic.
    /// </summary>
    private void InitializeEditorShortcuts()
    {
        try
        {
            var registry = EditorShortcuts.CreateDefaultRegistry();
            var actions = new Dictionary<string, Func<Task>>(StringComparer.Ordinal)
            {
                [EditorShortcuts.TogglePlaybackId] = TogglePlaybackAsync,
                [EditorShortcuts.StepBackwardId] = () => StepTimelineFramesAsync(-1),
                [EditorShortcuts.StepForwardId] = () => StepTimelineFramesAsync(1),
                [EditorShortcuts.StepBackwardTenId] = () => StepTimelineFramesAsync(-10),
                [EditorShortcuts.StepForwardTenId] = () => StepTimelineFramesAsync(10),
                [EditorShortcuts.GoToStartId] = GoToTimelineStartAsync,
                [EditorShortcuts.GoToEndId] = GoToTimelineEndAsync,
                [EditorShortcuts.SplitAtPlayheadId] = SplitTimelineClipAtPlayheadAsync,
                [EditorShortcuts.RemoveSelectedClipId] = RemoveSelectedTimelineClipAsync,
                [EditorShortcuts.SaveProjectId] = SaveProjectFromShortcutAsync,
                [EditorShortcuts.UndoId] = UndoTimelineAsync,
                [EditorShortcuts.RedoId] = RedoTimelineAsync,
            };
            foreach (var binding in registry.Bindings)
            {
                if (!binding.IsSupported) continue;
                // Ctrl+Wheel is a real pointer gesture handled by
                // TimelineScrollViewer_PointerWheelChanged, not a key press, so
                // it must not demand a keyboard action (and must not disable
                // every other shortcut when it has none).
                if (binding.IsPointerGesture) continue;
                if (!actions.ContainsKey(binding.Id))
                {
                    throw new InvalidOperationException($"Phím tắt '{binding.Id}' chưa có hành vi thật.");
                }
            }

            _shortcutRegistry = registry;
            _shortcutActions.Clear();
            foreach (var pair in actions) _shortcutActions[pair.Key] = pair.Value;
            // Shortcuts are listened for in the TUNNEL phase (PreviewKeyDown)
            // so they come before the scrub slider's and the text box's arrow
            // keys: text boxes keep all their keys, and the page's shortcuts
            // are not applied twice.
            AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(EditorPage_PreviewKeyDown), handledEventsToo: true);
            _shortcutsReady = true;
        }
        catch (Exception error)
        {
            _shortcutsReady = false;
            StatusText.Text = "Lỗi đăng ký phím tắt: " + error.Message;
            StartupDiagnostics.WriteException("editor-shortcuts-init", error);
        }
    }

    /// <summary>
    /// Dispatches a keyboard key according to the registry. Returns true when
    /// the shortcut consumed the key, blocking further processing.
    /// </summary>
    /// <param name="e">The keyboard event.</param>
    private bool HandleEditorShortcut(KeyRoutedEventArgs e)
    {
        if (e.Handled) return false;
        if (!_shortcutsReady || !IsLoaded) return false;
        if (WorkspaceGrid.Visibility != Visibility.Visible) return false;
        if (EditorDialogOpen) return false;
        var modifiers = CurrentEditorShortcutModifiers();
        if (EditorTextInputBlocksShortcut(e.Key, modifiers)) return false;
        if (EditorFocusOwnsActivationKey(e.Key)) return false;
        if (IsEditorNavigationKey(e.Key) && EditorFocusOwnsNavigationKeys()) return false;

        var binding = FindEditorShortcutBinding(e.Key, modifiers);
        if (binding is null) return false;
        if (!binding.IsSupported)
        {
            StatusText.Text = binding.UnsupportedNote ?? "Phím tắt này chưa được hỗ trợ.";
            e.Handled = true;
            return true;
        }
        if (!_shortcutActions.TryGetValue(binding.Id, out var action))
        {
            StatusText.Text = $"Phím tắt {binding.Gesture} chưa có hành vi thật.";
            e.Handled = true;
            return true;
        }

        e.Handled = true;
        _ = RunEditorShortcutAsync(binding, action);
        return true;
    }

    /// <summary>Runs the shortcut's behavior and reports an error if the command fails.</summary>
    /// <param name="binding">The binding that was triggered.</param>
    /// <param name="action">The real behavior of the binding.</param>
    private async Task RunEditorShortcutAsync(EditorShortcutBinding binding, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception error)
        {
            StatusText.Text = $"Không thực hiện được phím tắt {binding.Gesture}: {error.Message}";
        }
    }

    /// <summary>
    /// Finds a binding by key plus the current modifiers. Only bindings whose
    /// scope is the Editor page are handled here; timeline-scoped bindings are
    /// handled by the timeline itself.
    /// </summary>
    /// <param name="key">The key pressed.</param>
    /// <param name="modifiers">The modifiers held, already normalized.</param>
    private EditorShortcutBinding? FindEditorShortcutBinding(VirtualKey key, string modifiers)
    {
        var name = EditorShortcutKeyName(key);
        foreach (var binding in _shortcutRegistry.Bindings)
        {
            if (binding.Scope != EditorShortcutScope.EditorPage) continue;
            if (!string.Equals(binding.Key, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(
                    EditorShortcuts.CanonicalModifiers(binding.Modifiers),
                    modifiers,
                    StringComparison.OrdinalIgnoreCase)) continue;
            return binding;
        }
        return null;
    }

    /// <summary>Key name following the Core registry's convention.</summary>
    /// <param name="key">The key pressed.</param>
    private static string EditorShortcutKeyName(VirtualKey key) => key switch
    {
        VirtualKey.Space => "Space",
        VirtualKey.Left => "Left",
        VirtualKey.Right => "Right",
        VirtualKey.Up => "Up",
        VirtualKey.Down => "Down",
        VirtualKey.Home => "Home",
        VirtualKey.End => "End",
        VirtualKey.Delete => "Delete",
        VirtualKey.Back => "Backspace",
        _ => key.ToString(),
    };

    /// <summary>The modifiers currently held, written in the registry's standard order.</summary>
    private static string CurrentEditorShortcutModifiers()
    {
        var parts = new List<string>(4);
        if (IsEditorShortcutKeyDown(VirtualKey.Control)) parts.Add("Ctrl");
        if (IsEditorShortcutKeyDown(VirtualKey.Menu)) parts.Add("Alt");
        if (IsEditorShortcutKeyDown(VirtualKey.Shift)) parts.Add("Shift");
        if (IsEditorShortcutKeyDown(VirtualKey.LeftWindows)) parts.Add("Win");
        return EditorShortcuts.CanonicalModifiers(string.Join('+', parts));
    }

    /// <summary>The held state of a key (used for modifiers).</summary>
    /// <param name="key">The key to query.</param>
    private static bool IsEditorShortcutKeyDown(VirtualKey key)
        => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    /// <summary>
    /// Whether the currently focused element is a text-input box. Text boxes
    /// keep all their keys: Space types a space, Delete/Backspace deletes a
    /// character, arrows move the caret. Timeline shortcuts must not steal
    /// focus from a text box.
    /// </summary>
    private bool IsEditorTextInputFocused()
    {
        var focused = FocusManager.GetFocusedElement(XamlRoot);
        return focused switch
        {
            TextBox => true,
            PasswordBox => true,
            RichTextBlock => true,
            RichEditBox => true,
            AutoSuggestBox => true,
            NumberBox => true,
            ComboBox combo => combo.IsEditable,
            _ => false,
        };
    }

    /// <summary>
    /// Whether a text-input box keeps this key for itself. Text boxes keep all
    /// their text-editing keys (Space, Delete/Backspace, arrows, Home/End,
    /// Ctrl+Z/Ctrl+Y), while Ctrl+S still saves the project like in every app.
    /// </summary>
    /// <param name="key">The key pressed.</param>
    /// <param name="modifiers">The modifiers held, already normalized.</param>
    private bool EditorTextInputBlocksShortcut(VirtualKey key, string modifiers)
    {
        if (!IsEditorTextInputFocused()) return false;
        if (key is VirtualKey.Space or VirtualKey.Delete or VirtualKey.Back
            or VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down
            or VirtualKey.Home or VirtualKey.End) return true;
        if (!modifiers.Contains("Ctrl", StringComparison.OrdinalIgnoreCase)) return false;
        return key is VirtualKey.Z or VirtualKey.Y;
    }

    /// <summary>Navigation keys of the timeline (scrub by frame).</summary>
    /// <param name="key">The key pressed.</param>
    private static bool IsEditorNavigationKey(VirtualKey key)
        => key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Home or VirtualKey.End;

    /// <summary>
    /// Whether the currently focused element uses Space itself (buttons, combo
    /// boxes, lists). Space belongs to that control: clicking a button with the
    /// mouse and then pressing Space re-triggers that button, it does not
    /// play/pause the preview.
    /// </summary>
    /// <param name="key">The key pressed.</param>
    private bool EditorFocusOwnsActivationKey(VirtualKey key)
    {
        if (key != VirtualKey.Space) return false;
        return FocusManager.GetFocusedElement(XamlRoot) is ButtonBase or ComboBox or ListViewBase;
    }

    /// <summary>
    /// Whether the currently focused element uses the arrow/Home/End keys
    /// itself (volume slider, zoom slider, combo box, list). That control's
    /// keys must be preserved; only the SCRUB slider yields the arrow keys to
    /// the page's shortcut system.
    /// </summary>
    private bool EditorFocusOwnsNavigationKeys()
    {
        var focused = FocusManager.GetFocusedElement(XamlRoot);
        return focused switch
        {
            ComboBox => true,
            ListViewBase => true,
            ScrollViewer => true,
            RangeBase => !ReferenceEquals(focused, Timeline),
            _ => false,
        };
    }

    /// <summary>
    /// Timeline shortcuts must never steal focus: the handler only reads the
    /// focus state, never calls Focus(), and never reassigns a text box's
    /// focus. Keys coming from the timeline also go through the same registry.
    /// </summary>
    /// <param name="sender">The event source.</param>
    /// <param name="e">The keyboard event.</param>
    private void EditorPage_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Only the SCRUB slider yields the arrow/Home/End keys to the page's
        // shortcut system (scrubbing by frame instead of jumping by
        // SmallChange). The volume slider, zoom slider, combo boxes and lists
        // keep all their keys; so do text boxes.
        if (!ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), Timeline)) return;
        if (!IsEditorNavigationKey(e.Key)) return;
        HandleEditorShortcut(e);
    }

    /// <summary>Steps back/forward exactly the given frame count according to the real fps of the media being scrubbed.</summary>
    /// <param name="frames">Number of frames (negative = back, positive = forward).</param>
    private Task StepTimelineFramesAsync(int frames)
    {
        var frameRate = TimelineFrameRateAt(Timeline.Value);
        var index = EditorTimelinePlayhead.FrameIndex(Timeline.Value, frameRate) + frames;
        return SeekEditorToTimelineAsync(EditorTimelinePlayhead.FrameTime(index, frameRate));
    }

    /// <summary>Moves the playhead to the start of the timeline.</summary>
    private Task GoToTimelineStartAsync() => SeekEditorToTimelineAsync(0);

    /// <summary>Moves the playhead to the end of the timeline.</summary>
    private Task GoToTimelineEndAsync() => SeekEditorToTimelineAsync(TimelineDuration);

    /// <summary>Saves the project using the page's real save command.</summary>
    private async Task SaveProjectFromShortcutAsync()
    {
        if (_project is null)
        {
            StatusText.Text = "Chưa mở project nên chưa có gì để lưu.";
            return;
        }
        await SaveProjectNowAsync();
        StatusText.Text = "Đã lưu project.";
    }

    /// <summary>Opens the shortcut help dialog (built from the real registry).</summary>
    /// <param name="sender">The event source.</param>
    /// <param name="e">The button-click event.</param>
    private async void EditorShortcutsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ShowEditorShortcutsDialogAsync();
        }
        catch (Exception error)
        {
            StatusText.Text = "Không mở được hộp thoại phím tắt: " + error.Message;
        }
    }

    /// <summary>
    /// Tells the shortcut system that one of the page's dialogs is open:
    /// while it is, shortcuts must not touch the timeline behind it.
    /// </summary>
    internal IDisposable BeginEditorDialogScope() => new EditorDialogScope(this);

    /// <summary>Scope of an open Editor page dialog.</summary>
    private sealed class EditorDialogScope : IDisposable
    {
        private readonly EditorPage _page;

        public EditorDialogScope(EditorPage page)
        {
            _page = page;
            _page._editorDialogDepth++;
        }

        public void Dispose() => _page._editorDialogDepth--;
    }

    /// <summary>Builds and opens the shortcut help dialog.</summary>
    private async Task ShowEditorShortcutsDialogAsync()
    {
        RenderEditorShortcutsHelp();
        EditorShortcutsDialog.XamlRoot = XamlRoot;
        _editorDialogDepth++;
        try
        {
            await EditorShortcutsDialog.ShowAsync();
        }
        finally
        {
            _editorDialogDepth--;
        }
    }

    /// <summary>
    /// Builds the help dialog content from the registry: one heading per group,
    /// one row per binding with a key column and a description column.
    /// Unsupported bindings are clearly marked.
    /// </summary>
    private void RenderEditorShortcutsHelp()
    {
        EditorShortcutsList.Children.Clear();
        foreach (var group in _shortcutRegistry.Groups)
        {
            var bindings = _shortcutRegistry.InGroup(group);
            if (bindings.Count == 0) continue;

            var header = new TextBlock
            {
                Text = EditorShortcuts.GroupName(group),
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 0),
            };
            AutomationProperties.SetName(header, "Nhóm phím tắt: " + header.Text);
            EditorShortcutsList.Children.Add(header);

            foreach (var binding in bindings)
            {
                EditorShortcutsList.Children.Add(BuildEditorShortcutRow(binding));
            }
        }
    }

    /// <summary>Builds one shortcut row (key column + description column) for the dialog.</summary>
    /// <param name="binding">The binding to display.</param>
    private static Grid BuildEditorShortcutRow(EditorShortcutBinding binding)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var key = new TextBlock
        {
            Text = binding.Gesture,
            VerticalAlignment = VerticalAlignment.Top,
            FontFamily = new FontFamily("Cascadia Mono"),
        };
        AutomationProperties.SetName(key, "Phím: " + binding.Gesture);

        var description = new TextBlock
        {
            Text = binding.IsSupported
                ? binding.DisplayName
                : $"{binding.DisplayName} — {binding.UnsupportedNote}",
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top,
        };
        if (!binding.IsSupported) description.Foreground = EditorWarningBrush();
        AutomationProperties.SetName(
            description,
            binding.IsSupported
                ? binding.DisplayName
                : $"{binding.DisplayName}. Chưa hỗ trợ.");

        Grid.SetColumn(key, 0);
        Grid.SetColumn(description, 1);
        row.Children.Add(key);
        row.Children.Add(description);
        return row;
    }

    /// <summary>The app's warning color (for unsupported bindings).</summary>
    private static Brush EditorWarningBrush()
        => Application.Current?.Resources.TryGetValue("WarningBrush", out var brush) == true && brush is Brush found
            ? found
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 242, 182, 88));
}
