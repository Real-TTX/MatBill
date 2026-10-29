using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace MatBill.Configuration;

/// <summary>
/// Reads and writes <c>{data}/config/app.json</c>. Environment variables <c>MATBILL__Section__Key</c> win for the sections
/// Database and Server (how this machine is deployed); they are never written back to the file.
/// </summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly object _gate = new();
    private readonly string _path;
    private AppConfig _config;

    public ConfigStore(string dataDir)
    {
        DataDir = dataDir;
        _path = Path.Combine(dataDir, "config", "app.json");
        _config = Load();
    }

    /// <summary>The data volume.</summary>
    public string DataDir { get; }

    public AppConfig Current
    {
        get
        {
            lock (_gate)
            {
                return _config;
            }
        }
    }

    /// <summary>Raised after the configuration was changed through <see cref="Update"/>.</summary>
    public event Action? Changed;

    /// <summary>Changes the configuration and writes the file: a temporary file first, then it replaces the old one.</summary>
    public void Update(Action<AppConfig> change)
    {
        lock (_gate)
        {
            change(_config);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Persistable(_config), Json), new UTF8Encoding(false));
            File.Move(temp, _path, overwrite: true);
        }

        Changed?.Invoke();
    }

    /// <summary>The connection string of the application database, from the Database section (environment variables included).</summary>
    public string ConnectionString(string? databaseOverride = null)
    {
        var db = Current.Database;
        return new NpgsqlConnectionStringBuilder
        {
            Host = db.Host,
            Port = db.Port,
            Database = databaseOverride ?? db.Database,
            Username = db.Username,
            Password = db.Password,
            Pooling = true,
            MaxPoolSize = 40,
            // No Kerberos: the image has no GSS library, and asking for it only writes noise into the log.
            GssEncryptionMode = GssEncryptionMode.Disable,
        }.ConnectionString;
    }

    private AppConfig Load()
    {
        var config = new AppConfig();
        if (File.Exists(_path))
        {
            try
            {
                config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_path), Json) ?? new AppConfig();
            }
            catch (JsonException)
            {
                // A damaged file must not keep the application from starting: keep it for inspection and start with the defaults.
                File.Copy(_path, _path + ".damaged", overwrite: true);
            }
        }

        ApplyEnvironment(config);
        return config;
    }

    /// <summary>The file without what comes from the environment: those values belong to the deployment, not to the file.</summary>
    private AppConfig Persistable(AppConfig live)
    {
        var copy = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(live, Json), Json)!;
        var fromFile = new AppConfig();
        if (File.Exists(_path))
        {
            try
            {
                fromFile = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_path), Json) ?? fromFile;
            }
            catch (JsonException)
            {
                // The new file replaces the damaged one.
            }
        }

        copy.Database = fromFile.Database;
        copy.Server = fromFile.Server;
        return copy;
    }

    private static void ApplyEnvironment(AppConfig config)
    {
        static string? Env(string name) => Environment.GetEnvironmentVariable("MATBILL__" + name);

        if (Env("Database__Host") is { Length: > 0 } host) { config.Database.Host = host; }
        if (int.TryParse(Env("Database__Port"), out var port)) { config.Database.Port = port; }
        if (Env("Database__Database") is { Length: > 0 } database) { config.Database.Database = database; }
        if (Env("Database__Username") is { Length: > 0 } user) { config.Database.Username = user; }
        if (Env("Database__Password") is { Length: > 0 } password) { config.Database.Password = password; }
        if (Env("Server__PublicUrl") is { Length: > 0 } url) { config.Server.PublicUrl = url.TrimEnd('/'); }
        if (Env("Server__TimeZone") is { Length: > 0 } zone) { config.Server.TimeZone = zone; }
        if (bool.TryParse(Env("Server__ForceSecureCookie"), out var secure)) { config.Server.ForceSecureCookie = secure; }
        if (Env("Server__TrustedProxies") is { Length: > 0 } proxies) { config.Server.TrustedProxies = proxies; }
    }
}

/// <summary>Protects secrets that are kept in the configuration or in the database (the SMTP password, the NAS password).</summary>
public sealed class SecretProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("MatBill.Secrets.v1");

    public string Protect(string plain) => string.IsNullOrEmpty(plain) ? "" : _protector.Protect(plain);

    /// <summary>Returns an empty string for an empty or damaged value (for example after the key ring was lost).</summary>
    public string Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue))
        {
            return "";
        }

        try
        {
            return _protector.Unprotect(protectedValue);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return "";
        }
    }
}
