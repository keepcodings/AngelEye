using AngelEyeBmsBridge;
using Xunit;

namespace AngelEyeBridge.Tests;

public sealed class AngelGameResultPayloadTests
{
    [Theory]
    [InlineData("A", "Spade", "As")]
    [InlineData("1", "Heart", "Ah")]
    [InlineData("10", "Diamond", "10d")]
    [InlineData("11", "Club", "Jc")]
    [InlineData("12", "Spades", "Qs")]
    [InlineData("13", "H", "Kh")]
    public void ToBmsCard_ProducesCanonicalBaccaratCard(
        string value,
        string suit,
        string expected)
    {
        BaccaratCard[] cards =
        [
            new()
            {
                Index = 2,
                Value = value,
                Suit = suit
            }
        ];

        string result = AngelBridgeWorker.ToBmsCard(cards, 2);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ToBmsCard_ReturnsEmptyForMissingOrInvalidCard()
    {
        BaccaratCard[] cards =
        [
            new()
            {
                Index = 1,
                Value = "Q",
                Suit = "Unknown"
            }
        ];

        Assert.Empty(AngelBridgeWorker.ToBmsCard(cards, 2));
        Assert.Empty(AngelBridgeWorker.ToBmsCard(cards, 1));
    }
}
