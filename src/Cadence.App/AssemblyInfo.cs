using System.Windows;

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]

// The HUD's token formatters are internal because they are presentation detail, and are exactly
// what the tests need to pin: the strip's whole value is that its forty characters read correctly.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Cadence.App.Tests")]
