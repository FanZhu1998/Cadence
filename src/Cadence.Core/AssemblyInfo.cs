using System.Runtime.CompilerServices;

// Parser helpers are internal because they are implementation detail of the provider layer, but
// they are exactly what the golden-file tests need to pin.
[assembly: InternalsVisibleTo("Cadence.Core.Tests")]
[assembly: InternalsVisibleTo("Cadence.Forecast.Tests")]
