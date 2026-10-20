using System.Text;

namespace NetShareHub.Infrastructure.Services.Dispatch;

/// <summary>Lecteur à tampon unique : permet de « regarder » le premier octet pour détecter le protocole, et de récupérer ce qui a été lu en trop.</summary>
internal sealed class ProxyReader
{
    private const int Capacity = 16 * 1024;

    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[Capacity];
    private int _start;
    private int _end;

    public ProxyReader(Stream stream)
    {
        _stream = stream;
    }

    /// <summary>Octets déjà lus depuis le réseau mais pas encore consommés.</summary>
    public ReadOnlyMemory<byte> Buffered => _buffer.AsMemory(_start, _end - _start);

    public void Consume(int count) => _start += count;

    /// <summary>Premier octet disponible sans le consommer, ou -1 si le client a fermé.</summary>
    public async Task<int> PeekByteAsync(CancellationToken cancellationToken)
    {
        if (_start == _end && !await FillAsync(cancellationToken))
        {
            return -1;
        }

        return _buffer[_start];
    }

    public async Task<byte> ReadByteAsync(CancellationToken cancellationToken)
        => (await ReadExactAsync(1, cancellationToken))[0];

    public async Task<byte[]> ReadExactAsync(int count, CancellationToken cancellationToken)
    {
        var result = new byte[count];
        var copied = 0;
        while (copied < count)
        {
            if (_start == _end && !await FillAsync(cancellationToken))
            {
                throw new EndOfStreamException();
            }

            var chunk = Math.Min(count - copied, _end - _start);
            Array.Copy(_buffer, _start, result, copied, chunk);
            _start += chunk;
            copied += chunk;
        }

        return result;
    }

    /// <summary>Lit l'en-tête HTTP complet (jusqu'à la ligne vide incluse) et le renvoie sans le CRLF final.</summary>
    public async Task<string> ReadHeadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var span = _buffer.AsSpan(_start, _end - _start);
            var index = span.IndexOf("\r\n\r\n"u8);
            if (index >= 0)
            {
                var head = Encoding.Latin1.GetString(span[..index]);
                _start += index + 4;
                return head;
            }

            if (_end - _start >= Capacity)
            {
                throw new InvalidDataException("En-tête HTTP trop volumineux.");
            }

            if (!await FillAsync(cancellationToken))
            {
                throw new EndOfStreamException();
            }
        }
    }

    private async Task<bool> FillAsync(CancellationToken cancellationToken)
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }

        if (_end == Capacity)
        {
            return true;
        }

        var read = await _stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken);
        if (read == 0)
        {
            return false;
        }

        _end += read;
        return true;
    }
}
