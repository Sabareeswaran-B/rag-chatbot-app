using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using System.Security.Claims;
using System.Text;
using RagChatbot.API.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "RAG Chatbot API",
        Version = "v1",
        Description = "REST API for the RAG Chatbot — handles document ingestion, vector search, and AI-powered Q&A."
    });

    // Include XML documentation
    var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath)) c.IncludeXmlComments(xmlPath);

    // JWT Bearer auth button
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste your JWT access token here. Obtain it from POST /api/auth/login."
    });
    c.AddSecurityRequirement(doc => new OpenApiSecurityRequirement
    {{
        new OpenApiSecuritySchemeReference("Bearer", doc),
        new List<string>()
    }});
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAngular", policy =>
        policy.WithOrigins("http://localhost:4200", "http://localhost:4300")
              .AllowAnyMethod()
              .AllowAnyHeader());
});

var jwtSecret = builder.Configuration["Jwt:Secret"];
if (string.IsNullOrWhiteSpace(jwtSecret))
    throw new InvalidOperationException("Jwt:Secret not configured. Run: dotnet user-secrets set \"Jwt:Secret\" \"<32+ char secret>\"");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Keep JWT claim names as-is (sub, unique_name, etc.) — without this,
        // ASP.NET Core remaps them to long URIs and User.FindFirst("sub") returns null.
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"],
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
            RoleClaimType = ClaimTypes.Role
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddSingleton<IEmbeddingService, EmbeddingService>();
builder.Services.AddSingleton<IMongoDbService, MongoDbService>();
builder.Services.AddSingleton<IUserRepository, UserRepository>();
builder.Services.AddSingleton<IModerationViolationRepository, ModerationViolationRepository>();
builder.Services.AddSingleton<IChatHistoryRepository, ChatHistoryRepository>();
builder.Services.AddSingleton<IJwtService, JwtService>();
builder.Services.AddScoped<IFileProcessingService, FileProcessingService>();
builder.Services.AddScoped<IModerationService, ModerationService>();
builder.Services.AddScoped<IRerankingService, RerankingService>();
builder.Services.AddScoped<IQueryRewriteService, QueryRewriteService>();
builder.Services.AddScoped<IMultiQueryService, MultiQueryService>();
builder.Services.AddScoped<IChatService, ChatService>();
builder.Services.AddScoped<IAuthService, AuthService>();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "RAG Chatbot API v1");
    c.RoutePrefix = string.Empty;
    c.DocumentTitle = "RAG Chatbot API";
    c.DefaultModelsExpandDepth(-1); // Hide schemas section by default
});

app.UseCors("AllowAngular");
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseMiddleware<RagChatbot.API.Middleware.BlockedUserMiddleware>();
app.UseAuthorization();
app.MapControllers();
app.Run();
