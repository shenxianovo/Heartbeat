namespace Heartbeat.Server.Entities
{
    public class App
    {
        public long Id { get; set; }

        /// <summary>跨平台产品的稳定短键；仅在真实碰撞时增加限定词。</summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>面向用户的产品名称，不承载平台身份。</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>未知 AppIdentity 自动创建的一对一待归类产品。</summary>
        public bool IsProvisional { get; set; }

        /// <summary>
        /// 旧服务端测试与尚未迁移的消费者使用的构造别名。数据库与查询必须使用
        /// Key/DisplayName；Ticket 04 完成消费者迁移后可删除。
        /// </summary>
        [System.ComponentModel.DataAnnotations.Schema.NotMapped]
        public string Name
        {
            get => DisplayName;
            set
            {
                DisplayName = value;
                if (string.IsNullOrWhiteSpace(Key))
                    Key = Heartbeat.Core.AppIdentityKeys.ProductSlug(value);
            }
        }

        public ICollection<AppIdentity> Identities { get; set; } = [];
    }
}
