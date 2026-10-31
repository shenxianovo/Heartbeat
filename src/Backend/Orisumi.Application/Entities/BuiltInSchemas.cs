using System.Text.Json;
using Orisumi.Core;

namespace Orisumi.Application.Entities;

public static class BuiltInSchemas
{
    public static readonly EntityId Person = new(Guid.Parse("019e1bbb-ec00-7000-8000-000000000006"));
    public static readonly EntityId Program = new(Guid.Parse("019e1bbb-ec00-7000-8000-000000000012"));
    public static readonly EntityId Review = new(Guid.Parse("019e1bbb-ec00-7000-8000-00000000000e"));
    public static readonly EntityId Name = new(Guid.Parse("019e1bbb-ec00-7000-8000-000000000010"));
    public static readonly IReadOnlyList<SchemaDeclaration> All =
    [
        new(Person, Schema("人", "表示一个人。用户本人确认、否定或陈述时，作为 Observer 对陈述负责。", "people", "thing", new { })),
        new(Program, Schema("程序实例", "表示一个对观测负责的程序实例或 Agent。", "program-instances", "thing", new { })),
        new(Review, Schema("复核", "对一条陈述的确认、否定或取代。事实时间是它针对的那一段，未知时针对整条陈述。", "reviews", "observation",
            new { targetVersionId = Reference("version", "被复核的陈述的版本"), verdict = Text("confirm 确认、deny 否定或 supersede 取代"), text = Text("复核的说明", true) })),
        new(new(Guid.Parse("019e1bbb-ec00-7000-8000-00000000000f")), Schema("关系", "表示两个事物之间的一种关系。事实时间是这个关系成立的时段。", "relations", "observation",
            new { subjectId = Reference("thing", "关系的主语"), predicate = Text("关系的说法；same-as 表示两个事物是同一个"), objectId = Reference("thing", "关系的宾语"), text = Text("补充说明", true) })),
        new(Name, Schema("名字", "表示一个事物的名字。事实时间是这个名字成立的时段，未知时为 null。", "names", "observation",
            new { thingId = Reference("thing", "被命名的事物"), name = Text("名字的原文") })),
    ];
    private static EntitySchema Schema(string name, string description, string resource, string kind, object fields) =>
        new(name, description, resource, JsonSerializer.SerializeToElement(fields), kind, false);
    private static object Text(string description, bool nullable = false) => new { type = "string", description, required = true, nullable };
    private static object Reference(string target, string description) => new { type = "reference", target, description, required = true, nullable = false };
}
