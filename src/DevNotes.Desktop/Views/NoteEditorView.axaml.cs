using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using AvaloniaEdit.TextMate;
using DevNotes.Application.Settings;
using DevNotes.Desktop.ViewModels;
using TextMateSharp.Grammars;

namespace DevNotes.Desktop.Views;

/// <summary>
/// Hosts the text editor and the preview. The code-behind only contains view concerns: keeping
/// the editor control and <see cref="NoteEditorViewModel.Text"/> in sync, syntax highlighting,
/// pane layout, scrolling and focus.
/// </summary>
public sealed partial class NoteEditorView : UserControl
{
    private const string MarkdownLanguageId = "markdown";

    private NoteEditorViewModel? _viewModel;
    private RegistryOptions? _registryOptions;
    private TextMate.Installation? _textMate;
    private string _editorText = string.Empty;
    private int _documentVersion = -1;
    private bool _synchronizing;

    public NoteEditorView()
    {
        InitializeComponent();

        var options = Editor.Options;
        options.ConvertTabsToSpaces = true;
        options.IndentationSize = 4;
        options.AllowScrollBelowDocument = true;

        // Links in notes are untrusted: they are only opened from the preview, through the link policy.
        options.EnableHyperlinks = false;
        options.EnableEmailHyperlinks = false;

        Editor.TextChanged += OnEditorTextChanged;
    }

    /// <summary>The underlying editor control (exposed for UI tests).</summary>
    internal AvaloniaEdit.TextEditor TextEditor => Editor;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Detach();

        _viewModel = DataContext as NoteEditorViewModel;
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.NavigateRequested += OnNavigateRequested;
        _viewModel.FocusRequested += OnFocusRequested;

        PushTextToEditor();
        Editor.IsReadOnly = _viewModel.IsReadOnly;
        ApplyPaneLayout();
        ApplyHighlightingTheme();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_textMate is null)
        {
            _registryOptions = new RegistryOptions(CurrentTheme);
            _textMate = Editor.InstallTextMate(_registryOptions);
            _textMate.SetGrammar(_registryOptions.GetScopeByLanguageId(MarkdownLanguageId));
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _textMate?.Dispose();
        _textMate = null;
        _registryOptions = null;
    }

    private ThemeName CurrentTheme => _viewModel?.IsDarkTheme == false ? ThemeName.LightPlus : ThemeName.DarkPlus;

    private void Detach()
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.NavigateRequested -= OnNavigateRequested;
            _viewModel.FocusRequested -= OnFocusRequested;
            _viewModel = null;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(NoteEditorViewModel.Text) when _viewModel is { IsReplacingDocument: false }:
            case nameof(NoteEditorViewModel.DocumentVersion):
                PushTextToEditor();
                break;
            case nameof(NoteEditorViewModel.ReloadVersion):
                // The text now comes from another version of the file: older undo steps no longer apply.
                Editor.Document.UndoStack.ClearAll();
                break;
            case nameof(NoteEditorViewModel.IsReadOnly):
                Editor.IsReadOnly = _viewModel?.IsReadOnly ?? false;
                break;
            case nameof(NoteEditorViewModel.ViewMode):
                ApplyPaneLayout();
                break;
            case nameof(NoteEditorViewModel.IsDarkTheme):
                ApplyHighlightingTheme();
                break;
            default:
                break;
        }
    }

    // Editor → view model.
    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_synchronizing || _viewModel is null)
        {
            return;
        }

        _editorText = Editor.Text;
        _synchronizing = true;
        try
        {
            _viewModel.Text = _editorText;
        }
        finally
        {
            _synchronizing = false;
        }
    }

    // View model → editor.
    private void PushTextToEditor()
    {
        if (_synchronizing || _viewModel is null)
        {
            return;
        }

        var text = _viewModel.Text;
        var isNewDocument = _documentVersion != _viewModel.DocumentVersion;
        if (!isNewDocument && string.Equals(text, _editorText, StringComparison.Ordinal))
        {
            return;
        }

        _synchronizing = true;
        try
        {
            if (isNewDocument)
            {
                _documentVersion = _viewModel.DocumentVersion;
                Editor.Document.Text = text;
                Editor.Document.UndoStack.ClearAll();
                Editor.CaretOffset = 0;
                Editor.ScrollToHome();
                PreviewScroller.ScrollToHome();
            }
            else
            {
                ReplaceChangedRange(_editorText, text);
            }

            _editorText = text;
        }
        finally
        {
            _synchronizing = false;
        }
    }

    /// <summary>
    /// Replaces only the part of the document that differs (common prefix and suffix are kept), so
    /// the caret, the selection and the scroll position survive programmatic updates such as the
    /// frontmatter stamp applied on save or a reload from disk.
    /// </summary>
    private void ReplaceChangedRange(string current, string updated)
    {
        var prefix = 0;
        var maxPrefix = Math.Min(current.Length, updated.Length);
        while (prefix < maxPrefix && current[prefix] == updated[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        var maxSuffix = maxPrefix - prefix;
        while (suffix < maxSuffix && current[^(suffix + 1)] == updated[^(suffix + 1)])
        {
            suffix++;
        }

        Editor.Document.Replace(prefix, current.Length - prefix - suffix, updated[prefix..^suffix]);
    }

    private void ApplyPaneLayout()
    {
        var mode = _viewModel?.ViewMode ?? EditorViewMode.Split;
        var columns = Panes.ColumnDefinitions;
        columns[0].Width = mode == EditorViewMode.Preview ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        columns[1].Width = mode == EditorViewMode.Split ? GridLength.Auto : new GridLength(0);
        columns[2].Width = mode == EditorViewMode.Editor ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
    }

    private void ApplyHighlightingTheme()
    {
        if (_textMate is not null && _registryOptions is not null)
        {
            _textMate.SetTheme(_registryOptions.LoadTheme(CurrentTheme));
        }
    }

    private void OnNavigateRequested(object? sender, int line)
    {
        var document = Editor.Document;
        if (document.LineCount == 0)
        {
            return;
        }

        var target = document.GetLineByNumber(Math.Clamp(line + 1, 1, document.LineCount));
        Editor.CaretOffset = target.Offset;
        Editor.ScrollTo(target.LineNumber, 0);
        Editor.TextArea.Focus();
    }

    // The request often arrives in the same tick that makes this view visible (first note opened,
    // note just created): focus is applied once layout has attached the text area.
    private void OnFocusRequested(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => Editor.TextArea.Focus(), DispatcherPriority.Loaded);
}
