using ComplianceMonitor.Api.Data;
using ComplianceMonitor.Api.Models;
using ComplianceMonitor.Api.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ComplianceDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Compliance") ?? "Data Source=compliance.db"));

builder.Services.AddHttpClient<
    IComplianceClassifier,
    HuggingFaceComplianceClassifier>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
});

var app = builder.Build();

// Validate body-encoding headers before Minimal API request binding.
app.Use(async (context, next) =>
{
    if (HttpMethods.IsPost(context.Request.Method)
        && string.Equals(context.Request.Path.Value?.TrimEnd('/'), "/analyze", StringComparison.OrdinalIgnoreCase))
    {
        var encodings = context.Request.Headers.ContentEncoding
            .SelectMany(value => (value ?? "").Split(','));
        if (encodings.Any(value => !string.Equals(value.Trim(), "identity", StringComparison.OrdinalIgnoreCase)))
        {
            await Results.Problem("Request content encoding is not supported.", statusCode: 415).ExecuteAsync(context);
            return;
        }

        if (System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType)
            && !string.IsNullOrEmpty(contentType.CharSet))
        {
            try
            {
                var charset = contentType.CharSet.Trim('"');
                _ = System.Text.Encoding.GetEncoding(charset);
                // Normalize a valid quoted charset before framework JSON binding.
                contentType.CharSet = charset;
                context.Request.ContentType = contentType.ToString();
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                await Results.Problem("Request charset is not supported.", statusCode: 400).ExecuteAsync(context);
                return;
            }
        }
    }
    await next(context);
});

app.MapPost("/analyze", async (
    AnalyzeRequest request,
    IComplianceClassifier classifier,
    ComplianceDbContext db,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Action) ||
        string.IsNullOrWhiteSpace(request.Guideline))
    {
        return Results.BadRequest(new
        {
            error = "Action and guideline are required."
        });
    }

    ClassificationResult classification;

    try
    {
        classification = await classifier.ClassifyAsync(
            request.Action,
            request.Guideline,
            cancellationToken);
    }
    catch (HttpRequestException)
    {
        return Results.Problem(
            "The classification service is unavailable.",
            statusCode: 502);
    }
    catch (OperationCanceledException)
        when (!cancellationToken.IsCancellationRequested)
    {
        return Results.Problem(
            "The classification service timed out.",
            statusCode: 504);
    }

    catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException)
    {
        return Results.Problem("The classification service returned an invalid response or is not configured.", statusCode: 502);
    }

    var record = new AnalysisRecord
    {
        Action = request.Action,
        Guideline = request.Guideline,
        Result = classification.Result,
        Confidence = classification.Confidence,
        Timestamp = DateTime.UtcNow
    };

    db.Analyses.Add(record);
    await db.SaveChangesAsync(cancellationToken);

    var result = new AnalyzeResponse(
        record.Action,
        record.Guideline,
        record.Result,
        record.Confidence,
        record.Timestamp);

    return Results.Ok(result);
});

app.MapGet("/history", async (ComplianceDbContext db, CancellationToken cancellationToken) =>
{
    var history = await db.Analyses
        .AsNoTracking()
        .OrderByDescending(record => record.Timestamp)
        .ThenByDescending(record => record.Id)
        .Select(record => new AnalyzeResponse(
            record.Action,
            record.Guideline,
            record.Result,
            record.Confidence,
            record.Timestamp))
        .ToListAsync(cancellationToken);

    return Results.Ok(history);
});

app.MapGet("/summary", async (ComplianceDbContext db, CancellationToken cancellationToken) =>
{
    var counts = await db.Analyses
        .AsNoTracking()
        .GroupBy(record => record.Result)
        .Select(group => new
        {
            Result = group.Key,
            Count = group.Count()
        })
        .ToListAsync(cancellationToken);

    var byResult = new Dictionary<string, int>
    {
        ["COMPLIES"] = 0,
        ["DEVIATES"] = 0,
        ["UNCLEAR"] = 0
    };

    foreach (var count in counts)
    {
        byResult[count.Result] = count.Count;
    }

    return Results.Ok(new SummaryResponse(
        byResult.Values.Sum(),
        byResult));
});

app.Run();

public partial class Program { }
