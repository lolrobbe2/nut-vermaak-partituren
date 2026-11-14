using System;

namespace nut_vermaak_partituren.Permissions;

[Flags]
public enum FilePermission
{
    None = 0,
    List = 1 << 0,
    Read = 1 << 1,
    Upload = 1 << 2,
    Update = 1 << 3,
    Delete = 1 << 4,
    CreateFolder = 1 << 5,
    ManageUsers = 1 << 6,

    All = List | Read | Upload | Update |
          Delete | CreateFolder | ManageUsers
}

public sealed record UserPermissionEntry(
    string Email,
    FilePermission Permissions);