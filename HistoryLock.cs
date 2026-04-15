using System.Text;

namespace nb;

/// <summary>
/// Single-writer lock for the per-directory conversation history file.
/// Relies on the OS to release the underlying handle on process termination (including SIGKILL),
/// and on FileOptions.DeleteOnClose to unlink the lock file — so there are no stale locks to clean up.
/// </summary>
public sealed class HistoryLock : IDisposable
{
    private FileStream? _stream;

    public string LockPath { get; }
    public bool IsOwner => _stream != null;
    public int? OwnerPid { get; private set; }

    public HistoryLock(string lockPath)
    {
        LockPath = lockPath;
    }

    public bool TryAcquire()
    {
        try
        {
            _stream = new FileStream(
                LockPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 256,
                FileOptions.DeleteOnClose);

            var pid = Environment.ProcessId;
            var bytes = Encoding.UTF8.GetBytes(pid.ToString());
            _stream.Write(bytes);
            _stream.Flush();
            OwnerPid = pid;
            return true;
        }
        catch (IOException)
        {
            OwnerPid = TryReadOwnerPid();
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            OwnerPid = TryReadOwnerPid();
            return false;
        }
    }

    private int? TryReadOwnerPid()
    {
        try
        {
            using var fs = new FileStream(LockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            var content = sr.ReadToEnd().Trim();
            return int.TryParse(content, out var pid) ? pid : null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }
}
