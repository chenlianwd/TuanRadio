using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.ViewModels;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class ProviderManifestTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "airadio-manifest-" + Guid.NewGuid().ToString("N"));

    public ProviderManifestTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Write(string json)
    {
        var path = Path.Combine(_dir, ProviderManifest.FileName);
        File.WriteAllText(path, json);
        return path;
    }

    private const string ValidManifest = """
        {
          "schemaVersion": 1,
          "generatedFrom": "checked-in package-lock.json",
          "providers": [
            {
              "id": "netease-node",
              "version": "1.0.0",
              "license": "MIT",
              "source": "npm:NeteaseCloudMusicApi@4.32.0",
              "components": [
                { "name": "NeteaseCloudMusicApi", "version": "4.32.0", "license": "MIT", "source": "", "integrity": "" },
                { "name": "axios", "version": "1.7.0", "license": "MIT", "source": "", "integrity": "" }
              ]
            },
            { "id": "kugou-node", "version": "1.6.2", "license": "MIT", "source": "x", "components": [] }
          ]
        }
        """;

    [Fact]
    public void Load_ValidManifest_ExposesProviderAndComponentVersions()
    {
        var manifest = ProviderManifest.Load(Write(ValidManifest));

        Assert.True(manifest.IsAvailable);
        Assert.Equal("1.6.2", manifest.GetProviderVersion("kugou-node"));
        Assert.Equal("4.32.0", manifest.GetComponentVersion("netease-node", "NeteaseCloudMusicApi"));
        Assert.Equal("4.32.0", manifest.GetComponentVersion("NETEASE-NODE", "neteasecloudmusicapi"));
        Assert.Null(manifest.GetComponentVersion("netease-node", "missing"));
        Assert.Null(manifest.GetProviderVersion("missing"));
    }

    [Fact]
    public void Evaluate_ReportsPinnedMismatchAndUnknown()
    {
        var pinned = ProviderManifest.Load(Write(ValidManifest));
        Assert.Equal(ProviderVersionState.Pinned, pinned.EvaluateNetease().State);
        Assert.Equal(ProviderVersionState.Pinned, pinned.EvaluateKugou().State);

        var drifted = ProviderManifest.Load(Write(
            ValidManifest.Replace("\"4.32.0\", \"license\"", "\"4.31.0\", \"license\"")
                         .Replace("\"version\": \"1.6.2\"", "\"version\": \"1.6.0\"")));
        var netease = drifted.EvaluateNetease();
        Assert.Equal(ProviderVersionState.Mismatch, netease.State);
        Assert.Equal("4.31.0", netease.InstalledVersion);
        Assert.Equal("4.32.0", netease.ExpectedVersion);
        Assert.Equal(ProviderVersionState.Mismatch, drifted.EvaluateKugou().State);

        var missing = ProviderManifest.Load(Path.Combine(_dir, "nope.json"));
        var unknown = missing.EvaluateNetease();
        Assert.Equal(ProviderVersionState.Unknown, unknown.State);
        Assert.Null(unknown.InstalledVersion);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("{\"schemaVersion\": 2, \"providers\": []}")]
    [InlineData("{\"schemaVersion\": \"1\", \"providers\": []}")]
    [InlineData("{\"schemaVersion\": 1}")]
    [InlineData("{\"schemaVersion\": 1, \"providers\": [ {\"id\": \"x\"}, 7, {\"version\": \"1\"} ]}")]
    public void Load_MalformedOrUnsupported_ReturnsEmptyWithoutThrowing(string json)
    {
        var manifest = ProviderManifest.Load(Write(json));

        Assert.False(manifest.IsAvailable);
        Assert.Empty(manifest.Entries);
    }

    [Fact]
    public void Load_OversizedManifest_IsRejected()
    {
        var path = Path.Combine(_dir, ProviderManifest.FileName);
        using (var stream = new FileStream(path, FileMode.Create))
            stream.SetLength(ProviderManifest.MaxManifestBytes + 1);

        Assert.False(ProviderManifest.Load(path).IsAvailable);
    }

    [Fact]
    public void LoadFromBaseDirectory_MissingFile_ReturnsEmpty()
    {
        Assert.False(ProviderManifest.LoadFromBaseDirectory(_dir).IsAvailable);

        Write(ValidManifest);
        Assert.True(ProviderManifest.LoadFromBaseDirectory(_dir).IsAvailable);
    }

    [Fact]
    public void PinnedVersions_MatchBuildScriptPins()
    {
        // 清单生成脚本是固定版本的构建期事实来源：运行时常量必须与之同步，防止升级代理后诊断误报“不一致”
        var script = FindUpwards("AIRadio.Desktop", "scripts", "generate-provider-manifest.js");
        Assert.NotNull(script);
        var text = File.ReadAllText(script!);

        Assert.Contains($"NeteaseCloudMusicApi@{ProviderManifest.PinnedVersions[ProviderManifest.NeteaseProviderId]}", text);
        Assert.Contains($"id: '{ProviderManifest.NeteaseProviderId}'", text);
        Assert.Contains($"id: '{ProviderManifest.KugouProviderId}'", text);

        var kugouPackage = FindUpwards("AIRadio.Desktop", "server-kugou", "package.json");
        Assert.NotNull(kugouPackage);
        Assert.Contains(
            $"\"version\": \"{ProviderManifest.PinnedVersions[ProviderManifest.KugouProviderId]}\"",
            File.ReadAllText(kugouPackage!));
    }

    [Fact]
    public void FormatBundledVersion_DescribesPinnedMismatchAndUnknownHonestly()
    {
        AppLanguage.Apply("en");
        try
        {
            var pinned = new ProviderVersionReport("netease-node", "4.32.0", "4.32.0", ProviderVersionState.Pinned);
            Assert.Equal("NeteaseCloudMusicApi 4.32.0 (pinned)",
                SettingsViewModel.FormatBundledVersion("NeteaseCloudMusicApi", pinned, null));

            var drift = new ProviderVersionReport("netease-node", "4.31.0", "4.32.0", ProviderVersionState.Mismatch);
            Assert.Contains("differs from pinned 4.32.0",
                SettingsViewModel.FormatBundledVersion("NeteaseCloudMusicApi", drift, null));

            var unknown = new ProviderVersionReport("kugou-node", null, "1.6.2", ProviderVersionState.Unknown);
            Assert.Contains("unknown", SettingsViewModel.FormatBundledVersion("KuGouMusicApi", unknown, null));
            Assert.DoesNotContain("1.6.2", SettingsViewModel.FormatBundledVersion("KuGouMusicApi", unknown, null));

            // 清单缺失时回退 package.json 的实际值，并仍与固定版本核对
            Assert.Equal("KuGouMusicApi 1.6.2 (pinned)",
                SettingsViewModel.FormatBundledVersion("KuGouMusicApi", unknown, "1.6.2"));
            Assert.Contains("differs from pinned",
                SettingsViewModel.FormatBundledVersion("KuGouMusicApi", unknown, "1.5.0"));
        }
        finally
        {
            AppLanguage.Apply("zh");
        }
    }

    [SkippableFact]
    public void BuiltManifest_MatchesPinnedVersionsAndContainsComponentIntegrity()
    {
#if DEBUG
        const string config = "Debug";
#else
        const string config = "Release";
#endif
        var manifestPath =
            FindUpwards("AIRadio.Desktop", "bin", config, "net10.0-windows10.0.19041.0", ProviderManifest.FileName) ??
            FindUpwards("AIRadio.Desktop", "bin", config, "net10.0-windows10.0.19041.0", "win-x64", ProviderManifest.FileName);
        var hasBundledNodeAssets = File.Exists(Path.Combine(AppContext.BaseDirectory, "server", "package.json"));
        Skip.If(manifestPath == null && !hasBundledNodeAssets,
            "精简构建（TuanRadioEnableNodeProviders=false）不生成 providers-manifest.json。");
        Assert.NotNull(manifestPath);

        var manifest = ProviderManifest.Load(manifestPath!);
        Assert.True(manifest.IsAvailable);
        Assert.Equal(ProviderVersionState.Pinned, manifest.EvaluateNetease().State);
        Assert.Equal(ProviderVersionState.Pinned, manifest.EvaluateKugou().State);

        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath!));
        var providers = doc.RootElement.GetProperty("providers").EnumerateArray().ToList();
        Assert.Equal(2, providers.Count);
        foreach (var provider in providers)
        {
            var components = provider.GetProperty("components").EnumerateArray().ToList();
            Assert.NotEmpty(components);
            foreach (var component in components)
            {
                Assert.False(string.IsNullOrWhiteSpace(component.GetProperty("name").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(component.GetProperty("version").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(component.GetProperty("license").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(component.GetProperty("integrity").GetString()));
            }
        }
    }

    [Fact]
    public void SlimBuildGate_CsprojIsolatesNodeProvidersAndManifestGeneration()
    {
        var csprojPath = FindUpwards("AIRadio.Desktop", "AIRadio.Desktop.csproj");
        Assert.NotNull(csprojPath);
        var doc = XDocument.Load(csprojPath!);
        var root = doc.Root!;

        var slimDefines = root.Elements("PropertyGroup")
            .Where(pg => (string?)pg.Attribute("Condition") is string cond &&
                         cond.Contains("TuanRadioEnableNodeProviders", StringComparison.Ordinal) &&
                         cond.Contains("!= 'true'", StringComparison.Ordinal))
            .SelectMany(pg => pg.Elements("DefineConstants"))
            .Select(el => el.Value)
            .ToList();
        Assert.Contains(slimDefines, value => value.Contains("TUANRADIO_SLIM_CORE", StringComparison.Ordinal));

        foreach (var targetName in new[] { "NpmInstall", "NpmInstallKugou", "GenerateProviderManifest" })
        {
            var target = root.Elements("Target")
                .FirstOrDefault(t => string.Equals((string?)t.Attribute("Name"), targetName, StringComparison.Ordinal));
            Assert.NotNull(target);
            var cond = (string?)target!.Attribute("Condition") ?? string.Empty;
            Assert.Contains("TuanRadioEnableNodeProviders", cond, StringComparison.Ordinal);
            Assert.Contains("== 'true'", cond, StringComparison.Ordinal);
        }

        var nodeContentItems = root.Descendants("Content")
            .Where(c => ((string?)c.Attribute("Include") ?? string.Empty) is var inc &&
                        (inc.Contains("server\\", StringComparison.OrdinalIgnoreCase) ||
                         inc.Contains("server-kugou\\", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        Assert.NotEmpty(nodeContentItems);
        foreach (var item in nodeContentItems)
        {
            var groupCond = (string?)item.Parent?.Attribute("Condition") ?? string.Empty;
            Assert.Contains("TuanRadioEnableNodeProviders", groupCond, StringComparison.Ordinal);
            Assert.Contains("== 'true'", groupCond, StringComparison.Ordinal);
        }
    }

    private static string? FindUpwards(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(relative).ToArray());
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
