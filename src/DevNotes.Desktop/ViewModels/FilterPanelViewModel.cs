using System.Data.Common;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevNotes.Application.Search;
using DevNotes.Application.Vaults;
using DevNotes.Desktop.Resources;
using DevNotes.Desktop.Services;
using DevNotes.Domain.Common;
using DevNotes.Domain.Notes;

namespace DevNotes.Desktop.ViewModels;

public enum FacetKind
{
    Project,
    Tag,
    Type,
}

/// <summary>One value of the sidebar (a project, a tag or a type) with its number of notes.</summary>
public sealed partial class FacetItemViewModel(FacetKind kind, string value, string label, int count) : ObservableObject
{
    public FacetKind Kind { get; } = kind;

    /// <summary>Projects show their colour dot in front of the name.</summary>
    public bool IsProject => Kind == FacetKind.Project;

    /// <summary>The value as stored in the index (the project name, the tag, the type key).</summary>
    public string Value { get; } = value;

    public string Label { get; } = label;

    [ObservableProperty]
    public partial int Count { get; set; } = count;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public string AccessibleName => $"{Label}, {Count}";
}

/// <summary>
/// The projects, tags and types of the open vault with counts. Selecting them builds a
/// <see cref="NoteFilter"/> for the note list: projects and types are alternatives, tags must
/// all be present. Counts are for the whole vault, not for the current selection.
/// </summary>
public sealed partial class FilterPanelViewModel : ObservableObject
{
    private readonly INotificationService _notifications;
    private IVaultSession? _session;
    private int _refreshVersion;
    private Task _latestRefresh = Task.CompletedTask;
    private bool _rebuilding;

    public FilterPanelViewModel(INotificationService notifications)
    {
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        Projects = [];
        Tags = [];
        Types = [];
        Filter = NoteFilter.Empty;
        ActiveCountText = string.Empty;
    }

    /// <summary>Raised when the selection changed; the argument is the resulting filter.</summary>
    public event EventHandler<NoteFilter>? FilterChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProjects))]
    public partial IReadOnlyList<FacetItemViewModel> Projects { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTags))]
    public partial IReadOnlyList<FacetItemViewModel> Tags { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<FacetItemViewModel> Types { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveFilter))]
    public partial NoteFilter Filter { get; private set; }

    [ObservableProperty]
    public partial string ActiveCountText { get; private set; }

    public bool HasProjects => Projects.Count > 0;

    public bool HasTags => Tags.Count > 0;

    public bool HasActiveFilter => !Filter.IsEmpty;

    /// <summary>Names of the projects in use, for the active-project detection and the settings.</summary>
    public IReadOnlyList<string> ProjectNames => [.. Projects.Select(project => project.Value)];

    /// <summary>Switches to another vault (or none): the selection is dropped and the facets reloaded.</summary>
    public Task SetSessionAsync(IVaultSession? session)
    {
        _session = session;
        Apply([], [], []);
        return RefreshAsync();
    }

    /// <summary>
    /// Reloads the counts, keeping the selected values that still exist. Only the most recent
    /// refresh applies its result; see <see cref="WhenSettledAsync"/> to wait for the latest one.
    /// </summary>
    public Task RefreshAsync()
    {
        var refresh = RefreshCoreAsync();
        _latestRefresh = refresh;
        return refresh;
    }

    /// <summary>Completes when no refresh is in flight, i.e. the facets reflect the latest query.</summary>
    public async Task WhenSettledAsync()
    {
        Task current;
        do
        {
            current = _latestRefresh;
            await current;
        }
        while (!ReferenceEquals(current, _latestRefresh));
    }

    private async Task RefreshCoreAsync()
    {
        var version = ++_refreshVersion;
        if (_session is not { } session)
        {
            return;
        }

        NoteFacets facets;
        try
        {
            facets = await session.Queries.GetFacetsAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is DbException or ObjectDisposedException || ErrorMessages.IsExpected(exception))
        {
            if (version == _refreshVersion)
            {
                _notifications.Show(ErrorMessages.Describe(exception), NotificationKind.Error);
            }

            return;
        }

        if (version != _refreshVersion || !ReferenceEquals(session, _session))
        {
            return; // Superseded by a newer refresh or by another vault.
        }

        var selected = SelectedValues();
        Apply(
            Build(FacetKind.Project, facets.Projects, project => project, selected),
            Build(FacetKind.Tag, facets.Tags, tag => "#" + tag, selected),
            Build(FacetKind.Type, facets.Types, type => NoteTypeLabels.Get(NoteTypes.ParseOrDefault(type)), selected));
    }

    /// <summary>Selects exactly this project (case-insensitively), or clears the project selection when it is already the only one.</summary>
    public void ToggleProject(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var key = TextKey.Of(name);
        var target = Projects.FirstOrDefault(project => TextKey.Of(project.Value) == key);
        if (target is null)
        {
            return;
        }

        var alreadyOnlyOne = target.IsSelected && Projects.Count(project => project.IsSelected) == 1;
        _rebuilding = true;
        try
        {
            foreach (var project in Projects)
            {
                project.IsSelected = !alreadyOnlyOne && ReferenceEquals(project, target);
            }
        }
        finally
        {
            _rebuilding = false;
        }

        UpdateFilter();
    }

    [RelayCommand]
    private void Toggle(FacetItemViewModel? item)
    {
        item?.IsSelected = !item.IsSelected;
    }

    [RelayCommand]
    public void Clear()
    {
        _rebuilding = true;
        try
        {
            foreach (var item in Projects.Concat(Tags).Concat(Types))
            {
                item.IsSelected = false;
            }
        }
        finally
        {
            _rebuilding = false;
        }

        UpdateFilter();
    }

    private HashSet<(FacetKind, string)> SelectedValues() =>
        [.. Projects.Concat(Tags).Concat(Types).Where(item => item.IsSelected).Select(item => (item.Kind, item.Value))];

    private List<FacetItemViewModel> Build(
        FacetKind kind,
        IReadOnlyList<FacetCount> facets,
        Func<string, string> label,
        HashSet<(FacetKind, string)> selected)
    {
        var items = new List<FacetItemViewModel>(facets.Count);
        foreach (var facet in facets)
        {
            var item = new FacetItemViewModel(kind, facet.Value, label(facet.Value), facet.Count)
            {
                IsSelected = selected.Contains((kind, facet.Value)),
            };
            item.PropertyChanged += OnItemPropertyChanged;
            items.Add(item);
        }

        return items;
    }

    private void Apply(IReadOnlyList<FacetItemViewModel> projects, IReadOnlyList<FacetItemViewModel> tags, IReadOnlyList<FacetItemViewModel> types)
    {
        foreach (var item in Projects.Concat(Tags).Concat(Types))
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }

        Projects = projects;
        Tags = tags;
        Types = types;
        UpdateFilter();
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!_rebuilding && e.PropertyName == nameof(FacetItemViewModel.IsSelected))
        {
            UpdateFilter();
        }
    }

    private void UpdateFilter()
    {
        var tags = new List<Tag>();
        foreach (var item in Tags)
        {
            if (item.IsSelected && Tag.TryCreate(item.Value, out var tag))
            {
                tags.Add(tag);
            }
        }

        var filter = new NoteFilter
        {
            Projects = [.. Projects.Where(project => project.IsSelected).Select(project => project.Value)],
            Tags = tags,
            Types = [.. Types.Where(type => type.IsSelected).Select(type => NoteTypes.ParseOrDefault(type.Value))],
        };

        var count = filter.Projects.Count + filter.Tags.Count + filter.Types.Count;
        var changed = !SameSelection(Filter, filter);
        Filter = filter;
        ActiveCountText = count == 0 ? string.Empty : ErrorMessages.Format(Strings.Filter_ActiveCount, count);
        if (changed)
        {
            FilterChanged?.Invoke(this, filter);
        }
    }

    private static bool SameSelection(NoteFilter a, NoteFilter b) =>
        a.Projects.SequenceEqual(b.Projects, StringComparer.Ordinal)
        && a.Tags.SequenceEqual(b.Tags)
        && a.Types.SequenceEqual(b.Types);
}
