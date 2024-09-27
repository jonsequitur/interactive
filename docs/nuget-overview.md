# Working with NuGet packages

The C# and F# kernels in .NET Interactive allow you to import NuGet packages into your interactive session using the `#r nuget` magic command. The syntax is the same in both languages.

To import the latest release version a package, you can use `#r nuget` without specifying a version number:

```csharp
#r "nuget:System.Text.Json"
```

If you'd like to use a specific version, you can specify it like this:

```csharp
#r "nuget:System.Text.Json,4.7.2"
```

If you're looking for the latest pre-release version, you can specify it like this:

```csharp
#r "nuget:System.Text.Json,*-*"
```

## Adding a NuGet Source

If your NuGet package is not hosted on the main NuGet feed you can specify an alternative NuGet source using `#i`. 

### Remote NuGet Sources

It is common for organizations to store packages on a private or pre-release feed. In the following example we are adding the [dotnet project](https://github.com/dotnet) pre-release NuGet feed.

```csharp
#i "nuget:https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-tools/nuget/v3/index.json"
```

### Local Nuget Sources

You can also use a local folder as a NuGet source:

```csharp
#i "nuget:C:\myorg\mypackage\src\bin\Release"
#r "nuget:MyOrg.MyPackage"
```

### Authenticated feeds

.NET Interactive does not directly support accessing package feeds that require authentication, but you can access authenticated feeds by putting a PAT for the feed into your user-level `nuget.config` file. You can read more about this approach [here](https://learn.microsoft.com/en-us/azure/devops/artifacts/nuget/nuget-exe?view=azure-devops). 
