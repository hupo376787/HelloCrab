using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using HelloCrab.Core.Services.Browser;

namespace HelloCrab.Core.Sites;

/// <summary>
/// 在已由真实页面成功发送的请求模板中替换游标。它同时支持普通查询参数、
/// URL 中的 JSON 参数、JSON POST body 和表单 body 中嵌套的 variables JSON。
/// 除游标外的动态参数和业务请求头保持不变，服务端拒绝复用时由采集器回退滚动。
/// </summary>
internal static class CursorRequestRewriter
{
    public static bool TryRewrite(
        BrowserRequestSnapshot previousRequest,
        string cursor,
        IReadOnlyCollection<string> cursorNames,
        out BrowserPageRequest nextRequest)
    {
        ArgumentNullException.ThrowIfNull(previousRequest);
        ArgumentException.ThrowIfNullOrWhiteSpace(cursor);
        ArgumentNullException.ThrowIfNull(cursorNames);

        var url = previousRequest.Url;
        var body = previousRequest.Body;
        var changed = TryRewriteUrl(url, cursor, cursorNames, out var rewrittenUrl);
        if (TryRewriteBody(body, cursor, cursorNames, out var rewrittenBody))
            changed = true;

        nextRequest = changed
            ? new BrowserPageRequest(
                rewrittenUrl,
                NormalizeMethod(previousRequest.Method, rewrittenBody),
                rewrittenBody,
                previousRequest.Headers)
            : null!;
        return changed;
    }

    public static bool TrySetQueryParameter(
        BrowserRequestSnapshot previousRequest,
        string name,
        string value,
        out BrowserPageRequest nextRequest,
        bool incrementPage = false)
    {
        if (!Uri.TryCreate(previousRequest.Url, UriKind.Absolute, out var uri))
        {
            nextRequest = null!;
            return false;
        }

        var pairs = ParseForm(uri.Query.TrimStart('?'));
        SetPair(pairs, name, value);
        if (incrementPage)
        {
            var page = pairs.FirstOrDefault(pair =>
                pair.Name.Equals("page", StringComparison.OrdinalIgnoreCase));
            if (page is not null
                && long.TryParse(page.Value, out var pageNumber))
            {
                page.Value = (pageNumber + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        var builder = new UriBuilder(uri)
        {
            Query = BuildForm(pairs)
        };
        nextRequest = new BrowserPageRequest(
            builder.Uri.ToString(),
            NormalizeMethod(previousRequest.Method, previousRequest.Body),
            previousRequest.Body,
            previousRequest.Headers);
        return true;
    }

    private static bool TryRewriteUrl(
        string url,
        string cursor,
        IReadOnlyCollection<string> cursorNames,
        out string rewrittenUrl)
    {
        rewrittenUrl = url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || string.IsNullOrEmpty(uri.Query))
        {
            return false;
        }

        var pairs = ParseForm(uri.Query.TrimStart('?'));
        var changed = RewritePairs(pairs, cursor, cursorNames);
        if (!changed)
            return false;

        var builder = new UriBuilder(uri)
        {
            Query = BuildForm(pairs)
        };
        rewrittenUrl = builder.Uri.ToString();
        return true;
    }

    private static bool TryRewriteBody(
        string? body,
        string cursor,
        IReadOnlyCollection<string> cursorNames,
        out string? rewrittenBody)
    {
        rewrittenBody = body;
        if (string.IsNullOrWhiteSpace(body))
            return false;

        if (TryRewriteJson(body, cursor, cursorNames, out var json))
        {
            rewrittenBody = json;
            return true;
        }

        var pairs = ParseForm(body);
        if (pairs.Count == 0 || !RewritePairs(pairs, cursor, cursorNames))
            return false;

        rewrittenBody = BuildForm(pairs);
        return true;
    }

    private static bool RewritePairs(
        List<FormPair> pairs,
        string cursor,
        IReadOnlyCollection<string> cursorNames)
    {
        var changed = false;
        foreach (var pair in pairs)
        {
            if (cursorNames.Contains(pair.Name, StringComparer.OrdinalIgnoreCase))
            {
                pair.Value = cursor;
                changed = true;
                continue;
            }

            if (TryRewriteJson(pair.Value, cursor, cursorNames, out var rewrittenJson))
            {
                pair.Value = rewrittenJson;
                changed = true;
            }
        }

        return changed;
    }

    private static bool TryRewriteJson(
        string value,
        string cursor,
        IReadOnlyCollection<string> cursorNames,
        out string rewritten)
    {
        rewritten = value;
        var trimmed = value.Trim();
        if (trimmed.Length < 2
            || trimmed[0] is not ('{' or '['))
        {
            return false;
        }

        try
        {
            var node = JsonNode.Parse(trimmed);
            if (node is null || !RewriteJsonNode(node, cursor, cursorNames))
                return false;

            rewritten = node.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = false
            });
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool RewriteJsonNode(
        JsonNode node,
        string cursor,
        IReadOnlyCollection<string> cursorNames)
    {
        var changed = false;
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToArray())
            {
                if (cursorNames.Contains(property.Key, StringComparer.OrdinalIgnoreCase))
                {
                    obj[property.Key] = property.Value is JsonArray
                        ? new JsonArray(cursor)
                        : JsonValue.Create(cursor);
                    changed = true;
                }
                else if (property.Value is not null)
                {
                    changed |= RewriteJsonNode(property.Value, cursor, cursorNames);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null)
                    changed |= RewriteJsonNode(item, cursor, cursorNames);
            }
        }

        return changed;
    }

    private static List<FormPair> ParseForm(string value)
    {
        var result = new List<FormPair>();
        if (string.IsNullOrEmpty(value))
            return result;

        foreach (var rawPair in value.Split('&', StringSplitOptions.RemoveEmptyEntries))
            result.Add(FormPair.FromRaw(rawPair));

        return result;
    }

    private static string BuildForm(IEnumerable<FormPair> pairs)
        => string.Join('&', pairs.Select(pair => pair.Render()));

    private static void SetPair(List<FormPair> pairs, string name, string value)
    {
        var pair = pairs.FirstOrDefault(candidate =>
            candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (pair is null)
            pairs.Add(FormPair.Create(name, value));
        else
            pair.Value = value;
    }

    private static string NormalizeMethod(string? method, string? body)
        => string.IsNullOrWhiteSpace(method)
            ? string.IsNullOrWhiteSpace(body) ? "GET" : "POST"
            : method.ToUpperInvariant();

    private sealed class FormPair
    {
        private readonly string? _raw;
        private string _value;
        private bool _changed;

        private FormPair(string name, string value, string? raw, bool changed)
        {
            Name = name;
            _value = value;
            _raw = raw;
            _changed = changed;
        }

        public string Name { get; }

        public string Value
        {
            get => _value;
            set
            {
                _value = value;
                _changed = true;
            }
        }

        public static FormPair FromRaw(string rawPair)
        {
            var separator = rawPair.IndexOf('=');
            var rawName = separator >= 0 ? rawPair[..separator] : rawPair;
            var rawValue = separator >= 0 ? rawPair[(separator + 1)..] : string.Empty;
            return new FormPair(
                WebUtility.UrlDecode(rawName.Replace('+', ' ')) ?? string.Empty,
                WebUtility.UrlDecode(rawValue.Replace('+', ' ')) ?? string.Empty,
                rawPair,
                changed: false);
        }

        public static FormPair Create(string name, string value)
            => new(name, value, raw: null, changed: true);

        public string Render()
            => !_changed && _raw is not null
                ? _raw
                : $"{Uri.EscapeDataString(Name)}={Uri.EscapeDataString(Value)}";
    }
}
