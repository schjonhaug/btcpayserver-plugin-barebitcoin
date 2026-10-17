using System.Text;
using BTCPayServer.Lightning;
using NBitcoin;

namespace BTCPayServer.Plugins.Tests;

/// <summary>
/// Signs mainnet BOLT11 invoices for a chosen preimage, so tests can check a provider preimage
/// against the payment hash. The official BOLT #11 vectors do not publish their preimages.
/// </summary>
internal static class TestBolt11
{
    private const string Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
    private static readonly Key SigningKey = new(Enumerable.Repeat((byte)0x11, 32).ToArray());

    public static string Create(byte[] preimage, DateTimeOffset timestamp, long? amountSatoshis = null)
    {
        var hrp = "lnbc" + (amountSatoshis is { } sats ? $"{sats * 10}n" : string.Empty);
        var words = new List<byte>();
        AppendNumber(words, timestamp.ToUnixTimeSeconds(), 7);
        AppendField(words, 'p', NBitcoin.Crypto.Hashes.SHA256(preimage));
        AppendField(words, 'd', Encoding.UTF8.GetBytes("test"));

        var signed = Encoding.ASCII.GetBytes(hrp).Concat(ToBytes(words)).ToArray();
        var signature = SigningKey.SignCompact(new uint256(NBitcoin.Crypto.Hashes.SHA256(signed)), false);
        words.AddRange(ToWords(signature.Signature.Append((byte)signature.RecoveryId).ToArray()));

        var checksum = Checksum(hrp, words);
        return hrp + "1" + new string(words.Concat(checksum).Select(w => Charset[w]).ToArray());
    }

    public static string Create(byte[] preimage, long? amountSatoshis = null) =>
        Create(preimage, DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()), amountSatoshis);

    public static BOLT11PaymentRequest Parse(string bolt11) => BOLT11PaymentRequest.Parse(bolt11, Network.Main);

    private static void AppendField(List<byte> words, char tag, byte[] data)
    {
        var dataWords = ToWords(data);
        words.Add((byte)Charset.IndexOf(tag));
        AppendNumber(words, dataWords.Count, 2);
        words.AddRange(dataWords);
    }

    private static void AppendNumber(List<byte> words, long value, int length)
    {
        for (var i = length - 1; i >= 0; i--)
            words.Add((byte)((value >> (5 * i)) & 31));
    }

    private static List<byte> ToWords(byte[] data) => Regroup(data, 8, 5);

    private static byte[] ToBytes(List<byte> words) => Regroup(words, 5, 8).ToArray();

    private static List<byte> Regroup(IEnumerable<byte> values, int fromBits, int toBits)
    {
        var result = new List<byte>();
        int accumulator = 0, bits = 0, mask = (1 << toBits) - 1;
        foreach (var value in values)
        {
            accumulator = (accumulator << fromBits) | value;
            bits += fromBits;
            while (bits >= toBits)
            {
                bits -= toBits;
                result.Add((byte)((accumulator >> bits) & mask));
            }
        }
        if (bits > 0)
            result.Add((byte)((accumulator << (toBits - bits)) & mask));
        return result;
    }

    private static IEnumerable<byte> Checksum(string hrp, List<byte> words)
    {
        var values = hrp.Select(c => (byte)(c >> 5))
            .Append((byte)0)
            .Concat(hrp.Select(c => (byte)(c & 31)))
            .Concat(words)
            .Concat(new byte[6]);
        var polymod = Polymod(values) ^ 1;
        return Enumerable.Range(0, 6).Select(i => (byte)((polymod >> (5 * (5 - i))) & 31));
    }

    private static uint Polymod(IEnumerable<byte> values)
    {
        uint[] generator = [0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3];
        uint checksum = 1;
        foreach (var value in values)
        {
            var top = checksum >> 25;
            checksum = ((checksum & 0x1ffffff) << 5) ^ value;
            for (var i = 0; i < 5; i++)
                if (((top >> i) & 1) == 1)
                    checksum ^= generator[i];
        }
        return checksum;
    }
}
