using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Hue.Configuration;
using Jellyfin.Plugin.Hue.Service;
using Xunit;

namespace Jellyfin.Plugin.Hue.Tests;

public sealed class HueSyncServiceTests
{
    [Fact]
    public void IsSupportedVideoPlaybackItem_RejectsAudioAndAcceptsVideoMediaTypes()
    {
        Assert.True(HueSyncService.IsSupportedVideoPlaybackItem(new MediaBrowser.Controller.Entities.Video()));
        Assert.False(HueSyncService.IsSupportedVideoPlaybackItem(new MediaBrowser.Controller.Entities.Folder()));
        Assert.False(HueSyncService.IsSupportedVideoPlaybackItem(null));
    }

    [Fact]
    public void IsSupportedPlaybackItem_AcceptsAudioWithoutTreatingItAsVideo()
    {
        var audio = new MediaBrowser.Controller.Entities.Audio.Audio();

        Assert.True(HueSyncService.IsAudioPlaybackItem(audio));
        Assert.True(HueSyncService.IsSupportedPlaybackItem(audio));
        Assert.False(HueSyncService.IsSupportedVideoPlaybackItem(audio));
    }

    [Theory]
    [InlineData(PluginConfiguration.PlaybackMediaFilterAllVideo, true, true, true)]
    [InlineData(PluginConfiguration.PlaybackMediaFilterMovies, true, false, false)]
    [InlineData(PluginConfiguration.PlaybackMediaFilterEpisodes, false, true, false)]
    [InlineData(PluginConfiguration.PlaybackMediaFilterOtherVideo, false, false, true)]
    [InlineData("movies", true, false, false)]
    [InlineData("unknown", true, true, true)]
    public void MatchesPlaybackMediaFilter_SelectsConfiguredVideoKinds(
        string filter,
        bool expectedMovie,
        bool expectedEpisode,
        bool expectedOtherVideo)
    {
        var movie = new MediaBrowser.Controller.Entities.Movies.Movie();
        var episode = new MediaBrowser.Controller.Entities.TV.Episode();
        var otherVideo = new MediaBrowser.Controller.Entities.Video();

        Assert.Equal(expectedMovie, HueSyncService.MatchesPlaybackMediaFilter(movie, filter));
        Assert.Equal(expectedEpisode, HueSyncService.MatchesPlaybackMediaFilter(episode, filter));
        Assert.Equal(expectedOtherVideo, HueSyncService.MatchesPlaybackMediaFilter(otherVideo, filter));
        Assert.False(HueSyncService.MatchesPlaybackMediaFilter(new MediaBrowser.Controller.Entities.Folder(), filter));
        Assert.False(HueSyncService.MatchesPlaybackMediaFilter(null, filter));
    }

    [Fact]
    public void MatchesPlaybackMediaFilter_UsesTheEffectivePerUserScope()
    {
        var userId = Guid.NewGuid();
        var config = new PluginConfiguration
        {
            PlaybackMediaFilter = PluginConfiguration.PlaybackMediaFilterMovies,
            UserMappings = new List<UserBridgeMapping>
            {
                new() { UserId = userId.ToString(), PlaybackMediaFilterOverride = PluginConfiguration.PlaybackMediaFilterEpisodes }
            }
        };

        Assert.Equal(PluginConfiguration.PlaybackMediaFilterEpisodes, config.GetPlaybackMediaFilterForUser(userId));
        Assert.True(HueSyncService.MatchesPlaybackMediaFilter(
            new MediaBrowser.Controller.Entities.TV.Episode(),
            config.GetPlaybackMediaFilterForUser(userId)));
        Assert.False(HueSyncService.MatchesPlaybackMediaFilter(
            new MediaBrowser.Controller.Entities.Movies.Movie(),
            config.GetPlaybackMediaFilterForUser(userId)));
    }

    [Theory]
    [InlineData(PluginConfiguration.PlaybackMediaFilterAllVideo, false)]
    [InlineData(PluginConfiguration.PlaybackMediaFilterAudio, true)]
    [InlineData(PluginConfiguration.PlaybackMediaFilterAllMedia, true)]
    [InlineData("audio", true)]
    public void MatchesPlaybackMediaFilter_SelectsAudioScopes(
        string filter,
        bool expectedAudio)
    {
        var audio = new MediaBrowser.Controller.Entities.Audio.Audio();

        Assert.Equal(expectedAudio, HueSyncService.MatchesPlaybackMediaFilter(audio, filter));
        Assert.False(HueSyncService.MatchesPlaybackMediaFilter(
            new MediaBrowser.Controller.Entities.Movies.Movie(),
            PluginConfiguration.PlaybackMediaFilterAudio));
    }

    [Fact]
    public void AnalyzeAudioSamplesAndBuildAudioColorsAreDeterministicAndSpatial()
    {
        var pcm = new byte[800 * 2 * 2];
        for (var index = 0; index < pcm.Length; index += 4)
        {
            pcm[index] = 0xff;
            pcm[index + 1] = 0x1f;
            pcm[index + 2] = 0xff;
            pcm[index + 3] = 0x1f;
        }

        var energy = HueSyncService.AnalyzeAudioSamples(pcm, pcm.Length);
        Assert.InRange(energy.Rms, 0.24, 0.27);
        Assert.True(energy.Low >= 0);
        Assert.True(energy.Mid >= 0);
        Assert.True(energy.High >= 0);

        var lights = new Dictionary<int, (double x, double z)>
        {
            [1] = (-1, 0),
            [2] = (1, 0)
        };
        var first = HueSyncService.BuildAudioChannelColors(lights, energy, frameIndex: 4);
        var second = HueSyncService.BuildAudioChannelColors(lights, energy, frameIndex: 4);

        Assert.Equal(first[1], second[1]);
        Assert.Equal(first[2], second[2]);
        Assert.NotEqual(first[1], first[2]);
    }

    [Fact]
    public void AnalyzeAudioSamples_UsesConfiguredFrequencyCenters()
    {
        const int sampleRate = 8000;
        const int sampleCount = 8000;
        var pcm = new byte[sampleCount * 2 * 2];
        for (var index = 0; index < sampleCount; index++)
        {
            var sample = (short)(Math.Sin(2 * Math.PI * 700 * index / sampleRate) * 16000);
            var offset = index * 4;
            pcm[offset] = (byte)(sample & 0xff);
            pcm[offset + 1] = (byte)((sample >> 8) & 0xff);
            pcm[offset + 2] = pcm[offset];
            pcm[offset + 3] = pcm[offset + 1];
        }

        var defaultProfile = HueSyncService.AnalyzeAudioSamples(pcm, pcm.Length);
        var tunedProfile = HueSyncService.AnalyzeAudioSamples(
            pcm,
            pcm.Length,
            sampleRate,
            channels: 2,
            lowFrequencyHz: 70,
            midFrequencyHz: 700,
            highFrequencyHz: 1800);

        Assert.True(tunedProfile.Mid > 0.4);
        Assert.True(tunedProfile.Mid > defaultProfile.Mid + 0.35);
        Assert.Equal(tunedProfile, HueSyncService.AnalyzeAudioSamples(
            pcm,
            pcm.Length,
            sampleRate,
            channels: 2,
            lowFrequencyHz: 70,
            midFrequencyHz: 700,
            highFrequencyHz: 1800));
    }

    [Fact]
    public void AnalyzeAudioSamples_UsesConfiguredBandSpreadForBetweenCenterContent()
    {
        const int sampleRate = 8000;
        const int sampleCount = 8000;
        var pcm = new byte[sampleCount * 2 * 2];
        for (var index = 0; index < sampleCount; index++)
        {
            var sample = (short)(Math.Sin(2 * Math.PI * 770 * index / sampleRate) * 16000);
            var offset = index * 4;
            pcm[offset] = (byte)(sample & 0xff);
            pcm[offset + 1] = (byte)((sample >> 8) & 0xff);
            pcm[offset + 2] = pcm[offset];
            pcm[offset + 3] = pcm[offset + 1];
        }

        var narrowProfile = HueSyncService.AnalyzeAudioSamples(
            pcm,
            pcm.Length,
            sampleRate,
            channels: 2,
            lowFrequencyHz: 70,
            midFrequencyHz: 700,
            highFrequencyHz: 1800,
            audioBandSpreadPercent: 0);
        var spreadProfile = HueSyncService.AnalyzeAudioSamples(
            pcm,
            pcm.Length,
            sampleRate,
            channels: 2,
            lowFrequencyHz: 70,
            midFrequencyHz: 700,
            highFrequencyHz: 1800,
            audioBandSpreadPercent: 10);

        Assert.True(spreadProfile.Mid > 0.05);
        Assert.True(spreadProfile.Mid > narrowProfile.Mid + 0.05);
        Assert.Equal(spreadProfile, HueSyncService.AnalyzeAudioSamples(
            pcm,
            pcm.Length,
            sampleRate,
            channels: 2,
            lowFrequencyHz: 70,
            midFrequencyHz: 700,
            highFrequencyHz: 1800,
            audioBandSpreadPercent: 10));
    }

    [Fact]
    public void AnalyzeAudioChannelSamples_PreservesStereoSourceEnergy()
    {
        const int sampleRate = 8000;
        const int sampleCount = 8000;
        var pcm = new byte[sampleCount * 2 * 2];
        for (var index = 0; index < sampleCount; index++)
        {
            var left = (short)(Math.Sin(2 * Math.PI * 700 * index / sampleRate) * 16000);
            var right = (short)(Math.Sin(2 * Math.PI * 1800 * index / sampleRate) * 16000);
            var offset = index * 4;
            pcm[offset] = (byte)(left & 0xff);
            pcm[offset + 1] = (byte)((left >> 8) & 0xff);
            pcm[offset + 2] = (byte)(right & 0xff);
            pcm[offset + 3] = (byte)((right >> 8) & 0xff);
        }

        var analysis = HueSyncService.AnalyzeAudioChannelSamples(
            pcm,
            pcm.Length,
            sampleRate,
            channels: 2,
            lowFrequencyHz: 70,
            midFrequencyHz: 700,
            highFrequencyHz: 1800);

        Assert.True(analysis.LeftMid > 0.4);
        Assert.True(analysis.RightHigh > 0.4);
        Assert.True(analysis.LeftHigh < 0.1);
        Assert.True(analysis.RightMid < 0.1);
        Assert.Equal(analysis.MixedEnergy, HueSyncService.AnalyzeAudioSamples(
            pcm,
            pcm.Length,
            sampleRate,
            channels: 2,
            lowFrequencyHz: 70,
            midFrequencyHz: 700,
            highFrequencyHz: 1800));
    }

    [Fact]
    public void ApplyAudioNoiseGate_SuppressesOnlySubThresholdWindows()
    {
        var analysis = new HueSyncService.AudioChannelAnalysis(
            Rms: 0.25,
            Low: 0.1,
            Mid: 0.2,
            High: 0.3,
            LeftRms: 0.3,
            LeftLow: 0.1,
            LeftMid: 0.2,
            LeftHigh: 0.3,
            RightRms: 0.2,
            RightLow: 0.1,
            RightMid: 0.2,
            RightHigh: 0.3);

        Assert.Equal(analysis, HueSyncService.ApplyAudioNoiseGate(analysis, 0));
        Assert.Equal(analysis, HueSyncService.ApplyAudioNoiseGate(analysis, 25));
        Assert.Equal(new HueSyncService.AudioChannelAnalysis(), HueSyncService.ApplyAudioNoiseGate(analysis, 26));
        Assert.Equal(new HueSyncService.AudioChannelAnalysis(), HueSyncService.ApplyAudioNoiseGate(analysis, 100));
    }

    [Fact]
    public void ApplyAudioBandGains_ScalesMixedAndSourceBandsWithoutChangingRms()
    {
        var analysis = new HueSyncService.AudioChannelAnalysis(
            Rms: 0.4,
            Low: 0.1,
            Mid: 0.2,
            High: 0.3,
            LeftRms: 0.3,
            LeftLow: 0.11,
            LeftMid: 0.21,
            LeftHigh: 0.31,
            RightRms: 0.2,
            RightLow: 0.12,
            RightMid: 0.22,
            RightHigh: 0.32);

        var neutral = HueSyncService.ApplyAudioBandGains(analysis);
        var tuned = HueSyncService.ApplyAudioBandGains(analysis, 0, 150, 200);

        Assert.Equal(analysis, neutral);
        Assert.Equal(analysis.Rms, tuned.Rms);
        Assert.Equal(0, tuned.Low);
        Assert.Equal(0.3, tuned.Mid, 6);
        Assert.Equal(0.6, tuned.High, 6);
        Assert.Equal(0, tuned.LeftLow);
        Assert.Equal(0.315, tuned.LeftMid, 6);
        Assert.Equal(0.64, tuned.RightHigh, 6);
    }

    [Fact]
    public void BuildAudioChannelColors_UsesStereoSourceOnlyForSpatialRouting()
    {
        const int sampleRate = 8000;
        const int sampleCount = 8000;
        var pcm = new byte[sampleCount * 2 * 2];
        for (var index = 0; index < sampleCount; index++)
        {
            var left = (short)(Math.Sin(2 * Math.PI * 700 * index / sampleRate) * 16000);
            var right = (short)(Math.Sin(2 * Math.PI * 1800 * index / sampleRate) * 16000);
            var offset = index * 4;
            pcm[offset] = (byte)(left & 0xff);
            pcm[offset + 1] = (byte)((left >> 8) & 0xff);
            pcm[offset + 2] = (byte)(right & 0xff);
            pcm[offset + 3] = (byte)((right >> 8) & 0xff);
        }

        var analysis = HueSyncService.AnalyzeAudioChannelSamples(
            pcm,
            pcm.Length,
            sampleRate,
            channels: 2,
            lowFrequencyHz: 70,
            midFrequencyHz: 700,
            highFrequencyHz: 1800);
        var lights = new Dictionary<int, (double x, double z)>
        {
            [1] = (-1, 0),
            [2] = (1, 0)
        };
        var stereo = HueSyncService.BuildAudioChannelColors(
            lights,
            analysis.MixedEnergy,
            frameIndex: 5,
            audioColorPalette: PluginConfiguration.AudioColorPaletteBand,
            audioSpatialMode: PluginConfiguration.AudioSpatialModeSpatial,
            audioChannelMode: PluginConfiguration.AudioChannelModeStereo,
            audioChannelAnalysis: analysis);
        var uniform = HueSyncService.BuildAudioChannelColors(
            lights,
            analysis.MixedEnergy,
            frameIndex: 5,
            audioColorPalette: PluginConfiguration.AudioColorPaletteBand,
            audioSpatialMode: PluginConfiguration.AudioSpatialModeUniform,
            audioChannelMode: PluginConfiguration.AudioChannelModeStereo,
            audioChannelAnalysis: analysis);

        Assert.NotEqual(stereo[1], stereo[2]);
        Assert.Equal(uniform[1], uniform[2]);
    }

    [Fact]
    public void BuildAudioChannelColors_CanIsolateLeftOrRightSourceInSpatialMode()
    {
        var lights = new Dictionary<int, (double x, double z)> { [1] = (0, 0) };
        var analysis = new HueSyncService.AudioChannelAnalysis(
            Rms: 0.5,
            Low: 0.1,
            Mid: 0.2,
            High: 0.3,
            LeftRms: 0.8,
            LeftLow: 0.2,
            LeftMid: 0.7,
            LeftHigh: 0.1,
            RightRms: 0.2,
            RightLow: 0.1,
            RightMid: 0.1,
            RightHigh: 0.8);

        var left = HueSyncService.BuildAudioChannelColors(
            lights,
            analysis.MixedEnergy,
            frameIndex: 2,
            audioColorPalette: PluginConfiguration.AudioColorPaletteBand,
            audioSpatialMode: PluginConfiguration.AudioSpatialModeSpatial,
            audioChannelMode: PluginConfiguration.AudioChannelModeLeft,
            audioChannelAnalysis: analysis);
        var right = HueSyncService.BuildAudioChannelColors(
            lights,
            analysis.MixedEnergy,
            frameIndex: 2,
            audioColorPalette: PluginConfiguration.AudioColorPaletteBand,
            audioSpatialMode: PluginConfiguration.AudioSpatialModeSpatial,
            audioChannelMode: PluginConfiguration.AudioChannelModeRight,
            audioChannelAnalysis: analysis);
        var uniformLeft = HueSyncService.BuildAudioChannelColors(
            lights,
            analysis.MixedEnergy,
            frameIndex: 2,
            audioColorPalette: PluginConfiguration.AudioColorPaletteBand,
            audioSpatialMode: PluginConfiguration.AudioSpatialModeUniform,
            audioChannelMode: PluginConfiguration.AudioChannelModeLeft,
            audioChannelAnalysis: analysis);
        var uniformRight = HueSyncService.BuildAudioChannelColors(
            lights,
            analysis.MixedEnergy,
            frameIndex: 2,
            audioColorPalette: PluginConfiguration.AudioColorPaletteBand,
            audioSpatialMode: PluginConfiguration.AudioSpatialModeUniform,
            audioChannelMode: PluginConfiguration.AudioChannelModeRight,
            audioChannelAnalysis: analysis);

        Assert.NotEqual(left[1], right[1]);
        Assert.True(left[1][1] > left[1][2]);
        Assert.True(right[1][2] > right[1][1]);
        Assert.Equal(uniformLeft[1], uniformRight[1]);
    }

    [Fact]
    public void CalculateAudioBeatPulse_RespondsOnlyToRisingEnergy()
    {
        var previous = (Rms: 0.2, Low: 0.1, Mid: 0.2, High: 0.3);
        var current = (Rms: 0.3, Low: 0.2, Mid: 0.25, High: 0.35);

        var pulse = HueSyncService.CalculateAudioBeatPulse(previous, current, 100);
        Assert.InRange(pulse, 0.39, 0.41);
        Assert.Equal(0, HueSyncService.CalculateAudioBeatPulse(previous, current, 0));
        Assert.Equal(0, HueSyncService.CalculateAudioBeatPulse(current, previous, 100));
        Assert.Equal(0, HueSyncService.CalculateAudioBeatPulse(null, current, 100));

        var lights = new Dictionary<int, (double x, double z)> { [1] = (0, 0) };
        var steady = HueSyncService.BuildAudioChannelColors(lights, current, frameIndex: 3);
        var pulsed = HueSyncService.BuildAudioChannelColors(lights, current, frameIndex: 3, audioBeatPulse: pulse);
        Assert.True(pulsed[1].Average(channel => channel) > steady[1].Average(channel => channel));
    }

    [Fact]
    public void CalculateAudioBeatPulse_AppliesBoundedReleaseTail()
    {
        var energy = (Rms: 0.2, Low: 0.1, Mid: 0.2, High: 0.3);
        var rising = (Rms: 0.3, Low: 0.2, Mid: 0.25, High: 0.35);
        var attack = HueSyncService.CalculateAudioBeatPulse(energy, rising, 100);

        var instant = HueSyncService.CalculateAudioBeatPulse(rising, rising, 100, attack, 0);
        var halfRelease = HueSyncService.CalculateAudioBeatPulse(rising, rising, 100, attack, 50);
        var longestRelease = HueSyncService.CalculateAudioBeatPulse(rising, rising, 100, attack, 100);

        Assert.Equal(0, instant);
        Assert.InRange(halfRelease, attack * 0.47, attack * 0.48);
        Assert.InRange(longestRelease, attack * 0.94, attack * 0.96);
        Assert.True(longestRelease > halfRelease);
        Assert.True(longestRelease < attack);
    }

    [Fact]
    public void CalculateAudioBeatPulse_AppliesOnsetThresholdWithoutChangingDefaultBehavior()
    {
        var previous = (Rms: 0.2, Low: 0.1, Mid: 0.2, High: 0.3);
        var current = (Rms: 0.3, Low: 0.2, Mid: 0.25, High: 0.35);

        var unfiltered = HueSyncService.CalculateAudioBeatPulse(previous, current, 100);
        var thresholded = HueSyncService.CalculateAudioBeatPulse(previous, current, 100, 0, 0, 5);
        var suppressed = HueSyncService.CalculateAudioBeatPulse(previous, current, 100, 0, 0, 10);

        Assert.InRange(unfiltered, 0.39, 0.41);
        Assert.InRange(thresholded, 0.19, 0.21);
        Assert.Equal(0, suppressed);
    }

    [Fact]
    public void BuildAudioChannelColors_UsesAudioSensitivityWithoutChangingSpatialMapping()
    {
        var lights = new Dictionary<int, (double x, double z)> { [1] = (0, 0) };
        var energy = (Rms: 0.1, Low: 0.2, Mid: 0.1, High: 0.05);

        var lowSensitivity = HueSyncService.BuildAudioChannelColors(lights, energy, frameIndex: 3, audioSensitivityPercent: 25);
        var highSensitivity = HueSyncService.BuildAudioChannelColors(lights, energy, frameIndex: 3, audioSensitivityPercent: 400);

        Assert.True(lowSensitivity[1].Average(channel => channel) < highSensitivity[1].Average(channel => channel));
        Assert.Equal(
            HueSyncService.BuildAudioChannelColors(lights, energy, frameIndex: 3, audioSensitivityPercent: 25)[1],
            HueSyncService.BuildAudioChannelColors(lights, energy, frameIndex: 3, audioSensitivityPercent: 0)[1]);
    }

    [Fact]
    public void BuildAudioChannelColors_SupportsDistinctAudioPalettes()
    {
        var lights = new Dictionary<int, (double x, double z)> { [1] = (-0.7, 0) };
        var energy = (Rms: 0.2, Low: 0.35, Mid: 0.2, High: 0.1);

        var spectrum = HueSyncService.BuildAudioChannelColors(
            lights, energy, frameIndex: 7, audioColorPalette: PluginConfiguration.AudioColorPaletteSpectrum)[1];
        var band = HueSyncService.BuildAudioChannelColors(
            lights, energy, frameIndex: 7, audioColorPalette: PluginConfiguration.AudioColorPaletteBand)[1];
        var warm = HueSyncService.BuildAudioChannelColors(
            lights, energy, frameIndex: 7, audioColorPalette: PluginConfiguration.AudioColorPaletteWarm)[1];
        var cool = HueSyncService.BuildAudioChannelColors(
            lights, energy, frameIndex: 7, audioColorPalette: PluginConfiguration.AudioColorPaletteCool)[1];
        var monochrome = HueSyncService.BuildAudioChannelColors(
            lights, energy, frameIndex: 7, audioColorPalette: PluginConfiguration.AudioColorPaletteMonochrome)[1];

        Assert.NotEqual(spectrum, band);
        Assert.NotEqual(warm, cool);
        Assert.Equal(monochrome[0], monochrome[1]);
        Assert.Equal(monochrome[1], monochrome[2]);
        Assert.NotEqual(band[0], band[1]);
        Assert.Equal(
            spectrum,
            HueSyncService.BuildAudioChannelColors(lights, energy, frameIndex: 7, audioColorPalette: "invalid")[1]);
    }

    [Fact]
    public void BuildAudioChannelColors_SupportsSpatialUniformAndMirrorRouting()
    {
        var lights = new Dictionary<int, (double x, double z)>
        {
            [1] = (-1, 0),
            [2] = (1, 0),
            [3] = (0, 0)
        };
        var energy = (Rms: 0.25, Low: 0.35, Mid: 0.2, High: 0.1);

        var spatial = HueSyncService.BuildAudioChannelColors(
            lights,
            energy,
            frameIndex: 11,
            audioColorPalette: PluginConfiguration.AudioColorPaletteBand,
            audioSpatialMode: PluginConfiguration.AudioSpatialModeSpatial);
        var uniform = HueSyncService.BuildAudioChannelColors(
            lights,
            energy,
            frameIndex: 11,
            audioColorPalette: PluginConfiguration.AudioColorPaletteBand,
            audioSpatialMode: PluginConfiguration.AudioSpatialModeUniform);
        var mirror = HueSyncService.BuildAudioChannelColors(
            lights,
            energy,
            frameIndex: 11,
            audioColorPalette: PluginConfiguration.AudioColorPaletteBand,
            audioSpatialMode: PluginConfiguration.AudioSpatialModeMirror);

        Assert.NotEqual(spatial[1], spatial[2]);
        Assert.Equal(uniform[1], uniform[2]);
        Assert.Equal(uniform[2], uniform[3]);
        Assert.Equal(mirror[1], mirror[2]);
        Assert.NotEqual(mirror[1], mirror[3]);
        Assert.Equal(
            spatial[1],
            HueSyncService.BuildAudioChannelColors(
                lights,
                energy,
                frameIndex: 11,
                audioColorPalette: PluginConfiguration.AudioColorPaletteBand,
                audioSpatialMode: "invalid")[1]);
    }

    [Fact]
    public void IsPlaybackSeek_RecognizesBackwardAndLargeForwardJumps()
    {
        var start = DateTime.UtcNow;
        var previous = TimeSpan.FromSeconds(100).Ticks;

        Assert.True(HueSyncService.IsPlaybackSeek(
            previous,
            start,
            TimeSpan.FromSeconds(90).Ticks,
            start.AddSeconds(1)));

        Assert.True(HueSyncService.IsPlaybackSeek(
            previous,
            start,
            TimeSpan.FromSeconds(140).Ticks,
            start.AddSeconds(1)));
    }

    [Fact]
    public void IsPlaybackSeek_IgnoresNormalProgressAndIncompleteSamples()
    {
        var start = DateTime.UtcNow;

        Assert.False(HueSyncService.IsPlaybackSeek(
            TimeSpan.FromSeconds(100).Ticks,
            start,
            TimeSpan.FromSeconds(110).Ticks,
            start.AddSeconds(10)));

        Assert.False(HueSyncService.IsPlaybackSeek(
            null,
            start,
            TimeSpan.FromSeconds(110).Ticks,
            start.AddSeconds(1)));

        Assert.False(HueSyncService.IsPlaybackSeek(
            TimeSpan.FromSeconds(100).Ticks,
            start,
            TimeSpan.FromSeconds(90).Ticks,
            default));
    }

    [Theory]
    [InlineData(true, false, null, "session-a", true)]
    [InlineData(true, true, "session-a", "session-a", false)]
    [InlineData(true, true, "session-a", "session-b", true)]
    [InlineData(false, true, "session-a", "session-a", false)]
    public void ShouldCaptureLightState_PreservesSnapshotForSamePlaybackSession(
        bool restoreLightState,
        bool hasSavedLightStates,
        string? savedLightStatePlaySessionId,
        string playSessionId,
        bool expected)
    {
        Assert.Equal(
            expected,
            HueSyncService.ShouldCaptureLightState(
                restoreLightState,
                hasSavedLightStates,
                savedLightStatePlaySessionId,
                playSessionId));
    }

    [Fact]
    public void ShouldCaptureLightState_DimPausePolicyCapturesWithoutFinalRestoration()
    {
        Assert.True(HueSyncService.ShouldCaptureLightState(
            restoreLightState: false,
            hasSavedLightStates: false,
            savedLightStatePlaySessionId: null,
            playSessionId: "session-a",
            captureForPause: true));
        Assert.False(HueSyncService.ShouldCaptureLightState(
            restoreLightState: false,
            hasSavedLightStates: true,
            savedLightStatePlaySessionId: "session-a",
            playSessionId: "session-a",
            captureForPause: true));
    }

    [Theory]
    [InlineData(PluginConfiguration.PauseBehaviorDimToCinemaLevel, true)]
    [InlineData("dimtocinemalevel", true)]
    [InlineData(PluginConfiguration.PauseBehaviorKeepLastColors, false)]
    [InlineData(PluginConfiguration.PauseBehaviorRestoreLightState, false)]
    public void IsPauseBehaviorDimToCinemaLevel_RecognizesOnlyDimPolicy(string value, bool expected)
    {
        Assert.Equal(expected, HueSyncService.IsPauseBehaviorDimToCinemaLevel(value));
    }

    [Fact]
    public void ResolvePlaybackSettings_UsesPerUserOverridesAndGlobalFallback()
    {
        var userId = System.Guid.NewGuid();
        var configuration = new PluginConfiguration
        {
            UseCinemaMode = true,
            BrightnessDimLevel = 40,
            RestoreLightState = true,
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = userId.ToString(),
                    UseCinemaModeOverride = false,
                    BrightnessDimLevelOverride = 10,
                    RestoreLightStateOverride = false
                }
            }
        };

        var effective = HueSyncService.ResolvePlaybackSettings(configuration, userId);
        var fallback = HueSyncService.ResolvePlaybackSettings(configuration, System.Guid.NewGuid());

        Assert.False(effective.UseCinemaMode);
        Assert.Equal(10, effective.BrightnessDimLevel);
        Assert.False(effective.RestoreLightState);
        Assert.True(fallback.UseCinemaMode);
        Assert.Equal(40, fallback.BrightnessDimLevel);
        Assert.True(fallback.RestoreLightState);
    }

    [Fact]
    public void ResolvePauseBehavior_UsesPerUserOverrideAndGlobalFallback()
    {
        var userId = System.Guid.NewGuid();
        var configuration = new PluginConfiguration
        {
            PauseBehavior = PluginConfiguration.PauseBehaviorKeepLastColors,
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = userId.ToString(),
                    PauseBehaviorOverride = PluginConfiguration.PauseBehaviorRestoreLightState
                }
            }
        };

        var effective = HueSyncService.ResolvePauseBehavior(configuration, userId);
        var fallback = HueSyncService.ResolvePauseBehavior(configuration, System.Guid.NewGuid());

        Assert.Equal(PluginConfiguration.PauseBehaviorRestoreLightState, effective);
        Assert.Equal(PluginConfiguration.PauseBehaviorKeepLastColors, fallback);
    }

    [Theory]
    [InlineData(PluginConfiguration.BlackoutBehaviorKeepLastColors, true)]
    [InlineData("keeplastcolors", true)]
    [InlineData(PluginConfiguration.BlackoutBehaviorBlackout, false)]
    [InlineData(null, false)]
    public void ShouldPreserveBlackoutColors_RecognizesKeepLastColorsPolicy(string? behavior, bool expected)
    {
        Assert.Equal(expected, HueSyncService.ShouldPreserveBlackoutColors(behavior));
    }

    [Fact]
    public void ResolvePerformanceSettings_UsesPerUserOverridesAndGlobalFallback()
    {
        var userId = System.Guid.NewGuid();
        var configuration = new PluginConfiguration
        {
            TargetFps = 20,
            FrameResolution = PluginConfiguration.FrameResolutionStandard,
            VideoScalingMode = PluginConfiguration.VideoScalingModeStretch,
            VideoDeinterlaceMode = PluginConfiguration.VideoDeinterlaceModeOff,
            SamplingBreadthPercent = 15,
            SamplingMode = PluginConfiguration.SamplingModeAverage,
            ColorSmoothingPercent = 0,
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = userId.ToString(),
                    TargetFpsOverride = 30,
                    FrameResolutionOverride = PluginConfiguration.FrameResolutionHigh,
                    VideoScalingModeOverride = PluginConfiguration.VideoScalingModeFit,
                    VideoDeinterlaceModeOverride = PluginConfiguration.VideoDeinterlaceModeAuto,
                    SamplingBreadthPercentOverride = 25,
                    SamplingModeOverride = PluginConfiguration.SamplingModeCenterWeighted,
                    ColorSmoothingPercentOverride = 40
                }
            }
        };

        var effective = HueSyncService.ResolvePerformanceSettings(configuration, userId);
        var fallback = HueSyncService.ResolvePerformanceSettings(configuration, System.Guid.NewGuid());

        Assert.Equal(30, effective.TargetFps);
        Assert.Equal(PluginConfiguration.FrameResolutionHigh, effective.FrameResolution);
        Assert.Equal(PluginConfiguration.VideoScalingModeFit, effective.VideoScalingMode);
        Assert.Equal(PluginConfiguration.VideoDeinterlaceModeAuto, effective.VideoDeinterlaceMode);
        Assert.Equal(25, effective.SamplingBreadthPercent);
        Assert.Equal(PluginConfiguration.SamplingModeCenterWeighted, effective.SamplingMode);
        Assert.Equal(40, effective.ColorSmoothingPercent);
        Assert.Equal(20, fallback.TargetFps);
        Assert.Equal(PluginConfiguration.FrameResolutionStandard, fallback.FrameResolution);
        Assert.Equal(PluginConfiguration.VideoScalingModeStretch, fallback.VideoScalingMode);
        Assert.Equal(PluginConfiguration.VideoDeinterlaceModeOff, fallback.VideoDeinterlaceMode);
        Assert.Equal(15, fallback.SamplingBreadthPercent);
        Assert.Equal(PluginConfiguration.SamplingModeAverage, fallback.SamplingMode);
        Assert.Equal(0, fallback.ColorSmoothingPercent);
    }

    [Fact]
    public void ResolveAudioSensitivityPercent_UsesPerUserOverrideAndGlobalFallback()
    {
        var userId = System.Guid.NewGuid();
        var configuration = new PluginConfiguration
        {
            AudioSensitivityPercent = 140,
            UserMappings = new List<UserBridgeMapping>
            {
                new() { UserId = userId.ToString(), AudioSensitivityPercentOverride = 280 }
            }
        };

        Assert.Equal(280, HueSyncService.ResolveAudioSensitivityPercent(configuration, userId));
        Assert.Equal(140, HueSyncService.ResolveAudioSensitivityPercent(configuration, System.Guid.NewGuid()));

        configuration.AudioSensitivityPercent = 999;
        Assert.Equal(PluginConfiguration.MaxAudioSensitivityPercent,
            HueSyncService.ResolveAudioSensitivityPercent(configuration, System.Guid.NewGuid()));
    }

    [Fact]
    public void ResolveAudioNoiseGatePercent_UsesPerUserOverrideAndGlobalFallback()
    {
        var userId = System.Guid.NewGuid();
        var configuration = new PluginConfiguration
        {
            AudioNoiseGatePercent = 15,
            UserMappings = new List<UserBridgeMapping>
            {
                new() { UserId = userId.ToString(), AudioNoiseGatePercentOverride = 45 }
            }
        };

        Assert.Equal(45, HueSyncService.ResolveAudioNoiseGatePercent(configuration, userId));
        Assert.Equal(15, HueSyncService.ResolveAudioNoiseGatePercent(configuration, System.Guid.NewGuid()));

        configuration.UserMappings[0].AudioNoiseGatePercentOverride = 999;
        Assert.Equal(
            PluginConfiguration.MaxAudioNoiseGatePercent,
            HueSyncService.ResolveAudioNoiseGatePercent(configuration, userId));

        configuration.UserMappings.Clear();
        configuration.AudioNoiseGatePercent = -1;
        Assert.Equal(
            PluginConfiguration.MinAudioNoiseGatePercent,
            HueSyncService.ResolveAudioNoiseGatePercent(configuration, userId));
    }

    [Fact]
    public void ResolveAudioBandGains_UsesPerUserOverrideAndClampsInvalidValues()
    {
        var userId = System.Guid.NewGuid();
        var configuration = new PluginConfiguration
        {
            AudioLowGainPercent = 80,
            AudioMidGainPercent = 110,
            AudioHighGainPercent = 140,
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = userId.ToString(),
                    AudioLowGainPercentOverride = 40,
                    AudioMidGainPercentOverride = 160,
                    AudioHighGainPercentOverride = 200
                }
            }
        };

        Assert.Equal((40, 160, 200), HueSyncService.ResolveAudioBandGains(configuration, userId));
        Assert.Equal((80, 110, 140), HueSyncService.ResolveAudioBandGains(configuration, System.Guid.NewGuid()));

        configuration.UserMappings[0].AudioLowGainPercentOverride = -1;
        configuration.UserMappings[0].AudioMidGainPercentOverride = 999;
        Assert.Equal(
            (PluginConfiguration.MinAudioBandGainPercent,
                PluginConfiguration.MaxAudioBandGainPercent,
                PluginConfiguration.MaxAudioBandGainPercent),
            HueSyncService.ResolveAudioBandGains(configuration, userId));
    }

    [Fact]
    public void ResolveAudioResponseSmoothingPercent_UsesPerUserOverrideAndClampsInvalidValues()
    {
        var userId = System.Guid.NewGuid();
        var configuration = new PluginConfiguration
        {
            AudioResponseSmoothingPercent = 25,
            UserMappings = new List<UserBridgeMapping>
            {
                new() { UserId = userId.ToString(), AudioResponseSmoothingPercentOverride = 70 }
            }
        };

        Assert.Equal(70, HueSyncService.ResolveAudioResponseSmoothingPercent(configuration, userId));
        Assert.Equal(25, HueSyncService.ResolveAudioResponseSmoothingPercent(configuration, System.Guid.NewGuid()));

        configuration.UserMappings[0].AudioResponseSmoothingPercentOverride = 999;
        Assert.Equal(
            PluginConfiguration.MaxAudioResponseSmoothingPercent,
            HueSyncService.ResolveAudioResponseSmoothingPercent(configuration, userId));

        configuration.UserMappings.Clear();
        configuration.AudioResponseSmoothingPercent = -1;
        Assert.Equal(
            PluginConfiguration.MinAudioResponseSmoothingPercent,
            HueSyncService.ResolveAudioResponseSmoothingPercent(configuration, userId));
    }

    [Fact]
    public void ResolveAudioFrequencies_UsesPerUserOverrideAndGlobalFallback()
    {
        var userId = System.Guid.NewGuid();
        var configuration = new PluginConfiguration
        {
            AudioLowFrequencyHz = 80,
            AudioMidFrequencyHz = 500,
            AudioHighFrequencyHz = 1800,
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = userId.ToString(),
                    AudioLowFrequencyHzOverride = 60,
                    AudioMidFrequencyHzOverride = 700,
                    AudioHighFrequencyHzOverride = 2400
                }
            }
        };

        Assert.Equal((60, 700, 2400), HueSyncService.ResolveAudioFrequencies(configuration, userId));
        Assert.Equal((80, 500, 1800), HueSyncService.ResolveAudioFrequencies(configuration, System.Guid.NewGuid()));

        configuration.UserMappings[0].AudioLowFrequencyHzOverride = 900;
        configuration.UserMappings[0].AudioMidFrequencyHzOverride = 700;
        Assert.Equal(
            (PluginConfiguration.DefaultAudioLowFrequencyHz,
                PluginConfiguration.DefaultAudioMidFrequencyHz,
                PluginConfiguration.DefaultAudioHighFrequencyHz),
            HueSyncService.ResolveAudioFrequencies(configuration, userId));

        configuration.UserMappings.Clear();
        configuration.AudioLowFrequencyHz = PluginConfiguration.MaxAudioFrequencyHz + 1;
        Assert.Equal(
            (PluginConfiguration.DefaultAudioLowFrequencyHz,
                PluginConfiguration.DefaultAudioMidFrequencyHz,
                PluginConfiguration.DefaultAudioHighFrequencyHz),
            HueSyncService.ResolveAudioFrequencies(configuration, userId));
    }

    [Fact]
    public void ResolveAudioBandSpreadPercent_UsesPerUserOverrideAndGlobalFallback()
    {
        var userId = System.Guid.NewGuid();
        var configuration = new PluginConfiguration
        {
            AudioBandSpreadPercent = 10,
            UserMappings = new List<UserBridgeMapping>
            {
                new() { UserId = userId.ToString(), AudioBandSpreadPercentOverride = 35 }
            }
        };

        Assert.Equal(35, HueSyncService.ResolveAudioBandSpreadPercent(configuration, userId));
        Assert.Equal(10, HueSyncService.ResolveAudioBandSpreadPercent(configuration, System.Guid.NewGuid()));

        configuration.UserMappings[0].AudioBandSpreadPercentOverride = 999;
        Assert.Equal(
            PluginConfiguration.MaxAudioBandSpreadPercent,
            HueSyncService.ResolveAudioBandSpreadPercent(configuration, userId));

        configuration.UserMappings.Clear();
        configuration.AudioBandSpreadPercent = -1;
        Assert.Equal(
            PluginConfiguration.MinAudioBandSpreadPercent,
            HueSyncService.ResolveAudioBandSpreadPercent(configuration, userId));
    }

    [Fact]
    public void ResolveAudioBeatPulsePercent_UsesPerUserOverrideAndGlobalFallback()
    {
        var userId = System.Guid.NewGuid();
        var configuration = new PluginConfiguration
        {
            AudioBeatPulsePercent = 10,
            UserMappings = new List<UserBridgeMapping>
            {
                new() { UserId = userId.ToString(), AudioBeatPulsePercentOverride = 65 }
            }
        };

        Assert.Equal(65, HueSyncService.ResolveAudioBeatPulsePercent(configuration, userId));
        Assert.Equal(10, HueSyncService.ResolveAudioBeatPulsePercent(configuration, System.Guid.NewGuid()));

        configuration.UserMappings[0].AudioBeatPulsePercentOverride = 999;
        Assert.Equal(
            PluginConfiguration.MaxAudioBeatPulsePercent,
            HueSyncService.ResolveAudioBeatPulsePercent(configuration, userId));

        configuration.UserMappings.Clear();
        configuration.AudioBeatPulsePercent = -1;
        Assert.Equal(
            PluginConfiguration.MinAudioBeatPulsePercent,
            HueSyncService.ResolveAudioBeatPulsePercent(configuration, userId));
    }

    [Fact]
    public void ResolveAudioBeatPulseDecayPercent_UsesPerUserOverrideAndGlobalFallback()
    {
        var userId = System.Guid.NewGuid();
        var configuration = new PluginConfiguration
        {
            AudioBeatPulseDecayPercent = 10,
            UserMappings = new List<UserBridgeMapping>
            {
                new() { UserId = userId.ToString(), AudioBeatPulseDecayPercentOverride = 65 }
            }
        };

        Assert.Equal(65, HueSyncService.ResolveAudioBeatPulseDecayPercent(configuration, userId));
        Assert.Equal(10, HueSyncService.ResolveAudioBeatPulseDecayPercent(configuration, System.Guid.NewGuid()));

        configuration.UserMappings[0].AudioBeatPulseDecayPercentOverride = 999;
        Assert.Equal(
            PluginConfiguration.MaxAudioBeatPulseDecayPercent,
            HueSyncService.ResolveAudioBeatPulseDecayPercent(configuration, userId));

        configuration.UserMappings.Clear();
        configuration.AudioBeatPulseDecayPercent = -1;
        Assert.Equal(
            PluginConfiguration.MinAudioBeatPulseDecayPercent,
            HueSyncService.ResolveAudioBeatPulseDecayPercent(configuration, userId));
    }

    [Fact]
    public void ResolveAudioBeatPulseThresholdPercent_UsesPerUserOverrideAndGlobalFallback()
    {
        var userId = System.Guid.NewGuid();
        var configuration = new PluginConfiguration
        {
            AudioBeatPulseThresholdPercent = 10,
            UserMappings = new List<UserBridgeMapping>
            {
                new() { UserId = userId.ToString(), AudioBeatPulseThresholdPercentOverride = 65 }
            }
        };

        Assert.Equal(65, HueSyncService.ResolveAudioBeatPulseThresholdPercent(configuration, userId));
        Assert.Equal(10, HueSyncService.ResolveAudioBeatPulseThresholdPercent(configuration, System.Guid.NewGuid()));

        configuration.UserMappings[0].AudioBeatPulseThresholdPercentOverride = 999;
        Assert.Equal(
            PluginConfiguration.MaxAudioBeatPulseThresholdPercent,
            HueSyncService.ResolveAudioBeatPulseThresholdPercent(configuration, userId));

        configuration.UserMappings.Clear();
        configuration.AudioBeatPulseThresholdPercent = -1;
        Assert.Equal(
            PluginConfiguration.MinAudioBeatPulseThresholdPercent,
            HueSyncService.ResolveAudioBeatPulseThresholdPercent(configuration, userId));
    }

    [Fact]
    public void ResolveAudioColorPalette_UsesPerUserOverrideAndGlobalFallback()
    {
        var userId = System.Guid.NewGuid();
        var configuration = new PluginConfiguration
        {
            AudioColorPalette = PluginConfiguration.AudioColorPaletteWarm,
            UserMappings = new List<UserBridgeMapping>
            {
                new() { UserId = userId.ToString(), AudioColorPaletteOverride = PluginConfiguration.AudioColorPaletteBand }
            }
        };

        Assert.Equal(
            PluginConfiguration.AudioColorPaletteBand,
            HueSyncService.ResolveAudioColorPalette(configuration, userId));
        Assert.Equal(
            PluginConfiguration.AudioColorPaletteWarm,
            HueSyncService.ResolveAudioColorPalette(configuration, System.Guid.NewGuid()));

        configuration.UserMappings[0].AudioColorPaletteOverride = "invalid";
        Assert.Equal(
            PluginConfiguration.AudioColorPaletteWarm,
            HueSyncService.ResolveAudioColorPalette(configuration, userId));

        configuration.UserMappings.Clear();
        configuration.AudioColorPalette = "invalid";
        Assert.Equal(
            PluginConfiguration.AudioColorPaletteSpectrum,
            HueSyncService.ResolveAudioColorPalette(configuration, userId));
    }

    [Fact]
    public void ResolveAudioSpatialMode_UsesPerUserOverrideAndGlobalFallback()
    {
        var userId = System.Guid.NewGuid();
        var configuration = new PluginConfiguration
        {
            AudioSpatialMode = PluginConfiguration.AudioSpatialModeMirror,
            UserMappings = new List<UserBridgeMapping>
            {
                new() { UserId = userId.ToString(), AudioSpatialModeOverride = PluginConfiguration.AudioSpatialModeUniform }
            }
        };

        Assert.Equal(
            PluginConfiguration.AudioSpatialModeUniform,
            HueSyncService.ResolveAudioSpatialMode(configuration, userId));
        Assert.Equal(
            PluginConfiguration.AudioSpatialModeMirror,
            HueSyncService.ResolveAudioSpatialMode(configuration, System.Guid.NewGuid()));

        configuration.UserMappings[0].AudioSpatialModeOverride = "invalid";
        Assert.Equal(
            PluginConfiguration.AudioSpatialModeMirror,
            HueSyncService.ResolveAudioSpatialMode(configuration, userId));

        configuration.UserMappings.Clear();
        configuration.AudioSpatialMode = "invalid";
        Assert.Equal(
            PluginConfiguration.AudioSpatialModeSpatial,
            HueSyncService.ResolveAudioSpatialMode(configuration, userId));
    }

    [Fact]
    public void ResolveAudioChannelMode_UsesPerUserOverrideAndGlobalFallback()
    {
        var userId = System.Guid.NewGuid();
        var configuration = new PluginConfiguration
        {
            AudioChannelMode = PluginConfiguration.AudioChannelModeStereo,
            UserMappings = new List<UserBridgeMapping>
            {
                new() { UserId = userId.ToString(), AudioChannelModeOverride = PluginConfiguration.AudioChannelModeMono }
            }
        };

        Assert.Equal(
            PluginConfiguration.AudioChannelModeMono,
            HueSyncService.ResolveAudioChannelMode(configuration, userId));
        Assert.Equal(
            PluginConfiguration.AudioChannelModeStereo,
            HueSyncService.ResolveAudioChannelMode(configuration, System.Guid.NewGuid()));

        configuration.UserMappings[0].AudioChannelModeOverride = "invalid";
        Assert.Equal(
            PluginConfiguration.AudioChannelModeStereo,
            HueSyncService.ResolveAudioChannelMode(configuration, userId));

        configuration.UserMappings.Clear();
        configuration.AudioChannelMode = "invalid";
        Assert.Equal(
            PluginConfiguration.AudioChannelModeMono,
            HueSyncService.ResolveAudioChannelMode(configuration, userId));
    }

    [Fact]
    public void ResolveColorProcessingSettings_UsesPerUserThresholdsAndGlobalFallback()
    {
        var userId = System.Guid.NewGuid();
        var configuration = new PluginConfiguration
        {
            BrightnessBoost = 110,
            RedGain = 95,
            GreenGain = 105,
            BlueGain = 115,
            ColorSaturation = 90,
            HueShiftDegrees = 20,
            OutputBrightnessPercent = 80,
            GammaCorrection = 1.1,
            BlackoutThreshold = 15,
            BlackoutBehavior = PluginConfiguration.BlackoutBehaviorBlackout,
            ColorChangeThreshold = 10,
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = userId.ToString(),
                    BrightnessBoostOverride = 150,
                    RedGainOverride = 120,
                    GreenGainOverride = 90,
                    BlueGainOverride = 130,
                    ColorSaturationOverride = 125,
                    HueShiftDegreesOverride = -45,
                    OutputBrightnessPercentOverride = 70,
                    GammaCorrectionOverride = 1.35,
                    BlackoutThresholdOverride = 35,
                    BlackoutBehaviorOverride = PluginConfiguration.BlackoutBehaviorKeepLastColors,
                    ColorChangeThresholdOverride = 4
                }
            }
        };

        var effective = HueSyncService.ResolveColorProcessingSettings(configuration, userId);
        var fallback = HueSyncService.ResolveColorProcessingSettings(configuration, System.Guid.NewGuid());

        Assert.Equal(150, effective.BrightnessBoost);
        Assert.Equal(120, effective.RedGain);
        Assert.Equal(90, effective.GreenGain);
        Assert.Equal(130, effective.BlueGain);
        Assert.Equal(125, effective.ColorSaturation);
        Assert.Equal(-45, effective.HueShiftDegrees);
        Assert.Equal(70, effective.OutputBrightnessPercent);
        Assert.Equal(1.35, effective.GammaCorrection, precision: 6);
        Assert.Equal(35, effective.BlackoutThreshold);
        Assert.Equal(PluginConfiguration.BlackoutBehaviorKeepLastColors, effective.BlackoutBehavior);
        Assert.Equal(4, effective.ColorChangeThreshold);
        Assert.Equal(110, fallback.BrightnessBoost);
        Assert.Equal(95, fallback.RedGain);
        Assert.Equal(105, fallback.GreenGain);
        Assert.Equal(115, fallback.BlueGain);
        Assert.Equal(90, fallback.ColorSaturation);
        Assert.Equal(20, fallback.HueShiftDegrees);
        Assert.Equal(80, fallback.OutputBrightnessPercent);
        Assert.Equal(1.1, fallback.GammaCorrection, precision: 6);
        Assert.Equal(15, fallback.BlackoutThreshold);
        Assert.Equal(PluginConfiguration.BlackoutBehaviorBlackout, fallback.BlackoutBehavior);
        Assert.Equal(10, fallback.ColorChangeThreshold);
    }

    [Fact]
    public void ResolveExecutionSettings_UsesPerUserOverridesAndGlobalFallback()
    {
        var userId = System.Guid.NewGuid();
        var configuration = new PluginConfiguration
        {
            UseGpu = true,
            CustomFfmpegFlags = "-threads 2",
            FfmpegStallTimeoutSeconds = 5,
            NetworkRetryAttempts = 3,
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = userId.ToString(),
                    UseGpuOverride = false,
                    CustomFfmpegFlagsOverride = "  -hwaccel vaapi  ",
                    FfmpegStallTimeoutSecondsOverride = 20,
                    NetworkRetryAttemptsOverride = 0
                }
            }
        };

        var effective = HueSyncService.ResolveExecutionSettings(configuration, userId);
        var fallback = HueSyncService.ResolveExecutionSettings(configuration, System.Guid.NewGuid());

        Assert.Equal((bool?)false, effective.UseGpu);
        Assert.Equal("-hwaccel vaapi", effective.CustomFfmpegFlags);
        Assert.Equal(20, effective.FfmpegStallTimeoutSeconds);
        Assert.Equal(0, effective.NetworkRetryAttempts);
        Assert.Equal((bool?)true, fallback.UseGpu);
        Assert.Equal("-threads 2", fallback.CustomFfmpegFlags);
        Assert.Equal(5, fallback.FfmpegStallTimeoutSeconds);
        Assert.Equal(3, fallback.NetworkRetryAttempts);
    }

    [Fact]
    public void ResolveChannelIds_UsesPerUserSelectionAndGlobalFallback()
    {
        var userId = System.Guid.NewGuid();
        var configuration = new PluginConfiguration
        {
            ChannelIds = "5, 1, 5",
            UserMappings = new List<UserBridgeMapping>
            {
                new()
                {
                    UserId = userId.ToString(),
                    ChannelIdsOverride = "9, 2, 9"
                }
            }
        };

        var selected = HueSyncService.ResolveChannelIds(configuration, userId);
        var fallback = HueSyncService.ResolveChannelIds(configuration, System.Guid.NewGuid());

        Assert.NotNull(selected);
        Assert.Equal(2, selected!.Count);
        Assert.Contains(2, selected);
        Assert.Contains(9, selected);
        Assert.NotNull(fallback);
        Assert.Equal(new[] { 1, 5 }, fallback!.OrderBy(channelId => channelId));
    }

    [Theory]
    [InlineData(0, 18)]
    [InlineData(1, 1)]
    [InlineData(15, 18)]
    [InlineData(50, 62)]
    [InlineData(100, 62)]
    public void CalculateSamplingDistance_NormalizesBreadth(int samplingBreadthPercent, int expectedDistance)
    {
        Assert.Equal(expectedDistance, HueSyncService.CalculateSamplingDistance(samplingBreadthPercent));
    }

    [Theory]
    [InlineData(80, 45, 9)]
    [InlineData(160, 90, 18)]
    [InlineData(320, 180, 37)]
    public void CalculateSamplingDistance_ScalesWithFrameResolution(
        int frameWidth,
        int frameHeight,
        int expectedDistance)
    {
        Assert.Equal(
            expectedDistance,
            HueSyncService.CalculateSamplingDistance(15, frameWidth, frameHeight));
    }

    [Fact]
    public void SampleRegionColor_CenterPixelReturnsMappedPixel()
    {
        var frame = new byte[160 * 90 * 3];
        var index = (45 * 160 + 80) * 3;
        frame[index] = 231;
        frame[index + 1] = 87;
        frame[index + 2] = 14;

        var sampled = HueSyncService.SampleRegionColor(
            frame,
            80,
            45,
            18,
            "centerpixel");

        Assert.Equal(new byte[] { 231, 87, 14 }, sampled);
    }

    [Fact]
    public void SampleRegionColor_SupportsHighResolutionFrames()
    {
        var frame = new byte[320 * 180 * 3];
        var index = (90 * 320 + 160) * 3;
        frame[index] = 12;
        frame[index + 1] = 34;
        frame[index + 2] = 56;

        var sampled = HueSyncService.SampleRegionColor(
            frame,
            160,
            90,
            37,
            PluginConfiguration.SamplingModeCenterPixel,
            320,
            180);

        Assert.Equal(new byte[] { 12, 34, 56 }, sampled);
    }

    [Fact]
    public void SampleRegionColor_AveragePreservesNeighborhoodMean()
    {
        var frame = new byte[160 * 90 * 3];
        for (var y = 43; y < 47; y++)
        {
            for (var x = 78; x < 82; x++)
            {
                var index = (y * 160 + x) * 3;
                frame[index] = 10;
                frame[index + 1] = 20;
                frame[index + 2] = 30;
            }
        }

        var centerIndex = (45 * 160 + 80) * 3;
        frame[centerIndex] = 250;
        frame[centerIndex + 1] = 100;
        frame[centerIndex + 2] = 50;

        var sampled = HueSyncService.SampleRegionColor(
            frame,
            80,
            45,
            2,
            PluginConfiguration.SamplingModeAverage);

        Assert.Equal(new byte[] { 25, 25, 31 }, sampled);
    }

    [Fact]
    public void SampleRegionColor_CenterWeightedFavorsMappedPixel()
    {
        var frame = new byte[160 * 90 * 3];
        var centerIndex = (45 * 160 + 80) * 3;
        frame[centerIndex] = byte.MaxValue;

        var average = HueSyncService.SampleRegionColor(
            frame,
            80,
            45,
            2,
            PluginConfiguration.SamplingModeAverage);
        var weighted = HueSyncService.SampleRegionColor(
            frame,
            80,
            45,
            2,
            PluginConfiguration.SamplingModeCenterWeighted);

        Assert.True(weighted[0] > average[0]);
        Assert.Equal(new byte[] { 0, 0 }, weighted[1..]);
    }

    [Fact]
    public void SampleRegionColor_UnknownModeFallsBackToAverage()
    {
        var frame = new byte[160 * 90 * 3];
        for (var y = 44; y < 46; y++)
        {
            for (var x = 79; x < 81; x++)
            {
                var index = (y * 160 + x) * 3;
                frame[index] = 40;
                frame[index + 1] = 50;
                frame[index + 2] = 60;
            }
        }

        var expected = HueSyncService.SampleRegionColor(frame, 80, 45, 1, "Average");
        var sampled = HueSyncService.SampleRegionColor(frame, 80, 45, 1, "not-a-mode");

        Assert.Equal(expected, sampled);
    }

    [Fact]
    public void ApplyTemporalSmoothing_BlendsPreviousFrameByConfiguredWeight()
    {
        var current = new Dictionary<int, byte[]>
        {
            [1] = new byte[] { 1, 100, 200 }
        };
        var previous = new Dictionary<int, byte[]>
        {
            [1] = new byte[] { 101, 50, 0 }
        };

        var smoothed = HueSyncService.ApplyTemporalSmoothing(current, previous, 50);

        Assert.Equal(new byte[] { 51, 75, 100 }, smoothed[1]);
    }

    [Fact]
    public void ApplyTemporalSmoothing_UsesCurrentFrameWhenHistoryIsMissing()
    {
        var current = new Dictionary<int, byte[]>
        {
            [1] = new byte[] { 10, 20, 30 }
        };

        var smoothed = HueSyncService.ApplyTemporalSmoothing(
            current,
            new Dictionary<int, byte[]>(),
            90);

        Assert.Equal(new byte[] { 10, 20, 30 }, smoothed[1]);
    }

    [Fact]
    public void ApplyAudioResponseSmoothing_BlendsAllMixedAndSourceBands()
    {
        var current = new HueSyncService.AudioChannelAnalysis(
            0.8,
            0.6,
            0.4,
            0.2,
            0.7,
            0.5,
            0.3,
            0.1,
            0.9,
            0.7,
            0.5,
            0.3);
        var previous = new HueSyncService.AudioChannelAnalysis(
            0.2,
            0.2,
            0.2,
            0.2,
            0.1,
            0.1,
            0.1,
            0.1,
            0.3,
            0.3,
            0.3,
            0.3);

        var smoothed = HueSyncService.ApplyAudioResponseSmoothing(current, previous, 50);

        Assert.Equal(0.5, smoothed.Rms, 10);
        Assert.Equal(0.4, smoothed.Low, 10);
        Assert.Equal(0.3, smoothed.Mid, 10);
        Assert.Equal(0.2, smoothed.High, 10);
        Assert.Equal(0.4, smoothed.LeftRms, 10);
        Assert.Equal(0.3, smoothed.LeftLow, 10);
        Assert.Equal(0.2, smoothed.LeftMid, 10);
        Assert.Equal(0.1, smoothed.LeftHigh, 10);
        Assert.Equal(0.6, smoothed.RightRms, 10);
        Assert.Equal(0.5, smoothed.RightLow, 10);
        Assert.Equal(0.4, smoothed.RightMid, 10);
        Assert.Equal(0.3, smoothed.RightHigh, 10);
    }

    [Fact]
    public void ApplyAudioResponseSmoothing_DefaultAndMissingHistoryPreserveCurrentAnalysis()
    {
        var current = new HueSyncService.AudioChannelAnalysis(
            0.8,
            0.6,
            0.4,
            0.2,
            0.7,
            0.5,
            0.3,
            0.1,
            0.9,
            0.7,
            0.5,
            0.3);

        Assert.Equal(current, HueSyncService.ApplyAudioResponseSmoothing(current, null));
        Assert.Equal(current, HueSyncService.ApplyAudioResponseSmoothing(current, current, 0));
    }

    [Fact]
    public void ApplyAudioResponseSmoothing_ClampsStrengthToSafeMaximum()
    {
        var current = new HueSyncService.AudioChannelAnalysis(
            1,
            1,
            1,
            1,
            1,
            1,
            1,
            1,
            1,
            1,
            1,
            1);
        var previous = new HueSyncService.AudioChannelAnalysis();

        var smoothed = HueSyncService.ApplyAudioResponseSmoothing(current, previous, 100);

        Assert.Equal(0.1, smoothed.Rms, 10);
        Assert.Equal(0.1, smoothed.Low, 10);
        Assert.Equal(0.1, smoothed.RightHigh, 10);
    }

    [Fact]
    public void ApplyTemporalSmoothing_ClampsStrengthToSafeMaximum()
    {
        var current = new Dictionary<int, byte[]>
        {
            [1] = new byte[] { 100, 100, 100 }
        };
        var previous = new Dictionary<int, byte[]>
        {
            [1] = new byte[] { 0, 0, 0 }
        };

        var smoothed = HueSyncService.ApplyTemporalSmoothing(current, previous, 100);

        Assert.Equal(new byte[] { 10, 10, 10 }, smoothed[1]);
    }

    [Theory]
    [InlineData(255, 100, 255)]
    [InlineData(255, 50, 127.5)]
    [InlineData(255, 0, 0)]
    [InlineData(-10, 100, 0)]
    [InlineData(300, 100, 255)]
    public void ApplyOutputBrightness_ScalesAndClamps(
        double channel,
        int outputBrightnessPercent,
        double expected)
    {
        Assert.Equal(
            expected,
            HueSyncService.ApplyOutputBrightness(channel, outputBrightnessPercent),
            precision: 6);
    }

    [Theory]
    [InlineData(0, 90, 0.25)]
    [InlineData(0.9, 90, 0.15)]
    [InlineData(0.1, -90, 0.85)]
    [InlineData(0.5, 180, 0)]
    public void ApplyHueShift_RotatesAndWrapsNormalizedHue(
        double hue,
        int hueShiftDegrees,
        double expected)
    {
        Assert.Equal(
            expected,
            HueSyncService.ApplyHueShift(hue, hueShiftDegrees),
            precision: 6);
    }
}
