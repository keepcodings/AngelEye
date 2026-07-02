using System.Text;

namespace AngelEyeBmsBridge;

/// <summary>
/// Provides packet builders and helpers for the ANGEL EYE II-EX serial protocol.
/// </summary>
public static class AngelEyeProtocol
{
    /// <summary>Bridge source name used in local payloads and logs.</summary>
    public const string SourceName = "AngelEye";

    /// <summary>Operation type used by the lock and unlock command.</summary>
    public const string LockType = "LK";

    /// <summary>Operation type used by the error cancellation command.</summary>
    public const string ErrorCancelType = "EC";

    /// <summary>Operation type used by the game process confirmation command.</summary>
    public const string GameProcessConfirmationType = "GP";

    /// <summary>
    /// Builds the serial command that asks the shoe to enter or leave lock mode.
    /// </summary>
    /// <param name="locked">True to lock the shoe; false to unlock it.</param>
    /// <returns>The complete packet bytes including STX, ETX, and BCC.</returns>
    public static byte[] BuildLockCommand(bool locked)
    {
        return BuildOperationCommand(LockType, locked ? "02" : "01");
    }

    /// <summary>
    /// Builds the serial command that asks the shoe to clear a specific error code.
    /// </summary>
    /// <param name="data">Two-digit error clear data from 00 through 99.</param>
    /// <returns>The complete packet bytes including STX, ETX, and BCC.</returns>
    public static byte[] BuildCancelErrorCommand(string data)
    {
        return BuildOperationCommand(ErrorCancelType, NormalizeTwoDigitData(data));
    }

    /// <summary>
    /// Builds the serial command that asks the shoe to confirm the current game process.
    /// </summary>
    /// <returns>The complete packet bytes including STX, ETX, and BCC.</returns>
    public static byte[] BuildGameProcessConfirmationCommand()
    {
        return BuildOperationCommand(GameProcessConfirmationType, "00");
    }

    /// <summary>
    /// Builds a generic operation command packet for the ANGEL EYE II-EX host interface.
    /// </summary>
    /// <param name="type">Two-character ASCII operation type.</param>
    /// <param name="data">Two-digit operation payload.</param>
    /// <returns>The complete packet bytes including STX, ETX, and BCC.</returns>
    public static byte[] BuildOperationCommand(string type, string data)
    {
        if (type.Length != 2)
        {
            throw new ArgumentException("Operation type must be two ASCII characters.", nameof(type));
        }

        string normalizedData = NormalizeTwoDigitData(data);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type.ToUpperInvariant());
        byte[] dataBytes = Encoding.ASCII.GetBytes(normalizedData);

        byte[] packet = [0x02, (byte)'O', (byte)'P', typeBytes[0], typeBytes[1], dataBytes[0], dataBytes[1], 0x03];
        string bcc = CalculateCommandBcc(packet);
        byte[] bccBytes = Encoding.ASCII.GetBytes(bcc);

        byte[] fullPacket = new byte[packet.Length + bccBytes.Length];
        Array.Copy(packet, 0, fullPacket, 0, packet.Length);
        Array.Copy(bccBytes, 0, fullPacket, packet.Length, bccBytes.Length);
        return fullPacket;
    }

    /// <summary>
    /// Normalizes a one- or two-digit operation data value to the two-character protocol form.
    /// </summary>
    /// <param name="data">Operation data from 00 through 99.</param>
    /// <returns>A two-character digit string.</returns>
    public static string NormalizeTwoDigitData(string data)
    {
        string trimmed = (data ?? string.Empty).Trim();
        if (trimmed.Length == 1 && char.IsDigit(trimmed[0]))
        {
            return "0" + trimmed;
        }

        if (trimmed.Length == 2 && trimmed.All(char.IsDigit))
        {
            return trimmed;
        }

        throw new ArgumentException("Operation data must be 00 through 99.", nameof(data));
    }

    /// <summary>
    /// Calculates the BCC used by outbound command packets.
    /// </summary>
    /// <param name="packetBytes">Packet bytes from STX through ETX.</param>
    /// <returns>Uppercase two-character hexadecimal BCC.</returns>
    public static string CalculateCommandBcc(byte[] packetBytes)
    {
        if (packetBytes.Length == 0)
        {
            return "00";
        }

        // 規格文字說 BCC「自 Cmd 欄位起至 ETX 止」（不含 STX），但規格 §10.1 的 Lock ON 範例
        // 02 4F 50 4C 4B 30 32 03 → BCC="1B"，只有含 STX 計算才能得到 1B；不含 STX 得 19。
        // 依範例行為實作（含 STX，從 index 0 起）。若硬體測試出現 NAK 需改從 index 1 開始。
        byte xorSum = 0;
        for (int i = 0; i < packetBytes.Length; i++)
        {
            xorSum ^= packetBytes[i];
        }

        return xorSum.ToString("X2");
    }

    /// <summary>
    /// Formats raw bytes as a space-separated uppercase hexadecimal string.
    /// </summary>
    /// <param name="bytes">Bytes to format.</param>
    /// <returns>Human-readable hexadecimal text.</returns>
    public static string FormatHex(byte[] bytes)
    {
        return BitConverter.ToString(bytes).Replace("-", " ");
    }
}
