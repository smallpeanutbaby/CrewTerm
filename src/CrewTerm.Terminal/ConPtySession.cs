using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CrewTerm.Terminal;

/// <summary>Owns a Windows pseudoconsole and its process tree independently of any UI.</summary>
public sealed class ConPtySession : IDisposable
{
    private readonly object _nativeGate = new();
    private readonly FileStream _input, _output;
    private readonly Thread _reader;
    private IntPtr _console, _process, _job;
    private bool _disposed;
    private int? _exitCode;

    public string Id { get; }
    public string CommandLine { get; }
    public string WorkingDirectory { get; }
    public int ProcessId { get; }
    public TerminalScreen Screen { get; }
    public event Action<string>? OutputReceived;
    public bool HasExited
    {
        get { lock (_nativeGate) { RefreshExitCode(); return _disposed || _exitCode.HasValue; } }
    }
    public int? ExitCode
    {
        get { lock (_nativeGate) { RefreshExitCode(); return _exitCode; } }
    }

    private ConPtySession(string id, string commandLine, string cwd, int columns, int rows, IntPtr console,
        ConPtyNative.ProcessInformation process, IntPtr job, IntPtr input, IntPtr output)
    {
        Id = id;
        CommandLine = commandLine;
        WorkingDirectory = cwd;
        ProcessId = process.ProcessId;
        Screen = new(columns, rows);
        _console = console;
        _process = process.Process;
        _job = job;
        _input = new(new SafeFileHandle(input, true), FileAccess.Write, 4096, false);
        _output = new(new SafeFileHandle(output, true), FileAccess.Read, 16384, false);
        Screen.ResponseRequested += Reply;
        _reader = new(ReadOutput) { IsBackground = true, Name = $"CrewTerm PTY {id}" };
        _reader.Start();
    }

    public static ConPtySession Start(string id, string commandLine, string workingDirectory, int columns, int rows)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native terminal sessions currently require Windows ConPTY.");
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
        TerminalScreen.ValidateSize(columns, rows);
        var cwd = Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(cwd)) throw new DirectoryNotFoundException(cwd);
        IntPtr inputRead = 0, inputWrite = 0, outputRead = 0, outputWrite = 0, console = 0, attributes = 0, job = 0;
        var process = new ConPtyNative.ProcessInformation();
        var attributesInitialized = false;
        try
        {
            Check(ConPtyNative.CreatePipe(out inputRead, out inputWrite, 0, 0));
            Check(ConPtyNative.CreatePipe(out outputRead, out outputWrite, 0, 0));
            Marshal.ThrowExceptionForHR(ConPtyNative.CreatePseudoConsole(new((short)columns, (short)rows), inputRead, outputWrite, 0, out console));
            IntPtr size = 0;
            ConPtyNative.InitializeProcThreadAttributeList(0, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            Check(ConPtyNative.InitializeProcThreadAttributeList(attributes, 1, 0, ref size));
            attributesInitialized = true;
            Check(ConPtyNative.UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x00020016, console, (IntPtr)IntPtr.Size, 0, 0));
            var startup = new ConPtyNative.StartupInfoEx { AttributeList = attributes };
            startup.StartupInfo.Size = Marshal.SizeOf<ConPtyNative.StartupInfoEx>();
            // Do not inherit redirected standard handles from a headless daemon/test host.
            // Null handles with STARTF_USESTDHANDLES let ConPTY supply its console handles.
            startup.StartupInfo.Flags = 0x00000100;
            // Suspended startup lets us attach a kill-on-close job before the program can spawn children.
            Check(ConPtyNative.CreateProcess(null, new StringBuilder(TerminalCommand.Resolve(commandLine, cwd)), 0, 0, false,
                0x00080000 | 0x00000004, 0, cwd, ref startup, out process));
            job = ConPtyNative.CreateJobObject(0, null);
            if (job == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits = new ConPtyNative.ExtendedLimitInformation();
            limits.Basic.LimitFlags = 0x00002000;
            Check(ConPtyNative.SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<ConPtyNative.ExtendedLimitInformation>()));
            Check(ConPtyNative.AssignProcessToJobObject(job, process.Process));
            var session = new ConPtySession(id, commandLine, cwd, columns, rows, console, process, job, inputWrite, outputRead);
            console = job = inputWrite = outputRead = 0;
            process.Process = 0;
            if (ConPtyNative.ResumeThread(process.Thread) == uint.MaxValue)
            {
                session.Dispose();
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return session;
        }
        finally
        {
            if (process.Process != 0) { ConPtyNative.TerminateProcess(process.Process, 1); ConPtyNative.CloseHandle(process.Process); }
            if (process.Thread != 0) ConPtyNative.CloseHandle(process.Thread);
            if (attributes != 0)
            {
                if (attributesInitialized) ConPtyNative.DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
            if (job != 0) ConPtyNative.CloseHandle(job);
            if (console != 0) ConPtyNative.ClosePseudoConsole(console);
            foreach (var handle in new[] { inputRead, inputWrite, outputRead, outputWrite })
                if (handle != 0) ConPtyNative.CloseHandle(handle);
        }
    }

    public void Write(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_nativeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RefreshExitCode();
            if (_exitCode.HasValue) throw new InvalidOperationException("The terminal process has exited.");
            _input.Write(Encoding.UTF8.GetBytes(text));
            _input.Flush();
        }
    }

    public void Resize(int columns, int rows)
    {
        TerminalScreen.ValidateSize(columns, rows);
        lock (_nativeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Marshal.ThrowExceptionForHR(ConPtyNative.ResizePseudoConsole(_console, new((short)columns, (short)rows)));
            Screen.Resize(columns, rows);
        }
    }

    private void RefreshExitCode()
    {
        if (_process != 0 && !_exitCode.HasValue && ConPtyNative.WaitForSingleObject(_process, 0) == 0
            && ConPtyNative.GetExitCodeProcess(_process, out var code)) _exitCode = unchecked((int)code);
    }

    private void Reply(string text)
    {
        try { Write(text); }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private void ReadOutput()
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[16384];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        try
        {
            int count;
            while ((count = _output.Read(bytes)) != 0)
            {
                var text = new string(chars, 0, decoder.GetChars(bytes, 0, count, chars, 0, false));
                Screen.Feed(text);
                if (OutputReceived is { } handler)
                    foreach (Action<string> callback in handler.GetInvocationList())
                        try { callback(text); } catch (Exception) { /* Client callbacks must not stop terminal draining. */ }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }

    public void Dispose()
    {
        IntPtr console;
        lock (_nativeGate)
        {
            if (_disposed) return;
            RefreshExitCode();
            _disposed = true;
            if (_job != 0) ConPtyNative.TerminateJobObject(_job, 1);
            if (_process != 0) { ConPtyNative.WaitForSingleObject(_process, 2000); RefreshExitCode(); }
            _input.Dispose();
            console = _console;
            _console = 0;
        }
        // Keep draining on the reader thread while ConPTY flushes its final terminal state.
        if (console != 0) ConPtyNative.ClosePseudoConsole(console);
        if (Thread.CurrentThread != _reader) _reader.Join(2000);
        _output.Dispose();
        lock (_nativeGate)
        {
            if (_process != 0) ConPtyNative.CloseHandle(_process);
            if (_job != 0) ConPtyNative.CloseHandle(_job);
            _process = _job = 0;
        }
        Screen.ResponseRequested -= Reply;
    }

    private static void Check(bool result)
    {
        if (!result) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}
