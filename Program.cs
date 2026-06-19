using DoAnLapTrinhWeb.Data;
using DoAnLapTrinhWeb.Services;
using DoAnLapTrinhWeb.Hubs;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddSignalR();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

var authenticationBuilder = builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.LogoutPath = "/Account/Logout";
        options.AccessDeniedPath = "/Account/Denied";
    });

if (!string.IsNullOrWhiteSpace(builder.Configuration["Authentication:Google:ClientId"]) &&
    !string.IsNullOrWhiteSpace(builder.Configuration["Authentication:Google:ClientSecret"]))
{
    authenticationBuilder.AddGoogle(options =>
    {
        options.ClientId = builder.Configuration["Authentication:Google:ClientId"] ?? string.Empty;
        options.ClientSecret = builder.Configuration["Authentication:Google:ClientSecret"] ?? string.Empty;
        options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    });
}

builder.Services.AddAuthorization();
builder.Services.AddSingleton<SqlSchemaParser>();
builder.Services.AddSingleton<SchemaReviewService>();
builder.Services.AddSingleton<AspNetMvcExportService>();
builder.Services.AddSingleton<MockDataService>();
builder.Services.AddScoped<ProjectStore>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapHub<ProjectCollaborationHub>("/hubs/project-collaboration");

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=DatabaseDesigner}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();
