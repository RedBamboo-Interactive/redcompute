namespace RedCompute.Node.Pairing;

/// <summary>
/// The one-time pairing code (design 3.1): 8 symbols of Crockford base32, shown <c>XXXX-XXXX</c>.
/// Input is case-insensitive, hyphens and spaces are optional, and the look-alikes Crockford maps are
/// accepted (<c>O</c> as 0, <c>I</c> and <c>L</c> as 1). <c>U</c> is never a symbol.
/// </summary>
public static class PairingCode
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static bool TryNormalize(string? text, out string code)
    {
        code = "";
        if (text is null) return false;
        var symbols = new List<char>(8);
        foreach (var raw in text)
        {
            if (raw is '-' or ' ') continue;
            var ch = char.ToUpperInvariant(raw) switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                var other => other,
            };
            if (!Alphabet.Contains(ch)) return false;
            symbols.Add(ch);
        }
        if (symbols.Count != 8) return false;
        code = new string(symbols.Take(4).ToArray()) + "-" + new string(symbols.Skip(4).ToArray());
        return true;
    }

    public static string Normalize(string? text)
        => TryNormalize(text, out var code)
            ? code
            : throw new NodeCommandException("bad_code",
                "That is not a pairing code. It has 8 letters and digits, like ABCD-EFGH (Leaf shows it in \"Add a node\").",
                NodeExitCodes.Usage);
}
