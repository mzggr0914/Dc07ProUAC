using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Dc07ProUAC;

public sealed class JsonSettingsStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public JsonSettingsStore(string appFolderName = "Dc07ProUAC")
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(baseDir, appFolderName);
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
    }

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!File.Exists(_path)) return new AppSettings();

            try
            {
                await using var fs = File.OpenRead(_path);
                var settings = await JsonSerializer.DeserializeAsync<AppSettings>(fs, JsonOpts, ct);
                return settings ?? new AppSettings();
            }
            catch
            {
                TryBackupCorrupt();
                return new AppSettings();
            }
        }
        finally { _lock.Release(); }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var tmp = _path + ".tmp";
            await using (var fs = File.Create(tmp))
            {
                await JsonSerializer.SerializeAsync(fs, settings, JsonOpts, ct);
                await fs.FlushAsync(ct);
            }

            if (File.Exists(_path))
            {
                try { File.Replace(tmp, _path, _path + ".bak", ignoreMetadataErrors: true); }
                catch
                {
                    File.Delete(_path);
                    File.Move(tmp, _path);
                }
            }
            else
            {
                File.Move(tmp, _path);
            }
        }
        finally { _lock.Release(); }
    }

    private void TryBackupCorrupt()
    {
        try
        {
            var bak = _path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".bak";
            File.Copy(_path, bak, overwrite: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }
}
