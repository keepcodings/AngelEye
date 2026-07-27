using AngelEyeBmsBridge;
using Xunit;

namespace AngelEyeBmsBridge.UiTests;

public sealed class GameResultDeliveryGateTests
{
    [Theory]
    [InlineData("As", "Kh", "2d", "3c", true)]
    [InlineData("", "Kh", "2d", "3c", false)]
    [InlineData("As", " ", "2d", "3c", false)]
    [InlineData("As", "Kh", "", "3c", false)]
    [InlineData("As", "Kh", "2d", null, false)]
    public void MandatoryBaccaratCards_MustAllBePresent(
        string p1,
        string p2,
        string b1,
        string? b2,
        bool expected)
    {
        Assert.Equal(
            expected,
            Form1.HasMandatoryBaccaratCards(p1, p2, b1, b2!));
    }
}
