using Google;
using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;
using Google.Apis.Download;
using Google.Apis.Upload;
using nut_vermaak_partituren.FileSystem;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using DriveFile = Google.Apis.Drive.v3.Data.File;

#nullable enable

namespace nut_vermaak_partituren.Infrastructure.GoogleDrive;

/// <summary>
/// Stores files in a configured folder inside a Google Shared Drive.
/// The service account must be a member of that Shared Drive.
/// </summary>
public sealed class GoogleDriveFileSystem : IFileSystem
{
    public const string FolderMimeType =
        "application/vnd.google-apps.folder";

    private const string FileFields =
        "id,name,mimeType,size,createdTime,modifiedTime,webViewLink";

    private const string RootFolderFields =
        "id,name,mimeType,driveId,trashed";

    private readonly DriveService _driveService;
    private readonly string _rootFolderId;
    private readonly SemaphoreSlim _sharedDriveLock = new(1, 1);

    private string? _sharedDriveId;

    public GoogleDriveFileSystem(
        DriveService driveService,
        string rootFolderId)
    {
        ArgumentNullException.ThrowIfNull(driveService);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootFolderId);

        _driveService = driveService;
        _rootFolderId = rootFolderId;
    }

    public async Task<IReadOnlyList<FileSystemEntry>> ListAsync(
        string? parentFolderId = null,
        CancellationToken cancellationToken = default)
    {
        string resolvedParentId = ResolveParentFolderId(parentFolderId);
        string sharedDriveId = await GetSharedDriveIdAsync(cancellationToken);

        FilesResource.ListRequest request = _driveService.Files.List();
        request.Q =
            $"'{EscapeQueryValue(resolvedParentId)}' in parents and trashed = false";
        request.Fields = $"nextPageToken,files({FileFields})";
        request.PageSize = 1000;
        request.OrderBy = "name";

        // Restrict listing to the Shared Drive containing the configured root folder.
        request.Corpora = "drive";
        request.DriveId = sharedDriveId;
        request.SupportsAllDrives = true;
        request.IncludeItemsFromAllDrives = true;

        List<FileSystemEntry> entries = new();

        do
        {
            FileList response =
                await request.ExecuteAsync(cancellationToken);

            if (response.Files != null)
            {
                foreach (DriveFile file in response.Files)
                {
                    entries.Add(Map(file));
                }
            }

            request.PageToken = response.NextPageToken;
        }
        while (!string.IsNullOrWhiteSpace(request.PageToken));

        return entries;
    }

    public async Task<FileSystemEntry?> GetAsync(
        string fileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

        FilesResource.GetRequest request = _driveService.Files.Get(fileId);
        request.Fields = FileFields;
        request.SupportsAllDrives = true;

        try
        {
            DriveFile file =
                await request.ExecuteAsync(cancellationToken);

            return Map(file);
        }
        catch (GoogleApiException exception)
            when (exception.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<FileSystemEntry> CreateFolderAsync(
        string name,
        string? parentFolderId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string resolvedParentId = ResolveParentFolderId(parentFolderId);
        await GetSharedDriveIdAsync(cancellationToken);

        DriveFile metadata = new()
        {
            Name = name,
            MimeType = FolderMimeType,
            Parents = new List<string>
            {
                resolvedParentId
            }
        };

        FilesResource.CreateRequest request =
            _driveService.Files.Create(metadata);
        request.Fields = FileFields;
        request.SupportsAllDrives = true;

        DriveFile created =
            await request.ExecuteAsync(cancellationToken);

        return Map(created);
    }

    public async Task<FileSystemEntry> UploadAsync(
        string name,
        string contentType,
        Stream content,
        string? parentFolderId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentNullException.ThrowIfNull(content);

        if (!content.CanRead)
        {
            throw new ArgumentException(
                "The content stream must be readable.",
                nameof(content));
        }

        string resolvedParentId = ResolveParentFolderId(parentFolderId);
        await GetSharedDriveIdAsync(cancellationToken);

        DriveFile metadata = new()
        {
            Name = name,
            MimeType = contentType,
            Parents = new List<string>
            {
                resolvedParentId
            }
        };

        FilesResource.CreateMediaUpload request =
            _driveService.Files.Create(metadata, content, contentType);
        request.Fields = FileFields;
        request.SupportsAllDrives = true;

        IUploadProgress progress =
            await request.UploadAsync(cancellationToken);

        EnsureUploadCompleted(progress);

        if (request.ResponseBody == null)
        {
            throw new IOException(
                "Google Drive did not return the uploaded file.");
        }

        return Map(request.ResponseBody);
    }

    public async Task<FileSystemEntry> UpdateAsync(
        string fileId,
        string contentType,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentNullException.ThrowIfNull(content);

        if (!content.CanRead)
        {
            throw new ArgumentException(
                "The content stream must be readable.",
                nameof(content));
        }

        await GetSharedDriveIdAsync(cancellationToken);

        DriveFile metadata = new();

        FilesResource.UpdateMediaUpload request =
            _driveService.Files.Update(
                metadata,
                fileId,
                content,
                contentType);

        request.Fields = FileFields;
        request.SupportsAllDrives = true;

        IUploadProgress progress =
            await request.UploadAsync(cancellationToken);

        EnsureUploadCompleted(progress);

        if (request.ResponseBody == null)
        {
            throw new IOException(
                "Google Drive did not return the updated file.");
        }

        return Map(request.ResponseBody);
    }

    public async Task DownloadAsync(
        string fileId,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        ArgumentNullException.ThrowIfNull(destination);

        if (!destination.CanWrite)
        {
            throw new ArgumentException(
                "The destination stream must be writable.",
                nameof(destination));
        }

        await GetSharedDriveIdAsync(cancellationToken);

        FilesResource.GetRequest request = _driveService.Files.Get(fileId);
        request.SupportsAllDrives = true;

        IDownloadProgress progress =
            await request.DownloadAsync(destination, cancellationToken);

        if (progress.Status != DownloadStatus.Completed)
        {
            throw progress.Exception ??
                new IOException("Google Drive download failed.");
        }
    }

    public async Task DeleteAsync(
        string fileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

        if (string.Equals(
            fileId,
            _rootFolderId,
            StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The configured Google Drive root folder cannot be deleted.");
        }

        await GetSharedDriveIdAsync(cancellationToken);

        FilesResource.DeleteRequest request =
            _driveService.Files.Delete(fileId);
        request.SupportsAllDrives = true;

        await request.ExecuteAsync(cancellationToken);
    }

    private string ResolveParentFolderId(string? parentFolderId)
    {
        return string.IsNullOrWhiteSpace(parentFolderId)
            ? _rootFolderId
            : parentFolderId;
    }

    /// <summary>
    /// Loads and validates the Shared Drive containing the configured root folder.
    /// The Drive ID is cached for subsequent listing operations.
    /// </summary>
    private async Task<string> GetSharedDriveIdAsync(
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_sharedDriveId))
        {
            return _sharedDriveId;
        }

        await _sharedDriveLock.WaitAsync(cancellationToken);

        try
        {
            if (!string.IsNullOrWhiteSpace(_sharedDriveId))
            {
                return _sharedDriveId;
            }

            FilesResource.GetRequest request =
                _driveService.Files.Get(_rootFolderId);
            request.Fields = RootFolderFields;
            request.SupportsAllDrives = true;

            DriveFile rootFolder =
                await request.ExecuteAsync(cancellationToken);

            if (rootFolder.Trashed == true)
            {
                throw new InvalidOperationException(
                    "The configured Google Drive root folder is in the trash.");
            }

            if (!string.Equals(
                rootFolder.MimeType,
                FolderMimeType,
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "GOOGLE_DRIVE_ROOT_FOLDER_ID must identify a folder.");
            }

            if (string.IsNullOrWhiteSpace(rootFolder.DriveId))
            {
                throw new InvalidOperationException(
                    "The configured folder is not inside a Google Shared Drive. " +
                    "A service account storage root must be in a Shared Drive.");
            }

            _sharedDriveId = rootFolder.DriveId;
            return _sharedDriveId;
        }
        finally
        {
            _sharedDriveLock.Release();
        }
    }

    private static void EnsureUploadCompleted(IUploadProgress progress)
    {
        if (progress.Status != UploadStatus.Completed)
        {
            throw progress.Exception ??
                new IOException("Google Drive upload failed.");
        }
    }

    private static FileSystemEntry Map(DriveFile file)
    {
        return new FileSystemEntry(
            file.Id,
            file.Name,
            file.MimeType,
            file.Size,
            file.CreatedTimeDateTimeOffset,
            file.ModifiedTimeDateTimeOffset,
            file.WebViewLink);
    }
    private static string EscapeQueryValue(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);
    }
}
