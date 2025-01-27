using Microsoft.AspNetCore.Rewrite;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Configure default document
var options = new RewriteOptions()
    .AddRewrite("^$", "index.html", skipRemainingRules: true);

app.UseRewriter(options);

app.UseStaticFiles();

app.UseRouting();
app.Run();












