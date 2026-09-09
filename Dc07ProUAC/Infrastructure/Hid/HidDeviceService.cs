using HidSharp;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Dc07ProUAC.Infrastructure.Hid;

public static class HidDeviceService
{
    private static readonly char[] TokenSplitChars = [' '];
    private static readonly string[] NoiseProductNames = ["hid interface", "hid-compliant device", "usb input device", "composite device", "generic hid", "hid device"];
    private static readonly string[] PreferredBrandTokens = ["ibasso"];
    private static readonly string[] PreferredModelTokens = ["dc07pro", "dc07", "dc07 pro"];

    public static Task<HidDeviceInfo[]> ListAsync(string queryForRanking = "") => Task.Run(() =>
        DeviceList.Local.GetHidDevices()
            .Select(d => ToInfo(d, queryForRanking))
            .OrderByDescending(d => d.MatchScore)
            .ThenBy(d => d.Title)
            .ThenBy(d => d.Vid)
            .ThenBy(d => d.Pid)
            .ToArray());

    private static HidDeviceInfo ToInfo(HidDevice d, string query)
    {
        int vid = 0, pid = 0;
        string product = "", manufacturer = "", serial = "", path = "";
        try { vid = d.VendorID; } catch (Exception ex) { Debug.WriteLine(ex); }
        try { pid = d.ProductID; } catch (Exception ex) { Debug.WriteLine(ex); }
        try
        {
            var p1 = d.GetProductName() ?? "";
            var p2 = d.GetFriendlyName() ?? "";
            product = string.IsNullOrWhiteSpace(p2) ? p1 : $"{p1}: {p2}";
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
        try { manufacturer = d.GetManufacturer() ?? ""; } catch (Exception ex) { Debug.WriteLine(ex); }
        try { serial = d.GetSerialNumber() ?? ""; } catch (Exception ex) { Debug.WriteLine(ex); }
        try { path = d.DevicePath ?? ""; } catch (Exception ex) { Debug.WriteLine(ex); }

        var haystack = $"{product} {manufacturer} {serial} {vid:X4}:{pid:X4} {path}";
        return new HidDeviceInfo(path, vid, pid, product, manufacturer, serial, ComputeScore(query, product, manufacturer, haystack));
    }

    public static int ComputeScore(string query, string product, string manufacturer, string haystack)
    {
        var score = SimilarityScore(query, haystack);
        var productNorm = Normalize(product);
        var manufacturerNorm = Normalize(manufacturer);

        if (NoiseProductNames.Any(n => productNorm.Contains(n, StringComparison.Ordinal))) score -= 15;
        if (ContainsAny(manufacturerNorm, PreferredBrandTokens)) score += 35;
        if (ContainsAny(productNorm, PreferredModelTokens)) score += 25;
        if (productNorm.Contains("dc07", StringComparison.Ordinal)) score += 10;
        if (manufacturerNorm.Contains("ibasso", StringComparison.Ordinal)) score += 10;
        return Math.Clamp(score, 0, 100);
    }

    public static HidDeviceSnapshot ToSnapshot(HidDeviceInfo device) => new()
    {
        Vid = device.Vid,
        Pid = device.Pid,
        DevicePath = NullIfWhite(device.DevicePath),
        ProductName = NullIfWhite(device.ProductName),
        Manufacturer = NullIfWhite(device.Manufacturer),
        SerialNumber = NullIfWhite(device.SerialNumber),
        LastSelectedUtc = DateTime.UtcNow
    };

    public static HidDeviceInfo? FindBestMatch(HidDeviceInfo[] current, HidDeviceSnapshot saved) => current
        .Select(d => new { Device = d, Score = Score(d, saved) })
        .Where(x => x.Score > 0)
        .OrderByDescending(x => x.Score)
        .ThenBy(x => x.Device.Title)
        .FirstOrDefault()?.Device;

    private static int Score(HidDeviceInfo d, HidDeviceSnapshot saved)
    {
        if (d.Vid != saved.Vid || d.Pid != saved.Pid) return 0;
        var score = 50;
        if (!string.IsNullOrWhiteSpace(saved.SerialNumber) && string.Equals(d.SerialNumber.Trim(), saved.SerialNumber.Trim(), StringComparison.OrdinalIgnoreCase)) score += 200;
        if (!string.IsNullOrWhiteSpace(saved.DevicePath) && string.Equals(d.DevicePath.Trim(), saved.DevicePath.Trim(), StringComparison.OrdinalIgnoreCase)) score += 150;
        if (!string.IsNullOrWhiteSpace(saved.ProductName) && Normalize(d.ProductName) == Normalize(saved.ProductName)) score += 20;
        if (!string.IsNullOrWhiteSpace(saved.Manufacturer) && Normalize(d.Manufacturer) == Normalize(saved.Manufacturer)) score += 10;
        return score;
    }

    private static bool ContainsAny(string value, string[] tokens) => tokens.Any(t => value.Contains(Normalize(t), StringComparison.Ordinal));
    private static string? NullIfWhite(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int SimilarityScore(string query, string haystack)
    {
        if (string.IsNullOrWhiteSpace(query)) return 0;
        var q = Normalize(query);
        var h = Normalize(haystack);
        if (h.Contains(q, StringComparison.Ordinal)) return 100;
        var tokens = q.Split(TokenSplitChars, StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
        var hits = tokens.Count(t => t.Length >= 2 && h.Contains(t, StringComparison.Ordinal));
        return tokens.Length == 0 ? 0 : (int)Math.Round(70.0 * hits / tokens.Length);
    }

    private static string Normalize(string value)
    {
        var lowered = value.Trim().ToLowerInvariant();
        return new string([.. lowered.Where(c => char.IsLetterOrDigit(c) || c == ' ')]).Replace("  ", " ").Trim();
    }
}
