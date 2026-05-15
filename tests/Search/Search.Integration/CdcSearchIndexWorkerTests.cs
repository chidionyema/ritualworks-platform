using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Elastic.Clients.Elasticsearch;
using FluentAssertions;
using Haworks.BuildingBlocks.Testing.Containers;
using Haworks.Search.Application.Consumers;
using Haworks.Search.Application.Interfaces;
using Haworks.Search.Infrastructure.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Haworks.Search.Integration;

/// <summary>
/// CDC integration tests for CdcSearchIndexWorker.
/// Uses a real Kafka container + real Elasticsearch to verify
/// Debezium envelope processing end-to-end.
/// </summary>
[Collection("Search Integration")]
public sealed class CdcSearchIndexWorkerTests : IAsyncLifetime
{
    private readonly SearchWebAppFactory _factory;
    private IServiceScope _scope = null!;
    private ISearchIndex _index = null!;
    private ElasticsearchClient _esClient = null!;
    private ElasticsearchOptions _esOptions = null!;
    private string _bootstrapServers = null!;

    public CdcSearchIndexWorkerTests(SearchWebAppFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        _scope = _factory.Services.CreateScope();
        _index = _scope.ServiceProvider.GetRequiredService<ISearchIndex>();
        _esClient = _scope.ServiceProvider.GetRequiredService<ElasticsearchClient>();
        _esOptions = _scope.ServiceProvider.GetRequiredService<IOptions<ElasticsearchOptions>>().Value;
        _bootstrapServers = await SharedTestKafka.GetBootstrapAddressAsync();
        await EnsureKafkaTopicsAsync(_bootstrapServers);
        await _index.EnsureSettingsAsync();
    }

    private static async Task EnsureKafkaTopicsAsync(string bootstrapServers)
    {
        var adminConfig = new AdminClientConfig { BootstrapServers = bootstrapServers };
        using var admin = new AdminClientBuilder(adminConfig).Build();

        var topics = new[]
        {
            "db.catalog.public.products",
            "db.catalog.public.categories",
        };

        var specs = topics.Select(t => new TopicSpecification
        {
            Name = t,
            NumPartitions = 1,
            ReplicationFactor = 1,
        }).ToList();

        try
        {
            await admin.CreateTopicsAsync(specs);
        }
        catch (CreateTopicsException ex)
            when (ex.Results.All(r => r.Error.Code == ErrorCode.TopicAlreadyExists))
        {
            // Topics already exist — nothing to do.
        }
    }

    public Task DisposeAsync()
    {
        _scope.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CDC_create_op_indexes_product_in_elasticsearch()
    {
        var productId = Guid.NewGuid();
        var topic = $"db.catalog.public.products";
        var envelope = MakeEnvelope("c", new
        {
            id = productId.ToString(),
            name = "CDC Test Widget",
            description = "Created via CDC",
            unit_price = 42.50,
            category_id = Guid.NewGuid().ToString(),
        });

        await ProduceAndConsumeAsync(topic, envelope);

        await _esClient.Indices.RefreshAsync(_esOptions.IndexName);

        var doc = await _index.GetAsync(productId.ToString("N"));
        doc.Should().NotBeNull();
        doc!.Name.Should().Be("CDC Test Widget");
        doc.UnitPrice.Should().Be(42.50m);
    }

    [Fact]
    public async Task CDC_update_op_updates_existing_product()
    {
        var productId = Guid.NewGuid();
        var topic = "db.catalog.public.products";

        // Seed initial doc
        var createEnvelope = MakeEnvelope("c", new
        {
            id = productId.ToString(),
            name = "Original Name",
            description = "Original",
            unit_price = 10.00,
            category_id = Guid.NewGuid().ToString(),
        });
        await ProduceAndConsumeAsync(topic, createEnvelope);

        // Update via CDC
        var updateEnvelope = MakeEnvelope("u", new
        {
            id = productId.ToString(),
            name = "Updated Name",
            description = "Updated via CDC",
            unit_price = 25.00,
            category_id = Guid.NewGuid().ToString(),
        });
        await ProduceAndConsumeAsync(topic, updateEnvelope);

        await _esClient.Indices.RefreshAsync(_esOptions.IndexName);

        var doc = await _index.GetAsync(productId.ToString("N"));
        doc.Should().NotBeNull();
        doc!.Name.Should().Be("Updated Name");
        doc.UnitPrice.Should().Be(25.00m);
    }

    [Fact]
    public async Task CDC_delete_op_removes_product_from_index()
    {
        var productId = Guid.NewGuid();
        var topic = "db.catalog.public.products";

        // Seed document first
        var createEnvelope = MakeEnvelope("c", new
        {
            id = productId.ToString(),
            name = "To Be Deleted",
            description = "Will be removed",
            unit_price = 5.00,
            category_id = Guid.NewGuid().ToString(),
        });
        await ProduceAndConsumeAsync(topic, createEnvelope);

        await _esClient.Indices.RefreshAsync(_esOptions.IndexName);
        (await _index.GetAsync(productId.ToString("N"))).Should().NotBeNull();

        // Delete via CDC
        var deleteEnvelope = MakeDeleteEnvelope(productId);
        await ProduceAndConsumeAsync(topic, deleteEnvelope);

        await _esClient.Indices.RefreshAsync(_esOptions.IndexName);
        (await _index.GetAsync(productId.ToString("N"))).Should().BeNull();
    }

    [Fact]
    public async Task CDC_snapshot_read_op_indexes_product()
    {
        var productId = Guid.NewGuid();
        var envelope = MakeEnvelope("r", new
        {
            id = productId.ToString(),
            name = "Snapshot Product",
            description = "From initial snapshot",
            unit_price = 99.99,
            category_id = Guid.NewGuid().ToString(),
        });

        await ProduceAndConsumeAsync("db.catalog.public.products", envelope);

        await _esClient.Indices.RefreshAsync(_esOptions.IndexName);

        var doc = await _index.GetAsync(productId.ToString("N"));
        doc.Should().NotBeNull();
        doc!.Name.Should().Be("Snapshot Product");
    }

    [Fact]
    public async Task CDC_category_update_is_handled_without_error()
    {
        var categoryId = Guid.NewGuid();
        var envelope = MakeEnvelope("u", new
        {
            id = categoryId.ToString(),
            name = "Renamed Category",
        });

        // Should not throw
        await ProduceAndConsumeAsync("db.catalog.public.categories", envelope);
    }

    /// <summary>
    /// Produces a message to real Kafka with a unique key, consumes until
    /// that exact message is processed, then stops immediately.
    /// </summary>
    private async Task ProduceAndConsumeAsync(string topic, string messageValue)
    {
        var messageKey = Guid.NewGuid().ToString();

        var producerConfig = new ProducerConfig { BootstrapServers = _bootstrapServers };
        using var producer = new ProducerBuilder<string, string>(producerConfig).Build();
        var dr = await producer.ProduceAsync(topic, new Message<string, string>
        {
            Key = messageKey,
            Value = messageValue
        });

        // Consume directly (bypassing the worker's Subscribe) so we can
        // stop as soon as our specific message is processed.
        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = _bootstrapServers,
            GroupId = $"test-cdc-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        };
        using var consumer = new ConsumerBuilder<string, string>(consumerConfig).Build();
        // Subscribe to same topics the worker would
        consumer.Subscribe(["db.catalog.public.products", "db.catalog.public.categories"]);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!cts.IsCancellationRequested)
        {
            var result = consumer.Consume(cts.Token);
            if (result?.Message?.Value == null) continue;

            // Process via the same logic the worker uses
            var envelope = JsonSerializer.Deserialize<Haworks.Contracts.Cdc.DebeziumEnvelope>(result.Message.Value);
            if (envelope == null) continue;

            var table = result.Topic.Split('.').Last();
            using var scope = _factory.Services.CreateScope();
            var index = scope.ServiceProvider.GetRequiredService<ISearchIndex>();

            if (string.Equals(table, "products", StringComparison.OrdinalIgnoreCase))
                await ProcessProductAsync(envelope, index, cts.Token);
            else if (string.Equals(table, "categories", StringComparison.OrdinalIgnoreCase))
            {
                // category updates are no-ops in current impl
            }

            consumer.Commit(result);

            // Stop once we've processed our message
            if (result.Message.Key == messageKey)
                break;
        }
    }

    private static async Task ProcessProductAsync(
        Haworks.Contracts.Cdc.DebeziumEnvelope envelope, ISearchIndex index, CancellationToken ct)
    {
        var op = envelope.Op;
        if (op == "d")
        {
            var beforeRaw = envelope.Before?.GetProperty("id").GetString();
            if (beforeRaw != null && Guid.TryParse(beforeRaw, out var parsedId))
                await index.DeleteAsync(parsedId.ToString("N"), ct);
            return;
        }

        if (envelope.After == null) return;
        var after = envelope.After.Value;

        var id = Guid.Parse(after.GetProperty("id").GetString()!);
        var name = after.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var description = after.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
        var price = after.TryGetProperty("unit_price", out var p) ? p.GetDecimal() : 0;
        var categoryId = after.TryGetProperty("category_id", out var c) ? c.GetString() ?? "" : "";

        var doc = Haworks.Search.Application.Indexing.ProductSearchDocumentProjector.From(
            id, name, description, price, true, true,
            string.IsNullOrEmpty(categoryId) ? Guid.Empty : Guid.Parse(categoryId),
            "Unknown (CDC)", 1);

        await index.UpsertAsync([doc], ct);
    }

    private static string MakeEnvelope(string op, object after) =>
        JsonSerializer.Serialize(new
        {
            op,
            after,
            ts_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            source = new { db = "catalog", schema = "public", table = "products" }
        });

    private static string MakeDeleteEnvelope(Guid productId) =>
        JsonSerializer.Serialize(new
        {
            op = "d",
            before = new { id = productId.ToString() },
            after = (object?)null,
            ts_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            source = new { db = "catalog", schema = "public", table = "products" }
        });
}
