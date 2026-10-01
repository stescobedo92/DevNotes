using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevNotes.Application.Notes;
using DevNotes.Desktop.Resources;
using DevNotes.Desktop.Services;
using DevNotes.Domain.Notes;

namespace DevNotes.Desktop.ViewModels.Dialogs;

/// <summary>A template as offered in the "new note" dialog.</summary>
public sealed record TemplateOption(string Key, string Label, NoteType Type, string Text)
{
    public static TemplateOption From(NoteTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return new TemplateOption(template.Key, TemplateLabels.Get(template.Key), template.Type, template.Text);
    }
}

public static class TemplateLabels
{
    /// <summary>Localized name of a built-in template; a template of the user is shown by its key.</summary>
    public static string Get(string key) =>
        Strings.ResourceManager.GetString("Template_" + key, Strings.Culture) ?? key;
}

/// <summary>What the user asked for: a title, a template and an optional project.</summary>
public sealed record NewNoteChoice(string Title, TemplateOption Template, string? Project);

/// <summary>Title, template and project of a new note.</summary>
public sealed partial class NewNoteDialogViewModel : DialogViewModel
{
    /// <param name="templates">Templates to choose from (never empty).</param>
    /// <param name="project">Project prefilled (the active project or the selected filter), or null.</param>
    /// <param name="preferredTemplateKey">Template selected initially when it exists (the last one used).</param>
    public NewNoteDialogViewModel(IReadOnlyList<TemplateOption> templates, string? project, string? preferredTemplateKey)
        : base(Strings.Dialog_NewNote_Title)
    {
        ArgumentNullException.ThrowIfNull(templates);
        if (templates.Count == 0)
        {
            throw new ArgumentException("At least one template is required.", nameof(templates));
        }

        Templates = templates;
        SelectedTemplate = templates.FirstOrDefault(template => template.Key == preferredTemplateKey) ?? templates[0];
        TitleInput = string.Empty;
        Project = project ?? string.Empty;
    }

    public IReadOnlyList<TemplateOption> Templates { get; }

    [ObservableProperty]
    public partial TemplateOption SelectedTemplate { get; set; }

    /// <summary>The title typed for the note (the dialog's own title is <see cref="DialogViewModel.Title"/>).</summary>
    [ObservableProperty]
    public partial string TitleInput { get; set; }

    [ObservableProperty]
    public partial string Project { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>The accepted choice, or null when the dialog was cancelled.</summary>
    public NewNoteChoice? Result { get; private set; }

    partial void OnTitleInputChanged(string value) => Error = null;

    [RelayCommand]
    private void Confirm()
    {
        if (!NoteTitle.TryNormalize(TitleInput, out var title))
        {
            Error = ErrorMessages.Format(Strings.Error_TitleRequired, NoteTitle.MaxLength);
            return;
        }

        var project = NoteTitle.TryNormalize(Project, out var normalizedProject) ? normalizedProject : null;
        Result = new NewNoteChoice(title, SelectedTemplate, project);
        Close();
    }
}
