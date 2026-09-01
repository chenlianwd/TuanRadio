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
using Moq;
using Xunit;
using PlaylistViewModel = AIRadio.Desktop.ViewModels.PlaylistViewModel;

namespace AIRadio.Desktop.Tests;

public class PlaylistViewModelTests
{
    private static (PlaylistViewModel vm, Mock<IAudioService> audioMock, Mock<IMusicSearchService> searchMock)
        CreateVm(string? playlistFile = null, Func<string, string, Task>? writeAllTextAsync = null)
    {
        var audioMock = new Mock<IAudioService>();
        audioMock.Setup(x => x.TrackEnded).Returns(new System.Reactive.Subjects.Subject<Track?>());
        audioMock.Setup(x => x.StateChanged).Returns(new System.Reactive.Subjects.Subject<PlaybackState>());
        audioMock.Setup(x => x.Playlist).Returns(new List<Track>().AsReadOnly());

        var searchMock = new Mock<IMusicSearchService>();
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
        await Task.Delay(150);

        var saved = await File.ReadAllTextAsync(playlistFile);
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
    public void PlayFavoriteCommand_PlaysMatchingTrack()
    {
        var (vm, audioMock, _) = CreateVm();
        var track1 = new Track { Title = "A", FilePath = "" };
        var track2 = new Track { Title = "B", FilePath = "" };
        vm.Tracks.Add(track1);
        vm.Tracks.Add(track2);

        vm.PlayFavoriteCommand.Execute(track1).Subscribe();

        audioMock.Verify(x => x.PlayTrack(track1), Times.Once);
    }

    [Fact]
    public void PlayFavoriteCommand_DoesNothingWhenTrackNotInPlaylist()
    {
        var (vm, audioMock, _) = CreateVm();
        var track = new Track { Title = "NotInList", FilePath = "" };
        vm.Tracks.Add(new Track { Title = "A", FilePath = "" });

        vm.PlayFavoriteCommand.Execute(track).Subscribe();

        audioMock.Verify(x => x.PlayTrack(It.IsAny<Track>()), Times.Never);
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
    public Task SearchAsync_ClearsAndFillsSearchResults()
    {
        var (vm, _, searchMock) = CreateVm();
        var results = new List<OnlineTrack>
        {
            new OnlineTrack { Id = "1", Title = "Song A" },
            new OnlineTrack { Id = "2", Title = "Song B" }
        };
        searchMock.Setup(x => x.SearchAsync("test", 20))
            .ReturnsAsync(results);

        vm.SearchText = "test";
        vm.SearchCommand.Execute().Subscribe();

        // Wait a moment for async to complete
        System.Threading.Thread.Sleep(100);

        Assert.Equal(2, vm.SearchResults.Count);
        Assert.Equal(2, vm.TabIndex); // auto-switches to search tab
        return Task.CompletedTask;
    }

    [Fact]
    public void SearchAsync_DoesNothingWhenTextEmpty()
    {
        var (vm, _, searchMock) = CreateVm();
        vm.SearchCommand.Execute().Subscribe();
        System.Threading.Thread.Sleep(50);
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
}
