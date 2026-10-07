using CommunityToolkit.Mvvm.ComponentModel;
using NegiCraftLauncher.Core.Instances;
using NegiCraftLauncher.Core.Modrinth;
using NegiCraftLauncher.Raster;

namespace NegiCraftLauncher.ViewModels;

/// <summary>UI-thread resource browsing with reusable lists and bounded recent-search caching.</summary>
public partial class ResourceBrowserViewModel : ViewModelBase
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
    private readonly ModrinthClient _client;
    private readonly TimeProvider _clock;
    private readonly int _cacheCapacity;
    private readonly Dictionary<SearchKey, CachedPage> _cache = new();
    private readonly Dictionary<string, PixelBuffer> _icons = new();
    private CancellationTokenSource? _load;
    private InstanceContext? _instance;
    private IReadOnlyList<ResourceModel> _gameResources = Array.Empty<ResourceModel>();
    private IReadOnlyList<ResourceModel> _filteredGames = Array.Empty<ResourceModel>();
    private string? _gameQuery;
    private string? _gameError;
    private bool _gamesLoaded;
    private long _cacheAccess;

    public ResourceBrowserViewModel(ModrinthClient? client = null, TimeProvider? clock = null,
        int cacheCapacity = 24)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cacheCapacity);
        _client = client ?? new ModrinthClient();
        _clock = clock ?? TimeProvider.System;
        _cacheCapacity = cacheCapacity;
    }

    [ObservableProperty]
    private string _currentResCategory = "game";

    [ObservableProperty]
    private string _resourceSearch = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResources))]
    private IReadOnlyList<ResourceModel> _filteredResources = Array.Empty<ResourceModel>();

    [ObservableProperty]
    private bool _isLoadingResources = true;

    [ObservableProperty]
    private string _resourceEmptyText = "正在加载版本列表…";

    public bool HasResources => FilteredResources.Count > 0;

    partial void OnCurrentResCategoryChanged(string value) => UpdateResources(debounce: false);
    partial void OnResourceSearchChanged(string value) => UpdateResources(debounce: true);

    public void SetInstance(Instance? instance)
    {
        var context = instance is null
            ? null
            : new InstanceContext(instance.Id, instance.VersionId, LoaderFacet(instance.Loader));
        if (_instance == context) return;

        _instance = context;
        UpdateResources(debounce: false);
    }

    public void SetGameVersions(IReadOnlyList<ResourceModel> resources)
    {
        _gameResources = resources;
        _gamesLoaded = true;
        _gameError = null;
        _gameQuery = null;
        if (CurrentResCategory == "game") UpdateResources(debounce: false);
    }

    public void SetGameVersionError(string message)
    {
        _gamesLoaded = true;
        _gameError = $"版本列表加载失败：{message}";
        if (CurrentResCategory == "game") UpdateResources(debounce: false);
    }

    private void UpdateResources(bool debounce)
    {
        _load?.Cancel();
        _load = null;
        var query = ResourceSearch.Trim();

        if (CurrentResCategory == "game")
        {
            if (!string.Equals(_gameQuery, query, StringComparison.OrdinalIgnoreCase))
            {
                _filteredGames = query.Length == 0
                    ? _gameResources
                    : _gameResources.Where(r => r.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
                _gameQuery = query;
            }

            Publish(_filteredGames, _gameError ??
                (_gamesLoaded ? "没有匹配的版本" : "正在加载版本列表…"), !_gamesLoaded);
            return;
        }

        var projectType = CurrentResCategory switch
        {
            "mod" => ModrinthClient.ProjectTypeMod,
            "rp" => ModrinthClient.ProjectTypeResourcePack,
            "shader" => ModrinthClient.ProjectTypeShader,
            _ => null,
        };

        if (projectType is null)
        {
            Publish(Array.Empty<ResourceModel>(), "整合包安装即将支持。", false);
            return;
        }

        if (_instance is not { } instance)
        {
            Publish(Array.Empty<ResourceModel>(), "先选择一个实例，才能按它的版本和加载器筛选。", false);
            return;
        }

        // Keep page/action state separate for each installation, not just each game version.
        var key = new SearchKey(CurrentResCategory, query, instance.Id, instance.Version,
            CurrentResCategory == "mod" ? instance.Loader : null);
        if (_cache.TryGetValue(key, out var page))
        {
            if (page.ExpiresAt > _clock.GetUtcNow())
            {
                page.LastAccess = ++_cacheAccess;
                Publish(page.Rows, EmptyText(query), false);
                return;
            }
            _cache.Remove(key);
        }

        Publish(Array.Empty<ResourceModel>(), "正在搜索…", true);
        var request = _load = new CancellationTokenSource();
        _ = LoadAsync(key, projectType, debounce, request);
    }

    private async Task LoadAsync(SearchKey key, string projectType, bool debounce,
        CancellationTokenSource request)
    {
        var token = request.Token;
        try
        {
            // Only typing is debounced; choosing a different category should respond immediately.
            if (debounce) await Task.Delay(350, token).ConfigureAwait(true);
            var hits = await _client.SearchAsync(projectType, key.Query, 30, 0,
                key.Version, key.Loader, token).ConfigureAwait(true);
            if (!IsCurrent(request)) return;

            if (!_icons.TryGetValue(key.Category, out var icon))
            {
                icon = PixelArt.CreateBlock(key.Category switch
                {
                    "mod" => "dirt",
                    "shader" => "log",
                    _ => "stone",
                });
                _icons.Add(key.Category, icon);
            }

            var rows = hits.Select(hit => new ResourceModel
            {
                Name = hit.Title,
                Desc = hit.Description,
                Stat = FormatCount(hit.Downloads),
                Category = key.Category,
                ProjectId = hit.Id,
                IconBitmap = icon,
            }).ToArray();

            if (_cache.Count >= _cacheCapacity)
                _cache.Remove(_cache.MinBy(entry => entry.Value.LastAccess).Key);
            _cache[key] = new CachedPage(rows, _clock.GetUtcNow() + CacheLifetime, ++_cacheAccess);
            Publish(rows, EmptyText(key.Query), false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsCurrent(request)) Publish(Array.Empty<ResourceModel>(), $"加载失败：{ex.Message}", false);
        }
        finally
        {
            if (ReferenceEquals(_load, request))
            {
                _load = null;
                IsLoadingResources = false;
            }
            request.Dispose();
        }
    }

    private bool IsCurrent(CancellationTokenSource request) =>
        ReferenceEquals(_load, request) && !request.IsCancellationRequested;

    private void Publish(IReadOnlyList<ResourceModel> rows, string emptyText, bool loading)
    {
        FilteredResources = rows;
        ResourceEmptyText = emptyText;
        IsLoadingResources = loading;
    }

    private static string EmptyText(string query) =>
        query.Length == 0 ? "没有找到内容" : $"没有和「{query}」匹配的内容";

    private static string? LoaderFacet(string loader) => loader switch
    {
        "Fabric" => "fabric",
        "Quilt" => "quilt",
        "Forge" => "forge",
        "NeoForge" => "neoforge",
        _ => null,
    };

    private static string FormatCount(int downloads) => downloads switch
    {
        >= 100_000_000 => $"{downloads / 100_000_000.0:0.#}亿",
        >= 10_000 => $"{downloads / 10_000.0:0.#}万",
        _ => downloads.ToString(),
    };

    private sealed record InstanceContext(string Id, string Version, string? Loader);
    private readonly record struct SearchKey(string Category, string Query, string InstanceId,
        string Version, string? Loader);
    private sealed class CachedPage(IReadOnlyList<ResourceModel> rows, DateTimeOffset expiresAt, long access)
    {
        public IReadOnlyList<ResourceModel> Rows { get; } = rows;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public long LastAccess { get; set; } = access;
    }
}
