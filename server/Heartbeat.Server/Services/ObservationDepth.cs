using System.Text.Json;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Collectors;

namespace Heartbeat.Server.Services
{
    /// <summary>深度树上的一个读数：某观测深度层上的 (读数名, 值)。Layer 由声明的层序派生。</summary>
    public readonly record struct DepthReading(int Layer, string Reading, string Value);

    /// <summary>
    /// 声明校验与规范化（ADR-030 §1）：name trim + 小写（与 Matcher 身份同尺）、source 内唯一、
    /// from 槽位语法合法、层序保持。返回 null = 合法，否则错误描述（端点映射 400）。
    /// </summary>
    public static class DeclarationValidator
    {
        public static string? Validate(CollectorDeclarationDto declaration)
        {
            if (string.IsNullOrWhiteSpace(declaration.Source)) return "source is required";
            if (declaration.Version < 1) return "version must be >= 1";
            if (declaration.Layers.Count == 0) return "at least one layer is required";

            var seen = new HashSet<string>();
            foreach (var layer in declaration.Layers)
            {
                if (layer.Readings.Count == 0) return "each layer needs at least one reading";
                foreach (var reading in layer.Readings)
                {
                    var name = (reading.Name ?? string.Empty).Trim().ToLowerInvariant();
                    if (name.Length == 0) return "reading name is required";
                    if (!seen.Add(name)) return $"duplicate reading name '{name}'";
                    if (!DepthSlots.IsValid(reading.From)) return $"invalid slot '{reading.From}' for reading '{name}'";
                }
            }
            return null;
        }

        /// <summary>canonical 形：source/name 小写、from/label trim。存库与解释前统一过此。</summary>
        public static CollectorDeclarationDto Normalize(CollectorDeclarationDto declaration) => new()
        {
            Source = declaration.Source.Trim().ToLowerInvariant(),
            Version = declaration.Version,
            CollectorVersion = declaration.CollectorVersion?.Trim(),
            Layers = declaration.Layers.Select(l => new DepthLayerDto
            {
                Readings = l.Readings.Select(r => new DepthReadingDto
                {
                    Name = (r.Name ?? string.Empty).Trim().ToLowerInvariant(),
                    From = (r.From ?? string.Empty).Trim(),
                    Label = string.IsNullOrWhiteSpace(r.Label) ? null : r.Label.Trim(),
                }).ToList()
            }).ToList()
        };
    }

    /// <summary>
    /// 运输槽位（ADR-030 §2）：值放在哪，不是值是什么。服务端唯一动作 = 按槽取值，
    /// per-source 语义知识为零。新读数一律走 attributes.*，wire 契约永不再改。
    /// </summary>
    public static class DepthSlots
    {
        public const string AppName = "appName";
        public const string Title = "title";
        public const string IdentityKey = "identityKey";
        public const string AttributesPrefix = "attributes.";

        public static bool IsValid(string? from) =>
            from is AppName or Title or IdentityKey
            || from != null && from.StartsWith(AttributesPrefix, StringComparison.Ordinal)
                            && from.Length > AttributesPrefix.Length;

        public static string? Resolve(string from, string? appName, string? title, string identityKey, string? attributesJson)
        {
            if (from == AppName) return appName;
            if (from == Title) return title;
            if (from == IdentityKey) return identityKey;
            if (from.StartsWith(AttributesPrefix, StringComparison.Ordinal))
                return ResolveAttributePath(attributesJson, from[AttributesPrefix.Length..]);
            return null;
        }

        private static string? ResolveAttributePath(string? attributesJson, string path)
        {
            if (string.IsNullOrWhiteSpace(attributesJson)) return null;
            try
            {
                using var doc = JsonDocument.Parse(attributesJson);
                var node = doc.RootElement;
                foreach (var key in path.Split('.'))
                {
                    if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(key, out node))
                        return null;
                }
                return node.ValueKind switch
                {
                    JsonValueKind.String => node.GetString(),
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => node.GetRawText(),
                    _ => null,
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// 生效深度表集（ADR-030 §4/§7）：每 source 一份声明（调用方已按 max(Version) 选出）+ 通用回落。
    /// 解释器是纯函数：按声明层序取槽出读数路径；某读数缺值即跳过（段挂最深可用读数由树构建负责），
    /// 唯一例外是首层首读数（树根轴）缺值给 "(unknown)"，段不因此从身份维度消失。
    /// </summary>
    public sealed class DepthTables
    {
        public const string UnknownValue = "(unknown)";

        private readonly Dictionary<string, CollectorDeclarationDto> _tables;

        /// <summary>同 source 多份声明按 max(Version) 生效（ADR-030 §4）——种子与 DB 行的合并即由此完成。</summary>
        public DepthTables(IEnumerable<CollectorDeclarationDto> declarations)
        {
            _tables = declarations
                .Select(DeclarationValidator.Normalize)
                .GroupBy(d => d.Source, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(d => d.Version).First(),
                    StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>迁移种子对应的进程内副本：DB 不可用路径（纯投影测试）与解释器测试的基准。</summary>
        public static DepthTables Seeds { get; } = new(SeedDeclarations.All);

        public CollectorDeclarationDto? For(string source) => _tables.GetValueOrDefault(source);

        /// <summary>声明 label 词典（读数名 → 展示名），供前端渲染（issue 02 下发）。</summary>
        public IReadOnlyDictionary<string, string> Labels()
        {
            var labels = new Dictionary<string, string>();
            foreach (var reading in _tables.Values.SelectMany(t => t.Layers).SelectMany(l => l.Readings))
                if (reading.Label != null)
                    labels.TryAdd(reading.Name, reading.Label);
            return labels;
        }

        public IReadOnlyList<DepthReading> ReadingsFor(
            string source, string? appName, string? title, string identityKey, string? attributesJson = null)
        {
            var table = For(source);
            var readings = new List<DepthReading>(2);
            if (table == null)
            {
                // 未声明 source 的通用回落（ADR-030 §4）：digest 不死，树浅。
                readings.Add(new(1, "identity", identityKey));
                if (!string.IsNullOrWhiteSpace(title)) readings.Add(new(2, "title", title));
                return readings;
            }

            for (var i = 0; i < table.Layers.Count; i++)
            {
                foreach (var decl in table.Layers[i].Readings)
                {
                    var value = DepthSlots.Resolve(decl.From, appName, title, identityKey, attributesJson);
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        if (i == 0 && ReferenceEquals(decl, table.Layers[0].Readings[0]))
                            readings.Add(new(1, decl.Name, UnknownValue));
                        continue;
                    }
                    readings.Add(new(i + 1, decl.Name, value));
                }
            }
            return readings;
        }
    }

    /// <summary>
    /// 迁移种子声明（ADR-030 §4）：切换日行为零断层的 bootstrap 数据，与各采集器 v1 声明
    /// 逐字节一致（采集器上线上报后幂等收敛）。运行时生效表以 DB 为准，此处仅供种子与纯函数测试。
    /// </summary>
    public static class SeedDeclarations
    {
        public static CollectorDeclarationDto System { get; } = new()
        {
            Source = ActivitySources.System,
            Version = 1,
            Layers =
            [
                new() { Readings = [new() { Name = "app", From = DepthSlots.AppName, Label = "应用" }] },
                new() { Readings = [new() { Name = "title", From = DepthSlots.Title, Label = "窗口标题" }] },
            ]
        };

        public static CollectorDeclarationDto Browser { get; } = new()
        {
            Source = ActivitySources.Browser,
            Version = 1,
            Layers =
            [
                new() { Readings = [new() { Name = "url", From = DepthSlots.IdentityKey, Label = "网址" }] },
                new() { Readings = [new() { Name = "tab_title", From = DepthSlots.Title, Label = "标签页" }] },
            ]
        };

        public static IReadOnlyList<CollectorDeclarationDto> All { get; } = [System, Browser];

        /// <summary>
        /// 启动种子（AddCollectorDeclarations 迁移的 C# 护航，与 KnowledgeIdentityBackfill 同理）：
        /// canonical PayloadJson 字节由 System.Text.Json 产出，SQL 无法复现，故种子在 C# 侧幂等补插。
        /// 只插缺席行——已被采集器上报的同 (Source, Version) 行不动。
        /// </summary>
        public static async Task SeedAsync(Data.AppDbContext db, TimeProvider? clock = null, CancellationToken ct = default)
        {
            var now = (clock ?? TimeProvider.System).GetUtcNow();
            foreach (var seed in All)
            {
                var normalized = DeclarationValidator.Normalize(seed);
                var exists = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
                    db.CollectorDeclarations,
                    d => d.Source == normalized.Source && d.Version == normalized.Version, ct);
                if (exists) continue;
                db.CollectorDeclarations.Add(new Entities.CollectorDeclaration
                {
                    Source = normalized.Source,
                    Version = normalized.Version,
                    PayloadJson = JsonSerializer.Serialize(normalized),
                    ReportedAt = now,
                });
            }
            await db.SaveChangesAsync(ct);
        }
    }
}
