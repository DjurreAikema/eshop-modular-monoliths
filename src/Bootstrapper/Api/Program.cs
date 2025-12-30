using Carter;
using Keycloak.AuthServices.Authentication;
using Serilog;
using Shared.Exceptions.Handlers;
using Shared.Extensions;
using Shared.Messaging.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, config) => config.ReadFrom.Configuration(context.Configuration));

// --- Add services to the container
// Common services: Carter, MassTransit, MediatR, Keycloak
var basketAssembly = typeof(BasketModule).Assembly;
var catalogAssembly = typeof(CatalogModule).Assembly;
var orderingAssembly = typeof(OrderingModule).Assembly;

builder.Services.AddCarterWithAssemblies(
    basketAssembly,
    catalogAssembly,
    orderingAssembly
);

builder.Services.AddKeycloakWebApiAuthentication(builder.Configuration);
builder.Services.AddAuthorization();

builder.Services.AddMassTransitWithAssemblies(
    builder.Configuration,
    basketAssembly,
    catalogAssembly,
    orderingAssembly
);

builder.Services.AddMediatRWithAssemblies(
    basketAssembly,
    catalogAssembly,
    orderingAssembly
);

builder.Services.AddStackExchangeRedisCache(options =>
    options.Configuration = builder.Configuration.GetConnectionString("Redis")
);

// Modules services
builder.Services
    .AddBasketModule(builder.Configuration)
    .AddCatalogModule(builder.Configuration)
    .AddOrderingModule(builder.Configuration);

builder.Services
    .AddExceptionHandler<CustomExceptionHandler>();

var app = builder.Build();

// --- Configure the HTTP request pipeline
app.MapCarter();
app.UseSerilogRequestLogging();
app.UseExceptionHandler(_ => { });
app.UseAuthentication();
app.UseAuthorization();

app
    .UseBasketModule()
    .UseCatalogModule()
    .UseOrderingModule();

app.Run();