using System;
using System.Collections.Generic;

namespace AjoJarjestys;

public enum UiLanguage
{
    Finnish,
    English
}

public static class UiLanguageManager
{
    private static UiLanguage _current = UiLanguage.Finnish;
    public static UiLanguage Current => _current;

    public static void Set(UiLanguage language) => _current = language;

    private static readonly Dictionary<string, (string Fi, string En)> Texts = new()
    {
        ["AcceptAll"] = ("Hyväksy kaikki", "Accept all"),
        ["NotRecognized"] = ("Ei tunnistettu", "Not recognized"),
        ["Settings"] = ("Asetukset", "Settings"),
        ["Language"] = ("Kieli", "Language"),
        ["Finnish"] = ("Suomi", "Finnish"),
        ["English"] = ("Englanti", "English"),
        ["Diagnostics"] = ("Vianmääritys", "Diagnostics"),
        ["OpenLogFolder"] = ("Avaa lokikansio", "Open log folder"),
        ["OpenLatestLog"] = ("Avaa viimeisin loki", "Open latest log"),
        ["Version"] = ("Versio", "Version"),
        ["CreatedBy"] = ("Tekijä", "Created by")
    };

    public static string T(string key)
    {
        if (!Texts.TryGetValue(key, out var value)) return key;
        return _current == UiLanguage.English ? value.En : value.Fi;
    }
}
