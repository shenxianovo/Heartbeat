using System.Text.Json;
using Orisumi.Core;

namespace Orisumi.Observers.ApplicationIdentity;

internal sealed record ApplicationIdentifier(string Scheme, string Value)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsShared => Scheme is "macos.bundle-id" or "android.package-name";
}

internal sealed record Product(EntityVersion Version, string Name, ApplicationIdentifier[] Identifiers);
internal sealed record IdentificationInput(string Platform, ApplicationIdentifier Identifier,
    ApplicationIdentifier[][] Applications, string[] Names, ProductCandidate[] Products);
internal sealed record ProductCandidate(Guid EntityId, string Name, ApplicationIdentifier[] Identifiers);
internal sealed record IdentificationDecision(string Kind, Guid? ProductId = null, string? Name = null);

internal interface IApplicationModel
{
    bool IsConfigured { get; }
    Task<IdentificationDecision> IdentifyAsync(IdentificationInput input, CancellationToken token);
}

internal static class IdentityData
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static readonly EntityId ProductSchemaId = new(Guid.Parse("019e1bbb-ec00-7000-8000-000000000004"));
    internal static readonly EntityId ObserverSchemaId = new(Guid.Parse("019e1bbb-ec00-7000-8000-000000000005"));
    internal static readonly EntityId PeriodSchemaId = new(Guid.Parse("019e1bbb-ec00-7000-8000-000000000001"));
    internal static ApplicationIdentifier[] Identifiers(EntityVersion version) =>
        ((BusinessContent)version.Content).Fields.GetProperty("identifiers").Deserialize<ApplicationIdentifier[]>(Json)!;
    internal static Product Product(EntityVersion version) => new(version,
        ((BusinessContent)version.Content).Fields.GetProperty("name").GetString()!, Identifiers(version));
    internal static BusinessContent Content(object content) => new(JsonSerializer.SerializeToElement(content, Json));
    internal static EntitySchema ProductSchema => new("应用产品",
        "表示用户认知中的一款软件，保存它的名称和属于它的跨设备线索；本机应用的跨设备线索出现在哪个产品中，就属于哪个产品。",
        "application-products", JsonSerializer.Deserialize<JsonElement>("""
        {"name":{"type":"string","description":"产品的名称","required":true,"nullable":false},
         "identifiers":{"type":"array","description":"属于这个产品的跨设备线索，顺序无意义；没有线索时为 []","required":true,"nullable":false,
          "items":{"type":"object","description":"一项跨设备线索，由 scheme 和 value 共同解释，与本机应用的标识线索相同","nullable":false,"fields":{
           "scheme":{"type":"string","description":"线索的种类，例如 macos.bundle-id","required":true,"nullable":false},
           "value":{"type":"string","description":"线索的原始值","required":true,"nullable":false}}}}}
        """));
    internal static EntitySchema ObserverSchema => new("应用识别 Observer",
        "表示一个应用识别 Observer 程序实例，保存它的程序版本和调用的模型；它作为 Observer 对它写入的应用产品负责。",
        "application-identity-observers", JsonSerializer.Deserialize<JsonElement>("""
        {"programVersion":{"type":"string","description":"程序的版本","required":true,"nullable":false},
         "model":{"type":"string","description":"判断时调用的模型，写作供应商和型号，例如 openai/gpt-5-mini","required":true,"nullable":false}}
        """));
}
