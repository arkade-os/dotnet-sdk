var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Static file server only — the Blazor WASM client runs the full NArk SDK in-browser
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();
