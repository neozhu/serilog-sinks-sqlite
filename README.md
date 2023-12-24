# Blazor.Serilog.Sinks.SQLite
[![.NET](https://github.com/neozhu/serilog-sinks-sqlite/actions/workflows/dotnet.yml/badge.svg)](https://github.com/neozhu/serilog-sinks-sqlite/actions/workflows/dotnet.yml)
[![Package](https://github.com/neozhu/serilog-sinks-sqlite/actions/workflows/package.yml/badge.svg)](https://github.com/neozhu/serilog-sinks-sqlite/actions/workflows/package.yml)

A lightweight high performance Serilog sink that writes to SQLite database for Clean Architecture Blazor Server Application 

## Getting started
Install [Blazor.Serilog.Sinks.SQLite](https://www.nuget.org/packages/Serilog.Sinks.SQLite) from NuGet

```PowerShell
Install-Package Blazor.Serilog.Sinks.SQLite
```

Configure logger by calling `WriteTo.SQLite()`

```C#
private static void WriteToSqLite(LoggerConfiguration serilogConfig, string? connectionString)
{
    if (string.IsNullOrEmpty(connectionString)) return;
    var sqlPath = Environment.CurrentDirectory + @"/app.db";
    const string tableName = "Loggers";
    serilogConfig.WriteTo.Async(wt => wt.SQLite(
        sqlPath,
        tableName,
        LogEventLevel.Information
    ).CreateLogger());
}
    
logger.Information("This informational message will be written to SQLite database");
```

## XML <appSettings> configuration

To use the SQLite sink with the [Serilog.Settings.AppSettings](https://www.nuget.org/packages/Serilog.Settings.AppSettings) package, first install that package if you haven't already done so:



```C#
var logger = new LoggerConfiguration()
    .ReadFrom.AppSettings()
    .CreateLogger();
```


## Performance
SQLite sink automatically buffers log internally and flush to SQLite database in batches on dedicated thread.

[![Build status](https://ci.appveyor.com/api/projects/status/sqjvxji4w84iyqa0?svg=true)](https://ci.appveyor.com/project/SaleemMirza/serilog-sinks-sqlite)
