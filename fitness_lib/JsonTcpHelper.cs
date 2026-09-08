using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace fitness_lib;

public static class JsonTcpHelper
{
    public static async Task<T> ReceiveAsync<T>(NetworkStream stream)
    {
        byte[] buffer = new byte[4096];

        int bytesRead = await stream.ReadAsync(buffer);

        string json = Encoding.UTF8.GetString(
            buffer,
            0,
            bytesRead
        );

        T? result = JsonSerializer.Deserialize<T>(json);

        if (result == null)
            throw new Exception("Не удалось десериализовать JSON.");

        return result;
    }

    public static async Task SendAsync<T>(
        NetworkStream stream,
        T data)
    {
        string json = JsonSerializer.Serialize(data);

        byte[] bytes = Encoding.UTF8.GetBytes(json);

        await stream.WriteAsync(bytes);
    }
}