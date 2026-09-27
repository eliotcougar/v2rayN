namespace ServiceLib.Services.AppRouting;

internal sealed record RoutePackage(string Family, string Name, string Publisher, string? InstallPath = null);

/// <summary>Read-only PackageManager enumeration for the account running v2rayN.</summary>
[SupportedOSPlatform("windows")]
internal static class RoutePackageCatalog
{
    public static IReadOnlyList<RoutePackage> Read() => Read(null);

    public static IReadOnlyList<RoutePackage> Read(IReadOnlySet<string>? installPathsFor)
    {
        // Called on a background thread. Keep the cross-platform ServiceLib independent
        // of Windows SDK projections by using the small, stable WinRT ABI surface below.
        Marshal.ThrowExceptionForHR(RoInitialize(1)); // RO_INIT_MULTITHREADED
        try
        {
            using var manager = Activate();
            Marshal.ThrowExceptionForHR(manager.Method<FindPackages>(12)(manager.Value, 0, out var collection));
            using var packages = new ComReference(collection);
            using var iterator = packages.GetObject(6); // IIterable<Package>.First
            var result = new Dictionary<string, RoutePackage>(StringComparer.OrdinalIgnoreCase);
            for (var current = iterator.GetBoolean(7); current; current = iterator.GetBoolean(8)) // HasCurrent / MoveNext
            {
                using var package = iterator.GetObject(6); // IIterator<Package>.Current -> IPackage
                if (package.GetBoolean(8)) { continue; } // IPackage.IsFramework
                using var metadata = package.Query(new("a6612fb6-7688-4ace-95fb-359538e7aa01")); // IPackage2
                if (metadata.GetBoolean(10) || metadata.GetBoolean(11)) { continue; } // resource package / bundle
                using var id = package.GetObject(6); // IPackage.Id -> IPackageId
                var family = id.GetString(13);
                // A missing localized display resource must not hide an otherwise valid package.
                var name = metadata.TryGetString(6);
                result[family] = new(family, string.IsNullOrWhiteSpace(name) ? id.GetString(6) : name, metadata.TryGetString(7),
                    installPathsFor?.Contains(family) == true ? ReadInstallPath(id.GetString(12)) : null);
            }
            return result.Values.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        finally { RoUninitialize(); }
    }

    private static string ReadInstallPath(string fullName)
    {
        uint length = 0;
        var error = GetPackagePathByFullName(fullName, ref length, null);
        if (error != 122) { throw new Win32Exception(error); } // ERROR_INSUFFICIENT_BUFFER
        var path = new StringBuilder(checked((int)length));
        error = GetPackagePathByFullName(fullName, ref length, path);
        if (error != 0) { throw new Win32Exception(error); }
        return path.ToString();
    }

    private static ComReference Activate()
    {
        const string type = "Windows.Management.Deployment.PackageManager";
        Marshal.ThrowExceptionForHR(WindowsCreateString(type, type.Length, out var name));
        try
        {
            Marshal.ThrowExceptionForHR(RoActivateInstance(name, out var instance));
            using var activated = new ComReference(instance);
            return activated.Query(new("9a7d4b65-5e8f-4fc7-a2e5-7f6925cb8b53")); // IPackageManager
        }
        finally { WindowsDeleteString(name); }
    }

    // Slots include IUnknown (0..2) and IInspectable (3..5). Interface layouts:
    // Windows SDK windows.management.deployment.h and windows.applicationmodel.h.
    // Each returned interface/HSTRING owns one reference, released in the same scope.
    private sealed class ComReference(nint value) : IDisposable
    {
        public nint Value { get; } = value;
        public T Method<T>(int slot) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(Value), slot * IntPtr.Size));

        public ComReference Query(Guid iid)
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(Value, in iid, out var result));
            return new(result);
        }

        public ComReference GetObject(int slot)
        {
            Marshal.ThrowExceptionForHR(Method<GetPointer>(slot)(Value, out var result));
            return new(result);
        }

        public bool GetBoolean(int slot)
        {
            Marshal.ThrowExceptionForHR(Method<GetBooleanValue>(slot)(Value, out var result));
            return result != 0; // WinRT boolean is one byte, including on x86.
        }

        public string GetString(int slot)
        {
            Marshal.ThrowExceptionForHR(Method<GetPointer>(slot)(Value, out var result));
            try
            {
                var buffer = WindowsGetStringRawBuffer(result, out var length);
                return Marshal.PtrToStringUni(buffer, checked((int)length)) ?? "";
            }
            finally { WindowsDeleteString(result); }
        }

        public string TryGetString(int slot)
        {
            try { return GetString(slot); }
            catch (COMException) { return ""; }
        }

        public void Dispose() => Marshal.Release(Value);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int FindPackages(nint instance, nint userSid, out nint packages);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPointer(nint instance, out nint result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBooleanValue(nint instance, out byte result);

    [DllImport("combase.dll")] private static extern int RoInitialize(uint type);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagePathByFullName(string packageFullName, ref uint pathLength, StringBuilder? path);
    [DllImport("combase.dll")] private static extern void RoUninitialize();
    [DllImport("combase.dll")] private static extern int RoActivateInstance(nint name, out nint instance);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string value, int length, out nint result);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll")] private static extern nint WindowsGetStringRawBuffer(nint value, out uint length);
}
