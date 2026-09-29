using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using NCUT_Market.Api.Configuration;
using NCUT_Market.Api.Errors;
using NCUT_Market.Core.Common;
using NCUT_Market.Infrastructure;
using Scalar.AspNetCore;

var dotEnv = DotEnvLoader.LoadFromRepositoryRoot();

// A real environment variable outranks the value in .env. That is the conventional precedence — .env
// is a local-development convenience, and a value exported in the shell or injected by a deployment
// platform has to be able to override it. Getting this backwards is silent: WebApplicationOptions
// .EnvironmentName overrides ASPNETCORE_ENVIRONMENT, so reading .env first would make
// `$env:ASPNETCORE_ENVIRONMENT = 'Production'` do nothing at all, and the app would keep reporting
// Development with no indication why.
//
// The .env fallback is what lets `dotnet run` with no --launch-profile start in Development rather
// than falling through to the host's own Production default. The two variable names and their order
// mirror what the host would have consulted itself.
var environmentName =
    Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
    ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
    ?? dotEnv.GetValueOrDefault("Environment");

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    EnvironmentName = environmentName
});

builder.Configuration.AddInMemoryCollection(dotEnv);

builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddControllers();

builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        // Indexer for traceId, deliberately. The framework has already inserted a traceId taken from
        // Activity.Current — a W3C id like "00-<trace>-<span>-00" — which is a different value from
        // HttpContext.TraceIdentifier and never appears in the server log. Leaving it in place means a
        // user who quotes the traceId from an error response cannot be given the matching log line,
        // which is the only reason the member exists. Overwriting it with the connection identifier
        // makes the response and the log agree; ApiExceptionHandler logs this same value.
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;

        // TryAdd for the code, by contrast: whatever failed usually knows a more specific code than
        // the status implies, and it has already said so by the time this runs.
        context.ProblemDetails.Extensions.TryAdd(
            "code",
            CodeForStatus(context.ProblemDetails.Status ?? context.HttpContext.Response.StatusCode));
    };
});

builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    // [ApiController] turns a model-state failure into a bare 400 by default, which would carry no
    // code member and no traceId. Rebuild it as a proper problem response.
    options.InvalidModelStateResponseFactory = context =>
    {
        var problemDetailsFactory = context.HttpContext.RequestServices
            .GetRequiredService<ProblemDetailsFactory>();

        var problemDetails = problemDetailsFactory.CreateValidationProblemDetails(
            context.HttpContext,
            context.ModelState);

        // Status 400 alone would be reported as BAD_REQUEST; "the request body or query string did not
        // bind" is a more specific thing for a client to branch on.
        problemDetails.Extensions["code"] = ErrorCodes.ValidationError;

        return new BadRequestObjectResult(problemDetails)
        {
            ContentTypes = { "application/problem+json" }
        };
    };
});

builder.Services.AddExceptionHandler<ApiExceptionHandler>();

// Registered in every environment; only the document and UI endpoints below are Development-gated.
builder.Services.AddOpenApi();

var app = builder.Build();

// Must be the first middleware in this pipeline. In Development, WebApplication wraps the pipeline in
// the developer exception page, but this handler sits inside that and swallows the exception before
// it can reach the page — which is what keeps the response a problem+json body with no stack trace.
app.UseExceptionHandler();

// The frontend is served from the repository-root web/ directory, which sits outside this project,
// so the file provider is supplied explicitly instead of relying on the Web SDK's wwwroot
// convention. StaticWebRoot logs which candidate directory it picked.
//
// WebApplicationOptions.WebRootPath is deliberately NOT set. It is not validated — the builder only
// pushes the value into configuration, so a missing directory would not fail loudly there either.
// The real reason to keep away from it is that StaticFileMiddleware's own "web root not found"
// warning is gated on the provider being both NullFileProvider and the hosting environment's
// WebRootFileProvider; supplying our own provider through the options below makes a missing
// directory produce silent 404s instead. Going through StaticWebRoot keeps the warning path ours.
var webRoot = StaticWebRoot.Resolve(app.Configuration, app.Environment, app.Logger);

if (webRoot is not null)
{
    var fileProvider = new PhysicalFileProvider(webRoot);

    // UseDefaultFiles must come before UseStaticFiles: it does not serve anything itself, it only
    // rewrites "/" to "/index.html", and it is that rewritten path UseStaticFiles then serves.
    // Reversed, GET / stays a 404.
    //
    // Both go after UseExceptionHandler so a failure while serving a file still produces the
    // problem+json contract, and before MapControllers so they cannot shadow an endpoint. In
    // practice they cannot: UseStaticFiles passes through to the next middleware whenever no file
    // matches, and neither touches a path an endpoint has claimed.
    //
    // No MapFallbackToFile is needed. The UI routes on the fragment (#/categories), which the
    // browser never sends to the server, so the only server-side path the UI needs is "/".
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = fileProvider });
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapControllers();

// Migrations are applied explicitly with `dotnet ef database update` rather than on startup, so the
// schema is never changed as a side effect of booting the API.

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
    MapFaultInjectionEndpoints(app);
}

app.Run();

// The code member implied by an HTTP status, for problem responses that did not set one.
//
// Each branch must return the same code ApiExceptionHandler uses for that status, so a client
// branching on `code` never sees two values for one condition.
//
// Plain comments, not XML doc comments: these are local functions in top-level statements, and
// GenerateDocumentationFile makes CS1587 (documentation comment on an invalid element) an error.
static string CodeForStatus(int statusCode) => statusCode switch
{
    StatusCodes.Status400BadRequest => ErrorCodes.BadRequest,
    StatusCodes.Status401Unauthorized => ErrorCodes.Unauthorized,
    StatusCodes.Status403Forbidden => ErrorCodes.Forbidden,
    StatusCodes.Status404NotFound => ErrorCodes.NotFound,
    StatusCodes.Status405MethodNotAllowed => ErrorCodes.MethodNotAllowed,
    StatusCodes.Status409Conflict => ErrorCodes.Conflict,
    StatusCodes.Status415UnsupportedMediaType => ErrorCodes.UnsupportedMediaType,
    StatusCodes.Status501NotImplemented => ErrorCodes.NotImplemented,
    StatusCodes.Status504GatewayTimeout => ErrorCodes.Timeout,
    >= StatusCodes.Status500InternalServerError => ErrorCodes.InternalError,
    _ => ErrorCodes.BadRequest
};

// Development-only fault injector: /dev/errors/throw/{kind}.
//
// There is no natural way to make the read-only slice fail — stopping MySQL produces a connection
// error, not a clean INTERNAL_ERROR — so this is the only way to exercise the whole mapping table
// from a terminal. It is mapped inside the IsDevelopment() block and is meant to be deleted whenever
// its usefulness runs out.
//
// Nothing here touches the database; these are thrown values, not provoked failures.
static void MapFaultInjectionEndpoints(WebApplication app)
{
    // The return type is written out and the exception comes from a helper, rather than throwing
    // straight from a switch expression: a lambda whose body is only a throw has no inferred return
    // type, so overload resolution falls back to the RequestDelegate overload and fails to bind.
    app.MapGet("/dev/errors/throw/{kind}", IResult (string kind) =>
    {
        throw CreateInjectedException(kind);
    });
}

// Maps a fault-injection kind to the exception it stands for.
static Exception CreateInjectedException(string kind) => kind switch
{
    "bad-request" => new BadHttpRequestException("Injected: malformed request."),
    "invalid-argument" => new ArgumentException("Injected: invalid argument."),
    "forbidden" => new UnauthorizedAccessException("Injected: access denied."),
    "not-found" => new KeyNotFoundException("Injected: resource not found."),
    // Not a real write — the type alone is what the handler maps to 409.
    "conflict" => new DbUpdateException("Injected: write conflict."),
    "not-implemented" => new NotImplementedException("Injected: not built yet."),
    "timeout" => new TimeoutException("Injected: operation timed out."),
    // The catch-all, so an unknown kind lands on INTERNAL_ERROR rather than 404.
    _ => new InvalidOperationException($"Injected: unexpected failure ({kind}).")
};
