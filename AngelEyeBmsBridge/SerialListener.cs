using System;
using System.IO.Ports;
using System.Net.Sockets;
using System.Text;

namespace AngelEyeBmsBridge;

/// <summary>
/// Reads, parses, and writes ANGEL EYE II-EX serial packets.
/// </summary>
public class SerialListener
{
    private SerialPort? _serialPort;
    private TcpClient? _tcpClient;
    private NetworkStream? _tcpStream;
    private CancellationTokenSource? _tcpReadCts;
    private Task? _tcpReadTask;
    private readonly List<byte> _buffer = [];
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly object _pendingCommandLock = new();
    private TaskCompletionSource<SerialCommandResult>? _pendingCommand;
    private byte? _lastProcessedSeq;

    /// <summary>Gets or sets whether the listener should run without a physical serial port.</summary>
    public bool IsMockMode { get; set; } = false;

    /// <summary>Raised when a card drawing event is parsed.</summary>
    public event Action<CardInfo>? OnCardDrawn;

    /// <summary>Raised when a game result event is parsed.</summary>
    public event Action<GameResult>? OnGameResult;

    /// <summary>Raised when a shoe error event is parsed.</summary>
    public event Action<ErrorInfo>? OnErrorOccurred;

    /// <summary>Raised when the listener status changes.</summary>
    public event Action<string>? OnStatusChanged;

    /// <summary>Raised for RX, RXRAW, TX, SIM, and SYS diagnostic log output.</summary>
    public event Action<string, string>? OnRawDataLogged; // Type (RX/RXRAW/TX/SYS), Hex/Message

    /// <summary>Raised when a pending command receives ACK, NAK, timeout, or port-closed completion.</summary>
    public event Action<SerialCommandResult>? OnCommandAcknowledged;

    /// <summary>Raised when the physical key lock status changes.</summary>
    public event Action<bool>? OnLockStatusChanged;

    /// <summary>Raised when the shoe reports an error cancellation event.</summary>
    public event Action<int, string>? OnErrorCleared;

    /// <summary>Raised when the cutting card is drawn.</summary>
    public event Action<CutCardInfo>? OnCuttingCardDrawn;

    /// <summary>
    /// Result of a command sent to the shoe.
    /// </summary>
    /// <param name="Succeeded">True when ACK was received or simulated.</param>
    /// <param name="NakCode">NAK error code when the shoe rejected the command.</param>
    /// <param name="Message">Human-readable command result.</param>
    public readonly record struct SerialCommandResult(bool Succeeded, int? NakCode, string Message);

    /// <summary>
    /// Parsed card draw or non-game card-state event.
    /// </summary>
    public struct CardInfo
    {
        /// <summary>Raw event code, such as D, d, or R.</summary>
        public char EventCode { get; set; } // 'D', 'd', 'R'

        /// <summary>Protocol sequence counter.</summary>
        public string Seq { get; set; }

        /// <summary>True when the event was an active-game D event.</summary>
        public bool IsActiveGame { get; set; }

        /// <summary>Parsed target, such as Player, Banker, Burn, or LockMode.</summary>
        public string Target { get; set; }  // Player, Banker, Burn, BurnCount, LockMode, ErrorMode, SettingMode

        /// <summary>Draw sequence index from the protocol payload.</summary>
        public int Index { get; set; }      // 0~10

        /// <summary>Parsed card suit.</summary>
        public string Suit { get; set; }    // Diamond, Club, Spade, Heart, None

        /// <summary>Parsed card rank.</summary>
        public string Value { get; set; }   // A, 2..10, J, Q, K, None

        /// <summary>Full raw packet rendered as hexadecimal text.</summary>
        public string RawBytes { get; set; }
    }

    /// <summary>
    /// Parsed baccarat result event.
    /// </summary>
    public struct GameResult
    {
        /// <summary>Protocol sequence counter.</summary>
        public string Seq { get; set; }

        /// <summary>Parsed winner value.</summary>
        public string Result { get; set; } // PlayerWin, Tie, BankerWin, ForceQuit

        /// <summary>Parsed pair value.</summary>
        public string Pair { get; set; }   // None, PlayerPair, BankerPair, BothPair

        /// <summary>Full raw packet rendered as hexadecimal text.</summary>
        public string RawBytes { get; set; }
    }

    /// <summary>
    /// Parsed shoe error event.
    /// </summary>
    public struct ErrorInfo
    {
        /// <summary>Protocol sequence counter.</summary>
        public string Seq { get; set; }

        /// <summary>True when the shoe is currently in error mode.</summary>
        public bool InErrorMode { get; set; }

        /// <summary>System error code reported by the shoe.</summary>
        public int ErrorCode { get; set; }

        /// <summary>Human-readable error description.</summary>
        public string ErrorMessage { get; set; }

        /// <summary>Full raw packet rendered as hexadecimal text.</summary>
        public string RawBytes { get; set; }
    }

    /// <summary>
    /// Parsed cutting-card event.
    /// </summary>
    public struct CutCardInfo
    {
        /// <summary>Protocol sequence counter.</summary>
        public string Seq { get; set; }

        /// <summary>Full raw packet rendered as hexadecimal text.</summary>
        public string RawBytes { get; set; }
    }

    /// <summary>
    /// Opens the configured serial port, or enters mock mode when enabled.
    /// </summary>
    /// <param name="portName">Serial port name, or MOCK when mock mode is enabled.</param>
    public void Open(string portName)
    {
        if (IsMockMode)
        {
            OnStatusChanged?.Invoke("模擬模式已開啟，無實體串口連線。");
            OnRawDataLogged?.Invoke("SYS", "Simulator initialized");
            return;
        }

        Close();

        _serialPort = new SerialPort(portName, 4800, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 500,
            WriteTimeout = 500
        };
        _serialPort.DataReceived += SerialPort_DataReceived;
        _serialPort.Open();

        OnStatusChanged?.Invoke($"串口 {portName} 已成功開啟。");
        OnRawDataLogged?.Invoke("SYS", $"Connected to {portName}");
    }

    /// <summary>
    /// Opens a direct TCP connection to a MOXA/NPort TCP server endpoint.
    /// </summary>
    /// <param name="host">MOXA IP address or host name.</param>
    /// <param name="port">MOXA TCP server port.</param>
    public void OpenTcp(string host, int port)
    {
        if (IsMockMode)
        {
            OnStatusChanged?.Invoke("模擬模式已開啟，無實體網路連線。");
            OnRawDataLogged?.Invoke("SYS", "Simulator initialized");
            return;
        }

        Close();

        _tcpClient = new TcpClient
        {
            NoDelay = true,
            ReceiveTimeout = 500,
            SendTimeout = 500
        };
        _tcpClient.Connect(host, port);
        _tcpStream = _tcpClient.GetStream();
        _tcpReadCts = new CancellationTokenSource();
        _tcpReadTask = Task.Run(() => TcpReadLoopAsync(host, port, _tcpReadCts.Token));

        OnStatusChanged?.Invoke($"MOXA TCP {host}:{port} 已成功連線。");
        OnRawDataLogged?.Invoke("SYS", $"Connected to MOXA TCP {host}:{port}");
    }

    /// <summary>
    /// Closes the serial/TCP connection and clears pending parser or command state.
    /// </summary>
    public void Close()
    {
        _tcpReadCts?.Cancel();
        _tcpStream?.Dispose();
        _tcpStream = null;
        _tcpClient?.Close();
        _tcpClient?.Dispose();
        _tcpClient = null;
        _tcpReadCts?.Dispose();
        _tcpReadCts = null;
        _tcpReadTask = null;

        if (_serialPort != null)
        {
            if (_serialPort.IsOpen)
            {
                _serialPort.Close();
            }
            _serialPort.Dispose();
            _serialPort = null;
        }
        CompletePendingCommand(new SerialCommandResult(false, null, "Serial port closed"));
        _buffer.Clear();
        _lastProcessedSeq = null;
        IsMockMode = false;
        OnStatusChanged?.Invoke("連線已關閉。");
    }

    /// <summary>Gets whether the listener is ready to receive data.</summary>
    public bool IsOpen => IsMockMode ||
        (_serialPort != null && _serialPort.IsOpen) ||
        (_tcpClient != null && _tcpClient.Connected && _tcpStream != null);

    private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (_serialPort == null || !_serialPort.IsOpen) return;

        try
        {
            int bytesToRead = _serialPort.BytesToRead;
            byte[] bytes = new byte[bytesToRead];
            _serialPort.Read(bytes, 0, bytesToRead);

            LogRawBytes(bytes);
            InjectBytes(bytes);
        }
        catch (Exception ex)
        {
            OnRawDataLogged?.Invoke("SYS", $"Error reading serial data: {ex.Message}");
        }
    }

    private async Task TcpReadLoopAsync(string host, int port, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[512];
        try
        {
            while (!cancellationToken.IsCancellationRequested && _tcpStream != null)
            {
                int bytesRead = await _tcpStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (bytesRead <= 0)
                {
                    OnRawDataLogged?.Invoke("SYS", $"MOXA TCP {host}:{port} connection closed by remote.");
                    OnStatusChanged?.Invoke($"MOXA TCP {host}:{port} 已斷線。");
                    break;
                }

                byte[] bytes = new byte[bytesRead];
                Array.Copy(buffer, bytes, bytesRead);
                LogRawBytes(bytes);
                InjectBytes(bytes);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when Close cancels the background read loop.
        }
        catch (ObjectDisposedException)
        {
            // Expected when Close disposes the network stream.
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                OnRawDataLogged?.Invoke("SYS", $"Error reading MOXA TCP data: {ex.Message}");
                OnStatusChanged?.Invoke($"MOXA TCP 讀取錯誤: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Injects bytes into the parser from either the real serial port or mock simulator.
    /// </summary>
    /// <param name="bytes">Raw bytes to parse.</param>
    public void InjectBytes(byte[] bytes)
    {
        lock (_buffer)
        {
            _buffer.AddRange(bytes);
            ProcessBuffer();
        }
    }

    private void LogRawBytes(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return;
        }

        string hexLog = BitConverter.ToString(bytes).Replace("-", " ");
        OnRawDataLogged?.Invoke("RXRAW", hexLog);
    }

    private void ProcessBuffer()
    {
        while (_buffer.Count > 0)
        {
            // Find start code: STX (02H), ENQ (05H), ACK (06H), NAK (15H)
            int startIndex = -1;
            for (int i = 0; i < _buffer.Count; i++)
            {
                byte b = _buffer[i];
                if (b == 0x02 || b == 0x05 || b == 0x06 || b == 0x15)
                {
                    startIndex = i;
                    break;
                }
            }

            if (startIndex == -1)
            {
                // No start code found, clear buffer
                _buffer.Clear();
                return;
            }

            if (startIndex > 0)
            {
                // Remove garbage before start code
                _buffer.RemoveRange(0, startIndex);
            }

            // At this point, buffer[0] is the start code
            byte startCode = _buffer[0];

            if (startCode == 0x06) // ACK
            {
                OnRawDataLogged?.Invoke("RX", "06 (ACK)");
                CompletePendingCommand(new SerialCommandResult(true, null, "ACK"));
                _buffer.RemoveAt(0);
                continue;
            }

            if (startCode == 0x15) // NAK
            {
                // NAK has 1 byte error code following it
                if (_buffer.Count < 2)
                {
                    return; // Wait for more bytes
                }
                byte errorCode = _buffer[1];
                string errorName = GetNakErrorDescription(errorCode);
                OnRawDataLogged?.Invoke("RX", $"15 {errorCode:X2} (NAK: {errorName})");
                CompletePendingCommand(new SerialCommandResult(false, errorCode, $"NAK: {errorName}"));
                _buffer.RemoveRange(0, 2);
                continue;
            }

            // For STX (02h) and ENQ (05h), we need to search for ETX (03h)
            int etxIndex = _buffer.IndexOf(0x03);
            if (etxIndex == -1)
            {
                // ETX not found yet, wait for more data. 
                // Set a safety limit: if buffer exceeds 128 bytes, clear it.
                if (_buffer.Count > 128)
                {
                    _buffer.Clear();
                    OnRawDataLogged?.Invoke("SYS", "Warning: Buffer overflow without ETX. Buffer cleared.");
                }
                return;
            }

            // ETX found at etxIndex. We need 2 more bytes for BCC.
            if (_buffer.Count < etxIndex + 3)
            {
                return; // Wait for BCC bytes
            }

            // Extract the packet bytes from startCode to ETX
            byte[] packetBytes = new byte[etxIndex + 1];
            _buffer.CopyTo(0, packetBytes, 0, etxIndex + 1);

            // Extract BCC ASCII characters
            byte bccChar1 = _buffer[etxIndex + 1];
            byte bccChar2 = _buffer[etxIndex + 2];
            string receivedBcc = Encoding.ASCII.GetString([bccChar1, bccChar2]);

            // Total packet size including BCC is etxIndex + 3
            int packetSize = etxIndex + 3;

            // Extract the whole packet in hex for logging
            byte[] fullPacket = new byte[packetSize];
            _buffer.CopyTo(0, fullPacket, 0, packetSize);
            string hexLog = BitConverter.ToString(fullPacket).Replace("-", " ");
            OnRawDataLogged?.Invoke("RX", hexLog);

            // Validate BCC
            string calculatedBcc = CalculateBcc(packetBytes);
            if (calculatedBcc != receivedBcc)
            {
                OnRawDataLogged?.Invoke("SYS", $"BCC Check failed. Expected: {calculatedBcc}, Received: {receivedBcc}");
                _buffer.RemoveRange(0, packetSize);
                continue;
            }

            // Parse Packet
            try
            {
                if (startCode == 0x05) // Active Report (ENQ)
                {
                    ParseActiveReport(packetBytes, hexLog);
                }
                else if (startCode == 0x02) // Response to Read or general STX packet
                {
                    ParseStxResponse(packetBytes, hexLog);
                }
            }
            catch (Exception ex)
            {
                OnRawDataLogged?.Invoke("SYS", $"Error parsing packet: {ex.Message}");
            }

            // Remove the processed packet from buffer
            _buffer.RemoveRange(0, packetSize);
        }
    }

    private void ParseActiveReport(byte[] packetBytes, string rawBytesHex)
    {
        // Format: ENQ + Seq. ct(1B) + Data(var) + ETX
        if (packetBytes.Length < 4) return;

        byte seqCt = packetBytes[1];
        string seqString = ((char)seqCt).ToString();

        // Event Deduplication: Compare with LastProcessedSeq
        if (_lastProcessedSeq.HasValue && _lastProcessedSeq.Value == seqCt)
        {
            OnRawDataLogged?.Invoke("SYS", $"Duplicate event filtered out. Seq: {seqString}");
            return;
        }

        _lastProcessedSeq = seqCt;

        // Data part is from index 2 to length - 2 (excluding ETX)
        int dataLength = packetBytes.Length - 3;
        byte[] dataBytes = new byte[dataLength];
        Array.Copy(packetBytes, 2, dataBytes, 0, dataLength);

        if (dataBytes.Length == 0) return;

        char interruptCode = (char)dataBytes[0];

        switch (interruptCode)
        {
            case 'S': // Start of Communication
                OnStatusChanged?.Invoke("通訊啟動 (Start of Communication)");
                break;

            case 'P': // Stand By
                OnStatusChanged?.Invoke("待機狀態 (Stand By)");
                break;

            case 'C': // Cutting Card
                OnStatusChanged?.Invoke("切牌抽出 (Cutting Card)");
                OnCuttingCardDrawn?.Invoke(new CutCardInfo
                {
                    Seq = seqString,
                    RawBytes = rawBytesHex
                });
                break;

            case 'G': // Game Result
                if (dataBytes.Length >= 2)
                {
                    ParseGameResult(seqString, dataBytes[1], rawBytesHex);
                }
                break;

            case 'D': // Card Drawing (Active game)
            case 'd': // Card Drawing (Non-game state)
            case 'R': // Card Retransmission
                if (dataBytes.Length >= 3)
                {
                    ParseCardDrawing(interruptCode, seqString, dataBytes[1], dataBytes[2], rawBytesHex);
                }
                break;

            case 'E': // Error Occurrence
                if (dataBytes.Length >= 2)
                {
                    ParseError(seqString, dataBytes[1], rawBytesHex);
                }
                break;

            case 'e': // Error Cancellation
                if (dataBytes.Length >= 2)
                {
                    ParseErrorCancellation(seqString, dataBytes[1]);
                }
                break;

            case 'L': // Lock Status Changed
                if (dataBytes.Length >= 2)
                {
                    ParseLockStatus(dataBytes[1]);
                }
                break;

            case 'M': // Preset Value Changed
                if (dataBytes.Length >= 7)
                {
                    string addr = Encoding.ASCII.GetString(dataBytes, 1, 2);
                    string val = Encoding.ASCII.GetString(dataBytes, 3, 4);
                    OnStatusChanged?.Invoke($"預設值變更 - 位址: {addr}, 數值: {val}");
                }
                break;
        }
    }

    private void ParseStxResponse(byte[] packetBytes, string rawBytesHex)
    {
        // General STX responses (could be read register values)
        // Format: STX + Data + ETX
        int dataLength = packetBytes.Length - 2;
        if (dataLength <= 0) return;

        byte[] dataBytes = new byte[dataLength];
        Array.Copy(packetBytes, 1, dataBytes, 0, dataLength);

        string rawString = Encoding.ASCII.GetString(dataBytes);
        OnStatusChanged?.Invoke($"收到牌盒回覆: {rawString}");
    }

    private void ParseGameResult(string seq, byte payload, string rawBytesHex)
    {
        // Bit 7: 1
        // Bit 6-4: 001 (Player Win), 010 (Tie), 100 (Banker Win), 111 (Force Quit)
        // Bit 1-0: 00 (None), 01 (Player Pair), 10 (Banker Pair), 11 (Both Pairs)

        int resultBits = (payload >> 4) & 0x07;
        int pairBits = payload & 0x03;

        string result = resultBits switch
        {
            1 => "PlayerWin",
            2 => "Tie",
            4 => "BankerWin",
            7 => "ForceQuit",
            _ => "Unknown"
        };

        string pair = pairBits switch
        {
            0 => "None",
            1 => "PlayerPair",
            2 => "BankerPair",
            3 => "BothPair",
            _ => "None"
        };

        OnGameResult?.Invoke(new GameResult
        {
            Seq = seq,
            Result = result,
            Pair = pair,
            RawBytes = rawBytesHex
        });
    }

    private void ParseCardDrawing(char eventCode, string seq, byte byte1, byte byte2, string rawBytesHex)
    {
        // Byte 1: Draw target and sequence number
        // Bit 7: 1
        // Bit 6-4 (Intention): 000 (Player), 001 (Banker), 010 (Burn), 100 (First card to count burn), 101 (Burn count)
        // (For 'd'): 000 (Lock mode), 001 (Error mode), 010 (Setting mode)
        // Bit 3-0: Draw sequence index (0-10) or error code for 'd'

        // Byte 2: Suit and Value
        // Bit 7: 1
        // Bit 6-4 (Suit): 000 (None), 001 (Diamond), 010 (Club), 011 (Spade), 100 (Heart)
        // Bit 3-0 (Value): 0001 (A), 2-10, 11 (J), 12 (Q), 13 (K)

        int targetBits = (byte1 >> 4) & 0x07;
        int indexBits = byte1 & 0x0F;

        int suitBits = (byte2 >> 4) & 0x07;
        int valueBits = byte2 & 0x0F;

        string target = "Unknown";
        if (eventCode == 'D' || eventCode == 'R')
        {
            target = targetBits switch
            {
                0 => "Player",
                1 => "Banker",
                2 => "Burn",
                4 => "FirstCard",
                5 => "BurnCount",
                _ => "Unknown"
            };
        }
        else if (eventCode == 'd')
        {
            target = targetBits switch
            {
                0 => "LockMode",
                1 => "ErrorMode",
                2 => "SettingMode",
                _ => "NonGameState"
            };
        }

        string suit = suitBits switch
        {
            0 => "None",
            1 => "Diamond",
            2 => "Club",
            3 => "Spade",
            4 => "Heart",
            _ => "None"
        };

        string value = valueBits switch
        {
            1 => "A",
            11 => "J",
            12 => "Q",
            13 => "K",
            _ => (valueBits >= 2 && valueBits <= 10) ? valueBits.ToString() : "None"
        };

        OnCardDrawn?.Invoke(new CardInfo
        {
            EventCode = eventCode,
            Seq = seq,
            IsActiveGame = (eventCode == 'D'),
            Target = target,
            Index = indexBits,
            Suit = suit,
            Value = value,
            RawBytes = rawBytesHex
        });
    }

    private void ParseError(string seq, byte payload, string rawBytesHex)
    {
        // Bit 7: 1
        // Bit 6: Error Mode flag (0 = Non-error mode, 1 = Error mode)
        // Bit 5-0: Error code
        bool isErrorMode = ((payload >> 6) & 0x01) == 1;
        int errCode = payload & 0x3F;

        string errMsg = GetSystemErrorDescription(errCode);

        OnErrorOccurred?.Invoke(new ErrorInfo
        {
            Seq = seq,
            InErrorMode = isErrorMode,
            ErrorCode = errCode,
            ErrorMessage = errMsg,
            RawBytes = rawBytesHex
        });
    }

    private void ParseErrorCancellation(string seq, byte payload)
    {
        int errCode = payload & 0x7F; // Bit 7 is 1
        string errMsg = GetSystemErrorDescription(errCode);
        OnStatusChanged?.Invoke($"錯誤消除 (Error Cleared) - 代碼: {errCode} ({errMsg})");
        OnErrorCleared?.Invoke(errCode, errMsg);
    }

    private void ParseLockStatus(byte payload)
    {
        // Bit 0: 0 = Unlocked, 1 = Locked
        bool isLocked = (payload & 0x01) == 1;
        OnStatusChanged?.Invoke(isLocked ? "牌盒狀態: 已鎖定 (Locked)" : "牌盒狀態: 已解鎖 (Unlocked)");
        OnLockStatusChanged?.Invoke(isLocked);
    }

    /// <summary>
    /// Calculates the BCC used by inbound active report packets.
    /// </summary>
    /// <param name="packetBytes">Packet bytes from ENQ or STX through ETX.</param>
    /// <returns>Uppercase two-character hexadecimal BCC.</returns>
    public static string CalculateBcc(byte[] packetBytes)
    {
        // 接收封包（ENQ 主動上報）BCC：規格說「自 Seq.ct 起至 ETX 止」，即跳過 ENQ/STX（index 0）。
        // 注意：發送端 CalculateCommandBcc 因規格範例含 STX 才算出正確值，故從 index 0 起；
        // 兩者行為不同，待實際硬體驗證後再統一。
        byte xorSum = 0;
        for (int i = 1; i < packetBytes.Length; i++)
        {
            xorSum ^= packetBytes[i];
        }

        return xorSum.ToString("X2");
    }

    /// <summary>
    /// Sends raw bytes to the serial port without waiting for ACK or NAK.
    /// </summary>
    /// <param name="cmdBytes">Command bytes to send.</param>
    public void SendRawCommand(byte[] cmdBytes)
    {
        if (IsMockMode)
        {
            string hex = BitConverter.ToString(cmdBytes).Replace("-", " ");
            OnRawDataLogged?.Invoke("TX", $"{hex} (Simulated)");
            return;
        }

        if (TryWritePhysicalBytes(cmdBytes, out string? errorMessage))
        {
            string hex = BitConverter.ToString(cmdBytes).Replace("-", " ");
            OnRawDataLogged?.Invoke("TX", hex);
        }
        else
        {
            OnRawDataLogged?.Invoke("SYS", errorMessage ?? "Cannot send command: connection is closed.");
        }
    }

    /// <summary>
    /// Sends command bytes and waits for ACK, NAK, timeout, or cancellation.
    /// </summary>
    /// <param name="cmdBytes">Command bytes to send.</param>
    /// <param name="timeout">Maximum wait time for ACK or NAK.</param>
    /// <param name="cancellationToken">Cancellation token for the wait operation.</param>
    /// <returns>The command result.</returns>
    public async Task<SerialCommandResult> SendCommandAsync(byte[] cmdBytes, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            string hex = BitConverter.ToString(cmdBytes).Replace("-", " ");
            if (IsMockMode)
            {
                OnRawDataLogged?.Invoke("TX", $"{hex} (Simulated)");
                SerialCommandResult mockResult = new(true, null, "ACK (simulated)");
                OnCommandAcknowledged?.Invoke(mockResult);
                return mockResult;
            }

            if (!IsPhysicalConnectionOpen)
            {
                SerialCommandResult closedResult = new(false, null, "Connection is closed");
                OnRawDataLogged?.Invoke("SYS", "Cannot send command: connection is closed.");
                return closedResult;
            }

            TaskCompletionSource<SerialCommandResult> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pendingCommandLock)
            {
                _pendingCommand = pending;
            }

            try
            {
                if (!TryWritePhysicalBytes(cmdBytes, out string? errorMessage))
                {
                    throw new InvalidOperationException(errorMessage ?? "Connection is closed");
                }

                OnRawDataLogged?.Invoke("TX", hex);
            }
            catch (Exception ex)
            {
                ClearPendingCommand(pending);
                SerialCommandResult errorResult = new(false, null, $"Error writing to connection: {ex.Message}");
                OnRawDataLogged?.Invoke("SYS", errorResult.Message);
                return errorResult;
            }

            Task timeoutTask = Task.Delay(timeout, cancellationToken);
            Task completed = await Task.WhenAny(pending.Task, timeoutTask);
            if (completed == pending.Task)
            {
                return await pending.Task;
            }

            ClearPendingCommand(pending);
            SerialCommandResult timeoutResult = new(false, null, $"Timeout waiting for ACK/NAK after {timeout.TotalSeconds:0.#}s");
            OnRawDataLogged?.Invoke("SYS", timeoutResult.Message);
            return timeoutResult;
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private bool IsPhysicalConnectionOpen =>
        (_serialPort != null && _serialPort.IsOpen) ||
        (_tcpClient != null && _tcpClient.Connected && _tcpStream != null);

    private bool TryWritePhysicalBytes(byte[] bytes, out string? errorMessage)
    {
        errorMessage = null;

        try
        {
            if (_serialPort != null && _serialPort.IsOpen)
            {
                _serialPort.Write(bytes, 0, bytes.Length);
                return true;
            }

            if (_tcpStream != null && _tcpClient != null && _tcpClient.Connected)
            {
                _tcpStream.Write(bytes, 0, bytes.Length);
                return true;
            }

            errorMessage = "Cannot send command: connection is closed.";
            return false;
        }
        catch (Exception ex)
        {
            errorMessage = $"Error writing to connection: {ex.Message}";
            return false;
        }
    }

    private void CompletePendingCommand(SerialCommandResult result)
    {
        TaskCompletionSource<SerialCommandResult>? pending;
        lock (_pendingCommandLock)
        {
            pending = _pendingCommand;
            _pendingCommand = null;
        }

        if (pending != null)
        {
            pending.TrySetResult(result);
            OnCommandAcknowledged?.Invoke(result);
        }
    }

    private void ClearPendingCommand(TaskCompletionSource<SerialCommandResult> pending)
    {
        lock (_pendingCommandLock)
        {
            if (ReferenceEquals(_pendingCommand, pending))
            {
                _pendingCommand = null;
            }
        }
    }

    /// <summary>
    /// Returns the protocol-defined system error description.
    /// </summary>
    /// <param name="errCode">System error code.</param>
    /// <returns>Human-readable error description.</returns>
    public static string GetSystemErrorDescription(int errCode)
    {
        return errCode switch
        {
            1 => "讀牌錯誤 (Can not read)",
            2 => "發牌過多 (Overdraw)",
            3 => "按鍵誤操作 (Mishandling of button)",
            4 => "出口逆向抽回 (Reverse run at exit)",
            5 => "中途逆向抽回 (Reverse run midway)",
            6 => "牌組代碼錯誤 (Card code error)",
            7 => "抽牌超時 (Time-out of drawing)",
            _ => "未知錯誤"
        };
    }

    /// <summary>
    /// Returns the protocol-defined NAK error description.
    /// </summary>
    /// <param name="errCode">NAK error code.</param>
    /// <returns>Human-readable NAK description.</returns>
    public static string GetNakErrorDescription(int errCode)
    {
        return errCode switch
        {
            0x01 => "溢位/影格/奇偶校驗錯誤",
            0x02 => "BCC 校驗錯誤",
            0x03 => "無 ETX 結束字元",
            0x11 => "電報格式/長度錯誤",
            0x12 => "無效設定位址 (Address Error)",
            0x13 => "無效指令 (Command Error)",
            0x14 => "無效操作類型 (Type Error)",
            0x21 => "牌盒忙碌中 (Busy)",
            0x22 => "寫入處理中",
            0xFF => "其他錯誤",
            _ => "未知拒絕錯誤"
        };
    }
}
