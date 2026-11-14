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
using nut_vermaak_partituren.Configuration;

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

    private readonly Lazy<DriveService> _driveService;

    public GoogleDriveFileSystem(
        Lazy<DriveService> driveService
        )
    {
        ArgumentNullException.ThrowIfNull(driveService);

        _driveService = driveService;
    }

    public async Task<IReadOnlyList<FileSystemEntry>> ListAsync(
     string? parentFolderId = null,
     CancellationToken cancellationToken = default)
    {
        string resolvedParentId = ResolveParentFolderId(parentFolderId);

        FilesResource.ListRequest request =
            _driveService.Value.Files.List();

        request.Q =
            $"'{EscapeQueryValue(resolvedParentId)}' in parents and trashed = false";

        request.Fields = $"nextPageToken,files({FileFields})";
        request.PageSize = 1000;
        request.OrderBy = "name";
        request.Corpora = "user";

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

        FilesResource.GetRequest request = _driveService.Value.Files.Get(fileId);
        request.Fields = FileFields;

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
            _driveService.Value.Files.Create(metadata);
        request.Fields = FileFields;
        

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
            _driveService.Value.Files.Create(metadata, content, contentType);
        request.Fields = FileFields;
        

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

        

        DriveFile metadata = new();

        FilesResource.UpdateMediaUpload request =
            _driveService.Value.Files.Update(
                metadata,
                fileId,
                content,
                contentType);

        request.Fields = FileFields;
        

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

        

        FilesResource.GetRequest request = _driveService.Value.Files.Get(fileId);
        

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
            AppEnv.GoogleDriveRootFolderId,
            StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The configured Google Drive root folder cannot be deleted.");
        }

        

        FilesResource.DeleteRequest request =
            _driveService.Value.Files.Delete(fileId);
        

        await request.ExecuteAsync(cancellationToken);
    }

    private string ResolveParentFolderId(string? parentFolderId)
    {
        return string.IsNullOrWhiteSpace(parentFolderId)
            ? AppEnv.GoogleDriveRootFolderId
            : parentFolderId;
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
