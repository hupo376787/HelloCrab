using System.Text.Json;
using System.Text.Json.Serialization;

namespace HelloCrab.Core.Contracts;

/// <summary>
/// Browser/WASM 与 Native AOT 兼容的远程 API JSON 元数据。
///
/// WebAssembly 发布时 System.Text.Json 的反射元数据默认可能被裁剪，
/// 因此所有远程 API DTO 都通过源生成上下文进行序列化和反序列化。
/// 使用 Web 默认值以保持与 ASP.NET Core Minimal API 的 camelCase JSON 一致。
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(RemoteHealthDto))]
[JsonSerializable(typeof(RemoteCrawlerSnapshot))]
[JsonSerializable(typeof(RemoteSettingsDto))]
[JsonSerializable(typeof(RemoteHistoryItemDto))]
[JsonSerializable(typeof(RemoteCommandResult))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<RemoteHistoryItemDto>))]
internal partial class RemoteJsonContext : JsonSerializerContext
{
}
