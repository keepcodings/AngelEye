using System.Text;
using AngelEyeBmsBridge;
using Xunit;

namespace AngelEyeBridge.Tests;

public sealed class SerialListenerCharacterizationTests
{
    [Fact]
    public void InjectBytes_DecodesActiveCardAndGameResult()
    {
        var listener = new SerialListener();
        SerialListener.CardInfo? card = null;
        SerialListener.GameResult? result = null;

        listener.OnCardDrawn += value => card = value;
        listener.OnGameResult += value => result = value;

        var cardPacket = BuildActiveReport('1', (byte)'D', 0x81, 0xB8);
        var resultPacket = BuildActiveReport('2', (byte)'G', 0x91);

        listener.InjectBytes(cardPacket);
        listener.InjectBytes(resultPacket);

        Assert.NotNull(card);
        Assert.Equal("1", card.Value.Seq);
        Assert.True(card.Value.IsActiveGame);
        Assert.Equal("Player", card.Value.Target);
        Assert.Equal(1, card.Value.Index);
        Assert.Equal("Spade", card.Value.Suit);
        Assert.Equal("8", card.Value.Value);
        Assert.Equal(ToSpacedHex(cardPacket), card.Value.RawBytes);

        Assert.NotNull(result);
        Assert.Equal("2", result.Value.Seq);
        Assert.Equal("PlayerWin", result.Value.Result);
        Assert.Equal("PlayerPair", result.Value.Pair);
        Assert.Equal(ToSpacedHex(resultPacket), result.Value.RawBytes);
    }

    [Fact]
    public void InjectBytes_DecodesCutCardErrorAndLockStatus()
    {
        var listener = new SerialListener();
        SerialListener.CutCardInfo? cutCard = null;
        SerialListener.ErrorInfo? error = null;
        bool? locked = null;

        listener.OnCuttingCardDrawn += value => cutCard = value;
        listener.OnErrorOccurred += value => error = value;
        listener.OnLockStatusChanged += value => locked = value;

        var cutPacket = BuildActiveReport('3', (byte)'C');
        var errorPacket = BuildActiveReport('4', (byte)'E', 0xC1);
        var lockPacket = BuildActiveReport('5', (byte)'L', 0x81);

        listener.InjectBytes(cutPacket);
        listener.InjectBytes(errorPacket);
        listener.InjectBytes(lockPacket);

        Assert.NotNull(cutCard);
        Assert.Equal("3", cutCard.Value.Seq);
        Assert.Equal(ToSpacedHex(cutPacket), cutCard.Value.RawBytes);

        Assert.NotNull(error);
        Assert.Equal("4", error.Value.Seq);
        Assert.True(error.Value.InErrorMode);
        Assert.Equal(1, error.Value.ErrorCode);
        Assert.Equal(ToSpacedHex(errorPacket), error.Value.RawBytes);

        Assert.True(locked);
    }

    [Fact]
    public void InjectBytes_WaitsForFragmentedPacketAndPreservesConcatenatedOrder()
    {
        var listener = new SerialListener();
        var observed = new List<string>();
        listener.OnCardDrawn += _ => observed.Add("card");
        listener.OnGameResult += _ => observed.Add("result");

        var fragmentedCard = BuildActiveReport('6', (byte)'D', 0x81, 0xB8);
        listener.InjectBytes(fragmentedCard[..4]);
        Assert.Empty(observed);

        listener.InjectBytes(fragmentedCard[4..]);
        Assert.Equal(new[] { "card" }, observed);

        var card = BuildActiveReport('7', (byte)'D', 0x91, 0xA5);
        var result = BuildActiveReport('8', (byte)'G', 0xA2);
        listener.InjectBytes(card.Concat(result).ToArray());

        Assert.Equal(new[] { "card", "card", "result" }, observed);
    }

    [Fact]
    public void InjectBytes_RejectsInvalidBcc()
    {
        var listener = new SerialListener();
        var cardCount = 0;
        var diagnostics = new List<string>();
        listener.OnCardDrawn += _ => cardCount++;
        listener.OnRawDataLogged += (_, description) => diagnostics.Add(description);

        var packet = BuildActiveReport('9', (byte)'D', 0x81, 0xB8);
        packet[^1] = packet[^1] == (byte)'0' ? (byte)'1' : (byte)'0';

        listener.InjectBytes(packet);

        Assert.Equal(0, cardCount);
        Assert.Contains(diagnostics, message =>
            message.Contains("BCC", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InjectBytes_DeduplicatesRepeatedSequence()
    {
        var listener = new SerialListener();
        var cardCount = 0;
        var diagnostics = new List<string>();
        listener.OnCardDrawn += _ => cardCount++;
        listener.OnRawDataLogged += (_, description) => diagnostics.Add(description);

        var packet = BuildActiveReport('A', (byte)'D', 0x81, 0xB8);
        listener.InjectBytes(packet);
        listener.InjectBytes(packet);

        Assert.Equal(1, cardCount);
        Assert.Contains(diagnostics, message =>
            message.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
    }

    private static byte[] BuildActiveReport(char sequence, params byte[] data)
    {
        var packetWithoutBcc = new byte[data.Length + 3];
        packetWithoutBcc[0] = 0x05;
        packetWithoutBcc[1] = (byte)sequence;
        data.CopyTo(packetWithoutBcc, 2);
        packetWithoutBcc[^1] = 0x03;

        var bcc = Encoding.ASCII.GetBytes(SerialListener.CalculateBcc(packetWithoutBcc));
        return packetWithoutBcc.Concat(bcc).ToArray();
    }

    private static string ToSpacedHex(byte[] bytes) =>
        string.Join(" ", bytes.Select(value => value.ToString("X2")));
}
