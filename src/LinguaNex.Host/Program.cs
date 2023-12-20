using Serilog.Events;
using Serilog;
using Autofac.Extensions.DependencyInjection;
using Autofac;
using LinguaNex;
using Microsoft.AspNetCore.Http.Features;
using IdGen.DependencyInjection;
using LinguaNex.AutoMapper;
using Microsoft.EntityFrameworkCore;
using LinguaNex.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using StackExchange.Redis;
using System.Text.Json.Serialization;
using Wheel.Json;
using Wheel.Controllers;
using Wheel;
using System.Reflection;
using Microsoft.AspNetCore.HttpOverrides;
using LinguaNex.Const;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Localization;
using static System.Net.Mime.MediaTypeNames;
using Wheel.Core.Exceptions;
using Wheel.Core.Dto;
using Microsoft.Extensions.DependencyInjection;
using LinguaNex.Hubs;
using Wheel.Localization;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.Extensions.Options;
using LinguaNex.DataSeeders;

var builder = WebApplication.CreateBuilder(args);
// Kestrel
builder.WebHost.ConfigureKestrel(options =>
{
    // Handle requests up to 50 MB
    options.Limits.MaxRequestBodySize = 1024 * 1024 * 50;
});

// logging
Log.Logger = new LoggerConfiguration()
#if DEBUG
    .MinimumLevel.Debug()
#else
    .MinimumLevel.Information()
#endif
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
    .WriteTo.Async(c => c.Console())
    .WriteTo.Async(c => c.File("Logs/log.txt", rollingInterval: RollingInterval.Day))
    .CreateLogger();

builder.Host.UseSerilog();

builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    var supportedCultures = SupportedCulture.All().Select(a => a.Name).ToArray();
    options.SetDefaultCulture(supportedCultures[0])
        .AddSupportedCultures(supportedCultures)
        .AddSupportedUICultures(supportedCultures);
    options.ApplyCurrentCultureToResponseHeaders = true;
});

// Localizer
builder.Services.AddEFStringLocalizer(typeof(EFStringLocalizerStore));
builder.Services.AddLocalization();

builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory());
builder.Host.ConfigureContainer<ContainerBuilder>(builder =>
{
    builder.RegisterModule<LinguaNexAutofacModule>();
});

builder.Services.Configure<FormOptions>(options =>
{
    // Set the limit to 256 MB
    options.MultipartBodyLengthLimit = 1024 * 1024 * 256;
});

builder.Services.AddAutoMapper();
builder.Services.AddIdGen(0);

var connectionString = builder.Configuration.GetConnectionString("Default") ?? throw new InvalidOperationException("Connection string 'Default' not found.");


builder.Services.AddDbContext<LinguaNexDbContext>(options =>
    options.UseSqlite(connectionString)
        .UseLazyLoadingProxies()
);


builder.Services.AddChannelRLoacalEventBus();
builder.Services.AddCapDistributedEventBus(x =>
{
    x.UseEntityFramework<LinguaNexDbContext>();

    x.UseSqlite(builder.Configuration.GetConnectionString("Default"));

    x.UseRabbitMQ(o => o.ConnectionFactoryOptions = (factory) => factory.Uri = new Uri(builder.Configuration["ConnectionStrings:RabbitMq"]));
    //x.UseRedis(builder.Configuration["ConnectionStrings:Redis"]);
});

builder.Services.AddMemoryCache();

var redis = await ConnectionMultiplexer.ConnectAsync(builder.Configuration["ConnectionStrings:Redis"]);
builder.Services.AddSingleton<IConnectionMultiplexer, ConnectionMultiplexer>(_ => redis);
builder.Services.AddStackExchangeRedisCache(options => options.Configuration = builder.Configuration["ConnectionStrings:Redis"]);
builder.Services.AddDataProtection()
    .SetApplicationName("LinguaNex")
    .PersistKeysToStackExchangeRedis(redis);


builder.Services.AddSignalR()
    .AddJsonProtocol()
    .AddMessagePackProtocol()
    .AddStackExchangeRedis(builder.Configuration["ConnectionStrings:Redis"]);


builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Encoder = JavaScriptEncoder.Create(UnicodeRanges.All);
    options.SerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
    options.SerializerOptions.Converters.Add(new Int32Converter());
    options.SerializerOptions.Converters.Add(new LongJsonConverter());
});

builder.Services.AddControllers()
    .AddApplicationPart(typeof(LinguaNexControllerBase).Assembly)
    .AddControllersAsServices()
    .AddJsonOptions(configure =>
    {
        configure.JsonSerializerOptions.Encoder = JavaScriptEncoder.Create(UnicodeRanges.All);
        configure.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
        configure.JsonSerializerOptions.Converters.Add(new Int32Converter());
        configure.JsonSerializerOptions.Converters.Add(new LongJsonConverter());
    });


builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    //��������xml������
    var binXmlFiles =
        new DirectoryInfo(string.IsNullOrWhiteSpace(AppDomain.CurrentDomain.BaseDirectory)
            ? Path.GetDirectoryName(Assembly.GetEntryAssembly().Location)
            : AppDomain.CurrentDomain.BaseDirectory).GetFiles("*.xml", SearchOption.TopDirectoryOnly);
    foreach (var filePath in binXmlFiles.Select(item => item.FullName))
    {
        options.IncludeXmlComments(filePath, true);
    }
    string GetCustomerSchemaId(Type type, bool first = true)
    {
        var name = "";
        if (first)
            name = type.FullName;
        else
            name = type.Name;
        if (type.IsGenericType)
        {
            name = type.Name.Substring(0, type.Name.IndexOf("`"));
            name += "<";
            for (int i = 0; i < type.GenericTypeArguments.Length; i++)
            {
                var arg = type.GenericTypeArguments[i];
                name += GetCustomerSchemaId(arg, false);
                if (i < type.GenericTypeArguments.Length - 1)
                    name += ",";
            }
            name += ">";
        }
        return name;
    }

    options.CustomSchemaIds(type => GetCustomerSchemaId(type));
});

builder.Services.AddHealthChecks();
builder.Services.AddCors();

var app = builder.Build();

//��ʼ��������Ϣ
await app.SeedData();

app.UseRequestLocalization();

var forwardOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
forwardOptions.KnownNetworks.Clear();
forwardOptions.KnownProxies.Clear();

app.UseForwardedHeaders(forwardOptions);

// Configure the HTTP request pipeline.
//if (app.Environment.IsDevelopment())
//{
app.UseSwagger();
app.UseSwaggerUI();
//}

app.UseReDoc(options => options.RoutePrefix = "doc");

app.UseCors(options => options.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());

app.UseExceptionHandler(exceptionHandlerApp =>
{
    exceptionHandlerApp.Run(async context =>
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;

        // using static System.Net.Mime.MediaTypeNames;
        context.Response.ContentType = Application.Json;
        var exceptionHandlerPathFeature =
            context.Features.Get<IExceptionHandlerPathFeature>();

        if (exceptionHandlerPathFeature?.Error is BusinessException businessException)
        {
            var L = context.RequestServices.GetRequiredService<IStringLocalizerFactory>().Create(null);
            if (businessException.MessageData != null)
                await context.Response.WriteAsJsonAsync(new R { Code = businessException.Code, Message = L[businessException.Message, businessException.MessageData] });
            else
                await context.Response.WriteAsJsonAsync(new R { Code = businessException.Code, Message = L[businessException.Message] });
        }
        else
        {
            await context.Response.WriteAsJsonAsync(new R { Code = ErrorCode.InternalError, Message = exceptionHandlerPathFeature?.Error.Message });
        }
    });
});

var webSocketOptions = new WebSocketOptions
{
};
app.UseWebSockets(webSocketOptions);

app.MapControllers();
app.MapHub<LinguaNexHub>("/hubs/LinguaNex");
app.MapHealthChecks("Health");
app.Run();
