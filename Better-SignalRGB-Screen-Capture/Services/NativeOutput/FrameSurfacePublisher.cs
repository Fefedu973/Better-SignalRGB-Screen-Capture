using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Better_SignalRGB_Screen_Capture.Services.NativeOutput;

/// <summary>
/// Windows ORGBFRM1 v1 latest-image publisher. The named mutex makes each copy atomic
/// for compatible readers; publication never waits for a reader or another publisher call.
/// Construct/dispose on the output worker, not the UI thread. No caller buffer is retained.
/// </summary>
public sealed unsafe class FrameSurfacePublisher : IDisposable
{
    public const ulong MaximumCapacity = 64UL * 1024 * 1024;
    public const int HeaderBytes = 128;
    private const uint Format = 1;
    private const uint LifetimeFlag = 1;
    private readonly object _gate = new();
    private readonly uint _processId = (uint)Environment.ProcessId;
    private readonly ulong _processStart;
    private KernelHandle? _mutex;
    private KernelHandle? _mapping;
    private KernelHandle? _lifetime;
    private ViewHandle? _view;
    private long _sequence;
    private long _timestamp;
    private int _disposed;
    private string? _lastError;

    public string Channel { get; }
    public ulong Capacity { get; }
    public ulong Generation { get; }
    public ulong Sequence => unchecked((ulong)Interlocked.Read(ref _sequence));
    public ulong TimestampMilliseconds => unchecked((ulong)Interlocked.Read(ref _timestamp));
    public bool IsOpen => Volatile.Read(ref _disposed) == 0 && _view is { IsInvalid: false, IsClosed: false };
    public string? LastError => Volatile.Read(ref _lastError);

    /// <remarks>
    /// A live publisher is never replaced. An old mapping retained by readers may be
    /// reused at its existing capacity, but cannot grow until those readers close it.
    /// The initial ownership transaction waits at most 100 ms for the named mutex.
    /// </remarks>
    public FrameSurfacePublisher(string channel, ulong requestedCapacity = MaximumCapacity)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Frame surfaces require Windows.");
        if (string.IsNullOrEmpty(channel) || channel.Length > 64 ||
            channel.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException("Use 1–64 ASCII letters, digits, '-' or '_' for a surface channel.", nameof(channel));
        if (requestedCapacity is 0 or > MaximumCapacity)
            throw new ArgumentOutOfRangeException(nameof(requestedCapacity));
        Channel = channel;
        _processStart = ProcessStart(Native.GetCurrentProcess());
        if (_processStart == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot identify publisher process.");

        try
        {
            using var security = new UserSecurity();
            _mutex = Native.CreateMutexW(ref security.Attributes, false, ObjectName + ".Mutex");
            CheckHandle(_mutex, "Cannot create surface mutex.");
            security.Verify(_mutex);
            if (!Acquire(_mutex, 100)) throw new InvalidOperationException("Surface mutex is busy.");
            try
            {
                _mapping = Native.CreateFileMappingW(new IntPtr(-1), ref security.Attributes, 4, 0,
                    checked((uint)(HeaderBytes + requestedCapacity)), ObjectName);
                var existed = Marshal.GetLastWin32Error() == 183;
                CheckHandle(_mapping, "Cannot create surface mapping.");
                security.Verify(_mapping);
                Capacity = requestedCapacity;
                if (existed)
                {
                    using var prefix = Map(_mapping, HeaderBytes, writable: false);
                    var previous = Bytes(prefix, HeaderBytes);
                    if (!ValidCapacityHeader(previous)) throw new InvalidOperationException("Existing surface header is invalid.");
                    if (OwnerAlive(previous)) throw new InvalidOperationException("Surface already has a live publisher.");
                    Capacity = Read64(previous, 16);
                    if (Capacity < requestedCapacity)
                        throw new InvalidOperationException("Existing reader mapping is smaller; close readers before resizing.");
                }

                _view = Map(_mapping, checked(HeaderBytes + (int)Capacity), writable: true);
                Span<byte> random = stackalloc byte[8];
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    RandomNumberGenerator.Fill(random);
                    var generation = BinaryPrimitives.ReadUInt64LittleEndian(random);
                    if (generation == 0) continue;
                    var marker = Native.CreateEventW(ref security.Attributes, true, false, OwnerName(generation));
                    var collision = Marshal.GetLastWin32Error() == 183;
                    CheckHandle(marker, "Cannot create publisher lifetime marker.");
                    if (collision) { marker.Dispose(); continue; }
                    _lifetime = marker;
                    Generation = generation;
                    break;
                }
                if (_lifetime is null) throw new InvalidOperationException("Cannot create a unique publisher generation.");
                var header = Bytes(_view, HeaderBytes);
                header.Clear();
                "ORGBFRM1"u8.CopyTo(header);
                Write32(header, 8, 1);
                Write32(header, 12, HeaderBytes);
                Write64(header, 16, Capacity);
                Write32(header, 36, Format);
                Write64(header, 64, Generation);
                Write32(header, 72, _processId);
                Write32(header, 76, LifetimeFlag);
                Write64(header, 80, _processStart);
            }
            finally { Native.ReleaseMutex(_mutex); }
        }
        catch
        {
            _lifetime?.Dispose();
            _view?.Dispose();
            _mapping?.Dispose();
            _mutex?.Dispose();
            Volatile.Write(ref _disposed, 1);
            throw;
        }
    }

    /// <summary>
    /// Copies one opaque, unpremultiplied BGRA8/sRGB frame. Callers must not mutate pixels
    /// until this call returns. Invalid input or contention leaves the last frame intact.
    /// Padding in the published rows is zeroed, regardless of caller padding contents.
    /// </summary>
    public bool TryPublish(ReadOnlySpan<byte> pixels, uint width, uint height, uint stride)
    {
        if (!Monitor.TryEnter(_gate)) return Fail("Publisher is busy; frame dropped.");
        try
        {
            if (!IsOpen) return Fail("Publisher is closed.");
            var rowBytes = (ulong)width * 4;
            var payload = (ulong)stride * height;
            if (width == 0 || height == 0 || rowBytes > uint.MaxValue || stride < rowBytes ||
                payload > Capacity || payload > (ulong)pixels.Length)
                return Fail("Invalid or oversized BGRA frame.");
            if (!IsOpaque(pixels, (int)width, (int)height, (int)stride))
                return Fail("BGRA8 surface requires alpha 255.");
            if (!Acquire(_mutex!, 0)) return Fail("Surface mutex is busy; frame dropped.");
            try
            {
                var header = Bytes(_view!, HeaderBytes);
                if (!Owns(header) || Volatile.Read(ref _disposed) != 0) return Fail("Publisher ownership ended.");
                var sequence = Sequence;
                if (sequence == ulong.MaxValue) return Fail("Frame sequence exhausted; recreate publisher.");
                var destination = Bytes(_view!, checked(HeaderBytes + (int)payload))[HeaderBytes..];
                if (rowBytes == stride) pixels[..(int)payload].CopyTo(destination);
                else for (var y = 0; y < (int)height; y++)
                {
                    var offset = y * (int)stride;
                    pixels.Slice(offset, (int)rowBytes).CopyTo(destination.Slice(offset));
                    destination.Slice(offset + (int)rowBytes, (int)(stride - rowBytes)).Clear();
                }
                var timestamp = Native.GetTickCount64();
                Write32(header, 24, width);
                Write32(header, 28, height);
                Write32(header, 32, stride);
                Write64(header, 40, payload);
                Write64(header, 48, ++sequence);
                Write64(header, 56, timestamp);
                Interlocked.Exchange(ref _sequence, unchecked((long)sequence));
                Interlocked.Exchange(ref _timestamp, unchecked((long)timestamp));
                Volatile.Write(ref _lastError, null);
                return true;
            }
            finally { Native.ReleaseMutex(_mutex!); }
        }
        finally { Monitor.Exit(_gate); }
    }

    /// <summary>
    /// Renews an already published static frame without changing its sequence or pixels.
    /// Call only from the healthy output worker; an independent timer could conceal a hang.
    /// </summary>
    public bool Heartbeat()
    {
        if (!Monitor.TryEnter(_gate)) return Fail("Publisher is busy; heartbeat skipped.");
        try
        {
            if (!IsOpen || Sequence == 0) return Fail("No live frame to renew.");
            if (!Acquire(_mutex!, 0)) return Fail("Surface mutex is busy; heartbeat skipped.");
            try
            {
                var header = Bytes(_view!, HeaderBytes);
                if (!Owns(header) || Volatile.Read(ref _disposed) != 0) return Fail("Publisher ownership ended.");
                var timestamp = Native.GetTickCount64();
                Write64(header, 56, timestamp);
                Interlocked.Exchange(ref _timestamp, unchecked((long)timestamp));
                Volatile.Write(ref _lastError, null);
                return true;
            }
            finally { Native.ReleaseMutex(_mutex!); }
        }
        finally { Monitor.Exit(_gate); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // Retire first: even a reader holding the mutex cannot keep this publisher alive.
        Interlocked.Exchange(ref _lifetime, null)?.Dispose();
        lock (_gate)
        {
            if (_view is { IsInvalid: false, IsClosed: false } && _mutex is not null && Acquire(_mutex, 0))
            {
                try
                {
                    var header = Bytes(_view, HeaderBytes);
                    if (Owns(header)) Write32(header, 72, 0);
                }
                finally { Native.ReleaseMutex(_mutex); }
            }
            _view?.Dispose();
            _mapping?.Dispose();
            _mutex?.Dispose();
        }
    }

    private string ObjectName => "Local\\OpenRGB-Room.Surface." + Channel;
    private string OwnerName(ulong generation) => ObjectName + ".Owner." + generation.ToString(CultureInfo.InvariantCulture);
    private bool Fail(string error) { Volatile.Write(ref _lastError, error); return false; }
    private bool Owns(ReadOnlySpan<byte> header) => ValidCapacityHeader(header) && Read64(header, 16) == Capacity &&
        Read64(header, 64) == Generation && Read32(header, 72) == _processId &&
        Read32(header, 76) == LifetimeFlag && Read64(header, 80) == _processStart;
    private static bool ValidCapacityHeader(ReadOnlySpan<byte> header) => header[..8].SequenceEqual("ORGBFRM1"u8) &&
        Read32(header, 8) == 1 && Read32(header, 12) == HeaderBytes && Read64(header, 16) is > 0 and <= MaximumCapacity;
    private bool OwnerAlive(ReadOnlySpan<byte> header)
    {
        var pid = Read32(header, 72);
        if (pid == 0) return false;
        if ((Read32(header, 76) & LifetimeFlag) != 0)
        {
            using var marker = Native.OpenEventW(0x100000, false, OwnerName(Read64(header, 64)));
            if (marker.IsInvalid && Marshal.GetLastWin32Error() == 2) return false;
        }
        using var process = Native.OpenProcess(0x101000, false, pid);
        if (process.IsInvalid) return Marshal.GetLastWin32Error() != 87;
        if (Native.WaitForSingleObject(process, 0) != 258) return false;
        var start = ProcessStart(process.DangerousGetHandle());
        return start == 0 || start == Read64(header, 80);
    }

    private static bool IsOpaque(ReadOnlySpan<byte> pixels, int width, int height, int stride)
    {
        var alpha = new Vector<uint>(0xff000000);
        for (var y = 0; y < height; y++)
        {
            var row = MemoryMarshal.Cast<byte, uint>(pixels.Slice(y * stride, width * 4));
            var x = 0;
            if (Vector.IsHardwareAccelerated)
                for (; x <= row.Length - Vector<uint>.Count; x += Vector<uint>.Count)
                    if (!Vector.EqualsAll(new Vector<uint>(row.Slice(x)) & alpha, alpha)) return false;
            for (; x < row.Length; x++) if ((row[x] & 0xff000000) != 0xff000000) return false;
        }
        return true;
    }

    private static ulong ProcessStart(IntPtr process) => Native.GetProcessTimes(process, out var creation, out _, out _, out _) ? creation : 0;
    private static bool Acquire(KernelHandle mutex, uint timeout) => Native.WaitForSingleObject(mutex, timeout) is 0 or 128;
    private static void CheckHandle(KernelHandle handle, string message)
    {
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error(), message); }
    }
    private static ViewHandle Map(KernelHandle mapping, int bytes, bool writable)
    {
        var view = Native.MapViewOfFile(mapping, writable ? 0xf001fu : 4u, 0, 0, (nuint)bytes);
        if (view.IsInvalid) { view.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot map bounded surface storage."); }
        return view;
    }
    private static Span<byte> Bytes(ViewHandle view, int size) => new((void*)view.DangerousGetHandle(), size);
    private static uint Read32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static ulong Read64(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]);
    private static void Write32(Span<byte> bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes[offset..], value);
    private static void Write64(Span<byte> bytes, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes[offset..], value);

    private sealed class UserSecurity : IDisposable
    {
        public Native.SecurityAttributes Attributes;
        private readonly SecurityIdentifier _sid;
        public UserSecurity()
        {
            if (!Native.OpenProcessToken(Native.GetCurrentProcess(), 8, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            using (token)
            using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                _sid = identity.User ?? throw new InvalidOperationException("Cannot identify current process user.");
            if (!Native.ConvertStringSecurityDescriptorToSecurityDescriptorW("D:P(A;;GA;;;" + _sid.Value + ")", 1, out var descriptor, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            Attributes = new() { Length = Marshal.SizeOf<Native.SecurityAttributes>(), Descriptor = descriptor, InheritHandle = false };
        }
        public void Verify(KernelHandle handle)
        {
            var error = Native.GetSecurityInfo(handle, 6, 4, out _, out _, out _, out _, out var pointer);
            if (error != 0) throw new Win32Exception((int)error, "Cannot verify surface user-only permissions.");
            try
            {
                var bytes = new byte[Native.GetSecurityDescriptorLength(pointer)];
                Marshal.Copy(pointer, bytes, 0, bytes.Length);
                var descriptor = new RawSecurityDescriptor(bytes, 0);
                var acl = descriptor.DiscretionaryAcl;
                if (!descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected) || acl is null || acl.Count != 1 ||
                    acl[0] is not CommonAce { AceQualifier: AceQualifier.AccessAllowed } ace || !ace.SecurityIdentifier.Equals(_sid))
                    throw new InvalidOperationException("Existing surface permissions are not restricted to the current user.");
            }
            finally { Native.LocalFree(pointer); }
        }
        public void Dispose() { Native.LocalFree(Attributes.Descriptor); Attributes.Descriptor = IntPtr.Zero; }
    }

    private sealed class KernelHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => Native.CloseHandle(handle);
    }
    private sealed class ViewHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => Native.UnmapViewOfFile(handle);
    }
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern KernelHandle CreateMutexW(ref SecurityAttributes security, bool owner, string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern KernelHandle CreateFileMappingW(IntPtr file, ref SecurityAttributes security, uint protect, uint high, uint low, string name);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern ViewHandle MapViewOfFile(KernelHandle mapping, uint access, uint high, uint low, nuint bytes);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnmapViewOfFile(IntPtr view);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern KernelHandle CreateEventW(ref SecurityAttributes security, bool manualReset, bool initialState, string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern KernelHandle OpenEventW(uint access, bool inherit, string name);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern KernelHandle OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(KernelHandle handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ReleaseMutex(KernelHandle handle);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] internal static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] internal static extern ulong GetTickCount64();
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetProcessTimes(IntPtr process, out ulong creation, out ulong exit, out ulong kernel, out ulong user);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr descriptor, out uint size);
        [DllImport("advapi32.dll")] internal static extern uint GetSecurityInfo(KernelHandle handle, uint type, uint information, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
        [DllImport("advapi32.dll")] internal static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
        [DllImport("kernel32.dll")] internal static extern IntPtr LocalFree(IntPtr memory);
    }
}
