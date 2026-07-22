using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VSDebuggerMCP.Ipc
{
    internal sealed class LengthPrefixedFrameCodec
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private readonly Stream _stream;

        public LengthPrefixedFrameCodec(Stream stream)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        }

        public async Task<string> ReadFrameAsync(int maxBytes, CancellationToken cancellationToken)
        {
            var header = new byte[4];
            int headerBytes = await ReadExactAsync(header, allowCleanEof: true, cancellationToken);
            if (headerBytes == 0) return null;

            int length = (header[0] << 24) |
                         (header[1] << 16) |
                         (header[2] << 8) |
                         header[3];
            if (length <= 0 || length > maxBytes)
            {
                throw new IOException($"Invalid frame length {length}; allowed range is 1..{maxBytes} bytes");
            }

            var payload = new byte[length];
            await ReadExactAsync(payload, allowCleanEof: false, cancellationToken);
            return StrictUtf8.GetString(payload);
        }

        public async Task WriteFrameAsync(string payload, int maxBytes, CancellationToken cancellationToken)
        {
            byte[] bytes = StrictUtf8.GetBytes(payload ?? string.Empty);
            if (bytes.Length <= 0 || bytes.Length > maxBytes)
            {
                throw new IOException($"Invalid response frame length {bytes.Length}; allowed range is 1..{maxBytes} bytes");
            }

            var header = new[]
            {
                (byte)(bytes.Length >> 24),
                (byte)(bytes.Length >> 16),
                (byte)(bytes.Length >> 8),
                (byte)bytes.Length,
            };
            await _stream.WriteAsync(header, 0, header.Length, cancellationToken);
            await _stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
        }

        private async Task<int> ReadExactAsync(
            byte[] buffer,
            bool allowCleanEof,
            CancellationToken cancellationToken)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = await _stream.ReadAsync(
                    buffer,
                    offset,
                    buffer.Length - offset,
                    cancellationToken);
                if (read == 0)
                {
                    if (allowCleanEof && offset == 0) return 0;
                    throw new IOException("Pipe closed before the complete frame was received");
                }
                offset += read;
            }
            return offset;
        }
    }
}
