using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Heartbeat.Server.Services
{
    /// <summary>Recap 的 LLM 接入配置。供应商纯配置可换（ADR-023 §1）：本地推理 = 改 BaseUrl，零改码。发问判官共用（ADR-029 §4）。</summary>
    public class RecapOptions
    {
        public const string Section = "Recap";

        /// <summary>OpenAI 兼容 API 根地址（如 https://api.deepseek.com/v1）。</summary>
        public string BaseUrl { get; set; } = string.Empty;

        /// <summary>走环境变量 / user-secrets，不进仓库。</summary>
        public string ApiKey { get; set; } = string.Empty;

        public string Model { get; set; } = string.Empty;
    }

    /// <summary>Recap 生成失败（未配置 / 上游错误 / 响应不可解析）。控制器映射为 502，不写缓存（ADR-023 §4）。</summary>
    public class RecapGenerationException(string message, Exception? inner = null) : Exception(message, inner);

    /// <summary>Recap 生成层（ADR-023 §2）：投影 digest → 叙事。刻意保持薄——质量核心在可测的投影层。</summary>
    public interface IRecapGenerator
    {
        string Model { get; }

        /// <summary>提示词模板的内容 hash。缓存的来源诊断字段。</summary>
        string PromptHash { get; }

        Task<string> GenerateAsync(string digest, CancellationToken ct = default);
    }

    /// <summary>叙事生成：prompt 模板 + ChatCompletionClient 传输（ADR-029 issue 03 合流）。</summary>
    public class OpenAiCompatibleRecapGenerator(ChatCompletionClient client, IOptions<RecapOptions> options) : IRecapGenerator
    {
        /// <summary>
        /// Recap 的产品人格（ADR-023 §5）：日记与档案，只叙事，不评判。
        /// 修改此模板即改变 PromptHash——旧缓存不失效，靠字段可辨、靠用户重生成收敛。
        /// </summary>
        private const string PromptTemplate =
            """
            你是 Heartbeat 的回顾写作者。Heartbeat 记录了用户在电脑上的活动，下面是某一天的活动摘要。请据此为用户写一段那一天的叙事回顾。

            写作规则：
            - 口吻是日记与档案：平实、克制、只叙述发生了什么。不评判效率，不打分，不给建议，不用感叹与营销腔。
            - 用第二人称"你"，中文，纯文本 2–4 个自然段，按时间顺序推进。不用列表、标题、emoji。
            - 抓主线与转场，不逐条罗列时间。具体的项目名、页面名、文件名值得点出——它们是记忆的锚点。
            - 摘要的数据模型：每台设备的"注意力轨"记录前台应用，互斥、时长可信；块内"其中:"是该应用下细分内容的时长分布；"语义细节轨"是页面/文件级细节，与注意力轨重叠属正常，其时长不可与注意力轨相加；不同设备的时长不可相加；"离开"表示人不在电脑前。
            - 只写摘要中有依据的事实，不虚构。数据少就写短，两三句话也是诚实的一天。
            - 摘要末尾若有"已知脉络"，那是用户确认过的项目/活动含义：当某段活动属于其中一条脉络时，用脉络的名字称呼它，而不是罗列 app 名。脉络之外的活动照常叙述。
            - "近 14 天高频出现"是背景注释——常驻的基础设施，不必逐一叙述。
            """;

        public static readonly string TemplateHash =
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PromptTemplate)))[..8];

        public string Model => options.Value.Model;

        public string PromptHash => TemplateHash;

        public async Task<string> GenerateAsync(string digest, CancellationToken ct = default)
        {
            try
            {
                return await client.CompleteAsync(PromptTemplate, digest, ct);
            }
            catch (ChatCompletionException ex)
            {
                throw new RecapGenerationException(ex.Message, ex);
            }
        }
    }
}
