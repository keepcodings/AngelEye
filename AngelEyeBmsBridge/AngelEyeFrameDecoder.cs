using System.Text;

namespace AngelEyeBmsBridge;

/// <summary>
/// Stateful, transport-independent decoder for ANGEL EYE II-EX receive frames.
/// It accepts bytes and emits immutable observations; it has no socket, serial,
/// command, journal, or BMS capability.
/// </summary>
public sealed class AngelEyeFrameDecoder
{
    private readonly List<byte> _buffer = [];
    private byte? _lastProcessedSequence;

    public event Action<SerialListener.CardInfo>? CardDrawn;
    public event Action<SerialListener.GameResult>? GameResultReceived;
    public event Action<SerialListener.ErrorInfo>? ErrorOccurred;
    public event Action<SerialListener.CutCardInfo>? CuttingCardDrawn;
    public event Action<bool>? LockStatusChanged;
    public event Action<int, string>? ErrorCleared;
    public event Action<string>? StatusChanged;
    public event Action<string, string>? Diagnostic;
    public event Action<SerialListener.SerialCommandResult>? CommandAcknowledged;

    /// <summary>Feeds one receive chunk into the decoder.</summary>
    public void Feed(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        lock (_buffer)
        {
            for (var index = 0; index < bytes.Length; index++)
            {
                _buffer.Add(bytes[index]);
            }

            ProcessBuffer();
        }
    }

    /// <summary>Clears incomplete-frame and sequence-deduplication state.</summary>
    public void Reset()
    {
        lock (_buffer)
        {
            _buffer.Clear();
            _lastProcessedSequence = null;
        }
    }

    private void ProcessBuffer()
    {
        while (_buffer.Count > 0)
        {
            var startIndex = _buffer.FindIndex(value =>
                value is 0x02 or 0x05 or 0x06 or 0x15);
            if (startIndex < 0)
            {
                _buffer.Clear();
                return;
            }

            if (startIndex > 0)
            {
                _buffer.RemoveRange(0, startIndex);
            }

            var startCode = _buffer[0];
            if (startCode == 0x06)
            {
                Diagnostic?.Invoke("RX", "06 (ACK)");
                CommandAcknowledged?.Invoke(
                    new SerialListener.SerialCommandResult(true, null, "ACK"));
                _buffer.RemoveAt(0);
                continue;
            }

            if (startCode == 0x15)
            {
                if (_buffer.Count < 2)
                {
                    return;
                }

                var errorCode = _buffer[1];
                var errorName = SerialListener.GetNakErrorDescription(errorCode);
                Diagnostic?.Invoke("RX", $"15 {errorCode:X2} (NAK: {errorName})");
                CommandAcknowledged?.Invoke(
                    new SerialListener.SerialCommandResult(false, errorCode, $"NAK: {errorName}"));
                _buffer.RemoveRange(0, 2);
                continue;
            }

            var etxIndex = _buffer.IndexOf(0x03);
            if (etxIndex < 0)
            {
                if (_buffer.Count > 128)
                {
                    _buffer.Clear();
                    Diagnostic?.Invoke("SYS", "Warning: Buffer overflow without ETX. Buffer cleared.");
                }

                return;
            }

            if (_buffer.Count < etxIndex + 3)
            {
                return;
            }

            var packetBytes = _buffer.GetRange(0, etxIndex + 1).ToArray();
            var receivedBcc = Encoding.ASCII.GetString(
                [_buffer[etxIndex + 1], _buffer[etxIndex + 2]]);
            var packetSize = etxIndex + 3;
            var fullPacket = _buffer.GetRange(0, packetSize).ToArray();
            var rawHex = ToHex(fullPacket);
            Diagnostic?.Invoke("RX", rawHex);

            var calculatedBcc = CalculateBcc(packetBytes);
            if (!string.Equals(calculatedBcc, receivedBcc, StringComparison.Ordinal))
            {
                Diagnostic?.Invoke(
                    "SYS",
                    $"BCC Check failed. Expected: {calculatedBcc}, Received: {receivedBcc}");
                _buffer.RemoveRange(0, packetSize);
                continue;
            }

            try
            {
                if (startCode == 0x05)
                {
                    ParseActiveReport(packetBytes, rawHex);
                }
                else
                {
                    ParseStxResponse(packetBytes);
                }
            }
            catch (Exception exception)
            {
                Diagnostic?.Invoke("SYS", $"Error parsing packet: {exception.Message}");
            }

            _buffer.RemoveRange(0, packetSize);
        }
    }

    private void ParseActiveReport(byte[] packetBytes, string rawHex)
    {
        if (packetBytes.Length < 4)
        {
            return;
        }

        var sequence = packetBytes[1];
        var sequenceText = ((char)sequence).ToString();
        if (_lastProcessedSequence == sequence)
        {
            Diagnostic?.Invoke("SYS", $"Duplicate event filtered out. Seq: {sequenceText}");
            return;
        }

        _lastProcessedSequence = sequence;
        var dataLength = packetBytes.Length - 3;
        var data = new byte[dataLength];
        Array.Copy(packetBytes, 2, data, 0, dataLength);
        if (data.Length == 0)
        {
            return;
        }

        switch ((char)data[0])
        {
            case 'S':
                StatusChanged?.Invoke("通訊啟動 (Start of Communication)");
                break;
            case 'P':
                StatusChanged?.Invoke("待機狀態 (Stand By)");
                break;
            case 'C':
                StatusChanged?.Invoke("切牌抽出 (Cutting Card)");
                CuttingCardDrawn?.Invoke(new SerialListener.CutCardInfo
                {
                    Seq = sequenceText,
                    RawBytes = rawHex
                });
                break;
            case 'G' when data.Length >= 2:
                ParseGameResult(sequenceText, data[1], rawHex);
                break;
            case 'D' or 'd' or 'R' when data.Length >= 3:
                ParseCardDrawing((char)data[0], sequenceText, data[1], data[2], rawHex);
                break;
            case 'E' when data.Length >= 2:
                ParseError(sequenceText, data[1], rawHex);
                break;
            case 'e' when data.Length >= 2:
                ParseErrorCancellation(data[1]);
                break;
            case 'L' when data.Length >= 2:
                ParseLockStatus(data[1]);
                break;
            case 'M' when data.Length >= 7:
                var address = Encoding.ASCII.GetString(data, 1, 2);
                var value = Encoding.ASCII.GetString(data, 3, 4);
                StatusChanged?.Invoke($"預設值變更 - 位址: {address}, 數值: {value}");
                break;
        }
    }

    private void ParseStxResponse(byte[] packetBytes)
    {
        var dataLength = packetBytes.Length - 2;
        if (dataLength <= 0)
        {
            return;
        }

        var data = new byte[dataLength];
        Array.Copy(packetBytes, 1, data, 0, dataLength);
        StatusChanged?.Invoke($"收到牌盒回覆: {Encoding.ASCII.GetString(data)}");
    }

    private void ParseGameResult(string sequence, byte payload, string rawHex)
    {
        var result = ((payload >> 4) & 0x07) switch
        {
            1 => "PlayerWin",
            2 => "Tie",
            4 => "BankerWin",
            7 => "ForceQuit",
            _ => "Unknown"
        };
        var pair = (payload & 0x03) switch
        {
            0 => "None",
            1 => "PlayerPair",
            2 => "BankerPair",
            3 => "BothPair",
            _ => "None"
        };

        GameResultReceived?.Invoke(new SerialListener.GameResult
        {
            Seq = sequence,
            Result = result,
            Pair = pair,
            RawBytes = rawHex
        });
    }

    private void ParseCardDrawing(
        char eventCode,
        string sequence,
        byte targetAndIndex,
        byte suitAndValue,
        string rawHex)
    {
        var targetBits = (targetAndIndex >> 4) & 0x07;
        var index = targetAndIndex & 0x0F;
        var suitBits = (suitAndValue >> 4) & 0x07;
        var valueBits = suitAndValue & 0x0F;

        var target = eventCode switch
        {
            'D' or 'R' => targetBits switch
            {
                0 => "Player",
                1 => "Banker",
                2 => "Burn",
                4 => "FirstCard",
                5 => "BurnCount",
                _ => "Unknown"
            },
            'd' => targetBits switch
            {
                0 => "LockMode",
                1 => "ErrorMode",
                2 => "SettingMode",
                _ => "NonGameState"
            },
            _ => "Unknown"
        };
        var suit = suitBits switch
        {
            0 => "None",
            1 => "Diamond",
            2 => "Club",
            3 => "Spade",
            4 => "Heart",
            _ => "None"
        };
        var value = valueBits switch
        {
            1 => "A",
            11 => "J",
            12 => "Q",
            13 => "K",
            >= 2 and <= 10 => valueBits.ToString(),
            _ => "None"
        };

        CardDrawn?.Invoke(new SerialListener.CardInfo
        {
            EventCode = eventCode,
            Seq = sequence,
            IsActiveGame = eventCode == 'D',
            Target = target,
            Index = index,
            Suit = suit,
            Value = value,
            RawBytes = rawHex
        });
    }

    private void ParseError(string sequence, byte payload, string rawHex)
    {
        var errorMode = ((payload >> 6) & 0x01) == 1;
        var errorCode = payload & 0x3F;
        ErrorOccurred?.Invoke(new SerialListener.ErrorInfo
        {
            Seq = sequence,
            InErrorMode = errorMode,
            ErrorCode = errorCode,
            ErrorMessage = SerialListener.GetSystemErrorDescription(errorCode),
            RawBytes = rawHex
        });
    }

    private void ParseErrorCancellation(byte payload)
    {
        var errorCode = payload & 0x7F;
        var message = SerialListener.GetSystemErrorDescription(errorCode);
        StatusChanged?.Invoke($"錯誤消除 (Error Cleared) - 代碼: {errorCode} ({message})");
        ErrorCleared?.Invoke(errorCode, message);
    }

    private void ParseLockStatus(byte payload)
    {
        var locked = (payload & 0x01) == 1;
        StatusChanged?.Invoke(
            locked ? "牌盒狀態: 已鎖定 (Locked)" : "牌盒狀態: 已解鎖 (Unlocked)");
        LockStatusChanged?.Invoke(locked);
    }

    public static string CalculateBcc(ReadOnlySpan<byte> packetBytes)
    {
        byte xor = 0;
        for (var index = 1; index < packetBytes.Length; index++)
        {
            xor ^= packetBytes[index];
        }

        return xor.ToString("X2");
    }

    private static string ToHex(byte[] bytes) =>
        BitConverter.ToString(bytes).Replace("-", " ");
}
