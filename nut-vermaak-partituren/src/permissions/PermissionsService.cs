
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using nut_vermaak_partituren.FileSystem;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mail;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace nut_vermaak_partituren.Permissions;

public sealed class PermissionsService : IHostedService
{
    private const string UsersFileName = "users.csv";
    private const string CsvContentType = "text/csv";

    private static readonly TimeSpan CacheDuration =
        TimeSpan.FromSeconds(60);

    private static readonly FilePermission[] IndividualPermissions =
    {
        FilePermission.List,
        FilePermission.Read,
        FilePermission.Upload,
        FilePermission.Update,
        FilePermission.Delete,
        FilePermission.CreateFolder,
        FilePermission.ManageUsers
    };

    private readonly IFileSystem _fileSystem;
    private readonly ILogger<PermissionsService> _logger;
    private readonly string _rootAddress;

    private readonly SemaphoreSlim _lock = new(1, 1);

    private Dictionary<string, FilePermission> _users =
        new(StringComparer.OrdinalIgnoreCase);

    private string? _usersFileId;
    private DateTimeOffset _lastLoadedAt;
    private bool _loaded;

    public PermissionsService(
        IFileSystem fileSystem,
        string rootAddress,
        ILogger<PermissionsService> logger)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootAddress);

        _fileSystem = fileSystem;
        _logger = logger;
        _rootAddress = NormalizeEmail(rootAddress);
    }

    // Automatically initialize users.csv when the application starts.
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await ReloadAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public bool IsRoot(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        return string.Equals(
            NormalizeEmail(email),
            _rootAddress,
            StringComparison.OrdinalIgnoreCase);
    }

    // --------------------------------------------------
    // Permission checks
    // --------------------------------------------------

    public async Task<FilePermission> GetPermissionsAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        string? email = GetUserEmail(user);

        return await GetPermissionsAsync(
            email,
            cancellationToken);
    }

    public async Task<FilePermission> GetPermissionsAsync(
        string? email,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return FilePermission.None;
        }

        if (IsRoot(email))
        {
            return FilePermission.All;
        }

        string normalizedEmail = NormalizeEmail(email);

        await _lock.WaitAsync(cancellationToken);

        try
        {
            await EnsureLoadedUnsafeAsync(cancellationToken);

            return _users.TryGetValue(
                normalizedEmail,
                out FilePermission permissions)
                    ? permissions
                    : FilePermission.None;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> HasPermissionAsync(
        ClaimsPrincipal user,
        FilePermission requiredPermission,
        CancellationToken cancellationToken = default)
    {
        if (requiredPermission == FilePermission.None)
        {
            return true;
        }

        FilePermission permissions =
            await GetPermissionsAsync(user, cancellationToken);

        return (permissions & requiredPermission) ==
            requiredPermission;
    }

    // --------------------------------------------------
    // User management
    // --------------------------------------------------

    public async Task<IReadOnlyList<UserPermissionEntry>> GetUsersAsync(
        ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);

        try
        {
            await EnsureLoadedUnsafeAsync(cancellationToken);
            EnsureCanManageUsers(actor);

            return _users
                .OrderBy(entry => entry.Key)
                .Select(entry => new UserPermissionEntry(
                    entry.Key,
                    entry.Value))
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task AddUserAsync(
        ClaimsPrincipal actor,
        string email,
        FilePermission permissions,
        CancellationToken cancellationToken = default)
    {
        string normalizedEmail = NormalizeEmail(email);

        await _lock.WaitAsync(cancellationToken);

        try
        {
            await EnsureLoadedUnsafeAsync(cancellationToken);
            EnsureCanManageUsers(actor);
            EnsureCanAssignPermissions(actor, permissions);

            if (IsRoot(normalizedEmail))
            {
                throw new InvalidOperationException(
                    "The root administrator does not need a CSV entry.");
            }

            if (_users.ContainsKey(normalizedEmail))
            {
                throw new InvalidOperationException(
                    $"User '{normalizedEmail}' already exists.");
            }

            Dictionary<string, FilePermission> updated =
                new(_users, StringComparer.OrdinalIgnoreCase);

            updated.Add(normalizedEmail, permissions);

            await SaveUsersUnsafeAsync(updated, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetPermissionsAsync(
        ClaimsPrincipal actor,
        string email,
        FilePermission permissions,
        CancellationToken cancellationToken = default)
    {
        string normalizedEmail = NormalizeEmail(email);

        await _lock.WaitAsync(cancellationToken);

        try
        {
            await EnsureLoadedUnsafeAsync(cancellationToken);
            EnsureCanManageUsers(actor);
            EnsureCanAssignPermissions(actor, permissions);

            if (IsRoot(normalizedEmail))
            {
                throw new InvalidOperationException(
                    "Root administrator permissions cannot be modified.");
            }

            if (!_users.ContainsKey(normalizedEmail))
            {
                throw new KeyNotFoundException(
                    $"User '{normalizedEmail}' does not exist.");
            }

            Dictionary<string, FilePermission> updated =
                new(_users, StringComparer.OrdinalIgnoreCase);

            updated[normalizedEmail] = permissions;

            await SaveUsersUnsafeAsync(updated, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> RemoveUserAsync(
        ClaimsPrincipal actor,
        string email,
        CancellationToken cancellationToken = default)
    {
        string normalizedEmail = NormalizeEmail(email);

        await _lock.WaitAsync(cancellationToken);

        try
        {
            await EnsureLoadedUnsafeAsync(cancellationToken);
            EnsureCanManageUsers(actor);

            if (IsRoot(normalizedEmail))
            {
                throw new InvalidOperationException(
                    "The root administrator cannot be removed.");
            }

            if (!_users.ContainsKey(normalizedEmail))
            {
                return false;
            }

            Dictionary<string, FilePermission> updated =
                new(_users, StringComparer.OrdinalIgnoreCase);

            updated.Remove(normalizedEmail);

            await SaveUsersUnsafeAsync(updated, cancellationToken);

            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    // --------------------------------------------------
    // CSV loading and initialization
    // --------------------------------------------------

    public async Task ReloadAsync(
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);

        try
        {
            _loaded = false;
            await EnsureLoadedUnsafeAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task EnsureLoadedUnsafeAsync(
        CancellationToken cancellationToken)
    {
        if (_loaded &&
            DateTimeOffset.UtcNow - _lastLoadedAt < CacheDuration)
        {
            return;
        }

        IReadOnlyList<FileSystemEntry> files =
            await _fileSystem.ListAsync(
                cancellationToken: cancellationToken);

        List<FileSystemEntry> matches = files
            .Where(file =>
                !file.IsFolder &&
                string.Equals(
                    file.Name,
                    UsersFileName,
                    StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count > 1)
        {
            throw new InvalidDataException(
                "Multiple users.csv files exist in the root folder.");
        }

        if (matches.Count == 0)
        {
            _logger.LogInformation(
                "users.csv does not exist. Creating it.");

            _users = new Dictionary<string, FilePermission>(
                StringComparer.OrdinalIgnoreCase);

            _usersFileId = null;

            await SaveUsersUnsafeAsync(
                _users,
                cancellationToken);

            return;
        }

        FileSystemEntry usersFile = matches[0];

        using MemoryStream stream = new();

        await _fileSystem.DownloadAsync(
            usersFile.Id,
            stream,
            cancellationToken);

        stream.Position = 0;

        using StreamReader reader = new(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);

        string? header =
            await reader.ReadLineAsync(cancellationToken);

        if (header == null)
        {
            throw new InvalidDataException("users.csv is empty.");
        }

        string[] headers = ParseCsvLine(header);

        int emailIndex = Array.FindIndex(
            headers,
            value => value.Equals(
                "email",
                StringComparison.OrdinalIgnoreCase));

        int permissionsIndex = Array.FindIndex(
            headers,
            value => value.Equals(
                "permissions",
                StringComparison.OrdinalIgnoreCase));

        if (emailIndex < 0 || permissionsIndex < 0)
        {
            throw new InvalidDataException(
                "users.csv requires email and permissions columns.");
        }

        Dictionary<string, FilePermission> loadedUsers =
            new(StringComparer.OrdinalIgnoreCase);

        string? line;
        int lineNumber = 1;

        while ((line = await reader.ReadLineAsync(
            cancellationToken)) != null)
        {
            lineNumber++;

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string[] fields = ParseCsvLine(line);

            if (fields.Length <= Math.Max(emailIndex, permissionsIndex))
            {
                throw new InvalidDataException(
                    $"Invalid users.csv line {lineNumber}.");
            }

            string email = NormalizeEmail(fields[emailIndex]);

            if (!loadedUsers.TryAdd(
                email,
                ParsePermissions(fields[permissionsIndex])))
            {
                throw new InvalidDataException(
                    $"Duplicate email '{email}' on line {lineNumber}.");
            }
        }

        _users = loadedUsers;
        _usersFileId = usersFile.Id;
        _lastLoadedAt = DateTimeOffset.UtcNow;
        _loaded = true;

        _logger.LogInformation(
            "Loaded permissions for {UserCount} users.",
            _users.Count);
    }

    // --------------------------------------------------
    // CSV saving
    // --------------------------------------------------

    private async Task SaveUsersUnsafeAsync(
        Dictionary<string, FilePermission> users,
        CancellationToken cancellationToken)
    {
        StringBuilder csv = new();

        csv.AppendLine("email,permissions");

        foreach (KeyValuePair<string, FilePermission> user in
                 users.OrderBy(entry => entry.Key))
        {
            csv.Append(EscapeCsv(user.Key));
            csv.Append(',');
            csv.AppendLine(
                EscapeCsv(SerializePermissions(user.Value)));
        }

        byte[] bytes =
            new UTF8Encoding(false).GetBytes(csv.ToString());

        using MemoryStream stream = new(bytes);

        FileSystemEntry savedFile;

        if (string.IsNullOrWhiteSpace(_usersFileId))
        {
            savedFile = await _fileSystem.UploadAsync(
                UsersFileName,
                CsvContentType,
                stream,
                cancellationToken: cancellationToken);
        }
        else
        {
            savedFile = await _fileSystem.UpdateAsync(
                _usersFileId,
                CsvContentType,
                stream,
                cancellationToken);
        }

        _usersFileId = savedFile.Id;

        _users = new Dictionary<string, FilePermission>(
            users,
            StringComparer.OrdinalIgnoreCase);

        _lastLoadedAt = DateTimeOffset.UtcNow;
        _loaded = true;

        _logger.LogInformation(
            "Saved permissions for {UserCount} users.",
            _users.Count);
    }

    // --------------------------------------------------
    // Authorization for permission management
    // --------------------------------------------------

    private void EnsureCanManageUsers(ClaimsPrincipal actor)
    {
        string? email = GetUserEmail(actor);

        if (email == null)
        {
            throw new UnauthorizedAccessException(
                "An authenticated email address is required.");
        }

        if (IsRoot(email))
        {
            return;
        }

        if (!_users.TryGetValue(
            email,
            out FilePermission permissions) ||
            (permissions & FilePermission.ManageUsers) !=
                FilePermission.ManageUsers)
        {
            throw new UnauthorizedAccessException(
                "You do not have permission to manage users.");
        }
    }

    private void EnsureCanAssignPermissions(
        ClaimsPrincipal actor,
        FilePermission permissions)
    {
        string? email = GetUserEmail(actor);

        if (email != null && IsRoot(email))
        {
            return;
        }

        FilePermission restrictedPermissions =
            FilePermission.ManageUsers | FilePermission.All;

        if ((permissions & restrictedPermissions) !=
            FilePermission.None)
        {
            throw new UnauthorizedAccessException(
                "Only the root administrator can grant all permissions " +
                "or user-management permissions.");
        }
    }

    // --------------------------------------------------
    // CSV helpers
    // --------------------------------------------------

    private static FilePermission ParsePermissions(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return FilePermission.None;
        }

        string[] names = value.Split(
            '|',
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);

        if (names.Length == 0)
        {
            return FilePermission.None;
        }

        if (names.Any(name =>
            name.Equals("None", StringComparison.OrdinalIgnoreCase)))
        {
            if (names.Length != 1)
            {
                throw new InvalidDataException(
                    "None cannot be combined with other permissions.");
            }

            return FilePermission.None;
        }

        FilePermission result = FilePermission.None;

        foreach (string name in names)
        {
            if (!Enum.TryParse(
                name,
                true,
                out FilePermission permission) ||
                !Enum.IsDefined(typeof(FilePermission), permission) ||
                permission == FilePermission.None)
            {
                throw new InvalidDataException(
                    $"Unknown permission '{name}'.");
            }

            result |= permission;
        }

        return result;
    }

    private static string SerializePermissions(
        FilePermission permissions)
    {
        if (permissions == FilePermission.None)
        {
            return "None";
        }

        return string.Join(
            "|",
            IndividualPermissions
                .Where(permission =>
                    (permissions & permission) == permission));
    }

    private static string EscapeCsv(string value)
    {
        if (!value.Contains(',') &&
            !value.Contains('"') &&
            !value.Contains('\n') &&
            !value.Contains('\r'))
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string[] ParseCsvLine(string line)
    {
        List<string> fields = new();
        StringBuilder current = new();
        bool insideQuotes = false;

        for (int index = 0; index < line.Length; index++)
        {
            char character = line[index];

            if (character == '"')
            {
                if (insideQuotes &&
                    index + 1 < line.Length &&
                    line[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                }
                else
                {
                    insideQuotes = !insideQuotes;
                }
            }
            else if (character == ',' && !insideQuotes)
            {
                fields.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }

        if (insideQuotes)
        {
            throw new InvalidDataException(
                "Invalid CSV: unclosed quoted field.");
        }

        fields.Add(current.ToString().Trim());

        return fields.ToArray();
    }

    // --------------------------------------------------
    // Identity helpers
    // --------------------------------------------------

    private static string? GetUserEmail(ClaimsPrincipal user)
    {
        string? email =
            user.FindFirstValue(ClaimTypes.Email) ??
            user.FindFirstValue("email") ??
            user.FindFirstValue("preferred_username") ??
            user.FindFirstValue(ClaimTypes.Upn);

        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        try
        {
            return NormalizeEmail(email);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string NormalizeEmail(string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        string trimmed = email.Trim();

        MailAddress parsed = new(trimmed);

        if (!string.Equals(
            parsed.Address,
            trimmed,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("Invalid email address.");
        }

        return parsed.Address.ToLowerInvariant();
    }
}
