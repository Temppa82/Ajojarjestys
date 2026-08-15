using System;
using System.IO;
using System.Text;

namespace AjoJarjestys;

public static class AppLogger
{
    private static readonly object Sync = new();

    private static string LogDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AjoJarjestys",
            "Logs");

    private static string CurrentLogPath =>
        Path.Combine(LogDirectory, $"ajojarjestys-{DateTime.Now:yyyy-MM-dd}.log");

    public static void Initialize()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            Info("AjoJärjestys käynnistetty.");
            Info($"Versio: {AppVersion.Version}");
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Error("Käsittelemätön sovelluspoikkeus.", e.ExceptionObject as Exception);
        }
        catch
        {
            // Lokitus ei saa koskaan kaataa varsinaista sovellusta.
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null)
    {
        var msg = ex == null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}";
        Write("ERROR", msg);
    }

    public static string GetLogDirectory() => LogDirectory;
    public static string GetLatestLog()
    {
        try
        {
            if (!Directory.Exists(LogDirectory)) return "";
            var latest = new DirectoryInfo(LogDirectory).GetFiles("*.log");
            Array.Sort(latest, (a,b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            return latest.Length == 0 ? "" : latest[0].FullName;
        }
        catch { return ""; }
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(
                    CurrentLogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {level} {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Never let logging interfere with the application.
        }
    }
}
