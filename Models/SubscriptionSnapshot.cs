using System.Text.Json.Serialization;

namespace NaiwaProxy.Models;

public sealed class SubscriptionSnapshot
{
    public int Version { get; set; } = 1;
    public string Client { get; set; } = "Nexora";
    public string Type { get; set; } = "subscription";

    [JsonPropertyName("generated_at")]
    public string GeneratedAt { get; set; } = "";

    public List<SnapshotSubscription> Subscriptions { get; set; } = [];
    public List<SnapshotProxyNode> ProxyNodes { get; set; } = [];
}

public sealed class SnapshotSubscription
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Name { get; set; } = "";
    public string UrlCiphertext { get; set; } = "";

    [JsonPropertyName("url_hash")]
    public string UrlHash { get; set; } = "";

    public int Enabled { get; set; } = 1;
    public long TotalBytes { get; set; }
    public long RemainBytes { get; set; }

    [JsonPropertyName("expire_at")]
    public string ExpireAt { get; set; } = "";

    [JsonPropertyName("last_update_time")]
    public string LastUpdateTime { get; set; } = "";

    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = "";

    [JsonPropertyName("updated_at")]
    public string UpdatedAt { get; set; } = "";
}

public sealed class SnapshotProxyNode
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int SubscriptionId { get; set; }
    public string Name { get; set; } = "";

    [JsonPropertyName("original_name")]
    public string OriginalName { get; set; } = "";

    public string Remark { get; set; } = "";
    public string Protocol { get; set; } = "";
    public string Address { get; set; } = "";
    public int Port { get; set; }
    public string Transport { get; set; } = "";
    public string Security { get; set; } = "";
    public string Sni { get; set; } = "";
    public string Host { get; set; } = "";
    public string Path { get; set; } = "";
    public string Alpn { get; set; } = "";

    [JsonPropertyName("country_code")]
    public string CountryCode { get; set; } = "";

    public string Region { get; set; } = "";
    public string City { get; set; } = "";

    [JsonPropertyName("credential_ciphertext")]
    public string CredentialCiphertext { get; set; } = "";

    public SnapshotCredential Credential { get; set; } = new();

    [JsonPropertyName("config_json")]
    public SnapshotConfigJson ConfigJson { get; set; } = new();

    [JsonPropertyName("share_link_ciphertext")]
    public string ShareLinkCiphertext { get; set; } = "";

    [JsonPropertyName("share_link")]
    public string ShareLink { get; set; } = "";

    [JsonPropertyName("node_hash")]
    public string NodeHash { get; set; } = "";

    public int Enabled { get; set; } = 1;

    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = "";

    [JsonPropertyName("updated_at")]
    public string UpdatedAt { get; set; } = "";
}

public sealed class SnapshotCredential
{
    [JsonPropertyName("alter_id")]
    public int AlterId { get; set; }

    public string Email { get; set; } = "";
    public string Uuid { get; set; } = "";
}

public sealed class SnapshotConfigJson
{
    [JsonPropertyName("expire_at")]
    public string ExpireAt { get; set; } = "";

    public string Network { get; set; } = "";
    public long RemainBytes { get; set; }
    public long TotalBytes { get; set; }
    public long UsedBytes { get; set; }
}
