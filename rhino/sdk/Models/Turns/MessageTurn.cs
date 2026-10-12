using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace Rhino.AI;

internal sealed record MessageTurn : Turn
{

    // TODO : Improve!
    public override string Data => string.Concat(Content.OfType<TextContent>().Select(p => p.Text));

    // [Obsolete("REMOVE THIS")] // TODO : Remove ALL usages of this
    public string Message => Data;

    private List<IMessageContent> PrivateContent { get; }
    public IReadOnlyList<IMessageContent> Content => PrivateContent;

    public MessageTurn(IEnumerable<IMessageContent> message, RoleType role = RoleType.User, DateTime? timestamp = null, TimeSpan? duration = null, int? tokenCount = null)
        : base("message")
    {
        PrivateContent = new(message);
        Role = role;
        TimeStamp = timestamp ?? DateTime.UtcNow;
        Duration = duration;
        TokenCount = tokenCount;
    }

    public MessageTurn(string message, RoleType role = RoleType.User, DateTime? timestamp = null, TimeSpan? duration = null, int? tokenCount = null)
        : this([new TextContent(message)], role, timestamp, duration, tokenCount)
    {

    }

    public override ITurn Copy() => new MessageTurn(Content.Select(c => c.Copy()), Role, TimeStamp, Duration, TokenCount) { Success = Success };

}

[JsonConverter(typeof(MessageJsonConverter))]
public interface IMessageContent
{
    public IMessageContent Copy();
    public string Type { get; }
}

public sealed record TextContent(string Text) : IMessageContent
{
    public string Type { get; init; } = "text";
    public IMessageContent Copy() => this;
}

public sealed record ImageContent(byte[] Bytes, string MediaType) : IMessageContent
{
    public string Type { get; init; } = "image";
    public IMessageContent Copy() => new ImageContent([.. Bytes], MediaType);
}

public sealed record FileContent(Uri Uri, string MediaType) : IMessageContent
{
    public string Type { get; init; } = "file";
    public IMessageContent Copy() => this;
}



internal sealed class MessageJsonConverter : JsonConverter<IMessageContent>
{
    public override IMessageContent? Read(ref Utf8JsonReader reader, System.Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument doc = JsonDocument.ParseValue(ref reader);
        JsonElement root = doc.RootElement;
        string? disc = root.TryGetProperty("type", out JsonElement d) ? d.GetString() : null;
        return disc?.ToLowerInvariant() switch
        {
            "text" => root.Deserialize<TextContent>(options),
            "image" => root.Deserialize<ImageContent>(options),
            "file" => root.Deserialize<FileContent>(options),

            _ => throw new JsonException($"Unknown ITurn type: {disc}"),
        };
    }

    public override void Write(Utf8JsonWriter writer, IMessageContent value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, value.GetType(), options);
}
