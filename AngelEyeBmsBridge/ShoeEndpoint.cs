using System.Drawing;
using System.Text;

namespace AngelEyeBmsBridge;

/// <summary>
/// Coordinates one ANGEL shoe endpoint, including serial connection, local game numbering, preview state, and simulator events.
/// </summary>
public sealed class ShoeEndpoint
{
    private byte _simSeqCt = 0x31;
    private bool _roundInProgress;
    private bool _roundSettled = true;
    private readonly object _stateTransitionGate = new();

    /// <summary>
    /// Creates an endpoint backed by the provided persistent settings.
    /// </summary>
    /// <param name="settings">Persistent endpoint settings.</param>
    public ShoeEndpoint(ShoeEndpointSettings settings)
    {
        Settings = settings;
        EnsureGameNumberInitialized();
        Listener.OnStatusChanged += value =>
            ExecuteSerializedStateTransition(() => HandleStatusChanged(value));
        Listener.OnProtocolSignalObserved += value =>
            ExecuteSerializedStateTransition(() => ProtocolSignalObserved?.Invoke(this, value));
        Listener.OnTransportStateChanged += value =>
            ExecuteSerializedStateTransition(() => HandleTransportStateChanged(value));
        Listener.OnRawDataLogged += (type, data) => LogReceived?.Invoke(this, type, data);
        Listener.OnCardDrawn += value =>
            ExecuteSerializedStateTransition(() => HandleCardDrawn(value));
        Listener.OnGameResult += value =>
            ExecuteSerializedStateTransition(() => HandleGameResult(value));
        Listener.OnErrorOccurred += value =>
            ExecuteSerializedStateTransition(() => HandleErrorOccurred(value));
        Listener.OnLockStatusChanged += value =>
            ExecuteSerializedStateTransition(() => HandleLockStatusChanged(value));
        Listener.OnErrorCleared += (code, message) =>
            ExecuteSerializedStateTransition(() => HandleErrorCleared(code, message));
        Listener.OnCommandAcknowledged += value =>
            ExecuteSerializedStateTransition(() => HandleCommandAcknowledged(value));
        Listener.OnCuttingCardDrawn += value =>
            ExecuteSerializedStateTransition(() => HandleCuttingCardDrawn(value));
    }

    /// <summary>
    /// Runs one endpoint lifecycle transition under the same re-entrant gate used by
    /// decoded protocol, transport, timer, and operator events.
    /// </summary>
    public void ExecuteSerializedStateTransition(Action transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        lock (_stateTransitionGate)
        {
            transition();
        }
    }

    /// <summary>Persistent settings used by this endpoint.</summary>
    public ShoeEndpointSettings Settings { get; }

    /// <summary>Serial protocol listener used by this endpoint.</summary>
    public SerialListener Listener { get; } = new();

    /// <summary>BMS system desk GUID.</summary>
    public string DeskId
    {
        get => Settings.DeskId;
        set => Settings.DeskId = value.Trim();
    }

    /// <summary>Human-readable desk name shown in the UI.</summary>
    public string DeskName
    {
        get => Settings.DeskName;
        set => Settings.DeskName = value.Trim();
    }

    /// <summary>BMS SourceData GUID.</summary>
    public string SourceDataId
    {
        get => Settings.SourceDataId;
        set => Settings.SourceDataId = value.Trim();
    }

    /// <summary>BMS source table code.</summary>
    public string SourceDataCode
    {
        get => Settings.SourceDataCode;
        set => Settings.SourceDataCode = value.Trim();
    }

    /// <summary>Bridge-side shoe device identifier.</summary>
    public string ShoeId
    {
        get => Settings.ShoeId;
        set => Settings.ShoeId = value.Trim();
    }

    /// <summary>Device identifier sent to BMS; currently matches <see cref="ShoeId"/>.</summary>
    public string DeviceId => ShoeId;

    /// <summary>Current BMS shoe number.</summary>
    public long CurrentShoe
    {
        get => Settings.CurrentShoe;
        private set => Settings.CurrentShoe = value;
    }

    /// <summary>Current BMS round number.</summary>
    public long CurrentRound
    {
        get => Settings.CurrentRound;
        private set => Settings.CurrentRound = value;
    }

    /// <summary>Current bridge round identifier, when a round exists.</summary>
    public long? CurrentRoundId
    {
        get => Settings.CurrentRoundId;
        private set => Settings.CurrentRoundId = value;
    }

    /// <summary>Combined shoe and round display value.</summary>
    public string ShoeRound => CurrentShoe > 0 && CurrentRound > 0 ? $"{CurrentShoe}{CurrentRound}" : string.Empty;

    /// <summary>Assigned local serial port.</summary>
    public string ComPort
    {
        get => Settings.ComPort;
        set => Settings.ComPort = value.Trim();
    }

    /// <summary>Physical connection mode for this endpoint.</summary>
    public string ConnectionMode
    {
        get => ShoeConnectionMode.Normalize(Settings.ConnectionMode);
        set => Settings.ConnectionMode = ShoeConnectionMode.Normalize(value);
    }

    /// <summary>Gets whether this endpoint connects directly to a MOXA/NPort TCP server.</summary>
    public bool IsMoxaTcpMode => string.Equals(ConnectionMode, ShoeConnectionMode.MoxaTcp, StringComparison.OrdinalIgnoreCase);

    /// <summary>MOXA/NPort TCP server host or IP address.</summary>
    public string MoxaHost
    {
        get => Settings.MoxaHost;
        set => Settings.MoxaHost = value.Trim();
    }

    /// <summary>MOXA/NPort TCP server port.</summary>
    public int MoxaPort
    {
        get => Settings.MoxaPort <= 0 ? 4001 : Settings.MoxaPort;
        set => Settings.MoxaPort = Math.Clamp(value, 1, 65535);
    }

    /// <summary>Human-readable connection endpoint shown in the UI and diagnostics.</summary>
    public string ConnectionDisplay =>
        MockMode ? "MOCK" :
        IsMoxaTcpMode ? (string.IsNullOrWhiteSpace(MoxaHost) ? "MOXA 未設定" : $"MOXA {MoxaHost}:{MoxaPort}") :
        (string.IsNullOrWhiteSpace(ComPort) ? string.Empty : ComPort);

    /// <summary>Compact connection display label used by the live preview status strip.</summary>
    public string ConnectionDisplayLabel =>
        MockMode ? "模式 Mock" :
        IsMoxaTcpMode ? $"MOXA {(string.IsNullOrWhiteSpace(MoxaHost) ? "未設定" : $"{MoxaHost}:{MoxaPort}")}" :
        $"COM {(string.IsNullOrWhiteSpace(ComPort) ? "未設定" : ComPort)}";

    /// <summary>Whether this endpoint is enabled for connection and API posting.</summary>
    public bool Enabled
    {
        get => Settings.Enabled;
        set => Settings.Enabled = value;
    }

    /// <summary>Whether this endpoint should post its events to the BMS event API.</summary>
    public bool BmsTransmitEnabled
    {
        get => Settings.BmsTransmitEnabled;
        set => Settings.BmsTransmitEnabled = value;
    }

    /// <summary>Whether this endpoint uses the built-in simulator instead of a physical serial port.</summary>
    public bool MockMode
    {
        get => Settings.MockMode;
        set => Settings.MockMode = value;
    }

    /// <summary>Betting countdown length for this endpoint. Zero means no betting window.</summary>
    public int TotalBetTimeSeconds
    {
        get => Math.Clamp(Settings.TotalBetTimeSeconds, 0, 120);
        set => Settings.TotalBetTimeSeconds = Math.Clamp(value, 0, 120);
    }

    /// <summary>Gets whether the endpoint is connected or in active mock mode.</summary>
    public bool IsConnected { get; private set; }

    /// <summary>Gets the last known key lock state, or null when unknown.</summary>
    public bool? IsLocked { get; private set; }

    /// <summary>Gets whether the shoe currently reports error mode.</summary>
    public bool InErrorMode { get; private set; }

    /// <summary>Gets whether the cutting card has been drawn and the shoe is ending.</summary>
    public bool ShoeEnding { get; private set; }

    /// <summary>Durable physical-round phase used to fail closed across process restarts.</summary>
    public string RoundPhase { get; private set; } = BridgeRoundPhases.AwaitingSynchronization;

    /// <summary>Boundary policy that armed the current round.</summary>
    public string BoundaryStrategy { get; private set; } = BridgeBoundaryStrategies.DisabledUntilValidated;

    /// <summary>UTC timestamp of the boundary that armed the current round.</summary>
    public DateTimeOffset? BoundaryObservedAtUtc { get; private set; }

    /// <summary>Stable StartGame identity allocated with the current round boundary.</summary>
    public Guid? StartGameEventUid { get; private set; }

    /// <summary>Last durable StartGame delivery state for the current round.</summary>
    public string StartGameDeliveryState { get; private set; } = string.Empty;

    /// <summary>UTC timestamp of the last physical final-result telegram.</summary>
    public DateTimeOffset? LastFinalResultAtUtc { get; private set; }

    /// <summary>Non-sensitive reason why the endpoint requires manual alignment.</summary>
    public string AlignmentReason { get; private set; } = string.Empty;

    /// <summary>Last idempotency key used to confirm a physical new shoe.</summary>
    public string LastNewShoeActionId { get; private set; } = string.Empty;

    /// <summary>Non-sensitive operator reason for the last new-shoe confirmation.</summary>
    public string LastNewShoeReason { get; private set; } = string.Empty;

    /// <summary>UTC time of the last audited new-shoe confirmation.</summary>
    public DateTimeOffset? LastNewShoeConfirmedAtUtc { get; private set; }

    /// <summary>
    /// Gets whether terminal telegrams must remain quarantined until the first
    /// authoritative result of a newly confirmed shoe.
    /// </summary>
    public bool AwaitingFirstAuthoritativeResultAfterShoeChange { get; private set; }

    /// <summary>Gets the last shoe error code, if any.</summary>
    public int? ErrorCode { get; private set; }

    /// <summary>Gets the last shoe error message.</summary>
    public string ErrorMessage { get; private set; } = string.Empty;

    /// <summary>Gets the current connection or command status text.</summary>
    public string StatusText { get; private set; } = "未連線";

    /// <summary>Gets when the last endpoint event was observed.</summary>
    public DateTime? LastEventAt { get; private set; }

    /// <summary>Gets when the last card or card-status telegram was observed.</summary>
    public DateTimeOffset? LastCardAtUtc { get; private set; }

    /// <summary>Gets the last endpoint event summary.</summary>
    public string LastEventText { get; private set; } = string.Empty;

    /// <summary>Player-side cards currently shown in the preview.</summary>
    public List<BaccaratCard> PlayerCards { get; } = [];

    /// <summary>Banker-side cards currently shown in the preview.</summary>
    public List<BaccaratCard> BankerCards { get; } = [];

    /// <summary>Current result banner text.</summary>
    public string GameResultText { get; private set; } = string.Empty;

    /// <summary>Current result banner color.</summary>
    public Color GameResultColor { get; private set; } = Color.White;

    /// <summary>UTC time when the current betting countdown started.</summary>
    public DateTimeOffset? BetCountdownStartedAtUtc { get; private set; }

    /// <summary>Total seconds configured for the active betting countdown.</summary>
    public int BetCountdownSeconds { get; private set; }

    /// <summary>Remaining seconds in the active betting countdown.</summary>
    public int BetCountdownRemainingSeconds
    {
        get
        {
            if (BetCountdownStartedAtUtc == null || BetCountdownSeconds <= 0)
            {
                return 0;
            }

            double elapsedSeconds = (DateTimeOffset.UtcNow - BetCountdownStartedAtUtc.Value).TotalSeconds;
            return Math.Clamp((int)Math.Ceiling(BetCountdownSeconds - elapsedSeconds), 0, BetCountdownSeconds);
        }
    }

    /// <summary>Gets whether the endpoint is still in a visible betting countdown state.</summary>
    public bool IsBetCountdownActive =>
        BetCountdownStartedAtUtc != null &&
        BetCountdownSeconds > 0 &&
        BetCountdownRemainingSeconds > 0 &&
        PlayerCards.Count == 0 &&
        BankerCards.Count == 0 &&
        string.IsNullOrWhiteSpace(GameResultText);

    /// <summary>Raised when endpoint state changes and the UI should refresh.</summary>
    public event Action<ShoeEndpoint>? StateChanged;

    /// <summary>Raised when endpoint diagnostics should be logged.</summary>
    public event Action<ShoeEndpoint, string, string>? LogReceived;

    /// <summary>Raised when a card or card-status event is parsed.</summary>
    public event Action<ShoeEndpoint, SerialListener.CardInfo>? CardDrawn;

    /// <summary>Raised when a game result is parsed.</summary>
    public event Action<ShoeEndpoint, SerialListener.GameResult>? GameResultReceived;

    /// <summary>Raised for typed S/P protocol observations.</summary>
    public event Action<ShoeEndpoint, SerialListener.ProtocolSignal>? ProtocolSignalObserved;

    /// <summary>Raised when the physical transport lifecycle changes.</summary>
    public event Action<ShoeEndpoint, SerialListener.TransportState>? TransportStateChanged;

    /// <summary>Raised when the shoe reports an error.</summary>
    public event Action<ShoeEndpoint, SerialListener.ErrorInfo>? ErrorOccurred;

    /// <summary>Raised when the key lock state changes.</summary>
    public event Action<ShoeEndpoint, bool>? LockStatusChanged;

    /// <summary>Raised when an error clear event is parsed.</summary>
    public event Action<ShoeEndpoint, int, string>? ErrorCleared;

    /// <summary>Raised when the cutting card is drawn.</summary>
    public event Action<ShoeEndpoint, SerialListener.CutCardInfo>? CuttingCardDrawn;

    /// <summary>
    /// Controls whether card/result telegrams may allocate a round implicitly.
    /// The headless Worker disables this so only its serialized boundary policy advances rounds.
    /// </summary>
    public bool AutoAdvanceRoundFromEvents { get; set; } = false;

    /// <summary>
    /// Connects the endpoint to its configured serial port or mock listener.
    /// </summary>
    public void Connect()
    {
        if (!Enabled)
        {
            throw new InvalidOperationException("此端點未啟用。");
        }

        if (!MockMode && IsMoxaTcpMode)
        {
            if (string.IsNullOrWhiteSpace(MoxaHost))
            {
                throw new InvalidOperationException("請先設定 MOXA IP。");
            }

            if (MoxaPort <= 0 || MoxaPort > 65535)
            {
                throw new InvalidOperationException("請先設定 MOXA TCP Port。");
            }
        }
        else if (!MockMode && string.IsNullOrWhiteSpace(ComPort))
        {
            throw new InvalidOperationException("請先設定 COM Port。");
        }

        Listener.IsMockMode = MockMode;
        if (MockMode)
        {
            Listener.Open("MOCK");
        }
        else if (IsMoxaTcpMode)
        {
            Listener.OpenTcp(MoxaHost, MoxaPort);
        }
        else
        {
            Listener.Open(ComPort);
        }

        IsConnected = Listener.IsOpen;
        StatusText = IsConnected ? "已連線" : "未連線";
        RaiseStateChanged();
    }

    /// <summary>
    /// Disconnects the endpoint and closes the listener.
    /// </summary>
    public void Disconnect()
    {
        Listener.Close();
        IsConnected = false;
        StatusText = "未連線";
        RaiseStateChanged();
    }

    /// <summary>
    /// Sends a lock or unlock command to the shoe.
    /// </summary>
    /// <param name="locked">True to lock the shoe; false to unlock it.</param>
    /// <param name="cancellationToken">Cancellation token for the command wait.</param>
    /// <returns>The serial command result.</returns>
    public async Task<SerialListener.SerialCommandResult> SetLockAsync(bool locked, CancellationToken cancellationToken = default)
    {
        SerialListener.SerialCommandResult result = await SendCommandAsync(
            AngelEyeProtocol.BuildLockCommand(locked), locked ? "Lock ON" : "Lock OFF", cancellationToken);

        // 規格說 'L' 事件只是物理鑰匙的廣播，PC 下 LK 指令後不保證有 'L' 事件。
        // 以 ACK 為準樂觀更新 IsLocked；若後續牌盒也發 'L' 事件，HandleLockStatusChanged 會再次覆蓋。
        if (result.Succeeded)
        {
            IsLocked = locked;
            LockStatusChanged?.Invoke(this, locked);
            RaiseStateChanged();
        }

        return result;
    }

    /// <summary>
    /// Sends the error cancellation command to the shoe.
    /// </summary>
    /// <param name="data">Two-digit error clear data from 00 through 99.</param>
    /// <param name="cancellationToken">Cancellation token for the command wait.</param>
    /// <returns>The serial command result.</returns>
    public async Task<SerialListener.SerialCommandResult> CancelErrorAsync(string data, CancellationToken cancellationToken = default)
    {
        string normalized = AngelEyeProtocol.NormalizeTwoDigitData(data);
        SerialListener.SerialCommandResult result = await SendCommandAsync(AngelEyeProtocol.BuildCancelErrorCommand(normalized), $"CancelError {normalized}", cancellationToken);
        if (result.Succeeded && MockMode && InErrorMode)
        {
            int errorCode = ErrorCode ?? 0;
            byte payload = (byte)(0x80 | (errorCode & 0x7F));
            InjectSimulatedEvent("e", [(byte)'e', payload]);
        }

        return result;
    }

    /// <summary>
    /// Sends the game process confirmation command to the shoe.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the command wait.</param>
    /// <returns>The serial command result.</returns>
    public Task<SerialListener.SerialCommandResult> ConfirmGameProcessAsync(CancellationToken cancellationToken = default)
    {
        return SendCommandAsync(AngelEyeProtocol.BuildGameProcessConfirmationCommand(), "GameProcessConfirmation GP 00", cancellationToken);
    }

    /// <summary>
    /// Updates the local BMS shoe and round counters.
    /// </summary>
    /// <param name="shoe">BMS shoe number.</param>
    /// <param name="round">BMS round number.</param>
    public void SetGameNumber(long shoe, long round)
    {
        if (shoe <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(shoe), "靴號必須大於 0。");
        }

        if (round < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(round), "局號不可小於 0。");
        }

        CurrentShoe = shoe;
        CurrentRound = round;
        CurrentRoundId = round > 0 ? round : null;
        _roundInProgress = false;
        _roundSettled = true;
        ShoeEnding = false;
        RoundPhase = BridgeRoundPhases.ConnectedWaitingBoundary;
        BoundaryStrategy = BridgeBoundaryStrategies.DisabledUntilValidated;
        BoundaryObservedAtUtc = null;
        StartGameEventUid = null;
        StartGameDeliveryState = string.Empty;
        LastFinalResultAtUtc = null;
        AlignmentReason = string.Empty;
        AwaitingFirstAuthoritativeResultAfterShoeChange = false;
        ClearBetCountdown(notify: false);
        LastEventAt = DateTime.Now;
        LastEventText = $"Game number {CurrentShoe}/{CurrentRound}";
        RaiseStateChanged();
    }

    /// <summary>
    /// Advances to the next BMS shoe and resets round and preview state.
    /// </summary>
    public void StartNewShoe(DateTime? now = null)
    {
        _ = ConfirmNewShoe(
            $"local-{Guid.NewGuid():N}",
            "Local engineering new-shoe confirmation.",
            now);
    }

    /// <summary>
    /// Applies an audited, idempotent physical new-shoe confirmation.
    /// Repeating the same action identifier does not advance the shoe again.
    /// Confirmation resets to round zero and never creates StartGame.
    /// </summary>
    public bool ConfirmNewShoe(
        string actionId,
        string reason,
        DateTime? now = null)
    {
        string normalizedActionId = actionId?.Trim() ?? string.Empty;
        string normalizedReason = reason?.Trim() ?? string.Empty;
        if (normalizedActionId.Length == 0)
        {
            throw new ArgumentException(
                "New-shoe action ID is required.",
                nameof(actionId));
        }

        if (normalizedReason.Length == 0)
        {
            throw new ArgumentException(
                "New-shoe reason is required.",
                nameof(reason));
        }

        if (string.Equals(
                LastNewShoeActionId,
                normalizedActionId,
                StringComparison.Ordinal))
        {
            return false;
        }

        CurrentShoe = BridgeGameNumbering.NextShoe(CurrentShoe, now);
        CurrentRound = 0;
        CurrentRoundId = null;
        _roundInProgress = false;
        _roundSettled = true;
        ShoeEnding = false;
        RoundPhase = BridgeRoundPhases.ConnectedWaitingBoundary;
        BoundaryStrategy = BridgeBoundaryStrategies.DisabledUntilValidated;
        BoundaryObservedAtUtc = null;
        StartGameEventUid = null;
        StartGameDeliveryState = string.Empty;
        LastFinalResultAtUtc = null;
        AlignmentReason = string.Empty;
        PlayerCards.Clear();
        BankerCards.Clear();
        GameResultText = string.Empty;
        GameResultColor = Color.White;
        ClearBetCountdown(notify: false);
        LastEventAt = DateTime.Now;
        LastEventText = $"New shoe {CurrentShoe}";
        LastNewShoeActionId = normalizedActionId;
        LastNewShoeReason = normalizedReason;
        LastNewShoeConfirmedAtUtc = DateTimeOffset.UtcNow;
        AwaitingFirstAuthoritativeResultAfterShoeChange = false;
        RaiseStateChanged();
        return true;
    }

    /// <summary>
    /// Completes an automatic physical shoe change only when the same endpoint
    /// already holds durable cut-card evidence.
    /// </summary>
    public bool TryConfirmNewShoeFromStartSignal(
        SerialListener.ProtocolSignal signal,
        DateTime? now = null)
    {
        if (signal.Kind != SerialListener.ProtocolSignalKind.StartOfCommunication ||
            !ShoeEnding)
        {
            return false;
        }

        string sequence = string.IsNullOrWhiteSpace(signal.Sequence)
            ? "unknown"
            : signal.Sequence.Trim();
        string actionId =
            $"device-c-s:{SourceDataCode}:{DeviceId}:{CurrentShoe}:{sequence}";
        bool confirmed = ConfirmNewShoe(
            actionId,
            $"Card shoe StartOfCommunication sequence {sequence} followed persisted CuttingCardDrawn.",
            now);
        if (confirmed)
        {
            AwaitingFirstAuthoritativeResultAfterShoeChange = true;
            RaiseStateChanged();
        }

        return confirmed;
    }

    /// <summary>
    /// Injects a simulated ANGEL active report, including triple transmission behavior.
    /// </summary>
    /// <param name="name">Human-readable simulator event name.</param>
    /// <param name="dataPayload">Payload bytes from event code through data bytes.</param>
    public void InjectSimulatedEvent(string name, byte[] dataPayload)
    {
        byte[] prefix = new byte[dataPayload.Length + 3];
        prefix[0] = 0x05;
        prefix[1] = _simSeqCt;
        Array.Copy(dataPayload, 0, prefix, 2, dataPayload.Length);
        prefix[prefix.Length - 1] = 0x03;

        string bcc = SerialListener.CalculateBcc(prefix);
        byte[] bccBytes = Encoding.ASCII.GetBytes(bcc);

        byte[] fullPacket = new byte[prefix.Length + bccBytes.Length];
        Array.Copy(prefix, 0, fullPacket, 0, prefix.Length);
        Array.Copy(bccBytes, 0, fullPacket, prefix.Length, bccBytes.Length);

        if (name != "S")
        {
            _simSeqCt++;
            if (_simSeqCt > 0x39)
            {
                _simSeqCt = 0x31;
            }
        }

        LogReceived?.Invoke(this, "SIM", $"Simulator triple transmission for '{name}' event...");
        Listener.InjectBytes(fullPacket);

        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 2; i++)
            {
                await Task.Delay(50);
                Listener.InjectBytes(fullPacket);
            }
        });
    }

    /// <summary>
    /// Clears visible cards, result banner, and betting countdown.
    /// The safety-critical shoe-ending state is intentionally preserved.
    /// </summary>
    public void ClearPreview()
    {
        PlayerCards.Clear();
        BankerCards.Clear();
        GameResultText = string.Empty;
        GameResultColor = Color.White;
        ClearBetCountdown(notify: false);
        RaiseStateChanged();
    }

    /// <summary>
    /// Restores safety-critical runtime state persisted by the headless Worker.
    /// This does not advance the shoe or round and never creates StartGame.
    /// </summary>
    public void RestoreRuntimeState(ShoeRuntimeState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ShoeEnding = state.ShoeEnding;
        RoundPhase = BridgeRoundPhases.Normalize(state.RoundPhase);
        BoundaryStrategy = BridgeBoundaryStrategies.Normalize(state.BoundaryStrategy);
        BoundaryObservedAtUtc = state.BoundaryObservedAtUtc;
        StartGameEventUid = state.StartGameEventUid;
        StartGameDeliveryState = state.StartGameDeliveryState?.Trim() ?? string.Empty;
        LastFinalResultAtUtc = state.LastFinalResultAtUtc;
        AlignmentReason = state.AlignmentReason?.Trim() ?? string.Empty;
        LastNewShoeActionId = state.LastNewShoeActionId?.Trim() ?? string.Empty;
        LastNewShoeReason = state.LastNewShoeReason?.Trim() ?? string.Empty;
        LastNewShoeConfirmedAtUtc = state.LastNewShoeConfirmedAtUtc;
        AwaitingFirstAuthoritativeResultAfterShoeChange =
            state.AwaitingFirstAuthoritativeResultAfterShoeChange;

        PlayerCards.Clear();
        PlayerCards.AddRange(state.PlayerCards.Select(CloneCard));
        BankerCards.Clear();
        BankerCards.AddRange(state.BankerCards.Select(CloneCard));

        _roundInProgress = RoundPhase is BridgeRoundPhases.Countdown or BridgeRoundPhases.Dealing;
        _roundSettled = !_roundInProgress;
        if (ShoeEnding || RoundPhase is BridgeRoundPhases.AlignmentRequired or BridgeRoundPhases.ShoeChangePending)
        {
            ClearBetCountdown(notify: false);
        }
    }

    /// <summary>
    /// Compatibility overload for callers that only persist the shoe-ending flag.
    /// Such state cannot prove round continuity and therefore remains fail closed.
    /// </summary>
    public void RestoreRuntimeState(bool shoeEnding)
    {
        RestoreRuntimeState(CaptureRuntimeState() with
        {
            ShoeEnding = shoeEnding,
            RoundPhase = shoeEnding
                ? BridgeRoundPhases.ShoeChangePending
                : BridgeRoundPhases.AlignmentRequired,
            AlignmentReason = "Legacy runtime state does not prove round continuity."
        });
    }

    /// <summary>Moves the current physical round into a fail-closed alignment state.</summary>
    public void MarkAlignmentRequired(string reason)
    {
        RoundPhase = BridgeRoundPhases.AlignmentRequired;
        AlignmentReason = string.IsNullOrWhiteSpace(reason)
            ? "Round continuity cannot be proven."
            : reason.Trim();
        ClearBetCountdown(notify: false);
        _roundInProgress = false;
        _roundSettled = true;
    }

    /// <summary>Records a trusted or explicitly configured boundary for the current round.</summary>
    public void ArmRoundBoundary(string strategy, DateTimeOffset observedAtUtc, Guid eventUid)
    {
        if (eventUid == Guid.Empty)
        {
            throw new ArgumentException("StartGame eventUid must not be empty.", nameof(eventUid));
        }

        RoundPhase = BridgeRoundPhases.Countdown;
        BoundaryStrategy = BridgeBoundaryStrategies.Normalize(strategy);
        BoundaryObservedAtUtc = observedAtUtc;
        StartGameEventUid = eventUid;
        StartGameDeliveryState = "Prepared";
        AlignmentReason = string.Empty;
        LastFinalResultAtUtc = null;
        _roundInProgress = true;
        _roundSettled = false;
    }

    /// <summary>
    /// Allocates and arms the next round from the QA-validated, non-retransmission
    /// Player #1 telegram. The card has already been decoded into the endpoint,
    /// so this transition preserves that card while advancing only round identity.
    /// </summary>
    public bool TryArmRoundFromPlayerOne(DateTimeOffset observedAtUtc, Guid eventUid)
    {
        if (eventUid == Guid.Empty)
        {
            throw new ArgumentException("StartGame eventUid must not be empty.", nameof(eventUid));
        }

        if (ShoeEnding ||
            InErrorMode ||
            StartGameEventUid.HasValue &&
            RoundPhase is BridgeRoundPhases.Countdown or BridgeRoundPhases.Dealing)
        {
            return false;
        }

        if (CurrentShoe <= 0)
        {
            CurrentShoe = BridgeGameNumbering.TodayFirstShoe();
        }

        CurrentRound++;
        CurrentRoundId = CurrentRound;
        GameResultText = string.Empty;
        GameResultColor = Color.White;
        ClearBetCountdown(notify: false);
        ArmRoundBoundary(
            BridgeBoundaryStrategies.VerifiedDeviceSignal,
            observedAtUtc,
            eventUid);
        LastEventAt = DateTime.Now;
        LastEventText = $"Player #1 boundary {CurrentShoe}/{CurrentRound}";
        RaiseStateChanged();
        return true;
    }

    /// <summary>Records the durable local delivery state of the current StartGame.</summary>
    public void MarkStartGameStored(string deliveryState)
    {
        StartGameDeliveryState = deliveryState?.Trim() ?? string.Empty;
    }

    /// <summary>Marks the armed current round as dealing.</summary>
    public void MarkDealing()
    {
        if (StartGameEventUid.HasValue &&
            RoundPhase is not BridgeRoundPhases.AlignmentRequired and not BridgeRoundPhases.ShoeChangePending)
        {
            RoundPhase = BridgeRoundPhases.Dealing;
            _roundInProgress = true;
            _roundSettled = false;
        }
    }

    /// <summary>Records a physical terminal result without allocating the next round.</summary>
    public void MarkFinalResultStored(DateTimeOffset observedAtUtc, string result)
    {
        string previousPhase = RoundPhase;
        LastFinalResultAtUtc = observedAtUtc;
        ClearBetCountdown(notify: false);
        _roundInProgress = false;
        _roundSettled = true;
        if (previousPhase is BridgeRoundPhases.AlignmentRequired or BridgeRoundPhases.ShoeChangePending)
        {
            // A terminal telegram is useful local evidence, but it cannot repair missing,
            // corrupt, disconnected, or shoe-change state by itself.
            return;
        }

        if (ShoeEnding)
        {
            RoundPhase = BridgeRoundPhases.ShoeChangePending;
            return;
        }

        if (string.Equals(result, "ForceQuit", StringComparison.OrdinalIgnoreCase))
        {
            RoundPhase = BridgeRoundPhases.Cancelled;
        }
        else if (!string.Equals(result, "PlayerWin", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(result, "BankerWin", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(result, "Tie", StringComparison.OrdinalIgnoreCase))
        {
            MarkAlignmentRequired($"Unknown terminal result '{result}'.");
        }
        else
        {
            RoundPhase = BridgeRoundPhases.WaitingForRoundBoundary;
        }
    }

    /// <summary>
    /// Persists the cut-card hold while allowing an already armed final round to finish.
    /// </summary>
    public void MarkShoeChangePending()
    {
        ShoeEnding = true;
        ClearBetCountdown(notify: false);
        bool canFinishArmedRound =
            StartGameEventUid.HasValue &&
            RoundPhase is BridgeRoundPhases.Countdown or BridgeRoundPhases.Dealing;
        if (canFinishArmedRound)
        {
            _roundInProgress = true;
            _roundSettled = false;
            return;
        }

        RoundPhase = BridgeRoundPhases.ShoeChangePending;
        _roundInProgress = false;
        _roundSettled = true;
    }

    /// <summary>Captures a deep copy suitable for durable persistence.</summary>
    public ShoeRuntimeState CaptureRuntimeState() => new()
    {
        ShoeEnding = ShoeEnding,
        RoundPhase = RoundPhase,
        BoundaryStrategy = BoundaryStrategy,
        BoundaryObservedAtUtc = BoundaryObservedAtUtc,
        StartGameEventUid = StartGameEventUid,
        StartGameDeliveryState = StartGameDeliveryState,
        LastFinalResultAtUtc = LastFinalResultAtUtc,
        AlignmentReason = AlignmentReason,
        LastNewShoeActionId = LastNewShoeActionId,
        LastNewShoeReason = LastNewShoeReason,
        LastNewShoeConfirmedAtUtc = LastNewShoeConfirmedAtUtc,
        AwaitingFirstAuthoritativeResultAfterShoeChange =
            AwaitingFirstAuthoritativeResultAfterShoeChange,
        PlayerCards = PlayerCards.Select(CloneCard).ToList(),
        BankerCards = BankerCards.Select(CloneCard).ToList()
    };

    private static BaccaratCard CloneCard(BaccaratCard card) => new()
    {
        Index = card.Index,
        Suit = card.Suit,
        Value = card.Value,
        IsPlayer = card.IsPlayer
    };

    /// <summary>
    /// Resets simulator-only state without changing the configured shoe number.
    /// </summary>
    public void ResetMockTestState()
    {
        PlayerCards.Clear();
        BankerCards.Clear();
        GameResultText = string.Empty;
        GameResultColor = Color.White;
        ShoeEnding = false;
        AwaitingFirstAuthoritativeResultAfterShoeChange = false;
        InErrorMode = false;
        ErrorCode = null;
        ErrorMessage = string.Empty;
        ClearBetCountdown(notify: false);
        _roundInProgress = false;
        _roundSettled = true;
        LastEventAt = DateTime.Now;
        LastEventText = "Mock test reset";
        StatusText = IsConnected ? "已連線" : "未連線";
        RaiseStateChanged();
    }

    /// <summary>
    /// Starts the next local round and clears preview cards before the betting countdown.
    /// </summary>
    public void BeginNextRoundCountdown(bool alignShoeDateForNewRound = false)
    {
        if (CurrentShoe <= 0)
        {
            CurrentShoe = BridgeGameNumbering.TodayFirstShoe();
        }

        if (_roundInProgress && !_roundSettled && CurrentRound > 0)
        {
            return;
        }

        // A game number is immutable after its first card.  The date is checked only
        // when a new round is about to begin, never while receiving later cards/results.
        if (alignShoeDateForNewRound && !BridgeGameNumbering.IsShoeForDate(CurrentShoe, DateTime.Now))
        {
            StartNewShoe();
            LogReceived?.Invoke(this, "SYS", $"Cross-day new round: switched BMS shoe to {CurrentShoe}.");
        }

        CurrentRound++;
        CurrentRoundId = CurrentRound;
        _roundInProgress = true;
        _roundSettled = false;
        RoundPhase = BridgeRoundPhases.AwaitingSynchronization;
        BoundaryStrategy = BridgeBoundaryStrategies.DisabledUntilValidated;
        BoundaryObservedAtUtc = null;
        StartGameEventUid = null;
        StartGameDeliveryState = string.Empty;
        AlignmentReason = string.Empty;
        PlayerCards.Clear();
        BankerCards.Clear();
        GameResultText = string.Empty;
        GameResultColor = Color.White;
        ClearBetCountdown(notify: false);
        LastEventAt = DateTime.Now;
        LastEventText = $"StartGame {CurrentShoe}/{CurrentRound}";
        RaiseStateChanged();
    }

    /// <summary>
    /// Starts the visible betting countdown for the current round.
    /// </summary>
    /// <param name="startedAtUtc">Countdown start time in UTC.</param>
    /// <param name="seconds">Countdown length in seconds.</param>
    public void StartBetCountdown(DateTimeOffset startedAtUtc, int seconds)
    {
        if (seconds <= 0)
        {
            ClearBetCountdown(notify: true);
            return;
        }

        BetCountdownStartedAtUtc = startedAtUtc;
        BetCountdownSeconds = Math.Clamp(seconds, 5, 120);
        RaiseStateChanged();
    }

    /// <summary>
    /// Clears the visible betting countdown and notifies listeners.
    /// </summary>
    public void ClearBetCountdown()
    {
        ClearBetCountdown(notify: true);
    }

    private async Task<SerialListener.SerialCommandResult> SendCommandAsync(byte[] command, string label, CancellationToken cancellationToken)
    {
        LastEventAt = DateTime.Now;
        LastEventText = $"TX {label}";
        RaiseStateChanged();

        SerialListener.SerialCommandResult result = await Listener.SendCommandAsync(command, TimeSpan.FromSeconds(3), cancellationToken);
        StatusText = result.Succeeded ? $"{label}: ACK" : $"{label}: {result.Message}";
        RaiseStateChanged();
        return result;
    }

    private void HandleStatusChanged(string message)
    {
        StatusText = message;
        LastEventAt = DateTime.Now;
        LastEventText = message;
        if (message.Contains("已斷線", StringComparison.Ordinal) ||
            message.Contains("讀取錯誤", StringComparison.Ordinal) ||
            message.Contains("連線已關閉", StringComparison.Ordinal))
        {
            IsConnected = false;
        }

        LogReceived?.Invoke(this, "SYS", message);
        RaiseStateChanged();
    }

    private void HandleTransportStateChanged(SerialListener.TransportState state)
    {
        IsConnected = state.Kind == SerialListener.TransportStateKind.Connected;
        TransportStateChanged?.Invoke(this, state);
        RaiseStateChanged();
    }

    private void HandleCardDrawn(SerialListener.CardInfo card)
    {
        LastCardAtUtc = DateTimeOffset.UtcNow;

        // 'R' (Retransmission)：Overdraw 清錯後指定用於「下一局」的牌（規格 §11.vi）。
        // 不計入當前局牌面，也不觸發新局推進；僅記錄狀態並送出 CardDrawn 事件供 Outbox 記錄。
        if (card.EventCode == 'R')
        {
            LastEventAt = DateTime.Now;
            LastEventText = $"Retransmit {card.Target} #{card.Index} {card.Suit} {card.Value} (next round)";
            CardDrawn?.Invoke(this, card);
            RaiseStateChanged();
            return;
        }

        if (!IsBaccaratGameCard(card))
        {
            LastEventAt = DateTime.Now;
            LastEventText = BuildNonGameCardStatus(card);
            CardDrawn?.Invoke(this, card);
            RaiseStateChanged();
            return;
        }

        bool canFinishArmedShoeEndingRound =
            StartGameEventUid.HasValue &&
            RoundPhase is BridgeRoundPhases.Countdown or BridgeRoundPhases.Dealing;
        if (ShoeEnding && !canFinishArmedShoeEndingRound)
        {
            LastEventAt = DateTime.Now;
            LastEventText = $"Ignored card after cutting card {card.Target} #{card.Index}";
            LogReceived?.Invoke(this, "WARN", $"切牌已抽出，未按「新靴」前收到發牌 {card.Target} #{card.Index}，已忽略。");
            RaiseStateChanged();
            return;
        }

        EnsureRoundForCard(card);

        LastEventAt = DateTime.Now;
        LastEventText = $"Card {card.Target} #{card.Index} {card.Suit} {card.Value}";

        if (card.Target == "Player" && card.Index == 1)
        {
            BaccaratCard? existingPlayerOne =
                PlayerCards.FirstOrDefault(existing => existing.Index == 1);
            bool activeArmedRound =
                StartGameEventUid.HasValue &&
                RoundPhase is BridgeRoundPhases.Countdown or BridgeRoundPhases.Dealing;
            if (activeArmedRound &&
                existingPlayerOne != null &&
                string.Equals(existingPlayerOne.Suit, card.Suit, StringComparison.Ordinal) &&
                string.Equals(existingPlayerOne.Value, card.Value, StringComparison.Ordinal))
            {
                LastEventText =
                    $"Duplicate card Player #1 {card.Suit} {card.Value} (state unchanged)";
                LogReceived?.Invoke(
                    this,
                    "SYS",
                    "重啟或重連後收到相同 Player #1；保留既有權威牌面，不重建本局。");
                RaiseStateChanged();
                return;
            }

            bool armedRoundAlreadyHasCards =
                StartGameEventUid.HasValue &&
                RoundPhase is BridgeRoundPhases.Countdown or BridgeRoundPhases.Dealing &&
                (PlayerCards.Count > 0 || BankerCards.Count > 0);
            if (armedRoundAlreadyHasCards)
            {
                MarkAlignmentRequired(
                    "Conflicting Player #1 arrived after authoritative cards were already retained.");
                LastEventText =
                    $"Conflicting card Player #1 {card.Suit} {card.Value} (retained cards preserved)";
                LogReceived?.Invoke(
                    this,
                    "ERR",
                    "已保留牌面的進行局收到不同 Player #1；未清除舊牌，轉人工對齊。");
                CardDrawn?.Invoke(this, card);
                RaiseStateChanged();
                return;
            }

            PlayerCards.Clear();
            BankerCards.Clear();
            GameResultText = string.Empty;
        }

        ClearBetCountdown(notify: false);

        if (card.Target == "Player" || card.Target == "Banker")
        {
            List<BaccaratCard> cards = card.Target == "Player" ? PlayerCards : BankerCards;
            BaccaratCard? existing = cards.FirstOrDefault(candidate => candidate.Index == card.Index);
            if (existing != null)
            {
                if (string.Equals(existing.Suit, card.Suit, StringComparison.Ordinal) &&
                    string.Equals(existing.Value, card.Value, StringComparison.Ordinal))
                {
                    LastEventText =
                        $"Duplicate card {card.Target} #{card.Index} {card.Suit} {card.Value} (state unchanged)";
                    LogReceived?.Invoke(
                        this,
                        "SYS",
                        $"收到相同 {card.Target} #{card.Index} 重複牌訊；保留既有權威牌面。");
                    RaiseStateChanged();
                    return;
                }

                MarkAlignmentRequired(
                    $"Conflicting {card.Target} #{card.Index} arrived for an occupied card position.");
                LastEventText =
                    $"Conflicting card {card.Target} #{card.Index} {card.Suit} {card.Value} (retained card preserved)";
                LogReceived?.Invoke(
                    this,
                    "ERR",
                    $"{card.Target} #{card.Index} 已有不同牌值；未覆寫舊牌，轉人工對齊。");
                CardDrawn?.Invoke(this, card);
                RaiseStateChanged();
                return;
            }

            cards.Add(new BaccaratCard
            {
                Index = card.Index,
                Suit = card.Suit,
                Value = card.Value,
                IsPlayer = card.Target == "Player"
            });
            cards.Sort((a, b) => a.Index.CompareTo(b.Index));
        }

        CardDrawn?.Invoke(this, card);
        RaiseStateChanged();
    }

    private void HandleGameResult(SerialListener.GameResult result)
    {
        EnsureRoundForResult();

        LastEventAt = DateTime.Now;
        bool hasAuthoritativeNewShoeRound =
            StartGameEventUid.HasValue &&
            PlayerCards.Any(static card => card.Index == 1) &&
            PlayerCards.Any(static card => card.Index == 2) &&
            BankerCards.Any(static card => card.Index == 1) &&
            BankerCards.Any(static card => card.Index == 2);
        if (AwaitingFirstAuthoritativeResultAfterShoeChange &&
            !hasAuthoritativeNewShoeRound)
        {
            LastEventText =
                $"Quarantined result after shoe change {result.Result} / {result.Pair}";
            GameResultReceived?.Invoke(this, result);
            RaiseStateChanged();
            return;
        }

        AwaitingFirstAuthoritativeResultAfterShoeChange = false;
        LastEventText = $"Result {result.Result} / {result.Pair}";

        (GameResultText, GameResultColor) = result.Result switch
        {
            "PlayerWin" => ("閒贏 PLAYER WIN", Color.DeepSkyBlue),
            "BankerWin" => ("莊贏 BANKER WIN", Color.IndianRed),
            "Tie" => ("和局 TIE", Color.LightGreen),
            "ForceQuit" => ("強制終止 FORCE QUIT", Color.Gold),
            _ => ($"未知結果 {result.Result}", Color.White)
        };

        if (result.Pair != "None")
        {
            GameResultText += $" / {result.Pair}";
        }

        ClearBetCountdown(notify: false);
        _roundInProgress = false;
        // ForceQuit 代表本局強制終止，局號仍保留（不回退）；BMS 端收到 ForceQuit 結果後
        // 如何處理（作廢/取消/保留）需依 BMS OpenSpec 定義，此處僅標記已結算。
        _roundSettled = true;

        GameResultReceived?.Invoke(this, result);
        RaiseStateChanged();
    }

    private void HandleErrorOccurred(SerialListener.ErrorInfo error)
    {
        LastEventAt = DateTime.Now;
        LastEventText = $"Error {error.ErrorCode}";
        InErrorMode = error.InErrorMode;
        ErrorCode = error.ErrorCode;
        ErrorMessage = error.ErrorMessage;
        ErrorOccurred?.Invoke(this, error);
        RaiseStateChanged();
    }

    private void HandleLockStatusChanged(bool isLocked)
    {
        LastEventAt = DateTime.Now;
        LastEventText = isLocked ? "Locked" : "Unlocked";
        IsLocked = isLocked;
        LockStatusChanged?.Invoke(this, isLocked);
        RaiseStateChanged();
    }

    private void HandleErrorCleared(int errCode, string errMsg)
    {
        LastEventAt = DateTime.Now;
        LastEventText = $"Error cleared {errCode}";
        InErrorMode = false;
        ErrorCode = null;
        ErrorMessage = string.Empty;
        ErrorCleared?.Invoke(this, errCode, errMsg);
        RaiseStateChanged();
    }

    private void HandleCommandAcknowledged(SerialListener.SerialCommandResult result)
    {
        LastEventAt = DateTime.Now;
        LastEventText = result.Succeeded ? "ACK" : result.Message;
        RaiseStateChanged();
    }

    private void HandleCuttingCardDrawn(SerialListener.CutCardInfo cutCard)
    {
        LastEventAt = DateTime.Now;
        LastEventText = "Cutting card drawn";
        MarkShoeChangePending();
        CuttingCardDrawn?.Invoke(this, cutCard);
        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        StateChanged?.Invoke(this);
    }

    private void ClearBetCountdown(bool notify)
    {
        if (BetCountdownStartedAtUtc == null && BetCountdownSeconds == 0)
        {
            return;
        }

        BetCountdownStartedAtUtc = null;
        BetCountdownSeconds = 0;
        if (notify)
        {
            RaiseStateChanged();
        }
    }

    private void EnsureGameNumberInitialized()
    {
        if (CurrentShoe <= 0)
        {
            CurrentShoe = BridgeGameNumbering.TodayFirstShoe();
        }

        if (CurrentRound < 0)
        {
            CurrentRound = 0;
        }

        CurrentRoundId = CurrentRound > 0 ? CurrentRound : null;
    }

    private void EnsureRoundForCard(SerialListener.CardInfo card)
    {
        if (CurrentShoe <= 0)
        {
            CurrentShoe = BridgeGameNumbering.TodayFirstShoe();
        }

        if (!AutoAdvanceRoundFromEvents)
        {
            return;
        }

        bool startsRound = card.Target == "Player" && card.Index == 1;
        if (startsRound && (!_roundInProgress || _roundSettled || CurrentRound <= 0))
        {
            StartNextRound();
        }
        else if (CurrentRound <= 0)
        {
            StartNextRound();
        }
    }

    private void EnsureRoundForResult()
    {
        if (CurrentShoe <= 0)
        {
            CurrentShoe = BridgeGameNumbering.TodayFirstShoe();
        }

        if (!AutoAdvanceRoundFromEvents)
        {
            return;
        }

        if (CurrentRound <= 0)
        {
            StartNextRound();
        }
    }

    private void StartNextRound()
    {
        BeginNextRoundCountdown();
    }

    private static bool IsBaccaratGameCard(SerialListener.CardInfo card)
    {
        return card.EventCode == 'D' &&
               (card.Target == "Player" || card.Target == "Banker");
    }

    private static string BuildNonGameCardStatus(SerialListener.CardInfo card)
    {
        return card.Target switch
        {
            "LockMode" => $"Lock mode status #{card.Index}",
            "ErrorMode" => $"Error mode status #{card.Index}",
            "SettingMode" => $"Setting mode status #{card.Index}",
            "Burn" => $"Burn card #{card.Index} {card.Suit} {card.Value}",
            "FirstCard" => $"First card to count burn #{card.Index}",
            "BurnCount" => $"Burn count {card.Index}",
            _ => $"Non-game status {card.Target} #{card.Index}"
        };
    }
}

/// <summary>Durable physical-round phases shared by the Worker state store and endpoint.</summary>
public static class BridgeRoundPhases
{
    public const string AwaitingSynchronization = "AwaitingSynchronization";
    public const string ConnectedWaitingBoundary = "ConnectedWaitingBoundary";
    public const string Countdown = "Countdown";
    public const string Dealing = "Dealing";
    public const string WaitingForRoundBoundary = "WaitingForRoundBoundary";
    public const string AlignmentRequired = "AlignmentRequired";
    public const string ShoeChangePending = "ShoeChangePending";
    public const string Cancelled = "Cancelled";

    private static readonly HashSet<string> Known =
    [
        AwaitingSynchronization,
        ConnectedWaitingBoundary,
        Countdown,
        Dealing,
        WaitingForRoundBoundary,
        AlignmentRequired,
        ShoeChangePending,
        Cancelled
    ];

    public static string Normalize(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Known.Contains(value.Trim())
            ? value.Trim()
            : AlignmentRequired;
}

/// <summary>Configured source of a legally armed physical-round boundary.</summary>
public static class BridgeBoundaryStrategies
{
    public const string DisabledUntilValidated = "DisabledUntilValidated";
    public const string VerifiedDeviceSignal = "VerifiedDeviceSignal";
    public const string DerivedAfterPreviousResult = "DerivedAfterPreviousResult";

    public static string Normalize(string? value) => value?.Trim() switch
    {
        VerifiedDeviceSignal => VerifiedDeviceSignal,
        DerivedAfterPreviousResult => DerivedAfterPreviousResult,
        _ => DisabledUntilValidated
    };
}

/// <summary>Deep-copyable runtime state persisted by the headless Worker.</summary>
public sealed record ShoeRuntimeState
{
    public bool ShoeEnding { get; init; }

    public string RoundPhase { get; init; } = BridgeRoundPhases.AlignmentRequired;

    public string BoundaryStrategy { get; init; } = BridgeBoundaryStrategies.DisabledUntilValidated;

    public DateTimeOffset? BoundaryObservedAtUtc { get; init; }

    public Guid? StartGameEventUid { get; init; }

    public string StartGameDeliveryState { get; init; } = string.Empty;

    public DateTimeOffset? LastFinalResultAtUtc { get; init; }

    public string AlignmentReason { get; init; } = string.Empty;

    public string LastNewShoeActionId { get; init; } = string.Empty;

    public string LastNewShoeReason { get; init; } = string.Empty;

    public DateTimeOffset? LastNewShoeConfirmedAtUtc { get; init; }

    public bool AwaitingFirstAuthoritativeResultAfterShoeChange { get; init; }

    public List<BaccaratCard> PlayerCards { get; init; } = [];

    public List<BaccaratCard> BankerCards { get; init; } = [];
}
