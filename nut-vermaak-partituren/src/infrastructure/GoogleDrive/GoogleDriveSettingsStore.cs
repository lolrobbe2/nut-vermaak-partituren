
using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;
using Google.Apis.Download;
using Google.Apis.Upload;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace nut_vermaak_partituren.Infrastructure.GoogleDrive;

public sealed class GoogleDriveSettingsStore
{
    private const string SettingsFileName =
        "nut-vermaak-partituren-storage.json";

    private const string SettingsMimeType = "application/json";

    private readonly Lazy<DriveService> _driveService;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public GoogleDriveSettingsStore(
        Lazy<DriveService> driveService)
    {
        ArgumentNullException.ThrowIfNull(driveService);

        _driveService = driveService;
    }

    public async Task<string?> GetRootFolderIdAsync(
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);

        try
        {
            DriveService drive = _driveService.Value;

            DriveFile? settingsFile =
                await FindSettingsFileAsync(drive, cancellationToken);

            if (settingsFile == null)
            {
                return null;
            }

            using MemoryStream content = new();

            FilesResource.GetRequest request =
                drive.Files.Get(settingsFile.Id);

            IDownloadProgress progress =
                await request.DownloadAsync(content, cancellationToken);

            EnsureDownloadCompleted(progress);

            content.Position = 0;

            StorageSettings? settings =
                await JsonSerializer.DeserializeAsync<StorageSettings>(
                    content,
                    JsonOptions,
                    cancellationToken);

            return settings?.RootFolderId;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetRootFolderIdAsync(
        string folderId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderId);

        await _lock.WaitAsync(cancellationToken);

        try
        {
            DriveService drive = _driveService.Value;

            // Verify that the selected item is an existing folder.
            FilesResource.GetRequest folderRequest =
                drive.Files.Get(folderId);

            folderRequest.Fields =
                "id,name,mimeType,trashed";

            DriveFile folder =
                await folderRequest.ExecuteAsync(cancellationToken);

            if (folder.Trashed == true)
            {
                throw new InvalidOperationException(
                    "The selected Google Drive folder is in the trash.");
            }

            if (!string.Equals(
                folder.MimeType,
                GoogleDriveFileSystem.FolderMimeType,
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The selected Google Drive item is not a folder.");
            }

            StorageSettings settings = new()
            {
                RootFolderId = folder.Id,
                RootFolderName = folder.Name,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };

            DriveFile? existingSettingsFile =
                await FindSettingsFileAsync(drive, cancellationToken);

            await SaveSettingsAsync(
                drive,
                existingSettingsFile,
                settings,
                cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task<DriveFile?> FindSettingsFileAsync(
        DriveService drive,
        CancellationToken cancellationToken)
    {
        FilesResource.ListRequest request = drive.Files.List();

        request.Spaces = "appDataFolder";
        request.Q =
            $"name = '{SettingsFileName}' and trashed = false";

        request.Fields = "files(id,name)";
        request.PageSize = 10;

        FileList result =
            await request.ExecuteAsync(cancellationToken);

        if (result.Files != null)
        {
            foreach (DriveFile file in result.Files)
            {
                return file;
            }
        }

        return null;
    }

    private static async Task SaveSettingsAsync(
        DriveService drive,
        DriveFile? existingFile,
        StorageSettings settings,
        CancellationToken cancellationToken)
    {
        using MemoryStream content = new();

        await JsonSerializer.SerializeAsync(
            content,
            settings,
            JsonOptions,
            cancellationToken);

        content.Position = 0;

        IUploadProgress progress;

        if (existingFile == null)
        {
            DriveFile metadata = new()
            {
                Name = SettingsFileName,
                MimeType = SettingsMimeType,
                Parents = new List<string>
                {
                    "appDataFolder"
                }
            };

            FilesResource.CreateMediaUpload request =
                drive.Files.Create(
                    metadata,
                    content,
                    SettingsMimeType);

            request.Fields = "id,name";

            progress = await request.UploadAsync(cancellationToken);

            EnsureUploadCompleted(progress);

            if (request.ResponseBody == null)
            {
                throw new IOException(
                    "Google Drive did not return the settings file.");
            }
        }
        else
        {
            DriveFile metadata = new();

            FilesResource.UpdateMediaUpload request =
                drive.Files.Update(
                    metadata,
                    existingFile.Id,
                    content,
                    SettingsMimeType);

            request.Fields = "id,name";

            progress = await request.UploadAsync(cancellationToken);

            EnsureUploadCompleted(progress);
        }
    }

    private static void EnsureDownloadCompleted(
        IDownloadProgress progress)
    {
        if (progress.Status != DownloadStatus.Completed)
        {
            throw progress.Exception ??
                new IOException(
                    "Could not download Google Drive settings.");
        }
    }

    private static void EnsureUploadCompleted(
        IUploadProgress progress)
    {
        if (progress.Status != UploadStatus.Completed)
        {
            throw progress.Exception ??
                new IOException(
                    "Could not save Google Drive settings.");
        }
    }

    private sealed class StorageSettings
    {
        public string RootFolderId { get; set; } = string.Empty;

        public string RootFolderName { get; set; } = string.Empty;

        public DateTimeOffset UpdatedAtUtc { get; set; }
    }
}
