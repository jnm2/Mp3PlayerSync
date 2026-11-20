using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.JobObjects;
using Windows.Win32.System.StationsAndDesktops;
using Windows.Win32.System.Threading;

namespace Mp3PlayerSync.EndToEnd.Launcher;

/// <summary>
/// The tests' process, on a desktop of its own (or this one), in a job object with everything it starts: the job tells
/// exactly which processes are the run's, and closing it ends any it left behind, and nothing else.
/// </summary>
internal sealed unsafe class TestProcess : IDisposable
{
    private readonly CloseDesktopSafeHandle? desktop;
    private readonly SafeFileHandle job;
    private readonly SafeFileHandle process;
    private readonly AnonymousPipeServerStream output;

    private TestProcess(CloseDesktopSafeHandle? desktop, SafeFileHandle job, SafeFileHandle process, int processId, AnonymousPipeServerStream output)
    {
        this.desktop = desktop;
        this.job = job;
        this.process = process;
        this.output = output;
        ProcessId = processId;
    }

    public int ProcessId { get; }

    /// <summary>What the process writes to its standard output and error, combined.</summary>
    public Stream Output => output;

    /// <summary>
    /// Starts <paramref name="commandLine"/> suspended, puts it in a new job object, then lets it run. On a new desktop
    /// called <paramref name="desktopName"/> on this window station, or on this one when it's null.
    /// </summary>
    public static TestProcess Start(string application, string commandLine, string? desktopName)
    {
        CloseDesktopSafeHandle? desktop = null;
        SafeFileHandle? job = null;
        var output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        try
        {
            if (desktopName is not null)
            {
                desktop = PInvoke.CreateDesktop(desktopName, 0, (uint)GENERIC_ACCESS_RIGHTS.GENERIC_ALL, null);
                if (desktop.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateDesktop failed");
            }

            job = PInvoke.CreateJobObject(null, (string?)null);
            if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateJobObject failed");
            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = { LimitFlags = JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
            };
            if (!PInvoke.SetInformationJobObject(job, JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, new ReadOnlySpan<byte>(&limits, sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION))))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetInformationJobObject failed");

            var outputHandle = (HANDLE)output.ClientSafePipeHandle.DangerousGetHandle();
            fixed (char* desktopPath = desktopName is null ? null : $@"WinSta0\{desktopName}")
            {
                var startup = new STARTUPINFOW
                {
                    cb = (uint)sizeof(STARTUPINFOW),
                    lpDesktop = desktopPath,
                    dwFlags = STARTUPINFOW_FLAGS.STARTF_USESTDHANDLES,
                    hStdOutput = outputHandle,
                    hStdError = outputHandle,
                };
                var command = (commandLine + '\0').ToCharArray().AsSpan();
                PROCESS_INFORMATION info;
                if (!PInvoke.CreateProcess(application, ref command, null, null, true, PROCESS_CREATION_FLAGS.CREATE_SUSPENDED | PROCESS_CREATION_FLAGS.CREATE_NO_WINDOW, null, Path.GetDirectoryName(application), in startup, out info))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateProcess failed");

                var process = new SafeFileHandle(info.hProcess, ownsHandle: true);
                try
                {
                    if (!PInvoke.AssignProcessToJobObject(job, process))
                    {
                        var error = Marshal.GetLastPInvokeError();
                        PInvoke.TerminateProcess(process, 1);
                        throw new Win32Exception(error, "AssignProcessToJobObject failed");
                    }
                    PInvoke.ResumeThread(info.hThread);
                }
                catch
                {
                    process.Dispose();
                    throw;
                }
                finally
                {
                    PInvoke.CloseHandle(info.hThread);
                }

                // Only the child keeps the writing end, so the output ends when the last process holding it does.
                output.DisposeLocalCopyOfClientHandle();
                return new TestProcess(desktop, job, process, (int)info.dwProcessId, output);
            }
        }
        catch
        {
            output.Dispose();
            job?.Dispose();
            desktop?.Dispose();
            throw;
        }
    }

    public bool WaitForExit(TimeSpan timeout) =>
        PInvoke.WaitForSingleObject(process, (uint)Math.Clamp(timeout.TotalMilliseconds, 0, uint.MaxValue - 1)) == WAIT_EVENT.WAIT_OBJECT_0;

    public int? ExitCode => PInvoke.GetExitCodeProcess(process, out var code) && code != (uint)NTSTATUS.STILL_ACTIVE ? (int)code : null;

    /// <summary>Whether the process is the tests' process or one it started, directly or not.</summary>
    public bool Contains(int processId)
    {
        using var other = PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        return !other.IsInvalid && PInvoke.IsProcessInJob(other, job, out var result) && result;
    }

    /// <summary>How many of the run's processes are still running.</summary>
    public int RunningProcesses
    {
        get
        {
            JOBOBJECT_BASIC_ACCOUNTING_INFORMATION accounting;
            return PInvoke.QueryInformationJobObject(job, JOBOBJECTINFOCLASS.JobObjectBasicAccountingInformation, new Span<byte>(&accounting, sizeof(JOBOBJECT_BASIC_ACCOUNTING_INFORMATION)))
                ? (int)accounting.ActiveProcesses
                : 0;
        }
    }

    /// <summary>Ends every process of the run.</summary>
    public void Terminate() => PInvoke.TerminateJobObject(job, 1);

    public void Dispose()
    {
        // Closing the job ends whatever is left in it (KILL_ON_JOB_CLOSE).
        job.Dispose();
        process.Dispose();
        output.Dispose();
        desktop?.Dispose();
    }
}
