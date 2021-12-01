﻿using System.Reflection;
using System.Text.Json;
using Chronic.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Nettle;
using Wabbajack.Common;
using Wabbajack.DTOs.ServerResponses;
using Wabbajack.Server.DataModels;
using Wabbajack.Server.DTOs;

namespace Wabbajack.BuildServer.Controllers;

[ApiController]
[Route("/metrics")]
public class MetricsController : ControllerBase
{
    private static readonly Func<object, string> ReportTemplate = NettleEngine.GetCompiler().Compile(@"
            <html><body>
                <h2>Tar Report for {{$.key}}</h2>
                <h3>Ban Status: {{$.status}}</h3>
                <table>
                {{each $.log }}
                <tr>
                <td>{{$.Timestamp}}</td>
                <td>{{$.Path}}</td>
                <td>{{$.Key}}</td>
                </tr>
                {{/each}}
                </table>
            </body></html>
        ");

    private static Func<object, string> _totalListTemplate;
    private readonly AppSettings _settings;
    private ILogger<MetricsController> _logger;
    private readonly Metrics _metricsStore;

    public MetricsController(ILogger<MetricsController> logger, Metrics metricsStore,
        AppSettings settings)
    {
        _logger = logger;
        _settings = settings;
        _metricsStore = metricsStore;
    }


    private static Func<object, string> TotalListTemplate
    {
        get
        {
            if (_totalListTemplate == null)
            {
                var resource = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("Wabbajack.Server.Controllers.Templates.TotalListTemplate.html")!
                    .ReadAllText();
                _totalListTemplate = NettleEngine.GetCompiler().Compile(resource);
            }

            return _totalListTemplate;
        }
    }

    [HttpGet]
    [Route("{subject}/{value}")]
    public async Task<Result> LogMetricAsync(string subject, string value)
    {
        var date = DateTime.UtcNow;
        var metricsKey = Request.Headers[_settings.MetricsKeyHeader].FirstOrDefault();

        // Used in tests
        if (value is "Default" or "untitled" || subject == "failed_download" || Guid.TryParse(value, out _))
            return new Result {Timestamp = date};

        await _metricsStore.Ingest(new Metric
        {
            Timestamp = DateTime.UtcNow, 
            Action = subject, 
            Subject = value, 
            MetricsKey = metricsKey,
            UserAgent = Request.Headers.UserAgent.FirstOrDefault() ?? "<unknown>",
        });
        return new Result {Timestamp = date};
    }

    private static byte[] EOL = {(byte)'\n'};
    
    [HttpGet]
    [Route("dump")]
    public async Task GetMetrics([FromQuery] string action, [FromQuery] string from, [FromQuery] string? to)
    {
        var parser = new Parser();
        
        to ??= "now";

        var toDate = parser.Parse(to).Start;
        var fromDate = parser.Parse(from).Start;

        var records = _metricsStore.GetRecords(fromDate!.Value, toDate!.Value, action);
        Response.Headers.ContentType = "application/json";
        await foreach (var record in records)
        {
            
            await JsonSerializer.SerializeAsync(Response.Body, record);
            await Response.Body.WriteAsync(EOL);
        }
    }
    
    [HttpGet]
    [Route("report")]
    [ResponseCache(Duration = 60 * 60 * 4, VaryByQueryKeys = new [] {"action", "from", "to"})]
    public async Task GetReport([FromQuery] string action, [FromQuery] string from, [FromQuery] string? to)
    {
        var parser = new Parser();
        
        to ??= "now";

        var toDate = parser.Parse(to).Start!.Value.TruncateToDate();
        
        var groupFilterStart = parser.Parse("three days ago").Start!.Value.TruncateToDate();
        toDate = new DateTime(toDate.Year, toDate.Month, toDate.Day);

        var prefetch = await _metricsStore.GetRecords(groupFilterStart, toDate, action)
            .Where((Func<MetricResult, bool>) (d => d.Action != d.Subject))
            .Select(async d => d.GroupingSubject)
            .ToHashSet();;
        
        var fromDate = parser.Parse(from).Start!.Value.TruncateToDate();

        var counts = new Dictionary<(DateTime, string), long>();

        await foreach (var record in _metricsStore.GetRecords(fromDate, toDate, action))
        {
            if (record.Subject == record.Action) continue;
            if (!prefetch.Contains(record.GroupingSubject)) continue;

            var key = (record.Timestamp.TruncateToDate(), record.GroupingSubject);
            if (counts.TryGetValue(key, out var old))
                counts[key] = old + 1;
            else
                counts[key] = 1;
        }

        Response.Headers.ContentType = "application/json";
        var row = new Dictionary<string, object>();
        for (var d = fromDate; d <= toDate; d = d.AddDays(1))
        {
            row["_Timestamp"] = d;
            foreach (var group in prefetch)
            {
                if (counts.TryGetValue((d, group), out var found))
                    row[group] = found;
                else
                    row[group] = 0;
            }
            await JsonSerializer.SerializeAsync(Response.Body, row);
            await Response.Body.WriteAsync(EOL);
        }

    }
    
    

    public class Result
    {
        public DateTime Timestamp { get; set; }
    }
}