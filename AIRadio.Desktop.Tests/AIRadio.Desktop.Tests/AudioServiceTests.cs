using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AIRadio.Desktop.Models;
using AIRadio.Desktop.Services;
using Xunit;

namespace AIRadio.Desktop.Tests;

public class AudioServiceTests
{
    [Fact]
    public void NewInstance_HasEmptyPlaylist()
    {
        var svc = new AudioService();
        Assert.Empty(svc.Playlist);
        Assert.Null(svc.CurrentTrack);
        svc.Dispose();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var svc = new AudioService();

        svc.Dispose();

        var exception = Record.Exception(svc.Dispose);

        Assert.Null(exception);
        Assert.False(svc.IsPlaying);
        Assert.Equal(TimeSpan.Zero, svc.CurrentPosition);
        Assert.Equal(TimeSpan.Zero, svc.Duration);
    }

    [Fact]
    public void Dispose_AfterTtsPlaybackAttempt_IsSafe()
    {
        var svc = new AudioService();
        try
        {
            svc.PlayTtsAudio(Array.Empty<byte>());

            var exception = Record.Exception(svc.Dispose);

            Assert.Null(exception);
        }
        finally
        {
            svc.Dispose();
        }
    }

    [Fact]
    public void LoadTracks_SetsCurrentTrackToFirst()
    {
        var svc = new AudioService();
        var tracks = new List<Track>
        {
            new Track { Title = "A", FilePath = "" },
            new Track { Title = "B", FilePath = "" }
        };

        svc.LoadTracks(tracks);

        Assert.Equal(2, svc.Playlist.Count);
        Assert.Equal("A", svc.Playlist[0].Title);
        svc.Dispose();
    }

    [Fact]
    public void AddTracks_AppendsToExisting()
    {
        var svc = new AudioService();
        svc.LoadTracks(new[] { new Track { Title = "A", FilePath = "" } });

        svc.AddTracks(new[] { new Track { Title = "B", FilePath = "" } });

        Assert.Equal(2, svc.Playlist.Count);
        Assert.Equal("B", svc.Playlist[1].Title);
        svc.Dispose();
    }

    [Fact]
    public void RemoveTrack_RemovesFromPlaylist()
    {
        var svc = new AudioService();
        var track = new Track { Title = "Test", FilePath = "" };
        svc.LoadTracks(new[] { track });

        svc.RemoveTrack(track);

        Assert.Empty(svc.Playlist);
    }

    [Fact]
    public void ClearPlaylist_RemovesAllTracks()
    {
        var svc = new AudioService();
        svc.LoadTracks(new[]
        {
            new Track { Title = "A", FilePath = "" },
            new Track { Title = "B", FilePath = "" }
        });

        svc.ClearPlaylist();

        Assert.Empty(svc.Playlist);
    }

    [Fact]
    public void Shuffle_TogglesShuffleState()
    {
        var svc = new AudioService();
        Assert.False(svc.IsShuffled);

        svc.Shuffle();
        Assert.True(svc.IsShuffled);

        svc.Shuffle();
        Assert.False(svc.IsShuffled);
        svc.Dispose();
    }

    [Fact]
    public void SetRepeatMode_ChangesRepeatMode()
    {
        var svc = new AudioService();
        Assert.Equal("radio", svc.RepeatMode);

        svc.SetRepeatMode("list");
        Assert.Equal("list", svc.RepeatMode);

        svc.SetRepeatMode("single");
        Assert.Equal("single", svc.RepeatMode);
        svc.Dispose();
    }

    [Fact]
    public void Volume_ClampsToValidRange()
    {
        var svc = new AudioService();
        svc.Volume = 1.5f;
        Assert.InRange(svc.Volume, 0.0f, 1.0f);

        svc.Volume = -0.5f;
        Assert.InRange(svc.Volume, 0.0f, 1.0f);

        svc.Volume = 0.5f;
        Assert.InRange(svc.Volume, 0.0f, 1.0f);
        svc.Dispose();
    }

    [Fact]
    public void Volume_ReturnsConfiguredUserVolumeWithoutPlayback()
    {
        var svc = new AudioService();
        try
        {
            svc.Volume = 0.5f;

            Assert.Equal(0.5f, svc.Volume, precision: 2);
        }
        finally
        {
            svc.Dispose();
        }
    }

    [Fact]
    public void PlayAtIndex_ClampsToValidRange()
    {
        var svc = new AudioService();
        svc.LoadTracks(new[] { new Track { Title = "A", FilePath = "" } });

        svc.PlayAtIndex(5);  // out of range
        Assert.NotNull(svc.CurrentTrack);

        svc.PlayAtIndex(-1); // negative
        Assert.NotNull(svc.CurrentTrack);

        svc.Dispose();
    }

    [Fact]
    public void StopTts_PublishesNotSpeakingState()
    {
        var svc = new AudioService();
        var states = new List<bool>();
        using var sub = svc.TtsStateChanged.Subscribe(states.Add);

        svc.StopTts();

        Assert.Contains(false, states);
        svc.Dispose();
    }

    [Fact]
    public async Task Next_RadioMode_DoesNotDuplicateTrackAlreadyAddedByCallback()
    {
        var svc = new AudioService();
        var recommended = new Track
        {
            Id = "recommended",
            SourceId = "test:recommended",
            Title = "Recommended",
            Artist = "DJ",
            FilePath = "http://example.com/recommended.mp3"
        };
        svc.LoadTracks(new[]
        {
            new Track
            {
                Id = "current",
                SourceId = "test:current",
                Title = "Current",
                Artist = "DJ",
                FilePath = "http://example.com/current.mp3"
            }
        });
        svc.SetRepeatMode("radio");
        svc.SetNextCallback(() =>
        {
            svc.AddTracks(new[] { recommended });
            return Task.FromResult<Track?>(recommended);
        });

        svc.Next();
        await Task.Delay(200);

        Assert.Equal(2, svc.Playlist.Count);
        Assert.Single(svc.Playlist.Where(t => t.SourceId == "test:recommended"));
        svc.Dispose();
    }

    [Fact]
    public async Task Next_RadioMode_FallsBackToPlaylistWhenRecommendationIsUnavailable()
    {
        var svc = new AudioService();
        try
        {
            svc.LoadTracks(new[]
            {
                new Track
                {
                    Title = "Current",
                    SourceId = "test:current",
                    FilePath = "http://example.com/current.mp3"
                },
                new Track
                {
                    Title = "Fallback",
                    SourceId = "test:fallback",
                    FilePath = "http://example.com/fallback.mp3"
                }
            });
            svc.SetRepeatMode("radio");
            svc.SetNextCallback(() => Task.FromResult<Track?>(null));

            svc.Next();
            await Task.Delay(100);

            Assert.Equal("Fallback", svc.CurrentTrack?.Title);
        }
        finally
        {
            svc.Dispose();
        }
    }

    [Fact]
    public void LooksLikeEarlyEnd_ReturnsTrueForLongTrackEndingTooSoon()
    {
        var svc = new AudioService();
        var startedField = typeof(AudioService).GetField("_trackStartedAtMs", BindingFlags.NonPublic | BindingFlags.Instance);
        var method = typeof(AudioService).GetMethod("LooksLikeEarlyEnd", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(startedField);
        Assert.NotNull(method);

        startedField!.SetValue(svc, Environment.TickCount64 - 30_000);
        var track = new Track { Title = "Long", Duration = TimeSpan.FromMinutes(4) };

        var result = (bool)method!.Invoke(svc, new object[] { track })!;

        Assert.True(result);
        svc.Dispose();
    }

    [Fact]
    public void LooksLikeEarlyEnd_ReturnsFalseForShortTracks()
    {
        var svc = new AudioService();
        var method = typeof(AudioService).GetMethod("LooksLikeEarlyEnd", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var track = new Track { Title = "Short", Duration = TimeSpan.FromSeconds(40) };

        var result = (bool)method!.Invoke(svc, new object[] { track })!;

        Assert.False(result);
        svc.Dispose();
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(5, 2)]
    public void GetEarlyEndRecoveryAction_UsesBoundedRecoverySequence(
        int completedRecoveryCount,
        int expectedAction)
    {
        var result = AudioService.GetEarlyEndRecoveryAction(completedRecoveryCount);

        Assert.Equal((AudioService.EarlyEndRecoveryAction)expectedAction, result);
    }

    [Fact]
    public async Task AlternativeSourceRetry_DoesNotApplyResultAfterPlaybackRequestChanges()
    {
        var svc = new AudioService();
        try
        {
            var original = new Track
            {
                Title = "Original",
                SourceId = "netease:123",
                FilePath = "https://trial.invalid/30s.mp3"
            };
            var selected = new Track
            {
                Title = "Selected",
                SourceId = "kuwo:789",
                FilePath = "https://selected.invalid/full.mp3"
            };
            svc.LoadTracks(new[] { original, selected });

            var resolverStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var resolverCompletion = new TaskCompletionSource<TrackUrlResolution?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            svc.SetFallbackTrackUrlResolver((_, _) =>
            {
                resolverStarted.TrySetResult(true);
                return resolverCompletion.Task;
            });

            var requestIdField = typeof(AudioService).GetField(
                "_playRequestId",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var currentIndexField = typeof(AudioService).GetField(
                "_currentIndex",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var recoveryScheduledField = typeof(AudioService).GetField(
                "_recoveryScheduled",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var scheduleMethod = typeof(AudioService).GetMethod(
                "ScheduleAlternativeSourceRetry",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(requestIdField);
            Assert.NotNull(currentIndexField);
            Assert.NotNull(recoveryScheduledField);
            Assert.NotNull(scheduleMethod);

            var requestId = (int)requestIdField!.GetValue(svc)!;
            scheduleMethod!.Invoke(svc, new object[] { 0, original, requestId });
            await resolverStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            requestIdField.SetValue(svc, requestId + 1);
            currentIndexField!.SetValue(svc, 1);
            resolverCompletion.SetResult(new TrackUrlResolution(
                "https://fallback.invalid/full.mp3",
                "fallback:456"));

            for (var attempt = 0;
                 attempt < 100 && (int)recoveryScheduledField!.GetValue(svc)! != 0;
                 attempt++)
            {
                await Task.Delay(10);
            }

            Assert.Equal(0, (int)recoveryScheduledField!.GetValue(svc)!);
            Assert.Same(selected, svc.CurrentTrack);
            Assert.Equal("netease:123", original.SourceId);
            Assert.Equal("https://trial.invalid/30s.mp3", original.FilePath);
        }
        finally
        {
            svc.Dispose();
        }
    }

    [Fact]
    public async Task RefreshBeforePlay_DoesNotApplyResolutionToStaleRequest()
    {
        var svc = new AudioService();
        try
        {
            var original = new Track
            {
                Title = "Original",
                SourceId = "netease:123",
                FilePath = "https://trial.invalid/30s.mp3"
            };
            var selected = new Track
            {
                Title = "Selected",
                SourceId = "kuwo:789",
                FilePath = "https://selected.invalid/full.mp3"
            };
            svc.LoadTracks(new[] { original, selected });

            var resolverCompletion = new TaskCompletionSource<TrackUrlResolution?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            svc.SetTrackUrlResolver((_, _) => resolverCompletion.Task);

            var requestIdField = typeof(AudioService).GetField(
                "_playRequestId",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var currentIndexField = typeof(AudioService).GetField(
                "_currentIndex",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var refreshMethod = typeof(AudioService).GetMethod(
                "RefreshAndPlayTrackAsync",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(requestIdField);
            Assert.NotNull(currentIndexField);
            Assert.NotNull(refreshMethod);

            var requestId = (int)requestIdField!.GetValue(svc)!;
            var refreshTask = (Task)refreshMethod!.Invoke(
                svc,
                new object[] { 0, original, requestId, true })!;

            requestIdField.SetValue(svc, requestId + 1);
            currentIndexField!.SetValue(svc, 1);
            resolverCompletion.SetResult(new TrackUrlResolution(
                "https://fallback.invalid/full.mp3",
                "fallback:456"));
            await refreshTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Same(selected, svc.CurrentTrack);
            Assert.Equal("netease:123", original.SourceId);
            Assert.Equal("https://trial.invalid/30s.mp3", original.FilePath);
        }
        finally
        {
            svc.Dispose();
        }
    }

    [Fact]
    public void RecoveryBudget_AnchorsOnceAndCapsSubsequentSteps()
    {
        var svc = new AudioService();
        try
        {
            // 未进入恢复流程：单步超时不受收紧
            Assert.Equal(TimeSpan.FromSeconds(8), svc.CapByRecoveryBudget(TimeSpan.FromSeconds(8)));

            svc.EnsureRecoveryDeadline();
            var firstCap = svc.CapByRecoveryBudget(TimeSpan.FromSeconds(60));
            Assert.True(firstCap > TimeSpan.Zero && firstCap <= TimeSpan.FromSeconds(12),
                $"cap should stay within total budget, got {firstCap}");

            // 重复锚定不得顺延预算：恢复流程中多次早停/错误共享同一窗口
            System.Threading.Thread.Sleep(50);
            svc.EnsureRecoveryDeadline();
            var secondCap = svc.CapByRecoveryBudget(TimeSpan.FromSeconds(60));
            Assert.True(secondCap <= firstCap, "re-anchoring must not extend the recovery budget");
            Assert.False(svc.IsRecoveryBudgetExhausted());
        }
        finally
        {
            svc.Dispose();
        }
    }

    [Fact]
    public void RecoveryBudget_SuccessfulPlaybackStartsFreshRecoveryWindow()
    {
        var svc = new AudioService();
        try
        {
            svc.EnsureRecoveryDeadline();
            var firstWindow = svc.CapByRecoveryBudget(TimeSpan.FromSeconds(60));
            Assert.True(firstWindow > TimeSpan.Zero && firstWindow <= TimeSpan.FromSeconds(12));

            svc.MarkRecoveryPlaybackStarted(nowMs: 1_000);
            Assert.False(svc.TryCompleteRecoveryAfterStablePlayback(nowMs: 3_999));
            Assert.True(svc.CapByRecoveryBudget(TimeSpan.FromSeconds(60)) <= firstWindow,
                "an immediate repeat failure must stay in the original recovery window");

            Assert.True(svc.TryCompleteRecoveryAfterStablePlayback(nowMs: 4_000));
            Assert.Equal(TimeSpan.FromSeconds(60), svc.CapByRecoveryBudget(TimeSpan.FromSeconds(60)));

            svc.EnsureRecoveryDeadline();
            var secondWindow = svc.CapByRecoveryBudget(TimeSpan.FromSeconds(60));
            Assert.True(secondWindow > TimeSpan.FromSeconds(11),
                $"a later independent failure should receive a fresh recovery window, got {secondWindow}");
        }
        finally
        {
            svc.Dispose();
        }
    }
}
