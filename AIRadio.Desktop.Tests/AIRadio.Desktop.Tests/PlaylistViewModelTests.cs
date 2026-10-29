using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using AIRadio.Desktop.Services.Music;
using Moq;
using Xunit;
using PlaylistViewModel = AIRadio.Desktop.ViewModels.PlaylistViewModel;

namespace AIRadio.Desktop.Tests;

public class PlaylistViewModelTests
{
    [Fact]
    public async Task DeferredSlowSearch_CannotOverwriteNewerSearch()
    {
        var audio = new Mock<IAudioService>();
        audio.Setup(x => x.TrackEnded).Returns(new Subject<Track?>());
        audio.Setup(x => x.TrackChanged).Returns(new Subject<Track?>());
        audio.Setup(x => x.StateChanged).Returns(new Subject<PlaybackState>());
        audio.Setup(x => x.Playlist).Returns(new List<Track>().AsReadOnly());
        var broker = new Mock<IMusicSourceBroker>();
        var late = new TaskCompletionSource<SearchOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        broker.Setup(x => x.SearchFastWithReportAsync("first", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchOutcome(new List<OnlineTrack>(), Array.Empty<SourceSearchStatus>()));
        broker.Setup(x => x.SearchSlowWithReportAsync("first", 20, It.IsAny<CancellationToken>()))
            .Returns(late.Task);
        broker.Setup(x => x.SearchFastWithReportAsync("second", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchOutcome(new List<OnlineTrack>
            {
                new() { Id = "fast:second", Title = "Second song" }
            }, Array.Empty<SourceSearchStatus>()));
        using var vm = new PlaylistViewModel(audio.Object, broker.Object, CreateTempPlaylistFile());

        vm.SearchText = "first";
        await vm.SearchCommand.Execute();
        vm.SearchText = "second";
        await vm.SearchCommand.Execute();
        late.SetResult(new SearchOutcome(new List<OnlineTrack>
        {
            new() { Id = "slow:first", Title = "Old song" }
        }, Array.Empty<SourceSearchStatus>()));
        await Task.Delay(50);

        Assert.Equal("fast:second", Assert.Single(vm.SearchResults).Id);
        Assert.Contains("Second", vm.SearchResults[0].Title);
    }

    [Fact]
    public async Task DeferredSlowSearch_QueryEditCancelsPendingResult()
    {
        var audio = new Mock<IAudioService>();
        audio.Setup(x => x.TrackEnded).Returns(new Subject<Track?>());
        audio.Setup(x => x.TrackChanged).Returns(new Subject<Track?>());
        audio.Setup(x => x.StateChanged).Returns(new Subject<PlaybackState>());
        audio.Setup(x => x.Playlist).Returns(new List<Track>().AsReadOnly());
        var broker = new Mock<IMusicSourceBroker>();
        var late = new TaskCompletionSource<SearchOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        broker.Setup(x => x.SearchFastWithReportAsync("first", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchOutcome(new List<OnlineTrack>(), Array.Empty<SourceSearchStatus>()));
        broker.Setup(x => x.SearchSlowWithReportAsync("first", 20, It.IsAny<CancellationToken>()))
            .Returns(late.Task);
        using var vm = new PlaylistViewModel(audio.Object, broker.Object, CreateTempPlaylistFile());

        vm.SearchText = "first";
        await vm.SearchCommand.Execute();
        vm.SearchText = "edited";
        Assert.Contains("搜索词已更改", vm.SearchStatusMessage);
        late.SetResult(new SearchOutcome(new List<OnlineTrack>
        {
            new() { Id = "slow:first", Title = "Old song" }
        }, Array.Empty<SourceSearchStatus>()));
        await Task.Delay(50);

        Assert.Empty(vm.SearchResults);
        Assert.Contains("搜索词已更改", vm.SearchStatusMessage);
    }

    [Fact]
    public async Task DeferredSlowSearch_PreservesFastSourceDiagnostics()
    {
        var audio = new Mock<IAudioService>();
        audio.Setup(x => x.TrackEnded).Returns(new Subject<Track?>());
        audio.Setup(x => x.TrackChanged).Returns(new Subject<Track?>());
        audio.Setup(x => x.StateChanged).Returns(new Subject<PlaybackState>());
        audio.Setup(x => x.Playlist).Returns(new List<Track>().AsReadOnly());
        var broker = new Mock<IMusicSourceBroker>();
        broker.Setup(x => x.SearchFastWithReportAsync("song", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchOutcome(new List<OnlineTrack>(),
                new[] { new SourceSearchStatus("Fast", "failed", 0, "offline") }));
        broker.Setup(x => x.SearchSlowWithReportAsync("song", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchOutcome(new List<OnlineTrack>
            {
                new() { Id = "slow:1", Title = "Song" }
            }, new[] { new SourceSearchStatus("YouTube", "ok", 1, null) }));
        using var vm = new PlaylistViewModel(audio.Object, broker.Object, CreateTempPlaylistFile());

        vm.SearchText = "song";
        await vm.SearchCommand.Execute();

        Assert.Equal("slow:1", Assert.Single(vm.SearchResults).Id);
        Assert.Contains("Fast失败:offline", vm.SearchStatusMessage);
        Assert.Contains("YouTube成功1条", vm.SearchStatusMessage);
    }

    private static (PlaylistViewModel vm, Mock<IAudioService> audioMock, Mock<IMusicSearchService> searchMock)
        CreateVm(string? playlistFile = null, Func<string, string, Task>? writeAllTextAsync = null)
    {
        var audioMock = new Mock<IAudioService>();
        audioMock.Setup(x => x.TrackEnded).Returns(new System.Reactive.Subjects.Subject<Track?>());
        audioMock.Setup(x => x.TrackChanged).Returns(new System.Reactive.Subjects.Subject<Track?>());
        audioMock.Setup(x => x.StateChanged).Returns(new System.Reactive.Subjects.Subject<PlaybackState>());
        audioMock.Setup(x => x.Playlist).Returns(new List<Track>().AsReadOnly());

        var searchMock = new Mock<IMusicSearchService>();
        // VM 现走带 token 的三参重载；Moq 不透传默认接口实现，两个重载都要兜底
        searchMock.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new List<OnlineTrack>());
        searchMock.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new List<OnlineTrack>());

        var vm = new PlaylistViewModel(
            audioMock.Object,
            searchMock.Object,
            playlistFile ?? CreateTempPlaylistFile(),
            writeAllTextAsync);
        return (vm, audioMock, searchMock);
    }

    [Theory]
    [InlineData("网易云音乐", "ok", 20, null, null, "网易云音乐成功20条")]
    [InlineData("网易云音乐", "ok", 20, "试听或失效片段，已过滤", null, "网易云音乐搜到20条(试听或失效片段，已过滤)")]
    [InlineData("YouTube", "timeout", 0, null, "超时(30s)", "YouTube超时")]
    [InlineData("酷我音乐", "failed", 0, null, "连接被拒绝", "酷我音乐失败:连接被拒绝")]
    public void FormatSourceStatus_CoversOkNoteTimeoutAndFailure(
        string name, string status, int count, string? note, string? error, string expected)
    {
        var entry = new SourceSearchStatus(name, status, count, error, note);

        Assert.Equal(expected, PlaylistViewModel.FormatSourceStatus(entry));
    }

    // Temp directories are created per-test and not cleaned up (M45).
    // OS temp cleanup handles them; for CI, consider adding IDisposable with cleanup.
    private static string CreateTempPlaylistFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "AIRadio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "playlist.json");
    }

    [Fact]
    public async Task LanguageChange_RelocalizesExistingActionStatus()
    {
        var (vm, _, search) = CreateVm();
        using (vm)
        {
            var online = new OnlineTrack
            {
                Id = "source:status-localization",
                Title = "Test Track",
                Artist = "Artist"
            };
            search.Setup(x => x.GetPlayUrlAsync(online.Id))
                .ReturnsAsync("https://example.com/test.mp3");

            try
            {
                await vm.AddOnlineCommand.Execute(online);
                Assert.Contains("已添加", vm.SearchStatusMessage);

                AppLanguage.Apply("en");

                Assert.Equal("Added \"Test Track\".", vm.SearchStatusMessage);
            }
            finally
            {
                AppLanguage.Apply("zh");
            }
        }
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("未知")]
    [InlineData("Unknown artist")]
    [InlineData("未知艺术家")]
    [InlineData("")]
    public void DisplayArtist_TreatsLegacyPlaceholdersAsLocalized(string stored)
    {
        var track = new Track { Id = "placeholder-artist", Title = "T", Artist = stored };
        try
        {
            AppLanguage.Apply("en");
            Assert.Equal("Unknown artist", track.DisplayArtist);
        }
        finally
        {
            AppLanguage.Apply("zh");
        }
    }

    [Fact]
    public void InitialTabIndex_IsZero()
    {
        var (vm, _, _) = CreateVm();
        Assert.Equal(0, vm.TabIndex);
    }

    [Fact]
    public void ShowPlaylistCommand_SetsTabIndexToZero()
    {
        var (vm, _, _) = CreateVm();
        vm.TabIndex = 2;
        vm.ShowPlaylistCommand.Execute().Subscribe();
        Assert.Equal(0, vm.TabIndex);
    }

    [Fact]
    public void ShowFavoritesCommand_SetsTabIndexToOne()
    {
        var (vm, _, _) = CreateVm();
        vm.ShowFavoritesCommand.Execute().Subscribe();
        Assert.Equal(1, vm.TabIndex);
    }

    [Fact]
    public void ShowSearchCommand_SetsTabIndexToTwo()
    {
        var (vm, _, _) = CreateVm();
        vm.ShowSearchCommand.Execute().Subscribe();
        Assert.Equal(2, vm.TabIndex);
    }

    [Fact]
    public Task ToggleFavorite_AddsToFavoritesWhenTrue()
    {
        var (vm, _, _) = CreateVm();
        var track = new Track { Id = "toggle-test-1", Title = "Test", Artist = "Me" };

        vm.Tracks.Add(track);
        vm.ToggleFavoriteCommand.Execute(track).Subscribe();

        Assert.True(track.IsFavorite);
        Assert.Contains(track, vm.Favorites);
        return Task.CompletedTask;
    }

    [Fact]
    public Task ToggleFavorite_RemovesFromFavoritesWhenFalse()
    {
        var (vm, _, _) = CreateVm();
        var track = new Track { Id = "toggle-test-2", Title = "Test", Artist = "Me" };
        vm.Tracks.Add(track);
        vm.ToggleFavoriteCommand.Execute(track).Subscribe(); // add first

        vm.ToggleFavoriteCommand.Execute(track).Subscribe(); // then remove

        Assert.False(track.IsFavorite);
        Assert.DoesNotContain(track, vm.Favorites);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ToggleFavorite_PersistsFavoriteIds()
    {
        var playlistFile = CreateTempPlaylistFile();
        var (vm, _, _) = CreateVm(playlistFile);
        var track = new Track
        {
            Id = "netease:ugly",
            Title = "丑八怪",
            Artist = "薛之谦",
            FilePath = "http://example.com/ugly.mp3",
            SourceId = "netease:ugly"
        };

        vm.Tracks.Add(track);
        vm.ToggleFavoriteCommand.Execute(track).Subscribe();
        // 保存是 fire-and-forget：轮询等待文件写入并包含预期内容，捕获写盘过程中的临时文件锁
        string saved = string.Empty;
        for (var waited = 0; waited < 50; waited++)
        {
            try
            {
                if (File.Exists(playlistFile))
                {
                    saved = await File.ReadAllTextAsync(playlistFile);
                    if (saved.Contains("\"FavoriteIds\"") && saved.Contains("netease:ugly"))
                        break;
                }
            }
            catch (IOException)
            {
                // File.Move/WriteAllTextAsync 临时占用的文件锁，稍候重试
            }
            await Task.Delay(100);
        }
        Assert.Contains("\"FavoriteIds\"", saved);
        Assert.Contains("netease:ugly", saved);
        // v3：在线曲目持久化稳定 Provider 身份与解析参数，不落盘临时播放直链
        using var doc = JsonDocument.Parse(saved);
        Assert.Equal(3, doc.RootElement.GetProperty("Version").GetInt32());
        var savedTrack = doc.RootElement.GetProperty("Tracks")[0];
        Assert.Equal("netease", savedTrack.GetProperty("Provider").GetProperty("ProviderId").GetString());
        Assert.Equal("ugly", savedTrack.GetProperty("Provider").GetProperty("TrackId").GetString());
        Assert.DoesNotContain("http://example.com/ugly.mp3", saved);
    }

    [Fact]
    public async Task LoadAsync_V1OnlineTrack_DropsStaleUrlAndKeepsIdentity()
    {
        var playlistFile = CreateTempPlaylistFile();
        await File.WriteAllTextAsync(playlistFile,
            """
            {
              "Tracks": [
                {
                  "Id": "netease:stale",
                  "Title": "旧链接",
                  "Artist": "歌手",
                  "Album": "",
                  "DurationMs": 200000,
                  "FilePath": "http://old-expired.example/a.mp3",
                  "SourceId": "netease:stale",
                  "IsOnline": true,
                  "IsFavorite": false
                }
              ],
              "FavoriteIds": []
            }
            """);

        var (vm, audioMock, searchMock) = CreateVm(playlistFile);

        await vm.LoadAsync();

        // v1 的在线 FilePath 可能是过期签名直链：读取时丢弃，播放前由 AudioService 懒解析
        searchMock.Verify(x => x.GetPlayUrlAsync(It.IsAny<string>()), Times.Never);
        Assert.Single(vm.Tracks);
        Assert.Equal(string.Empty, vm.Tracks[0].FilePath);
        Assert.Equal("netease:stale", vm.Tracks[0].SourceId);
    }

    [Fact]
    public async Task LoadAsync_V1Migration_WritesV3WithBackupAndIsIdempotent()
    {
        var playlistFile = CreateTempPlaylistFile();
        var v1Json = """
            {
              "Tracks": [
                {
                  "Id": "kuwo:9",
                  "Title": "歌",
                  "Artist": "手",
                  "Album": "",
                  "DurationMs": 100000,
                  "FilePath": "http://stale.example/x.mp3",
                  "SourceId": "kuwo:9",
                  "IsOnline": true,
                  "IsFavorite": true
                }
              ],
              "FavoriteIds": [ "kuwo:9" ]
            }
            """;
        await File.WriteAllTextAsync(playlistFile, v1Json);

        var (vm, _, _) = CreateVm(playlistFile);
        await vm.LoadAsync();
        await Task.Delay(200); // 加载完成后的自动回写

        var backupPath = playlistFile + ".v1.bak";
        Assert.True(File.Exists(backupPath));
        Assert.Equal(v1Json, await File.ReadAllTextAsync(backupPath));

        var migrated = await File.ReadAllTextAsync(playlistFile);
        using (var doc = JsonDocument.Parse(migrated))
        {
            Assert.Equal(3, doc.RootElement.GetProperty("Version").GetInt32());
            var track = doc.RootElement.GetProperty("Tracks")[0];
            Assert.Equal("kuwo", track.GetProperty("Provider").GetProperty("ProviderId").GetString());
            Assert.Equal("9", track.GetProperty("Provider").GetProperty("TrackId").GetString());
            Assert.True(track.GetProperty("IsFavorite").GetBoolean());
        }
        Assert.DoesNotContain("http://stale.example", migrated);
        Assert.True(vm.Tracks[0].IsFavorite);

        // 重复加载/保存幂等：已是 v3，不再重写备份
        await vm.LoadAsync();
        await vm.SaveAsync();
        await Task.Delay(100);

        Assert.Equal(v1Json, await File.ReadAllTextAsync(backupPath));
        using var doc2 = JsonDocument.Parse(await File.ReadAllTextAsync(playlistFile));
        Assert.Equal(3, doc2.RootElement.GetProperty("Version").GetInt32());
    }

    [Fact]
    public async Task LoadAsync_V2Migration_WritesV3AndKeepsV2Backup()
    {
        var playlistFile = CreateTempPlaylistFile();
        var v2Json = """
            {
              "Version": 2,
              "Tracks": [
                {
                  "Id": "kugou:OLD",
                  "Title": "旧歌",
                  "Artist": "歌手",
                  "Provider": { "ProviderId": "kugou", "TrackId": "OLD" },
                  "IsOnline": true
                }
              ],
              "FavoriteIds": []
            }
            """;
        await File.WriteAllTextAsync(playlistFile, v2Json);

        var (vm, _, _) = CreateVm(playlistFile);
        await vm.LoadAsync();
        await Task.Delay(200);

        var backupPath = playlistFile + ".v2.bak";
        Assert.True(File.Exists(backupPath));
        Assert.Equal(v2Json, await File.ReadAllTextAsync(backupPath));

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(playlistFile));
        Assert.Equal(3, doc.RootElement.GetProperty("Version").GetInt32());
        var metadata = doc.RootElement.GetProperty("Tracks")[0]
            .GetProperty("Provider")
            .GetProperty("Metadata");
        Assert.Equal(JsonValueKind.Object, metadata.ValueKind);
        Assert.Empty(vm.Tracks[0].ProviderMetadata);
    }

    [Fact]
    public async Task LoadAsync_V1Migration_BackupFailureFallsBackToTimestampedBackup()
    {
        var playlistFile = CreateTempPlaylistFile();
        var v1Json = """
            {
              "Tracks": [
                {
                  "Id": "netease:1",
                  "Title": "歌",
                  "Artist": "手",
                  "FilePath": "https://stale.invalid/a.mp3",
                  "SourceId": "netease:1",
                  "IsOnline": true
                }
              ]
            }
            """;
        await File.WriteAllTextAsync(playlistFile, v1Json);
        Directory.CreateDirectory(playlistFile + ".v1.bak"); // 让首选备份路径稳定失败

        var (vm, _, _) = CreateVm(playlistFile);
        await vm.LoadAsync();
        vm.Tracks.Add(new Track { Id = "local:new", Title = "新歌", Artist = "歌手", FilePath = @"X:\不存在.mp3" });
        await vm.SaveAsync();

        // 首选 .v1.bak 不可写时回退时间戳备份：保存不再被静默阻断，v1 内容仍被保留
        var migrated = await File.ReadAllTextAsync(playlistFile);
        using var doc = JsonDocument.Parse(migrated);
        Assert.Equal(3, doc.RootElement.GetProperty("Version").GetInt32());
        var dir = Path.GetDirectoryName(playlistFile)!;
        var fallback = Directory.GetFiles(dir, "playlist.json.v1.*.bak").Single();
        Assert.Equal(v1Json, await File.ReadAllTextAsync(fallback));
    }

    [Fact]
    public async Task LoadAsync_FutureVersion_RejectsSubsequentSaves()
    {
        var playlistFile = CreateTempPlaylistFile();
        var futureJson = """
            {
              "Version": 99,
              "FutureField": "must survive",
              "Tracks": [
                {
                  "Id": "future:1",
                  "Title": "未来歌曲",
                  "Artist": "未来歌手",
                  "Provider": { "ProviderId": "future", "TrackId": "1" },
                  "IsOnline": true
                }
              ]
            }
            """;
        await File.WriteAllTextAsync(playlistFile, futureJson);

        var (vm, _, _) = CreateVm(playlistFile);
        await vm.LoadAsync();
        // 会话内后续的任何保存（含列表变更触发的自动保存）都必须拒绝回写，
        // 否则首个列表操作就会用只含新内容的 v3 覆盖未来格式文件
        vm.Tracks.Add(new Track { Id = "local:new", Title = "新歌", Artist = "歌手", FilePath = @"X:\不存在.mp3" });
        await vm.SaveAsync();

        Assert.Equal(futureJson, await File.ReadAllTextAsync(playlistFile));
    }

    [Fact]
    public async Task LoadAsync_FutureVersion_DoesNotLoadOrDowngradeFile()
    {
        var playlistFile = CreateTempPlaylistFile();
        var futureJson = """
            {
              "Version": 99,
              "FutureField": "must survive",
              "Tracks": [
                {
                  "Id": "future:1",
                  "Title": "未来歌曲",
                  "Artist": "未来歌手",
                  "Provider": { "ProviderId": "future", "TrackId": "1" },
                  "IsOnline": true
                }
              ]
            }
            """;
        await File.WriteAllTextAsync(playlistFile, futureJson);

        var (vm, audioMock, _) = CreateVm(playlistFile);
        await vm.LoadAsync();
        await Task.Delay(100);

        Assert.Empty(vm.Tracks);
        Assert.Equal(futureJson, await File.ReadAllTextAsync(playlistFile));
        audioMock.Verify(x => x.LoadTracks(It.IsAny<IEnumerable<Track>>()), Times.Never);
    }

    [Fact]
    public async Task LoadAsync_PreservesOnlineFavoriteWhenUrlRefreshFails()
    {
        var playlistFile = CreateTempPlaylistFile();
        await File.WriteAllTextAsync(playlistFile,
            """
            {
              "Tracks": [
                {
                  "Id": "netease:ugly",
                  "Title": "丑八怪",
                  "Artist": "薛之谦",
                  "Album": "",
                  "DurationMs": 240000,
                  "FilePath": "",
                  "SourceId": "netease:ugly",
                  "IsOnline": true,
                  "IsFavorite": true
                }
              ],
              "FavoriteIds": [ "netease:ugly" ]
            }
            """);

        var (vm, audioMock, searchMock) = CreateVm(playlistFile);

        await vm.LoadAsync();
        await Task.Delay(150);

        Assert.Single(vm.Tracks);
        Assert.True(vm.Tracks[0].IsFavorite);
        Assert.Single(vm.Favorites);
        audioMock.Verify(x => x.LoadTracks(It.Is<IEnumerable<Track>>(tracks =>
            tracks.Any(t => t.Id == "netease:ugly" && t.IsFavorite))), Times.Once);

        var saved = await File.ReadAllTextAsync(playlistFile);
        Assert.Contains("ugly", saved);
        Assert.Contains("netease", saved);
        Assert.Contains("\"FavoriteIds\"", saved);
    }

    [Fact]
    public Task RemoveTrackCommand_RemovesTrackAndCallsAudioService()
    {
        var (vm, audioMock, _) = CreateVm();
        var track = new Track { Title = "Test", FilePath = "" };
        vm.Tracks.Add(track);

        vm.RemoveTrackCommand.Execute(track).Subscribe();

        audioMock.Verify(x => x.RemoveTrack(track), Times.Once);
        Assert.DoesNotContain(track, vm.Tracks);
        return Task.CompletedTask;
    }

    [Fact]
    public void PlayFavoriteCommand_StartsContextOverFavoritesFromClickedTrack()
    {
        var (vm, audioMock, _) = CreateVm();
        var track1 = new Track { Title = "A", FilePath = "" };
        var track2 = new Track { Title = "B", FilePath = "" };
        vm.Tracks.Add(track1);
        vm.Tracks.Add(track2);
        vm.Favorites.Add(track1);
        vm.Favorites.Add(track2);

        IReadOnlyList<Track>? contextTracks = null;
        var contextStartIndex = -1;
        audioMock.Setup(x => x.StartPlaybackContext(
                It.IsAny<IEnumerable<Track>>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<string?>()))
            .Callback<IEnumerable<Track>, int, bool, string?>((tracks, index, _, _) =>
            {
                contextTracks = tracks.ToList();
                contextStartIndex = index;
            });

        vm.PlayFavoriteCommand.Execute(track2).Subscribe();

        audioMock.Verify(x => x.StartPlaybackContext(
            It.Is<IEnumerable<Track>>(tracks => tracks.SequenceEqual(new[] { track1, track2 })),
            1, false, "收藏"), Times.Once);
        Assert.Equal(1, contextStartIndex);
        Assert.NotNull(contextTracks);
    }

    [Fact]
    public void PlayFavoriteCommand_TrackMissingFromFavoritesFallsBackToFirst()
    {
        // 收藏点播上下文化后不再有"不在主歌单则静默"语义：找不到点击项就从头播收藏
        var (vm, audioMock, _) = CreateVm();
        var track1 = new Track { Title = "A", FilePath = "" };
        var outsider = new Track { Title = "NotInFavorites", FilePath = "" };
        vm.Tracks.Add(track1);
        vm.Favorites.Add(track1);

        vm.PlayFavoriteCommand.Execute(outsider).Subscribe();

        audioMock.Verify(x => x.StartPlaybackContext(
            It.Is<IEnumerable<Track>>(tracks => tracks.SequenceEqual(new[] { track1 })),
            0, false, "收藏"), Times.Once);
    }

    [Fact]
    public void PlayOnlineCommand_StartsSearchResultContextWithoutPlaylistWrite()
    {
        var (vm, audioMock, _) = CreateVm();
        var result1 = new OnlineTrack { Id = "netease:a", Title = "A", Artist = "X" };
        var result2 = new OnlineTrack { Id = "netease:b", Title = "B", Artist = "Y" };
        vm.SearchResults.Add(result1);
        vm.SearchResults.Add(result2);

        vm.PlayOnlineCommand.Execute(result2).Subscribe();

        // 上下文曲目 = 搜索结果快照（空 FilePath 懒解析），起点 = 点击项
        audioMock.Verify(x => x.StartPlaybackContext(
            It.Is<IEnumerable<Track>>(tracks => tracks
                .Select(track => track.SourceId)
                .SequenceEqual(new[] { "netease:a", "netease:b" })),
            1, false, "搜索结果"), Times.Once);
        // 点播不再隐式写主歌单、不触发入列与保存
        Assert.Empty(vm.Tracks);
        audioMock.Verify(x => x.AddTracks(It.IsAny<IEnumerable<Track>>()), Times.Never);
        audioMock.Verify(x => x.PlayTrack(It.IsAny<Track>()), Times.Never);
    }

    [Fact]
    public void PlayOnlineCommand_ResultMissingFromSnapshotFallsBackToIndexZero()
    {
        var (vm, audioMock, _) = CreateVm();
        var result1 = new OnlineTrack { Id = "netease:a", Title = "A", Artist = "X" };
        vm.SearchResults.Add(result1);
        var clickedElsewhere = new OnlineTrack { Id = "netease:z", Title = "Z", Artist = "W" };

        vm.PlayOnlineCommand.Execute(clickedElsewhere).Subscribe();

        audioMock.Verify(x => x.StartPlaybackContext(
            It.Is<IEnumerable<Track>>(tracks => tracks
                .Select(track => track.SourceId)
                .SequenceEqual(new[] { "netease:a" })),
            0, false, "搜索结果"), Times.Once);
    }

    [Fact]
    public async Task AddOnlineCommand_DoesNotDuplicateWhenUrlChanged()
    {
        var playlistFile = CreateTempPlaylistFile();
        var (vm, _, searchMock) = CreateVm(playlistFile);
        // 已存在同源同曲：仅临时 URL 因刷新而不同，不得视为新歌
        searchMock.Setup(x => x.GetPlayUrlAsync("track1"))
            .ReturnsAsync("http://refreshed.example/track1.mp3");

        var existing = new Track
        {
            Id = "track1",
            Title = "Online Song",
            Artist = "Artist",
            FilePath = "http://existing.com/track1.mp3",
            SourceId = "track1"
        };
        vm.Tracks.Add(existing);

        var onlineTrack = new OnlineTrack { Id = "track1", Title = "Online Song", Artist = "Artist" };
        await vm.AddOnlineCommand.Execute(onlineTrack);

        Assert.Single(vm.Tracks);
    }

    [Fact]
    public async Task AddOnlineCommand_DoesNotDuplicateAcrossSourcesForSameSong()
    {
        var (vm, _, searchMock) = CreateVm();
        searchMock.Setup(x => x.GetPlayUrlAsync("kuwo:456"))
            .ReturnsAsync("http://kuwo.example/song.mp3");

        var existing = new Track
        {
            Id = "netease:123",
            Title = "晴天",
            Artist = "周杰伦",
            FilePath = string.Empty,
            SourceId = "netease:123"
        };
        vm.Tracks.Add(existing);

        var onlineTrack = new OnlineTrack { Id = "kuwo:456", Title = "晴天", Artist = "周杰伦" };
        await vm.AddOnlineCommand.Execute(onlineTrack);

        // 跨源同曲按标准化身份判定为同一首，不再重复入列
        Assert.Single(vm.Tracks);
    }

    [Fact]
    public async Task AddOnlineCommand_AllowsSameTitleArtistWhenDurationDiffersSignificantly()
    {
        var (vm, _, searchMock) = CreateVm();
        searchMock.Setup(x => x.GetPlayUrlAsync("kuwo:live"))
            .ReturnsAsync("http://kuwo.example/live.mp3");
        vm.Tracks.Add(new Track
        {
            Id = "netease:studio",
            Title = "同名歌曲",
            Artist = "同一歌手",
            Duration = TimeSpan.FromSeconds(180),
            SourceId = "netease:studio"
        });

        await vm.AddOnlineCommand.Execute(new OnlineTrack
        {
            Id = "kuwo:live",
            Title = "同名歌曲",
            Artist = "同一歌手",
            DurationMs = 240_000
        });

        Assert.Equal(2, vm.Tracks.Count);
    }

    [Fact]
    public void AddExternalTrack_DoesNotDuplicateSameSongFromAnotherProvider()
    {
        var (vm, audioMock, _) = CreateVm();
        vm.Tracks.Add(new Track
        {
            Id = "netease:123",
            Title = "晴天",
            Artist = "周杰伦",
            Duration = TimeSpan.FromSeconds(269),
            SourceId = "netease:123"
        });

        vm.AddExternalTrack(new Track
        {
            Id = "kuwo:456",
            Title = "晴天",
            Artist = "周杰伦",
            Duration = TimeSpan.FromSeconds(270),
            SourceId = "kuwo:456"
        });

        Assert.Single(vm.Tracks);
        audioMock.Verify(x => x.AddTracks(It.IsAny<IEnumerable<Track>>()), Times.Never);
    }

    [Fact]
    public async Task AddOnlineCommand_AddsTrackToPlaylist()
    {
        var (vm, audioMock, searchMock) = CreateVm();
        searchMock.Setup(x => x.GetPlayUrlAsync("track1"))
            .ReturnsAsync("http://example.com/track1.mp3");

        var onlineTrack = new OnlineTrack { Id = "track1", Title = "Online Song", Artist = "Artist" };

        await vm.AddOnlineCommand.Execute(onlineTrack);

        Assert.Single(vm.Tracks);
        Assert.Equal("Online Song", vm.Tracks[0].Title);
        audioMock.Verify(x => x.AddTracks(It.IsAny<IEnumerable<Track>>()), Times.Once);
    }

    [Fact]
    public async Task SearchAsync_ClearsAndFillsSearchResults()
    {
        var (vm, _, searchMock) = CreateVm();
        var results = new List<OnlineTrack>
        {
            new OnlineTrack { Id = "1", Title = "Song A" },
            new OnlineTrack { Id = "2", Title = "Song B" }
        };
        searchMock.Setup(x => x.SearchAsync("test", 20, It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(results);

        vm.SearchText = "test";
        // IObservable 可直接 await 首个结果：命令异步完成即断言，替代固定 Sleep
        await vm.SearchCommand.Execute();

        Assert.Equal(2, vm.SearchResults.Count);
        Assert.Equal(2, vm.TabIndex); // auto-switches to search tab
    }

    [Fact]
    public async Task SearchAsync_DoesNothingWhenTextEmpty()
    {
        var (vm, _, searchMock) = CreateVm();
        await vm.SearchCommand.Execute();
        searchMock.Verify(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<System.Threading.CancellationToken>()), Times.Never);
        searchMock.Verify(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void AddFiles_AddsTracksFromPaths()
    {
        var (vm, audioMock, _) = CreateVm();
        var path = "D:\\test.mp3";

        vm.AddFiles(new[] { path });

        Assert.NotEmpty(vm.Tracks);
        audioMock.Verify(x => x.AddTracks(It.IsAny<IEnumerable<Track>>()), Times.Once);
    }

    [Fact]
    public async Task LoadAsync_MalformedJson_DoesNotThrow()
    {
        var playlistFile = CreateTempPlaylistFile();
        await File.WriteAllTextAsync(playlistFile, "{ invalid json !!!");

        var (vm, _, _) = CreateVm(playlistFile);

        var ex = await Record.ExceptionAsync(() => vm.LoadAsync());
        Assert.Null(ex);
        Assert.Empty(vm.Tracks);
    }

    [Fact]
    public async Task SaveAsync_ConcurrentRequests_ProduceValidPlaylistFile()
    {
        var playlistFile = CreateTempPlaylistFile();
        var sync = new object();
        var activeWriters = 0;
        var maxConcurrentWriters = 0;

        async Task ObservedWriteAsync(string path, string contents)
        {
            lock (sync)
            {
                activeWriters++;
                maxConcurrentWriters = Math.Max(maxConcurrentWriters, activeWriters);
            }

            try
            {
                await Task.Delay(20);
                await File.WriteAllTextAsync(path, contents);
            }
            finally
            {
                lock (sync)
                    activeWriters--;
            }
        }

        var (vm, _, _) = CreateVm(playlistFile, ObservedWriteAsync);
        for (var i = 0; i < 20; i++)
        {
            vm.Tracks.Add(new Track
            {
                Id = $"track-{i}",
                Title = $"Track {i}",
                FilePath = $"http://example.com/{i}.mp3"
            });
        }

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => vm.SaveAsync()));

        var json = await File.ReadAllTextAsync(playlistFile);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, maxConcurrentWriters);
        Assert.Equal(20, doc.RootElement.GetProperty("Tracks").GetArrayLength());
    }

    // ---------- 搜索状态行按失败分类渲染（docs/plans/2026-09-15-music-source-experience-enhancement-design.md） ----------

    [Fact]
    public void FormatSourceStatus_RendersStructuredFailureKinds()
    {
        var originalLanguage = AppLanguage.Current;
        try
        {
            AppLanguage.Apply("zh");

            var notSignedIn = PlaylistViewModel.FormatSourceStatus(
                new Services.SourceSearchStatus("酷狗音乐", "failed", 0, "raw", FailureKind: MusicSourceFailureKind.NotSignedIn));
            Assert.Contains("未登录", notSignedIn);
            Assert.Contains("扫码登录", notSignedIn);

            var authExpired = PlaylistViewModel.FormatSourceStatus(
                new Services.SourceSearchStatus("网易云音乐", "failed", 0, "raw", FailureKind: MusicSourceFailureKind.AuthExpired));
            // AuthExpired 同时覆盖登录态失效与本地代理未就绪，文案不得只给"重新扫码"这一半场景有效的动作
            Assert.Contains("登录态或本地服务异常", authExpired);

            var riskControl = PlaylistViewModel.FormatSourceStatus(
                new Services.SourceSearchStatus("酷狗音乐", "failed", 0, "raw", FailureKind: MusicSourceFailureKind.RiskControl));
            Assert.Contains("滑块验证", riskControl);
            Assert.Contains("10 分钟", riskControl);

            var apiBroken = PlaylistViewModel.FormatSourceStatus(
                new Services.SourceSearchStatus("咪咕音乐", "failed", 0, "raw", FailureKind: MusicSourceFailureKind.ApiBroken));
            Assert.Contains("暂时不可用", apiBroken);
        }
        finally
        {
            AppLanguage.Apply(originalLanguage);
        }
    }

    [Fact]
    public void FormatSourceStatus_UnknownOrNoneFallsBackToRawErrorText()
    {
        var originalLanguage = AppLanguage.Current;
        try
        {
            AppLanguage.Apply("zh");

            var unknown = PlaylistViewModel.FormatSourceStatus(
                new Services.SourceSearchStatus("酷我音乐", "failed", 0, "业务码异常(301)", FailureKind: MusicSourceFailureKind.Unknown));
            Assert.Contains("业务码异常(301)", unknown);

            var none = PlaylistViewModel.FormatSourceStatus(
                new Services.SourceSearchStatus("酷我音乐", "failed", 0, "传输故障", FailureKind: MusicSourceFailureKind.None));
            Assert.Contains("传输故障", none);
        }
        finally
        {
            AppLanguage.Apply(originalLanguage);
        }
    }

    // ==== 歌单列表操作增强（多选/队列命令/在播标记/过滤/清空确认/重排）====

    private static (PlaylistViewModel vm, Mock<IAudioService> audioMock, System.Reactive.Subjects.Subject<Track?> trackChanged)
        CreateVmWithTrackChannel(string? playlistFile = null)
    {
        var audioMock = new Mock<IAudioService>();
        var trackChanged = new System.Reactive.Subjects.Subject<Track?>();
        audioMock.Setup(x => x.TrackEnded).Returns(new Subject<Track?>());
        audioMock.Setup(x => x.TrackChanged).Returns(trackChanged);
        audioMock.Setup(x => x.StateChanged).Returns(new Subject<PlaybackState>());
        audioMock.Setup(x => x.Playlist).Returns(new List<Track>().AsReadOnly());
        var searchMock = new Mock<IMusicSearchService>();
        var vm = new PlaylistViewModel(audioMock.Object, searchMock.Object,
            playlistFile ?? CreateTempPlaylistFile());
        return (vm, audioMock, trackChanged);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(condition());
    }

    [Fact]
    public void PlayFromLibraryCommand_PlaysOnlyWhenTrackInLibrary()
    {
        var (vm, audio, _) = CreateVmWithTrackChannel();
        try
        {
            var inLibrary = new Track { Title = "在库" };
            var foreign = new Track { Title = "不在库" };
            vm.Tracks.Add(inLibrary);

            vm.PlayFromLibraryCommand.Execute(inLibrary).Subscribe();
            vm.PlayFromLibraryCommand.Execute(foreign).Subscribe();

            audio.Verify(x => x.PlayTrack(inLibrary), Times.Once);
            audio.Verify(x => x.PlayTrack(foreign), Times.Never);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void QueueCommands_PlayNextAndEnqueueLibraryTracks()
    {
        var (vm, audio, _) = CreateVmWithTrackChannel();
        try
        {
            var track = new Track { Title = "A" };

            vm.PlayTrackNextCommand.Execute(track).Subscribe();
            audio.Verify(x => x.PlayNextInQueue(track), Times.Once);

            vm.EnqueueTrackCommand.Execute(track).Subscribe();
            audio.Verify(x => x.AddToQueue(track), Times.Once);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void SearchQueueCommands_PassLazyResolvedOnlineTracks()
    {
        var (vm, audio, _) = CreateVmWithTrackChannel();
        try
        {
            var online = new OnlineTrack { Id = "kugou:x", Title = "甲", Artist = "歌手", DurationMs = 1000 };

            vm.PlaySearchNextCommand.Execute(online).Subscribe();
            vm.AddSearchToQueueCommand.Execute(online).Subscribe();

            audio.Verify(x => x.PlayNextInQueue(It.Is<Track>(t =>
                t.SourceId == "kugou:x" && string.IsNullOrEmpty(t.FilePath))), Times.Once);
            audio.Verify(x => x.AddToQueue(It.Is<Track>(t =>
                t.SourceId == "kugou:x" && string.IsNullOrEmpty(t.FilePath))), Times.Once);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task NowPlayingMarker_LooseMatchMarksLibraryInstanceAndClearsOnNull()
    {
        var (vm, audio, trackChanged) = CreateVmWithTrackChannel();
        try
        {
            var canonical = new Track { SourceId = "kugou:a", Title = "A", Artist = "X" };
            vm.Tracks.Add(canonical);
            // 在播实例是瞬态副本（不同引用、同 SourceId）：宽松匹配应标到列表本体
            var transientCopy = new Track { SourceId = "kugou:a", Title = "A", Artist = "X" };

            trackChanged.OnNext(transientCopy);
            await WaitUntilAsync(() => canonical.IsNowPlaying);
            Assert.Same(canonical, vm.NowPlayingListTrack);
            audio.Verify(x => x.PlayTrack(It.IsAny<Track>()), Times.Never);

            trackChanged.OnNext(null);
            await WaitUntilAsync(() => !canonical.IsNowPlaying && vm.NowPlayingListTrack == null);

            // 不在列表中的曲：无标记可落
            trackChanged.OnNext(new Track { SourceId = "kugou:zzz", Title = "ZZZ" });
            await Task.Delay(50);
            Assert.Null(vm.NowPlayingListTrack);

            vm.Dispose();
            // Dispose 后推送不再触碰标记（NowPlayingListTrack 保持 null）
            trackChanged.OnNext(transientCopy);
            await Task.Delay(50);
            Assert.Null(vm.NowPlayingListTrack);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task LibraryFilterText_FiltersVisibleTracks_AndCombinesWithSyncedPlaylist()
    {
        var (vm, _, _) = CreateVmWithTrackChannel();
        try
        {
            var a = new Track { SourceId = "kugou:a", Title = "晴天", Artist = "周杰伦" };
            var b = new Track { SourceId = "kugou:b", Title = "稻香", Artist = "周杰伦" };
            var c = new Track { SourceId = "kugou:moon", Title = "月亮", Artist = "张三" };
            foreach (var track in new[] { a, b, c })
                vm.Tracks.Add(track);

            vm.LibraryFilterText = "周杰伦";
            await WaitUntilAsync(() => vm.VisibleLibraryTracks.Count == 2);
            Assert.DoesNotContain(vm.VisibleLibraryTracks, t => ReferenceEquals(t, c));

            // 同步歌单视图与关键词 AND 组合
            var synced = new AIRadio.Desktop.ViewModels.SyncedPlaylistInfo { Id = "kugou-playlist:1", ProviderId = "kugou", RemoteId = "1" };
            synced.TrackSourceIds.Add("kugou:moon");
            vm.SyncedPlaylists.Add(synced);
            vm.SelectedSyncedPlaylist = synced;
            Assert.Empty(vm.VisibleLibraryTracks); // c 不含"周杰伦"

            vm.LibraryFilterText = "";
            await WaitUntilAsync(() => vm.VisibleLibraryTracks.Count == 1);
            Assert.Same(c, vm.VisibleLibraryTracks.Single());
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void ClearPlaylist_RequiresSecondClickWithinWindow()
    {
        var (vm, audio, _) = CreateVmWithTrackChannel();
        try
        {
            var track = new Track { Title = "A" };
            vm.Tracks.Add(track);
            var originalText = vm.ClearPlaylistButtonText;

            // 首次点击：武装确认态，不执行清空
            vm.ClearPlaylistCommand.Execute().Subscribe();
            audio.Verify(x => x.ClearPlaylist(), Times.Never);
            Assert.Single(vm.Tracks);
            Assert.NotEqual(originalText, vm.ClearPlaylistButtonText);

            // 5 秒窗口内第二次点击：执行清空并回到初始文案
            vm.ClearPlaylistCommand.Execute().Subscribe();
            audio.Verify(x => x.ClearPlaylist(), Times.Once);
            Assert.Empty(vm.Tracks);
            Assert.Empty(vm.Favorites);
            Assert.Empty(vm.SyncedPlaylists);
            Assert.Equal(originalText, vm.ClearPlaylistButtonText);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task MoveVisibleTrack_ReordersTracksMirrorsAudioAndPersists()
    {
        var playlistFile = CreateTempPlaylistFile();
        var (vm, audio, _) = CreateVmWithTrackChannel(playlistFile);
        try
        {
            var a = new Track { SourceId = "kugou:a", Title = "A" };
            var b = new Track { SourceId = "kugou:b", Title = "B" };
            var c = new Track { SourceId = "kugou:c", Title = "C" };
            foreach (var track in new[] { a, b, c })
                vm.Tracks.Add(track);
            await Task.Delay(50); // 等自动保存落盘

            vm.MoveVisibleTrack(0, 2);

            Assert.Equal(new[] { "B", "C", "A" }, vm.Tracks.Select(t => t.Title));
            Assert.Equal(new[] { "B", "C", "A" }, vm.VisibleLibraryTracks.Select(t => t.Title));
            audio.Verify(x => x.MoveTrack(0, 2), Times.Once);

            await WaitUntilAsync(() =>
            {
                try
                {
                    var json = File.ReadAllText(playlistFile);
                    using var doc = JsonDocument.Parse(json);
                    var titles = doc.RootElement.GetProperty("Tracks").EnumerateArray()
                        .Select(item => item.GetProperty("Title").GetString());
                    return titles.SequenceEqual(new[] { "B", "C", "A" });
                }
                catch (IOException)
                {
                    // VM 的异步保存（写临时文件+替换）与轮询读盘的短暂互斥窗口：视为未就绪继续轮询
                    return false;
                }
            });
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task MoveVisibleTrack_RejectedWhileFilteredOrSyncedViewActive()
    {
        var (vm, audio, _) = CreateVmWithTrackChannel();
        try
        {
            var a = new Track { SourceId = "kugou:a", Title = "A" };
            var b = new Track { SourceId = "kugou:b", Title = "B" };
            foreach (var track in new[] { a, b })
                vm.Tracks.Add(track);

            vm.LibraryFilterText = "A";
            await WaitUntilAsync(() => vm.VisibleLibraryTracks.Count == 1);
            Assert.False(vm.CanReorderLibrary);

            vm.MoveVisibleTrack(0, 1);
            audio.Verify(x => x.MoveTrack(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
            Assert.Equal(new[] { "A", "B" }, vm.Tracks.Select(t => t.Title));
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void RemoveSelectedTracks_RemovesAllSelectedAndClearsSelection()
    {
        var (vm, audio, _) = CreateVmWithTrackChannel();
        try
        {
            var a = new Track { SourceId = "kugou:a", Title = "A" };
            var b = new Track { SourceId = "kugou:b", Title = "B" };
            var keeper = new Track { SourceId = "kugou:k", Title = "K" };
            foreach (var track in new[] { a, b, keeper })
                vm.Tracks.Add(track);
            vm.SelectedLibraryTracks.Add(a);
            vm.SelectedLibraryTracks.Add(b);

            vm.RemoveSelectedTracksCommand.Execute().Subscribe();

            audio.Verify(x => x.RemoveTrack(a), Times.Once);
            audio.Verify(x => x.RemoveTrack(b), Times.Once);
            audio.Verify(x => x.RemoveTrack(keeper), Times.Never);
            Assert.Equal(new[] { "K" }, vm.Tracks.Select(t => t.Title));
            Assert.Empty(vm.SelectedLibraryTracks);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void FavoriteSelectedTracks_FavoritesOnlyUnfavorited()
    {
        var (vm, _, _) = CreateVmWithTrackChannel();
        try
        {
            var a = new Track { SourceId = "kugou:a", Title = "A" };
            var b = new Track { SourceId = "kugou:b", Title = "B" };
            foreach (var track in new[] { a, b })
                vm.Tracks.Add(track);
            vm.Favorites.Add(b);
            vm.SelectedLibraryTracks.Add(a);
            vm.SelectedLibraryTracks.Add(b);

            vm.FavoriteSelectedTracksCommand.Execute().Subscribe();

            Assert.True(a.IsFavorite);
            Assert.True(b.IsFavorite);
            Assert.Equal(2, vm.Favorites.Count);
            Assert.Contains(AppLanguage.T("已收藏", "Favorited"), vm.LocalLibraryStatus);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task NowPlayingMarker_PrefersExactInstanceWhenDuplicatesShareSourceId()
    {
        var (vm, _, trackChanged) = CreateVmWithTrackChannel();
        try
        {
            // 同曲多份（同 SourceId 不同实例）：在播的恰是第二份本体时高亮必须落在第二份
            var first = new Track { SourceId = "kugou:a", Title = "晴天", Artist = "周杰伦" };
            var second = new Track { SourceId = "kugou:a", Title = "晴天", Artist = "周杰伦" };
            vm.Tracks.Add(first);
            vm.Tracks.Add(second);

            trackChanged.OnNext(second);
            await WaitUntilAsync(() => second.IsNowPlaying);

            Assert.True(second.IsNowPlaying);
            Assert.False(first.IsNowPlaying);
            Assert.Same(second, vm.NowPlayingListTrack);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public async Task AddExternalTrack_PromotesPlayingTransient_MarkerLandsOnLibraryInstance()
    {
        var (vm, audio, trackChanged) = CreateVmWithTrackChannel();
        try
        {
            // DJ 点歌以瞬态身份在播（不在库中）→ 无标记可落
            var transientPlaying = new Track { SourceId = "kugou:x", Title = "甲", Artist = "歌手" };
            audio.Setup(x => x.CurrentTrack).Returns(transientPlaying);
            trackChanged.OnNext(transientPlaying);
            await Task.Delay(50);
            Assert.Null(vm.NowPlayingListTrack);

            // 「+」转正：新解析实例入库（AddTracks 合并不发 TrackChanged），补发标记须落到库内本体
            var promoted = new Track { SourceId = "kugou:x", Title = "甲", Artist = "歌手" };
            vm.AddExternalTrack(promoted);

            Assert.True(promoted.IsNowPlaying);
            Assert.Same(promoted, vm.NowPlayingListTrack);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [Fact]
    public void FormatTrackInfo_JoinsTitleAndArtist()
    {
        Assert.Equal("晴天 - 周杰伦", PlaylistViewModel.FormatTrackInfo("晴天", "周杰伦"));
        Assert.Equal("晴天", PlaylistViewModel.FormatTrackInfo("晴天", ""));
        Assert.Equal("晴天", PlaylistViewModel.FormatTrackInfo("晴天", null));
    }
}
