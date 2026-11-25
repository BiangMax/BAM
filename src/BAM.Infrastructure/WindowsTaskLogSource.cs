using System.Runtime.InteropServices;
using System.Text;
using BAM.Core;
using Microsoft.Win32.SafeHandles;

namespace BAM.Infrastructure;

public sealed class WindowsTaskLogSource : ITaskLogSource
{
    public ITaskLogTail Follow(string path) => new Tail(WindowsImagePath.ExpandShortNames(Path.GetFullPath(path)));

    private sealed class Tail(string path) : ITaskLogTail
    {
        private const int ReadLimit = 128 * 1024;
        private const int AnchorSize = 64;
        private static readonly UTF8Encoding Utf8 = new(false, true);
        private readonly byte[] _line = new byte[64 * 1024];
        private FileIdentity? _file;
        private long _offset;
        private byte[] _anchor = [];
        private int _lineLength;
        private bool _discardLine;

        public async ValueTask<TaskLogBatch> ReadAsync(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            // Writers may keep the log open or rename it while BAM samples.
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous);
            if (!string.Equals(WindowsImagePath.Resolve(stream.SafeFileHandle), path, StringComparison.OrdinalIgnoreCase))
                throw new IOException("无法确认任务日志的实际路径。");
            if (!GetFileInformationByHandleEx(stream.SafeFileHandle, 18, out var file, Marshal.SizeOf<FileIdentity>()))
                throw new IOException($"无法确认任务日志文件身份（Windows {Marshal.GetLastPInvokeError()}）。");

            var end = stream.Length;
            if (_file != file || end < _offset || !await HasAnchorAsync(stream, cancellation).ConfigureAwait(false))
            {
                // A replacement/copy-truncate may contain replayed events. Attach at its end.
                await AttachAsync(stream, file, end, cancellation).ConfigureAwait(false);
                return new([], Discontinuity: true, CaughtUp: true);
            }

            stream.Position = _offset;
            List<string> lines = [];
            bool discontinuity = false;
            int remaining = (int)Math.Min(end - _offset, ReadLimit);
            var buffer = new byte[Math.Min(remaining, 8192)];
            while (remaining > 0)
            {
                int count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellation)
                    .ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException();
                Consume(buffer.AsSpan(0, count), lines, ref discontinuity);
                remaining -= count;
            }

            var nextOffset = stream.Position;
            var nextAnchor = await ReadAnchorAsync(stream, nextOffset, cancellation).ConfigureAwait(false);
            if (!await HasAnchorAsync(stream, cancellation).ConfigureAwait(false))
                throw new IOException("读取任务日志时文件内容发生替换。");
            _offset = nextOffset;
            _anchor = nextAnchor;
            return new(lines, discontinuity, _offset == stream.Length);
        }

        private async ValueTask<bool> HasAnchorAsync(FileStream stream, CancellationToken cancellation)
        {
            var current = await ReadAnchorAsync(stream, _offset, cancellation).ConfigureAwait(false);
            return _anchor.AsSpan().SequenceEqual(current);
        }

        private async Task AttachAsync(FileStream stream, FileIdentity file, long end, CancellationToken cancellation)
        {
            var anchor = await ReadAnchorAsync(stream, end, cancellation).ConfigureAwait(false);
            _file = file;
            _offset = end;
            _anchor = anchor;
            _lineLength = 0;
            _discardLine = anchor.Length > 0 && anchor[^1] != (byte)'\n';
        }

        private void Consume(ReadOnlySpan<byte> bytes, List<string> lines, ref bool discontinuity)
        {
            foreach (byte value in bytes)
            {
                if (value == (byte)'\n')
                {
                    if (!_discardLine && _lineLength > 0)
                    {
                        int length = _line[_lineLength - 1] == (byte)'\r' ? _lineLength - 1 : _lineLength;
                        try { lines.Add(Utf8.GetString(_line, 0, length)); }
                        catch (DecoderFallbackException)
                        {
                            lines.Clear();
                            discontinuity = true;
                        }
                    }
                    _lineLength = 0;
                    _discardLine = false;
                }
                else if (!_discardLine)
                {
                    if (_lineLength < _line.Length) _line[_lineLength++] = value;
                    else
                    {
                        _discardLine = true;
                        lines.Clear();
                        discontinuity = true;
                    }
                }
            }
        }

        private static async ValueTask<byte[]> ReadAnchorAsync(FileStream stream, long offset, CancellationToken cancellation)
        {
            int length = (int)Math.Min(offset, AnchorSize);
            if (length == 0) return [];
            var anchor = new byte[length];
            stream.Position = offset - length;
            await stream.ReadExactlyAsync(anchor, cancellation).ConfigureAwait(false);
            return anchor;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct FileIdentity(ulong Volume, ulong IdLow, ulong IdHigh);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass,
        out FileIdentity information, int size);
}
