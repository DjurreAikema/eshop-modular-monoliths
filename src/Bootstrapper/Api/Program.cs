using Carter;
using FluentValidation;
using Serilog;
using Shared.Behaviors;
using Shared.Exceptions.Handlers;
using Shared.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, config) => config.ReadFrom.Configuration(context.Configuration));

// --- Add services to the container
// Common services: Carter, MediatR, FluentValidation
var basketAssembly = typeof(BasketModule).Assembly;
var catalogAssembly = typeof(CatalogModule).Assembly;

builder.Services.AddCarterWithAssemblies(
    basketAssembly,
    catalogAssembly
);

builder.Services.AddMediatRWithAssemblies(
    basketAssembly,
    catalogAssembly
);

// Modules services
builder.Services
    .AddBasketModule(builder.Configuration)
    .AddCatalogModule(builder.Configuration)
    .AddOrderingModule(builder.Configuration);

builder.Services
    .AddExceptionHandler<CustomExceptionHandler>();

var app = builder.Build();

// Configure the HTTP request pipeline
app.MapCarter();
app.UseSerilogRequestLogging();
app.UseExceptionHandler(_ => { });

app
    .UseBasketModule()
    .UseCatalogModule()
    .UseOrderingModule();

app.Run();