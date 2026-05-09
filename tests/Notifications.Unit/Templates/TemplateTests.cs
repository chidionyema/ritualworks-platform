using System.Reflection;
using FluentAssertions;
using Haworks.Notifications.Application.Templates;
using Haworks.Notifications.Domain.Entities;
using Haworks.Notifications.Domain.Enums;
using Moq;
using Xunit;

namespace Haworks.Notifications.Unit.Templates;

[Trait("Category", "Unit")]
public sealed class TemplateTests
{
    private static readonly string[] OrderShippedRequired =
        ["order_id", "carrier", "first_name", "tracking_url"];

    private static readonly string[] WelcomeRequired = ["name"];

    private static readonly string[] MissingTrackingRequired =
        ["order_id", "tracking_url"];

    private static readonly string[] SingleARequired = ["a"];

    // --- ScribanTemplateRenderer ---------------------------------------------

    [Fact]
    public void Render_ValidVariables_SubstitutesIntoSubjectAndBody()
    {
        var template = TemplateFactory.Build(
            templateId: "order.shipped",
            channel: NotificationChannel.Email,
            locale: "en-US",
            version: 1,
            isActive: true,
            subject: "Your order #{{ order_id }} has shipped via {{ carrier }}",
            body: "Hi {{ first_name }}, track at {{ tracking_url }}",
            textFallback: "Order {{ order_id }} shipped",
            requiredVariables: OrderShippedRequired);

        var renderer = new ScribanTemplateRenderer();
        var data = new Dictionary<string, object?>
        {
            ["order_id"] = "ABC-123",
            ["carrier"] = "Royal Mail",
            ["first_name"] = "Ada",
            ["tracking_url"] = "https://track/ABC-123"
        };

        var rendered = renderer.Render(template, data);

        rendered.Subject.Should().Be("Your order #ABC-123 has shipped via Royal Mail");
        rendered.Body.Should().Be("Hi Ada, track at https://track/ABC-123");
        rendered.TextFallback.Should().Be("Order ABC-123 shipped");
    }

    [Fact]
    public void Render_NullTextFallback_ReturnsNullTextFallback()
    {
        var template = TemplateFactory.Build(
            templateId: "welcome",
            channel: NotificationChannel.Sms,
            locale: "*",
            version: 1,
            isActive: true,
            subject: string.Empty,
            body: "Welcome {{ name }}",
            textFallback: null,
            requiredVariables: WelcomeRequired);

        var renderer = new ScribanTemplateRenderer();
        var rendered = renderer.Render(template, new Dictionary<string, object?> { ["name"] = "Ada" });

        rendered.Body.Should().Be("Welcome Ada");
        rendered.TextFallback.Should().BeNull();
    }

    [Fact]
    public void Render_MissingRequiredVariable_ThrowsArgumentExceptionNamingTheKey()
    {
        var template = TemplateFactory.Build(
            templateId: "order.shipped",
            channel: NotificationChannel.Email,
            locale: "en-US",
            version: 1,
            isActive: true,
            subject: "{{ order_id }}",
            body: "track at {{ tracking_url }}",
            textFallback: null,
            requiredVariables: MissingTrackingRequired);

        var renderer = new ScribanTemplateRenderer();
        var data = new Dictionary<string, object?>
        {
            ["order_id"] = "ABC-123"
            // tracking_url intentionally missing
        };

        var act = () => renderer.Render(template, data);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*missing variable: tracking_url*");
    }

    [Fact]
    public void Render_RequiredVariablePresentButNull_ThrowsArgumentException()
    {
        var template = TemplateFactory.Build(
            templateId: "x",
            channel: NotificationChannel.Email,
            locale: "*",
            version: 1,
            isActive: true,
            subject: "{{ a }}",
            body: "{{ a }}",
            textFallback: null,
            requiredVariables: SingleARequired);

        var renderer = new ScribanTemplateRenderer();
        var data = new Dictionary<string, object?> { ["a"] = null };

        var act = () => renderer.Render(template, data);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*missing variable: a*");
    }

    [Fact]
    public void Render_TemplateWithoutRequiredVariablesJson_DoesNotValidate()
    {
        var template = TemplateFactory.Build(
            templateId: "x",
            channel: NotificationChannel.Email,
            locale: "*",
            version: 1,
            isActive: true,
            subject: "static subject",
            body: "static body",
            textFallback: null,
            requiredVariables: null);

        var renderer = new ScribanTemplateRenderer();
        var rendered = renderer.Render(template, new Dictionary<string, object?>());

        rendered.Subject.Should().Be("static subject");
        rendered.Body.Should().Be("static body");
    }

    // --- TemplateSelector ----------------------------------------------------

    [Fact]
    public async Task SelectAsync_ExactLocaleAvailable_ReturnsExactMatch()
    {
        var enUs = TemplateFactory.Build("order.shipped", NotificationChannel.Email, "en-US", 2, true, "en-US-subject", "body", null, null);
        var wildcard = TemplateFactory.Build("order.shipped", NotificationChannel.Email, "*", 1, true, "wildcard-subject", "body", null, null);
        var repo = new Mock<ITemplateRepository>(MockBehavior.Strict);
        repo.Setup(r => r.GetVersionsAsync("order.shipped"))
            .ReturnsAsync(new List<NotificationTemplate> { enUs, wildcard });

        var selector = new TemplateSelector(repo.Object);

        var result = await selector.SelectAsync("order.shipped", "en-US", NotificationChannel.Email);

        result.Should().NotBeNull();
        result.Locale.Should().Be("en-US");
        result.SubjectTemplate.Should().Be("en-US-subject");
    }

    [Fact]
    public async Task SelectAsync_LocaleMissing_FallsBackToWildcard()
    {
        var wildcard = TemplateFactory.Build("order.shipped", NotificationChannel.Email, "*", 1, true, "wildcard-subject", "body", null, null);
        var repo = new Mock<ITemplateRepository>(MockBehavior.Strict);
        repo.Setup(r => r.GetVersionsAsync("order.shipped"))
            .ReturnsAsync(new List<NotificationTemplate> { wildcard });

        var selector = new TemplateSelector(repo.Object);

        var result = await selector.SelectAsync("order.shipped", "es-MX", NotificationChannel.Email);

        result.Should().NotBeNull();
        result.Locale.Should().Be("*");
    }

    [Fact]
    public async Task SelectAsync_NoActiveAndNoWildcard_ReturnsNull()
    {
        var inactiveEnUs = TemplateFactory.Build("order.shipped", NotificationChannel.Email, "en-US", 1, isActive: false, "x", "y", null, null);
        var repo = new Mock<ITemplateRepository>(MockBehavior.Strict);
        repo.Setup(r => r.GetVersionsAsync("order.shipped"))
            .ReturnsAsync(new List<NotificationTemplate> { inactiveEnUs });

        var selector = new TemplateSelector(repo.Object);

        var result = await selector.SelectAsync("order.shipped", "en-US", NotificationChannel.Email);

        ((object?)result).Should().BeNull();
    }

    [Fact]
    public async Task SelectAsync_MultipleVersionsActive_PicksHighestVersion()
    {
        var v1 = TemplateFactory.Build("order.shipped", NotificationChannel.Email, "en-US", 1, true, "v1", "body", null, null);
        var v3 = TemplateFactory.Build("order.shipped", NotificationChannel.Email, "en-US", 3, true, "v3", "body", null, null);
        var v2 = TemplateFactory.Build("order.shipped", NotificationChannel.Email, "en-US", 2, true, "v2", "body", null, null);
        var repo = new Mock<ITemplateRepository>(MockBehavior.Strict);
        repo.Setup(r => r.GetVersionsAsync("order.shipped"))
            .ReturnsAsync(new List<NotificationTemplate> { v1, v3, v2 });

        var selector = new TemplateSelector(repo.Object);

        var result = await selector.SelectAsync("order.shipped", "en-US", NotificationChannel.Email);

        result.Version.Should().Be(3);
        result.SubjectTemplate.Should().Be("v3");
    }

    [Fact]
    public async Task SelectAsync_OnlyInactiveActiveCandidates_SkipsInactiveAndPicksActive()
    {
        var v2Inactive = TemplateFactory.Build("order.shipped", NotificationChannel.Email, "en-US", 2, isActive: false, "v2", "body", null, null);
        var v1Active = TemplateFactory.Build("order.shipped", NotificationChannel.Email, "en-US", 1, isActive: true, "v1", "body", null, null);
        var repo = new Mock<ITemplateRepository>(MockBehavior.Strict);
        repo.Setup(r => r.GetVersionsAsync("order.shipped"))
            .ReturnsAsync(new List<NotificationTemplate> { v2Inactive, v1Active });

        var selector = new TemplateSelector(repo.Object);

        var result = await selector.SelectAsync("order.shipped", "en-US", NotificationChannel.Email);

        result.Version.Should().Be(1);
        result.IsActive.Should().BeTrue();
    }

    // --- Test helpers --------------------------------------------------------

    private static class TemplateFactory
    {
        // L1.A's NotificationTemplate.Create() factory body is still TODO at the
        // time of writing; reflection-based hydration mirrors the pattern used
        // by other test builders in the codebase and is contained in this file
        // so it can be deleted in a single PR once the canonical factory ships.
        public static NotificationTemplate Build(
            string templateId,
            NotificationChannel channel,
            string locale,
            int version,
            bool isActive,
            string subject,
            string body,
            string? textFallback,
            IReadOnlyCollection<string>? requiredVariables)
        {
            var ctor = typeof(NotificationTemplate)
                .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, Type.EmptyTypes)!;
            var template = (NotificationTemplate)ctor.Invoke(null);

            Set(template, nameof(NotificationTemplate.TemplateId), templateId);
            Set(template, nameof(NotificationTemplate.Name), templateId);
            Set(template, nameof(NotificationTemplate.Category), "test");
            Set(template, nameof(NotificationTemplate.Channel), channel.ToString());
            Set(template, nameof(NotificationTemplate.Locale), locale);
            Set(template, nameof(NotificationTemplate.Version), version);
            Set(template, nameof(NotificationTemplate.IsActive), isActive);
            Set(template, nameof(NotificationTemplate.SubjectTemplate), subject);
            Set(template, nameof(NotificationTemplate.BodyTemplate), body);
            Set(template, nameof(NotificationTemplate.TextFallbackTemplate), textFallback);
            Set(template, nameof(NotificationTemplate.RequiredVariablesJson),
                requiredVariables is null
                    ? null
                    : System.Text.Json.JsonSerializer.Serialize(requiredVariables));
            return template;
        }

        private static void Set(NotificationTemplate template, string property, object? value)
        {
            var prop = typeof(NotificationTemplate)
                .GetProperty(property, BindingFlags.Instance | BindingFlags.Public)!;
            prop.SetValue(template, value);
        }
    }
}
