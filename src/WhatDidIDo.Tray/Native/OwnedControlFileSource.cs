using System.Security.AccessControl;
using System.Security.Principal;
using WhatDidIDo.Tray.Core.Control;

namespace WhatDidIDo.Tray.Native;

/// <summary>
/// Reads <c>%LOCALAPPDATA%\whatdidido\control.json</c> only when it and its
/// directory are real (no link or junction) and owned by this user, the owner
/// this user's token gives new objects, Administrators or SYSTEM (TRAY.md
/// section 2.3). The last two: an elevated session's token makes
/// Administrators the owner, and the tray's token may not share that default;
/// neither can be set as owner by another unprivileged account.
/// </summary>
internal sealed class OwnedControlFileSource(string path) : IControlFileSource
{
    private static readonly SecurityIdentifier[] Owners = CurrentOwners();

    public async Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        var directory = file.Directory!;
        if (!directory.Exists || !file.Exists)
            return null;
        Check(directory, directory.GetAccessControl(AccessControlSections.Owner), "control directory");
        Check(file, file.GetAccessControl(AccessControlSections.Owner), "control file");
        return await new PlainControlFileSource(path).ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Check(FileSystemInfo info, FileSystemSecurity security, string what)
    {
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new ControlException($"{what} is a link");
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !Owners.Contains(owner))
            throw new ControlException($"{what} is not owned by this user");
    }

    private static SecurityIdentifier[] CurrentOwners()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new[]
        {
            identity.User,
            identity.Owner,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
        }.OfType<SecurityIdentifier>().Distinct().ToArray();
    }
}
