using Jellyfin.Plugin.Hue.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Hue.Tests;

public class PluginConfigurationTests
{
    [Fact]
    public void Validate_WhenSyncDisabled_ReturnsNoErrors()
    {
        // Arrange
        var config = new PluginConfiguration
        {
            SyncEnabled = false
            // All other fields can be invalid
        };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.Empty(errors);
        Assert.True(config.IsValid());
    }

    [Fact]
    public void Validate_WhenSyncEnabledWithValidConfig_ReturnsNoErrors()
    {
        // Arrange
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-app-key",
            HueClientKey = "test-client-key",
            EntertainmentAreaId = "test-area-id",
            TargetFps = 20,
            SamplingBreadthPercent = 15,
            BrightnessDimLevel = 30,
            BrightnessBoost = 100,
            ColorSaturation = 100,
            BlackoutThreshold = 15,
            ColorChangeThreshold = 10,
            NetworkRetryAttempts = 3,
            FfmpegStallTimeoutSeconds = 5
        };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.Empty(errors);
        Assert.True(config.IsValid());
    }

    [Fact]
    public void Validate_WhenHueBridgeIpMissing_ReturnsError()
    {
        // Arrange
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id"
        };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.Contains("Hue Bridge IP is required when sync is enabled", errors);
        Assert.False(config.IsValid());
    }

    [Fact]
    public void Validate_WhenHueBridgeIpInvalid_ReturnsError()
    {
        // Arrange
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "invalid-ip-address",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id"
        };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.Contains("Hue Bridge address must be a valid private IP address or .local host name", errors);
    }

    [Fact]
    public void Validate_WhenHueAppKeyMissing_ReturnsError()
    {
        // Arrange
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id"
        };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.Contains("Hue App Key is required. Use the 'Link Bridge' button to generate credentials", errors);
    }

    [Fact]
    public void Validate_WhenHueClientKeyMissing_ReturnsError()
    {
        // Arrange
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "",
            EntertainmentAreaId = "test-id"
        };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.Contains("Hue Client Key is required for streaming. Use the 'Link Bridge' button to generate credentials", errors);
    }

    [Fact]
    public void Validate_WhenEntertainmentAreaIdMissing_ReturnsError()
    {
        // Arrange
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = ""
        };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.Contains("Entertainment Area ID is required. Select an area from the dropdown or enter manually", errors);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(61)]
    [InlineData(100)]
    public void Validate_WhenTargetFpsOutOfRange_ReturnsError(int fps)
    {
        // Arrange
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            TargetFps = fps
        };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.Contains("Target FPS must be between 1 and 60", errors);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(60)]
    public void Validate_WhenTargetFpsValid_NoError(int fps)
    {
        // Arrange
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            TargetFps = fps
        };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.DoesNotContain("Target FPS", errors.ToString());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Validate_WhenBrightnessDimLevelOutOfRange_ReturnsError(int level)
    {
        // Arrange
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            BrightnessDimLevel = level
        };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.Contains("Brightness dim level must be between 0 and 100", errors);
    }

    [Theory]
    [InlineData(49)]
    [InlineData(201)]
    public void Validate_WhenBrightnessBoostOutOfRange_ReturnsError(int boost)
    {
        // Arrange
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            BrightnessBoost = boost
        };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.Contains("Brightness boost must be between 50 and 200", errors);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(201)]
    public void Validate_WhenColorSaturationOutOfRange_ReturnsError(int saturation)
    {
        // Arrange
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            ColorSaturation = saturation
        };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.Contains("Color saturation must be between 0 and 200", errors);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    public void Validate_WhenBlackoutThresholdOutOfRange_ReturnsError(int threshold)
    {
        // Arrange
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            BlackoutThreshold = threshold
        };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.Contains("Blackout threshold must be between 0 and 255", errors);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    public void Validate_WhenColorChangeThresholdOutOfRange_ReturnsError(int threshold)
    {
        // Arrange
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            ColorChangeThreshold = threshold
        };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.Contains("Color change threshold must be between 0 and 255", errors);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    public void Validate_WhenNetworkRetryAttemptsOutOfRange_ReturnsError(int attempts)
    {
        // Arrange
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            NetworkRetryAttempts = attempts
        };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.Contains("Network retry attempts must be between 0 and 10", errors);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public void Validate_WhenFfmpegStallTimeoutOutOfRange_ReturnsError(int timeout)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            FfmpegStallTimeoutSeconds = timeout
        };

        var errors = config.Validate();

        Assert.Contains("FFmpeg stall timeout must be between 1 and 60 seconds", errors);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(60)]
    public void Validate_WhenFfmpegStallTimeoutIsValid_ReturnsNoStallError(int timeout)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            FfmpegStallTimeoutSeconds = timeout
        };

        var errors = config.Validate();

        Assert.DoesNotContain("FFmpeg stall timeout must be between 1 and 60 seconds", errors);
    }

    [Fact]
    public void Validate_WithMultipleErrors_ReturnsAllErrors()
    {
        // Arrange
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "",
            HueAppKey = "",
            HueClientKey = "",
            EntertainmentAreaId = "",
            TargetFps = 0,
            BrightnessDimLevel = -1,
            BrightnessBoost = 300,
            ColorSaturation = -50
        };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.NotEmpty(errors);
        Assert.True(errors.Count >= 5); // At least 5 validation errors
    }

    [Fact]
    public void Constructor_SetsDefaultValues()
    {
        // Arrange & Act
        var config = new PluginConfiguration();

        // Assert
        Assert.False(config.SyncEnabled);
        Assert.True(config.UseCinemaMode);
        Assert.True(config.RestoreLightState);
        Assert.Equal(30, config.BrightnessDimLevel);
        Assert.Equal(20, config.TargetFps);
        Assert.Equal(PluginConfiguration.FrameResolutionStandard, config.FrameResolution);
        Assert.Equal(PluginConfiguration.VideoScalingModeStretch, config.VideoScalingMode);
        Assert.Equal(15, config.SamplingBreadthPercent);
        Assert.Equal(PluginConfiguration.SamplingModeAverage, config.SamplingMode);
        Assert.Equal(0, config.ColorSmoothingPercent);
        Assert.True(config.UseGpu);
        Assert.Equal(100, config.BrightnessBoost);
        Assert.Equal(100, config.ColorSaturation);
        Assert.Equal(15, config.BlackoutThreshold);
        Assert.Equal(10, config.ColorChangeThreshold);
        Assert.Equal(3, config.NetworkRetryAttempts);
        Assert.Equal(5, config.FfmpegStallTimeoutSeconds);
        Assert.Equal(PluginConfiguration.PauseBehaviorKeepLastColors, config.PauseBehavior);
        Assert.True(new UserBridgeMapping().SyncEnabled);
    }

    [Fact]
    public void GetFrameDimensions_ReturnsConfiguredResolutionPresets()
    {
        Assert.Equal((80, 45), PluginConfiguration.GetFrameDimensions(PluginConfiguration.FrameResolutionLow));
        Assert.Equal((160, 90), PluginConfiguration.GetFrameDimensions(PluginConfiguration.FrameResolutionStandard));
        Assert.Equal((320, 180), PluginConfiguration.GetFrameDimensions(PluginConfiguration.FrameResolutionHigh));
        Assert.Equal((160, 90), PluginConfiguration.GetFrameDimensions("unknown"));
    }

    [Theory]
    [InlineData("Unsupported")]
    [InlineData("")]
    public void Validate_WhenFrameResolutionIsInvalid_ReturnsError(string frameResolution)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            FrameResolution = frameResolution
        };

        var errors = config.Validate();

        Assert.Contains("Frame resolution must be 80x45, 160x90, or 320x180", errors);
    }

    [Theory]
    [InlineData(PluginConfiguration.FrameResolutionLow)]
    [InlineData(PluginConfiguration.FrameResolutionStandard)]
    [InlineData(PluginConfiguration.FrameResolutionHigh)]
    [InlineData("320X180")]
    public void Validate_WhenFrameResolutionIsValid_ReturnsNoResolutionError(string frameResolution)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            FrameResolution = frameResolution
        };

        var errors = config.Validate();

        Assert.DoesNotContain("Frame resolution must be 80x45, 160x90, or 320x180", errors);
    }

    [Theory]
    [InlineData("Unsupported")]
    [InlineData("")]
    public void Validate_WhenVideoScalingModeIsInvalid_ReturnsError(string videoScalingMode)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            VideoScalingMode = videoScalingMode
        };

        var errors = config.Validate();

        Assert.Contains("Video scaling mode must be Stretch, Fit, or Crop", errors);
    }

    [Theory]
    [InlineData(PluginConfiguration.VideoScalingModeStretch)]
    [InlineData(PluginConfiguration.VideoScalingModeFit)]
    [InlineData(PluginConfiguration.VideoScalingModeCrop)]
    [InlineData("fit")]
    public void Validate_WhenVideoScalingModeIsValid_ReturnsNoScalingError(string videoScalingMode)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            VideoScalingMode = videoScalingMode
        };

        var errors = config.Validate();

        Assert.DoesNotContain("Video scaling mode must be Stretch, Fit, or Crop", errors);
    }

    [Theory]
    [InlineData("Unsupported")]
    [InlineData("")]
    public void Validate_WhenPauseBehaviorIsInvalid_ReturnsError(string pauseBehavior)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            PauseBehavior = pauseBehavior
        };

        var errors = config.Validate();

        Assert.Contains("Pause behavior must be KeepLastColors or RestoreLightState", errors);
    }

    [Theory]
    [InlineData(PluginConfiguration.PauseBehaviorKeepLastColors)]
    [InlineData(PluginConfiguration.PauseBehaviorRestoreLightState)]
    [InlineData("restorelightstate")]
    public void Validate_WhenPauseBehaviorIsValid_ReturnsNoPauseError(string pauseBehavior)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            PauseBehavior = pauseBehavior
        };

        var errors = config.Validate();

        Assert.DoesNotContain("Pause behavior must be KeepLastColors or RestoreLightState", errors);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void Validate_WhenSamplingBreadthIsOutOfRange_ReturnsError(int samplingBreadthPercent)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            SamplingBreadthPercent = samplingBreadthPercent
        };

        var errors = config.Validate();

        Assert.Contains("Sampling breadth must be between 1 and 50 percent", errors);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(50)]
    public void Validate_WhenSamplingBreadthIsValid_ReturnsNoSamplingError(int samplingBreadthPercent)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            SamplingBreadthPercent = samplingBreadthPercent
        };

        var errors = config.Validate();

        Assert.DoesNotContain("Sampling breadth must be between 1 and 50 percent", errors);
    }

    [Theory]
    [InlineData("Unsupported")]
    [InlineData("")]
    public void Validate_WhenSamplingModeIsInvalid_ReturnsError(string samplingMode)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            SamplingMode = samplingMode
        };

        var errors = config.Validate();

        Assert.Contains("Sampling mode must be Average, CenterWeighted, or CenterPixel", errors);
    }

    [Theory]
    [InlineData(PluginConfiguration.SamplingModeAverage)]
    [InlineData(PluginConfiguration.SamplingModeCenterWeighted)]
    [InlineData(PluginConfiguration.SamplingModeCenterPixel)]
    [InlineData("centerweighted")]
    public void Validate_WhenSamplingModeIsValid_ReturnsNoSamplingModeError(string samplingMode)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            SamplingMode = samplingMode
        };

        var errors = config.Validate();

        Assert.DoesNotContain("Sampling mode must be Average, CenterWeighted, or CenterPixel", errors);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(91)]
    public void Validate_WhenColorSmoothingIsOutOfRange_ReturnsError(int colorSmoothingPercent)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            ColorSmoothingPercent = colorSmoothingPercent
        };

        var errors = config.Validate();

        Assert.Contains("Color smoothing must be between 0 and 90 percent", errors);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(90)]
    public void Validate_WhenColorSmoothingIsValid_ReturnsNoSmoothingError(int colorSmoothingPercent)
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "test-key",
            HueClientKey = "test-key",
            EntertainmentAreaId = "test-id",
            ColorSmoothingPercent = colorSmoothingPercent
        };

        var errors = config.Validate();

        Assert.DoesNotContain("Color smoothing must be between 0 and 90 percent", errors);
    }

    [Fact]
    public void Validate_WhenUserMappingsExist_SkipsDefaultBridgeValidation()
    {
        // Arrange — sync enabled, empty default fields, but a valid user mapping
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "",
            HueAppKey = "",
            HueClientKey = "",
            EntertainmentAreaId = "",
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = "user-1",
                    HueBridgeIp = "192.168.1.100",
                    HueAppKey = "app-key",
                    HueClientKey = "client-key",
                    EntertainmentAreaId = "area-1"
                }
            }
        };

        // Act
        var errors = config.Validate();

        // Assert — no bridge-related errors since user mappings cover it
        Assert.DoesNotContain("Hue Bridge IP is required", errors);
        Assert.DoesNotContain("Hue App Key is required", errors);
        Assert.DoesNotContain("Hue Client Key is required", errors);
        Assert.DoesNotContain("Entertainment Area ID is required", errors);
        Assert.True(config.IsValid());
    }

    [Fact]
    public void Validate_WhenUserMappingsEmptyIp_StillRequiresDefaultBridge()
    {
        // Arrange — user mapping exists but has empty bridge IP
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "",
            HueAppKey = "",
            HueClientKey = "",
            EntertainmentAreaId = "",
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = "user-1",
                    HueBridgeIp = "", // empty — doesn't count
                }
            }
        };

        // Act
        var errors = config.Validate();

        // Assert — should still require default bridge fields
        Assert.Contains("Hue Bridge IP is required when sync is enabled", errors);
    }

    [Fact]
    public void Validate_WhenUserMappingDisablesSync_AllowsMissingBridgeCredentials()
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            HueBridgeIp = "192.168.1.100",
            HueAppKey = "default-key",
            HueClientKey = "default-client",
            EntertainmentAreaId = "default-area",
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = "user-1",
                    SyncEnabled = false,
                    HueBridgeIp = "not-a-bridge",
                    HueAppKey = "",
                    HueClientKey = "",
                    EntertainmentAreaId = ""
                }
            }
        };

        var errors = config.Validate();

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_WhenOnlyDisabledUserMappingsExist_StillRequiresDefaultBridge()
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = "user-1",
                    SyncEnabled = false
                }
            }
        };

        var errors = config.Validate();

        Assert.Contains("Hue Bridge IP is required when sync is enabled", errors);
    }

    [Fact]
    public void GetBridgeConfigForUser_WithMatchingMapping_ReturnsUserConfig()
    {
        // Arrange
        var userId = System.Guid.NewGuid();
        var config = new PluginConfiguration
        {
            HueBridgeIp = "10.0.0.1",
            HueAppKey = "default-key",
            HueClientKey = "default-client",
            EntertainmentAreaId = "default-area",
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = userId.ToString(),
                    HueBridgeIp = "192.168.1.200",
                    HueAppKey = "user-key",
                    HueClientKey = "user-client",
                    EntertainmentAreaId = "user-area"
                }
            }
        };

        // Act
        var (bridgeIp, appKey, clientKey, areaId) = config.GetBridgeConfigForUser(userId);

        // Assert
        Assert.Equal("192.168.1.200", bridgeIp);
        Assert.Equal("user-key", appKey);
        Assert.Equal("user-client", clientKey);
        Assert.Equal("user-area", areaId);
    }

    [Fact]
    public void GetBridgeConfigForUser_MatchesMappingCaseInsensitively()
    {
        var userId = System.Guid.NewGuid();
        var config = new PluginConfiguration
        {
            HueBridgeIp = "10.0.0.1",
            HueAppKey = "default-key",
            HueClientKey = "default-client",
            EntertainmentAreaId = "default-area",
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = userId.ToString().ToUpperInvariant(),
                    HueBridgeIp = "192.168.1.200",
                    HueAppKey = "user-key",
                    HueClientKey = "user-client",
                    EntertainmentAreaId = "user-area"
                }
            }
        };

        var result = config.GetBridgeConfigForUser(userId);

        Assert.Equal("192.168.1.200", result.BridgeIp);
        Assert.Equal("user-key", result.AppKey);
    }

    [Fact]
    public void Validate_WhenUserMappingIsIncomplete_ReturnsMappingErrors()
    {
        var config = new PluginConfiguration
        {
            SyncEnabled = true,
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = "user-1",
                    HueBridgeIp = "not-an-ip",
                    HueAppKey = "",
                    HueClientKey = "",
                    EntertainmentAreaId = ""
                }
            }
        };

        var errors = config.Validate();

        Assert.Contains("User mapping 1 bridge address must be a valid private IP address or .local host name", errors);
        Assert.Contains("User mapping 1 requires a Hue App Key", errors);
        Assert.Contains("User mapping 1 requires a Hue Client Key", errors);
        Assert.Contains("User mapping 1 requires an Entertainment Area ID", errors);
    }

    [Fact]
    public void GetBridgeConfigForUser_WithNoMatchingMapping_ReturnsDefaultConfig()
    {
        // Arrange
        var config = new PluginConfiguration
        {
            HueBridgeIp = "10.0.0.1",
            HueAppKey = "default-key",
            HueClientKey = "default-client",
            EntertainmentAreaId = "default-area",
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = "some-other-user",
                    HueBridgeIp = "192.168.1.200",
                    HueAppKey = "user-key",
                    HueClientKey = "user-client",
                    EntertainmentAreaId = "user-area"
                }
            }
        };

        // Act
        var (bridgeIp, appKey, clientKey, areaId) = config.GetBridgeConfigForUser(System.Guid.NewGuid());

        // Assert — falls back to default
        Assert.Equal("10.0.0.1", bridgeIp);
        Assert.Equal("default-key", appKey);
        Assert.Equal("default-client", clientKey);
        Assert.Equal("default-area", areaId);
    }

    [Fact]
    public void GetBridgeConfigForUser_WithEmptyBridgeIpMapping_ReturnsDefaultConfig()
    {
        // Arrange — mapping exists for user but has empty bridge IP
        var userId = System.Guid.NewGuid();
        var config = new PluginConfiguration
        {
            HueBridgeIp = "10.0.0.1",
            HueAppKey = "default-key",
            HueClientKey = "default-client",
            EntertainmentAreaId = "default-area",
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = userId.ToString(),
                    HueBridgeIp = "",
                }
            }
        };

        // Act
        var (bridgeIp, appKey, clientKey, areaId) = config.GetBridgeConfigForUser(userId);

        // Assert — falls back to default because mapping has empty bridge IP
        Assert.Equal("10.0.0.1", bridgeIp);
        Assert.Equal("default-key", appKey);
    }

    [Fact]
    public void IsSyncEnabledForUser_UsesMappingOptOutAndDefaultsUnmappedUsersToEnabled()
    {
        var disabledUser = System.Guid.NewGuid();
        var config = new PluginConfiguration
        {
            UserMappings = new System.Collections.Generic.List<UserBridgeMapping>
            {
                new UserBridgeMapping
                {
                    UserId = disabledUser.ToString().ToUpperInvariant(),
                    SyncEnabled = false
                }
            }
        };

        Assert.False(config.IsSyncEnabledForUser(disabledUser));
        Assert.True(config.IsSyncEnabledForUser(System.Guid.NewGuid()));
    }
}
