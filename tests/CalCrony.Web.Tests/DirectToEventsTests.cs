using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Bunit;
using CalCrony.Contracts;
using CalCrony.Web.Api;
using CalCrony.Web.Auth;
using CalCrony.Web.Pages;
using CalCrony.Web.Pages.App;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CalCrony.Web.Tests;

/// <summary>Signed-in visitors skip the marketing page, and a member of a single CalCrony server
/// skips the server picker, landing straight on that server's events (fork behaviour).</summary>
public class DirectToEventsTests : TestContext
{
    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    [Fact]
    public void One_server_opens_its_events_without_showing_the_picker()
    {
        UseGuilds(Guild(111, "West Country Leaders"));
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();

        Render<Guilds>();

        Assert.EndsWith("/app/guilds/111/events", nav.Uri);
    }

    [Fact]
    public void One_server_with_all_in_the_query_still_shows_the_list()
    {
        UseGuilds(Guild(111, "West Country Leaders"));
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo("/app?all=1");

        var cut = Render<Guilds>();

        cut.WaitForAssertion(() => Assert.Contains("West Country Leaders", cut.Markup));
        Assert.EndsWith("/app?all=1", nav.Uri);
    }

    [Fact]
    public void Several_servers_show_the_picker()
    {
        UseGuilds(Guild(111, "West Country Leaders"), Guild(222, "Other Place"));
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();

        var cut = Render<Guilds>();

        cut.WaitForAssertion(() => Assert.Contains("Other Place", cut.Markup));
        Assert.Contains("West Country Leaders", cut.Markup);
        Assert.EndsWith("/app", nav.Uri.TrimEnd('/'));
    }

    [Fact]
    public void No_servers_show_the_add_to_a_server_prompt_instead_of_redirecting()
    {
        UseGuilds();
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();

        var cut = Render<Guilds>();

        cut.WaitForAssertion(() => Assert.Contains("None of your servers have CalCrony yet", cut.Markup));
        Assert.DoesNotContain("/events", nav.Uri);
    }

    [Fact]
    public void Landing_sends_a_signed_in_visitor_to_the_app()
    {
        UseConfig();
        UseSignedInState(signedIn: true);
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();

        Render<Landing>();

        Assert.EndsWith("/app", nav.Uri);
    }

    [Fact]
    public void Landing_still_shows_the_page_to_an_anonymous_visitor()
    {
        UseConfig();
        UseSignedInState(signedIn: false);
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        var before = nav.Uri;

        var cut = Render<Landing>();

        Assert.Equal(before, nav.Uri);
        Assert.Contains("free/busy availability", cut.Markup);
    }

    private static WebGuildDto Guild(long id, string name) => new(id, name, null, false);

    private void UseGuilds(params WebGuildDto[] guilds)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var json = JsonSerializer.Serialize(new WebGuildListResponse(DateTimeOffset.UtcNow, guilds), JsonWeb);
        Services.AddScoped(_ => new CalCronyWebApiClient(
            new HttpClient(new FixedHandler(json)) { BaseAddress = new Uri("http://localhost") }));

        Services.AddSingleton<ITokenStore, InMemoryTokenStore>();
        Services.AddSingleton<JwtAuthenticationStateProvider>();
        Services.AddScoped(sp => new AuthApiClient(
            new HttpClient(new FixedHandler("{}", HttpStatusCode.Unauthorized)) { BaseAddress = new Uri("http://localhost") },
            sp.GetRequiredService<ITokenStore>(),
            sp.GetRequiredService<JwtAuthenticationStateProvider>()));
        UseConfig();
    }

    private void UseConfig() =>
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

    /// <summary>Provides the cascading authentication state the real app gets from
    /// AddCascadingAuthenticationState.</summary>
    private void UseSignedInState(bool signedIn)
    {
        Services.AddSingleton<AuthenticationStateProvider>(new FixedAuthState(signedIn));
        RenderTree.Add<CascadingAuthenticationState>();
    }

    private sealed class FixedAuthState(bool signedIn) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var identity = signedIn
                ? new ClaimsIdentity([new Claim(ClaimTypes.Name, "Renna")], "test")
                : new ClaimsIdentity();
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
        }
    }

    private sealed class FixedHandler(string json, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }
}
