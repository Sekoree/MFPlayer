using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using HaCue2.Engine;
using HaCue2.Session;
using HaCue2.Presentation;
using HaCue2.ViewModels;

namespace HaCue2.Views;

public partial class ShellWindow : Window
{
    private DiagnosticsWindow? _diagnostics;
    private SettingsWindow? _settings;
    private ProjectStatusWindow? _projectStatus;
    private OutputInfoWindow? _outputInfo;
    private readonly EmergencyKeySequence _emergencyKeys = new();
    private S.Media.Source.YouTube.YouTubePreparationQueue? _youTubeDownloads;

    public ShellWindow() => InitializeComponent();

    /// <summary>
    /// Hands the view-model the one thing it cannot do itself: open another project.
    /// </summary>
    /// <remarks>
    /// The File menu's recent list is bound to a command on the shell, and what that command DOES is
    /// close this window and open another — a decision about windows, which belongs here.
    /// </remarks>
    protected override void OnDataContextChanged(EventArgs e)
    {
        DetachYouTubeDownloads();
        base.OnDataContextChanged(e);

        if (DataContext is ShellViewModel shell)
        {
            shell.OpenRecent = OnOpenRecent;
            _youTubeDownloads = YouTubeRuntime.Downloads;
            _youTubeDownloads.Changed += OnYouTubeDownloadChanged;
            _youTubeDownloads.ReadinessChanged += OnYouTubeReadinessChanged;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        DetachYouTubeDownloads();
        base.OnClosed(e);
    }

    private void OnYouTubeDownloadChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (DataContext is ShellViewModel shell)
            shell.RefreshYouTubeDownloadProgress();
    });

    private void OnYouTubeReadinessChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (DataContext is ShellViewModel shell)
            shell.RefreshYouTubeReadiness();
    });

    private void DetachYouTubeDownloads()
    {
        if (_youTubeDownloads is null)
            return;
        _youTubeDownloads.Changed -= OnYouTubeDownloadChanged;
        _youTubeDownloads.ReadinessChanged -= OnYouTubeReadinessChanged;
        _youTubeDownloads = null;
    }

    /// <summary>
    /// The shell's view-model.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so an auxiliary window can steer the main one: the Project status
    /// window's FIX buttons switch the shell to the screen that owns the failing thing, and its only
    /// handle on the shell is <see cref="Window.Owner"/>.
    /// </remarks>
    internal ShellViewModel Shell =>
        DataContext as ShellViewModel ?? throw new InvalidOperationException("The shell has no view-model.");

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// F9 summons the Output info drawer (register item 4). It is a window-level key because the
    /// drawer is shell chrome, not part of whichever view happens to be focused.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (DataContext is not ShellViewModel shell)
        {
            base.OnKeyDown(e);
            return;
        }

        // Undo is a WINDOW-level key with ONE journal behind it, across every view. That is the whole
        // point of a single journal: ⌘Z means the same thing whether the last edit was a cue label or
        // a patch cell, and the toast says which surface it changed so an undo cannot silently alter a
        // screen the operator is not looking at.
        var gesture = KeyboardGestureText.Format(e.Key, e.KeyModifiers);

        // Learn owns the next gesture, including a normally reserved transport key. Nothing fires
        // while the operator is deliberately teaching the project what that key is.
        if (shell.Targets.CaptureKeyboardGesture(gesture))
        {
            e.Handled = true;
            return;
        }

        // Esc is a cue-software safety convention, deliberately outside editable profiles: one press
        // stops, a second within 700 ms panics. A settings typo must never make emergency stop vanish.
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            if (_emergencyKeys.Press() == EscapeAction.Panic)
                shell.Cues.Panic();
            else
                shell.Cues.Stop();
            e.Handled = true;
            return;
        }

        var typing = IsTyping();
        if (AppHotkeys.Matches(shell.Settings, AppHotkeys.Go, gesture)
            && (gesture == "Space" ? shell.AllowsSpaceGo(typing) : !typing))
            shell.Cues.Go();
        else if (AppHotkeys.Matches(shell.Settings, AppHotkeys.StandbyUp, gesture) && !typing)
            shell.Cues.StepStandby(-1);
        else if (AppHotkeys.Matches(shell.Settings, AppHotkeys.StandbyDown, gesture) && !typing)
            shell.Cues.StepStandby(1);
        else if (AppHotkeys.Matches(shell.Settings, AppHotkeys.Preview, gesture))
            shell.Cues.PreviewSelected();
        else if (AppHotkeys.Matches(shell.Settings, AppHotkeys.StopPreview, gesture))
            shell.Cues.StopPreview();
        else if (AppHotkeys.Matches(shell.Settings, AppHotkeys.OutputInfo, gesture))
            shell.IsOutputInfoOpen = !shell.IsOutputInfoOpen;
        else if (AppHotkeys.Matches(shell.Settings, AppHotkeys.Undo, gesture))
            shell.Undo();
        else if (AppHotkeys.Matches(shell.Settings, AppHotkeys.Redo, gesture))
            shell.Redo();
        else if (AppHotkeys.Matches(shell.Settings, AppHotkeys.Save, gesture))
            _ = SaveAsync(shell);
        else if (AppHotkeys.Matches(shell.Settings, AppHotkeys.SaveAs, gesture))
            _ = SaveAsAsync(shell);
        else if (AppHotkeys.Matches(shell.Settings, AppHotkeys.NewProject, gesture))
            _ = NewProjectAsync();
        else if (AppHotkeys.Matches(shell.Settings, AppHotkeys.OpenProject, gesture))
            _ = OpenProjectAsync();
        else
            goto projectTriggers;

        e.Handled = true;
        return;

    projectTriggers:
        if (shell.Host?.FeedKeyboard(gesture, IsTyping()) == true)
        {
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>
    /// Whether the focus is in something that eats keys.
    /// </summary>
    /// <remarks>
    /// Space in a cue LABEL has to be a space. This is the one place a transport key gives way, and it
    /// gives way to typing rather than to focus being "somewhere else" — every other control in the
    /// app can have focus and Space still fires the show.
    /// </remarks>
    private bool IsTyping() => FocusManager?.GetFocusedElement() is TextBox;

    /// <summary>
    /// Saves, asking where only when there is nowhere yet.
    /// </summary>
    /// <remarks>
    /// The fallback matters: a Ctrl+S on a project that has never been saved must open Save As rather
    /// than appearing to work and writing nothing, which is the worst outcome this code can produce.
    /// </remarks>
    private async Task SaveAsync(ShellViewModel shell)
    {
        if (!await shell.SaveAsync())
            await SaveAsAsync(shell);
    }

    private Task SaveAsAsync(ShellViewModel shell) => SaveAsAsync(shell, "Save project");

    private async Task SaveAsAsync(ShellViewModel shell, string title)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = shell.Project.Title,
            DefaultExtension = ProjectFiles.Extension.TrimStart('.'),
            FileTypeChoices =
            [
                new FilePickerFileType("HaCue2 project") { Patterns = [$"*{ProjectFiles.Extension}"] },
            ],
        });

        if (file?.TryGetLocalPath() is { } path)
            await shell.SaveToAsync(path);
    }

    /// <summary>
    /// Asks a brand-new project where it lives, as soon as it exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A show used to be created with nowhere to go and only find out at the first Ctrl+S. Everything
    /// that keys off the project's own folder is wrong until then: media paths are stored relative to
    /// it, the relink search starts from it, the autosave that protects a crash is written beside it,
    /// and the recents list cannot offer a file that has none. An hour of authoring could sit in a
    /// document that had never touched a disk.
    /// </para>
    /// <para>
    /// A cancelled picker is not a refusal to create the project — it is already open and its edits are
    /// real. It goes back to being a project with nowhere to go, and the status line says so rather
    /// than letting that be discovered later.
    /// </para>
    /// </remarks>
    internal async Task AskWhereToSaveAsync()
    {
        await SaveAsAsync(Shell, "Where should this show live?");

        if (!Shell.HasPath)
            Shell.FileMessage =
                "not saved anywhere yet — media paths stay absolute and there is no autosave until it is";
    }

    // ── the File menu ─────────────────────────────────────────────────────────────────────────
    // Every verb here is also a hotkey, and that is the point of having them written down: a save
    // that only ever happened via Ctrl+S left an operator with nothing to look at and no way to find
    // out where the project lived.

    private void OnFileSave(object? sender, RoutedEventArgs e) => _ = SaveAsync(Shell);

    private void OnFileSaveAs(object? sender, RoutedEventArgs e) => _ = SaveAsAsync(Shell);

    private async void OnFileNew(object? sender, RoutedEventArgs e) => await NewProjectAsync();

    private Task NewProjectAsync() =>
        LeaveThenAsync(() =>
        {
            // The launcher's own New-project prompt, reused rather than reimplemented: what a new
            // project is seeded with is one decision and it already lives there.
            var launcher = new LauncherViewModel(Shell.Settings, App.Machine);

            launcher.ProjectOpened += (project, _) =>
            {
                // Always the NEW route from here: this menu item only ever produces a freshly created
                // project, and it has to be asked where it lives like any other.
                App.ShowNewProject(project);
                Close();
            };

            PromptWindow.Show(this, launcher.NewProject());
            return Task.CompletedTask;
        });

    private async void OnFileOpen(object? sender, RoutedEventArgs e) => await OpenProjectAsync();

    private Task OpenProjectAsync() =>
        LeaveThenAsync(async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open project",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("HaCue2 project") { Patterns = [$"*{ProjectFiles.Extension}"] },
                ],
            });

            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
                await SwitchToAsync(path);
        });

    /// <summary>
    /// A second INSTANCE, not a second window.
    /// </summary>
    /// <remarks>
    /// Two shells in one process would each open the audio backend and the same devices, and the
    /// second would find them taken. A process of its own also means a crash in one show cannot take
    /// the other down, which is the whole reason somebody runs two.
    /// </remarks>
    private void OnFileNewWindow(object? sender, RoutedEventArgs e)
    {
        try
        {
            var executable = Environment.ProcessPath;

            if (executable is null)
            {
                Shell.FileMessage = "could not find this application's own executable";
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(executable)
            {
                UseShellExecute = false,
            });
        }
        catch (Exception failure) when (
            failure is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Shell.FileMessage = $"a second window could not be started — {failure.Message}";
        }
    }

    private async void OnFileClose(object? sender, RoutedEventArgs e) =>
        await LeaveThenAsync(() =>
        {
            App.ShowLauncher();
            Close();
            return Task.CompletedTask;
        });

    /// <summary>Opens a project the File menu's recent list names.</summary>
    private async void OnOpenRecent(string path) => await LeaveThenAsync(() => SwitchToAsync(path));

    /// <summary>
    /// Asks about unsaved work, then does the thing that would discard it.
    /// </summary>
    /// <remarks>
    /// Three answers, because the question has three: save it, throw it away, or go back to editing.
    /// Cancelling runs nothing at all — which is what makes this safe to attach to every verb that
    /// leaves the current project.
    /// </remarks>
    private async Task LeaveThenAsync(Func<Task> leave)
    {
        if (!Shell.Journal.IsDirty)
        {
            await leave();
            return;
        }

        var proceed = new TaskCompletionSource();

        PromptWindow.Show(
            this,
            new PromptViewModel(
                $"“{Shell.ProjectFile}” has unsaved edits",
                $"{Shell.UnsavedSummary} · {Shell.ProjectLocation}",
                [],
                // Named rather than discarded: `_` is already the lambda's parameter, and assigning
                // the task to it would be assigning a Task to a PromptViewModel.
                prompt => SaveThen(prompt, proceed, leave),
                confirm: "SAVE FIRST",
                alternative: "DISCARD",
                applyAlternative: () => _ = leave()),
            afterClose: () => proceed.TrySetResult());

        await proceed.Task;
    }

    private void SaveThen(PromptViewModel prompt, TaskCompletionSource done, Func<Task> leave)
    {
        _ = prompt;
        _ = SaveThenAsync(done, leave);
    }

    private async Task SaveThenAsync(TaskCompletionSource done, Func<Task> leave)
    {
        try
        {
            await SaveAsync(Shell);

            // Only when the save actually landed. A cancelled Save As must not then throw the work
            // away — the operator answered "save first", not "leave anyway".
            if (!Shell.Journal.IsDirty)
                await leave();
        }
        finally
        {
            done.TrySetResult();
        }
    }

    /// <summary>Opens another project in its own window and closes this one.</summary>
    /// <remarks>
    /// A new window rather than swapping the document under the running view-models: the shell owns a
    /// session, a journal and a dozen projections built around one project, and rebuilding them in
    /// place is a great many chances to leave one pointing at the old one. This is the hand-off the
    /// launcher already performs.
    /// </remarks>
    private async Task SwitchToAsync(string path)
    {
        var (project, result) = await ProjectFiles.OpenAsync(path);

        if (project is null)
        {
            Shell.FileMessage = result.Message;
            return;
        }

        App.ShowProject(project, result.Path);
        Close();
    }

    private void OnHideDrawer(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ShellViewModel shell)
            shell.IsOutputInfoOpen = false;
    }

    private void OnProgramMeterPressed(object? sender, PointerPressedEventArgs e)
    {
        Shell.ResetMeterClips();
        e.Handled = true;
    }

    /// <summary>Pop the drawer out for a second monitor, and close the in-shell copy behind it —
    /// two live copies of the same meters is a way to misread one of them.</summary>
    private void OnPopOutDrawer(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel shell)
            return;

        shell.IsOutputInfoOpen = false;
        _outputInfo = Reopen(_outputInfo, () => new OutputInfoWindow { DataContext = shell.OutputInfo });
    }

    /// <summary>
    /// Opens Settings with the journal behind it.
    /// </summary>
    /// <remarks>
    /// The journal is what makes the project half of this screen real: without it every project-scope
    /// setting is read once and written nowhere, so the pane describes the document rather than editing
    /// it (register items 26 and 28 — project settings are journaled and travel in the file).
    /// </remarks>
    private void OnSettings(object? sender, RoutedEventArgs e)
        => _settings = Reopen(
            _settings,
            () =>
            {
                var settings = new SettingsViewModel(
                    Shell.Project,
                    Shell.Journal,
                    Shell.Settings,
                    Shell.ApplyApplicationSettings)
                {
                    // The one settings pane whose content is a whole window of its own.
                    OpenProjectStatus = () => OnProjectStatus(this, new RoutedEventArgs()),
                };

                return new SettingsWindow { DataContext = settings };
            });

    private void OnDiagnostics(object? sender, RoutedEventArgs e)
        => _diagnostics = Reopen(_diagnostics, () => new DiagnosticsWindow { DataContext = Shell.OpenDiagnostics() });

    private void OnProjectStatus(object? sender, RoutedEventArgs e)
        => _projectStatus = Reopen(
            _projectStatus,
            () => new ProjectStatusWindow
            {
                DataContext = new ProjectStatusViewModel(
                    Shell.Project,
                    Shell.Environment,
                    Shell.Journal,
                    Shell.HasPath ? Shell.Path : null,
                    YouTubeRuntime.Downloads),
            });

    /// <summary>
    /// Bring an already-open auxiliary window forward instead of stacking a second copy. Diagnostics
    /// in particular is meant to sit on a second monitor for a whole show, so pressing its button
    /// again must find it, not clone it.
    /// </summary>
    private T Reopen<T>(T? existing, Func<T> create) where T : Window
    {
        if (existing is { } window && window.IsVisible)
        {
            window.Activate();
            return window;
        }

        var created = create();
        created.Show(this);
        return created;
    }
}
