using System.Text.Json.Serialization;

namespace Heartbeat.Core.DTOs.Recaps
{
    /// <summary>
    /// 流式生成的事件载荷（ADR-042 §4）。响应头一发出，502 就不再可能——所以生成域的失败
    /// 从 HTTP 状态码搬进流内：HTTP 只负责鉴权/参数类 4xx 与并发 409。
    ///
    /// 事件类型（SSE 的 event 字段）：
    /// - <c>delta</c>：增量文本；
    /// - <c>thinking</c>：推理增量（思考模型的 reasoning）——正文之外的旁白，不进叙事、不落库，
    ///   前端可据此显示"正在思考"；
    /// - <c>done</c>：完整的 DailyRecapResponse，与 GET 同一形状，前端只有一份渲染逻辑；
    /// - <c>error</c>：可读失败原因；
    /// - <c>ping</c>：心跳，保持代理链路的读活性，前端忽略。
    ///
    /// 不进 OpenAPI（端点从 codegen 排除），契约靠 docs/api.md 与手写 wrapper 维持。
    /// </summary>
    public class RecapStreamEvent
    {
        public const string DeltaType = "delta";
        public const string ThinkingType = "thinking";
        public const string DoneType = "done";
        public const string ErrorType = "error";
        public const string PingType = "ping";

        [JsonIgnore]
        public string Type { get; set; } = DeltaType;

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Delta { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Thinking { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DailyRecapResponse? Recap { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Message { get; set; }

        public static RecapStreamEvent OfDelta(string text) => new() { Type = DeltaType, Delta = text };

        public static RecapStreamEvent OfThinking(string text) => new() { Type = ThinkingType, Thinking = text };

        public static RecapStreamEvent OfDone(DailyRecapResponse recap) => new() { Type = DoneType, Recap = recap };

        public static RecapStreamEvent OfError(string message) => new() { Type = ErrorType, Message = message };

        public static RecapStreamEvent OfPing() => new() { Type = PingType };
    }
}
