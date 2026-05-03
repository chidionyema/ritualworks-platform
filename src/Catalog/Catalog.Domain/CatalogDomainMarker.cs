namespace Haworks.Catalog.Domain;

/// <summary>
/// Marker class — its existence anchors the <c>Haworks.Catalog.Domain</c>
/// namespace so global-using directives in dependent assemblies resolve
/// even before real entities (Phase 2b) populate it. Used by NetArchTest
/// in tests/Catalog.Architecture to grab this assembly via typeof().
/// </summary>
public sealed class CatalogDomainMarker { }
