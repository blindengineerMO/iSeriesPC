namespace Ipc.Services.Configuration;

public sealed class SystemConfig
{
    public string DataDirectory { get; set; } = "/var/lib/ipcsys";

    public string SystemName { get; set; } = "MYSYS";

    public int Ccsid { get; set; } = 37;

    public int WebPort { get; set; } = 8443;

    public int ObjectConnectPort { get; set; } = 3390;

    public string SshSwapShellPath { get; set; } = "/usr/local/bin/as400menu";

    public string LibrarySuffix { get; set; } = "";

    public string HostIdentity { get; set; } = "iSeriesPC";

    public string Version { get; set; } = "0.1.0-dev";

    public static SystemConfig Default() => new();
}

public sealed class ConfigStore
{
    private readonly string _path;

    public ConfigStore(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, "system.json");
    }

    public SystemConfig Load()
    {
        if (File.Exists(_path))
        {
            return System.Text.Json.JsonSerializer.Deserialize<SystemConfig>(File.ReadAllText(_path))
                ?? SystemConfig.Default();
        }

        return SystemConfig.Default();
    }

    public void Save(SystemConfig config)
    {
        var file = new FileInfo(_path);
        file.Directory?.Create();
        var json = System.Text.Json.JsonSerializer.Serialize(
            config,
            new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
            });
        File.WriteAllText(_path, json);
    }
}