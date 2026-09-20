using Xunit;

// Los tests con MassTransit comparten el fabric in-memory del proceso:
// se ejecutan en serie para que los buses de cada test no se interfieran.
// (El paralelismo DENTRO de cada test —p.ej. 200 holds concurrentes— no se ve afectado.)
[assembly: CollectionBehavior(DisableTestParallelization = true)]
