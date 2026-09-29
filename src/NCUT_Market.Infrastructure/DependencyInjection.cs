using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NCUT_Market.Core.Services;
using NCUT_Market.Infrastructure.Persistence;
using NCUT_Market.Infrastructure.Services;

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

        services.AddScoped<ICategoryService, CategoryService>();
        services.AddScoped<IDormitoryAreaService, DormitoryAreaService>();

        return services;
    }
}
