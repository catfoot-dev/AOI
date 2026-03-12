using System.Text.Json;
using System.Text.Json.Serialization;
using AoiDemo.Web.Models;

namespace AoiDemo.Web.Infrastructure;

/// <summary>
/// 클라이언트/서버 WebSocket 메시지의 JSON 직렬화 규칙을 한곳에서 관리합니다.
/// </summary>
public static class DemoJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>
    /// 원시 JSON 문자열을 읽어 구체적인 클라이언트 메시지 형식으로 역직렬화합니다.
    /// </summary>
    /// <param name="json">클라이언트가 보낸 JSON 원문입니다.</param>
    /// <returns>메시지 type 필드에 맞는 클라이언트 메시지 인스턴스입니다.</returns>
    public static ClientMessage DeserializeClientMessage(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("type", out var typeElement))
        {
            throw new JsonException("Client message requires a 'type' field.");
        }

        var messageType = typeElement.GetString();
        return messageType switch
        {
            "join" => Deserialize<JoinClientMessage>(json),
            "moveInput" => Deserialize<MoveInputClientMessage>(json),
            "changeAlgorithm" => Deserialize<ChangeAlgorithmClientMessage>(json),
            "resetWorld" => Deserialize<ResetWorldClientMessage>(json),
            "ping" => Deserialize<PingClientMessage>(json),
            _ => throw new JsonException($"Unknown client message type '{messageType}'.")
        };
    }

    /// <summary>
    /// 서버 메시지를 런타임 형식 기준으로 JSON 문자열로 직렬화합니다.
    /// </summary>
    /// <param name="message">클라이언트로 전송할 서버 메시지 객체입니다.</param>
    /// <returns>WebSocket으로 보낼 수 있는 JSON 문자열입니다.</returns>
    public static string SerializeServerMessage(ServerMessage message) =>
        JsonSerializer.Serialize(message, message.GetType(), Options);

    /// <summary>
    /// 지정한 클라이언트 메시지 형식으로 JSON 문자열을 역직렬화합니다.
    /// </summary>
    /// <typeparam name="T">역직렬화할 구체적인 클라이언트 메시지 형식입니다.</typeparam>
    /// <param name="json">역직렬화할 JSON 문자열입니다.</param>
    /// <returns>지정한 형식으로 변환된 클라이언트 메시지 인스턴스입니다.</returns>
    private static T Deserialize<T>(string json) where T : ClientMessage =>
        JsonSerializer.Deserialize<T>(json, Options) ??
        throw new JsonException($"Unable to deserialize '{typeof(T).Name}'.");

    /// <summary>
    /// 데모 전반에서 공유할 JSON 직렬화 옵션을 생성합니다.
    /// </summary>
    /// <returns>camelCase와 enum 문자열 변환 규칙이 적용된 JSON 옵션입니다.</returns>
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
