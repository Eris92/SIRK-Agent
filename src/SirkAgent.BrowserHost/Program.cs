using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

const int maximumMessageBytes = 256 * 1024;
var input = Console.OpenStandardInput();
var output = Console.OpenStandardOutput();
var header = new byte[4];

while (await ReadExactlyAsync(input, header, CancellationToken.None))
{
    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
    if (length <= 0 || length > maximumMessageBytes)
    {
        await WriteAsync(output, JsonSerializer.Serialize(new
        {
            ok = false,
            code = "BROWSER_MESSAGE_SIZE_INVALID"
        }));
        continue;
    }
    var body = new byte[length];
    if (!await ReadExactlyAsync(input, body, CancellationToken.None))
        break;
    string response;
    try
    {
        using var document = JsonDocument.Parse(body);
        await using var pipe = new NamedPipeClientStream(".", "SIRK-Agent-Browser-Bridge",
            PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await pipe.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true)
            { AutoFlush = true };
        await writer.WriteLineAsync(document.RootElement.GetRawText());
        response = await reader.ReadLineAsync(timeout.Token) ??
                   "{\"ok\":false,\"code\":\"BROWSER_BRIDGE_EMPTY_RESPONSE\"}";
    }
    catch (Exception error)
    {
        response = JsonSerializer.Serialize(new
        {
            ok = false,
            code = "BROWSER_BRIDGE_UNAVAILABLE",
            error = error.GetType().Name
        });
    }
    await WriteAsync(output, response);
}

static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken token)
{
    var offset = 0;
    while (offset < buffer.Length)
    {
        var read = await stream.ReadAsync(buffer.AsMemory(offset), token);
        if (read == 0)
            return false;
        offset += read;
    }
    return true;
}

static async Task WriteAsync(Stream stream, string json)
{
    var data = Encoding.UTF8.GetBytes(json);
    var header = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    await stream.WriteAsync(header);
    await stream.WriteAsync(data);
    await stream.FlushAsync();
}
