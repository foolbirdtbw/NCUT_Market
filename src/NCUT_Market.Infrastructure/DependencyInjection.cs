using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NCUT_Market.Core.Entities;
using NCUT_Market.Core.Services;
using NCUT_Market.Infrastructure.Jobs;
using NCUT_Market.Infrastructure.Persistence;
using NCUT_Market.Infrastructure.Security;
using NCUT_Market.Infrastructure.Services;
using NCUT_Market.Infrastructure.Storage;

namespace NCUT_Market.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "Connection string 'DefaultConnection' is not configured.");

        services.AddDbContext<AppDbContext>(options =>
            options.UseMySql(
                connectionString,
                new MySqlServerVersion(new Version(8, 4, 0)),
                mysqlOptions => mysqlOptions
                    .MigrationsAssembly(typeof(AppDbContext).Assembly.FullName!)
                    .MigrationsHistoryTable("__efmigrationshistory")));

        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.SectionName));
        services.Configure<BackgroundJobsOptions>(
            configuration.GetSection(BackgroundJobsOptions.SectionName));

        // PBKDF2 with a per-user salt and the framework's own format versioning. Registered rather
        // than hand-rolled: the iteration count and the encoded-hash format are things a
        // reimplementation gets subtly wrong, and PasswordHasher is in the shared framework so it
        // costs nothing to use.
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

        services.AddScoped<ImageStorage>();

        services.AddScoped<ICategoryService, CategoryService>();
        services.AddScoped<IDormitoryAreaService, DormitoryAreaService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IProductService, ProductService>();
        services.AddScoped<IConversationService, ConversationService>();
        services.AddScoped<IAnnouncementService, AnnouncementService>();
        services.AddScoped<IFeedbackService, FeedbackService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<ITransactionService, TransactionService>();
        services.AddScoped<IOnlineService, OnlineService>();

        // The first hosted service in the project. It starts the trade-deadline sweep, and it is
        // switched off in the test host through BackgroundJobsOptions — see that type.
        services.AddHostedService<TransactionSweepJob>();

        return services;
    }
}
