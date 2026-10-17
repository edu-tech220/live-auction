using LiveAuction.Application.Auctions;
using LiveAuction.Application.Auth;
using LiveAuction.Application.Bidding;
using LiveAuction.Application.Lifecycle;
using Microsoft.Extensions.DependencyInjection;

namespace LiveAuction.Application;

public static class DependencyInjection
{
    // Casos de uso: Scoped porque dependen del DbContext (una instancia por request/operación).
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IAuctionService, AuctionService>();
        services.AddScoped<IBidService, BidService>();
        services.AddScoped<IAuctionLifecycleService, AuctionLifecycleService>();
        return services;
    }
}
