using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Threading;
using System.Threading.Tasks;
#nullable enable
namespace nut_vermaak_partituren.FileSystem;

public sealed record FileSystemEntry(
    string Id,
    string Name,
    string MimeType,
    long? Size,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? ModifiedAt,
    string? WebViewLink)
{
    public bool IsFolder =>
        MimeType == "application/vnd.google-apps.folder";
}
public interface IFileSystem
{
    Task<IReadOnlyList<FileSystemEntry>> ListAsync(
        string? parentFolderId = null,
        CancellationToken cancellationToken = default);

    Task<FileSystemEntry?> GetAsync(
        string fileId,
        CancellationToken cancellationToken = default);

    Task<FileSystemEntry> CreateFolderAsync(
        string name,
        string? parentFolderId = null,
        CancellationToken cancellationToken = default);

    Task<FileSystemEntry> UploadAsync(
        string name,
        string contentType,
        Stream content,
        string? parentFolderId = null,
        CancellationToken cancellationToken = default);

    Task<FileSystemEntry> UpdateAsync(
        string fileId,
        string contentType,
        Stream content,
        CancellationToken cancellationToken = default);

    Task DownloadAsync(
        string fileId,
        Stream destination,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string fileId,
        CancellationToken cancellationToken = default);
}