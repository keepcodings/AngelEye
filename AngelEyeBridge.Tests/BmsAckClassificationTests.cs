using AngelEyeBmsBridge;
using Xunit;

namespace AngelEyeBridge.Tests;

public sealed class BmsAckClassificationTests
{
    private const long EventId = 42;
    private const string EventUid = "2ad19b12-a9c8-43af-8f0d-51b5eec5c949";

    [Theory]
    [InlineData("""{"ErrCode":0,"Data":{"Accepted":true,"Duplicate":false,"EventId":42,"EventUid":"2ad19b12-a9c8-43af-8f0d-51b5eec5c949"}}""")]
    [InlineData("""{"errCode":0,"data":{"accepted":false,"duplicate":true,"eventId":42,"eventUid":"2AD19B12-A9C8-43AF-8F0D-51B5EEC5C949"}}""")]
    public void ExactAcceptedOrDuplicateAck_IsAccepted(string json)
    {
        Assert.Equal(
            BridgeAckDisposition.Accepted,
            BmsApiClient.ClassifyAck(json, EventId, EventUid));
    }

    [Theory]
    [InlineData("""{"ErrCode":400,"Data":{"Accepted":false,"Duplicate":false,"EventId":42,"EventUid":"2ad19b12-a9c8-43af-8f0d-51b5eec5c949"}}""")]
    [InlineData("""{"errCode":0,"data":{"accepted":false,"duplicate":false,"eventId":42,"eventUid":"2ad19b12-a9c8-43af-8f0d-51b5eec5c949"}}""")]
    public void ExplicitNegativeAck_IsRejected(string json)
    {
        Assert.Equal(
            BridgeAckDisposition.Rejected,
            BmsApiClient.ClassifyAck(json, EventId, EventUid));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>proxy response</html>")]
    [InlineData("""{"ErrCode":0}""")]
    [InlineData("""{"ErrCode":400,"Data":{"Accepted":false}}""")]
    [InlineData("""{"Data":{"Accepted":true,"Duplicate":false,"EventId":42,"EventUid":"2ad19b12-a9c8-43af-8f0d-51b5eec5c949"}}""")]
    [InlineData("""{"ErrCode":0,"Data":{}}""")]
    [InlineData("""{"ErrCode":0,"Data":{"Accepted":true,"EventId":42,"EventUid":"2ad19b12-a9c8-43af-8f0d-51b5eec5c949"}}""")]
    [InlineData("""{"ErrCode":0,"Data":{"Accepted":true,"Duplicate":false,"EventUid":"2ad19b12-a9c8-43af-8f0d-51b5eec5c949"}}""")]
    [InlineData("""{"ErrCode":0,"Data":{"Accepted":true,"Duplicate":false,"EventId":42}}""")]
    [InlineData("""{"ErrCode":0,"Data":{"Accepted":true,"Duplicate":false,"EventId":43,"EventUid":"2ad19b12-a9c8-43af-8f0d-51b5eec5c949"}}""")]
    [InlineData("""{"ErrCode":0,"Data":{"Accepted":true,"Duplicate":false,"EventId":42,"EventUid":"a158a4b3-330c-41ae-b7d6-f83867efeec7"}}""")]
    [InlineData("""{"ErrCode":0,"Data":{"Accepted":true,"Duplicate":false,"EventId":42,"EventUid":"not-a-guid"}}""")]
    public void MissingMalformedOrMismatchedIdentityAck_IsUnconfirmed(string json)
    {
        Assert.Equal(
            BridgeAckDisposition.Unconfirmed,
            BmsApiClient.ClassifyAck(json, EventId, EventUid));
    }
}
