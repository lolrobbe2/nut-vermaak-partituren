
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using System;

namespace nut_vermaak_partituren.Infrastructure.GoogleDrive;

public static class GoogleDriveServiceFactory
{
    public static DriveService Create(
        string clientId,
        string clientSecret,
        string refreshToken,
        string ownerEmail,
        string applicationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerEmail);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);

        GoogleAuthorizationCodeFlow flow =
            new(new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = new ClientSecrets
                {
                    ClientId = clientId,
                    ClientSecret = clientSecret
                },

                Scopes = new[]
                {
                    DriveService.Scope.DriveFile,
                    "https://www.googleapis.com/auth/drive.appdata"
                },

                DataStore = new NullDataStore()
            });

        UserCredential credential = new(
            flow,
            ownerEmail,
            new TokenResponse
            {
                RefreshToken = refreshToken
            });

        return new DriveService(
            new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = applicationName
            });
    }
}
