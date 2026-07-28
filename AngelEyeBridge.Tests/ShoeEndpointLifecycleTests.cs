using System.Text;
using AngelEyeBmsBridge;
using Xunit;

namespace AngelEyeBridge.Tests;

public sealed class ShoeEndpointLifecycleTests
{
    [Fact]
    public void BeginNextRoundCountdown_DoesNotSilentlyReplaceCrossDayShoe()
    {
        ShoeEndpoint endpoint = CreateEndpoint(
            currentShoe: 202601010001,
            currentRound: 0);

        endpoint.BeginNextRoundCountdown();

        Assert.Equal(202601010001, endpoint.CurrentShoe);
        Assert.Equal(1, endpoint.CurrentRound);
        Assert.Equal(1, endpoint.CurrentRoundId);
    }

    [Fact]
    public void ClearPreview_PreservesShoeEndingSafetyState()
    {
        ShoeEndpoint endpoint = CreateEndpoint(
            currentShoe: 202607260001,
            currentRound: 12);
        endpoint.RestoreRuntimeState(shoeEnding: true);

        endpoint.ClearPreview();

        Assert.True(endpoint.ShoeEnding);
    }

    [Fact]
    public void NewShoeSelection_DoesNotAllocateFirstRound()
    {
        ShoeEndpoint endpoint = CreateEndpoint(
            currentShoe: 202607260001,
            currentRound: 12);

        endpoint.StartNewShoe(new DateTime(2026, 7, 26));

        Assert.Equal(0, endpoint.CurrentRound);
        Assert.Null(endpoint.CurrentRoundId);
    }

    [Fact]
    public void ConfirmNewShoe_IsAuditedAndIdempotentForTheSameAction()
    {
        ShoeEndpoint endpoint = CreateEndpoint(
            currentShoe: 202607260001,
            currentRound: 12);

        bool first = endpoint.ConfirmNewShoe(
            "operator-action-1",
            "Physical shoe was replaced.",
            new DateTime(2026, 7, 26));
        long confirmedShoe = endpoint.CurrentShoe;
        bool duplicate = endpoint.ConfirmNewShoe(
            "operator-action-1",
            "Physical shoe was replaced.",
            new DateTime(2026, 7, 26));

        Assert.True(first);
        Assert.False(duplicate);
        Assert.Equal(confirmedShoe, endpoint.CurrentShoe);
        Assert.Equal(0, endpoint.CurrentRound);
        Assert.Equal("operator-action-1", endpoint.CaptureRuntimeState().LastNewShoeActionId);
        Assert.False(endpoint.AwaitingFirstAuthoritativeResultAfterShoeChange);
    }

    [Fact]
    public void EndpointDefault_DoesNotAllocateRoundFromCardOrResultTelegram()
    {
        ShoeEndpoint endpoint = CreateEndpoint(
            currentShoe: 202607260001,
            currentRound: 0);

        Assert.False(endpoint.AutoAdvanceRoundFromEvents);
    }

    [Fact]
    public void RestoredDuplicateCards_AreIdempotent_AndConflictsPreserveAuthoritativeCards()
    {
        ShoeEndpoint endpoint = CreateEndpoint(
            currentShoe: 202607260001,
            currentRound: 12);
        endpoint.ArmRoundBoundary(
            BridgeBoundaryStrategies.DerivedAfterPreviousResult,
            DateTimeOffset.UtcNow.AddSeconds(-5),
            Guid.NewGuid());
        endpoint.MarkStartGameStored("Pending");
        endpoint.PlayerCards.Add(new BaccaratCard
        {
            Index = 1,
            Suit = "Spade",
            Value = "8",
            IsPlayer = true
        });
        endpoint.PlayerCards.Add(new BaccaratCard
        {
            Index = 2,
            Suit = "Heart",
            Value = "10",
            IsPlayer = true
        });
        endpoint.BankerCards.Add(new BaccaratCard
        {
            Index = 1,
            Suit = "Club",
            Value = "K",
            IsPlayer = false
        });
        endpoint.MarkDealing();
        int cardEvents = 0;
        endpoint.CardDrawn += (_, _) => cardEvents++;

        endpoint.Listener.InjectBytes(
            BuildActiveReport('1', (byte)'D', 0x81, 0xB8));

        Assert.Equal(0, cardEvents);
        Assert.Equal(BridgeRoundPhases.Dealing, endpoint.RoundPhase);
        Assert.Collection(
            endpoint.PlayerCards.OrderBy(card => card.Index),
            card => Assert.Equal((1, "Spade", "8"), (card.Index, card.Suit, card.Value)),
            card => Assert.Equal((2, "Heart", "10"), (card.Index, card.Suit, card.Value)));
        Assert.Single(endpoint.BankerCards);

        endpoint.Listener.InjectBytes(
            BuildActiveReport('2', (byte)'D', 0x81, 0xB9));

        Assert.Equal(1, cardEvents);
        Assert.Equal(BridgeRoundPhases.AlignmentRequired, endpoint.RoundPhase);
        Assert.Collection(
            endpoint.PlayerCards.OrderBy(card => card.Index),
            card => Assert.Equal((1, "Spade", "8"), (card.Index, card.Suit, card.Value)),
            card => Assert.Equal((2, "Heart", "10"), (card.Index, card.Suit, card.Value)));
        Assert.Single(endpoint.BankerCards);
    }

    [Fact]
    public void CutCardDuringArmedRound_AllowsFinalRoundToFinishBeforeShoeChange()
    {
        ShoeEndpoint endpoint = CreateEndpoint(
            currentShoe: 202607280001,
            currentRound: 12);
        endpoint.ArmRoundBoundary(
            BridgeBoundaryStrategies.DerivedAfterPreviousResult,
            DateTimeOffset.UtcNow.AddSeconds(-5),
            Guid.NewGuid());
        endpoint.MarkStartGameStored("Pending");
        endpoint.MarkDealing();
        endpoint.GameResultReceived += (shoe, result) =>
            shoe.MarkFinalResultStored(DateTimeOffset.UtcNow, result.Result);

        endpoint.Listener.InjectBytes(BuildActiveReport('1', (byte)'C'));

        Assert.True(endpoint.ShoeEnding);
        Assert.Equal(BridgeRoundPhases.Dealing, endpoint.RoundPhase);

        endpoint.Listener.InjectBytes(
            BuildActiveReport('2', (byte)'D', 0x81, 0xB8));
        endpoint.Listener.InjectBytes(
            BuildActiveReport('3', (byte)'G', 0x91));

        Assert.Single(endpoint.PlayerCards);
        Assert.Equal(BridgeRoundPhases.ShoeChangePending, endpoint.RoundPhase);
        Assert.True(endpoint.ShoeEnding);
    }

    [Fact]
    public void StartSignalWithoutSameEndpointCutCard_DoesNotAdvanceShoe()
    {
        ShoeEndpoint endpoint = CreateEndpoint(
            currentShoe: 202607280001,
            currentRound: 12);

        bool confirmed = endpoint.TryConfirmNewShoeFromStartSignal(
            new SerialListener.ProtocolSignal(
                SerialListener.ProtocolSignalKind.StartOfCommunication,
                "1",
                "05 31 53 03"));

        Assert.False(confirmed);
        Assert.Equal(202607280001, endpoint.CurrentShoe);
        Assert.Equal(12, endpoint.CurrentRound);
    }

    [Fact]
    public void CutThenStartSignal_ConfirmsNewShoeOnceWithoutAllocatingRound()
    {
        ShoeEndpoint endpoint = CreateEndpoint(
            currentShoe: 202607280001,
            currentRound: 12);
        SerialListener.ProtocolSignal signal = new(
            SerialListener.ProtocolSignalKind.StartOfCommunication,
            "2",
            "05 32 53 03");
        endpoint.Listener.InjectBytes(BuildActiveReport('1', (byte)'C'));

        bool first = endpoint.TryConfirmNewShoeFromStartSignal(
            signal,
            new DateTime(2026, 7, 28));
        bool duplicate = endpoint.TryConfirmNewShoeFromStartSignal(
            signal,
            new DateTime(2026, 7, 28));

        Assert.True(first);
        Assert.False(duplicate);
        Assert.Equal(202607280002, endpoint.CurrentShoe);
        Assert.Equal(0, endpoint.CurrentRound);
        Assert.Null(endpoint.CurrentRoundId);
        Assert.False(endpoint.ShoeEnding);
        Assert.Equal(BridgeRoundPhases.ConnectedWaitingBoundary, endpoint.RoundPhase);
        Assert.True(endpoint.AwaitingFirstAuthoritativeResultAfterShoeChange);
    }

    [Fact]
    public void CrossDayCutThenStartSignal_ResetsToFirstShoeForNewDate()
    {
        ShoeEndpoint endpoint = CreateEndpoint(
            currentShoe: 202607270005,
            currentRound: 58);
        endpoint.Listener.InjectBytes(BuildActiveReport('1', (byte)'C'));

        bool confirmed = endpoint.TryConfirmNewShoeFromStartSignal(
            new SerialListener.ProtocolSignal(
                SerialListener.ProtocolSignalKind.StartOfCommunication,
                "2",
                "05 32 53 03"),
            new DateTime(2026, 7, 28));

        Assert.True(confirmed);
        Assert.Equal(202607280001, endpoint.CurrentShoe);
        Assert.Equal(0, endpoint.CurrentRound);
    }

    private static ShoeEndpoint CreateEndpoint(long currentShoe, long currentRound) =>
        new(new ShoeEndpointSettings
        {
            Enabled = true,
            DeskName = "901桌",
            SourceDataCode = "901",
            ShoeId = "SHOE901",
            CurrentShoe = currentShoe,
            CurrentRound = currentRound,
            CurrentRoundId = currentRound > 0 ? currentRound : null,
            ConnectionMode = ShoeConnectionMode.MoxaTcp,
            MoxaHost = "127.0.0.1",
            MoxaPort = 4001
        });

    private static byte[] BuildActiveReport(char sequence, params byte[] data)
    {
        byte[] packetWithoutBcc = new byte[data.Length + 3];
        packetWithoutBcc[0] = 0x05;
        packetWithoutBcc[1] = (byte)sequence;
        data.CopyTo(packetWithoutBcc, 2);
        packetWithoutBcc[^1] = 0x03;
        byte[] bcc = Encoding.ASCII.GetBytes(
            SerialListener.CalculateBcc(packetWithoutBcc));
        return packetWithoutBcc.Concat(bcc).ToArray();
    }
}
