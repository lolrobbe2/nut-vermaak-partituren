using DotNetEnv;
using System;
using System.IO;

namespace nut_vermaak_partituren.Configuration;
#nullable enable
public static class AppEnv
{
    public static void Load(bool isDevelopment)
    {
        if (!isDevelopment)
        {
            Env.Load();
            return;
        }

        DirectoryInfo? directory =
                    new DirectoryInfo(AppContext.BaseDirectory);

        while (directory != null)
        {
            string envPath = Path.Combine(directory.FullName, ".env");
            string srcEnvPath = Path.Combine(directory.FullName, "src", ".env");

            if (File.Exists(envPath))
            {
                Env.Load(envPath);
                return;
            }

            if (File.Exists(srcEnvPath))
            {
                Env.Load(srcEnvPath);
                return;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "Could not find .env in the application directory or its parent directories.");
    }

    public static string GoogleClientId =>
        GetRequired("GOOGLE_CLIENT_ID");

    public static string GoogleClientSecret =>
        GetRequired("GOOGLE_CLIENT_SECRET");

    public static string MicrosoftClientId =>
        GetRequired("MICROSOFT_CLIENT_ID");

    public static string MicrosoftClientSecret =>
        GetRequired("MICROSOFT_CLIENT_SECRET");
    public static string GoogleDriveRefreshToken =>
        GetRequired("GOOGLE_DRIVE_REFRESH_TOKEN");
    public static string GoogleDriveClientEmail =>
        GetRequired("GOOGLE_DRIVE_CLIENT_EMAIL");

    public static string GoogleDrivePrivateKey =>
        GetRequired("GOOGLE_DRIVE_PRIVATE_KEY")
            .Replace("\\n", "\n", StringComparison.Ordinal);

    public static string GoogleDriveRootFolderId =>
        GetRequired("GOOGLE_DRIVE_ROOT_FOLDER_ID");
    private static string GetRequired(string key)
    {
        string? value = Environment.GetEnvironmentVariable(key);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Environment variable '{key}' is missing.");
        }

        return value;
    }
}