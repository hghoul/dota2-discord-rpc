using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dota2RPC.Core;

internal sealed class AppSettings
{
    /// <summary>Свой Application ID пользователя. Пусто — используется встроенный в программу.</summary>
    public string DiscordClientId { get; set; } = "";
    public int GsiPort { get; set; } = AppInfo.DefaultGsiPort;
    public string GsiToken { get; set; } = "";
    public string? DotaPath { get; set; }

    public bool StartWithWindows { get; set; } = true;
    public bool StartWithDota { get; set; }

    public bool ShowLevel { get; set; } = true;
    public bool ShowKda { get; set; } = true;
    public bool ShowGameTime { get; set; } = true;
    public bool ShowTeamAndScore { get; set; } = true;
    public bool ShowNetStats { get; set; }
    public bool ShowInMenu { get; set; } = true;
    public bool ShowProfileButton { get; set; }

    /// <summary>Анимированная иконка героя (если анимации опубликованы и вшиты в сборку).</summary>
    public bool AnimatedHeroIcons { get; set; } = true;

    /// <summary>Цветное оформление: эмодзи в строках статуса.</summary>
    public bool ShowEmoji { get; set; } = true;

    public bool TrayHintShown { get; set; }

    /// <summary>Проверять наличие новой версии на GitHub (раз в 12 часов) и напоминать о ней.</summary>
    public bool CheckUpdates { get; set; } = true;

    /// <summary>Версия, о которой пользователь попросил больше не напоминать.</summary>
    public string? SkippedUpdateVersion { get; set; }

    /// <summary>Показывать технические настройки (Application ID, порт GSI, диагностика).</summary>
    public bool DeveloperMode { get; set; }

    /// <summary>Сохранять образцы пакетов GSI на диск (переключатель — в режиме разработчика, для отладки).</summary>
    public bool RecordGsiSamples { get; set; }

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static bool IsValidClientId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length is >= 17 and <= 21 && id.All(char.IsAsciiDigit);

    /// <summary>ID, который реально используется: свой из настроек или встроенный.</summary>
    [JsonIgnore]
    public string EffectiveClientId => IsValidClientId(DiscordClientId) ? DiscordClientId : AppInfo.EmbeddedClientId;

    [JsonIgnore]
    public bool UsesEmbeddedClientId => !IsValidClientId(DiscordClientId) && AppInfo.EmbeddedClientId.Length > 0;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    public void Normalize()
    {
        DiscordClientId = (DiscordClientId ?? "").Trim();
        if (GsiPort is < 1024 or > 65535) GsiPort = AppInfo.DefaultGsiPort;
        if (string.IsNullOrWhiteSpace(GsiToken) || GsiToken.Length < 16 || !GsiToken.All(char.IsAsciiLetterOrDigit))
            GsiToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(DotaPath)) DotaPath = null;
    }

    public static AppSettings Load()
    {
        var path = AppPaths.SettingsFile;
        AppSettings? settings = null;
        try
        {
            if (File.Exists(path))
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path, Encoding.UTF8), JsonOptions);
        }
        catch (Exception ex)
        {
            Log.Error("Файл настроек повреждён, будут использованы значения по умолчанию", ex);
            try { File.Copy(path, path + ".bad", overwrite: true); } catch { /* не критично */ }
        }

        settings ??= new AppSettings();
        var tokenBefore = settings.GsiToken;
        settings.Normalize();
        if (!File.Exists(path) || tokenBefore != settings.GsiToken)
            settings.TrySave();
        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.SettingsDir);
        var path = AppPaths.SettingsFile;
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    public bool TrySave()
    {
        try
        {
            Save();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось сохранить настройки", ex);
            return false;
        }
    }
}
