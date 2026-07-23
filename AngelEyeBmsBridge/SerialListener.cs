using System;
using System.IO.Ports;
using System.Net.Sockets;

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
    private readonly AngelEyeFrameDecoder _decoder = new();
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly object _pendingCommandLock = new();
    private TaskCompletionSource<SerialCommandResult>? _pendingCommand;

    public SerialListener()
    {
        _decoder.CardDrawn += value => OnCardDrawn?.Invoke(value);
        _decoder.GameResultReceived += value => OnGameResult?.Invoke(value);
        _decoder.ErrorOccurred += value => OnErrorOccurred?.Invoke(value);
        _decoder.CuttingCardDrawn += value => OnCuttingCardDrawn?.Invoke(value);
        _decoder.LockStatusChanged += value => OnLockStatusChanged?.Invoke(value);
        _decoder.ErrorCleared += (code, message) => OnErrorCleared?.Invoke(code, message);
        _decoder.StatusChanged += value => OnStatusChanged?.Invoke(value);
        _decoder.Diagnostic += (kind, message) => OnRawDataLogged?.Invoke(kind, message);
        _decoder.CommandAcknowledged += CompletePendingCommand;
    }

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
        _decoder.Reset();
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
        _decoder.Feed(bytes);
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

    /// <summary>
    /// Calculates the BCC used by inbound active report packets.
    /// </summary>
    /// <param name="packetBytes">Packet bytes from ENQ or STX through ETX.</param>
    /// <returns>Uppercase two-character hexadecimal BCC.</returns>
    public static string CalculateBcc(byte[] packetBytes)
    {
        return AngelEyeFrameDecoder.CalculateBcc(packetBytes);
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
