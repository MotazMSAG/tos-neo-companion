using System.Security.AccessControl;
using System.Security.Principal;

namespace Companion.App.Hosting;

/// <summary>
/// RuntimeDir (%ProgramData%\TOS-companion, SPEC 4 step 3 / 10.1): created or verified by the elevated host with a
/// protected ACL (Administrators + SYSTEM full control, the interactive user read-only) and refused if it is a reparse
/// point. Called only from the host start (never from tests: the ACL would lock the test user out of the folder).
/// </summary>
internal static class RuntimeDirectory
{
    /// <summary>Creates or re-protects RuntimeDir. Returns null on success, else a problem code.</summary>
    public static string? EnsureProtected(string runtimeDir, SecurityIdentifier interactiveUser)
    {
        try
        {
            var full = Path.GetFullPath(runtimeDir);
            var parent = Path.GetDirectoryName(full);
            if (parent is not null && SafeFile.IsReparsePoint(parent))
            {
                return "runtimeDir.parentReparse";
            }

            if (SafeFile.IsReparsePoint(full))
            {
                return "runtimeDir.reparse";
            }

            var security = BuildSecurity(interactiveUser);
            var info = new DirectoryInfo(full);
            if (!info.Exists)
            {
                info.Create(security);
            }
            else
            {
                info.SetAccessControl(security);
            }

            return SafeFile.IsReparsePoint(full) ? "runtimeDir.reparse" : null;
        }
        catch (UnauthorizedAccessException)
        {
            return "runtimeDir.accessDenied";
        }
        catch (IOException)
        {
            return "runtimeDir.io";
        }
    }

    /// <summary>The protected ACL: no inheritance from %ProgramData%; children inherit these rules.</summary>
    public static DirectorySecurity BuildSecurity(SecurityIdentifier interactiveUser)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            interactiveUser, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, inherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }
}
