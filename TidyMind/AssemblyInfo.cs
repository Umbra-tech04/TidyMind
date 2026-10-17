using System.Windows;

[assembly: ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]

// The test harness (tests/Harness) points the update check at its own local release server (Updates.Endpoints).
// Nothing a user can set or start reaches that: production always uses UpdateEndpoints.Production.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Harness")]
