using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using RoomScheduler.Web.Components;
using RoomScheduler.Web.Services;

// The whole app runs in the browser, so it can be hosted as static files
// (GitHub Pages). Department spreadsheets are read in the browser and kept in
// its local storage; they are never uploaded anywhere.
var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Routes>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddSingleton<PlanningService>();

var host = builder.Build();
await host.Services.GetRequiredService<PlanningService>()
    .InitAsync(new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
await host.RunAsync();
