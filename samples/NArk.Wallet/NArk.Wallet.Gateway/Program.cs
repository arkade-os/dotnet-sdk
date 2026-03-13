var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Cross-origin isolation headers required for SharedArrayBuffer (SQLite WASM)
app.Use(async (context, next) =>
{
    context.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
    context.Response.Headers["Cross-Origin-Embedder-Policy"] = "require-corp";
    await next();
});

// Static file server only — the Blazor WASM client runs the full NArk SDK in-browser
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();
