using System.Collections.Generic;
using System.Globalization;
using AIRadio.Desktop.Converters;
using AIRadio.Desktop.Models;
using Avalonia.Layout;
using Avalonia.Media;
using Xunit;

namespace AIRadio.Desktop.Tests;

/// <summary>合并后 Converter 与原行为等价性测试（spec §5.3 / §8.2）。</summary>
public class ConverterEquivalenceTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void InverseBool_RoundTrips(bool input, bool expected)
    {
        var c = new InverseBoolConverter();
        Assert.Equal(expected, c.Convert(input, typeof(bool), null, CultureInfo.InvariantCulture));
        Assert.Equal(expected, c.ConvertBack(input, typeof(bool), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void MessageAlign_UserRight_OthersLeft()
    {
        var c = MessageAlignConverter.Instance;
        Assert.Equal(HorizontalAlignment.Right, c.Convert(MessageRole.User, typeof(object), null, CultureInfo.InvariantCulture));
        Assert.Equal(HorizontalAlignment.Left, c.Convert(MessageRole.Assistant, typeof(object), null, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(RadioState.Idle, "AIRADIO FM")]
    [InlineData(RadioState.Curating, "CURATING")]
    [InlineData(RadioState.Searching, "SEARCHING")]
    [InlineData(RadioState.Speaking, "SPEAKING")]
    [InlineData(RadioState.Playing, "ON AIR")]
    [InlineData(RadioState.Error, "ERROR")]
    public void RadioStateToText_MapsCorrectly(RadioState state, string expected)
        => Assert.Equal(expected, RadioStateToTextConverter.Instance.Convert(state, typeof(string), null, CultureInfo.InvariantCulture));

    [Theory]
    [InlineData(RadioState.Idle)]
    [InlineData(RadioState.Curating)]
    [InlineData(RadioState.Searching)]
    [InlineData(RadioState.Speaking)]
    [InlineData(RadioState.Playing)]
    [InlineData(RadioState.Error)]
    public void RadioStateToBrush_UsesDifferentPaletteForLightAndDark(RadioState state)
    {
        var converter = RadioStateToBrushConverter.Instance;
        var dark = Assert.IsType<SolidColorBrush>(
            converter.Convert(new List<object?> { state, true }, typeof(object), null, CultureInfo.InvariantCulture));
        var light = Assert.IsType<SolidColorBrush>(
            converter.Convert(new List<object?> { state, false }, typeof(object), null, CultureInfo.InvariantCulture));

        Assert.NotEqual(dark.Color, light.Color);
    }
}
