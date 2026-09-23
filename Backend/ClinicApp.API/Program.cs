using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using ClinicApp.API.Middleware;
using ClinicApp.Application.Interfaces;
using ClinicApp.Domain.Entities;
using ClinicApp.Infrastructure.Data;
using ClinicApp.Infrastructure.Helpers;
using ClinicApp.Infrastructure.Services.Implementations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
var builder = WebApplication.CreateBuilder(args);

// Lokalne postavke za hosting (connection string, JWT kljuc, CORS...).
// appsettings.Local.json je u .gitignore i NE ide na GitHub.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

// ------------------------------------------------------
// CONFIG VALIDATION
// ------------------------------------------------------
string RequireConfig(string key)
{
    var value = builder.Configuration[key];
    if (string.IsNullOrWhiteSpace(value))
        throw new InvalidOperationException($"Missing required configuration: {key}");

    return value;
}

var defaultConnection = RequireConfig("ConnectionStrings:DefaultConnection");
var jwtKey = RequireConfig("Jwt:Key");
var jwtIssuer = RequireConfig("Jwt:Issuer");
var jwtAudience = RequireConfig("Jwt:Audience");

if (jwtKey.Length < 32)
    throw new InvalidOperationException("Jwt:Key must be at least 32 characters long.");

// ------------------------------------------------------
// DATABASE
// ------------------------------------------------------
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(defaultConnection, sql =>
    {
        sql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(5), null);
        sql.CommandTimeout(30);
    }));

// ------------------------------------------------------
// SERVICES
// ------------------------------------------------------
builder.Services.AddControllers();
builder.Services.AddHealthChecks();

builder.Services.AddScoped<ILookupService, LookupService>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IMedicationService, MedicationService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<IMedicationHistoryService, MedicationHistoryService>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<PushNotificationService>();

// ------------------------------------------------------
// CORS
// ------------------------------------------------------
const string AngularCorsPolicy = "AngularClient";

builder.Services.AddCors(options =>
{
    options.AddPolicy(AngularCorsPolicy, policy =>
    {
        var origins = builder.Configuration
            .GetSection("Cors:AllowedOrigins")
            .Get<string[]>();

        if (origins != null && origins.Length > 0)
        {
            policy
                .WithOrigins(origins)
                .AllowAnyHeader()
                .AllowAnyMethod();
        }
        else
        {
            policy
                .WithOrigins("http://localhost:4200")
                .AllowAnyHeader()
                .AllowAnyMethod();
        }
    });
});

// ------------------------------------------------------
// SWAGGER
// ------------------------------------------------------
// ------------------------------------------------------
// SWAGGER
// ------------------------------------------------------
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "ClinicApp.API",
        Version = "v1"
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "Unesi JWT token ovako: Bearer {token}",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                },
                Scheme = "oauth2",
                Name = "Bearer",
                In = ParameterLocation.Header
            },
            new List<string>()
        }
    });
});
// ------------------------------------------------------
// AUTHENTICATION
// ------------------------------------------------------
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opt =>
    {
        opt.RequireHttpsMetadata = !builder.Environment.IsDevelopment();

        opt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew = TimeSpan.Zero
        };
    });

builder.Services.AddAuthorization();

// ------------------------------------------------------
// RATE LIMITING
// ------------------------------------------------------
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        var key = ctx.User.Identity?.IsAuthenticated == true
            ? $"user:{ctx.User.Identity.Name}"
            : $"ip:{ctx.Connection.RemoteIpAddress}";

        return RateLimitPartition.GetFixedWindowLimiter(
            key,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });

    options.AddPolicy("loginPolicy", ctx =>
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var username = ExtractJsonField(ctx, "username");

        var key = string.IsNullOrWhiteSpace(username)
            ? $"login:{ip}"
            : $"login:{username.ToLower()}|{ip}";

        return RateLimitPartition.GetFixedWindowLimiter(
            key,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });

    options.AddPolicy("passwordResetPolicy", ctx =>
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var usernameOrEmail = ExtractJsonField(ctx, "usernameOrEmail");

        var key = string.IsNullOrWhiteSpace(usernameOrEmail)
            ? $"pwd-reset:{ip}"
            : $"pwd-reset:{usernameOrEmail.ToLower()}|{ip}";

        return RateLimitPartition.GetFixedWindowLimiter(
            key,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(15),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });

    options.AddPolicy("excelImportPolicy", ctx =>
    {
        var identity = ctx.User.Identity?.Name ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        return RateLimitPartition.GetFixedWindowLimiter(
            $"excel-import:{identity}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(10),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });
});

// ------------------------------------------------------
// HELPERS
// ------------------------------------------------------
static string? ExtractJsonField(HttpContext ctx, string fieldName)
{
    try
    {
        if (ctx.Request.ContentLength is null || ctx.Request.ContentLength == 0)
            return null;

        ctx.Request.EnableBuffering();

        using var reader = new StreamReader(
            ctx.Request.Body,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);

        var body = reader.ReadToEndAsync().GetAwaiter().GetResult();
        ctx.Request.Body.Position = 0;

        if (string.IsNullOrWhiteSpace(body))
            return null;

        using var doc = JsonDocument.Parse(body);

        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (string.Equals(property.Name, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : property.Value.ToString();
            }
        }

        return null;
    }
    catch
    {
        if (ctx.Request.Body.CanSeek)
            ctx.Request.Body.Position = 0;

        return null;
    }
}

static void ApplyDatabaseMigrations(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

static void SeedDemoData(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    AppDbSeeder.Seed(db, app.Configuration);
}

static void EnsureBootstrapAdmin(WebApplication app)
{
    var enabled = app.Configuration.GetValue<bool>("BootstrapAdmin:Enabled");
    if (!enabled)
        return;

    var username = app.Configuration["BootstrapAdmin:Username"]?.Trim();
    var email = app.Configuration["BootstrapAdmin:Email"]?.Trim();
    var password = app.Configuration["BootstrapAdmin:Password"];

    if (string.IsNullOrWhiteSpace(username) ||
        string.IsNullOrWhiteSpace(email) ||
        string.IsNullOrWhiteSpace(password))
    {
        return;
    }

    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    if (db.Users.Any(u => u.Username == username))
        return;

    db.Users.Add(new User
    {
        Username = username,
        Email = email,
        PasswordHash = PasswordHelper.HashPassword(password),
        Role = "admin",
        MustChangePassword = false
    });

    db.SaveChanges();
}

static async Task<IResult> ReadyHealthResponse(HttpContext ctx)
{
    try
    {
        using var scope = ctx.RequestServices.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var canConnect = await db.Database.CanConnectAsync();

        if (!canConnect)
        {
            return Results.Json(new
            {
                status = "unhealthy",
                env = ctx.RequestServices.GetRequiredService<IHostEnvironment>().EnvironmentName,
                time = DateTime.UtcNow,
                database = "unreachable"
            }, statusCode: 503);
        }

        return Results.Json(new
        {
            status = "ok",
            env = ctx.RequestServices.GetRequiredService<IHostEnvironment>().EnvironmentName,
            time = DateTime.UtcNow,
            database = "reachable"
        });
    }
    catch (Exception ex)
    {
        return Results.Json(new
        {
            status = "unhealthy",
            error = ex.Message
        }, statusCode: 503);
    }
}

static IResult LiveHealthResponse(HttpContext ctx)
{
    return Results.Json(new
    {
        status = "ok",
        time = DateTime.UtcNow
    });
}

// ------------------------------------------------------
// APP START
// ------------------------------------------------------
var app = builder.Build();

ApplyDatabaseMigrations(app);
SeedDemoData(app);
EnsureBootstrapAdmin(app);

if (app.Configuration.GetValue<bool>("SeedData:ExitAfterSeeding"))
{
    return;
}

app.UseMiddleware<ErrorHandlingMiddleware>();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Swagger:Enabled"))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors(AngularCorsPolicy);

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Json(new
{
    name = "ClinicApp API",
    status = "ok",
    environment = app.Environment.EnvironmentName
}));

app.MapGet("/health/live", (Delegate)LiveHealthResponse);
app.MapGet("/health/ready", (Delegate)ReadyHealthResponse);

app.MapControllers();

app.Run();