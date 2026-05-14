using System.ComponentModel.DataAnnotations;

namespace Haworks.Search.Infrastructure.Options;

public sealed class ElasticsearchOptions
{
    public const string SectionName = "Elasticsearch";

    [Required]
    public string Url { get; set; } = "";

    public string IndexName { get; set; } = "products";
}
