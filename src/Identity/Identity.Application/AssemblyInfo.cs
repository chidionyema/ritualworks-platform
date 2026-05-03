// Expose internal handlers + types to the unit-test assembly so tests
// can construct them directly without going through MediatR + DI.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Haworks.Identity.Unit")]
