using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Orchard.NativeHost.Windows;

/// <summary>
/// Creates a native child with an atomic Job Object assignment, an inherited-handle allowlist, and
/// a newly constructed environment. The initial thread remains suspended until membership is
/// verified.
/// </summary>
internal static class WindowsNativeProcessLauncher
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateNoWindow = 0x08000000;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;
    private const nuint ProcThreadAttributeHandleList = 0x00020002;
    private const nuint ProcThreadAttributeJobList = 0x0002000D;
    private const int MaximumEnvironmentBlockCharacters = 32_767;
    private const int MaximumCommandLineCharacters = 32_767;
    private const int ErrorInsufficientBuffer = 122;

    private static readonly HashSet<string> LauncherControlledEnvironmentVariables =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "PATH",
            "SystemDrive",
            "SystemRoot",
            "TEMP",
            "TMP"
        };

    internal static bool IsLauncherControlledEnvironmentVariable(string name) =>
        LauncherControlledEnvironmentVariables.Contains(name);

    public static LaunchedNativeChild Start(ValidatedNativeHostLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The Orchard native Windows launcher requires Windows.");
        }

        using var job = WindowsJobObject.Create(options);
        SafeFileHandle? stdinRead = null;
        SafeFileHandle? stdinWrite = null;
        SafeFileHandle? stdoutRead = null;
        SafeFileHandle? stdoutWrite = null;
        SafeFileHandle? stderrRead = null;
        SafeFileHandle? stderrWrite = null;
        FileStream? standardInput = null;
        FileStream? standardOutput = null;
        FileStream? standardError = null;
        Process? process = null;

        try
        {
            CreateRedirectedPipe(out stdinRead, out stdinWrite, parentReads: false);
            CreateRedirectedPipe(out stdoutRead, out stdoutWrite, parentReads: true);
            CreateRedirectedPipe(out stderrRead, out stderrWrite, parentReads: true);

            var childHandles = new[]
            {
                stdinRead.DangerousGetHandle(),
                stdoutWrite.DangerousGetHandle(),
                stderrWrite.DangerousGetHandle()
            };

            using var attributes = ProcessThreadAttributeList.Create(childHandles, job.DangerousGetHandle());
            using var environment = NativeEnvironmentBlock.Create(options);
            var commandLine = BuildCommandLine(options.ExecutablePath, options.Arguments);
            var startupInfo = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = StartfUseStdHandles,
                    StandardInput = stdinRead.DangerousGetHandle(),
                    StandardOutput = stdoutWrite.DangerousGetHandle(),
                    StandardError = stderrWrite.DangerousGetHandle()
                },
                AttributeList = attributes.DangerousGetHandle()
            };

            if (!CreateProcess(
                options.ExecutablePath,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                inheritHandles: true,
                CreateSuspended | CreateUnicodeEnvironment | ExtendedStartupInfoPresent | CreateNoWindow,
                environment.DangerousGetHandle(),
                options.WorkingDirectory,
                ref startupInfo,
                out var processInformation))
            {
                throw NativeError("Windows could not create the native child process");
            }

            using var createdProcessHandle = new SafeFileHandle(processInformation.Process, ownsHandle: true);
            using var createdThreadHandle = new SafeFileHandle(processInformation.Thread, ownsHandle: true);
            try
            {
                if (!IsProcessInJob(
                    createdProcessHandle.DangerousGetHandle(),
                    job.DangerousGetHandle(),
                    out var assignedToJob))
                {
                    throw NativeError("Windows could not verify native-child Job Object membership");
                }
                if (!assignedToJob)
                {
                    throw new InvalidOperationException(
                        "The native child was not assigned to Orchard's Job Object before execution.");
                }

                process = Process.GetProcessById(checked((int)processInformation.ProcessId));
                process.EnableRaisingEvents = true;

                standardInput = new FileStream(stdinWrite, FileAccess.Write, 4_096, isAsync: false);
                stdinWrite = null;
                standardOutput = new FileStream(stdoutRead, FileAccess.Read, 4_096, isAsync: false);
                stdoutRead = null;
                standardError = new FileStream(stderrRead, FileAccess.Read, 4_096, isAsync: false);
                stderrRead = null;

                // Parent copies of the child endpoints must be closed before execution. Otherwise
                // EOF could be held open after the child exits.
                stdinRead.Dispose();
                stdinRead = null;
                stdoutWrite.Dispose();
                stdoutWrite = null;
                stderrWrite.Dispose();
                stderrWrite = null;

                if (ResumeThread(createdThreadHandle.DangerousGetHandle()) == uint.MaxValue)
                {
                    throw NativeError("Windows could not resume the verified native child process");
                }
            }
            catch
            {
                _ = TerminateProcess(createdProcessHandle.DangerousGetHandle(), 1);
                _ = WaitForSingleObject(createdProcessHandle.DangerousGetHandle(), 2_000);
                throw;
            }

            var launched = new LaunchedNativeChild(
                process,
                standardInput,
                standardOutput,
                standardError,
                job.TransferOwnership());
            process = null;
            standardInput = null;
            standardOutput = null;
            standardError = null;
            return launched;
        }
        finally
        {
            stdinRead?.Dispose();
            stdinWrite?.Dispose();
            stdoutRead?.Dispose();
            stdoutWrite?.Dispose();
            stderrRead?.Dispose();
            stderrWrite?.Dispose();
            standardInput?.Dispose();
            standardOutput?.Dispose();
            standardError?.Dispose();
            process?.Dispose();
        }
    }

    private static void CreateRedirectedPipe(
        out SafeFileHandle childEnd,
        out SafeFileHandle parentEnd,
        bool parentReads)
    {
        var securityAttributes = new SecurityAttributes
        {
            Length = Marshal.SizeOf<SecurityAttributes>(),
            InheritHandle = true
        };
        if (!CreatePipe(out var read, out var write, ref securityAttributes, 0))
        {
            throw NativeError("Windows could not create a redirected child pipe");
        }

        if (parentReads)
        {
            childEnd = write;
            parentEnd = read;
        }
        else
        {
            childEnd = read;
            parentEnd = write;
        }

        if (!SetHandleInformation(parentEnd, HandleFlagInherit, 0))
        {
            childEnd.Dispose();
            parentEnd.Dispose();
            throw NativeError("Windows could not remove inheritance from a parent pipe handle");
        }
    }

    private static StringBuilder BuildCommandLine(string executablePath, IReadOnlyList<string> arguments)
    {
        var commandLine = new StringBuilder(QuoteArgument(executablePath));
        foreach (var argument in arguments)
        {
            commandLine.Append(' ');
            commandLine.Append(QuoteArgument(argument));
        }

        if (commandLine.Length >= MaximumCommandLineCharacters)
        {
            throw new ArgumentException(
                $"The native child command line must be shorter than {MaximumCommandLineCharacters} characters.",
                nameof(arguments));
        }
        return commandLine;
    }

    // This is the CommandLineToArgvW/C runtime inverse: runs of backslashes are doubled only when
    // they precede a quote or the closing quote.
    private static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && argument.All(character =>
            character is not (' ' or '\t' or '\n' or '\v' or '"')))
        {
            return argument;
        }

        var result = new StringBuilder(argument.Length + 2);
        result.Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', checked((backslashes * 2) + 1));
                result.Append('"');
                backslashes = 0;
                continue;
            }

            result.Append('\\', backslashes);
            result.Append(character);
            backslashes = 0;
        }

        result.Append('\\', checked(backslashes * 2));
        result.Append('"');
        return result.ToString();
    }

    private static Win32Exception NativeError(string message) =>
        new(Marshal.GetLastWin32Error(), message);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;

        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public uint Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    private sealed class ProcessThreadAttributeList : IDisposable
    {
        private IntPtr _attributeList;
        private IntPtr _handleList;
        private IntPtr _jobList;

        private ProcessThreadAttributeList(IntPtr attributeList, IntPtr handleList, IntPtr jobList)
        {
            _attributeList = attributeList;
            _handleList = handleList;
            _jobList = jobList;
        }

        public IntPtr DangerousGetHandle() => _attributeList;

        public static ProcessThreadAttributeList Create(IntPtr[] inheritedHandles, IntPtr jobHandle)
        {
            nuint requiredBytes = 0;
            _ = InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref requiredBytes);
            if (requiredBytes == 0 || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
            {
                throw NativeError("Windows could not size the native-child startup attributes");
            }

            var attributeList = Marshal.AllocHGlobal(checked((nint)requiredBytes));
            var handleList = Marshal.AllocHGlobal(checked(inheritedHandles.Length * IntPtr.Size));
            var jobList = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                if (!InitializeProcThreadAttributeList(attributeList, 2, 0, ref requiredBytes))
                {
                    throw NativeError("Windows could not initialize native-child startup attributes");
                }

                Marshal.Copy(inheritedHandles, 0, handleList, inheritedHandles.Length);
                Marshal.WriteIntPtr(jobList, jobHandle);
                if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    ProcThreadAttributeHandleList,
                    handleList,
                    checked((nuint)(inheritedHandles.Length * IntPtr.Size)),
                    IntPtr.Zero,
                    IntPtr.Zero))
                {
                    throw NativeError("Windows could not apply the native-child inherited-handle allowlist");
                }
                if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    ProcThreadAttributeJobList,
                    jobList,
                    checked((nuint)IntPtr.Size),
                    IntPtr.Zero,
                    IntPtr.Zero))
                {
                    throw NativeError("Windows could not apply the native-child Job Object at creation");
                }

                return new ProcessThreadAttributeList(attributeList, handleList, jobList);
            }
            catch
            {
                if (attributeList != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(attributeList);
                }
                Marshal.FreeHGlobal(jobList);
                Marshal.FreeHGlobal(handleList);
                Marshal.FreeHGlobal(attributeList);
                throw;
            }
        }

        public void Dispose()
        {
            var attributeList = Interlocked.Exchange(ref _attributeList, IntPtr.Zero);
            if (attributeList == IntPtr.Zero)
            {
                return;
            }

            DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(Interlocked.Exchange(ref _jobList, IntPtr.Zero));
            Marshal.FreeHGlobal(Interlocked.Exchange(ref _handleList, IntPtr.Zero));
            Marshal.FreeHGlobal(attributeList);
        }
    }

    private sealed class NativeEnvironmentBlock : IDisposable
    {
        private IntPtr _value;

        private NativeEnvironmentBlock(IntPtr value)
        {
            _value = value;
        }

        public IntPtr DangerousGetHandle() => _value;

        public static NativeEnvironmentBlock Create(ValidatedNativeHostLaunchOptions options)
        {
            var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (string.IsNullOrWhiteSpace(windowsDirectory) || !Path.IsPathFullyQualified(windowsDirectory))
            {
                throw new DirectoryNotFoundException("Windows did not report a valid system directory.");
            }

            var systemDirectory = Environment.SystemDirectory;
            var temporaryDirectory = Path.GetFullPath(Path.GetTempPath());
            var pathEntries = options.RuntimeSearchPaths
                .Append(systemDirectory)
                .Append(windowsDirectory)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PATH"] = string.Join(Path.PathSeparator, pathEntries),
                ["SystemDrive"] = Path.GetPathRoot(windowsDirectory)?.TrimEnd(Path.DirectorySeparatorChar)
                    ?? throw new DirectoryNotFoundException("Windows did not report a valid system drive."),
                ["SystemRoot"] = windowsDirectory,
                ["TEMP"] = temporaryDirectory,
                ["TMP"] = temporaryDirectory
            };
            foreach (var pair in options.EnvironmentVariables)
            {
                variables.Add(pair.Key, pair.Value);
            }

            var text = string.Join('\0', variables.Select(pair => $"{pair.Key}={pair.Value}")) + "\0\0";
            if (text.Length > MaximumEnvironmentBlockCharacters)
            {
                throw new ArgumentException(
                    $"The explicit child environment cannot exceed {MaximumEnvironmentBlockCharacters} characters.",
                    nameof(options));
            }

            return new NativeEnvironmentBlock(Marshal.StringToHGlobalUni(text));
        }

        public void Dispose()
        {
            var value = Interlocked.Exchange(ref _value, IntPtr.Zero);
            if (value != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(value);
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(
        out SafeFileHandle readPipe,
        out SafeFileHandle writePipe,
        ref SecurityAttributes pipeAttributes,
        int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(
        SafeFileHandle handle,
        uint mask,
        uint flags);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr attributeList,
        int attributeCount,
        int flags,
        ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr attributeList,
        uint flags,
        nuint attribute,
        IntPtr value,
        nuint size,
        IntPtr previousValue,
        IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(
        IntPtr processHandle,
        IntPtr jobHandle,
        [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr threadHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr processHandle, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
}

internal sealed class WindowsJobObject : IDisposable
{
    private const int ExtendedLimitInformationClass = 9;
    private const uint LimitActiveProcess = 0x00000008;
    private const uint LimitProcessMemory = 0x00000100;
    private const uint LimitJobMemory = 0x00000200;
    private const uint LimitDieOnUnhandledException = 0x00000400;
    private const uint LimitKillOnJobClose = 0x00002000;

    private SafeFileHandle? _handle;

    private WindowsJobObject(SafeFileHandle handle)
    {
        _handle = handle;
    }

    public IntPtr DangerousGetHandle() =>
        _handle?.DangerousGetHandle() ?? throw new ObjectDisposedException(nameof(WindowsJobObject));

    public static WindowsJobObject Create(ValidatedNativeHostLaunchOptions options)
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "Windows could not create the native-child Job Object.");
        }

        try
        {
            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = LimitActiveProcess |
                        LimitProcessMemory |
                        LimitJobMemory |
                        LimitDieOnUnhandledException |
                        LimitKillOnJobClose,
                    ActiveProcessLimit = checked((uint)options.ActiveProcessLimit)
                },
                ProcessMemoryLimit = checked((nuint)options.ProcessMemoryLimitBytes),
                JobMemoryLimit = checked((nuint)options.JobMemoryLimitBytes)
            };
            var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(limits, buffer, fDeleteOld: false);
                if (!SetInformationJobObject(handle, ExtendedLimitInformationClass, buffer, checked((uint)size)))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Windows could not apply native-child Job Object resource limits.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return new WindowsJobObject(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public WindowsJobObject TransferOwnership()
    {
        var handle = Interlocked.Exchange(ref _handle, null)
            ?? throw new ObjectDisposedException(nameof(WindowsJobObject));
        return new WindowsJobObject(handle);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _handle, null)?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        int informationClass,
        IntPtr information,
        uint informationLength);
}

internal sealed class LaunchedNativeChild : IDisposable
{
    private readonly WindowsJobObject _job;
    private int _jobClosed;
    private int _disposed;

    public LaunchedNativeChild(
        Process process,
        FileStream standardInput,
        FileStream standardOutput,
        FileStream standardError,
        WindowsJobObject job)
    {
        Process = process;
        StandardInput = standardInput;
        StandardOutput = standardOutput;
        StandardError = standardError;
        _job = job;
    }

    public Process Process { get; }

    public FileStream StandardInput { get; }

    public FileStream StandardOutput { get; }

    public FileStream StandardError { get; }

    public void CloseJobObject()
    {
        if (Interlocked.Exchange(ref _jobClosed, 1) == 0)
        {
            _job.Dispose();
        }
    }

    public async Task TerminateAndReapAsync()
    {
        CloseJobObject();
        if (!Process.HasExited)
        {
            try
            {
                Process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                // Job close raced normal process exit or Windows had already torn down the handle.
            }
        }

        if (!Process.HasExited)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await Process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cleanup remains bounded even if the OS has not reported the exit yet.
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CloseJobObject();
        StandardInput.Dispose();
        StandardOutput.Dispose();
        StandardError.Dispose();
        Process.Dispose();
    }
}
