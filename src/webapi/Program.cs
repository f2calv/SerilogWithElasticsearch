﻿using CasCap;
using CasCap.Models;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using Serilog.Core;
using Serilog.Enrichers.OpenTracing;
using Serilog.Events;
using Serilog.Exceptions;
using Serilog.Formatting.Elasticsearch;
using Serilog.Sinks.Elasticsearch;
using Serilog.Sinks.MSSqlServer;
using Serilog.Sinks.SystemConsole.Themes;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development";
//preload basic serilog settings from local json file
var configuration = new ConfigurationBuilder().AddJsonFile("appsettings.json").AddJsonFile($"appsettings.{environment}.json", optional: true).Build();
var loggerConfiguration = new LoggerConfiguration().ReadFrom.Configuration(configuration);
var appInsightsConfig = configuration.GetSection($"{nameof(CasCap)}:{nameof(AppInsightsConfig)}").Get<AppInsightsConfig>();
var connectionStrings = configuration.GetSection(nameof(ConnectionStrings)).Get<ConnectionStrings>();

//set-up application insights telemetry sink (optional)
if (!string.IsNullOrWhiteSpace(appInsightsConfig.InstrumentationKey))
{
    var telemetryConfiguration = TelemetryConfiguration.CreateDefault();
    telemetryConfiguration.InstrumentationKey = appInsightsConfig.InstrumentationKey;
    loggerConfiguration.WriteTo.ApplicationInsights(telemetryConfiguration, TelemetryConverter.Traces, restrictedToMinimumLevel: LogEventLevel.Debug);
}

if (!string.IsNullOrWhiteSpace(connectionStrings.mssql))
{
    if (!IsSqlServerOnline(connectionStrings.mssql))
        Log.Error("Unable to connect to MSSQL :(");
    else
        loggerConfiguration.WriteTo.MSSqlServer(
            connectionString: connectionStrings.mssql,
            sinkOptions: new MSSqlServerSinkOptions { TableName = "LogEvents", AutoCreateSqlTable = true });
}

var levelSwitch = new LoggingLevelSwitch();//this would have to be a singleton or something and passed in?

//add all the log enrichers
Log.Logger = loggerConfiguration
    .MinimumLevel.ControlledBy(levelSwitch)
    .Enrich.FromLogContext()
    //.Enrich.WithMachineName()
    .Enrich.WithEnvironmentUserName()
    .Enrich.WithProcessId()
    .Enrich.WithThreadId()
    .Enrich.WithExceptionDetails()
    .Enrich.WithAssemblyName()
    .Enrich.WithOpenTracingContext()
    .Enrich.WithProperty("ApplicationContext", AppDomain.CurrentDomain.FriendlyName)
    .Enrich.WithProperty("Version", "1.0.0")//const enricher
    .Enrich.With(new ThreadIdEnricher())//dynamic enricher

    .Destructure.ByTransforming<TestObj>(
        r => new { dt = r.utcNow, sid = r.id.ToString(), wibble = "wobble" })

    .WriteTo.File("log.txt", rollingInterval: RollingInterval.Day)

    .WriteTo.Console(theme: AnsiConsoleTheme.Code, applyThemeToRedirectedOutput: true)//local development pretty print console logging
                                                                                      //.WriteTo.Console(outputTemplate: "{Timestamp:HH:mm} [{Level}] ({ThreadId}) {Message}{NewLine}{Exception}")
                                                                                      //.WriteTo.Console(new ElasticsearchJsonFormatter())
                                                                                      //.WriteTo.Console(new ExceptionAsObjectJsonFormatter())//or output as json object for production+filebeat

    .WriteTo.Seq(connectionStrings.seq)

    .WriteTo.Elasticsearch(new ElasticsearchSinkOptions(new Uri(connectionStrings.elasticsearch))
    {
        //IndexFormat = "workerservice-{0:yyyy.MM.dd}",
        //IndexFormat = AppDomain.CurrentDomain.FriendlyName + "-{0:yyyy.MM}",
        //IndexFormat = "AdminLogs-{0:yyyy.MM.dd}",
        IndexFormat = $"{Assembly.GetExecutingAssembly().GetName().Name.ToLower().Replace(".", "-")}-{environment?.ToLower().Replace(".", "-")}-{DateTime.UtcNow:yyyy-MM}",
        AutoRegisterTemplate = true,
        AutoRegisterTemplateVersion = AutoRegisterTemplateVersion.ESv7,
        //OverwriteTemplate = true,
        //RegisterTemplateFailure = RegisterTemplateRecovery.IndexToDeadletterIndex,
        EmitEventFailure = EmitEventFailureHandling.WriteToSelfLog |
                           EmitEventFailureHandling.RaiseCallback |
                           EmitEventFailureHandling.ThrowException |
                           EmitEventFailureHandling.WriteToFailureSink,
        FailureCallback = e =>
        {
            Log.Error("Unable to submit event {MessageTemplate}", e.MessageTemplate);
            //Console.WriteLine("Unable to submit event " + e.MessageTemplate);
        },
        BufferCleanPayload = (failingEvent, statuscode, exception) =>
        {
            dynamic e = JObject.Parse(failingEvent);
            var d = new Dictionary<string, object>
            {
                { "@timestamp", e["@timestamp"]},
                { "level", "Error"},
                { "message", "Error: " + e.message},
                { "messageTemplate", e.messageTemplate},
                { "failingStatusCode", statuscode},
                { "failingException", exception}
            };
            return JsonConvert.SerializeObject(d);
        },
        MinimumLogEventLevel = LogEventLevel.Verbose,
        CustomFormatter = new ExceptionAsObjectJsonFormatter(renderMessage: true),
    })

    //.WriteTo.AzureAnalytics(workspaceId: < id removed >,
    //    authenticationId: < id removed >,
    //    logName: "wibble123",
    //    restrictedToMinimumLevel: LogEventLevel.Debug,
    //    //logBufferSize:5,
    //    batchSize: 10
    //    )

    .WriteTo.AzureBlobStorage(connectionString: connectionStrings.azureStorage,
        restrictedToMinimumLevel: LogEventLevel.Debug,
        storageContainerName: AppDomain.CurrentDomain.FriendlyName,
        storageFileName: "{yyyy}/{MM}/{dd}/log.txt"
        )

    .WriteTo.AzureTableStorage(connectionStrings.azureStorage,
        storageTableName: AppDomain.CurrentDomain.FriendlyName)

    //serilog to redis?

    //.Filter.ByExcluding($"RequestPath like '/healthz%'")
    .CreateLogger();
var result = 0;
try
{
    Log.Information("Starting {AppName}", AppDomain.CurrentDomain.FriendlyName);
    Host.CreateDefaultBuilder(args)
        .ConfigureAppConfiguration((hostContext, config) => { })
        .ConfigureWebHostDefaults(webBuilder =>
        {
            webBuilder.UseStartup<Startup>();
        })
        .UseSerilog()
        .Build().Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "{AppName} terminated unexpectedly", AppDomain.CurrentDomain.FriendlyName);
    result = 1;
}
finally
{
    Log.CloseAndFlush();
}
return result;

bool IsSqlServerOnline(string connectionString, int timeout = 5)
{
    using (var connection = new SqlConnection($"{connectionString};Connection Timeout={timeout}"))
    {
        try
        {
            connection.Open();
            return true;
        }
        catch (SqlException)
        {
            return false;
        }
    }
}

class ThreadIdEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(
            "ThreadId", Thread.CurrentThread.ManagedThreadId));
    }
}