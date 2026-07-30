using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace HelloCrab.Core.Services.Localization;

public sealed class LocalizationService : ObservableObject
{
    private const string DefaultLanguageCode = "zh-CN";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        WriteIndented = true
    };

    private readonly Dictionary<string, LanguagePack> _packs =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _appliedResourceKeys = new(StringComparer.Ordinal);
    private string _currentLanguageCode = DefaultLanguageCode;

    public LocalizationService()
    {
        Current = this;
        LanguageDirectory = ResolveLanguageDirectory();
        EnsureBundledPacksOnDisk();
        Reload();
    }

    public static LocalizationService? Current { get; private set; }

    public string LanguageDirectory { get; }

    public IReadOnlyList<LanguageOption> Languages { get; private set; } = Array.Empty<LanguageOption>();

    public string CurrentLanguageCode
    {
        get => _currentLanguageCode;
        private set => SetProperty(ref _currentLanguageCode, value);
    }

    public event EventHandler? LanguageChanged;

    public void Reload()
    {
        _packs.Clear();

        try
        {
            if (Directory.Exists(LanguageDirectory))
            {
                foreach (var path in Directory.EnumerateFiles(LanguageDirectory, "*.json")
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    TryLoadPack(File.ReadAllText(path), path);
                }
            }
        }
        catch
        {
            // 目录损坏或单个语言包不可读时，继续使用嵌入式语言包。
        }

        LoadEmbeddedPacks();

        Languages = _packs.Values
            .OrderBy(pack => pack.SortOrder)
            .ThenBy(pack => pack.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(pack => new LanguageOption(pack.Code, pack.DisplayName))
            .ToArray();
        OnPropertyChanged(nameof(Languages));

        if (!_packs.ContainsKey(CurrentLanguageCode))
            CurrentLanguageCode = _packs.ContainsKey(DefaultLanguageCode)
                ? DefaultLanguageCode
                : _packs.Keys.FirstOrDefault() ?? DefaultLanguageCode;

        Apply(CurrentLanguageCode);
    }

    public bool Apply(string? code)
    {
        var requested = string.IsNullOrWhiteSpace(code) ? DefaultLanguageCode : code.Trim();
        if (!_packs.TryGetValue(requested, out var selected))
        {
            selected = _packs.GetValueOrDefault(DefaultLanguageCode)
                       ?? _packs.Values.FirstOrDefault();
        }

        if (selected is null)
            return false;

        CurrentLanguageCode = selected.Code;
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_packs.TryGetValue(DefaultLanguageCode, out var fallback))
        {
            foreach (var pair in fallback.Strings)
                strings[pair.Key] = pair.Value;
        }
        foreach (var pair in selected.Strings)
            strings[pair.Key] = pair.Value;

        // 该提示已从界面设计中废弃。即使 exe 旁仍保留旧版语言 JSON，
        // 或用户切换到自定义语言包，也不允许旧文案再次显示。
        strings["History.Hint"] = string.Empty;

        if (Application.Current is { } app)
        {
            foreach (var oldKey in _appliedResourceKeys)
                app.Resources.Remove(oldKey);
            _appliedResourceKeys.Clear();

            foreach (var pair in strings)
            {
                var resourceKey = "Lang." + pair.Key;
                app.Resources[resourceKey] = pair.Value;
                _appliedResourceKeys.Add(resourceKey);
            }
        }

        LanguageChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public string Get(string key, string? fallback = null)
    {
        if (key.Equals("History.Hint", StringComparison.Ordinal))
            return string.Empty;

        if (_packs.TryGetValue(CurrentLanguageCode, out var selected)
            && selected.Strings.TryGetValue(key, out var value))
        {
            return value;
        }

        if (_packs.TryGetValue(DefaultLanguageCode, out var defaultPack)
            && defaultPack.Strings.TryGetValue(key, out value))
        {
            return value;
        }

        return fallback ?? key;
    }

    public string Format(string key, params object?[] arguments)
    {
        var template = Get(key);
        try
        {
            return string.Format(template, arguments);
        }
        catch (FormatException)
        {
            // 客户自定义语言包中的占位符写错时，不让界面或采集任务崩溃。
            if (_packs.TryGetValue(DefaultLanguageCode, out var fallback)
                && fallback.Strings.TryGetValue(key, out var fallbackTemplate))
            {
                try
                {
                    return string.Format(fallbackTemplate, arguments);
                }
                catch (FormatException)
                {
                }
            }

            return template;
        }
    }

    private void TryLoadPack(string json, string source)
    {
        try
        {
            var pack = JsonSerializer.Deserialize<LanguagePack>(json, JsonOptions);
            if (pack is null
                || string.IsNullOrWhiteSpace(pack.Code)
                || string.IsNullOrWhiteSpace(pack.DisplayName)
                || pack.Strings is null
                || pack.Strings.Count == 0)
            {
                return;
            }

            pack.Code = pack.Code.Trim();
            pack.DisplayName = pack.DisplayName.Trim();
            pack.Source = source;
            _packs[pack.Code] = pack;
        }
        catch
        {
            // 客户自定义语言包有语法错误时忽略该文件，不阻止程序启动。
        }
    }

    private void LoadEmbeddedPacks()
    {
        var assembly = typeof(LocalizationService).Assembly;
        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(name => name.Contains(".Languages.", StringComparison.Ordinal)
                                    && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            // 磁盘文件优先，允许客户直接覆盖同 code 的内置语言包。
            try
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is null)
                    continue;
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                var pack = JsonSerializer.Deserialize<LanguagePack>(json, JsonOptions);
                if (pack is null
                    || string.IsNullOrWhiteSpace(pack.Code)
                    || string.IsNullOrWhiteSpace(pack.DisplayName)
                    || pack.Strings is null
                    || pack.Strings.Count == 0)
                {
                    continue;
                }
                if (!_packs.ContainsKey(pack.Code))
                {
                    pack.Source = resourceName;
                    _packs[pack.Code] = pack;
                }
            }
            catch
            {
            }
        }
    }

    private void EnsureBundledPacksOnDisk()
    {
        try
        {
            Directory.CreateDirectory(LanguageDirectory);
            var assembly = typeof(LocalizationService).Assembly;
            foreach (var resourceName in assembly.GetManifestResourceNames()
                         .Where(name => name.Contains(".Languages.", StringComparison.Ordinal)
                                        && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            {
                var fileName = resourceName.Split('.').TakeLast(2).Aggregate((left, right) => left + "." + right);
                var targetPath = Path.Combine(LanguageDirectory, fileName);
                if (File.Exists(targetPath))
                    continue;
                using var input = assembly.GetManifestResourceStream(resourceName);
                if (input is null)
                    continue;
                using var output = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
            }
        }
        catch
        {
            // 程序目录只读时仍可从嵌入式资源加载；只是不支持在该目录热添加文件。
        }
    }

    private static string ResolveLanguageDirectory()
    {
        var portable = Path.Combine(AppContext.BaseDirectory, "Languages");
        try
        {
            Directory.CreateDirectory(portable);
            var probe = Path.Combine(portable, ".write-test");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return portable;
        }
        catch
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root))
                root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var fallback = Path.Combine(root, "HelloCrab", "Languages");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    private sealed class LanguagePack
    {
        public string Code { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public int SortOrder { get; set; } = 100;
        public Dictionary<string, string> Strings { get; set; } =
            new(StringComparer.Ordinal);
        public string Source { get; set; } = string.Empty;
    }
}

public sealed record LanguageOption(string Code, string DisplayName)
{
    public override string ToString() => DisplayName;
}
