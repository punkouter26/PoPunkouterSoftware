using Microsoft.AspNetCore.Rewrite;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddRazorPages();

var app = builder.Build();

// Configure the HTTP request pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// Configure URL rewriting for default document
var options = new RewriteOptions()
    .AddRewrite("^$", "index.html", skipRemainingRules: true);

app.UseRewriter(options);
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.Run();












