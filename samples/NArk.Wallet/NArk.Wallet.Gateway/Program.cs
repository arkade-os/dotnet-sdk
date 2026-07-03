var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Required for SharedArrayBuffer (SQLite/OPFS in Blazor WASM)
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
    ctx.Response.Headers["Cross-Origin-Embedder-Policy"] = "credentialless";
    await next();
});

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();
