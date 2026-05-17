using System.Reflection;
using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Rocks;
using NetArchTest.Rules;
using Xunit;

namespace Haworks.Platform.ArchitecturalGuards;

/// <summary>
/// Assembly-level architecture rules using NetArchTest (ArchUnit for .NET).
/// These validate dependency direction and layer isolation at the type level,
/// not just file-pattern scanning. Inspired by:
/// https://medium.com/@bnayae/proactive-architecture-guarding-b71c4a77a0ec
/// </summary>
public sealed class NetArchTests
{
    // ─── Clean Architecture: Domain must not depend on anything ───────

    [Fact]
    public void Payments_Domain_has_no_dependency_on_Application()
    {
        var result = Types.InAssembly(typeof(Haworks.Payments.Domain.Payment).Assembly)
            .ShouldNot()
            .HaveDependencyOn("Haworks.Payments.Application")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Domain layer must not reference Application — dependency inversion violation");
    }

    [Fact]
    public void Payments_Domain_has_no_dependency_on_Infrastructure()
    {
        var result = Types.InAssembly(typeof(Haworks.Payments.Domain.Payment).Assembly)
            .ShouldNot()
            .HaveDependencyOn("Haworks.Payments.Infrastructure")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Domain layer must not reference Infrastructure");
    }

    [Fact]
    public void Payments_Application_has_no_dependency_on_Infrastructure()
    {
        var result = Types.InAssembly(typeof(Haworks.Payments.Application.DependencyInjection).Assembly)
            .ShouldNot()
            .HaveDependencyOn("Haworks.Payments.Infrastructure")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Application layer must not reference Infrastructure — use interfaces");
    }

    [Fact]
    public void Payouts_Domain_has_no_dependency_on_Application_or_Infrastructure()
    {
        var result = Types.InAssembly(typeof(Haworks.Payouts.Domain.Aggregates.Payout).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Haworks.Payouts.Application",
                "Haworks.Payouts.Infrastructure")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Payouts Domain must not reference Application or Infrastructure");
    }

    // ─── Domain entities must be classes (not records) ────────────────

    [Fact]
    public void Domain_entities_are_classes_not_records()
    {
        var domainTypes = Types.InAssembly(typeof(Haworks.Payments.Domain.Payment).Assembly)
            .That()
            .HaveNameEndingWith("State")
            .Or()
            .Inherit(typeof(Haworks.BuildingBlocks.Persistence.AuditableEntity))
            .GetTypes();

        foreach (var type in domainTypes)
        {
            type.IsClass.Should().BeTrue($"{type.Name} must be a class, not a record/struct (EF change tracking)");
            // Records have a compiler-generated <Clone>$ method
            type.GetMethod("<Clone>$").Should().BeNull($"{type.Name} is a record — EF entities must be classes");
        }
    }

    // ─── No service-to-service direct references ─────────────────────

    [Fact]
    public void Payments_does_not_reference_Orders_or_Catalog_directly()
    {
        var result = Types.InAssembly(typeof(Haworks.Payments.Domain.Payment).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Haworks.Orders",
                "Haworks.Catalog",
                "Haworks.Content",
                "Haworks.Identity")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Payments must not directly reference sibling services — communicate via events");
    }

    // ─── Handlers/Consumers must be sealed ────────────────────────────

    [Fact]
    public void Consumers_and_handlers_should_be_sealed()
    {
        var types = Types.InAssembly(typeof(Haworks.Payments.Application.DependencyInjection).Assembly)
            .That()
            .HaveNameEndingWith("Handler")
            .Or()
            .HaveNameEndingWith("Consumer")
            .GetTypes();

        foreach (var type in types)
        {
            if (type.IsAbstract || type.IsInterface) continue;
            type.IsSealed.Should().BeTrue(
                $"{type.Name} should be sealed — prevents unintended inheritance and improves performance");
        }
    }

    // ─── Domain must not use MediatR directly (only contracts) ────────

    [Fact]
    public void Domain_does_not_reference_MediatR()
    {
        var result = Types.InAssembly(typeof(Haworks.Payments.Domain.Payment).Assembly)
            .ShouldNot()
            .HaveDependencyOn("MediatR")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Domain must not reference MediatR — it's an application concern");
    }

    // ─── BuildingBlocks must not reference any service ────────────────

    [Fact]
    public void BuildingBlocks_does_not_reference_any_service()
    {
        var result = Types.InAssembly(typeof(Haworks.BuildingBlocks.Common.BrandOptions).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Haworks.Payments",
                "Haworks.Payouts",
                "Haworks.Orders",
                "Haworks.Catalog",
                "Haworks.Identity",
                "Haworks.Notifications")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "BuildingBlocks is shared infrastructure — must not reference any service");
    }

    // ─── Reusable base component enforcement (IL-level) ───────────────
    //
    // These tests are ASPIRATIONAL — they document the target architecture.
    // Remove the Skip attribute after all handlers/consumers have been
    // migrated to the reusable base classes (IdempotentConsumerBase,
    // ThreePhaseHandlerBase). Track progress in the migration backlog.
    // ──────────────────────────────────────────────────────────────────

    [Fact(Skip = "Migration in progress — will enforce after all handlers are migrated")]
    public void Consumers_In_Financial_Services_Must_Inherit_IdempotentConsumerBase()
    {
        var financialAssemblies = new[]
        {
            typeof(Haworks.Payments.Application.DependencyInjection).Assembly,
            typeof(Haworks.Payouts.Infrastructure.DependencyInjection).Assembly,
        };

        var consumerTypes = financialAssemblies
            .SelectMany(a => Types.InAssembly(a)
                .That()
                .ImplementInterface(typeof(MassTransit.IConsumer<>))
                .GetTypes())
            .Where(t => !t.IsAbstract && !t.IsInterface)
            .ToList();

        consumerTypes.Should().NotBeEmpty("there should be consumers in financial services");

        foreach (var type in consumerTypes)
        {
            var inheritsIdempotentBase = InheritsGenericBase(type, "IdempotentConsumerBase");
            inheritsIdempotentBase.Should().BeTrue(
                $"{type.FullName} implements IConsumer<> but does not inherit " +
                "IdempotentConsumerBase<,> — financial consumers must use the idempotent base " +
                "to guarantee exactly-once processing");
        }
    }

    [Fact(Skip = "Migration in progress — will enforce after all handlers are migrated")]
    public void Handlers_With_External_Gateways_Must_Not_Call_BeginTransactionAsync_Raw()
    {
        var financialAssemblies = new[]
        {
            typeof(Haworks.Payments.Infrastructure.DependencyInjection).Assembly,
            typeof(Haworks.Payouts.Infrastructure.DependencyInjection).Assembly,
        };

        var result = Types.InAssemblies(financialAssemblies)
            .That()
            .AreClasses()
            .And()
            .AreNotAbstract()
            .Should()
            .MeetCustomRule(new NoRawTransactionManagementRule())
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Types that call BeginTransactionAsync must inherit ThreePhaseHandlerBase. " +
            $"Violating types: {string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
    }

    [Fact(Skip = "Migration in progress — will enforce after all handlers are migrated")]
    public void Handlers_With_Gateway_Dependencies_Must_Use_ThreePhaseHandler()
    {
        var financialAssemblies = new[]
        {
            typeof(Haworks.Payments.Application.DependencyInjection).Assembly,
            typeof(Haworks.Payments.Infrastructure.DependencyInjection).Assembly,
            typeof(Haworks.Payouts.Application.DependencyInjection).Assembly,
            typeof(Haworks.Payouts.Infrastructure.DependencyInjection).Assembly,
        };

        var gatewayInterfaceNames = new[]
        {
            "IPayoutGateway",
            "IStripeClientFactory",
            "IPaymentProcessor",
            "IRefundService",
        };

        var handlerTypes = financialAssemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => t.GetConstructors()
                .SelectMany(c => c.GetParameters())
                .Any(p => gatewayInterfaceNames.Contains(p.ParameterType.Name)))
            .ToList();

        foreach (var type in handlerTypes)
        {
            var inheritsThreePhase = InheritsGenericBase(type, "ThreePhaseHandlerBase");
            inheritsThreePhase.Should().BeTrue(
                $"{type.FullName} depends on a payment/payout gateway but does not inherit " +
                "ThreePhaseHandlerBase — handlers with external gateway calls must use the " +
                "three-phase pattern (validate → execute → persist) to ensure consistency");
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private static bool InheritsGenericBase(Type type, string baseTypeName)
    {
        var current = type.BaseType;
        while (current is not null)
        {
            var name = current.IsGenericType ? current.GetGenericTypeDefinition().Name : current.Name;
            if (name.StartsWith(baseTypeName, StringComparison.Ordinal))
                return true;
            current = current.BaseType;
        }
        return false;
    }
}

/// <summary>
/// Mono.Cecil IL-inspection rule that blocks raw BeginTransactionAsync calls
/// outside of ThreePhaseHandlerBase-derived types. This ensures all transaction
/// management goes through the three-phase pattern.
/// </summary>
public sealed class NoRawTransactionManagementRule : NetArchTest.Rules.ICustomRule
{
    public bool MeetsRule(TypeDefinition type)
    {
        // Types inheriting ThreePhaseHandlerBase are allowed to manage transactions
        if (InheritsThreePhaseHandler(type))
            return true;

        // Scan all method bodies for BeginTransactionAsync calls
        foreach (var method in type.Methods.Where(m => m.HasBody))
        {
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.Operand is MethodReference mr
                    && string.Equals(mr.Name, "BeginTransactionAsync", StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool InheritsThreePhaseHandler(TypeDefinition type)
    {
        var current = type.BaseType;
        while (current is not null)
        {
            if (current.Name.StartsWith("ThreePhaseHandlerBase", StringComparison.Ordinal))
                return true;

            try
            {
                current = current.Resolve()?.BaseType;
            }
            catch
            {
                break;
            }
        }
        return false;
    }
}
