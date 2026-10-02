using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NCUT_Market.Api.Configuration;
using NCUT_Market.Api.Errors;
using NCUT_Market.Core.Common;
using NCUT_Market.Infrastructure;
using NCUT_Market.Infrastructure.Security;
using NCUT_Market.Infrastructure.Storage;
using Scalar.AspNetCore;

var dotEnv = DotEnvLoader.LoadFromRepositoryRoot();

// Precedence, stated accurately: the environment NAME is resolved from real environment variables
// first, but every other key ends up taking its value from .env. That is because the .env
// dictionary is appended to the configuration as an in-memory source below, and a source added
// later wins. So `$env:ConnectionStrings__DefaultConnection` does NOT override .env — only
// ASPNETCORE_ENVIRONMENT / DOTNET_ENVIRONMENT do, because they are read explicitly here.
//
// This is known and deliberate for now: .env is the single place local settings live, and moving a
// deployment to real environment variables is a change for later. It is written down because it is
// the opposite of what most .NET code does, and it is what forces the HTTP test factory to replace
// the DbContext registration outright rather than setting a connection string.
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

// Uploaded images live under the content root, which resolves correctly in both layouts without a
// candidate search: under `dotnet run` it is the project directory, and after `dotnet publish` it is
// the publish output. `??=` leaves an explicit Storage__UploadRoot from configuration in place.
builder.Configuration["Storage:UploadRoot"] ??=
    Path.Combine(builder.Environment.ContentRootPath, "data", "uploads");

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

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();

// Registered as an options configurator rather than configured inline, so the validation parameters
// are built lazily from IOptions<JwtOptions> — see ConfigureJwtBearerOptions for why that matters.
builder.Services.AddSingleton<IConfigureOptions<JwtBearerOptions>, ConfigureJwtBearerOptions>();

builder.Services.AddAuthorization();

// Replaces the framework's default, which answers a failed [Authorize] with a bare status code and
// an empty body — no code, no traceId, nothing for the frontend's error card to render.
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, AuthorizationProblemHandler>();

// Registered in every environment; only the document and UI endpoints below are Development-gated.
// The transformer adds the bearer scheme, which is what gives the Scalar page a token field.
builder.Services.AddOpenApi(options =>
    options.AddDocumentTransformer<BearerSecuritySchemeTransformer>());

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

// Uploaded product images, mounted from the same directory ImageStorage writes to. Kept separate
// from the frontend mount above because the two have different lifetimes and different trust:
// web/ is source that ships with the build, data/uploads is content users create at runtime.
//
// ServeUnknownFileTypes is left off, which is the meaningful default here. Every file is written by
// ImageStorage with an extension it chose after decoding the bytes, so only .jpg/.png/.webp can
// exist — but if one ever appeared with an extension the provider does not map, this refuses to
// serve it rather than guessing a content type and handing the browser something executable.
var uploadRoot = app.Services.GetRequiredService<IOptions<StorageOptions>>().Value.UploadRoot;

Directory.CreateDirectory(uploadRoot);

// Logged because the root is derived from ContentRootPath by default but overridable from
// configuration, and the two land in different places after a publish (see the ??= above). Without
// this line the only way to find out which one won is to upload a file and go looking for it.
app.Logger.LogInformation("Product images are served from {UploadRoot}", uploadRoot);

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(uploadRoot),
    RequestPath = app.Services.GetRequiredService<IOptions<StorageOptions>>().Value.PublicBasePath,

    // Stops a browser from second-guessing the content type it was served. The files here are
    // re-encoded images so there is nothing to sniff out, but the header is what makes that a
    // property of the response rather than a hope about the bytes.
    OnPrepareResponse = context =>
        context.Context.Response.Headers["X-Content-Type-Options"] = "nosniff"
});

// Order matters: authentication establishes who is calling, authorization decides whether they may.
// Both sit after UseExceptionHandler so an authentication failure still produces the problem+json
// contract, and before the endpoints so every mapped route sees the principal.
app.UseAuthentication();
app.UseAuthorization();

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

// Touches the bearer options once so a missing or malformed signing key fails here, at startup,
// rather than on the first request that needs a token. JwtBearerOptions are otherwise built lazily,
// and an API that boots happily and then refuses every login is a worse way to find out.
//
// This runs after builder.Build(), so a test host's PostConfigure<JwtOptions> has already applied.
_ = app.Services.GetRequiredService<IOptions<JwtBearerOptions>>().Value;

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

// Top-level statements compile the generated entry point into an INTERNAL Program class, which a
// test project cannot name. This declaration — same assembly, same namespace — makes it public. It
// adds no members and changes no behaviour; it exists so WebApplicationFactory<Program> resolves.
//
// It lives here rather than in its own file because this is where anyone looks when the test
// project stops compiling.
public partial class Program;
