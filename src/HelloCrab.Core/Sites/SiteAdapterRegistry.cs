using HelloCrab.Core.Services.Localization;
using HelloCrab.Core.Models;

namespace HelloCrab.Core.Sites;

public sealed class SiteAdapterRegistry
{
    private readonly IReadOnlyDictionary<string, ISiteAdapter> _adapters;

    public SiteAdapterRegistry(IEnumerable<ISiteAdapter> adapters)
    {
        _adapters = adapters.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<PlatformOption> Platforms => _adapters.Values
        .Select(x => new PlatformOption(x.Id, x.DisplayName, x.HomeUrl))
        .OrderBy(x => x.DisplayName)
        .ToArray();

    public ISiteAdapter GetRequired(string id)
        => _adapters.TryGetValue(id, out var adapter)
            ? adapter
            : throw new InvalidOperationException(RuntimeLocalization.Format("Error.Site.NotRegistered", "未注册平台适配器：{0}", id));
}
