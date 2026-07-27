using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AngelEyeBmsBridge;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AngelEyeBridge.Tests;

public sealed class DedicatedRecoveryClientTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "angel-eye-tests",
        Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;

    public DedicatedRecoveryClientTests()
    {
        Directory.CreateDirectory(_directory);
        _dbPath = Path.Combine(_directory, "dedicated-recovery.sqlite");
    }

    [Fact]
    public async Task RecoveryCheck_IsBoundedAndNeverIncludesResultPayload()
    {
        BridgeEventJournal journal = new(_dbPath);
        for (int index = 1; index <= 21; index++)
        {
            await AddUnconfirmedGameResultAsync(
                journal,
                round: index,
                roundId: 9000 + index);
        }

        string requestJson = string.Empty;
        using BmsApiClient client = new(new DelegateHandler(async request =>
        {
            requestJson = await request.Content!.ReadAsStringAsync();
            return JsonResponse(new
            {
                errCode = 0,
                data = new
                {
                    accepted = true,
                    nextPollSeconds = 15,
                    commands = Array.Empty<object>(),
                    decisions = Array.Empty<object>()
                }
            });
        }));

        await client.RunRecoveryCheckOnceAsync(
            Settings(),
            journal,
            _ => true,
            () => []);

        using JsonDocument document = JsonDocument.Parse(requestJson);
        JsonElement summaries = document.RootElement.GetProperty("unconfirmedEvents");
        Assert.Equal(20, summaries.GetArrayLength());
        string serialized = summaries.GetRawText();
        Assert.DoesNotContain("\"cards\"", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"winner\"", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"pair\"", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"gameResult\":", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuthorizedRecoverRound_PostsOriginalResultOnlyToDedicatedEndpoint()
    {
        BridgeEventJournal journal = new(_dbPath);
        BridgeRecoveryCandidate candidate = await AddUnconfirmedGameResultAsync(
            journal,
            round: 12,
            roundId: 9988);
        string retainedGameResultJson = ReadRetainedGameResultJson(candidate.EventId);
        List<string> paths = [];
        string recoveryJson = string.Empty;
        AngelBridgeCommand command = Command(candidate, dispatchCount: 1);
        using BmsApiClient client = new(new DelegateHandler(async request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath.EndsWith("/recoveries/check", StringComparison.Ordinal))
            {
                return CheckResponse(commands: [command]);
            }

            recoveryJson = await request.Content!.ReadAsStringAsync();
            return RecoveryAck(command, "Recovered");
        }));

        await client.RunRecoveryCheckOnceAsync(Settings(), journal, _ => true, () => []);

        Assert.Equal(
            ["/api/source/angel/recoveries/check", "/api/source/angel/recoveries"],
            paths);
        using JsonDocument submitted = JsonDocument.Parse(recoveryJson);
        JsonElement root = submitted.RootElement;
        Assert.Equal(command.CommandId, root.GetProperty("commandId").GetString());
        Assert.Equal(command.Generation, root.GetProperty("generation").GetInt32());
        Assert.Equal(command.DispatchCount, root.GetProperty("dispatchCount").GetInt32());
        Assert.Equal(candidate.EventUid, root.GetProperty("eventUid").GetString());
        Assert.Equal("Found", root.GetProperty("outcome").GetString());
        JsonElement submittedGameResult = root.GetProperty("gameResult");
        Assert.Equal(retainedGameResultJson, submittedGameResult.GetRawText());
        Assert.Equal("Qh", submittedGameResult.GetProperty("cards").GetProperty("p1").GetString());
        Assert.Equal("5h", submittedGameResult.GetProperty("cards").GetProperty("p2").GetString());
        Assert.Equal("As", submittedGameResult.GetProperty("cards").GetProperty("p3").GetString());
        Assert.Equal("3s", submittedGameResult.GetProperty("cards").GetProperty("b1").GetString());
        Assert.Equal("3h", submittedGameResult.GetProperty("cards").GetProperty("b2").GetString());
        Assert.Equal(string.Empty, submittedGameResult.GetProperty("cards").GetProperty("b3").GetString());
        Assert.False(root.TryGetProperty("type", out _));

        (string status, string recoveryState) = ReadEventState(candidate.EventId);
        Assert.Equal("Unconfirmed", status);
        Assert.Equal("Recovered", recoveryState);
        Assert.Equal("Recovered", ReadAuditResult(command.CommandId));
    }

    [Fact]
    public async Task RetainedResultFromAnotherBridge_IsRejectedWithoutResultDisclosure()
    {
        BridgeEventJournal journal = new(_dbPath);
        BridgeRecoveryCandidate candidate = await AddUnconfirmedGameResultAsync(
            journal,
            round: 13,
            roundId: 9989,
            bridgeId: "QA-30");
        AngelBridgeCommand command = Command(candidate, dispatchCount: 1);
        string recoveryJson = string.Empty;
        using BmsApiClient client = new(new DelegateHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/recoveries/check", StringComparison.Ordinal))
            {
                return CheckResponse(commands: [command]);
            }

            recoveryJson = await request.Content!.ReadAsStringAsync();
            return RecoveryAck(command, "Conflict");
        }));

        await client.RunRecoveryCheckOnceAsync(Settings(), journal, _ => true, () => []);

        using JsonDocument submitted = JsonDocument.Parse(recoveryJson);
        Assert.Equal("Conflict", submitted.RootElement.GetProperty("outcome").GetString());
        Assert.False(submitted.RootElement.TryGetProperty("gameResult", out _));
        Assert.Equal("Conflict", ReadEventState(candidate.EventId).RecoveryState);
    }

    [Fact]
    public async Task AwaitingOperator_SurvivesRestart_AndLaterGlobalCommandCanRecover()
    {
        BridgeEventJournal journal = new(_dbPath);
        BridgeRecoveryCandidate candidate = await AddUnconfirmedGameResultAsync(
            journal,
            round: 13,
            roundId: 9989);
        AngelBridgeRecoveryDecision awaiting = Decision(candidate, "AwaitingOperator");
        using (BmsApiClient firstClient = new(new DelegateHandler(_ =>
                   Task.FromResult(CheckResponse(decisions: [awaiting])))))
        {
            await firstClient.RunRecoveryCheckOnceAsync(Settings(), journal, _ => true, () => []);
        }

        BridgeEventJournal reopened = new(_dbPath);
        Assert.Empty(await reopened.GetDueRecoveryCandidatesAsync(20, DateTimeOffset.UtcNow.AddDays(1)));

        int recoveryPosts = 0;
        AngelBridgeCommand command = Command(candidate, dispatchCount: 1);
        using BmsApiClient secondClient = new(new DelegateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/recoveries/check", StringComparison.Ordinal))
            {
                return Task.FromResult(CheckResponse(commands: [command]));
            }

            recoveryPosts++;
            return Task.FromResult(RecoveryAck(command, "Recovered"));
        }));
        await secondClient.RunRecoveryCheckOnceAsync(Settings(), reopened, _ => true, () => []);

        Assert.Equal(1, recoveryPosts);
        Assert.Equal("Recovered", ReadEventState(candidate.EventId).RecoveryState);
    }

    [Fact]
    public async Task AckUnknown_DoesNotRetrySameDispatch_ButHigherDispatchReauthorizesOnce()
    {
        BridgeEventJournal journal = new(_dbPath);
        BridgeRecoveryCandidate candidate = await AddUnconfirmedGameResultAsync(
            journal,
            round: 14,
            roundId: 9990);
        AngelBridgeCommand firstCommand = Command(candidate, dispatchCount: 1);
        int firstRecoveryPosts = 0;
        using (BmsApiClient firstClient = new(new DelegateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/recoveries/check", StringComparison.Ordinal))
            {
                return Task.FromResult(CheckResponse(commands: [firstCommand]));
            }

            firstRecoveryPosts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html>proxy page</html>", Encoding.UTF8, "text/html")
            });
        })))
        {
            await firstClient.RunRecoveryCheckOnceAsync(Settings(), journal, _ => true, () => []);
        }

        Assert.Equal(1, firstRecoveryPosts);
        Assert.Equal("RecoveryUnconfirmed", ReadEventState(candidate.EventId).RecoveryState);
        Assert.Equal("RecoveryUnconfirmed", ReadAuditResult(firstCommand.CommandId));
        MakeReconciliationDue(candidate.EventId);

        int restartedRecoveryPosts = 0;
        int checkCount = 0;
        AngelBridgeCommand reauthorized = Command(candidate, dispatchCount: 2);
        AngelBridgeCommand tampered = reauthorized with
        {
            Round = candidate.Round + 1
        };
        BridgeEventJournal reopened = new(_dbPath);
        using BmsApiClient restartedClient = new(new DelegateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/recoveries/check", StringComparison.Ordinal))
            {
                checkCount++;
                AngelBridgeCommand returned = checkCount switch
                {
                    1 => firstCommand,
                    2 => tampered,
                    _ => reauthorized
                };
                return Task.FromResult(CheckResponse(commands: [returned]));
            }

            restartedRecoveryPosts++;
            return Task.FromResult(RecoveryAck(reauthorized, "Recovered"));
        }));

        await restartedClient.RunRecoveryCheckOnceAsync(Settings(), reopened, _ => true, () => []);
        Assert.Equal(0, restartedRecoveryPosts);

        await restartedClient.RunRecoveryCheckOnceAsync(Settings(), reopened, _ => true, () => []);
        Assert.Equal(0, restartedRecoveryPosts);

        await restartedClient.RunRecoveryCheckOnceAsync(Settings(), reopened, _ => true, () => []);
        Assert.Equal(1, restartedRecoveryPosts);

        await restartedClient.RunRecoveryCheckOnceAsync(Settings(), reopened, _ => true, () => []);
        Assert.Equal(1, restartedRecoveryPosts);
        Assert.Equal("Unconfirmed", ReadEventState(candidate.EventId).Status);
        Assert.Equal("Recovered", ReadEventState(candidate.EventId).RecoveryState);
    }

    [Fact]
    public async Task CrashAfterReservation_SameDispatchNeverPosts_HigherDispatchPostsOnce()
    {
        BridgeEventJournal journal = new(_dbPath);
        BridgeRecoveryCandidate candidate = await AddUnconfirmedGameResultAsync(
            journal,
            round: 141,
            roundId: 19990);
        AngelBridgeCommand firstDispatch = Command(candidate, dispatchCount: 1);

        // Simulate a process crash after the durable reservation and before the
        // event-state transition or the recovery HTTP POST.
        Assert.True(await journal.TryBeginRecoveryCommandAsync(RecoveryAudit(firstDispatch)));

        BridgeEventJournal reopened = new(_dbPath);
        AngelBridgeCommand higherDispatch = firstDispatch with { DispatchCount = 2 };
        int checkCount = 0;
        int recoveryPosts = 0;
        using BmsApiClient client = new(new DelegateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/recoveries/check", StringComparison.Ordinal))
            {
                checkCount++;
                return Task.FromResult(CheckResponse(commands:
                [
                    checkCount == 1 ? firstDispatch : higherDispatch
                ]));
            }

            recoveryPosts++;
            return Task.FromResult(RecoveryAck(higherDispatch, "Recovered"));
        }));

        await client.RunRecoveryCheckOnceAsync(Settings(), reopened, _ => true, () => []);
        Assert.Equal(0, recoveryPosts);

        await client.RunRecoveryCheckOnceAsync(Settings(), reopened, _ => true, () => []);
        Assert.Equal(1, recoveryPosts);

        await client.RunRecoveryCheckOnceAsync(Settings(), reopened, _ => true, () => []);
        Assert.Equal(1, recoveryPosts);
        Assert.Equal(2, ReadAuditDispatchCount(firstDispatch.CommandId));
        Assert.Equal("Recovered", ReadEventState(candidate.EventId).RecoveryState);
    }

    [Fact]
    public async Task ConcurrentDifferentCommands_ForSameEvent_AuthorizeOnlyOneAndReturnConflict()
    {
        BridgeEventJournal firstJournal = new(_dbPath);
        BridgeRecoveryCandidate candidate = await AddUnconfirmedGameResultAsync(
            firstJournal,
            round: 142,
            roundId: 19991);
        BridgeEventJournal secondJournal = new(_dbPath);
        AngelBridgeCommand first = Command(candidate, dispatchCount: 1);
        AngelBridgeCommand competing = first with
        {
            CommandId = first.CommandId + ":competing"
        };

        BridgeRecoveryReservationResult[] results = await Task.WhenAll(
            firstJournal.ReserveRecoveryCommandAsync(RecoveryAudit(first)),
            secondJournal.ReserveRecoveryCommandAsync(RecoveryAudit(competing)));

        Assert.Single(results, result =>
            result.Disposition == BridgeRecoveryReservationDisposition.Authorized);
        Assert.Single(results, result =>
            result.Disposition == BridgeRecoveryReservationDisposition.Conflict);
    }

    [Fact]
    public async Task DifferentCommandId_WhileExactEventIsActive_IsAuditedAsConflictAndNeverPosts()
    {
        BridgeEventJournal journal = new(_dbPath);
        BridgeRecoveryCandidate candidate = await AddUnconfirmedGameResultAsync(
            journal,
            round: 143,
            roundId: 19992);
        AngelBridgeCommand active = Command(candidate, dispatchCount: 1);
        Assert.True(await journal.TryBeginRecoveryCommandAsync(RecoveryAudit(active)));

        AngelBridgeCommand competing = active with
        {
            CommandId = active.CommandId + ":competing"
        };
        int recoveryPosts = 0;
        using BmsApiClient client = new(new DelegateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/recoveries/check", StringComparison.Ordinal))
            {
                return Task.FromResult(CheckResponse(commands: [competing]));
            }

            recoveryPosts++;
            return Task.FromResult(RecoveryAck(competing, "Recovered"));
        }));

        await client.RunRecoveryCheckOnceAsync(Settings(), journal, _ => true, () => []);

        Assert.Equal(0, recoveryPosts);
        Assert.Equal("Conflict", ReadAuditResult(competing.CommandId));
        Assert.Equal("RecoveryRequested", ReadAuditResult(active.CommandId));
    }

    [Fact]
    public async Task ReopenableTerminal_AllowsOnlyExplicitNextGeneration()
    {
        BridgeEventJournal journal = new(_dbPath);
        BridgeRecoveryCandidate candidate = await AddUnconfirmedGameResultAsync(
            journal,
            round: 144,
            roundId: 19993);
        AngelBridgeCommand first = Command(candidate, dispatchCount: 1);
        Assert.True(await journal.TryBeginRecoveryCommandAsync(RecoveryAudit(first)));
        Assert.True(await journal.MarkRecoveryRequestedAsync(
            candidate.EventId,
            candidate.EventUid,
            first.CommandId));
        await journal.MarkRecoverySubmissionOutcomeAsync(
            candidate.EventId,
            candidate.EventUid,
            first.CommandId,
            "Conflict",
            "operator revalidation required",
            DateTimeOffset.UtcNow);
        await journal.RecordRecoveryRequestAsync(RecoveryAudit(first) with
        {
            Result = "Conflict",
            Outcome = "Conflict",
            TerminalReason = "operator revalidation required"
        });

        AngelBridgeCommand staleGeneration = first with
        {
            CommandId = first.CommandId + ":stale"
        };
        BridgeRecoveryReservationResult stale = await journal.ReserveRecoveryCommandAsync(
            RecoveryAudit(staleGeneration));
        Assert.Equal(BridgeRecoveryReservationDisposition.Conflict, stale.Disposition);

        AngelBridgeCommand nextGeneration = first with
        {
            CommandId = first.CommandId + ":g2",
            Generation = 2
        };
        int recoveryPosts = 0;
        using BmsApiClient client = new(new DelegateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/recoveries/check", StringComparison.Ordinal))
            {
                return Task.FromResult(CheckResponse(commands: [nextGeneration]));
            }

            recoveryPosts++;
            return Task.FromResult(RecoveryAck(nextGeneration, "Recovered"));
        }));

        await client.RunRecoveryCheckOnceAsync(Settings(), journal, _ => true, () => []);

        Assert.Equal(1, recoveryPosts);
        Assert.Equal("Recovered", ReadEventState(candidate.EventId).RecoveryState);
        Assert.Equal("Recovered", ReadAuditResult(nextGeneration.CommandId));
    }

    [Theory]
    [InlineData("Recovered")]
    [InlineData("AlreadyAccepted")]
    [InlineData("Rejected")]
    public async Task NonReopenableTerminal_RejectsEveryLaterGeneration(string terminal)
    {
        BridgeEventJournal journal = new(_dbPath);
        BridgeRecoveryCandidate candidate = await AddUnconfirmedGameResultAsync(
            journal,
            round: 145,
            roundId: 19994);
        AngelBridgeCommand first = Command(candidate, dispatchCount: 1);
        Assert.True(await journal.TryBeginRecoveryCommandAsync(RecoveryAudit(first)));
        await journal.RecordRecoveryRequestAsync(RecoveryAudit(first) with
        {
            Result = terminal,
            Outcome = terminal,
            TerminalReason = terminal
        });

        AngelBridgeCommand nextGeneration = first with
        {
            CommandId = first.CommandId + ":g2",
            Generation = 2
        };
        BridgeRecoveryReservationResult result = await journal.ReserveRecoveryCommandAsync(
            RecoveryAudit(nextGeneration));

        Assert.Equal(BridgeRecoveryReservationDisposition.Conflict, result.Disposition);
    }

    [Fact]
    public async Task AckUnknown_ThenAlreadyAcceptedDecision_EndsWithoutAnotherPost()
    {
        BridgeEventJournal journal = new(_dbPath);
        BridgeRecoveryCandidate candidate = await AddUnconfirmedGameResultAsync(
            journal,
            round: 15,
            roundId: 9991);
        AngelBridgeCommand command = Command(candidate, dispatchCount: 1);
        int recoveryPosts = 0;
        int checkCount = 0;
        using BmsApiClient client = new(new DelegateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/recoveries/check", StringComparison.Ordinal))
            {
                checkCount++;
                if (checkCount == 1)
                {
                    return Task.FromResult(CheckResponse(commands: [command]));
                }

                return Task.FromResult(CheckResponse(decisions:
                [
                    Decision(
                        candidate,
                        "AlreadyAccepted",
                        command.CommandId,
                        generation: 1,
                        dispatchCount: 1)
                ]));
            }

            recoveryPosts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Empty)
            });
        }));

        await client.RunRecoveryCheckOnceAsync(Settings(), journal, _ => true, () => []);
        MakeReconciliationDue(candidate.EventId);
        await client.RunRecoveryCheckOnceAsync(Settings(), journal, _ => true, () => []);

        Assert.Equal(1, recoveryPosts);
        Assert.Equal("AlreadyAccepted", ReadEventState(candidate.EventId).RecoveryState);
        Assert.Equal("AlreadyAccepted", ReadAuditResult(command.CommandId));
    }

    [Fact]
    public async Task RecoverRoundWithoutExactIdentity_IsAuditedAndNeverPostsRecovery()
    {
        BridgeEventJournal journal = new(_dbPath);
        BridgeRecoveryCandidate candidate = await AddUnconfirmedGameResultAsync(
            journal,
            round: 16,
            roundId: 9992);
        AngelBridgeRecoveryDecision invalid = Decision(
            candidate,
            "RecoverRound",
            commandId: string.Empty,
            generation: 0,
            dispatchCount: 0);
        int recoveryPosts = 0;
        using BmsApiClient client = new(new DelegateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/recoveries/check", StringComparison.Ordinal))
            {
                return Task.FromResult(CheckResponse(decisions: [invalid]));
            }

            recoveryPosts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }));

        await client.RunRecoveryCheckOnceAsync(Settings(), journal, _ => true, () => []);

        Assert.Equal(0, recoveryPosts);
        Assert.Equal("Conflict", ReadEventState(candidate.EventId).RecoveryState);
    }

    [Fact]
    public async Task AuthorizedMissingResult_PostsNotFoundWithoutGameResult_AndAuditsTerminal()
    {
        BridgeEventJournal journal = new(_dbPath);
        string eventUid = Guid.NewGuid().ToString("D");
        AngelBridgeCommand command = new()
        {
            CommandId = "recover:901:202607250001:99",
            Type = "RecoverRound",
            SourceDataCode = "901",
            DeviceId = "SHOE901",
            EventId = 999,
            EventUid = eventUid,
            Shoe = 202607250001,
            Round = 99,
            RoundId = 9999,
            Generation = 1,
            DispatchCount = 1
        };
        string recoveryJson = string.Empty;
        using BmsApiClient client = new(new DelegateHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/recoveries/check", StringComparison.Ordinal))
            {
                return CheckResponse(commands: [command]);
            }

            recoveryJson = await request.Content!.ReadAsStringAsync();
            return RecoveryAck(command, "NotFound");
        }));

        await client.RunRecoveryCheckOnceAsync(Settings(), journal, _ => true, () => []);

        using JsonDocument submission = JsonDocument.Parse(recoveryJson);
        Assert.Equal("NotFound", submission.RootElement.GetProperty("outcome").GetString());
        Assert.False(submission.RootElement.TryGetProperty("gameResult", out _));
        Assert.Equal("NotFound", ReadAuditResult(command.CommandId));
    }

    [Fact]
    public async Task StartGameSummary_CannotAuthorizeRecoverRound()
    {
        BridgeEventJournal journal = new(_dbPath);
        Guid eventUid = Guid.NewGuid();
        long eventId = await journal.AppendAsync(new Dictionary<string, object?>
        {
            ["eventUid"] = eventUid,
            ["type"] = "StartGame",
            ["source"] = "ANGEL",
            ["timestamp"] = "2026-07-25T02:50:00Z",
            ["sourceDataCode"] = "901",
            ["deviceId"] = "SHOE901",
            ["shoe"] = 202607250001,
            ["round"] = 11,
            ["roundId"] = 9987,
            ["data"] = new { startTime = "2026-07-25T02:50:00Z" }
        });
        DateTime attemptedAt = new(2026, 7, 25, 2, 50, 0, DateTimeKind.Utc);
        Assert.True(await journal.TryClaimForDeliveryAsync(eventId, attemptedAt));
        await journal.MarkUnconfirmedAsync(eventId, 1, attemptedAt, "timeout");
        BridgeRecoveryCandidate candidate = new(
            eventId,
            eventUid.ToString("D"),
            "StartGame",
            "901",
            "SHOE901",
            202607250001,
            11,
            9987,
            new DateTimeOffset(attemptedAt),
            0,
            "Unconfirmed");
        AngelBridgeRecoveryDecision invalid = Decision(
            candidate,
            "RecoverRound",
            "recover:901:202607250001:11",
            generation: 1,
            dispatchCount: 1);
        int recoveryPosts = 0;
        using BmsApiClient client = new(new DelegateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/recoveries/check", StringComparison.Ordinal))
            {
                return Task.FromResult(CheckResponse(decisions: [invalid]));
            }

            recoveryPosts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }));

        await client.RunRecoveryCheckOnceAsync(Settings(), journal, _ => true, () => []);

        Assert.Equal(0, recoveryPosts);
        Assert.Equal("Conflict", ReadEventState(eventId).RecoveryState);
    }

    [Fact]
    public async Task BearerTokenProvider_IsResolvedAtRequestTime_ForAllThreeEndpoints()
    {
        BridgeEventJournal journal = new(_dbPath);
        BridgeRecoveryCandidate candidate = await AddUnconfirmedGameResultAsync(
            journal,
            round: 17,
            roundId: 9993);
        AngelBridgeCommand recoveryCommand = Command(candidate, dispatchCount: 1);
        long startEventId = await AddPendingStartGameAsync(journal, round: 18, roundId: 9994);
        var observed = new List<(string Path, string Token)>();
        SequenceAccessTokenProvider tokenProvider = new();
        using BmsApiClient client = new(new DelegateHandler(async request =>
        {
            observed.Add((
                request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.Parameter ?? string.Empty));
            if (request.RequestUri.AbsolutePath.EndsWith("/recoveries/check", StringComparison.Ordinal))
            {
                return CheckResponse(commands: [recoveryCommand]);
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/recoveries", StringComparison.Ordinal))
            {
                return RecoveryAck(recoveryCommand, "Recovered");
            }

            string payloadJson = await request.Content!.ReadAsStringAsync();
            using JsonDocument payload = JsonDocument.Parse(payloadJson);
            return JsonResponse(new
            {
                errCode = 0,
                data = new
                {
                    accepted = true,
                    duplicate = false,
                    eventId = payload.RootElement.GetProperty("eventId").GetInt64(),
                    eventUid = payload.RootElement.GetProperty("eventUid").GetString()
                }
            });
        }));

        await client.RunRecoveryCheckOnceAsync(
            Settings(),
            journal,
            _ => true,
            () => [],
            accessTokenProvider: tokenProvider);
        Assert.Equal(1, await client.RunDispatchOnceAsync(
            Settings(),
            journal,
            _ => true,
            accessTokenProvider: tokenProvider));

        Assert.Equal(
            [
                ("/api/source/angel/recoveries/check", "rotated-token-1"),
                ("/api/source/angel/recoveries", "rotated-token-2"),
                ("/api/source/angel/events", "rotated-token-3")
            ],
            observed);
        Assert.Equal("Sent", ReadEventState(startEventId).Status);
    }

    [Fact]
    public async Task RecoveryIdentityConflict_IsReportedThroughDedicatedEndpoint()
    {
        BridgeEventJournal journal = new(_dbPath);
        BridgeRecoveryCandidate candidate = await AddUnconfirmedGameResultAsync(
            journal,
            round: 19,
            roundId: 9995);
        AngelBridgeCommand command = Command(candidate, dispatchCount: 1) with
        {
            Round = candidate.Round + 1
        };
        string recoveryJson = string.Empty;
        using BmsApiClient client = new(new DelegateHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/recoveries/check", StringComparison.Ordinal))
            {
                return CheckResponse(commands: [command]);
            }

            recoveryJson = await request.Content!.ReadAsStringAsync();
            return RecoveryAck(command, "Conflict");
        }));

        await client.RunRecoveryCheckOnceAsync(Settings(), journal, _ => true, () => []);

        using JsonDocument submission = JsonDocument.Parse(recoveryJson);
        Assert.Equal("Conflict", submission.RootElement.GetProperty("outcome").GetString());
        Assert.False(submission.RootElement.TryGetProperty("gameResult", out _));
        Assert.Contains(
            "identity",
            submission.RootElement.GetProperty("message").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Conflict", ReadAuditResult(command.CommandId));
    }

    [Fact]
    public async Task RecoveryPayloadConflict_RequiresExactConflictAck_ThenAcceptsStickyDecision()
    {
        BridgeEventJournal journal = new(_dbPath);
        BridgeRecoveryCandidate candidate = await AddUnconfirmedGameResultAsync(
            journal,
            round: 20,
            roundId: 9996);
        TamperPayloadRound(candidate.EventId, candidate.Round + 100);
        AngelBridgeCommand command = Command(candidate, dispatchCount: 1);
        int checkCount = 0;
        int recoveryPosts = 0;
        string recoveryJson = string.Empty;
        using BmsApiClient client = new(new DelegateHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/recoveries/check", StringComparison.Ordinal))
            {
                checkCount++;
                return checkCount == 1
                    ? CheckResponse(commands: [command])
                    : CheckResponse(decisions:
                    [
                        Decision(
                            candidate,
                            "Conflict",
                            command.CommandId,
                            generation: 1,
                            dispatchCount: 1)
                    ]);
            }

            recoveryPosts++;
            recoveryJson = await request.Content!.ReadAsStringAsync();
            // A successful HTTP response with the wrong outcome is ACK-unknown.
            return RecoveryAck(command, "Recovered");
        }));

        await client.RunRecoveryCheckOnceAsync(Settings(), journal, _ => true, () => []);

        using (JsonDocument submission = JsonDocument.Parse(recoveryJson))
        {
            Assert.Equal("Conflict", submission.RootElement.GetProperty("outcome").GetString());
            Assert.False(submission.RootElement.TryGetProperty("gameResult", out _));
        }
        Assert.Equal("RecoveryUnconfirmed", ReadEventState(candidate.EventId).RecoveryState);
        Assert.Equal("RecoveryUnconfirmed", ReadAuditResult(command.CommandId));

        MakeReconciliationDue(candidate.EventId);
        await client.RunRecoveryCheckOnceAsync(Settings(), journal, _ => true, () => []);

        Assert.Equal(1, recoveryPosts);
        Assert.Equal("Conflict", ReadEventState(candidate.EventId).RecoveryState);
        Assert.Equal("Conflict", ReadAuditResult(command.CommandId));
    }

    [Fact]
    public async Task RecoveryStateConflict_IsReportedThroughDedicatedEndpoint()
    {
        BridgeEventJournal journal = new(_dbPath);
        BridgeRecoveryCandidate candidate = await AddUnconfirmedGameResultAsync(
            journal,
            round: 21,
            roundId: 9997);
        SetRecoveryState(candidate.EventId, "AlreadyAccepted");
        AngelBridgeCommand command = Command(candidate, dispatchCount: 1);
        string recoveryJson = string.Empty;
        using BmsApiClient client = new(new DelegateHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/recoveries/check", StringComparison.Ordinal))
            {
                return CheckResponse(commands: [command]);
            }

            recoveryJson = await request.Content!.ReadAsStringAsync();
            return RecoveryAck(command, "Conflict");
        }));

        await client.RunRecoveryCheckOnceAsync(Settings(), journal, _ => true, () => []);

        using JsonDocument submission = JsonDocument.Parse(recoveryJson);
        Assert.Equal("Conflict", submission.RootElement.GetProperty("outcome").GetString());
        Assert.Contains(
            "state",
            submission.RootElement.GetProperty("message").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Conflict", ReadAuditResult(command.CommandId));
        Assert.Equal("AlreadyAccepted", ReadEventState(candidate.EventId).RecoveryState);
    }

    [Theory]
    [InlineData("Recovered")]
    [InlineData("NotFound")]
    [InlineData("Cancelled")]
    [InlineData("Expired")]
    [InlineData("ManualReview")]
    public async Task StickyTerminalDecision_SurvivesRestart_AndStopsPolling(string decision)
    {
        BridgeEventJournal journal = new(_dbPath);
        BridgeRecoveryCandidate candidate = await AddUnconfirmedGameResultAsync(
            journal,
            round: 22,
            roundId: 9998);
        AngelBridgeCommand command = Command(candidate, dispatchCount: 1);
        int recoveryPosts = 0;
        using (BmsApiClient firstClient = new(new DelegateHandler(request =>
               {
                   if (request.RequestUri!.AbsolutePath.EndsWith("/recoveries/check", StringComparison.Ordinal))
                   {
                       return Task.FromResult(CheckResponse(commands: [command]));
                   }

                   recoveryPosts++;
                   return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                   {
                       Content = new StringContent(string.Empty)
                   });
               })))
        {
            await firstClient.RunRecoveryCheckOnceAsync(Settings(), journal, _ => true, () => []);
        }

        Assert.Equal("RecoveryUnconfirmed", ReadEventState(candidate.EventId).RecoveryState);
        MakeReconciliationDue(candidate.EventId);
        BridgeEventJournal reopened = new(_dbPath);
        using BmsApiClient secondClient = new(new DelegateHandler(request =>
            Task.FromResult(CheckResponse(decisions:
            [
                Decision(
                    candidate,
                    decision,
                    command.CommandId,
                    generation: command.Generation,
                    dispatchCount: command.DispatchCount)
            ]))));
        await secondClient.RunRecoveryCheckOnceAsync(Settings(), reopened, _ => true, () => []);

        Assert.Equal(1, recoveryPosts);
        Assert.Equal(decision, ReadEventState(candidate.EventId).RecoveryState);
        Assert.Empty(await reopened.GetDueRecoveryCandidatesAsync(20, DateTimeOffset.UtcNow.AddDays(1)));
    }

    [Fact]
    public async Task TerminalDecision_FromOlderGeneration_CannotTerminateLatestCommand()
    {
        BridgeEventJournal journal = new(_dbPath);
        BridgeRecoveryCandidate candidate = await AddUnconfirmedGameResultAsync(
            journal,
            round: 23,
            roundId: 9999);
        AngelBridgeCommand first = Command(candidate, dispatchCount: 1);
        Assert.True(await journal.TryBeginRecoveryCommandAsync(RecoveryAudit(first)));
        Assert.True(await journal.MarkRecoveryRequestedAsync(
            candidate.EventId,
            candidate.EventUid,
            first.CommandId));
        await journal.MarkRecoverySubmissionOutcomeAsync(
            candidate.EventId,
            candidate.EventUid,
            first.CommandId,
            "Conflict",
            "operator revalidation required",
            DateTimeOffset.UtcNow);
        await journal.RecordRecoveryRequestAsync(RecoveryAudit(first) with
        {
            Result = "Conflict",
            Outcome = "Conflict",
            TerminalReason = "operator revalidation required"
        });
        AngelBridgeCommand latest = first with
        {
            CommandId = first.CommandId + ":g2",
            Generation = 2,
            DispatchCount = 1
        };
        Assert.True(await journal.TryBeginRecoveryCommandAsync(RecoveryAudit(latest)));
        Assert.True(await journal.MarkRecoveryRequestedAsync(
            candidate.EventId,
            candidate.EventUid,
            latest.CommandId));
        await journal.MarkRecoverySubmissionOutcomeAsync(
            candidate.EventId,
            candidate.EventUid,
            latest.CommandId,
            "RecoveryUnconfirmed",
            "ack unknown",
            DateTimeOffset.UtcNow);
        await journal.RecordRecoveryRequestAsync(RecoveryAudit(latest) with
        {
            Result = "RecoveryUnconfirmed",
            Outcome = "RecoveryUnconfirmed"
        });
        MakeReconciliationDue(candidate.EventId);

        using BmsApiClient client = new(new DelegateHandler(request =>
            Task.FromResult(CheckResponse(decisions:
            [
                Decision(
                    candidate,
                    "Conflict",
                    first.CommandId,
                    generation: first.Generation,
                    dispatchCount: first.DispatchCount)
            ]))));
        await client.RunRecoveryCheckOnceAsync(Settings(), journal, _ => true, () => []);

        Assert.Equal("RecoveryUnconfirmed", ReadEventState(candidate.EventId).RecoveryState);
        Assert.Equal("RecoveryUnconfirmed", ReadAuditResult(latest.CommandId));
    }

    [Fact]
    public async Task TerminalDecision_WithOlderDispatch_CannotTerminateLatestDispatch()
    {
        BridgeEventJournal journal = new(_dbPath);
        BridgeRecoveryCandidate candidate = await AddUnconfirmedGameResultAsync(
            journal,
            round: 24,
            roundId: 10000);
        AngelBridgeCommand latest = Command(candidate, dispatchCount: 2);
        await MakeRecoveryUnconfirmedAsync(journal, latest);
        MakeReconciliationDue(candidate.EventId);

        using BmsApiClient client = new(new DelegateHandler(request =>
            Task.FromResult(CheckResponse(decisions:
            [
                Decision(
                    candidate,
                    "NotFound",
                    latest.CommandId,
                    generation: latest.Generation,
                    dispatchCount: 1)
            ]))));
        await client.RunRecoveryCheckOnceAsync(Settings(), journal, _ => true, () => []);

        Assert.Equal("RecoveryUnconfirmed", ReadEventState(candidate.EventId).RecoveryState);
        Assert.Equal("RecoveryUnconfirmed", ReadAuditResult(latest.CommandId));
    }

    [Fact]
    public async Task TerminalDecision_WithUnrecordedHigherDispatch_CannotTerminateStoredDispatch()
    {
        BridgeEventJournal journal = new(_dbPath);
        BridgeRecoveryCandidate candidate = await AddUnconfirmedGameResultAsync(
            journal,
            round: 25,
            roundId: 10001);
        AngelBridgeCommand stored = Command(candidate, dispatchCount: 1);
        await MakeRecoveryUnconfirmedAsync(journal, stored);
        MakeReconciliationDue(candidate.EventId);

        using BmsApiClient client = new(new DelegateHandler(request =>
            Task.FromResult(CheckResponse(decisions:
            [
                Decision(
                    candidate,
                    "NotFound",
                    stored.CommandId,
                    generation: stored.Generation,
                    dispatchCount: 2)
            ]))));
        await client.RunRecoveryCheckOnceAsync(Settings(), journal, _ => true, () => []);

        Assert.Equal("RecoveryUnconfirmed", ReadEventState(candidate.EventId).RecoveryState);
        Assert.Equal("RecoveryUnconfirmed", ReadAuditResult(stored.CommandId));
        Assert.Equal(1, ReadAuditDispatchCount(stored.CommandId));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private async Task<BridgeRecoveryCandidate> AddUnconfirmedGameResultAsync(
        BridgeEventJournal journal,
        long round,
        long roundId,
        string bridgeId = "QA-29")
    {
        Guid eventUid = Guid.NewGuid();
        long eventId = await journal.AppendAsync(new Dictionary<string, object?>
        {
            ["eventUid"] = eventUid,
            ["bridgeId"] = bridgeId,
            ["type"] = "GameResult",
            ["source"] = "ANGEL",
            ["timestamp"] = "2026-07-25T02:52:00Z",
            ["sourceDataCode"] = "901",
            ["deviceId"] = "SHOE901",
            ["shoe"] = 202607250001,
            ["round"] = round,
            ["roundId"] = roundId,
            ["data"] = new
            {
                sourceTimestamp = "2026-07-25T02:52:00Z",
                cards = new
                {
                    p1 = "Qh",
                    p2 = "5h",
                    p3 = "As",
                    b1 = "3s",
                    b2 = "3h",
                    b3 = ""
                },
                winner = "PlayerWin",
                pair = "BankerPair",
                status = "Normal"
            }
        });
        DateTime attempt = new(2026, 7, 25, 2, 52, 0, DateTimeKind.Utc);
        Assert.True(await journal.TryClaimForDeliveryAsync(eventId, attempt));
        await journal.MarkUnconfirmedAsync(eventId, 1, attempt, "timeout");
        return new BridgeRecoveryCandidate(
            eventId,
            eventUid.ToString("D"),
            "GameResult",
            "901",
            "SHOE901",
            202607250001,
            round,
            roundId,
            new DateTimeOffset(attempt),
            DecisionCount: 0,
            ReconciliationState: "Unconfirmed");
    }

    private static Task<long> AddPendingStartGameAsync(
        BridgeEventJournal journal,
        long round,
        long roundId) =>
        journal.AppendAsync(new Dictionary<string, object?>
        {
            ["eventUid"] = Guid.NewGuid(),
            ["type"] = "StartGame",
            ["source"] = "ANGEL",
            ["timestamp"] = "2026-07-25T02:53:00Z",
            ["sourceDataCode"] = "901",
            ["deviceId"] = "SHOE901",
            ["shoe"] = 202607250001,
            ["round"] = round,
            ["roundId"] = roundId,
            ["data"] = new { startTime = "2026-07-25T02:53:00Z" }
        });

    private static AngelBridgeCommand Command(
        BridgeRecoveryCandidate candidate,
        int dispatchCount) => new()
    {
        CommandId = $"recover:901:{candidate.Shoe}:{candidate.Round}",
        Type = "RecoverRound",
        SourceDataCode = candidate.SourceDataCode,
        DeviceId = candidate.DeviceId,
        EventId = candidate.EventId,
        EventUid = candidate.EventUid,
        Shoe = candidate.Shoe,
        Round = candidate.Round,
        RoundId = candidate.RoundId,
        Generation = 1,
        DispatchCount = dispatchCount
    };

    private static AngelBridgeRecoveryDecision Decision(
        BridgeRecoveryCandidate candidate,
        string decision,
        string commandId = "",
        int generation = 0,
        int dispatchCount = 0) => new()
    {
        EventId = candidate.EventId,
        EventUid = candidate.EventUid,
        Decision = decision,
        CommandId = commandId,
        SourceDataCode = candidate.SourceDataCode,
        DeviceId = candidate.DeviceId,
        Shoe = candidate.Shoe,
        Round = candidate.Round,
        RoundId = candidate.RoundId,
        Generation = generation,
        DispatchCount = dispatchCount,
        Message = decision
    };

    private static BridgeRecoveryAudit RecoveryAudit(AngelBridgeCommand command)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new BridgeRecoveryAudit(
            command.CommandId,
            "RecoverRound",
            command.SourceDataCode,
            command.DeviceId,
            command.Shoe,
            command.Round,
            command.RoundId,
            now,
            now,
            "RecoveryRequested",
            NextRetryUtc: null,
            Message: "test authorization",
            EventId: command.EventId,
            EventUid: command.EventUid,
            Outcome: "RecoveryRequested",
            DecisionCount: 1,
            Generation: command.Generation,
            DispatchCount: command.DispatchCount);
    }

    private static async Task MakeRecoveryUnconfirmedAsync(
        BridgeEventJournal journal,
        AngelBridgeCommand command)
    {
        using BmsApiClient client = new(new DelegateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/recoveries/check", StringComparison.Ordinal))
            {
                return Task.FromResult(CheckResponse(commands: [command]));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Empty)
            });
        }));
        await client.RunRecoveryCheckOnceAsync(Settings(), journal, _ => true, () => []);
    }

    private static HttpResponseMessage CheckResponse(
        IReadOnlyList<AngelBridgeCommand>? commands = null,
        IReadOnlyList<AngelBridgeRecoveryDecision>? decisions = null) => JsonResponse(new
    {
        errCode = 0,
        data = new
        {
            accepted = true,
            nextPollSeconds = 15,
            commands = commands ?? [],
            decisions = decisions ?? []
        }
    });

    private static HttpResponseMessage RecoveryAck(
        AngelBridgeCommand command,
        string outcome) => JsonResponse(new
    {
        errCode = 0,
        data = new
        {
            accepted = true,
            commandId = command.CommandId,
            generation = command.Generation,
            dispatchCount = command.DispatchCount,
            eventUid = command.EventUid,
            outcome,
            duplicate = outcome == "Duplicate",
            state = outcome,
            message = outcome
        }
    });

    private static HttpResponseMessage JsonResponse(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json")
    };

    private static BmsApiSettings Settings() => new(
        "https://bms.test/api/source/angel/events",
        "test-token",
        "QA-29",
        "AngelEyeBridge",
        "QA");

    private (string Status, string RecoveryState) ReadEventState(long eventId)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT status, reconciliation_state
            FROM bridge_events
            WHERE event_id = $event_id;
            """;
        command.Parameters.AddWithValue("$event_id", eventId);
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (
            reader.GetString(0),
            reader.IsDBNull(1) ? string.Empty : reader.GetString(1));
    }

    private string ReadRetainedGameResultJson(long eventId)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload_json
            FROM bridge_events
            WHERE event_id = $event_id;
            """;
        command.Parameters.AddWithValue("$event_id", eventId);
        using JsonDocument payload = JsonDocument.Parse(
            Assert.IsType<string>(command.ExecuteScalar()));
        return payload.RootElement.GetProperty("data").GetRawText();
    }

    private string ReadAuditResult(string commandId)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT result
            FROM bridge_recovery_requests
            WHERE command_id = $command_id;
            """;
        command.Parameters.AddWithValue("$command_id", commandId);
        return Assert.IsType<string>(command.ExecuteScalar());
    }

    private int ReadAuditDispatchCount(string commandId)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT dispatch_count
            FROM bridge_recovery_requests
            WHERE command_id = $command_id;
            """;
        command.Parameters.AddWithValue("$command_id", commandId);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private void MakeReconciliationDue(long eventId)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE bridge_events
            SET next_reconcile_utc = '2000-01-01T00:00:00.0000000Z',
                unconfirmed_since_utc = $unconfirmed_since_utc
            WHERE event_id = $event_id;
            """;
        command.Parameters.AddWithValue(
            "$unconfirmed_since_utc",
            DateTimeOffset.UtcNow.UtcDateTime.ToString("o"));
        command.Parameters.AddWithValue("$event_id", eventId);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private void TamperPayloadRound(long eventId, long round)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand select = connection.CreateCommand();
        select.CommandText = "SELECT payload_json FROM bridge_events WHERE event_id = $event_id;";
        select.Parameters.AddWithValue("$event_id", eventId);
        string payloadJson = Assert.IsType<string>(select.ExecuteScalar());
        JsonObject payload = Assert.IsType<JsonObject>(JsonNode.Parse(payloadJson));
        payload["round"] = round;

        using SqliteCommand update = connection.CreateCommand();
        update.CommandText = """
            UPDATE bridge_events
            SET payload_json = $payload_json
            WHERE event_id = $event_id;
            """;
        update.Parameters.AddWithValue("$payload_json", payload.ToJsonString());
        update.Parameters.AddWithValue("$event_id", eventId);
        Assert.Equal(1, update.ExecuteNonQuery());
    }

    private void SetRecoveryState(long eventId, string state)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE bridge_events
            SET reconciliation_state = $state,
                next_reconcile_utc = NULL
            WHERE event_id = $event_id;
            """;
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$event_id", eventId);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private SqliteConnection Open()
    {
        SqliteConnection connection = new($"Data Source={_dbPath}");
        connection.Open();
        return connection;
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return handler(request);
        }
    }

    private sealed class SequenceAccessTokenProvider : IBmsAccessTokenProvider
    {
        private int _sequence;

        public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                $"rotated-token-{Interlocked.Increment(ref _sequence)}");
        }

        public void InvalidateAccessToken(string rejectedAccessToken)
        {
        }
    }
}
