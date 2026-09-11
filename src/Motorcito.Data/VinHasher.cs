using System.Security.Cryptography;
using System.Text;

namespace Motorcito.Data;

/// <summary>
/// Turns a VIN into a stable, non-reversible identifier for anything that
/// leaves the device.
///
/// A VIN identifies a specific vehicle, and through registration and insurance
/// records, its owner. The plaintext is genuinely useful on-device — it decodes
/// year, make, model and engine — so it stays in the local database. Nothing
/// beyond the device needs it: a hash is enough to recognise the same car
/// twice, and useless for identifying whose it is.
///
/// <para><b>A plain hash is not enough.</b> The VIN space is small and heavily
/// structured — 17 characters from a restricted alphabet, with the first
/// eleven describing manufacturer, model and plant. An unsalted SHA-256 of a
/// VIN is brute-forceable in practice, so anyone holding a leaked hash could
/// recover the original. The pepper is what prevents that, and it is only
/// effective while it stays secret.</para>
///
/// <para><b>Where the pepper lives.</b> Server-side, never in the app binary —
/// anything shipped to a device is extractable, which would reduce this to the
/// unsalted case. So the intended flow is: the device sends the plaintext VIN
/// over TLS, the server hashes it on arrival, stores only the hash, and
/// discards the plaintext. This class is written to run on either side so the
/// decision is not foreclosed, but shipping the pepper is not a real option.</para>
/// </summary>
public static class VinHasher
{
    /// <summary>
    /// HMAC-SHA256 of a normalised VIN, hex-encoded.
    ///
    /// HMAC rather than a plain salted hash: it is the construction designed
    /// for keyed hashing, and it avoids the length-extension pitfalls of naive
    /// concatenation.
    /// </summary>
    /// <param name="vin">The raw VIN as read from the vehicle.</param>
    /// <param name="pepper">A secret key. Must not be shipped in an app binary.</param>
    public static string Hash(string vin, string pepper)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vin);
        ArgumentException.ThrowIfNullOrWhiteSpace(pepper);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(pepper));
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(Normalise(vin)));

        return Convert.ToHexStringLower(digest);
    }

    /// <summary>
    /// Canonical form, so the same physical car always hashes identically.
    ///
    /// Adapters differ in casing and padding, and a VIN echoed with a trailing
    /// space would otherwise hash to a different vehicle — silently splitting
    /// one car's history in two.
    /// </summary>
    public static string Normalise(string vin) => vin.Trim().ToUpperInvariant();
}
