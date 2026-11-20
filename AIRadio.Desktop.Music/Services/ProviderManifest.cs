using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AIRadio.Desktop.Services;

/// <summary>清单中单个随包 Provider 组件的版本信息（仅本地元数据，不含凭据）。</summary>
public sealed record ProviderManifestEntry(
    string Id,
    string Version,
    IReadOnlyDictionary<string, string> ComponentVersions);

/// <summary>随包版本相对固定版本的核对结论。</summary>
public enum ProviderVersionState
{
    /// <summary>清单缺失或不含该组件（精简构建、开发环境或清单损坏）。</summary>
    Unknown,

    /// <summary>与构建固定的版本一致。</summary>
    Pinned,

    /// <summary>与固定版本不一致（被手动替换或清单陈旧）。</summary>
    Mismatch,
}

public sealed record ProviderVersionReport(
    string ProviderId,
    string? InstalledVersion,
    string ExpectedVersion,
    ProviderVersionState State);

/// <summary>
/// 运行时读取构建期生成的 providers-manifest.json：只读本地文件，不联网、不启动进程。
/// 清单是完整性识别信息而非信任证明；损坏、过大、schema 不符一律视为“无清单”，绝不抛出。
/// </summary>
public sealed class ProviderManifest
{
    public const string FileName = "providers-manifest.json";
    public const int SupportedSchemaVersion = 1;
    public const string NeteaseProviderId = "netease-node";
    public const string KugouProviderId = "kugou-node";
    public const string NeteaseMainComponent = "NeteaseCloudMusicApi";

    /// <summary>清单体积上限：随包清单仅含数百条组件，超限视为异常文件。</summary>
    internal const long MaxManifestBytes = 8 * 1024 * 1024;

    /// <summary>构建固定的版本（与 scripts/generate-provider-manifest.js 保持一致）。</summary>
    public static IReadOnlyDictionary<string, string> PinnedVersions { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [NeteaseProviderId] = "4.32.0",
            [KugouProviderId] = "1.6.2",
        };

    public static ProviderManifest Empty { get; } = new(new Dictionary<string, ProviderManifestEntry>(StringComparer.OrdinalIgnoreCase));

    private readonly IReadOnlyDictionary<string, ProviderManifestEntry> _entries;

    private ProviderManifest(IReadOnlyDictionary<string, ProviderManifestEntry> entries) => _entries = entries;

    public bool IsAvailable => _entries.Count > 0;

    public IReadOnlyCollection<ProviderManifestEntry> Entries => (IReadOnlyCollection<ProviderManifestEntry>)_entries.Values;

    public static ProviderManifest LoadFromBaseDirectory(string? baseDirectory = null)
        => Load(Path.Combine(baseDirectory ?? AppContext.BaseDirectory, FileName));

    public static ProviderManifest Load(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > MaxManifestBytes)
                return Empty;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out var schemaVersion) ||
                schemaVersion != SupportedSchemaVersion ||
                !root.TryGetProperty("providers", out var providers) ||
                providers.ValueKind != JsonValueKind.Array)
            {
                return Empty;
            }

            var entries = new Dictionary<string, ProviderManifestEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var provider in providers.EnumerateArray())
            {
                if (provider.ValueKind != JsonValueKind.Object)
                    continue;
                var id = ReadString(provider, "id");
                var version = ReadString(provider, "version");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version))
                    continue;

                var components = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (provider.TryGetProperty("components", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var component in list.EnumerateArray())
                    {
                        if (component.ValueKind != JsonValueKind.Object)
                            continue;
                        var name = ReadString(component, "name");
                        var componentVersion = ReadString(component, "version");
                        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(componentVersion))
                            components[name] = componentVersion;
                    }
                }

                entries[id] = new ProviderManifestEntry(id, version, components);
            }

            return entries.Count == 0 ? Empty : new ProviderManifest(entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return Empty;
        }
    }

    public string? GetProviderVersion(string providerId)
        => _entries.TryGetValue(providerId, out var entry) ? entry.Version : null;

    public string? GetComponentVersion(string providerId, string componentName)
        => _entries.TryGetValue(providerId, out var entry) &&
           entry.ComponentVersions.TryGetValue(componentName, out var version)
            ? version
            : null;

    /// <summary>网易代理的实际生效版本取主依赖组件版本（包装包自身版本不代表上游 API 版本）。</summary>
    public ProviderVersionReport EvaluateNetease()
        => Evaluate(NeteaseProviderId, GetComponentVersion(NeteaseProviderId, NeteaseMainComponent));

    public ProviderVersionReport EvaluateKugou()
        => Evaluate(KugouProviderId, GetProviderVersion(KugouProviderId));

    private static ProviderVersionReport Evaluate(string providerId, string? installed)
    {
        var expected = PinnedVersions[providerId];
        var state = string.IsNullOrWhiteSpace(installed)
            ? ProviderVersionState.Unknown
            : string.Equals(installed, expected, StringComparison.Ordinal)
                ? ProviderVersionState.Pinned
                : ProviderVersionState.Mismatch;
        return new ProviderVersionReport(providerId, string.IsNullOrWhiteSpace(installed) ? null : installed, expected, state);
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
