using System.ComponentModel.DataAnnotations;

namespace Haworks.Search.Infrastructure.Options;

public sealed class MeilisearchOptions
{
    public const string SectionName = "Meilisearch";

    [Required]
    public string Url { get; set; } = "";

    [Required]
    public string MasterKey { get; set; } = "";

    public string IndexName { get; set; } = "products";
}
