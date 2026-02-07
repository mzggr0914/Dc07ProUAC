using HidSharp;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Dc07ProUAC;

public sealed class HidDeviceService
{
    private static readonly char[] TokenSplitChars = [' '];

    private static readonly string[] NoiseProductNames =
    [
        "hid interface",
        "hid-compliant device",
        "usb input device",
        "composite device",
        "generic hid",
        "hid device"
    ];

    private static readonly string[] PreferredBrandTokens = ["ibasso"];
    private static readonly string[] PreferredModelTokens = ["dc07pro", "dc07", "dc07 pro"];


    public static Task<AudioDevicePickerDialog.DeviceRow[]> ListRowsAsync(string queryForRanking = "")
        => Task.Run(() =>
        {
            var devices = DeviceList.Local.GetHidDevices().ToArray();

            return devices
                .Select(d =>
                {
                    int vid = 0, pid = 0;
                    string product = "", manu = "", sn = "", path = "";

                    try { vid = d.VendorID; } catch (Exception ex) { Debug.WriteLine(ex); }
                    try { pid = d.ProductID; } catch (Exception ex) { Debug.WriteLine(ex); }

                    try
                    {
                        var p1 = d.GetProductName() ?? "";
                        var p2 = d.GetFriendlyName() ?? "";
                        product = string.IsNullOrWhiteSpace(p2) ? p1 : $"{p1}: {p2}";
                    }
                    catch (Exception ex) { Debug.WriteLine(ex); }

                    try { manu = d.GetManufacturer() ?? ""; } catch (Exception ex) { Debug.WriteLine(ex); }
                    try { sn = d.GetSerialNumber() ?? ""; } catch (Exception ex) { Debug.WriteLine(ex); }
                    try { path = d.DevicePath ?? ""; } catch (Exception ex) { Debug.WriteLine(ex); }
                    var haystack = $"{product} {manu} {sn} {vid:X4}:{pid:X4} {path}";
                    return new AudioDevicePickerDialog.DeviceRow(path, vid, pid, product, manu, sn, ComputeScore(queryForRanking, product, manu, haystack));
                })
                .ToArray();
        });

    public static int ComputeScore(string query, string product, string manufacturer, string haystack)
    {
        var baseScore = SimilarityScore(query, haystack);

        var pNorm = Normalize(product);
        var mNorm = Normalize(manufacturer);

        if (IsNoiseProductName(pNorm)) baseScore -= 15;

        if (ContainsAny(mNorm, PreferredBrandTokens)) baseScore += 35;
        if (ContainsAny(pNorm, PreferredModelTokens) || ContainsAny(Normalize(query), PreferredModelTokens)) baseScore += 25;

        if (pNorm.Contains("dc07", StringComparison.Ordinal)) baseScore += 10;
        if (mNorm.Contains("ibasso", StringComparison.Ordinal)) baseScore += 10;

        return Clamp(baseScore, 0, 100);

        static bool IsNoiseProductName(string p)
            => NoiseProductNames.Any(n => p.Contains(n, StringComparison.Ordinal));

        static bool ContainsAny(string s, string[] tokens)
            => tokens.Any(t => s.Contains(Normalize(t), StringComparison.Ordinal));
    }

    private static int LevenshteinDistance(string a, string b)
    {
        if (a == b) return 0;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            var ca = a[i - 1];

            for (var j = 1; j <= b.Length; j++)
            {
                var cost = ca == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(curr[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost
                );
            }

            (prev, curr) = (curr, prev);
        }

        return prev[b.Length];
    }

    private static int SimilarityScore(string query, string haystack)
    {
        if (string.IsNullOrWhiteSpace(query)) return 0;

        var q = Normalize(query);
        var h = Normalize(haystack);

        if (h.Contains(q, StringComparison.Ordinal)) return 100;

        var tokens = q.Split(TokenSplitChars, StringSplitOptions.RemoveEmptyEntries)
            .Distinct()
            .ToArray();

        var tokenHits = tokens.Count(t => t.Length >= 2 && h.Contains(t, StringComparison.Ordinal));
        var tokenScore = tokens.Length == 0 ? 0 : (int)Math.Round(70.0 * tokenHits / tokens.Length);

        var hShort = h.Length > 80 ? h[..80] : h;
        var lev = LevenshteinDistance(q, hShort);
        var maxLen = Math.Max(q.Length, hShort.Length);
        var levScore = maxLen == 0 ? 0 : (int)Math.Round(30.0 * (1.0 - (double)lev / maxLen));

        return Clamp(tokenScore + Clamp(levScore, 0, 30), 0, 100);
    }

    private static string Normalize(string s)
    {
        var lowered = s.Trim().ToLowerInvariant();
        var chars = lowered.Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray();
        var normalized = new string(chars);
        while (normalized.Contains("  ", StringComparison.Ordinal)) normalized = normalized.Replace("  ", " ");
        return normalized.Trim();
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

    public static HidDeviceSnapshot ToSnapshot(AudioDevicePickerDialog.DeviceRow row) => new()
    {
        Vid = row.Vid,
        Pid = row.Pid,
        DevicePath = NullIfWhite(row.DevicePath),
        ProductName = NullIfWhite(row.ProductName),
        Manufacturer = NullIfWhite(row.Manufacturer),
        SerialNumber = NullIfWhite(row.SerialNumber),
        LastSelectedUtc = DateTime.UtcNow
    };

    private static string NullIfWhite(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public static AudioDevicePickerDialog.DeviceRow FindBestMatch(
        AudioDevicePickerDialog.DeviceRow[] current,
        HidDeviceSnapshot saved)
    {
        return current
            .Select(d => new { d, score = Score(d) })
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.d.Title)
            .FirstOrDefault()
            ?.d;

        int Score(AudioDevicePickerDialog.DeviceRow d)
        {
            var s = 0;

            if (d.Vid == saved.Vid && d.Pid == saved.Pid) s += 50;
            else return 0;

            if (!string.IsNullOrWhiteSpace(saved.SerialNumber) &&
                string.Equals(d.SerialNumber?.Trim(), saved.SerialNumber.Trim(), StringComparison.OrdinalIgnoreCase))
                s += 200;

            if (!string.IsNullOrWhiteSpace(saved.DevicePath) &&
                string.Equals(d.DevicePath?.Trim(), saved.DevicePath.Trim(), StringComparison.OrdinalIgnoreCase))
                s += 150;

            if (!string.IsNullOrWhiteSpace(saved.ProductName) &&
                string.Equals(Norm(d.ProductName), Norm(saved.ProductName), StringComparison.Ordinal))
                s += 20;

            if (!string.IsNullOrWhiteSpace(saved.Manufacturer) &&
                string.Equals(Norm(d.Manufacturer), Norm(saved.Manufacturer), StringComparison.Ordinal))
                s += 10;

            if (!string.IsNullOrWhiteSpace(d.DevicePath)) s += 2;

            return s;
        }

        static string Norm(string s)
        {
            var lowered = (s ?? "").Trim().ToLowerInvariant();
            var chars = lowered.Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray();
            var normalized = new string(chars);
            while (normalized.Contains("  ", StringComparison.Ordinal)) normalized = normalized.Replace("  ", " ");
            return normalized.Trim();
        }
    }
}
