using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using Better_SignalRGB_Screen_Capture.Services.NativeOutput;

internal static class Program
{
    private static int _checks;
    private static string _readerPath = "";
    private static string _headerHash = "";
    private static string Channel() => "BetterTest_" + Guid.NewGuid().ToString("N");

    private static int Main(string[] args)
    {
        try
        {
            if (args is ["--child-publisher", var channel])
            {
                using var publisher = new FrameSurfacePublisher(channel, 1024);
                if (!publisher.TryPublish(Pattern(4, 3, 16, 77), 4, 3, 16)) return 2;
                Console.WriteLine("PUBLISHED");
                Console.ReadLine();
                return 0;
            }
            _readerPath = Value(args, "--reader");
            _headerHash = Value(args, "--header-hash");
            Console.WriteLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, process={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}.");
            BasicContract();
            OwnershipAndSecurity();
            StaticAndContention();
            ReadersAndConcurrency();
            DisposeAndReopen();
            CrashRecovery();
            CorruptHeaders();
            if (!args.Contains("--skip-benchmark")) Benchmark();
            Console.WriteLine($"PASS Native output: {_checks} assertions; actual external C++ Reader SHA256={_headerHash}.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static void BasicContract()
    {
        foreach (var channel in new[] { "", "a/b", "é", new string('x', 65), "a.b", "Global\\a" })
            Throws<ArgumentException>(() => new FrameSurfacePublisher(channel), "invalid channel rejected");
        Throws<ArgumentOutOfRangeException>(() => new FrameSurfacePublisher(Channel(), 0), "zero capacity rejected");
        Throws<ArgumentOutOfRangeException>(() => new FrameSurfacePublisher(Channel(), FrameSurfacePublisher.MaximumCapacity + 1), "over-budget capacity rejected");
        var name = Channel();
        using var reader = new Reader(name);
        Equal("Unavailable", reader.Read().Status, "absent publisher");
        using var publisher = new FrameSurfacePublisher(name, 4096);
        Check(publisher.IsOpen && publisher.Generation != 0 && publisher.Sequence == 0, "open unpublished identity");
        Check(!publisher.Heartbeat(), "heartbeat cannot invent a frame");
        Equal("Unavailable", reader.Read().Status, "unpublished surface unavailable");
        foreach (var dimensions in new[] { (1, 1, 4), (3, 5, 16), (8, 7, 32) })
        {
            var (width, height, stride) = dimensions;
            var bytes = Pattern(width, height, stride, 13);
            Check(publisher.TryPublish(bytes, (uint)width, (uint)height, (uint)stride), "valid odd/padded frame publishes");
            var expected = (byte[])bytes.Clone();
            ClearPadding(expected, width, height, stride);
            Array.Fill(bytes, (byte)0); // Published storage must own the copy.
            var result = reader.Read();
            Equal("NewFrame", result.Status, "reader receives new frame");
            Equal((uint)width, result.Width, "width ABI");
            Equal((uint)height, result.Height, "height ABI");
            Equal((uint)stride, result.Stride, "stride ABI");
            Equal(Hash(expected), result.Hash, "owned pixels and cleared row padding");
            Check(result.PaddingZero && result.PatternValid, "opaque unpremultiplied BGRA and padding");
            Equal(publisher.Generation, result.Generation, "stable generation");
            Equal(publisher.Sequence, result.Sequence, "committed sequence");
            Equal("Unchanged", reader.Read().Status, "reader does not consume or duplicate image");
        }
        var header = Header(name);
        Equal("ORGBFRM1", System.Text.Encoding.ASCII.GetString(header, 0, 8), "magic ABI");
        Equal(1u, U32(header, 8), "version ABI");
        Equal(128u, U32(header, 12), "header bytes ABI");
        Equal(4096UL, U64(header, 16), "capacity ABI");
        Equal(1u, U32(header, 36), "format ABI");
        Equal((ulong)U32(header, 28) * U32(header, 32), U64(header, 40), "payload bytes ABI");
        Equal((uint)Environment.ProcessId, U32(header, 72), "owner PID ABI");
        Check(U32(header, 76) == 1 && U64(header, 80) != 0, "lifetime marker and process creation identity");
        Check(header.AsSpan(88).IndexOfAnyExcept((byte)0) < 0, "reserved bytes remain zero");
        var sequence = publisher.Sequence;
        var timestamp = publisher.TimestampMilliseconds;
        var valid = Pattern(3, 5, 12, 7);
        Check(!publisher.TryPublish(valid, 0, 5, 12), "zero width rejected");
        Check(!publisher.TryPublish(valid, 3, 0, 12), "zero height rejected");
        Check(!publisher.TryPublish(valid, uint.MaxValue, 5, 12), "row overflow rejected");
        Check(!publisher.TryPublish(valid, 3, uint.MaxValue, uint.MaxValue), "payload overflow rejected");
        Check(!publisher.TryPublish(valid, 3, 5, 11), "short stride rejected");
        Check(!publisher.TryPublish(valid.AsSpan(0, valid.Length - 1), 3, 5, 12), "short input rejected");
        valid[^1] = 254;
        Check(!publisher.TryPublish(valid, 3, 5, 12), "non-opaque frame rejected");
        Equal(sequence, publisher.Sequence, "invalid frames preserve sequence");
        Equal(timestamp, publisher.TimestampMilliseconds, "invalid frames do not conceal producer failure");
        Equal("Unchanged", reader.Read().Status, "invalid frames preserve last reader image");
    }

    private static void OwnershipAndSecurity()
    {
        var channel = Channel();
        using var publisher = new FrameSurfacePublisher(channel, 1024);
        Throws<InvalidOperationException>(() => new FrameSurfacePublisher(channel, 1024), "live owner cannot be replaced");
        foreach (var name in new[] { "Local\\OpenRGB-Room.Surface." + channel, "Local\\OpenRGB-Room.Surface." + channel + ".Mutex",
            "Local\\OpenRGB-Room.Surface." + channel + ".Owner." + publisher.Generation.ToString(CultureInfo.InvariantCulture) })
            Check(NativeTestAccess.IsUserOnly(name), "mapping, mutex and lifetime marker are user-only with protected DACL");
        var unsafeChannel = Channel();
        using var unsafeMutex = NativeTestAccess.PermissiveMutex("Local\\OpenRGB-Room.Surface." + unsafeChannel + ".Mutex");
        Throws<InvalidOperationException>(() => new FrameSurfacePublisher(unsafeChannel, 1024), "preexisting permissive permissions rejected without rewriting object");
    }

    private static void StaticAndContention()
    {
        var channel = Channel();
        using var publisher = new FrameSurfacePublisher(channel, 1024);
        using var reader = new Reader(channel);
        var bytes = Pattern(4, 3, 16, 33);
        Check(publisher.TryPublish(bytes, 4, 3, 16), "static frame published");
        var frame = reader.Read();
        Thread.Sleep(45);
        Equal("Stale", reader.Read(20).Status, "hung producer expires while lifetime marker is alive");
        Check(publisher.Heartbeat(), "healthy static heartbeat");
        var renewed = reader.Read(200);
        Equal("Unchanged", renewed.Status, "heartbeat renews TTL without creating a new frame");
        Equal(frame.Sequence, publisher.Sequence, "heartbeat keeps sequence");
        Equal(frame.Generation, publisher.Generation, "heartbeat keeps generation");
        Check(U64(Header(channel), 56) > frame.Timestamp, "actual shared header heartbeat timestamp advances");
        Equal(frame.Timestamp, renewed.Timestamp, "actual Reader keeps cached frame timestamp on Unchanged");
        reader.Send("hold 250");
        Equal("HELD", reader.Line(), "external reader holds mutex");
        var clock = Stopwatch.StartNew();
        Check(!publisher.TryPublish(bytes, 4, 3, 16), "contended publication drops latest attempt");
        Check(!publisher.Heartbeat(), "contended heartbeat skips");
        Check(clock.ElapsedMilliseconds < 100, "publisher never waits for slow reader");
        Equal(frame.Sequence, publisher.Sequence, "contention preserves sequence");
        Equal("RELEASED", reader.Line(), "external reader releases mutex");
        Check(publisher.TryPublish(bytes, 4, 3, 16), "publication resumes after contention");
        Equal("NewFrame", reader.Read().Status, "reader recovers");
        using (var abandoned = new Reader(channel))
        {
            abandoned.Send("abandon");
            Equal("HELD", abandoned.Line(), "synthetic reader owns mutex before exit");
            abandoned.ExpectExit();
            Check(publisher.TryPublish(bytes, 4, 3, 16), "writer recovers WAIT_ABANDONED without hanging");
        }
        Equal("NewFrame", reader.Read().Status, "surviving reader recovers after another reader crashes while locked");
    }

    private static void ReadersAndConcurrency()
    {
        var channel = Channel();
        using var publisher = new FrameSurfacePublisher(channel, 320 * 200 * 4);
        using var first = new Reader(channel);
        using var second = new Reader(channel);
        for (var i = 0; i < 50; i++) Check(publisher.TryPublish(Pattern(16, 12, 64, (byte)i), 16, 12, 64), "burst publication succeeds without readers holding lock");
        var a = first.Read();
        var b = second.Read();
        Equal(50UL, a.Sequence, "slow reader gets latest image only");
        Equal(a.Sequence, b.Sequence, "independent readers receive same sequence");
        Equal(a.Hash, b.Hash, "independent readers receive same pixels");
        Equal("Unchanged", first.Read().Status, "no queued old frames");
        var frames = new[] { Pattern(320, 200, 1280, 17), Pattern(320, 200, 1280, 239) };
        first.Send("stress 650"); second.Send("stress 650");
        Equal("STRESS_READY", first.Line(), "first reader stress ready");
        Equal("STRESS_READY", second.Line(), "second reader stress ready");
        var clock = Stopwatch.StartNew();
        var published = 0;
        var dropped = 0;
        while (clock.ElapsedMilliseconds < 600)
        {
            if (publisher.TryPublish(frames[published & 1], 320, 200, 1280)) published++;
            else dropped++;
            Thread.Yield();
        }
        foreach (var reader in new[] { first, second })
        {
            var result = reader.Line().Split(' ');
            Equal("STRESS", result[0], "reader stress completion");
            Check(int.Parse(result[1]) > 0, "concurrent reader observes fresh frames");
            Equal("0", result[3], "no torn images under simultaneous readers and writer");
            Equal("0", result[4], "no malformed or unavailable frames during healthy writer");
        }
        Check(published > 0, "writer progresses with multiple readers");
        Console.WriteLine($"Contention: published={published}, dropped={dropped}; latest-only drops are expected.");
    }

    private static void DisposeAndReopen()
    {
        var channel = Channel();
        var publisher = new FrameSurfacePublisher(channel, 1024);
        using var reader = new Reader(channel);
        var pixels = Pattern(4, 3, 16, 99);
        Check(publisher.TryPublish(pixels, 4, 3, 16), "pre-disposal frame");
        var old = reader.Read();
        reader.Send("hold 250");
        Equal("HELD", reader.Line(), "reader holds during disposal");
        var clock = Stopwatch.StartNew();
        publisher.Dispose();
        Check(clock.ElapsedMilliseconds < 100, "dispose does not wait for external reader");
        Equal("RELEASED", reader.Line(), "reader finishes after disposal");
        Equal("Unavailable", reader.Read().Status, "lifetime marker retires publisher even if owner header could not be cleared");
        Check(!publisher.TryPublish(pixels, 4, 3, 16) && !publisher.Heartbeat(), "disposed publisher cannot write");
        publisher.Dispose();
        Throws<InvalidOperationException>(() => new FrameSurfacePublisher(channel, 2048), "reader-held mapping cannot grow");
        using (var replacement = new FrameSurfacePublisher(channel, 512))
        {
            Equal(1024UL, replacement.Capacity, "reuses actual retained mapping capacity");
            Check(replacement.Generation != old.Generation, "reopen changes generation");
            Check(replacement.TryPublish(pixels, 4, 3, 16), "replacement publishes");
            var next = reader.Read();
            Equal("NewFrame", next.Status, "existing Reader sees replacement generation");
            Equal(1UL, next.Sequence, "new generation sequence starts at one");
        }
        reader.Send("close"); Equal("CLOSED", reader.Line(), "reader releases old mapping");
        using (var larger = new FrameSurfacePublisher(channel, 2048)) Equal(2048UL, larger.Capacity, "growth succeeds once old readers release mapping");
        var raced = new FrameSurfacePublisher(Channel(), 800 * 600 * 4);
        var big = Pattern(800, 600, 3200, 42);
        using var started = new ManualResetEventSlim();
        var worker = Task.Run(() => { started.Set(); while (raced.IsOpen) raced.TryPublish(big, 800, 600, 3200); });
        Check(started.Wait(2000), "concurrent publisher starts");
        Thread.Sleep(20);
        Parallel.Invoke(raced.Dispose, raced.Dispose);
        Check(worker.Wait(2000), "concurrent publish and repeated dispose terminate without invalid pointer access");
        Check(!raced.IsOpen && !raced.TryPublish(big, 800, 600, 3200), "concurrent disposal permanently closes publisher");
    }

    private static void CrashRecovery()
    {
        var channel = Channel();
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--child-publisher"); start.ArgumentList.Add(channel);
        using var child = Process.Start(start)!;
        try
        {
            Equal("PUBLISHED", child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(), "owned synthetic child publishes");
            using var reader = new Reader(channel);
            var old = reader.Read();
            Equal("NewFrame", old.Status, "actual Reader consumes child publisher");
            child.Kill();
            Check(child.WaitForExit(3000), "owned synthetic child terminated");
            Equal("Unavailable", reader.Read().Status, "crash closes lifetime marker immediately");
            using var recovered = new FrameSurfacePublisher(channel, 1024);
            Check(recovered.Generation != old.Generation && recovered.TryPublish(Pattern(4, 3, 16, 12), 4, 3, 16), "new publisher recovers abandoned surface");
            Equal("NewFrame", reader.Read().Status, "reader recovers from producer crash without restart");
        }
        finally { if (!child.HasExited) { child.Kill(); child.WaitForExit(3000); } }
    }

    private static void CorruptHeaders()
    {
        var channel = Channel();
        using var publisher = new FrameSurfacePublisher(channel, 1024);
        using var reader = new Reader(channel);
        Check(publisher.TryPublish(Pattern(4, 3, 16, 44), 4, 3, 16), "header rejection baseline");
        Equal("NewFrame", reader.Read().Status, "header rejection reader opens");
        var original = Header(channel);
        foreach (var mutation in new Action<byte[]>[]
        {
            h => h[0] = 0,
            h => BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), 2),
            h => BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), 64),
            h => BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(16), ulong.MaxValue),
            h => BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(24), uint.MaxValue),
            h => BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(32), 1),
            h => BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(36), 77),
            h => BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(40), 1),
            h => BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(56), ulong.MaxValue)
        })
        {
            var corrupt = (byte[])original.Clone(); mutation(corrupt);
            try { Header(channel, corrupt); Equal("Invalid", reader.Read().Status, "actual Reader rejects malformed ABI/arithmetic before copying"); }
            finally { Header(channel, original); }
        }
        Equal("Unchanged", reader.Read().Status, "reader recovers after rejected header with last valid image intact");
        var changedOwner = (byte[])original.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(changedOwner.AsSpan(64), publisher.Generation ^ 0x1234UL);
        try
        {
            Header(channel, changedOwner);
            Check(!publisher.TryPublish(Pattern(4, 3, 16, 1), 4, 3, 16) && !publisher.Heartbeat(), "lost ownership rejects both pixels and heartbeats");
            Check(Header(channel).SequenceEqual(changedOwner), "lost owner never repairs or overwrites another generation");
        }
        finally { Header(channel, original); }
    }

    private static void Benchmark()
    {
        foreach (var (width, height) in new[] { (320, 200), (800, 600) })
        {
            using var publisher = new FrameSurfacePublisher(Channel(), 800 * 600 * 4);
            var bytes = Pattern(width, height, width * 4, 44);
            for (var warmup = 0; warmup < 1000; warmup++) publisher.TryPublish(bytes, (uint)width, (uint)height, (uint)(width * 4));
            const int count = 90;
            var times = new double[count];
            var allocated = 0L;
            using var process = Process.GetCurrentProcess();
            var cpuBefore = process.TotalProcessorTime;
            var clock = Stopwatch.StartNew();
            for (var frame = 0; frame < count; frame++)
            {
                var deadline = frame * Stopwatch.Frequency / 30;
                while (clock.ElapsedTicks < deadline) Thread.Sleep(1);
                var before = GC.GetAllocatedBytesForCurrentThread();
                var timestamp = Stopwatch.GetTimestamp();
                var success = publisher.TryPublish(bytes, (uint)width, (uint)height, (uint)(width * 4));
                times[frame] = Stopwatch.GetElapsedTime(timestamp).TotalMicroseconds;
                allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                Check(success, "paced benchmark publishes");
            }
            while (clock.ElapsedTicks < count * Stopwatch.Frequency / 30) Thread.Sleep(1);
            var cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
            Array.Sort(times);
            Console.WriteLine(FormattableString.Invariant($"BENCH {width}x{height} target=30fps samples={count} actual={count / clock.Elapsed.TotalSeconds:F2}fps publish_p50={times[count / 2]:F2}us publish_p95={times[(int)(count * .95) - 1]:F2}us max={times[^1]:F2}us process_cpu={cpuMs:F2}ms allocations={allocated}B; opaque validation + synchronous copy, no readers/compositing/capture/LED renderer."));
            Equal(0L, allocated, "successful publication allocates no managed bytes after warmup");
        }
    }

    private static byte[] Pattern(int width, int height, int stride, byte value)
    {
        var result = new byte[stride * height]; Array.Fill(result, (byte)0xcc);
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        { var p = y * stride + x * 4; result[p] = value; result[p + 1] = (byte)(value ^ 0x55); result[p + 2] = (byte)(255 - value); result[p + 3] = 255; }
        return result;
    }
    private static void ClearPadding(byte[] bytes, int width, int height, int stride)
    { for (var y = 0; y < height; y++) bytes.AsSpan(y * stride + width * 4, stride - width * 4).Clear(); }
    private static ulong Hash(byte[] bytes) { var hash = 14695981039346656037UL; foreach (var b in bytes) hash = unchecked((hash ^ b) * 1099511628211UL); return hash; }
    private static byte[] Header(string channel, byte[]? replacement = null)
    {
        using var mutex = Mutex.OpenExisting("Local\\OpenRGB-Room.Surface." + channel + ".Mutex");
        if (!mutex.WaitOne(1000)) throw new TimeoutException("Test header mutex busy.");
        try
        {
            using var mapping = MemoryMappedFile.OpenExisting("Local\\OpenRGB-Room.Surface." + channel);
            using var view = mapping.CreateViewAccessor(0, 128);
            if (replacement is not null) view.WriteArray(0, replacement, 0, replacement.Length);
            var bytes = new byte[128]; view.ReadArray(0, bytes, 0, bytes.Length); return bytes;
        }
        finally { mutex.ReleaseMutex(); }
    }
    private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
    private static ulong U64(byte[] b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(o));
    private static string Value(string[] args, string name) { var index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : throw new ArgumentException(name); }
    private static void Check(bool condition, string name) { _checks++; if (!condition) throw new InvalidOperationException(name); }
    private static void Equal<T>(T expected, T actual, string name) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{name}: expected {expected}, actual {actual}");
    private static void Throws<T>(Action operation, string name) where T : Exception
    { try { operation(); } catch (T) { Check(true, name); return; } throw new InvalidOperationException(name); }

    private sealed class Reader : IDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _errors;
        public Reader(string channel)
        {
            var start = new ProcessStartInfo(_readerPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(channel); _process = Process.Start(start)!;
            _errors = _process.StandardError.ReadToEndAsync();
            Equal("READY " + _headerHash, Line(), "reader binary includes authoritative header hash");
        }
        public void Send(string value) { _process.StandardInput.WriteLine(value); _process.StandardInput.Flush(); }
        public void ExpectExit() => Check(_process.WaitForExit(3000), "owned reader process exits within bound");
        public string Line() => _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult() ?? throw new IOException("Native reader stopped: " + _errors.GetAwaiter().GetResult());
        public ReadResult Read(uint ttl = 2000, uint timeout = 5)
        {
            Send($"read {ttl} {timeout}"); var parts = Line().Split(' ');
            return new(parts[0], uint.Parse(parts[1]), uint.Parse(parts[2]), uint.Parse(parts[3]), ulong.Parse(parts[4]), ulong.Parse(parts[5]), ulong.Parse(parts[6]), ulong.Parse(parts[7]), parts[8] == "1", parts[9] == "1");
        }
        public void Dispose()
        {
            try { if (!_process.HasExited) { Send("quit"); if (!_process.WaitForExit(2000)) { _process.Kill(); _process.WaitForExit(2000); } } }
            finally { _process.Dispose(); }
        }
    }
    private sealed record ReadResult(string Status, uint Width, uint Height, uint Stride, ulong Sequence, ulong Generation, ulong Timestamp, ulong Hash, bool PaddingZero, bool PatternValid);
}
