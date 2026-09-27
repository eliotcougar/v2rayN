using Microsoft.Win32.SafeHandles;

namespace ServiceLib.Services.AppRouting;

/// <summary>Null means unknown; empty means Windows confirmed an unpackaged process.</summary>
[SupportedOSPlatform("windows")]
internal static class RoutePackageIdentity
{
    public static string? Read(SafeProcessHandle process)
    {
        var length = 128u; // PACKAGE_FAMILY_NAME_MAX_LENGTH + terminator
        var family = new StringBuilder((int)length);
        var result = GetPackageFamilyName(process, ref length, family);
        return result switch { 0 => family.ToString(), 15700 => "", _ => null }; // APPMODEL_ERROR_NO_PACKAGE
    }

    public static string? FromFullName(string fullName)
    {
        if (fullName.Length == 0) { return ""; }
        var length = 128u;
        var family = new StringBuilder((int)length);
        return PackageFamilyNameFromFullName(fullName, ref length, family) == 0 ? family.ToString() : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFamilyName(SafeProcessHandle process, ref uint length, StringBuilder family);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int PackageFamilyNameFromFullName(string fullName, ref uint length, StringBuilder family);
}
