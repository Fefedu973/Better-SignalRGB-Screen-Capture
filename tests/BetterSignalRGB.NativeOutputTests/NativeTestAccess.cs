using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

internal static class NativeTestAccess
{
    public static bool IsUserOnly(string name)
    {
        var error = GetNamedSecurityInfoW(name, 6, 4, out _, out _, out _, out _, out var pointer);
        if (error != 0) throw new Win32Exception((int)error);
        try
        {
            var bytes = new byte[GetSecurityDescriptorLength(pointer)];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            var descriptor = new RawSecurityDescriptor(bytes, 0);
            using var identity = WindowsIdentity.GetCurrent();
            return descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected) &&
                descriptor.DiscretionaryAcl is { Count: 1 } acl && acl[0] is CommonAce { AceQualifier: AceQualifier.AccessAllowed } ace && identity.User is { } user && ace.SecurityIdentifier.Equals(user);
        }
        finally { LocalFree(pointer); }
    }
    public static SafeWaitHandle PermissiveMutex(string name)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("D:P(A;;GA;;;WD)", 1, out var descriptor, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var handle = CreateMutexW(ref attributes, false, name);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            return handle;
        }
        finally { LocalFree(descriptor); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern uint GetNamedSecurityInfoW(string name, uint type, uint information, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeWaitHandle CreateMutexW(ref SecurityAttributes attributes, bool owner, string name);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
