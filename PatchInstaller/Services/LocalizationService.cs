using System.Globalization;
using System.Resources;

namespace PatchInstaller.Services;

public enum AppLanguage
{
    SimplifiedChinese,
    TraditionalChinese,
    English
}

internal static class LocalizationService
{
    private static readonly ResourceManager Resources = new("PatchInstaller.Lang.Resources", typeof(LocalizationService).Assembly);

    private static AppLanguage _currentLanguage = AppLanguage.SimplifiedChinese;
    private static CultureInfo _currentCulture = new("zh-CN");

    public static AppLanguage CurrentLanguage
    {
        get => _currentLanguage;
        set
        {
            _currentLanguage = value;
            var culture = GetCulture(value);
            _currentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }
    }

    public static string Get(string key)
    {
        return Resources.GetString(key, _currentCulture) ?? key;
    }

    private static CultureInfo GetCulture(AppLanguage language)
    {
        return language switch
        {
            AppLanguage.SimplifiedChinese => new CultureInfo("zh-CN"),
            AppLanguage.TraditionalChinese => new CultureInfo("zh-TW"),
            _ => CultureInfo.InvariantCulture
        };
    }
}
