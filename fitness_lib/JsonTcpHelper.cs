using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace fitness_lib;

/// <summary>
/// Передача JSON по TCP с префиксом длины сообщения.
/// Это предотвращает проблему, когда один ReadAsync получает
/// только часть JSON или сразу несколько сообщений.
/// </summary>
public static class JsonTcpHelper
{
    public static async Task<T> ReceiveAsync<T>(NetworkStream stream)
    {
        byte[] lengthBuffer = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, lengthBuffer);

        int payloadLength = BitConverter.ToInt32(lengthBuffer, 0);

        if (payloadLength <= 0 || payloadLength > 1024 * 1024)
            throw new InvalidDataException("Некорректный размер JSON-сообщения.");

        byte[] payload = new byte[payloadLength];
        await ReadExactlyAsync(stream, payload);

        string json = Encoding.UTF8.GetString(payload);
        T? result = JsonSerializer.Deserialize<T>(json);

        if (result == null)
            throw new InvalidDataException("Не удалось десериализовать JSON.");

        return result;
    }

    public static async Task SendAsync<T>(NetworkStream stream, T data)
    {
        string json = JsonSerializer.Serialize(data);
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] length = BitConverter.GetBytes(payload.Length);

        await stream.WriteAsync(length);
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
    }

    private static async Task ReadExactlyAsync(
        NetworkStream stream,
        byte[] buffer)
    {
        int offset = 0;

        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(
                buffer.AsMemory(offset, buffer.Length - offset));

            if (read == 0)
                throw new EndOfStreamException("Соединение закрыто удалённой стороной.");

            offset += read;
        }
    }
}
