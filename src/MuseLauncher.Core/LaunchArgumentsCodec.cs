using System.Text.Json;

namespace MuseLauncher.Core;

/// <summary>命名管道传递结构化参数，不用换行分隔，避免内容变成额外命令。</summary>
public static class LaunchArgumentsCodec
{
    public const int MaxPayloadBytes = 65536;
    private const int MaxArguments = 256;

    public static byte[] Encode(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count > MaxArguments || arguments.Any(argument => argument == null))
            throw new InvalidDataException("无效或过多的启动参数");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(arguments);
        if (bytes.Length > MaxPayloadBytes) throw new InvalidDataException("启动参数过长");
        return bytes;
    }

    public static string[] Decode(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length is 0 or > MaxPayloadBytes) throw new InvalidDataException("无效的启动参数长度");
        try
        {
            var arguments = JsonSerializer.Deserialize<string[]>(payload);
            if (arguments == null || arguments.Length > MaxArguments || arguments.Any(argument => argument == null))
                throw new InvalidDataException("无效的启动参数");
            return arguments;
        }
        catch (JsonException ex) { throw new InvalidDataException("启动参数 JSON 无效", ex); }
    }
}
