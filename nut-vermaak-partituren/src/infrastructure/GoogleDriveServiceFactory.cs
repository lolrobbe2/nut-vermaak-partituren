using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;

namespace nut_vermaak_partituren.Infrastructure.GoogleDrive;

public static class GoogleDriveServiceFactory
{
    public static DriveService Create(
        string clientEmail,
        string privateKey,
        string applicationName)
    {
        ServiceAccountCredential credential =
            new ServiceAccountCredential(
                new ServiceAccountCredential.Initializer(clientEmail)
                {
                    Scopes = new[]
                    {
                        DriveService.Scope.Drive
                    }
                }.FromPrivateKey(privateKey));

        return new DriveService(
            new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = applicationName
            });
    }
}