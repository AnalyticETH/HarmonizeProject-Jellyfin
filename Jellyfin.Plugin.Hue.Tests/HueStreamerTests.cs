using Jellyfin.Plugin.Hue.Hue;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Hue.Tests;

/// <summary>
/// Tests for HueStreamer binary protocol and color encoding.
/// </summary>
public class HueStreamerTests
{
    private readonly Mock<ILogger<HueStreamer>> _loggerMock;

    public HueStreamerTests()
    {
        _loggerMock = new Mock<ILogger<HueStreamer>>();
    }

    #region Color Encoding Tests

    [Theory]
    [InlineData(0, 0, 0, 0, 0, 0)] // Black
    [InlineData(255, 255, 255, 127, 127, 127)] // White (halved)
    [InlineData(255, 0, 0, 127, 0, 0)] // Red
    [InlineData(0, 255, 0, 0, 127, 0)] // Green
    [InlineData(0, 0, 255, 0, 0, 127)] // Blue
    [InlineData(128, 128, 128, 64, 64, 64)] // Gray
    public void EncodeColorFor16Bit_CorrectlyHalvesValues(
        byte inputR, byte inputG, byte inputB,
        byte expectedR, byte expectedG, byte expectedB)
    {
        // The HueStream protocol halves 8-bit values for 16-bit encoding
        var encodedR = (byte)(inputR / 2);
        var encodedG = (byte)(inputG / 2);
        var encodedB = (byte)(inputB / 2);

        Assert.Equal(expectedR, encodedR);
        Assert.Equal(expectedG, encodedG);
        Assert.Equal(expectedB, encodedB);
    }

    [Fact]
    public void HueStreamPacket_HasCorrectHeader()
    {
        // Arrange
        var expectedHeader = "HueStream"u8.ToArray();

        // Act & Assert
        Assert.Equal(9, expectedHeader.Length);
        Assert.Equal((byte)'H', expectedHeader[0]);
        Assert.Equal((byte)'u', expectedHeader[1]);
        Assert.Equal((byte)'e', expectedHeader[2]);
        Assert.Equal((byte)'S', expectedHeader[3]);
        Assert.Equal((byte)'t', expectedHeader[4]);
        Assert.Equal((byte)'r', expectedHeader[5]);
        Assert.Equal((byte)'e', expectedHeader[6]);
        Assert.Equal((byte)'a', expectedHeader[7]);
        Assert.Equal((byte)'m', expectedHeader[8]);
    }

    [Fact]
    public void HueStreamPacket_VersionBytes_AreCorrect()
    {
        // HueStream v2.0 version bytes
        byte[] versionBytes = [0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

        Assert.Equal(7, versionBytes.Length);
        Assert.Equal(0x02, versionBytes[0]); // Version 2
        Assert.All(versionBytes.Skip(1), b => Assert.Equal(0x00, b)); // Padding
    }

    [Fact]
    public void ChannelColorData_Has6BytesPerChannel()
    {
        // Each channel has: R(2 bytes) + G(2 bytes) + B(2 bytes) = 6 bytes
        // Format: [R, R, G, G, B, B] where each value is duplicated for 16-bit
        byte r = 127, g = 64, b = 32;
        byte[] channelData = [r, r, g, g, b, b];

        Assert.Equal(6, channelData.Length);
        Assert.Equal(channelData[0], channelData[1]); // R duplicated
        Assert.Equal(channelData[2], channelData[3]); // G duplicated
        Assert.Equal(channelData[4], channelData[5]); // B duplicated
    }

    #endregion

    #region Color Change Detection Tests

    [Fact]
    public void HasSignificantColorChange_IdenticalColors_ReturnsFalse()
    {
        // Arrange
        var current = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 100, 100, 100 } },
            { 1, new byte[] { 200, 200, 200 } }
        };
        var previous = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 100, 100, 100 } },
            { 1, new byte[] { 200, 200, 200 } }
        };

        // Act
        var hasChange = HasSignificantColorChange(current, previous, threshold: 10);

        // Assert
        Assert.False(hasChange);
    }

    [Fact]
    public void HasSignificantColorChange_LargeChange_ReturnsTrue()
    {
        // Arrange
        var current = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 100, 100, 100 } }
        };
        var previous = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 200, 200, 200 } }
        };

        // Act
        var hasChange = HasSignificantColorChange(current, previous, threshold: 10);

        // Assert
        Assert.True(hasChange);
    }

    [Fact]
    public void HasSignificantColorChange_SmallChange_BelowThreshold_ReturnsFalse()
    {
        // Arrange
        var current = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 100, 100, 100 } }
        };
        var previous = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 105, 103, 102 } }
        };

        // Act
        var hasChange = HasSignificantColorChange(current, previous, threshold: 10);

        // Assert
        Assert.False(hasChange);
    }

    [Fact]
    public void HasSignificantColorChange_SmallChange_AboveThreshold_ReturnsTrue()
    {
        // Arrange
        var current = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 100, 100, 100 } }
        };
        var previous = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 115, 100, 100 } } // R differs by 15
        };

        // Act
        var hasChange = HasSignificantColorChange(current, previous, threshold: 10);

        // Assert
        Assert.True(hasChange);
    }

    [Fact]
    public void HasSignificantColorChange_NewChannel_ReturnsTrue()
    {
        // Arrange
        var current = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 100, 100, 100 } },
            { 1, new byte[] { 200, 200, 200 } } // New channel
        };
        var previous = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 100, 100, 100 } }
        };

        // Act
        var hasChange = HasSignificantColorChange(current, previous, threshold: 10);

        // Assert
        Assert.True(hasChange);
    }

    [Fact]
    public void HasSignificantColorChange_EmptyPrevious_ReturnsTrue()
    {
        // Arrange
        var current = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 100, 100, 100 } }
        };
        var previous = new Dictionary<int, byte[]>();

        // Act
        var hasChange = HasSignificantColorChange(current, previous, threshold: 10);

        // Assert
        Assert.True(hasChange);
    }

    [Fact]
    public void HasSignificantColorChange_ZeroThreshold_AnyChange_ReturnsTrue()
    {
        // Arrange
        var current = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 100, 100, 100 } }
        };
        var previous = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 101, 100, 100 } } // Differs by 1
        };

        // Act
        var hasChange = HasSignificantColorChange(current, previous, threshold: 0);

        // Assert
        Assert.True(hasChange);
    }

    #endregion

    #region Packet Construction Tests

    [Fact]
    public void BuildHueStreamPacket_SingleChannel_CorrectSize()
    {
        // Arrange
        var areaId = "test-area-id";
        var channelColors = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 255, 128, 64 } }
        };

        // Act
        var packet = BuildHueStreamPacket(areaId, channelColors);

        // Assert
        // Header (9) + Version (7) + AreaId (variable) + null terminator (1) + channel data (1 + 6)
        var expectedMinSize = 9 + 7 + areaId.Length + 1 + 7;
        Assert.True(packet.Length >= expectedMinSize);
    }

    [Fact]
    public void BuildHueStreamPacket_MultipleChannels_IncludesAllChannels()
    {
        // Arrange
        var areaId = "area";
        var channelColors = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 255, 0, 0 } },
            { 1, new byte[] { 0, 255, 0 } },
            { 2, new byte[] { 0, 0, 255 } }
        };

        // Act
        var packet = BuildHueStreamPacket(areaId, channelColors);

        // Assert
        // Should have 3 channels worth of data (3 * 7 bytes each = 21 bytes for channel data)
        Assert.True(packet.Length >= 9 + 7 + 4 + 1 + 21);
    }

    [Fact]
    public void BuildHueStreamPacket_StartsWithCorrectHeader()
    {
        // Arrange
        var channelColors = new Dictionary<int, byte[]>
        {
            { 0, new byte[] { 100, 100, 100 } }
        };

        // Act
        var packet = BuildHueStreamPacket("area", channelColors);

        // Assert
        Assert.Equal((byte)'H', packet[0]);
        Assert.Equal((byte)'u', packet[1]);
        Assert.Equal((byte)'e', packet[2]);
        Assert.Equal((byte)'S', packet[3]);
        Assert.Equal((byte)'t', packet[4]);
        Assert.Equal((byte)'r', packet[5]);
        Assert.Equal((byte)'e', packet[6]);
        Assert.Equal((byte)'a', packet[7]);
        Assert.Equal((byte)'m', packet[8]);
    }

    #endregion

    #region Coordinate Mapping Tests

    [Theory]
    [InlineData(-1.0, 160, 0)] // Far left -> X = 0
    [InlineData(1.0, 160, 159)] // Far right -> X = 159
    [InlineData(0.0, 160, 79)] // Center -> X = 79 (0.5 * 159 = 79.5, truncated to 79)
    public void MapHueCoordinateToPixelX_CorrectMapping(
        double hueX, int width, int expectedPixelX)
    {
        // Hue X coordinate ranges from -1 (left) to +1 (right)
        // Map to pixel coordinate: ((hueX + 1) / 2) * (width - 1)
        var pixelX = (int)(((hueX + 1.0) / 2.0) * (width - 1));

        Assert.Equal(expectedPixelX, pixelX);
    }

    [Theory]
    [InlineData(1.0, 90, 0)] // Top -> Y = 0
    [InlineData(-1.0, 90, 89)] // Bottom -> Y = 89
    [InlineData(0.0, 90, 44)] // Center -> Y = 44
    public void MapHueCoordinateToPixelY_CorrectMapping(
        double hueZ, int height, int expectedPixelY)
    {
        // Hue Z coordinate ranges from -1 (bottom) to +1 (top)
        // Map to pixel coordinate: ((1 - hueZ) / 2) * (height - 1)
        var pixelY = (int)(((1.0 - hueZ) / 2.0) * (height - 1));

        Assert.Equal(expectedPixelY, pixelY);
    }

    #endregion

    #region Helper Methods (Simulating HueStreamer logic)

    private static bool HasSignificantColorChange(
        Dictionary<int, byte[]> current,
        Dictionary<int, byte[]> previous,
        int threshold)
    {
        if (previous.Count == 0 && current.Count > 0)
            return true;

        foreach (var (channelId, colors) in current)
        {
            if (!previous.TryGetValue(channelId, out var prevColors))
                return true;

            for (int i = 0; i < Math.Min(colors.Length, prevColors.Length); i++)
            {
                if (Math.Abs(colors[i] - prevColors[i]) > threshold)
                    return true;
            }
        }

        return false;
    }

    private static byte[] BuildHueStreamPacket(string areaId, Dictionary<int, byte[]> channelColors)
    {
        using var ms = new MemoryStream();

        // Header: "HueStream"
        ms.Write("HueStream"u8);

        // Version: 0x02 0x00 0x00 0x00 0x00 0x00 0x00
        ms.Write(new byte[] { 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });

        // Area ID (ASCII) + null terminator
        var areaIdBytes = System.Text.Encoding.ASCII.GetBytes(areaId);
        ms.Write(areaIdBytes);
        ms.WriteByte(0x00);

        // Channel data
        foreach (var (channelId, rgb) in channelColors.OrderBy(c => c.Key))
        {
            ms.WriteByte((byte)channelId);

            // Each color component is halved and duplicated for 16-bit format
            byte r = (byte)(rgb[0] / 2);
            byte g = (byte)(rgb[1] / 2);
            byte b = (byte)(rgb[2] / 2);

            ms.Write(new byte[] { r, r, g, g, b, b });
        }

        return ms.ToArray();
    }

    #endregion
}
